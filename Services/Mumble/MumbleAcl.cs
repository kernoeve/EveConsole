namespace EveConsole.Services.Mumble;

/// <summary>
/// A group as one channel sees it: its own, or one it has from a channel above, possibly with members added or taken
/// away here. Members are registered users, by their ids on the server.
/// </summary>
public sealed class MumbleAclGroup
{
    public string Name { get; set; } = "";

    /// <summary>Defined on a channel above. ⚠️ Can't be removed here, only put back as it is above.</summary>
    public bool Inherited { get; set; }

    /// <summary>Has the members it has above, as well as those added here.</summary>
    public bool Inherit { get; set; } = true;

    /// <summary>Channels below have it too.</summary>
    public bool Inheritable { get; set; } = true;

    public List<uint> Add { get; set; } = [];
    public List<uint> Remove { get; set; } = [];

    /// <summary>Who it has from above. Read only: the server works this out.</summary>
    public List<uint> InheritedMembers { get; set; } = [];

    /// <summary>
    /// Nothing changed here from what it is above, so there is nothing to send: sending it would make a copy of it here,
    /// which would stop following later changes above. The same test the standard client makes.
    /// </summary>
    public bool IsJustInherited => Inherited && Inherit && Inheritable && Add.Count == 0 && Remove.Count == 0;

    public MumbleAclGroup Copy() => new()
    {
        Name = Name, Inherited = Inherited, Inherit = Inherit, Inheritable = Inheritable,
        Add = [.. Add], Remove = [.. Remove], InheritedMembers = [.. InheritedMembers],
    };
}

/// <summary>One line of a channel's permissions: what it allows and denies, to whom, here and below.</summary>
public sealed class MumbleAclEntry
{
    public bool ApplyHere { get; set; } = true;
    public bool ApplySubs { get; set; } = true;

    /// <summary>From a channel above. Read only here: it is changed there.</summary>
    public bool Inherited { get; set; }

    /// <summary>A registered user, by id. When set, <see cref="Group"/> is not used.</summary>
    public uint? UserId { get; set; }

    /// <summary>A group, by name without the "@": "all", "auth", "in", "~sub,0,1", "$hash", "#token", or one of the channel's.</summary>
    public string Group { get; set; } = "";

    public MumblePermission Grant { get; set; }
    public MumblePermission Deny { get; set; }

    public MumbleAclEntry Copy() => new()
    {
        ApplyHere = ApplyHere, ApplySubs = ApplySubs, Inherited = Inherited, UserId = UserId, Group = Group, Grant = Grant, Deny = Deny,
    };
}

/// <summary>
/// A channel's permissions and groups, as the server sends them when asked and as they go back to change them.
///
/// <para>⚠️ What goes back is the whole of this channel's own part: whatever is left out is removed. The server only
/// takes this from someone with Write on the channel, and gives them Write back if what they send would lock them out.</para>
/// </summary>
public sealed class MumbleAcl
{
    public uint Channel { get; set; }

    /// <summary>Takes the permissions of the channels above that apply below them.</summary>
    public bool InheritAcls { get; set; } = true;

    public List<MumbleAclGroup> Groups { get; set; } = [];

    /// <summary>In order: ⚠️ later lines win over earlier ones, inherited ones always come first.</summary>
    public List<MumbleAclEntry> Entries { get; set; } = [];

    /// <summary>Every user id it names, to ask the server their names.</summary>
    public IEnumerable<uint> UserIds =>
        Groups.SelectMany(g => g.Add.Concat(g.Remove).Concat(g.InheritedMembers))
              .Concat(Entries.Where(e => e.UserId is not null).Select(e => e.UserId!.Value))
              .Distinct();

    // Mumble.proto: ACL { channel_id 1, inherit_acls 2, groups 3, acls 4, query 5 }.
    // ChanGroup { name 1, inherited 2, inherit 3, inheritable 4, add 5, remove 6, inherited_members 7 }.
    // ChanACL { apply_here 1, apply_subs 2, inherited 3, user_id 4, group 5, grant 6, deny 7 }.
    // ⚠️ Booleans missing from the message are true: those are the protocol's defaults.

