using System.Text.Json;
using System.Xml.Linq;

namespace EveConsole.Services.Jabber;

/// <summary>Someone a room has banned, with why if it says.</summary>
public sealed record XmppBan(string Jid, string Reason);

/// <summary>What the person may do to someone else in a room. See <see cref="JabberRoomRights"/>.</summary>
public sealed record JabberPersonRights(
    bool Kick, bool GiveVoice, bool TakeVoice, bool MakeModerator, bool RemoveModerator,
    bool Ban, bool MakeMember, bool RemoveMember, bool MakeAdmin, bool RemoveAdmin)
{
    public static readonly JabberPersonRights None = new(false, false, false, false, false, false, false, false, false, false);

    public bool Any => Kick || GiveVoice || TakeVoice || MakeModerator || RemoveModerator || Ban || MakeMember || RemoveMember || MakeAdmin || RemoveAdmin;
}

/// <summary>What the person may do to the room itself.</summary>
public sealed record JabberRoomRightsSet(bool ChangeTopic, bool Invite, bool LiftBan);

/// <summary>
/// Who may do what in a room, by the rules every server keeps (XEP-0045): a moderator kicks and gives or takes voice, an
/// admin or owner bans and gives membership and moderation, an owner makes admins. Nobody does any of it to an admin or
/// owner but someone above them. Only what this allows is offered; the room decides in the end, and says no when it does.
///
/// <para>Bans and memberships are of an address, so they are only offered for someone whose real address the room shows.</para>
/// </summary>
public static class JabberRoomRights
{
    public static int Rank(string affiliation) => affiliation switch
    {
        "owner"   => 4,
        "admin"   => 3,
        "member"  => 2,
        "outcast" => 0,
        _         => 1,
    };

    public static JabberPersonRights Over(XmppOccupant? me, XmppOccupant them)
    {
        if (me is null || them.Self || them.Nick == me.Nick) return JabberPersonRights.None;

        var moderator = me.Role == "moderator";
        var mine = Rank(me.Affiliation);
        var theirs = Rank(them.Affiliation);
        var known = XmppAddress.Parse(them.RealJid).Bare.Length > 0;

        // Admins and owners are moderators by right: no moderator can kick them, silence them or take that away.
        var below = theirs < 3;

        return new JabberPersonRights(
            Kick:            moderator && below,
            GiveVoice:       moderator && below && them.Role == "visitor",
            TakeVoice:       moderator && below && them.Role == "participant",
            MakeModerator:   mine >= 3 && them.Role is "participant" or "visitor",
            RemoveModerator: mine >= 3 && below && them.Role == "moderator",
            Ban:             mine >= 3 && known && mine > theirs,
            MakeMember:      mine >= 3 && known && theirs == 1,
            RemoveMember:    mine >= 3 && known && theirs == 2,
            MakeAdmin:       mine == 4 && known && theirs < 3,
            RemoveAdmin:     mine == 4 && known && theirs == 3);
    }

    /// <summary>
    /// A moderator changes the topic. Some rooms let anyone, but a room does not say so to its members, so it is only
    /// offered to moderators. Anyone in a room may invite; only an admin or owner can see and lift bans.
    /// </summary>
    public static JabberRoomRightsSet OfRoom(XmppOccupant? me) => new(
        ChangeTopic: me?.Role == "moderator",
        Invite:      me is not null,
        LiftBan:     me is not null && Rank(me.Affiliation) >= 3);
}

public sealed partial class XmppClient
{
    public static readonly XNamespace MucAdmin = "http://jabber.org/protocol/muc#admin";

    /// <summary>A role change for someone in the room, by their nickname: voice, moderation, or none at all, which kicks.</summary>
    public static XElement RoleQuery(string nick, string role, string reason) =>
        new(MucAdmin + "query", new XElement(MucAdmin + "item",
            new XAttribute("nick", nick), new XAttribute("role", role),
            reason.Length > 0 ? new XElement(MucAdmin + "reason", reason) : null));

    /// <summary>An affiliation change for an address: member, admin, owner, none, or outcast, which bans.</summary>
    public static XElement AffiliationQuery(string jid, string affiliation, string reason) =>
        new(MucAdmin + "query", new XElement(MucAdmin + "item",
            new XAttribute("jid", XmppAddress.Parse(jid).Bare), new XAttribute("affiliation", affiliation),
            reason.Length > 0 ? new XElement(MucAdmin + "reason", reason) : null));

