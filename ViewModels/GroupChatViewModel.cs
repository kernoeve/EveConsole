using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Jabber;
using EveConsole.Services.Mumble;
using ReactiveUI;

namespace EveConsole.ViewModels;

public enum GroupRowKind { JabberServer, Room, Invite, MumbleServer, IrcServer, IrcChannel, IrcInvite }

/// <summary>
/// A line on the left: a server, or a room under one.
///
/// <para>⚠️ Kept and updated in place. Rebuilt on each change, the list control dropped its selection with every
/// message that arrived, and the pane on the right went stale behind it.</para>
/// </summary>
public sealed class GroupChatRowVm : ReactiveObject
{
    public GroupRowKind Kind { get; init; }
    public int ServerId { get; init; }

    /// <summary>The room's address, for a room or an invitation to one.</summary>
    public string Key { get; init; } = "";
    public string Text { get; init; } = "";
    public string Detail { get; init; } = "";

    /// <summary>Red, amber, green or grey: what state it is in at a glance.</summary>
    public string Dot { get; init; } = "";
    public int Unread { get; init; }
    public int Mentions { get; init; }

    /// <summary>What a room is for: see <see cref="JabberRoomType"/>.</summary>
    public int RoomType { get; init; }

    /// <summary>The same, kept up to date on a row that stays in the list.</summary>
    public int ShownRoomType { get; private set; }

    public string Identity => $"{Kind}:{ServerId}:{Key}";

    public bool IsServer => Kind is GroupRowKind.JabberServer or GroupRowKind.MumbleServer or GroupRowKind.IrcServer;

    /// <summary>A room with saved conversation: a Jabber room or an IRC channel.</summary>
    public bool IsRoom => Kind is GroupRowKind.Room or GroupRowKind.IrcChannel;
    public bool IsMumble => Kind == GroupRowKind.MumbleServer;

    /// <summary>⚠️ Which service a row is: server ids are each service's own, so a Jabber server and an IRC server can share one.</summary>
    public bool IsJabber => Kind is GroupRowKind.JabberServer or GroupRowKind.Room or GroupRowKind.Invite;
    public bool IsIrc => Kind is GroupRowKind.IrcServer or GroupRowKind.IrcChannel or GroupRowKind.IrcInvite;

    public Avalonia.Thickness Indent => new(IsServer ? 0 : 14, IsServer ? 4 : 0, 0, 0);

    private string _text = "", _detail = "", _dot = "";
    private int _unread, _mentions;

    public string ShownText { get => _text; private set => this.RaiseAndSetIfChanged(ref _text, value); }
    public string ShownDetail { get => _detail; private set { this.RaiseAndSetIfChanged(ref _detail, value); this.RaisePropertyChanged(nameof(HasDetail)); } }
    public bool HasDetail => _detail.Length > 0;

    public string ShownDot
    {
        get => _dot;
        private set
        {
            this.RaiseAndSetIfChanged(ref _dot, value);
            foreach (var name in new[] { nameof(DotGreen), nameof(DotAmber), nameof(DotRed) }) this.RaisePropertyChanged(name);
        }
    }
    public bool DotGreen => _dot == "green";
    public bool DotAmber => _dot == "amber";
    public bool DotRed => _dot == "red";

    public int ShownUnread
    {
        get => _unread;
        private set
        {
            this.RaiseAndSetIfChanged(ref _unread, value);
            this.RaisePropertyChanged(nameof(HasUnread));
            this.RaisePropertyChanged(nameof(UnreadText));
            this.RaisePropertyChanged(nameof(UnreadTip));
        }
    }
    public bool HasUnread => _unread > 0;
    public string UnreadText => _unread.ToString("N0", CultureInfo.CurrentCulture);
    public string UnreadTip => string.Format(CultureInfo.CurrentCulture, GroupChatText.UnreadTip, _unread);

    public int ShownMentions
    {
        get => _mentions;
        private set
        {
            this.RaiseAndSetIfChanged(ref _mentions, value);
            this.RaisePropertyChanged(nameof(HasMentions));
            this.RaisePropertyChanged(nameof(MentionsText));
            this.RaisePropertyChanged(nameof(MentionsTip));
        }
    }
    public bool HasMentions => _mentions > 0;
    public string MentionsText => "@" + _mentions.ToString("N0", CultureInfo.CurrentCulture);
    public string MentionsTip => string.Format(CultureInfo.CurrentCulture, GroupChatText.MentionsTip, _mentions);

    /// <summary>Takes on what a freshly worked-out line says.</summary>
    public GroupChatRowVm Fill(GroupChatRowVm from)
    {
        ShownText = from.Text; ShownDetail = from.Detail; ShownDot = from.Dot;
        ShownUnread = from.Unread; ShownMentions = from.Mentions;
        ShownRoomType = from.RoomType;
        return this;
    }
}

/// <summary>One message in a room or on a Mumble server. Both read the same on the screen.</summary>
public sealed partial class GroupLineVm : ReactiveObject
{
    public GroupLineVm(long id, string sender, string body, string when, string channel, bool isMention, EmoteSet? emotes = null,
        int deleted = JabberDeleted.No, string deletedReason = "")
    {
        Id = id;
        IsDeleted = deleted != JabberDeleted.No;
        Emotes = IsDeleted ? EmoteSet.None : emotes ?? EmoteSet.None;
        Sender = sender;
        Body = IsDeleted ? DeletedText(deleted, deletedReason) : body;
        When = when;
        Channel = channel;
        IsMention = isMention && !IsDeleted;
        Links = IsDeleted ? [] : [.. UrlPattern().Matches(body).Select(x => x.Value.TrimEnd('.', ',', ')', ';', ':', '!', '?', '\'', '"')).Distinct()];
        Pictures = ChatPictureVm.Of(Links);
    }

    /// <summary>The pictures it links to, shown under it.</summary>
    public IReadOnlyList<ChatPictureVm> Pictures { get; }
    public bool HasPictures => Pictures.Count > 0;

    /// <summary>The words, unless they are only a picture's link: the picture shows, with the link under it.</summary>
    public bool ShowsBody => !(Pictures.Count == 1 && Body.Trim() == Pictures[0].Url);

    /// <summary>A deleted message keeps its place, and says so, as other clients show it.</summary>
    public bool IsDeleted { get; }

    /// <summary>Whose picture goes beside it.</summary>
    public AvatarRef? Avatar { get; init; }

    /// <summary>What a reaction to it names: the room's id for it. Blank where it cannot be reacted to.</summary>
    public string ReactTarget { get; init; } = "";
    public bool CanReact => ReactTarget.Length > 0 && !IsDeleted;

    /// <summary>The id it was sent with, which a correction names.</summary>
    public string MessageId { get; init; } = "";
    public bool IsOutgoing { get; init; }

    /// <summary>What it answers, shown above it: who, and the start of what they said.</summary>
    public string ReplyText { get; init; } = "";
    public bool HasReply => ReplyText.Length > 0;

    private bool _canEdit;

    /// <summary>This account's latest message here, the only one the protocol lets it correct.</summary>
    public bool CanEdit { get => _canEdit; set => this.RaiseAndSetIfChanged(ref _canEdit, value); }

    /// <summary>An IRC line this person said, on a server that lets it be taken back (REDACT).</summary>
    public bool Redactable { get; init; }

    /// <summary>One of this account's, which it can take back.</summary>
    public bool CanDelete => !IsDeleted && (Redactable || IsOutgoing && ReactTarget.Length > 0 && MessageId.Length > 0);

    public bool CanReply => CanReact;

    public ObservableCollection<ReactionChipVm> Reactions { get; } = [];

    /// <summary>Takes on its reactions as they stand. ⚠️ In place: the chips are on screen.</summary>
    public void SetReactions(IReadOnlyList<JabberReactionCount>? counts)
    {
        Reactions.Clear();
        foreach (var c in counts ?? []) Reactions.Add(new ReactionChipVm(c));
    }

    public static string DeletedText(int deleted, string reason) =>
        deleted != JabberDeleted.ByModerator ? JabberText.MessageDeleted
        : reason.Length > 0 ? string.Format(CultureInfo.CurrentCulture, JabberText.MessageRemovedReason, reason)
        : JabberText.MessageRemoved;

    public long Id { get; }
    public string Sender { get; }
    public string Body { get; }
    public string When { get; }

    /// <summary>Which Mumble channel it was said in. A room's are all in the room.</summary>
    public string Channel { get; }
    public bool HasChannel => Channel.Length > 0;

    public bool IsMention { get; }

    /// <summary>The emotes of the server it was said on. None for a Mumble server's.</summary>
    public EmoteSet Emotes { get; }

    /// <summary>The web addresses in the message, to open. Web only: a link in a chat decides nothing else.</summary>
    public IReadOnlyList<string> Links { get; }
    public bool HasLinks => Links.Count > 0;

