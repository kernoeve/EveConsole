using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Services.Mumble;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// One channel's permissions and groups, as the standard client's ACL editor has them: read from the server, changed
/// here, and sent back whole.
///
/// <para>⚠️ Holds the client and channel it was opened for. The tree's selection moves on while this is open, and a save
/// must land on the channel that was read, on the connection it was read from.</para>
/// </summary>
public sealed class MumbleAclEditorVm : ReactiveObject, IDisposable
{
    /// <summary>How long a name lookup or the server's answer is waited for.</summary>
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(6);

    /// <summary>What a line can allow or deny anywhere.</summary>
    private static readonly (MumblePermission Bit, string Name)[] ChannelPermissions =
    [
        (MumblePermission.Write, MumbleText.PermWrite),
        (MumblePermission.Traverse, MumbleText.PermTraverse),
        (MumblePermission.Enter, MumbleText.PermEnter),
        (MumblePermission.Speak, MumbleText.PermSpeak),
        (MumblePermission.MuteDeafen, MumbleText.PermMuteDeafen),
        (MumblePermission.Move, MumbleText.PermMove),
        (MumblePermission.MakeChannel, MumbleText.PermMakeChannel),
        (MumblePermission.MakeTempChannel, MumbleText.PermMakeTempChannel),
        (MumblePermission.LinkChannel, MumbleText.PermLinkChannel),
        (MumblePermission.Whisper, MumbleText.PermWhisper),
        (MumblePermission.TextMessage, MumbleText.PermTextMessage),
        (MumblePermission.Listen, MumbleText.PermListen),
    ];

    /// <summary>What only means anything on the root, as it is about the whole server.</summary>
    private static readonly (MumblePermission Bit, string Name)[] ServerPermissions =
    [
        (MumblePermission.Kick, MumbleText.PermKick),
        (MumblePermission.Ban, MumbleText.PermBan),
        (MumblePermission.Register, MumbleText.PermRegister),
        (MumblePermission.SelfRegister, MumbleText.PermSelfRegister),
        (MumblePermission.ResetUserContent, MumbleText.PermResetUserContent),
    ];

    /// <summary>The groups every server has without anyone making them, as the standard client offers them.</summary>
    public static readonly IReadOnlyList<string> BuiltInGroups = ["all", "auth", "in", "out", "sub", "~in", "~out", "~sub"];

    private readonly MumbleClient _client;
    private readonly uint _channel;
    private bool _waitingForAcl, _loading = true, _inheritAcls = true, _busy;
    private string _status = "", _newGroupName = "";
    private MumbleAclEntryVm? _selectedEntry;
    private MumbleAclGroupVm? _selectedGroup;
    private TaskCompletionSource? _namesArrived;

    public MumbleAclEditorVm(MumbleClient client, uint channel, string channelName)
    {
        _client = client;
        _channel = channel;
        Title = string.Format(CultureInfo.CurrentCulture, MumbleText.AclTitle, channelName);

        _client.AclReceived += OnAcl;
        _client.RegisteredNamesChanged += OnNames;
        _client.Denied += OnDenied;

        AddEntryCommand    = ReactiveCommand.Create(AddEntry);
        RemoveEntryCommand = ReactiveCommand.Create(RemoveEntry);
        MoveUpCommand      = ReactiveCommand.Create(() => MoveEntry(-1));
        MoveDownCommand    = ReactiveCommand.Create(() => MoveEntry(+1));
        AddGroupCommand    = ReactiveCommand.Create(AddGroup);
        RemoveGroupCommand = ReactiveCommand.Create(RemoveGroup);
        SaveCommand        = ReactiveCommand.CreateFromTask(SaveAsync);
        ReloadCommand      = ReactiveCommand.CreateFromTask(() => AskAsync(MumbleText.AclLoading));
        foreach (var c in new IHandleObservableErrors[] { SaveCommand, ReloadCommand })
            c.ThrownExceptions.Subscribe(e => { Busy = false; StatusText = e.Message; });
    }