    /// <summary>A topic for a room, as the room's own message sets it.</summary>
    public static XElement SubjectMessage(string room, string subject) =>
        new(Client + "message", new XAttribute("to", room), new XAttribute("type", "groupchat"), new XElement(Client + "subject", subject));

    /// <summary>An invitation sent through the room, which tells the person and lets them in if it is members only.</summary>
    public static XElement InviteMessage(string room, string jid, string reason) =>
        new(Client + "message", new XAttribute("to", room),
            new XElement(MucUser + "x", new XElement(MucUser + "invite",
                new XAttribute("to", XmppAddress.Parse(jid).Bare),
                reason.Length > 0 ? new XElement(MucUser + "reason", reason) : null)));

    public static List<XmppBan> ParseBans(XElement answer) =>
        [.. (answer.Element(MucAdmin + "query")?.Elements(MucAdmin + "item") ?? [])
            .Select(i => new XmppBan(XmppAddress.Parse((string?)i.Attribute("jid")).Bare, i.Element(MucAdmin + "reason")?.Value ?? ""))
            .Where(b => b.Jid.Length > 0)];

    public async Task SetRoleAsync(string room, string nick, string role, string reason, CancellationToken ct) =>
        await QueryAsync("set", room, RoleQuery(nick, role, reason), ct);

    public async Task SetAffiliationAsync(string room, string jid, string affiliation, string reason, CancellationToken ct) =>
        await QueryAsync("set", room, AffiliationQuery(jid, affiliation, reason), ct);

    public async Task<List<XmppBan>> BansAsync(string room, CancellationToken ct) =>
        ParseBans(await QueryAsync("get", room,
            new XElement(MucAdmin + "query", new XElement(MucAdmin + "item", new XAttribute("affiliation", "outcast"))), ct));

    public Task SetSubjectAsync(string room, string subject, CancellationToken ct) => connection.WriteAsync(SubjectMessage(room, subject), ct);

    public Task InviteAsync(string room, string jid, string reason, CancellationToken ct) => connection.WriteAsync(InviteMessage(room, jid, reason), ct);
}

/// <summary>Running a room: what <see cref="JabberRoomRights"/> offers, done here or asked of the worker that holds the connection.</summary>
public sealed partial class JabberService
{
    private static XmppClient Connected(JabberSession session) =>
        session.Client ?? throw new XmppException(XmppFailure.Network, "Not connected.");

    public async Task SetRoomRoleAsync(JabberSession session, string room, string nick, string role, string reason, CancellationToken ct = default)
    {
        if (!ActsHere) await AskWorkerAsync(session.Account.Id, "role", new CommandArgs(room, Name: nick, Status: role, Text: reason), ct);
        else await Connected(session).SetRoleAsync(room, nick, role, reason, ct);
    }

    public async Task SetRoomAffiliationAsync(JabberSession session, string room, string jid, string affiliation, string reason, CancellationToken ct = default)
    {
        if (!ActsHere) await AskWorkerAsync(session.Account.Id, "affiliation", new CommandArgs(room, Name: jid, Status: affiliation, Text: reason), ct);
        else await Connected(session).SetAffiliationAsync(room, jid, affiliation, reason, ct);
    }

    public async Task<List<XmppBan>> RoomBansAsync(JabberSession session, string room, CancellationToken ct = default) =>
        !ActsHere
            ? JsonSerializer.Deserialize<List<XmppBan>>(await AskWorkerAsync(session.Account.Id, "bans", new CommandArgs(room), ct)) ?? []
            : await Connected(session).BansAsync(room, ct);

    public async Task SetRoomSubjectAsync(JabberSession session, string room, string subject, CancellationToken ct = default)
    {
        if (!ActsHere) await AskWorkerAsync(session.Account.Id, "subject", new CommandArgs(room, Text: subject), ct);
        else await Connected(session).SetSubjectAsync(room, subject, ct);
    }

    public async Task InviteToRoomAsync(JabberSession session, string room, string jid, string reason, CancellationToken ct = default)
    {
        if (!ActsHere) await AskWorkerAsync(session.Account.Id, "roominvite", new CommandArgs(room, Name: jid, Text: reason), ct);
        else await Connected(session).InviteAsync(room, jid, reason, ct);
    }
}