    [System.Text.RegularExpressions.GeneratedRegex(@"https?://[^\s<>""]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex UrlPattern();
}

/// <summary>One emoji under a message, how many chose it, and who, for its tip.</summary>
public sealed class ReactionChipVm(JabberReactionCount count)
{
    public string Emoji => count.Emoji;
    public string CountText => count.Count.ToString("N0", CultureInfo.CurrentCulture);
    public string Tip => string.Join(", ", count.Who);
    public bool Mine => count.Mine;
}

public sealed record JabberAlertChoice(int Mode, string Label);

public sealed record JabberRoomTypeChoice(int Type, string Label);

public sealed record GroupRoomSortChoice(int Sort, string Label);

public sealed record JabberOccupantVm(string Nick, string Dot, string Role)
{
    /// <summary>For someone in an IRC channel: what the server says about them. Null for a Jabber room's.</summary>
    public EveConsole.Services.Irc.IrcMember? IrcMember { get; init; }

    /// <summary>For an IRC channel: this person is the one using the app.</summary>
    public bool IrcSelf { get; init; }

    /// <summary>All the room says about them: affiliation, real address, whether it is the person themselves.</summary>
    public XmppOccupant? Person { get; init; }

    public bool IsSelf => Person?.Self ?? IrcSelf;

    public AvatarRef? Avatar { get; init; }

    public bool DotGreen => Dot == "green";
    public bool DotAmber => Dot == "amber";
    public bool DotRed => Dot == "red";
}

/// <summary>A room a server lists, with whatever it says about itself once it has been asked.</summary>
public sealed class GroupRoomChoiceVm(XmppRoomInfo room, bool joined) : ReactiveObject
{
    public string Jid => room.Jid;

    /// <summary>The address under the name, where it says something more: an IRC channel's name is its address.</summary>
    public bool HasAddressLine => !string.Equals(Jid, Name, StringComparison.OrdinalIgnoreCase);

    private bool _isJoined = joined;
    public bool IsJoined { get => _isJoined; set => this.RaiseAndSetIfChanged(ref _isJoined, value); }

    /// <summary>How many are in it, to sort by: the number, as "10" would come before "2" by its words. Null until it says.</summary>
    public int? PeopleCount { get; private set; }

    private string _name = room.Name.Length > 0 ? room.Name : XmppAddress.Parse(room.Jid).Local;
    private string _description = "", _topic = "", _people = "", _flags = "";

    public string Name { get => _name; private set => this.RaiseAndSetIfChanged(ref _name, value); }
    public string Description { get => _description; private set { this.RaiseAndSetIfChanged(ref _description, value); this.RaisePropertyChanged(nameof(HasDescription)); } }
    public bool HasDescription => Description.Length > 0;
    public string Topic { get => _topic; private set { this.RaiseAndSetIfChanged(ref _topic, value); this.RaisePropertyChanged(nameof(HasTopic)); } }
    public bool HasTopic => Topic.Length > 0;
    public string People { get => _people; private set { this.RaiseAndSetIfChanged(ref _people, value); this.RaisePropertyChanged(nameof(HasPeople)); } }
    public bool HasPeople => People.Length > 0;
    public string Flags { get => _flags; private set { this.RaiseAndSetIfChanged(ref _flags, value); this.RaisePropertyChanged(nameof(HasFlags)); } }
    public bool HasFlags => Flags.Length > 0;

    public void Apply(XmppRoomDetails details)
    {
        if (details.Name.Length > 0) Name = details.Name;
        Description = details.Description;
        Topic = details.Subject.Length > 0 ? string.Format(CultureInfo.CurrentCulture, GroupChatText.RoomTopic, details.Subject) : "";
        PeopleCount = details.People;
        People = details.People is { } count ? string.Format(CultureInfo.CurrentCulture, GroupChatText.RoomPeople, count) : "";
        Flags = string.Join(" · ", new[]
        {
            details.NeedsPassword ? GroupChatText.FlagPassword : "",
            details.MembersOnly ? GroupChatText.FlagMembersOnly : "",
            details.Moderated ? GroupChatText.FlagModerated : "",
            details.Temporary ? GroupChatText.FlagTemporary : "",
        }.Where(f => f.Length > 0));
    }

    public bool Matches(string words) =>
        Name.Contains(words, StringComparison.CurrentCultureIgnoreCase)
        || Jid.Contains(words, StringComparison.CurrentCultureIgnoreCase)
        || Description.Contains(words, StringComparison.CurrentCultureIgnoreCase);

    public const int SortByName = 0;
    public const int SortByPeople = 1;

    /// <summary>In the order asked for. Most people first, and a room that has not said how many goes last, by name.</summary>
    public static IEnumerable<GroupRoomChoiceVm> Sorted(IEnumerable<GroupRoomChoiceVm> rooms, int sort) =>
        sort == SortByPeople
            ? rooms.OrderByDescending(r => r.PeopleCount ?? -1).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            : rooms.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
}

/// <summary>
/// Group Chat: what is said to many at once, by server. Every Jabber server with the rooms kept on it, and each Mumble
/// server as one entry.
///
/// <para>⚠️ Mumble is one entry on purpose. A Mumble client only ever hears the channel it is in, so its chat is a single
/// thread that follows the person around, not a set of conversations to choose between. It lasts as long as the app
/// runs, because a Mumble server keeps none of it.</para>
///
/// <para>⚠️ Reading is what clears a count. A message in what is open only counts as read while the tool is actually
/// in front of the person.</para>
/// </summary>
public sealed partial class GroupChatViewModel : ReactiveObject
{
    public const string SoundPref = "group_chat.alert_sound";

    private const int PageSize = 200;

    /// <summary>A busy room left open for days should not grow without end. The rest are a click away.</summary>
    private const int MostShown = MumbleService.MaxChatLines;

    /// <summary>A server can list thousands, and each is a question of its own: a few at a time, or it would be flooded.</summary>
    private const int RoomsAskedAtOnce = 6;
    private const int ResortEveryMs = 1500;

    private readonly JabberService _jabber;
    private readonly MumbleService _mumble;
    private readonly AlarmSoundService _sound;
    private readonly AlarmMuteState _mute;
    private readonly AppPreferencesService _prefs;
    private readonly EmoteService? _emotes;

    private List<JabberUnread> _unread = [];
    private readonly Dictionary<int, (int Count, int Mentions)> _mumbleUnread = [];
    private readonly Dictionary<string, string> _drafts = [];
    private int _refreshQueued;
    private Task? _refreshing;
    private bool _refreshAgain;
    private bool _loadingDetail;

    public GroupChatViewModel(JabberService jabber, MumbleService mumble, AppPreferencesService prefs, AlarmSoundService sound, AlarmMuteState mute, EmoteService? emotes = null,
        EveConsole.Services.Irc.IrcService? irc = null)
    {
        _irc = irc;
        _emotes = emotes;
        _jabber = jabber;
        _mumble = mumble;
        _prefs = prefs;
        _sound = sound;
        _mute = mute;

        SendCommand = ReactiveCommand.CreateFromTask(SendAsync, this.WhenAnyValue(x => x.Draft, x => x.CanSend, (d, can) => can && !string.IsNullOrWhiteSpace(d)));
        LoadEarlierCommand = ReactiveCommand.CreateFromTask(LoadEarlierAsync);
        LeaveRoomCommand = ReactiveCommand.CreateFromTask(LeaveRoomAsync);
        ConnectCommand = ReactiveCommand.Create(Connect);
        AddRoomsCommand = ReactiveCommand.CreateFromTask(AddRoomsAsync);
        JoinAgainCommand = ReactiveCommand.CreateFromTask(JoinAgainAsync);
        JoinRoomCommand = ReactiveCommand.CreateFromTask(JoinRoomAsync);
        CancelAddRoomsCommand = ReactiveCommand.Create(FinishAddingRooms);
        AcceptInviteCommand = ReactiveCommand.CreateFromTask(() => AnswerInviteAsync(true));
        DeclineInviteCommand = ReactiveCommand.CreateFromTask(() => AnswerInviteAsync(false));

        foreach (var command in new IHandleObservableErrors[]
                 {
                     SendCommand, LoadEarlierCommand, LeaveRoomCommand, AddRoomsCommand, JoinAgainCommand, JoinRoomCommand, AcceptInviteCommand, DeclineInviteCommand,
                 })
            command.ThrownExceptions.Subscribe(e => StatusText = e.Message);

        AlertChoices =
        [
            new(JabberRoomAlert.Mentions, JabberText.RoomAlertMentions),
            new(JabberRoomAlert.All, JabberText.RoomAlertAll),
            new(JabberRoomAlert.None, JabberText.RoomAlertNone),
        ];

        // ⚠️ The one list of room types, for both dropdowns and the room list: a new type only needs adding here.
        RoomTypeChoices =
        [
            new(JabberRoomType.General, GroupChatText.RoomTypeGeneral),
            new(JabberRoomType.Intel, GroupChatText.RoomTypeIntel),
        ];
        _joinRoomType = RoomTypeChoices[0];

        RoomSortChoices =
        [
            new(GroupRoomChoiceVm.SortByName, GroupChatText.RoomSortName),
            new(GroupRoomChoiceVm.SortByPeople, GroupChatText.RoomSortPeople),
        ];
        _roomSort = RoomSortChoices[0];

        _soundOn = _prefs.GetBool(SoundPref, true);

        _jabber.SessionChanged += _ => QueueRefresh();
        _jabber.UnreadChanged += QueueRefresh;
        _jabber.MessageStored += m => Dispatcher.UIThread.Post(() => { OnRoomMessage(m); RaisePanelLine(m); });
        _jabber.AlertArrived += OnRoomAlert;
        _jabber.ConversationEdited += (account, conversation) => Dispatcher.UIThread.Post(() =>
        {
            if (SelectedRow is { IsRoom: true } row && row.ServerId == account && row.Key == conversation) _ = OpenSelectedAsync();
        });
        _jabber.ReactionsChanged += (account, conversation) => Dispatcher.UIThread.Post(() =>
        {
            if (SelectedRow is { IsRoom: true } row && row.ServerId == account && row.Key == conversation) _ = LoadReactionsAsync();
        });
        _mumble.SessionChanged += _ => QueueRefresh();
        _mumble.ChatReceived += (session, line) => Dispatcher.UIThread.Post(() => { OnMumbleChat(session, line); RaisePanelLine(session, line); });
        HookIrc();

        QueueRefresh();
    }

    // ── The list on the left ──────────────────────────────────────────────────

    public ObservableCollection<GroupChatRowVm> Rows { get; } = [];

    private GroupChatRowVm? _selectedRow;
    public GroupChatRowVm? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (ReferenceEquals(_selectedRow, value)) return;

            this.RaiseAndSetIfChanged(ref _selectedRow, value);
            IsAddingRooms = false;
            CancelDraftMode();
            this.RaisePropertyChanged(nameof(Draft));
            DraftEmotes = value is { IsRoom: true } room ? EmotesOf(room.ServerId) : EmoteSet.None;
            _ = OpenSelectedAsync();
        }
    }