    /// <exception cref="InvalidDataException">A part of it is not a well-formed message.</exception>
    public static MumbleAcl Read(PbMessage m)
    {
        var acl = new MumbleAcl { Channel = (uint)(m.UInt(1) ?? 0), InheritAcls = m.Bool(2) ?? true };

        foreach (var raw in m.AllBytes(3))
        {
            var g = new PbMessage(raw);
            acl.Groups.Add(new MumbleAclGroup
            {
                Name = g.Text(1) ?? "",
                Inherited = g.Bool(2) ?? true,
                Inherit = g.Bool(3) ?? true,
                Inheritable = g.Bool(4) ?? true,
                Add = [.. g.UInts(5).Select(v => (uint)v)],
                Remove = [.. g.UInts(6).Select(v => (uint)v)],
                InheritedMembers = [.. g.UInts(7).Select(v => (uint)v)],
            });
        }

        foreach (var raw in m.AllBytes(4))
        {
            var a = new PbMessage(raw);
            acl.Entries.Add(new MumbleAclEntry
            {
                ApplyHere = a.Bool(1) ?? true,
                ApplySubs = a.Bool(2) ?? true,
                Inherited = a.Bool(3) ?? true,
                UserId = a.UInt(4) is { } user ? (uint)user : null,
                Group = a.Text(5) ?? "",
                Grant = (MumblePermission)(uint)(a.UInt(6) ?? 0),
                Deny = (MumblePermission)(uint)(a.UInt(7) ?? 0),
            });
        }

        return acl;
    }

    /// <summary>What goes back to change it: this channel's own lines and groups, never what it has from above as it is.</summary>
    public byte[] ToMessage()
    {
        var message = new PbWriter().UInt(1, Channel).Bool(2, InheritAcls);

        foreach (var group in Groups.Where(g => !g.IsJustInherited && g.Name.Length > 0))
        {
            var g = new PbWriter().Text(1, group.Name).Bool(2, group.Inherited).Bool(3, group.Inherit).Bool(4, group.Inheritable);
            foreach (var id in group.Add) g.UInt(5, id);
            foreach (var id in group.Remove) g.UInt(6, id);
            message.Bytes(3, g.ToArray());
        }

        foreach (var entry in Entries.Where(e => !e.Inherited))
        {
            var a = new PbWriter().Bool(1, entry.ApplyHere).Bool(2, entry.ApplySubs).Bool(3, false);
            if (entry.UserId is { } user) a.UInt(4, user);
            else a.Text(5, entry.Group);
            a.UInt(6, (uint)entry.Grant).UInt(7, (uint)entry.Deny);
            message.Bytes(4, a.ToArray());
        }

        return message.Bool(5, false).ToArray();
    }

    /// <summary>
    /// Whether this, as the server holds it, is what was sent: the channel's own lines in order, and its own groups. False
    /// when the server dropped the change, as it does with messages sent too fast, or changed it, as it does to give back
    /// Write to someone who would have locked themselves out.
    /// </summary>
    public bool Keeps(MumbleAcl sent)
    {
        if (InheritAcls != sent.InheritAcls) return false;

        static (bool, bool, uint?, string, MumblePermission, MumblePermission) Line(MumbleAclEntry e) =>
            (e.ApplyHere, e.ApplySubs, e.UserId, e.UserId is null ? e.Group : "", e.Grant, e.Deny);
        if (!Entries.Where(e => !e.Inherited).Select(Line).SequenceEqual(sent.Entries.Where(e => !e.Inherited).Select(Line))) return false;

        static bool Same(List<uint> a, List<uint> b) => a.Distinct().Order().SequenceEqual(b.Distinct().Order());
        var mine = Groups.Where(g => !g.IsJustInherited).ToDictionary(g => g.Name, StringComparer.Ordinal);
        var theirs = sent.Groups.Where(g => !g.IsJustInherited).ToList();
        return mine.Count == theirs.Count && theirs.All(g =>
            mine.TryGetValue(g.Name, out var held) && held.Inherit == g.Inherit && held.Inheritable == g.Inheritable
            && Same(held.Add, g.Add) && Same(held.Remove, g.Remove));
    }

    /// <summary>Asks for a channel's permissions and groups.</summary>
    public static byte[] QueryMessage(uint channel) => new PbWriter().UInt(1, channel).Bool(5, true).ToArray();
}