    public string Title { get; }

    /// <summary>The root, where what is about the whole server can be allowed too.</summary>
    public bool IsRoot => _channel == 0;

    public bool IsLoading { get => _loading; private set { this.RaiseAndSetIfChanged(ref _loading, value); this.RaisePropertyChanged(nameof(IsLoaded)); } }
    public bool IsLoaded => !IsLoading;

    /// <summary>Saving or looking up a name: nothing else is started meanwhile.</summary>
    public bool Busy { get => _busy; private set => this.RaiseAndSetIfChanged(ref _busy, value); }

    public string StatusText { get => _status; private set { this.RaiseAndSetIfChanged(ref _status, value); this.RaisePropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;

    public bool InheritAcls { get => _inheritAcls; set => this.RaiseAndSetIfChanged(ref _inheritAcls, value); }

    public ObservableCollection<MumbleAclEntryVm> Entries { get; } = [];
    public ObservableCollection<MumbleAclGroupVm> Groups { get; } = [];

    /// <summary>What the "who" box of a group line offers: the server's own groups, then this channel's.</summary>
    public ObservableCollection<string> GroupChoices { get; } = [];

    /// <summary>Registered people the server has named, and those connected now, for a person's line or a group's members.</summary>
    public ObservableCollection<string> PeopleChoices { get; } = [];

    public MumbleAclEntryVm? SelectedEntry
    {
        get => _selectedEntry;
        set { this.RaiseAndSetIfChanged(ref _selectedEntry, value); this.RaisePropertyChanged(nameof(HasSelectedEntry)); }
    }
    public bool HasSelectedEntry => SelectedEntry is not null;

    public MumbleAclGroupVm? SelectedGroup
    {
        get => _selectedGroup;
        set { this.RaiseAndSetIfChanged(ref _selectedGroup, value); this.RaisePropertyChanged(nameof(HasSelectedGroup)); }
    }
    public bool HasSelectedGroup => SelectedGroup is not null;

    public string NewGroupName { get => _newGroupName; set => this.RaiseAndSetIfChanged(ref _newGroupName, value ?? ""); }

    public ReactiveCommand<Unit, Unit> AddEntryCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveEntryCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit, Unit> AddGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadCommand { get; }

    /// <summary>Asks the server for what the channel has now. Everything here is replaced by its answer.</summary>
    public async Task AskAsync(string saying)
    {
        StatusText = saying;
        IsLoading = Entries.Count == 0 && Groups.Count == 0;
        await AskAgainAsync();
    }

    private int _asked;

    /// <summary>
    /// ⚠️ A server quietly drops what one person sends faster than it allows, so an answer can simply never come. One more
    /// try, then it is said, rather than waiting for ever with every button off.
    /// </summary>
    private async Task AskAgainAsync()
    {
        var ask = ++_asked;
        _waitingForAcl = true;
        await Task.Run(() => _client.AskAclAsync(_channel, default));

        await Task.Delay(AnswerWithin / 2);
        if (!_waitingForAcl || ask != _asked) return;
        await Task.Run(() => _client.AskAclAsync(_channel, default));

        await Task.Delay(AnswerWithin);
        if (!_waitingForAcl || ask != _asked) return;
        _waitingForAcl = false;
        _sent = null;
        Busy = false;
        StatusText = MumbleText.AclNoAnswer;
    }

    // ── What the server says ───────────────────────────────────────────────────

    private void OnAcl(MumbleAcl acl)
    {
        if (acl.Channel != _channel) return;
        Dispatcher.UIThread.Post(() =>
        {
            // Only an answer to this window's own question: another window open on the same channel asks its own.
            if (!_waitingForAcl) return;
            _waitingForAcl = false;
            Load(acl);
        });
    }

    private void OnNames()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _namesArrived?.TrySetResult();
            foreach (var entry in Entries) entry.NamesKnown();
            foreach (var group in Groups) group.NamesKnown();
            FillPeopleChoices();
        });
    }

    private void OnDenied(string why)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // ⚠️ Any refusal on the connection lands here, but one while this window waits is almost surely its own.
            if (!_waitingForAcl && !Busy) return;
            _waitingForAcl = false;
            _sent = null;
            Busy = false;
            IsLoading = false;
            StatusText = why.Length > 0 ? MumbleService.PlainText(why) : MumbleText.AclRefused;
        });
    }

    /// <summary>What was last sent, until the server's answer shows whether it took it.</summary>
    private MumbleAcl? _sent;

    private void Load(MumbleAcl acl)
    {
        var selectedEntry = SelectedEntry is { } e ? Entries.IndexOf(e) : -1;
        var selectedGroup = SelectedGroup?.Name;

        InheritAcls = acl.InheritAcls;
        Entries.Clear();
        foreach (var entry in acl.Entries) Entries.Add(new MumbleAclEntryVm(this, entry));
        Groups.Clear();
        foreach (var group in acl.Groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)) Groups.Add(new MumbleAclGroupVm(this, group));

        FillGroupChoices();
        FillPeopleChoices();
        SelectedEntry = selectedEntry >= 0 && selectedEntry < Entries.Count ? Entries[selectedEntry] : Entries.FirstOrDefault(x => !x.Inherited) ?? Entries.FirstOrDefault();
        SelectedGroup = Groups.FirstOrDefault(g => g.Name == selectedGroup) ?? Groups.FirstOrDefault();

        IsLoading = false;
        Busy = false;
        StatusText = _sent is null ? "" : acl.Keeps(_sent) ? MumbleText.AclSaved : MumbleText.AclNotKept;
        _sent = null;

        // Ids are all a server sends: their names come separately.
        var unknown = acl.UserIds.Where(id => _client.RegisteredName(id) is null).ToList();
        if (unknown.Count > 0) _ = Task.Run(() => _client.AskRegisteredAsync(unknown, [], default));
    }

    internal string NameOf(uint userId) => _client.RegisteredName(userId) ?? "#" + userId.ToString(CultureInfo.InvariantCulture);

    internal IEnumerable<(MumblePermission Bit, string Name)> PermissionsShown => IsRoot ? ChannelPermissions.Concat(ServerPermissions) : ChannelPermissions;

    internal void GroupsChanged() => FillGroupChoices();

    private void FillGroupChoices()
    {
        var names = BuiltInGroups.Concat(Groups.Select(g => g.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ListSync.Sync(GroupChoices, names, n => n);
    }

    private void FillPeopleChoices()
    {
        var names = _client.Users.Where(u => u.UserId is not null).Select(u => u.Name)
            .Concat(Groups.SelectMany(g => g.AllMemberIds).Concat(Entries.Where(x => x.UserId is not null).Select(x => x.UserId!.Value))
                          .Select(id => _client.RegisteredName(id)).OfType<string>())
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        ListSync.Sync(PeopleChoices, names, n => n);
    }

    // ── Lines ──────────────────────────────────────────────────────────────────

    private void AddEntry()
    {
        // A new line is the channel's own, so it goes after everything: last wins, as the server reads them.
        var entry = new MumbleAclEntryVm(this, new MumbleAclEntry { Group = "all", ApplyHere = true, ApplySubs = true });
        Entries.Add(entry);
        SelectedEntry = entry;
    }

    private void RemoveEntry()
    {
        if (SelectedEntry is not { Inherited: false } entry) return;
        var at = Entries.IndexOf(entry);
        Entries.Remove(entry);
        SelectedEntry = Entries.Count == 0 ? null : Entries[Math.Min(at, Entries.Count - 1)];
    }

    /// <summary>Up or down among the channel's own lines. ⚠️ Never above what is inherited: the server puts those first whatever is sent.</summary>
    private void MoveEntry(int by)
    {
        if (SelectedEntry is not { Inherited: false } entry) return;
        var at = Entries.IndexOf(entry);
        var to = at + by;
        if (to < 0 || to >= Entries.Count || Entries[to].Inherited) return;
        Entries.Move(at, to);
    }

    // ── Groups ─────────────────────────────────────────────────────────────────

    private void AddGroup()
    {
        var name = NewGroupName.Trim();
        if (name.Length == 0) return;

        // ⚠️ The server's own groups can't be made: "in", "~sub" and the rest are worked out, never kept.
        if (BuiltInGroups.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.StartsWith('~') || name.StartsWith('$') || name.StartsWith('#') || name.StartsWith('!') || name.Contains(','))
        {
            StatusText = string.Format(CultureInfo.CurrentCulture, MumbleText.AclGroupNameTaken, name);
            return;
        }
        if (Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is { } there)
        {
            SelectedGroup = there;
            StatusText = string.Format(CultureInfo.CurrentCulture, MumbleText.AclGroupNameTaken, name);
            return;
        }

        var group = new MumbleAclGroupVm(this, new MumbleAclGroup { Name = name, Inherited = false });
        Groups.Add(group);
        SelectedGroup = group;
        NewGroupName = "";
        StatusText = "";
        FillGroupChoices();
    }

    /// <summary>The channel's own group goes; one from above goes back to being as it is above.</summary>
    private void RemoveGroup()
    {
        if (SelectedGroup is not { } group) return;

        if (group.Inherited)
        {
            group.ResetToInherited();
            return;
        }

        var at = Groups.IndexOf(group);
        Groups.Remove(group);
        SelectedGroup = Groups.Count == 0 ? null : Groups[Math.Min(at, Groups.Count - 1)];
        FillGroupChoices();
    }

    /// <summary>A registered user's id by name, asking the server when it hasn't said yet. Null if it has no one by that name.</summary>
    internal async Task<uint?> IdOfAsync(string name)
    {
        if (_client.RegisteredId(name) is { } known) return known;

        // Someone connected and registered is known without asking, though maybe not yet by the server's answer.
        _namesArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = _namesArrived.Task;
        await Task.Run(() => _client.AskRegisteredAsync([], [name], default));
        await Task.WhenAny(arrived, Task.Delay(AnswerWithin));
        return _client.RegisteredId(name);
    }

    internal void Say(string text) => StatusText = text;

    // ── Saving ─────────────────────────────────────────────────────────────────

    /// <summary>What would go to the server, people's names made into ids. Null, with the reason said, if one has no id.</summary>
    internal async Task<MumbleAcl?> BuildAsync()
    {
        var acl = new MumbleAcl { Channel = _channel, InheritAcls = InheritAcls };

        foreach (var vm in Entries)
        {
            var entry = vm.ToModel();
            if (!entry.Inherited && vm.ForUser)
            {
                var name = vm.UserName.Trim();
                if (name.Length == 0 || await IdOfAsync(name) is not { } id)
                {
                    SelectedEntry = vm;
                    StatusText = string.Format(CultureInfo.CurrentCulture, MumbleText.AclNoSuchUser, name);
                    return null;
                }
                entry.UserId = id;
                entry.Group = "";
            }
            else if (!entry.Inherited && entry.Group.Trim().Length == 0)
            {
                SelectedEntry = vm;
                StatusText = MumbleText.AclNoGroup;
                return null;
            }
            acl.Entries.Add(entry);
        }

        acl.Groups.AddRange(Groups.Select(g => g.ToModel()));
        return acl;
    }

    private async Task SaveAsync()
    {
        if (Busy || IsLoading) return;
        Busy = true;
        StatusText = MumbleText.AclSaving;

        if (await BuildAsync() is not { } acl) { Busy = false; return; }

        // ⚠️ Read back after: what the server kept is what shows, so a change it didn't take doesn't look saved.
        _sent = acl;
        await Task.Run(() => _client.SetAclAsync(acl, default));
        await AskAgainAsync();
    }

    public void Dispose()
    {
        _client.AclReceived -= OnAcl;
        _client.RegisteredNamesChanged -= OnNames;
        _client.Denied -= OnDenied;
    }
}

/// <summary>One line of the channel's permissions, as it is being changed.</summary>
public sealed class MumbleAclEntryVm : ReactiveObject
{
    private readonly MumbleAclEditorVm _owner;
    private bool _forUser, _applyHere, _applySubs;
    private string _group, _userName;

    public MumbleAclEntryVm(MumbleAclEditorVm owner, MumbleAclEntry from)
    {
        _owner = owner;
        Inherited = from.Inherited;
        UserId = from.UserId;
        _forUser = from.UserId is not null;
        _group = from.Group;
        _userName = from.UserId is { } id ? owner.NameOf(id) : "";
        _applyHere = from.ApplyHere;
        _applySubs = from.ApplySubs;

        foreach (var (bit, name) in owner.PermissionsShown)
            Permissions.Add(new MumbleAclPermissionVm(this, bit, name, from.Grant.HasFlag(bit), from.Deny.HasFlag(bit)));

        // ⚠️ Kept as they came, so a save never drops a bit this window doesn't show, such as one a newer server has.
        _otherGrant = from.Grant & ~Permissions.Aggregate(MumblePermission.None, (all, p) => all | p.Bit);
        _otherDeny = from.Deny & ~Permissions.Aggregate(MumblePermission.None, (all, p) => all | p.Bit);
    }

    private readonly MumblePermission _otherGrant, _otherDeny;

    /// <summary>From a channel above: shown, but changed only there.</summary>
    public bool Inherited { get; }
    public bool IsEditable => !Inherited;

    /// <summary>The person's id as the server sent it. A name typed here is looked up when saving.</summary>
    public uint? UserId { get; private set; }

    public ObservableCollection<MumbleAclPermissionVm> Permissions { get; } = [];

    public ObservableCollection<string> GroupChoices => _owner.GroupChoices;
    public ObservableCollection<string> PeopleChoices => _owner.PeopleChoices;

    /// <summary>For one registered person, rather than a group.</summary>
    public bool ForUser
    {
        get => _forUser;
        set { this.RaiseAndSetIfChanged(ref _forUser, value); this.RaisePropertyChanged(nameof(ForGroup)); this.RaisePropertyChanged(nameof(Text)); }
    }
    public bool ForGroup { get => !ForUser; set => ForUser = !value; }

    public string Group
    {
        get => _group;
        set { this.RaiseAndSetIfChanged(ref _group, (value ?? "").Trim().TrimStart('@')); this.RaisePropertyChanged(nameof(Text)); }
    }

    public string UserName
    {
        get => _userName;
        set
        {
            this.RaiseAndSetIfChanged(ref _userName, value ?? "");
            UserId = null;
            this.RaisePropertyChanged(nameof(Text));
        }
    }

    public bool ApplyHere { get => _applyHere; set { this.RaiseAndSetIfChanged(ref _applyHere, value); this.RaisePropertyChanged(nameof(Text)); } }
    public bool ApplySubs { get => _applySubs; set { this.RaiseAndSetIfChanged(ref _applySubs, value); this.RaisePropertyChanged(nameof(Text)); } }

    /// <summary>The line as the list shows it: who, then where it applies when that isn't both.</summary>
    public string Text
    {
        get
        {
            var who = ForUser ? (UserName.Length > 0 ? UserName : "?") : "@" + Group;
            var where = (ApplyHere, ApplySubs) switch
            {
                (true, false) => MumbleText.AclHereOnly,
                (false, true) => MumbleText.AclSubsOnly,
                (false, false) => MumbleText.AclNowhere,
                _ => "",
            };
            return where.Length > 0 ? $"{who} · {where}" : who;
        }
    }

    /// <summary>The server named people this line had only by id.</summary>
    internal void NamesKnown()
    {
        if (UserId is { } id && _owner.NameOf(id) is var name && name != _userName)
        {
            _userName = name;
            this.RaisePropertyChanged(nameof(UserName));
            this.RaisePropertyChanged(nameof(Text));
        }
    }

    public MumbleAclEntry ToModel()
    {
        var grant = _otherGrant;
        var deny = _otherDeny;
        foreach (var p in Permissions)
        {
            if (p.Allow) grant |= p.Bit;
            if (p.Deny) deny |= p.Bit;
        }

        return new MumbleAclEntry
        {
            Inherited = Inherited, ApplyHere = ApplyHere, ApplySubs = ApplySubs, Grant = grant, Deny = deny,
            UserId = ForUser ? UserId : null, Group = ForUser ? "" : Group.Trim(),
        };
    }
}

/// <summary>One permission on a line: allowed, denied, or left to what came before.</summary>
public sealed class MumbleAclPermissionVm : ReactiveObject
{
    private bool _allow, _deny;

    public MumbleAclPermissionVm(MumbleAclEntryVm line, MumblePermission bit, string name, bool allow, bool deny)
    {
        Line = line;
        Bit = bit;
        Name = name;
        _allow = allow;
        _deny = deny && !allow;
    }

    public MumbleAclEntryVm Line { get; }
    public MumblePermission Bit { get; }
    public string Name { get; }
    public bool IsEditable => Line.IsEditable;

    /// <summary>⚠️ Allow and deny exclude each other, as in the standard client: ticking one clears the other.</summary>
    public bool Allow
    {
        get => _allow;
        set { this.RaiseAndSetIfChanged(ref _allow, value); if (value && _deny) { _deny = false; this.RaisePropertyChanged(nameof(Deny)); } }
    }

    public bool Deny
    {
        get => _deny;
        set { this.RaiseAndSetIfChanged(ref _deny, value); if (value && _allow) { _allow = false; this.RaisePropertyChanged(nameof(Allow)); } }
    }
}

/// <summary>One group as this channel has it, with who is added and taken away here.</summary>
public sealed class MumbleAclGroupVm : ReactiveObject
{
    private readonly MumbleAclEditorVm _owner;
    private readonly List<uint> _add, _remove, _inheritedMembers;
    private bool _inherit, _inheritable;
    private string _newMember = "";

    public MumbleAclGroupVm(MumbleAclEditorVm owner, MumbleAclGroup from)
    {
        _owner = owner;
        Name = from.Name;
        Inherited = from.Inherited;
        _inherit = from.Inherit;
        _inheritable = from.Inheritable;
        _add = [.. from.Add];
        _remove = [.. from.Remove];
        _inheritedMembers = [.. from.InheritedMembers];

        AddMemberCommand = ReactiveCommand.CreateFromTask(AddMemberAsync);
        AddMemberCommand.ThrownExceptions.Subscribe(e => owner.Say(e.Message));
        Refill();
    }

    public string Name { get; }

    /// <summary>Defined on a channel above.</summary>
    public bool Inherited { get; }

    public string Text => Inherited ? string.Format(CultureInfo.CurrentCulture, MumbleText.AclGroupInherited, Name) : Name;

    /// <summary>Removing it puts back what is above rather than removing it.</summary>
    public string RemoveText => Inherited ? MumbleText.AclGroupReset : MumbleText.AclGroupRemove;

    public bool Inherit { get => _inherit; set { this.RaiseAndSetIfChanged(ref _inherit, value); Refill(); } }
    public bool Inheritable { get => _inheritable; set => this.RaiseAndSetIfChanged(ref _inheritable, value); }

    /// <summary>Added here.</summary>
    public ObservableCollection<MumbleAclMemberVm> Members { get; } = [];

    /// <summary>Members from above, while it takes them, less those taken away here.</summary>
    public ObservableCollection<MumbleAclMemberVm> FromAbove { get; } = [];

    /// <summary>Taken away here.</summary>
    public ObservableCollection<MumbleAclMemberVm> Excluded { get; } = [];

    public bool HasFromAbove => FromAbove.Count > 0;
    public bool HasExcluded => Excluded.Count > 0;

    public string NewMember { get => _newMember; set => this.RaiseAndSetIfChanged(ref _newMember, value ?? ""); }
    public ObservableCollection<string> PeopleChoices => _owner.PeopleChoices;
    public ReactiveCommand<Unit, Unit> AddMemberCommand { get; }

    internal IEnumerable<uint> AllMemberIds => _add.Concat(_remove).Concat(_inheritedMembers);

    private async Task AddMemberAsync()
    {
        var name = NewMember.Trim();
        if (name.Length == 0) return;

        if (await _owner.IdOfAsync(name) is not { } id)
        {
            _owner.Say(string.Format(CultureInfo.CurrentCulture, MumbleText.AclNoSuchUser, name));
            return;
        }

        _remove.Remove(id);
        if (!_add.Contains(id)) _add.Add(id);
        NewMember = "";
        _owner.Say("");
        Refill();
    }

    internal void RemoveMember(uint id) { _add.Remove(id); Refill(); }
    internal void Exclude(uint id) { if (!_remove.Contains(id)) _remove.Add(id); Refill(); }
    internal void Include(uint id) { _remove.Remove(id); Refill(); }

    /// <summary>Back to just what is above: nothing added or taken away here, and both switches on.</summary>
    internal void ResetToInherited()
    {
        _add.Clear();
        _remove.Clear();
        _inheritable = true;
        this.RaisePropertyChanged(nameof(Inheritable));
        Inherit = true;
    }

    internal void NamesKnown() => Refill();

    private void Refill()
    {
        Fill(Members, _add, MumbleAclMemberRole.Added);
        Fill(FromAbove, Inherit ? _inheritedMembers.Where(id => !_remove.Contains(id) && !_add.Contains(id)) : [], MumbleAclMemberRole.FromAbove);
        Fill(Excluded, _remove, MumbleAclMemberRole.Excluded);
        this.RaisePropertyChanged(nameof(HasFromAbove));
        this.RaisePropertyChanged(nameof(HasExcluded));

        void Fill(ObservableCollection<MumbleAclMemberVm> into, IEnumerable<uint> ids, MumbleAclMemberRole role)
        {
            into.Clear();
            foreach (var id in ids.Distinct().OrderBy(_owner.NameOf, StringComparer.CurrentCultureIgnoreCase))
                into.Add(new MumbleAclMemberVm(this, id, _owner.NameOf(id), role));
        }
    }

    public MumbleAclGroup ToModel() => new()
    {
        Name = Name, Inherited = Inherited, Inherit = Inherit, Inheritable = Inheritable,
        Add = [.. _add], Remove = [.. _remove], InheritedMembers = [.. _inheritedMembers],
    };
}

public enum MumbleAclMemberRole { Added, FromAbove, Excluded }

/// <summary>Someone in a group's list, with what can be done about them there.</summary>
public sealed class MumbleAclMemberVm
{
    public MumbleAclMemberVm(MumbleAclGroupVm group, uint id, string name, MumbleAclMemberRole role)
    {
        Id = id;
        Name = name;
        ActionText = role switch
        {
            MumbleAclMemberRole.Added => MumbleText.AclMemberRemove,
            MumbleAclMemberRole.FromAbove => MumbleText.AclMemberExclude,
            _ => MumbleText.AclMemberInclude,
        };
        ActCommand = ReactiveCommand.Create(() =>
        {
            switch (role)
            {
                case MumbleAclMemberRole.Added: group.RemoveMember(id); break;
                case MumbleAclMemberRole.FromAbove: group.Exclude(id); break;
                default: group.Include(id); break;
            }
        });
    }

    public uint Id { get; }
    public string Name { get; }
    public string ActionText { get; }
    public ReactiveCommand<Unit, Unit> ActCommand { get; }
}