    // ── The icon at the top of the main window ────────────────────────────────

    private int _totalUnread, _totalMentions;

    /// <summary>New messages across every room and server. ⚠️ From the rows, not from what was counted: that holds direct messages too.</summary>
    public int TotalUnread
    {
        get => _totalUnread;
        private set
        {
            if (_totalUnread == value) return;
            this.RaiseAndSetIfChanged(ref _totalUnread, value);
            this.RaisePropertyChanged(nameof(HasUnread));
            this.RaisePropertyChanged(nameof(HasOnlyUnread));
            this.RaisePropertyChanged(nameof(UnreadText));
            this.RaisePropertyChanged(nameof(IconTip));
        }
    }

    /// <summary>The person's name, said in any of them and not yet seen.</summary>
    public int TotalMentions
    {
        get => _totalMentions;
        private set
        {
            if (_totalMentions == value) return;
            this.RaiseAndSetIfChanged(ref _totalMentions, value);
            this.RaisePropertyChanged(nameof(HasMentions));
            this.RaisePropertyChanged(nameof(HasOnlyUnread));
            this.RaisePropertyChanged(nameof(MentionsText));
            this.RaisePropertyChanged(nameof(IconTip));
        }
    }

    public bool HasUnread => TotalUnread > 0;
    public bool HasMentions => TotalMentions > 0;

    /// <summary>A busy room is not an alarm: only being named lights the icon up in warning colours.</summary>
    public bool HasOnlyUnread => HasUnread && !HasMentions;

    public string UnreadText => TotalUnread.ToString("N0", CultureInfo.CurrentCulture);
    public string MentionsText => "@" + TotalMentions.ToString("N0", CultureInfo.CurrentCulture);

    public string IconTip
    {
        get
        {
            var lines = new List<string> { ShellText.NavGroupChat };
            if (HasUnread) lines.Add(string.Format(CultureInfo.CurrentCulture, GroupChatText.UnreadTip, TotalUnread));
            if (HasMentions) lines.Add(string.Format(CultureInfo.CurrentCulture, GroupChatText.MentionsTip, TotalMentions));
            return string.Join("\n", lines);
        }
    }

    /// <summary>Many changes come in a burst on connecting: one refresh for the lot.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;

        _ = Task.Delay(TimeSpan.FromMilliseconds(150)).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        }, TaskScheduler.Default);
    }

    /// <summary>Brings the list up to date. ⚠️ One at a time: each reads the database first, and an older one finishing after a newer one put the old state back.</summary>
    public Task RefreshAsync()
    {
        if (_refreshing is { IsCompleted: false })
        {
            _refreshAgain = true;
            return _refreshing;
        }

        return _refreshing = Run();

        async Task Run()
        {
            do
            {
                _refreshAgain = false;
                await RefreshOnceAsync();
            }
            while (_refreshAgain);
        }
    }

    private async Task RefreshOnceAsync()
    {
        _unread = await Task.Run(() => _jabber.UnreadAsync());
        var rooms = await Task.Run(() => _jabber.SavedRoomsAsync());

        var rows = new List<GroupChatRowVm>();
        foreach (var session in _jabber.Sessions.OrderBy(s => NameOf(s), StringComparer.CurrentCultureIgnoreCase))
            AddJabberRows(rows, session, rooms);

        await AddIrcRowsAsync(rows);

        foreach (var session in _mumble.Sessions.OrderBy(s => NameOf(s), StringComparer.CurrentCultureIgnoreCase))
        {
            var count = _mumbleUnread.GetValueOrDefault(session.Server.Id);
            rows.Add(new GroupChatRowVm
            {
                Kind = GroupRowKind.MumbleServer, ServerId = session.Server.Id, Text = NameOf(session),
                Detail = $"{ShellText.NavMumble} · {MumbleViewModel.StateText(session)}",
                Dot = session.State switch
                {
                    MumbleState.Connected => "green",
                    MumbleState.Failed => "red",
                    MumbleState.Offline => "grey",
                    _ => "amber",
                },
                Unread = count.Count, Mentions = count.Mentions,
            });
        }

        SyncRows(rows);
        HasServers = Rows.Count > 0;
        TotalUnread = Rows.Sum(r => r.ShownUnread);
        TotalMentions = Rows.Sum(r => r.ShownMentions);
        RefreshDetail();
        SyncAddingRooms();
    }

    private void AddJabberRows(List<GroupChatRowVm> rows, JabberSession session, List<JabberRoom> rooms)
    {
        var id = session.Account.Id;
        rows.Add(new GroupChatRowVm
        {
            Kind = GroupRowKind.JabberServer, ServerId = id, Text = NameOf(session),
            Detail = $"{ShellText.NavJabber} · {JabberViewModel.StateText(session)}",
            Dot = JabberViewModel.StateDot(session.State),
        });

        foreach (var invite in session.Invites)
            rows.Add(new GroupChatRowVm
            {
                Kind = GroupRowKind.Invite, ServerId = id, Key = invite.Room, Text = XmppAddress.Parse(invite.Room).Local, Dot = "amber",
                Detail = string.Format(CultureInfo.CurrentCulture, JabberText.RowInvitedBy, invite.From),
            });

        foreach (var room in rooms.Where(r => r.AccountId == id).OrderBy(JabberViewModel.DisplayRoom, StringComparer.CurrentCultureIgnoreCase))
        {
            var state = session.Locked(s => s.RoomMap.GetValueOrDefault(room.RoomJid));
            var count = _unread.FirstOrDefault(u => u.AccountId == id && u.Conversation == room.RoomJid);
            rows.Add(new GroupChatRowVm
            {
                Kind = GroupRowKind.Room, ServerId = id, Key = room.RoomJid, Text = JabberViewModel.DisplayRoom(room),
                Detail = state?.Failure is { } failure ? JabberViewModel.RoomFailureText(failure, state.FailureText) : RoomTypeNote(room.RoomType),
                RoomType = room.RoomType,
                Dot = state is { Joined: true } ? "green" : state?.Failure is not null ? "red" : "grey",
                Unread = count.Count, Mentions = count.Mentions,
            });
        }
    }

    /// <summary>A room's type as its line in the list says it. The usual type needs no saying.</summary>
    private string RoomTypeNote(int type) =>
        type == JabberRoomType.General ? "" : RoomTypeChoices.FirstOrDefault(c => c.Type == type)?.Label ?? "";

    private static string NameOf(JabberSession session) => session.Account.Name.Length > 0 ? session.Account.Name : session.Account.Address;
    private static string NameOf(MumbleSession session) => session.Server.Name.Length > 0 ? session.Server.Name : session.Server.Host;

    /// <summary>Makes the list say this, moving as little as it can. See <see cref="GroupChatRowVm"/>.</summary>
    private void SyncRows(List<GroupChatRowVm> wanted)
    {
        var have = Rows.ToDictionary(r => r.Identity);

        for (var i = 0; i < wanted.Count; i++)
            wanted[i] = have.TryGetValue(wanted[i].Identity, out var kept) ? kept.Fill(wanted[i]) : wanted[i].Fill(wanted[i]);

        var keep = wanted.Select(r => r.Identity).ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(Rows[i].Identity))
            {
                if (ReferenceEquals(Rows[i], SelectedRow)) SelectedRow = null;
                Rows.RemoveAt(i);
            }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], wanted[i])) continue;

            var at = Rows.IndexOf(wanted[i]);
            if (at >= 0) Rows.Move(at, i);
            else Rows.Insert(i, wanted[i]);
        }
    }

    // ── The pane on the right ─────────────────────────────────────────────────

    private bool _hasServers;
    public bool HasServers
    {
        get => _hasServers;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasServers, value);
            this.RaisePropertyChanged(nameof(ShowWelcome));
            this.RaisePropertyChanged(nameof(ShowNothing));
        }
    }

    public bool ShowWelcome => !HasServers;
    public bool ShowNothing => HasServers && SelectedRow is null && !IsAddingRooms;
    public bool ShowConversation => SelectedRow is { Kind: GroupRowKind.Room or GroupRowKind.MumbleServer or GroupRowKind.IrcChannel } && !IsAddingRooms;
    public bool ShowOccupants => SelectedRow is { IsRoom: true } && !IsAddingRooms;
    public bool ShowRoomControls => SelectedRow is { IsRoom: true };
    public bool ShowSubchannels => SelectedRow is { IsMumble: true };
    public bool ShowJabberServer => SelectedRow is { Kind: GroupRowKind.JabberServer or GroupRowKind.IrcServer } && !IsAddingRooms;
    public bool ShowInvite => SelectedRow is { Kind: GroupRowKind.Invite or GroupRowKind.IrcInvite };

    private JabberSession? JabberOf(GroupChatRowVm? row) => row is { IsJabber: true } ? _jabber.SessionOf(row.ServerId) : null;
    private MumbleSession? MumbleOf(GroupChatRowVm? row) => row is { IsMumble: true } ? _mumble.SessionOf(row.ServerId) : null;

    public ObservableCollection<GroupLineVm> Lines { get; } = [];
    public ObservableCollection<JabberOccupantVm> Occupants { get; } = [];

    /// <summary>Asks the view to bring the newest message into sight.</summary>
    public event Action? ScrollToEndRequested;

    private string _title = "", _subtitle = "", _serverState = "", _archiveText = "", _draftHint = "", _cannotSend = "", _roomProblem = "", _sendingFile = "", _status = "";
    private bool _canSend, _canAttach, _hasEarlier, _canConnect, _canAddRooms, _includeSubchannels;

    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => this.RaiseAndSetIfChanged(ref _subtitle, value); }
    public string ServerState { get => _serverState; private set => this.RaiseAndSetIfChanged(ref _serverState, value); }
    public string ArchiveText { get => _archiveText; private set { this.RaiseAndSetIfChanged(ref _archiveText, value); this.RaisePropertyChanged(nameof(HasArchiveText)); } }
    public bool HasArchiveText => ArchiveText.Length > 0;
    public string DraftHint { get => _draftHint; private set => this.RaiseAndSetIfChanged(ref _draftHint, value); }
    public string CannotSend { get => _cannotSend; private set { this.RaiseAndSetIfChanged(ref _cannotSend, value); this.RaisePropertyChanged(nameof(HasCannotSend)); } }
    public bool HasCannotSend => CannotSend.Length > 0;
    public string RoomProblem { get => _roomProblem; private set { this.RaiseAndSetIfChanged(ref _roomProblem, value); this.RaisePropertyChanged(nameof(HasRoomProblem)); } }
    public bool HasRoomProblem => RoomProblem.Length > 0;
    public bool CanSend { get => _canSend; private set => this.RaiseAndSetIfChanged(ref _canSend, value); }
    public bool CanAttach { get => _canAttach; private set => this.RaiseAndSetIfChanged(ref _canAttach, value); }
    public bool HasEarlier { get => _hasEarlier; private set => this.RaiseAndSetIfChanged(ref _hasEarlier, value); }
    public bool CanConnect { get => _canConnect; private set => this.RaiseAndSetIfChanged(ref _canConnect, value); }
    public bool CanAddRooms { get => _canAddRooms; private set => this.RaiseAndSetIfChanged(ref _canAddRooms, value); }

    /// <summary>A Mumble message goes to the channel the person is in, and optionally everything under it.</summary>
    public bool IncludeSubchannels { get => _includeSubchannels; set => this.RaiseAndSetIfChanged(ref _includeSubchannels, value); }

    public string SendingFile { get => _sendingFile; private set { this.RaiseAndSetIfChanged(ref _sendingFile, value); this.RaisePropertyChanged(nameof(IsSendingFile)); } }
    public bool IsSendingFile => SendingFile.Length > 0;

    public string StatusText { get => _status; set { this.RaiseAndSetIfChanged(ref _status, value); this.RaisePropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>What is being written here. Each room and server keeps its own while the person looks elsewhere.</summary>
    public string Draft
    {
        get => SelectedRow is { } row ? _drafts.GetValueOrDefault(row.Identity, "") : "";
        set
        {
            if (SelectedRow is not { } row || Draft == value) return;
            _drafts[row.Identity] = value;
            this.RaisePropertyChanged();
        }
    }

    private bool _isBeingLookedAt;

    /// <summary>Whether this tool is on screen, in a window with the person's attention. Set by the view.</summary>
    public bool IsBeingLookedAt
    {
        get => _isBeingLookedAt;
        set
        {
            _isBeingLookedAt = value;
            if (value) _ = MarkOpenReadAsync();
        }
    }

    private void RaisePanes()
    {
        foreach (var name in new[] { nameof(ShowNothing), nameof(ShowConversation), nameof(ShowOccupants), nameof(ShowRoomControls), nameof(ShowSubchannels), nameof(ShowJabberServer), nameof(ShowInvite), nameof(ShowAddRooms) })
            this.RaisePropertyChanged(name);
    }

    private async Task OpenSelectedAsync()
    {
        RaisePanes();

        Lines.Clear();
        Occupants.Clear();
        HasEarlier = false;
        StatusText = "";
        RefreshDetail();

        var row = SelectedRow;
        if (row is { IsMumble: true })
        {
            if (MumbleOf(row) is { } session)
                foreach (var line in session.Chat) Lines.Add(ToLine(session, line));
        }
        else if (row is { IsIrc: true, IsRoom: true })
        {
            if (!await OpenIrcChannelAsync(row)) return;
        }
        else if (row is { IsRoom: true })
        {
            if (JabberOf(row) is { State: JabberState.Connected } roomSession) _ = LearnRoomAsync(roomSession, row.Key);
            var page = await Task.Run(() => _jabber.LoadConversationAsync(row.ServerId, row.Key, limit: PageSize));
            if (!ReferenceEquals(SelectedRow, row)) return;

            foreach (var m in page) Lines.Add(ToLine(m));
            HasEarlier = page.Count == PageSize;
            MarkLastEditable();
            await LoadReactionsAsync();
        }
        else return;

        ScrollToEndRequested?.Invoke();
        await MarkOpenReadAsync();
    }

    private GroupLineVm ToLine(JabberStoredMessage m) => new(
        m.Id, m.Sender, m.Body,
        ChatTime.Of(m.SentAt),
        "", m.IsMention, EmotesOf(m.AccountId), m.Deleted, m.DeletedReason)
    {
        Avatar = new AvatarRef(JabberService.AvatarServiceName, m.AccountId, m.IsOutgoing ? "me" : $"{m.Conversation}/{m.Sender}", m.Sender),
        ReactTarget = m.IsRoom ? m.StanzaId : "",
        MessageId = m.MessageId,
        IsOutgoing = m.IsOutgoing,
        ReplyText = ReplyTextFor(m.ReplyId, m.ReplyQuote, Lines, l => l.ReactTarget, l => l.Sender, l => l.Body),
    };

    /// <summary>
    /// What a reply answers, as a line above it: who and the start of what they said, from the message itself where it
    /// is showing, else what the reply quoted of it.
    /// </summary>
    public static string ReplyTextFor<T>(string replyId, string quote, IEnumerable<T> lines, Func<T, string> idOf, Func<T, string> senderOf, Func<T, string> bodyOf)
    {
        if (replyId.Length == 0) return "";

        // ⚠️ Found or not, said apart: a default is not "none" for every kind of line.
        var found = false;
        T answered = default!;
        foreach (var line in lines)
            if (idOf(line) == replyId) { answered = line; found = true; }

        var text = found ? $"{senderOf(answered)}: {bodyOf(answered)}" : quote;
        text = string.Join(" ", text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return "↩ " + (text.Length > 120 ? text[..120] + "…" : text.Length > 0 ? text : JabberText.ReplyUnknown);
    }

    /// <summary>Only this account's latest message here can be corrected: the protocol's rule, which other clients keep to.</summary>
    private void MarkLastEditable()
    {
        var last = Lines.LastOrDefault(l => l.IsOutgoing && !l.IsDeleted && l.MessageId.Length > 0);
        foreach (var line in Lines) line.CanEdit = ReferenceEquals(line, last);
    }

    // ── What the message box is doing: a reply, or a correction ──────────────

    private enum DraftMode { None, Reply, Edit }
    private DraftMode _mode;
    private GroupLineVm? _modeLine;
    private string _draftBar = "";

    /// <summary>"Replying to…" or "Editing…" above the box, while it is.</summary>
    public string DraftBarText { get => _draftBar; private set { this.RaiseAndSetIfChanged(ref _draftBar, value); this.RaisePropertyChanged(nameof(HasDraftBar)); } }
    public bool HasDraftBar => DraftBarText.Length > 0;

    public void BeginReply(GroupLineVm line)
    {
        if (!line.CanReply) return;
        _mode = DraftMode.Reply;
        _modeLine = line;
        DraftBarText = string.Format(CultureInfo.CurrentCulture, JabberText.ReplyingTo, line.Sender);
    }

    public void BeginEdit(GroupLineVm line)
    {
        if (!line.CanEdit) return;
        _mode = DraftMode.Edit;
        _modeLine = line;
        DraftBarText = JabberText.EditingMessage;
        Draft = line.Body;
    }

    /// <summary>The Up key in an empty box, as in other clients: corrects this account's latest message.</summary>
    public bool BeginEditLast()
    {
        if (Lines.LastOrDefault(l => l.CanEdit) is not { } last) return false;
        BeginEdit(last);
        return true;
    }

    public void CancelDraftMode()
    {
        if (_mode == DraftMode.Edit) Draft = "";
        _mode = DraftMode.None;
        _modeLine = null;
        DraftBarText = "";
    }

    /// <summary>What the next message sent says beyond its words, which is then forgotten.</summary>
    private XmppSendExtras? TakeExtras(string room)
    {
        var extras = (_mode, _modeLine) switch
        {
            (DraftMode.Edit, { } l) => new XmppSendExtras(ReplaceId: l.MessageId),
            (DraftMode.Reply, { } l) => new XmppSendExtras(ReplyTo: $"{room}/{l.Sender}", ReplyId: l.ReactTarget, ReplyQuote: XmppClient.QuoteFor(l.Sender, l.Body)),
            _ => null,
        };
        _mode = DraftMode.None;
        _modeLine = null;
        DraftBarText = "";
        return extras;
    }

    /// <summary>Takes back one of this account's messages, for everyone. The room's copy back says it is gone.</summary>
    public async Task DeleteOwnAsync(GroupLineVm line)
    {
        if (!line.CanDelete || SelectedRow is not { IsRoom: true } row) return;
        if (row.IsIrc) { await IrcDeleteAsync(row, line); return; }
        if (JabberOf(row) is not { } session) return;

        StatusText = "";
        try { await Task.Run(() => _jabber.DeleteOwnAsync(session, row.Key, true, line.ReactTarget, line.MessageId)); }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; }
    }

    private EmoteSet EmotesOf(int accountId) => _emotes?.For(accountId) ?? EmoteSet.None;

    private EmoteSet _draftEmotes = EmoteSet.None;

    /// <summary>The emotes the message being written can use: the room's server's. None for a Mumble server.</summary>
    public EmoteSet DraftEmotes { get => _draftEmotes; private set => this.RaiseAndSetIfChanged(ref _draftEmotes, value); }

    private static GroupLineVm ToLine(MumbleSession session, MumbleChatLine line) => new(
        0, line.Sender, line.Text, ChatTime.Of(line.When),
        line.IsTree ? string.Format(CultureInfo.CurrentCulture, MumbleText.ChatTreeLabel, line.Channel) : line.Channel,
        NamesMe(session, line))
    {
        Avatar = new AvatarRef(MumbleService.AvatarServiceName, session.Server.Id, line.IsOutgoing ? "me" : "name:" + line.Sender, line.Sender),
    };

    /// <summary>Their name said on this server. Their own messages do not count, whatever is in them.</summary>
    private static bool NamesMe(MumbleSession session, MumbleChatLine line) =>
        !line.IsOutgoing && session.Client?.Me?.Name is { Length: > 0 } me && JabberService.ContainsWord(line.Text, me);

    /// <summary>Clears what is open, if it is really being looked at.</summary>
    private async Task MarkOpenReadAsync()
    {
        if (!IsBeingLookedAt || SelectedRow is not { } row) return;

        if (row.IsMumble)
        {
            if (_mumbleUnread.Remove(row.ServerId)) QueueRefresh();
        }
        else if (row is { IsIrc: true, IsRoom: true } && _irc is { } irc) await Task.Run(() => irc.MarkReadAsync(row.ServerId, row.Key));
        else if (row.IsRoom) await Task.Run(() => _jabber.MarkReadAsync(row.ServerId, row.Key));
    }

    private void OnRoomMessage(JabberStoredMessage message)
    {
        if (!message.IsRoom || SelectedRow is not { IsRoom: true } row || row.ServerId != message.AccountId || row.Key != message.Conversation) return;
        if (Lines.Any(l => l.Id == message.Id)) return;

        Add(ToLine(message));
        _ = MarkOpenReadAsync();
    }

    private void OnMumbleChat(MumbleSession session, MumbleChatLine line)
    {
        var open = SelectedRow is { IsMumble: true } row && row.ServerId == session.Server.Id;
        if (open) Add(ToLine(session, line));

        var mention = NamesMe(session, line);
        if (line.IsOutgoing || open && IsBeingLookedAt) return;

        var (count, mentions) = _mumbleUnread.GetValueOrDefault(session.Server.Id);
        _mumbleUnread[session.Server.Id] = (count + 1, mentions + (mention ? 1 : 0));
        QueueRefresh();
        if (mention) Ring();
    }

    private void Add(GroupLineVm line)
    {
        Lines.Add(line);
        if (line.IsOutgoing) MarkLastEditable();
        if (Lines.Count > MostShown)
        {
            Lines.RemoveAt(0);
            HasEarlier = SelectedRow is { IsRoom: true };
        }
        ScrollToEndRequested?.Invoke();
    }

    // ── For the Group Chat control on panels ──────────────────────────────────
    //
    // A panel shows one room of its own, apart from this tool's selection, so changing it there
    // doesn't change what the tool shows. These are this tool's own ways of reading and writing.

    /// <summary>The rooms and Mumble servers a panel can show, by <see cref="GroupChatRowVm.Identity"/>, named with their server.</summary>
    public IReadOnlyList<(string Id, string Name)> PanelRooms()
    {
        var list = new List<(string, string)>();
        var server = "";
        foreach (var row in Rows)
        {
            if (row.Kind is GroupRowKind.JabberServer or GroupRowKind.IrcServer) { server = row.ShownText; continue; }
            if (row.IsRoom) list.Add((row.Identity, $"{row.ShownText} · {server}"));
            else if (row.IsMumble) list.Add((row.Identity, $"{row.ShownText} · Mumble"));
        }
        return list;
    }

    public GroupChatRowVm? RowOf(string id) => Rows.FirstOrDefault(r => r.Identity == id);

    /// <summary>A room's messages, or a Mumble server's chat, the latest page of them.</summary>
    public async Task<(IReadOnlyList<GroupLineVm> Lines, bool HasEarlier)> PanelLinesAsync(GroupChatRowVm row)
    {
        if (row.IsMumble)
            return (MumbleOf(row) is { } session ? [.. session.Chat.Select(l => ToLine(session, l))] : [], false);
        if (row.IsIrc) return await IrcPanelLinesAsync(row);
        var page = await Task.Run(() => _jabber.LoadConversationAsync(row.ServerId, row.Key, limit: PageSize));
        return ([.. page.Select(ToLine)], page.Count == PageSize);
    }

    /// <summary>Raised on the UI thread with a new line and the room or server it's in (its Identity).</summary>
    public event Action<string, GroupLineVm>? PanelLineArrived;

    private void RaisePanelLine(JabberStoredMessage m)
    {
        if (m.IsRoom) PanelLineArrived?.Invoke(new GroupChatRowVm { Kind = GroupRowKind.Room, ServerId = m.AccountId, Key = m.Conversation }.Identity, ToLine(m));
    }

    private void RaisePanelLine(MumbleSession session, MumbleChatLine line) =>
        PanelLineArrived?.Invoke(new GroupChatRowVm { Kind = GroupRowKind.MumbleServer, ServerId = session.Server.Id }.Identity, ToLine(session, line));

    /// <summary>Sends to a room or a Mumble server's channel. Throws as the tool's own send does.</summary>
    public async Task PanelSendAsync(GroupChatRowVm row, string text)
    {
        if (MumbleOf(row) is { } mumble) await Task.Run(() => _mumble.SendTextAsync(mumble, text, null, false));
        else if (IrcOf(row) is { } irc) await IrcSendAsync(irc, row, text, "");
        else if (JabberOf(row) is { } jabber) await Task.Run(() => _jabber.SendAsync(jabber, row.Key, true, text));
        else throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, MessagesText.CannotSendNotConnected,
            row.IsMumble ? row.ShownText : PanelRooms().FirstOrDefault(r => r.Id == row.Identity).Name ?? row.ShownText));
    }

    /// <summary>Marks a room read, as looking at it in the tool does.</summary>
    public async Task PanelMarkReadAsync(GroupChatRowVm row)
    {
        if (row.IsMumble) { if (_mumbleUnread.Remove(row.ServerId)) QueueRefresh(); }
        else if (row is { IsIrc: true, IsRoom: true } && _irc is { } irc) await Task.Run(() => irc.MarkReadAsync(row.ServerId, row.Key));
        else if (row.IsRoom) await Task.Run(() => _jabber.MarkReadAsync(row.ServerId, row.Key));
    }

    private async Task LoadEarlierAsync()
    {
        if (SelectedRow is not { IsRoom: true } row || Lines.Count == 0) return;
        if (row.IsIrc) { await IrcLoadEarlierAsync(row); return; }

        var page = await Task.Run(() => _jabber.LoadConversationAsync(row.ServerId, row.Key, Lines[0].Id, PageSize));
        for (var i = page.Count - 1; i >= 0; i--) Lines.Insert(0, ToLine(page[i]));
        HasEarlier = page.Count == PageSize;
        await LoadReactionsAsync();
    }

    /// <summary>The reactions to every message showing, as they stand now.</summary>
    private async Task LoadReactionsAsync()
    {
        if (SelectedRow is { IsIrc: true, IsRoom: true } ircRow) { await IrcLoadReactionsAsync(ircRow); return; }
        if (SelectedRow is not { IsRoom: true } row || JabberOf(row) is not { } session) return;

        var lines = Lines.Where(l => l.ReactTarget.Length > 0).ToList();
        // Who this account is there, as its reactions were kept: by the room's lasting id for it where it gives one.
        var me = session.Locked(s => s.RoomMap.GetValueOrDefault(row.Key) is { } r ? (r.MyOccupantId.Length > 0 ? r.MyOccupantId : r.Nick) : "");
        var counts = await Task.Run(() => _jabber.ReactionsAsync(row.ServerId, row.Key, [.. lines.Select(l => l.ReactTarget)], me));
        if (!ReferenceEquals(SelectedRow, row)) return;

        foreach (var line in lines) line.SetReactions(counts.GetValueOrDefault(line.ReactTarget));
    }

    /// <summary>Adds an emoji to this person's reactions to a message, or takes it back if it is theirs already.</summary>
    public async Task ToggleReactionAsync(GroupLineVm line, string emoji)
    {
        if (line.CanReact && SelectedRow is { IsIrc: true, IsRoom: true } ircRow) { await IrcReactAsync(ircRow, line, emoji); return; }
        if (!line.CanReact || SelectedRow is not { IsRoom: true } row || JabberOf(row) is not { } session) return;

        var mine = line.Reactions.Where(c => c.Mine).Select(c => c.Emoji).ToList();
        if (!mine.Remove(emoji)) mine.Add(emoji);

        StatusText = "";
        try { await Task.Run(() => _jabber.SendReactionsAsync(session, row.Key, true, line.ReactTarget, mine)); }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; }
    }

    private async Task SendAsync()
    {
        if (SelectedRow is not { } row) return;

        var text = Draft;
        var extras = TakeExtras(row.Key);
        Draft = "";
        StatusText = "";
        try
        {
            if (MumbleOf(row) is { } mumble)
            {
                var tree = IncludeSubchannels;
                await Task.Run(() => _mumble.SendTextAsync(mumble, text, null, tree));

                // ⚠️ Not left on: the next message should not reach a whole tree of channels because this one did.
                IncludeSubchannels = false;
            }
            else if (IrcOf(row) is { } irc) await IrcSendAsync(irc, row, text, extras?.ReplyId ?? "");
            else if (JabberOf(row) is { } jabber) await Task.Run(() => _jabber.SendAsync(jabber, row.Key, true, text, default, extras));
        }
        catch
        {
            if (ReferenceEquals(SelectedRow, row)) Draft = text;   // nothing typed is lost to a dropped connection
            else _drafts[row.Identity] = text;
            throw;
        }
    }

    /// <summary>Sends a file the person chose to the room open.</summary>
    public async Task SendFileAsync(Stream content, string fileName, long size)
    {
        if (SelectedRow is { } ircRow && IrcOf(ircRow) is { } irc)
        {
            await IrcSendFileAsync(irc, ircRow, content, fileName, size);
            return;
        }
        if (SelectedRow is not { IsRoom: true } row || JabberOf(row) is not { } session) return;

        if (session.UploadService is { MaxBytes: > 0 } service && size > service.MaxBytes)
        {
            StatusText = string.Format(CultureInfo.CurrentCulture, JabberText.FileTooBig, fileName, JabberViewModel.SizeText(size), JabberViewModel.SizeText(service.MaxBytes));
            return;
        }

        StatusText = "";
        SendingFile = string.Format(CultureInfo.CurrentCulture, JabberText.SendingFile, fileName);
        try
        {
            await Task.Run(() => _jabber.SendFileAsync(session, row.Key, true, content, fileName, size, JabberViewModel.ContentTypeOf(fileName)));
        }
        catch (Exception e) when (e is XmppException or HttpRequestException or IOException or TaskCanceledException)
        {
            StatusText = string.Format(CultureInfo.CurrentCulture, JabberText.SendFileFailed, fileName, e.Message);
        }
        finally { SendingFile = ""; }
    }

    /// <summary>Opens the first thing waiting to be read, for the alert: where they were named first.</summary>
    public async Task ShowUnreadAsync()
    {
        await RefreshAsync();

        var next = Rows.FirstOrDefault(r => r.HasMentions) ?? Rows.FirstOrDefault(r => r.HasUnread);
        if (next is not null) SelectedRow = next;
    }

    private void OnRoomAlert(JabberStoredMessage message)
    {
        // A direct message is the Messages tool's to sound.
        if (!message.IsRoom) return;

        // No sound for what is already on screen and being read.
        if (IsBeingLookedAt && SelectedRow is { IsRoom: true } open && open.ServerId == message.AccountId && open.Key == message.Conversation) return;

        Ring();
    }

    private void Ring()
    {
        if (SoundOn && !_mute.Muted) _ = PlaySoundAsync();
    }

    private async Task PlaySoundAsync()
    {
        try { await _sound.PlayAsync(AlarmSoundService.DefaultKey); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { StatusText = e.Message; }
    }

    private bool _soundOn;
    public bool SoundOn
    {
        get => _soundOn;
        set
        {
            this.RaiseAndSetIfChanged(ref _soundOn, value);
            _ = _prefs.SetBoolAsync(SoundPref, value);
        }
    }

    // ── Details of what is selected ───────────────────────────────────────────

    public IReadOnlyList<JabberAlertChoice> AlertChoices { get; }

    private JabberAlertChoice? _chosenAlert;
    public JabberAlertChoice? ChosenAlert
    {
        get => _chosenAlert;
        set
        {
            this.RaiseAndSetIfChanged(ref _chosenAlert, value);
            if (value is not null && !_loadingDetail && SelectedRow is { IsRoom: true } ircRow && IrcOf(ircRow) is { } irc)
                _ = _irc!.SetAlertModeAsync(irc, ircRow.Key, value.Mode);
            else if (value is not null && !_loadingDetail && SelectedRow is { IsRoom: true } row && JabberOf(row) is { } session)
                _ = _jabber.SetRoomAlertAsync(session, row.Key, value.Mode);
        }
    }

    public IReadOnlyList<JabberRoomTypeChoice> RoomTypeChoices { get; }

    private JabberRoomTypeChoice? _chosenRoomType;

    /// <summary>What the room open is for.</summary>
    public JabberRoomTypeChoice? ChosenRoomType
    {
        get => _chosenRoomType;
        set
        {
            this.RaiseAndSetIfChanged(ref _chosenRoomType, value);
            if (value is not null && !_loadingDetail && SelectedRow is { IsRoom: true } ircRow && IrcOf(ircRow) is { } irc)
                _ = _irc!.SetRoomTypeAsync(irc, ircRow.Key, value.Type);
            else if (value is not null && !_loadingDetail && SelectedRow is { IsRoom: true } row && JabberOf(row) is { } session)
                _ = _jabber.SetRoomTypeAsync(session, row.Key, value.Type);
        }
    }

    private void RefreshDetail()
    {
        this.RaisePropertyChanged(nameof(ShowNothing));
        CanAddRooms = _jabber.Sessions.Any(s => s.State == JabberState.Connected) || IrcConnected().Any();

        if (SelectedRow is not { } row) return;

        _loadingDetail = true;
        try
        {
            Title = row.ShownText;
            ArchiveText = "";
            RoomProblem = "";

            if (MumbleOf(row) is { } mumble) ShowMumble(mumble);
            else if (IrcOf(row) is { } irc) ShowIrc(row, irc);
            else if (JabberOf(row) is { } session)
            {
                ServerState = JabberViewModel.StateText(session);
                CanConnect = session.State is JabberState.Offline or JabberState.Failed;

                if (row.Kind == GroupRowKind.JabberServer) Subtitle = session.Account.Address;
                else if (row.Kind == GroupRowKind.Invite) Subtitle = row.ShownDetail;
                else ShowRoom(row, session);
            }
        }
        finally { _loadingDetail = false; }
    }

    private void ShowMumble(MumbleSession session)
    {
        var client = session is { State: MumbleState.Connected } ? session.Client : null;
        var channel = client?.Me is { } me ? client.ChannelOf(me.ChannelId)?.Name ?? "" : "";

        ServerState = MumbleViewModel.StateText(session);
        CanConnect = session.State is MumbleState.Offline or MumbleState.Failed;
        Subtitle = ServerState;
        ArchiveText = GroupChatText.MumbleNote;
        CanSend = client is not null;
        CanAttach = false;
        CannotSend = client is null ? string.Format(CultureInfo.CurrentCulture, MessagesText.CannotSendNotConnected, NameOf(session)) : "";
        DraftHint = string.Format(CultureInfo.CurrentCulture, MumbleText.MessageHint, channel);
    }

    private void ShowRoom(GroupChatRowVm row, JabberSession session)
    {
        var state = session.Locked(s => s.RoomMap.GetValueOrDefault(row.Key));
        var connected = session.State == JabberState.Connected;

        ChosenRoomType = RoomTypeChoices.FirstOrDefault(c => c.Type == row.ShownRoomType);
        CanSend = connected && state is { Joined: true };
        CanAttach = CanSend && session.UploadService is not null;
        DraftHint = JabberText.MessageHint;
        CannotSend = !connected ? string.Format(CultureInfo.CurrentCulture, MessagesText.CannotSendNotConnected, NameOf(session)) : "";
        Subtitle = state is { Subject.Length: > 0 } ? state.Subject : row.Key;

        Occupants.Clear();
        if (state is null) return;

        ChosenAlert = AlertChoices.FirstOrDefault(c => c.Mode == state.Room.AlertMode);
        if (connected) ArchiveText = JabberViewModel.RoomArchiveLine(state.HasArchive, state.ArchiveRefused);
        if (state.Failure is { } failure) RoomProblem = JabberViewModel.RoomFailureText(failure, state.FailureText);

        var people = session.Locked(_ => state.Occupants.Values.ToList());
        foreach (var o in people.OrderBy(o => RoleRank(o.Role)).ThenBy(o => o.Nick, StringComparer.CurrentCultureIgnoreCase))
            Occupants.Add(new JabberOccupantVm(o.Nick, JabberViewModel.ShowDot(o.Show), o.Role)
            {
                Person = o,
                Avatar = new AvatarRef(JabberService.AvatarServiceName, session.Account.Id, o.Self ? "me" : $"{row.Key}/{o.Nick}", o.Nick),
            });
    }

    // ── Running the room: what the right-click menus do ───────────────────────

    /// <summary>Opens a conversation in the Messages tool: account, address and name.</summary>
    public Action<int, string, string>? OpenDirect { get; set; }

    private (JabberSession Session, JabberRoomState State)? OpenRoom()
    {
        if (SelectedRow is not { IsRoom: true } row || JabberOf(row) is not { } session) return null;
        return session.Locked(s => s.RoomMap.GetValueOrDefault(row.Key)) is { } state ? (session, state) : null;
    }

    /// <summary>The person themselves in the room open, as the room sees them.</summary>
    private XmppOccupant? Me() => OpenRoom() is (JabberSession session, JabberRoomState state)
        ? session.Locked(_ => state.Occupants.Values.FirstOrDefault(o => o.Self) ?? state.Occupants.GetValueOrDefault(state.Nick))
        : null;

    public JabberPersonRights RightsOver(JabberOccupantVm person) =>
        person.Person is { } them ? JabberRoomRights.Over(Me(), them) : JabberPersonRights.None;

    public JabberRoomRightsSet RoomRights() =>
        JabberRoomRights.OfRoom(Me(), OpenRoom() is (_, JabberRoomState state) ? _roomDetails.GetValueOrDefault(state.Room.RoomJid) : null);

    /// <summary>What each room opened says of itself (members-only, and whether members may invite), asked once per room.</summary>
    private readonly Dictionary<string, XmppRoomDetails> _roomDetails = new(StringComparer.Ordinal);

    /// <summary>Asks a room about itself, for what its menu offers. Not waited on: the menu offers what it knows until then.</summary>
    private async Task LearnRoomAsync(JabberSession session, string room)
    {
        if (_roomDetails.ContainsKey(room)) return;
        try
        {
            if (await Task.Run(() => _jabber.RoomDetailsAsync(session, room)) is { } details) _roomDetails[room] = details;
        }
        catch (Exception e) when (e is not OperationCanceledException) { /* the menu offers what it would have, and the room refuses if it must */ }
    }

    /// <summary>The room open, as the menus and their questions name it.</summary>
    public string RoomName => Title;

    /// <summary>
    /// Writes to someone in the room in the Messages tool: at their own address where the room shows it, so it is the
    /// same conversation as anywhere else, or through the room where it hides it.
    /// </summary>
    public void MessageOccupant(JabberOccupantVm person)
    {
        if (SelectedRow is { IsIrc: true } && person.IrcMember is { } member && !person.IsSelf) { OpenIrcDirect?.Invoke(SelectedRow.ServerId, member.Nick); return; }
        if (SelectedRow is not { IsRoom: true } row || person.IsSelf) return;
        var real = XmppAddress.Parse(person.Person?.RealJid).Bare;
        OpenDirect?.Invoke(row.ServerId, real.Length > 0 ? real : $"{row.Key}/{person.Nick}", person.Nick);
    }

    /// <summary>The addresses of the people this server knows, to offer when inviting.</summary>
    public IReadOnlyList<string> KnownAddresses() =>
        OpenRoom() is (JabberSession session, _) ? [.. session.Contacts.Select(c => c.Jid).Order(StringComparer.OrdinalIgnoreCase)] : [];

    private async Task AdminAsync(Func<JabberSession, string, Task> act)
    {
        StatusText = "";
        if (OpenRoom() is not (JabberSession session, JabberRoomState state)) return;
        try { await Task.Run(() => act(session, state.Room.RoomJid)); }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; }
    }

    public Task SetRoleAsync(JabberOccupantVm person, string role, string reason = "") =>
        AdminAsync((session, room) => _jabber.SetRoomRoleAsync(session, room, person.Nick, role, reason));

    public Task SetAffiliationAsync(JabberOccupantVm person, string affiliation, string reason = "") =>
        AdminAsync((session, room) => _jabber.SetRoomAffiliationAsync(session, room, person.Person?.RealJid ?? "", affiliation, reason));

    public Task SetSubjectAsync(string subject) =>
        AdminAsync((session, room) => _jabber.SetRoomSubjectAsync(session, room, subject));

    public Task InviteAsync(string address, string reason)
    {
        var jid = XmppAddress.Parse(address);
        if (jid.Local.Length == 0 || jid.Domain.Length == 0) { StatusText = JabberText.AddressInvalid; return Task.CompletedTask; }
        return AdminAsync((session, room) => _jabber.InviteToRoomAsync(session, room, jid.Bare, reason));
    }

    /// <summary>Who the room has banned. Empty, with what went wrong said, when it will not say.</summary>
    public async Task<List<XmppBan>> BansAsync()
    {
        StatusText = "";
        if (OpenRoom() is not (JabberSession session, JabberRoomState state)) return [];
        try { return await Task.Run(() => _jabber.RoomBansAsync(session, state.Room.RoomJid)); }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; return []; }
    }

    public Task LiftBanAsync(string jid) =>
        AdminAsync((session, room) => _jabber.SetRoomAffiliationAsync(session, room, jid, "none", ""));

    public void SetStatus(string text) => StatusText = text;

    private static int RoleRank(string role) => role switch { "moderator" => 0, "participant" => 1, _ => 2 };

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> SendCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadEarlierCommand { get; }
    public ReactiveCommand<Unit, Unit> LeaveRoomCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> AddRoomsCommand { get; }
    public ReactiveCommand<Unit, Unit> JoinAgainCommand { get; }
    public ReactiveCommand<Unit, Unit> JoinRoomCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAddRoomsCommand { get; }
    public ReactiveCommand<Unit, Unit> AcceptInviteCommand { get; }
    public ReactiveCommand<Unit, Unit> DeclineInviteCommand { get; }

    private void Connect()
    {
        if (MumbleOf(SelectedRow) is { } mumble) _mumble.Connect(mumble);
        else if (IrcOf(SelectedRow) is { } irc) _irc!.Connect(irc);
        else if (JabberOf(SelectedRow) is { } jabber) _jabber.Connect(jabber);
    }

    private async Task LeaveRoomAsync()
    {
        if (SelectedRow is { IsIrc: true, IsRoom: true } ircRow && IrcOf(ircRow) is { } irc)
        {
            await _irc!.LeaveChannelAsync(irc, ircRow.Key);
            SelectedRow = null;
            await RefreshAsync();
            return;
        }
        if (SelectedRow is not { IsRoom: true } row || JabberOf(row) is not { } session) return;

        await _jabber.LeaveRoomAsync(session, row.Key);
        SelectedRow = null;
        await RefreshAsync();
    }

    private async Task AnswerInviteAsync(bool accept)
    {
        if (SelectedRow is { Kind: GroupRowKind.IrcInvite } ircRow && IrcOf(ircRow) is { } irc)
        {
            await Task.Run(() => _irc!.AnswerInviteAsync(irc, ircRow.Key, accept));
            await RefreshAsync();
            if (accept) SelectedRow = Rows.FirstOrDefault(r => r.Kind == GroupRowKind.IrcChannel && r.ServerId == ircRow.ServerId && r.Key == irc.Fold(ircRow.Key));
            return;
        }
        if (SelectedRow is not { Kind: GroupRowKind.Invite } row || JabberOf(row) is not { } session) return;

        await Task.Run(() => _jabber.AnswerInviteAsync(session, row.Key, accept));
        await RefreshAsync();
        if (accept) SelectedRow = Rows.FirstOrDefault(r => r.IsRoom && r.ServerId == row.ServerId && r.Key == XmppAddress.Parse(row.Key).Bare);
    }

    // ── Adding rooms ──────────────────────────────────────────────────────────

    private bool _isAddingRooms;
    public bool IsAddingRooms
    {
        get => _isAddingRooms;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isAddingRooms, value);
            if (!value) _browseStamp++;
            RaisePanes();
        }
    }

    public bool ShowAddRooms => IsAddingRooms;

    private string _addingTo = "";

    /// <summary>Which server rooms are being added on.</summary>
    public string AddingTo { get => _addingTo; private set => this.RaiseAndSetIfChanged(ref _addingTo, value); }

    private bool _isLoadingRooms;
    public bool IsLoadingRooms { get => _isLoadingRooms; private set => this.RaiseAndSetIfChanged(ref _isLoadingRooms, value); }

    private readonly List<GroupRoomChoiceVm> _allChoices = [];
    private string _lastJoined = "";
    private GroupChatRowVm? _rowBeforeAdding;
    private int _browseStamp;
    private int _addingAccount;

    private ObservableCollection<GroupRoomChoiceVm> _roomChoices = [];

    /// <summary>⚠️ Replaced whole rather than refilled: a server can list thousands, and each one added would be a change for the list to take in.</summary>
    public ObservableCollection<GroupRoomChoiceVm> RoomChoices { get => _roomChoices; private set => this.RaiseAndSetIfChanged(ref _roomChoices, value); }

    public IReadOnlyList<GroupRoomSortChoice> RoomSortChoices { get; }

    private GroupRoomSortChoice _roomSort;

    /// <summary>How the rooms are listed.</summary>
    public GroupRoomSortChoice RoomSort
    {
        get => _roomSort;
        set
        {
            this.RaiseAndSetIfChanged(ref _roomSort, value ?? RoomSortChoices[0]);
            FillChoices();
        }
    }

    private string _roomFilter = "";
    public string RoomFilter
    {
        get => _roomFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _roomFilter, value);
            FillChoices();
        }
    }

    private GroupRoomChoiceVm? _roomChoice;
    public GroupRoomChoiceVm? RoomChoice
    {
        get => _roomChoice;
        set
        {
            this.RaiseAndSetIfChanged(ref _roomChoice, value);

            // Not when only the list was rearranged around it: what was chosen for it stays chosen.
            if (value is null || value.Jid == JoinRoom) return;

            JoinRoom = value.Jid;
            JoinRoomType = TypeOfKept(value.Jid);
        }
    }

    private string _joinRoom = "", _joinNick = "", _joinPassword = "";
    public string JoinRoom { get => _joinRoom; set => this.RaiseAndSetIfChanged(ref _joinRoom, value); }
    public string JoinNick { get => _joinNick; set => this.RaiseAndSetIfChanged(ref _joinNick, value); }
    public string JoinPassword { get => _joinPassword; set => this.RaiseAndSetIfChanged(ref _joinPassword, value); }

    private JabberRoomTypeChoice _joinRoomType;

    /// <summary>What the room being added is for. The usual type until someone says otherwise.</summary>
    public JabberRoomTypeChoice JoinRoomType { get => _joinRoomType; set => this.RaiseAndSetIfChanged(ref _joinRoomType, value ?? RoomTypeChoices[0]); }

    /// <summary>The type of a room already kept, so joining it again does not quietly change it. The usual type for any other.</summary>
    private JabberRoomTypeChoice TypeOfKept(string room)
    {
        if (_addingIrc) return IrcTypeOfKept(room);
        var type = _jabber.SessionOf(_addingAccount)?.Locked(s => s.RoomMap.GetValueOrDefault(room)?.Room.RoomType) ?? JabberRoomType.General;
        return RoomTypeChoices.FirstOrDefault(c => c.Type == type) ?? RoomTypeChoices[0];
    }

    private void FillChoices()
    {
        var words = RoomFilter.Trim();
        var shown = GroupRoomChoiceVm.Sorted(_allChoices.Where(c => words.Length == 0 || c.Matches(words)), RoomSort.Sort).ToList();
        if (RoomChoices.SequenceEqual(shown)) return;

        var chosen = RoomChoice;
        RoomChoices = new ObservableCollection<GroupRoomChoiceVm>(shown);
        if (chosen is not null && shown.Contains(chosen)) RoomChoice = chosen;
    }

    /// <summary>Keeps the list of rooms honest while it stays open: which are joined, and why the last one tried would not have this person.</summary>
    private void SyncAddingRooms()
    {
        if (IsAddingRooms && _addingIrc) { IrcSyncAddingRooms(); return; }
        if (!IsAddingRooms || _jabber.SessionOf(_addingAccount) is not { } session) return;

        var joined = session.Locked(s => s.RoomMap.Values.Where(r => r.Joined).Select(r => r.Room.RoomJid).ToHashSet(StringComparer.Ordinal));
        foreach (var choice in _allChoices) choice.IsJoined = joined.Contains(choice.Jid);

        if (_lastJoined.Length > 0 && session.Locked(s => s.RoomMap.GetValueOrDefault(_lastJoined)) is { Failure: { } failure } refused)
        {
            StatusText = $"{JabberViewModel.DisplayRoom(refused.Room)}: {JabberViewModel.RoomFailureText(failure, refused.FailureText)}";
            _lastJoined = "";
        }
    }

    /// <summary>Lists what the server open offers, or the first one connected when something else is open.</summary>
    private async Task AddRoomsAsync()
    {
        // IRC when an IRC server or channel is open, or when no Jabber server is connected to add rooms on.
        if (IrcOf(SelectedRow) is { State: EveConsole.Services.Irc.IrcState.Connected } ircHere) { await BrowseIrcAsync(ircHere); return; }
        if (SelectedRow is not { IsJabber: true } && !_jabber.Sessions.Any(s => s.State == JabberState.Connected) && IrcConnected().FirstOrDefault() is { } anyIrc)
        {
            await BrowseIrcAsync(anyIrc);
            return;
        }

        var session = JabberOf(SelectedRow) is { State: JabberState.Connected } here
            ? here
            : _jabber.Sessions.FirstOrDefault(s => s.State == JabberState.Connected);
        if (session is null) return;

        await BrowseAsync(session);
    }

    /// <summary>Brings a room that would not take this person back up with its name filled in, to fix what stopped it.</summary>
    private async Task JoinAgainAsync()
    {
        if (SelectedRow is { IsIrc: true, IsRoom: true } ircRow && IrcOf(ircRow) is { } irc)
        {
            StatusText = "";
            await Task.Run(() => _irc!.RejoinAsync(irc, ircRow.Key));
            return;
        }
        if (SelectedRow is not { IsRoom: true } row || JabberOf(row) is not { } session) return;

        var key = row.Key;
        await BrowseAsync(session);
        JoinRoom = key;
        JoinRoomType = TypeOfKept(key);
    }

    private async Task BrowseAsync(JabberSession session)
    {
        var stamp = ++_browseStamp;
        IsAddingIrc = false;

        _addingAccount = session.Account.Id;
        AddingTo = string.Format(CultureInfo.CurrentCulture, GroupChatText.AddingTo, NameOf(session));
        JoinRoom = JoinNick = JoinPassword = "";
        JoinRoomType = RoomTypeChoices[0];
        _allChoices.Clear();
        RoomChoices = [];
        _lastJoined = "";

        // ⚠️ Nothing stays selected on the left while this is open: clicking what is already selected changes nothing,
        // so that one row could not be gone back to. Done brings it back.
        _rowBeforeAdding = SelectedRow;
        _selectedRow = null;
        this.RaisePropertyChanged(nameof(SelectedRow));
        _roomFilter = "";
        this.RaisePropertyChanged(nameof(RoomFilter));
        StatusText = "";

        _isAddingRooms = true;
        this.RaisePropertyChanged(nameof(IsAddingRooms));
        RaisePanes();
        IsLoadingRooms = true;
        try
        {
            var kept = session.Locked(s => s.RoomMap.Values.Where(r => r.Joined).Select(r => r.Room.RoomJid).ToHashSet(StringComparer.Ordinal));

            foreach (var service in session.ConferenceServices)
                foreach (var room in await Task.Run(() => _jabber.BrowseRoomsAsync(session, service)))
                    _allChoices.Add(new GroupRoomChoiceVm(room, kept.Contains(room.Jid)));

            _allChoices.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            FillChoices();
            if (_allChoices.Count == 0) StatusText = JabberText.NoRoomsListed;

            await AskRoomsAsync(session, stamp);
        }
        finally
        {
            if (stamp == _browseStamp) IsLoadingRooms = false;
        }
    }

    /// <summary>Fills in what each room says about itself, as the answers come. ⚠️ A few at a time: this is a question per room, to one server.</summary>
    private async Task AskRoomsAsync(JabberSession session, int stamp)
    {
        using var gate = new SemaphoreSlim(RoomsAskedAtOnce);
        var sortedAt = Environment.TickCount64;

        await Task.WhenAll(_allChoices.ToList().Select(async choice =>
        {
            await gate.WaitAsync();
            try
            {
                if (stamp != _browseStamp) return;

                var details = await Task.Run(() => _jabber.RoomDetailsAsync(session, choice.Jid));
                if (details is null || stamp != _browseStamp) return;

                choice.Apply(details);

                // Every so often, not with each answer: the list should settle, not jump about under the pointer.
                if (RoomSort.Sort == GroupRoomChoiceVm.SortByPeople && Environment.TickCount64 - sortedAt > ResortEveryMs)
                {
                    sortedAt = Environment.TickCount64;
                    FillChoices();
                }
            }
            finally { gate.Release(); }
        }));

        if (stamp == _browseStamp) FillChoices();   // a filter may match what only just arrived
    }

    /// <summary>Back to what was open before, if it is still there.</summary>
    private void FinishAddingRooms()
    {
        IsAddingRooms = false;
        if (SelectedRow is null && _rowBeforeAdding is { } before && Rows.Contains(before)) SelectedRow = before;
        _rowBeforeAdding = null;
    }

    private async Task JoinRoomAsync()
    {
        if (_addingIrc) { await IrcJoinRoomAsync(); return; }
        if (_jabber.SessionOf(_addingAccount) is not { } session) return;

        // A bare name joins on the server's own conference service, as nobody should have to know its name.
        var room = JoinRoom.Trim();
        if (room.Length == 0) return;
        if (!room.Contains('@') && session.ConferenceServices.Count > 0) room = $"{room}@{session.ConferenceServices[0]}";

        var bare = XmppAddress.Parse(room).Bare;
        var choice = _allChoices.FirstOrDefault(c => c.Jid == bare);

        StatusText = "";

        // Already in it: nothing to do but make it what the dropdown now says it is for.
        var type = JoinRoomType.Type;
        if (session.Locked(s => s.RoomMap.TryGetValue(bare, out var state) && state.Joined))
            await Task.Run(() => _jabber.SetRoomTypeAsync(session, bare, type));
        else
            await Task.Run(() => _jabber.JoinRoomAsync(session, room, choice?.Name ?? "", JoinNick.Trim(), JoinPassword, roomType: type));

        // ⚠️ This stays open to join more: on a large server the list takes a while to come back. The nickname stays
        // too, as someone going by another name goes by it in the next room as well.
        _lastJoined = bare;
        JoinPassword = "";
        JoinRoom = "";
        RoomChoice = null;
        JoinRoomType = RoomTypeChoices[0];
        await RefreshAsync();
    }
}
