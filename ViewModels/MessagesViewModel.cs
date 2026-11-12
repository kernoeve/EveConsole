using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Text.Json;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.Services.Jabber;
using EveConsole.Services.Messages;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>Someone found online, to start a conversation with.</summary>
public sealed class MessagesPersonVm(DmPerson person)
{
    public DmPerson Person => person;
    public string Name => person.Name;
    public AvatarRef Avatar { get; } = new(person.Key.Source, person.Key.ServerId, person.Key.Peer, person.Name);
    public string Where => $"{person.Service} · {person.Server}";
    public bool DotGreen => person.Presence == DmPresence.Online;
    public bool DotAmber => person.Presence == DmPresence.Away;
}

/// <summary>A line in the list of every saved conversation.</summary>
public sealed class MessagesConversationVm(DmSummary summary)
{
    public DmSummary Summary => summary;
    public string Name => summary.Name;
    public string Where => $"{summary.Service} · {summary.Server}";
    public string LastText => summary.LastText;
    public string LastAt => ChatTime.Of(summary.LastAt);
    public int Unread => summary.Unread;
    public bool HasUnread => summary.Unread > 0;
    public string UnreadText => summary.Unread.ToString("N0", CultureInfo.CurrentCulture);
    public bool DotGreen => summary.Presence == DmPresence.Online;
    public bool DotAmber => summary.Presence == DmPresence.Away;
}

public sealed partial class MessagesLineVm(DmLine line, string shownSender, EmoteSet emotes, AvatarRef? avatar = null, string replyText = "", bool editable = false,
    bool deletable = false) : ReactiveObject
{
    public string MessageId => line.Target;

    /// <summary>What it answers, shown above it.</summary>
    public string ReplyText => replyText;
    public bool HasReply => replyText.Length > 0;

    private bool _canEdit, _showSeen;

    /// <summary>This account's latest message here, the only one the protocol lets it correct.</summary>
    public bool CanEdit { get => _canEdit; set => this.RaiseAndSetIfChanged(ref _canEdit, value); }
    public bool CanDelete => (editable || deletable) && IsOutgoing && !IsDeleted && MessageId.Length > 0;
    public bool CanReply => editable && CanReact;

    /// <summary>The latest of this account's that the person has seen, which says so.</summary>
    public bool ShowSeen { get => _showSeen; set => this.RaiseAndSetIfChanged(ref _showSeen, value); }

    /// <summary>What a reaction to it names. Blank where it cannot be reacted to.</summary>
    public string ReactTarget => line.Target;
    public bool CanReact => ReactTarget.Length > 0 && !IsDeleted;

    public ObservableCollection<ReactionChipVm> Reactions { get; } = [];

    public void SetReactions(IReadOnlyList<JabberReactionCount>? counts)
    {
        Reactions.Clear();
        foreach (var c in counts ?? []) Reactions.Add(new ReactionChipVm(c));
    }

    /// <summary>Whose picture goes beside it.</summary>
    public AvatarRef? Avatar => avatar;

    public DmLine Line       => line;

    /// <summary>The emotes of the server the conversation is on. None for one that is not on Jabber.</summary>
    public EmoteSet Emotes   => IsDeleted ? EmoteSet.None : emotes;
    public long   Id         => line.Id;
    public string Sender     { get; } = shownSender;
    public bool   IsDeleted  => line.Deleted != EveConsole.Models.JabberDeleted.No;
    public string Body       => IsDeleted ? GroupLineVm.DeletedText(line.Deleted, line.DeletedReason) : line.Body;
    public bool   IsOutgoing => line.IsOutgoing;
    public string When       { get; } = ChatTime.Of(line.SentAt);

    /// <summary>The web addresses in the message, to open. Web only: a link in a chat decides nothing else.</summary>
    public IReadOnlyList<string> Links { get; } = line.Deleted != EveConsole.Models.JabberDeleted.No ? [] : [.. UrlPattern().Matches(line.Body).Select(x => x.Value.TrimEnd('.', ',', ')', ';', ':', '!', '?', '\'', '"')).Distinct()];
    public bool HasLinks => Links.Count > 0;

    [System.Text.RegularExpressions.GeneratedRegex(@"https?://[^\s<>""]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex UrlPattern();
}

/// <summary>One open conversation: a tab of the Messages tool.</summary>
public sealed class MessagesTabVm : ReactiveObject
{
    private const int PageSize = 100;
    private const int MostShown = 2000;

    private static readonly TimeSpan TypingRepeat = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TypingForgotten = TimeSpan.FromSeconds(45);

    private readonly IDmSource _source;
    private DateTime _lastTypingSent = DateTime.MinValue;
    private int _theirTypingStamp;

    public MessagesTabVm(IDmSource source, DmSummary summary, EmoteSet? emotes = null)
    {
        _source = source;
        Key = summary.Key;
        Emotes = emotes ?? EmoteSet.None;
        Apply(summary);

        SendCommand = ReactiveCommand.CreateFromTask(SendAsync, this.WhenAnyValue(x => x.Draft, x => x.CanSend, (d, can) => can && !string.IsNullOrWhiteSpace(d)));
        LoadEarlierCommand = ReactiveCommand.CreateFromTask(LoadEarlierAsync);
        AddContactCommand = ReactiveCommand.CreateFromTask(() => _source.AddContactAsync(Key));
        RemoveContactCommand = ReactiveCommand.CreateFromTask(RemoveContactAsync);

        foreach (var command in new[] { SendCommand, LoadEarlierCommand, AddContactCommand, RemoveContactCommand })
            command.ThrownExceptions.Subscribe(e => StatusText = e.Message);
    }

    public DmKey Key { get; }

    /// <summary>The emotes this conversation's messages are read and written with.</summary>
    public EmoteSet Emotes { get; }

    public ReactiveCommand<Unit, Unit> SendCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadEarlierCommand { get; }
    public ReactiveCommand<Unit, Unit> AddContactCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveContactCommand { get; }

    public ObservableCollection<MessagesLineVm> Lines { get; } = [];

    /// <summary>Asks the view to bring the newest message into sight.</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>Set by the window: asks before something that cannot be undone.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    private DmSummary _summary = null!;

    /// <summary>Takes on how the person stands now. Unread is the view model's to set, from what was counted.</summary>
    public void Apply(DmSummary summary)
    {
        _summary = summary;

        // Someone given a saved name goes by it throughout, not only from here on.
        var renamed = _name.Length > 0 && _name != summary.Name;
        Name = summary.Name;
        if (renamed)
            for (var i = 0; i < Lines.Count; i++)
                if (!Lines[i].IsOutgoing) Lines[i] = ToVm(Lines[i].Line);

        Where = $"{summary.Service} · {summary.Server}";
        PresenceText = summary.Presence switch
        {
            DmPresence.Online  => MessagesText.PresenceOnline,
            DmPresence.Away    => MessagesText.PresenceAway,
            DmPresence.Offline => MessagesText.PresenceOffline,
            _                  => "",
        };
        StatusLine = string.Join(" · ", new[] { PresenceText, summary.Status }.Where(t => t.Length > 0));
        DotGreen = summary.Presence == DmPresence.Online;
        DotAmber = summary.Presence == DmPresence.Away;
        CannotSend = summary.CannotSend;
        CanSend = summary.CannotSend.Length == 0;
        Note = summary.Note;
        CanAttach = summary.CanAttach && CanSend;
        CanAddContact = summary is { HasContacts: true, IsContact: false } && CanSend;
        CanRemoveContact = summary is { HasContacts: true, IsContact: true } && CanSend;
    }

    private string _name = "", _where = "", _presenceText = "", _statusLine = "", _cannotSend = "", _note = "", _draft = "", _statusText = "", _typingText = "", _sendingFile = "";
    private bool _dotGreen, _dotAmber, _canSend, _canAttach, _canAddContact, _canRemoveContact, _hasEarlier;
    private int _unread;

    public string Name { get => _name; private set => this.RaiseAndSetIfChanged(ref _name, value); }
    public string Where { get => _where; private set => this.RaiseAndSetIfChanged(ref _where, value); }
    public string PresenceText { get => _presenceText; private set => this.RaiseAndSetIfChanged(ref _presenceText, value); }
    public string StatusLine { get => _statusLine; private set { this.RaiseAndSetIfChanged(ref _statusLine, value); this.RaisePropertyChanged(nameof(HasStatusLine)); } }
    public bool HasStatusLine => StatusLine.Length > 0;
    public string CannotSend { get => _cannotSend; private set { this.RaiseAndSetIfChanged(ref _cannotSend, value); this.RaisePropertyChanged(nameof(HasCannotSend)); } }
    public bool HasCannotSend => CannotSend.Length > 0;
    public string Note { get => _note; private set { this.RaiseAndSetIfChanged(ref _note, value); this.RaisePropertyChanged(nameof(HasNote)); } }
    public bool HasNote => Note.Length > 0;
    public bool DotGreen { get => _dotGreen; private set => this.RaiseAndSetIfChanged(ref _dotGreen, value); }
    public bool DotAmber { get => _dotAmber; private set => this.RaiseAndSetIfChanged(ref _dotAmber, value); }
    public bool CanSend { get => _canSend; private set => this.RaiseAndSetIfChanged(ref _canSend, value); }
    public bool CanAttach { get => _canAttach; private set => this.RaiseAndSetIfChanged(ref _canAttach, value); }
    public bool CanAddContact { get => _canAddContact; private set => this.RaiseAndSetIfChanged(ref _canAddContact, value); }
    public bool CanRemoveContact { get => _canRemoveContact; private set => this.RaiseAndSetIfChanged(ref _canRemoveContact, value); }
    public bool HasEarlier { get => _hasEarlier; private set => this.RaiseAndSetIfChanged(ref _hasEarlier, value); }

    public string StatusText { get => _statusText; set { this.RaiseAndSetIfChanged(ref _statusText, value); this.RaisePropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;

    public int Unread
    {
        get => _unread;
        set
        {
            this.RaiseAndSetIfChanged(ref _unread, value);
            this.RaisePropertyChanged(nameof(HasUnread));
            this.RaisePropertyChanged(nameof(UnreadText));
        }
    }
    public bool HasUnread => Unread > 0;
    public string UnreadText => Unread.ToString("N0", CultureInfo.CurrentCulture);

    public string Draft
    {
        get => _draft;
        set
        {
            if (_draft == value) return;
            this.RaiseAndSetIfChanged(ref _draft, value);
            OnDraftChanged();
        }
    }

    public string TypingText { get => _typingText; private set { this.RaiseAndSetIfChanged(ref _typingText, value); this.RaisePropertyChanged(nameof(IsSomeoneTyping)); } }
    public bool IsSomeoneTyping => TypingText.Length > 0;

    public string SendingFile { get => _sendingFile; private set { this.RaiseAndSetIfChanged(ref _sendingFile, value); this.RaisePropertyChanged(nameof(IsSendingFile)); } }
    public bool IsSendingFile => SendingFile.Length > 0;

    private MessagesLineVm ToVm(DmLine line)
    {
        var sender = line.IsOutgoing ? JabberText.SenderYou : Name;
        var reply = GroupChatViewModel.ReplyTextFor(line.ReplyId, line.ReplyQuote, Lines, l => l.MessageId, l => l.Sender, l => l.Body);
        return new(line, sender, Emotes, new AvatarRef(Key.Source, Key.ServerId, line.IsOutgoing ? "me" : Key.Peer, line.IsOutgoing ? MyName : Name),
            reply, _source.CanEditMessages, _source.CanDeleteIn(Key));
    }

    /// <summary>Which line can be corrected, and which says it was seen: both only ever the latest of their kind.</summary>
    private void MarkLines()
    {
        var editable = _source.CanEditMessages ? Lines.LastOrDefault(l => l.IsOutgoing && !l.IsDeleted && l.MessageId.Length > 0) : null;
        var seen = Lines.LastOrDefault(l => l.IsOutgoing && l.Line.SeenByThem);
        foreach (var line in Lines)
        {
            line.CanEdit = ReferenceEquals(line, editable);
            line.ShowSeen = ReferenceEquals(line, seen);
        }
    }

    private enum DraftMode { None, Reply, Edit }
    private DraftMode _mode;
    private MessagesLineVm? _modeLine;
    private string _draftBar = "";

    public string DraftBarText { get => _draftBar; private set { this.RaiseAndSetIfChanged(ref _draftBar, value); this.RaisePropertyChanged(nameof(HasDraftBar)); } }
    public bool HasDraftBar => DraftBarText.Length > 0;

    public void BeginReply(MessagesLineVm line)
    {
        if (!line.CanReply) return;
        _mode = DraftMode.Reply;
        _modeLine = line;
        DraftBarText = string.Format(CultureInfo.CurrentCulture, JabberText.ReplyingTo, line.Sender);
    }

    public void BeginEdit(MessagesLineVm line)
    {
        if (!line.CanEdit) return;
        _mode = DraftMode.Edit;
        _modeLine = line;
        DraftBarText = JabberText.EditingMessage;
        Draft = line.Body;
    }

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

    private XmppSendExtras? TakeExtras()
    {
        var extras = (_mode, _modeLine) switch
        {
            (DraftMode.Edit, { } l) => new XmppSendExtras(ReplaceId: l.MessageId),
            (DraftMode.Reply, { } l) => new XmppSendExtras(ReplyTo: Key.Peer, ReplyId: l.MessageId,
                ReplyQuote: XmppClient.QuoteFor(l.IsOutgoing ? MyName : Name, l.Body)),
            _ => null,
        };
        _mode = DraftMode.None;
        _modeLine = null;
        DraftBarText = "";
        return extras;
    }

    /// <summary>Takes back one of this account's messages, for everyone.</summary>
    public async Task DeleteOwnAsync(MessagesLineVm line)
    {
        if (!line.CanDelete) return;
        StatusText = "";
        try { await Task.Run(() => _source.DeleteOwnAsync(Key, line.MessageId)); }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; }
    }

    /// <summary>For this account's own picture's initial, where it has none: "You" would give everyone a Y.</summary>
    public string MyName { get; init; } = "";

    public async Task LoadAsync()
    {
        var page = await Task.Run(() => _source.LoadAsync(Key, long.MaxValue, PageSize));
        Lines.Clear();
        foreach (var line in page) Lines.Add(ToVm(line));
        HasEarlier = page.Count == PageSize;
        MarkLines();
        ScrollToEndRequested?.Invoke();
        await LoadReactionsAsync();
    }

    /// <summary>The reactions to every message showing, as they stand now.</summary>
    public async Task LoadReactionsAsync()
    {
        var lines = Lines.Where(l => l.ReactTarget.Length > 0).ToList();
        if (lines.Count == 0) return;

        var counts = await Task.Run(() => _source.ReactionsAsync(Key, [.. lines.Select(l => l.ReactTarget)]));
        foreach (var line in lines) line.SetReactions(counts.GetValueOrDefault(line.ReactTarget));
    }

    /// <summary>Adds an emoji to this person's reactions to a message, or takes it back if it is theirs already.</summary>
    public async Task ToggleReactionAsync(MessagesLineVm line, string emoji)
    {
        if (!line.CanReact) return;

        var mine = line.Reactions.Where(c => c.Mine).Select(c => c.Emoji).ToList();
        if (!mine.Remove(emoji)) mine.Add(emoji);

        StatusText = "";
        try { await Task.Run(() => _source.ReactAsync(Key, line.ReactTarget, mine)); }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; }
    }

    private async Task LoadEarlierAsync()
    {
        if (Lines.Count == 0) return;

        var page = await Task.Run(() => _source.LoadAsync(Key, Lines[0].Id, PageSize));
        for (var i = page.Count - 1; i >= 0; i--) Lines.Insert(0, ToVm(page[i]));
        HasEarlier = page.Count == PageSize;
        await LoadReactionsAsync();
    }

    public void Add(DmLine line)
    {
        if (Lines.Any(l => l.Id == line.Id)) return;

        Lines.Add(ToVm(line));
        MarkLines();
        if (Lines.Count > MostShown)
        {
            Lines.RemoveAt(0);
            HasEarlier = true;
        }
        ScrollToEndRequested?.Invoke();
    }

    private async Task SendAsync()
    {
        var text = Draft;
        var extras = TakeExtras();
        StatusText = "";
        Draft = "";
        StopTyping();
        try { await Task.Run(() => extras is null ? _source.SendAsync(Key, text) : _source.SendAsync(Key, text, extras)); }
        catch { Draft = text; throw; }
    }

    private async Task RemoveContactAsync()
    {
        if (Confirm is not null && !await Confirm(string.Format(CultureInfo.CurrentCulture, JabberText.ConfirmRemoveContact, Name))) return;
        await _source.RemoveContactAsync(Key);
    }

    /// <summary>Sends a file the person chose. Sizes are looked at here, while they can still choose another.</summary>
    public async Task SendFileAsync(Stream content, string fileName, long size)
    {
        if (_summary.MostFileBytes > 0 && size > _summary.MostFileBytes)
        {
            StatusText = string.Format(CultureInfo.CurrentCulture, JabberText.FileTooBig, fileName, JabberViewModel.SizeText(size), JabberViewModel.SizeText(_summary.MostFileBytes));
            return;
        }

        StatusText = "";
        SendingFile = string.Format(CultureInfo.CurrentCulture, JabberText.SendingFile, fileName);
        try { await Task.Run(() => _source.SendFileAsync(Key, content, fileName, size, JabberViewModel.ContentTypeOf(fileName))); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            StatusText = string.Format(CultureInfo.CurrentCulture, JabberText.SendFileFailed, fileName, e.Message);
        }
        finally { SendingFile = ""; }
    }

    private void OnDraftChanged()
    {
        if (string.IsNullOrWhiteSpace(_draft))
        {
            StopTyping();
            return;
        }

        if (DateTime.UtcNow - _lastTypingSent < TypingRepeat) return;

        _lastTypingSent = DateTime.UtcNow;
        Quietly(_source.SetTypingAsync(Key, true));
    }

    private void StopTyping()
    {
        if (_lastTypingSent == DateTime.MinValue) return;

        _lastTypingSent = DateTime.MinValue;
        Quietly(_source.SetTypingAsync(Key, false));
    }

    /// <summary>A courtesy, so a dropped connection here is nothing to report: sending the message itself will say.</summary>
    private static void Quietly(Task send) => _ = send.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    public void TheyAreTyping(bool typing)
    {
        var stamp = ++_theirTypingStamp;
        TypingText = typing ? string.Format(CultureInfo.CurrentCulture, JabberText.Typing, Name) : "";

        // A notice nobody follows up on stops counting: their client may have just gone away.
        if (typing)
            _ = Task.Delay(TypingForgotten).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                if (stamp == _theirTypingStamp) TypingText = "";
            }), TaskScheduler.Default);
    }
}

/// <summary>
/// The Messages tool: direct messages from every service, a tab per conversation, and a list of everything saved.
/// It stays alive with its tab closed, because a message arriving has to be counted and heard either way.
/// </summary>
public sealed class MessagesViewModel : ReactiveObject
{
    public const string SoundPref = "messages.alert_sound";
    public const string OpenTabsPref = "messages.open_tabs";
    public const string SelectedTabPref = "messages.selected_tab";
    public const string ShowListPref = "messages.show_list";

    private readonly DirectMessagesService _service;
    private readonly AppPreferencesService _prefs;
    private readonly AlarmSoundService _sound;
    private readonly AlarmMuteState _mute;
    private readonly EmoteService? _emotes;

    private List<DmSummary> _all = [];
    private int _refreshQueued;

    /// <summary>A Jabber conversation is read with its server's emotes. Nothing else has any.</summary>
    private EmoteSet EmotesOf(DmKey key) =>
        key.Source == JabberDmSource.Name && _emotes is not null ? _emotes.For(key.ServerId) : EmoteSet.None;

    public MessagesViewModel(DirectMessagesService service, AppPreferencesService prefs, AlarmSoundService sound, AlarmMuteState mute, EmoteService? emotes = null)
    {
        _emotes = emotes;
        _service = service;
        _prefs = prefs;
        _sound = sound;
        _mute = mute;

        _soundOn = _prefs.GetBool(SoundPref, true);
        _showList = _prefs.GetBool(ShowListPref, true);

        ToggleListCommand = ReactiveCommand.Create(() => { ShowList = !ShowList; });
        NewConversationCommand = ReactiveCommand.Create(BeginNewConversation);
        CancelNewConversationCommand = ReactiveCommand.Create(() => { IsFinding = false; });

        _service.Changed += QueueRefresh;
        _service.UnreadChanged += QueueRefresh;
        _service.Arrived += (key, line, alert) => Dispatcher.UIThread.Post(() => OnArrived(key, line, alert));
        _service.Edited += key => Dispatcher.UIThread.Post(() => { if (TabOf(key) is { } tab) _ = tab.LoadAsync(); });
        _service.ReactionsChanged += key => Dispatcher.UIThread.Post(() => { if (TabOf(key) is { } tab) _ = tab.LoadReactionsAsync(); });
        _service.Typing += (key, typing) => Dispatcher.UIThread.Post(() => TabOf(key)?.TheyAreTyping(typing));

        QueueRefresh();
    }

    public ReactiveCommand<Unit, Unit> ToggleListCommand { get; }
    public ReactiveCommand<Unit, Unit> NewConversationCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelNewConversationCommand { get; }

    // ── Starting a conversation ───────────────────────────────────────────────

    /// <summary>A very busy server could list thousands, and nobody reads past this before typing more.</summary>
    private const int MostPeopleListed = 100;

    public ObservableCollection<MessagesPersonVm> People { get; } = [];

    private bool _isFinding;

    /// <summary>The search for someone online is showing, in place of the saved list.</summary>
    public bool IsFinding
    {
        get => _isFinding;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFinding, value);
            this.RaisePropertyChanged(nameof(ShowSaved));
            this.RaisePropertyChanged(nameof(ShowNoSaved));
            if (!value) FindWords = "";
        }
    }

    public bool ShowSaved => !IsFinding;
    public bool ShowNoSaved => !IsFinding && HasNoConversations;

    private string _findWords = "";
    public string FindWords
    {
        get => _findWords;
        set
        {
            this.RaiseAndSetIfChanged(ref _findWords, value);
            FillPeople();
        }
    }

    private string _findNote = "";

    /// <summary>Why the list is empty, or that it is not everyone.</summary>
    public string FindNote { get => _findNote; private set { this.RaiseAndSetIfChanged(ref _findNote, value); this.RaisePropertyChanged(nameof(HasFindNote)); } }
    public bool HasFindNote => FindNote.Length > 0;

    private void BeginNewConversation()
    {
        ShowList = true;
        FindWords = "";
        IsFinding = true;
        FillPeople();
    }

    private void FillPeople()
    {
        if (!IsFinding) return;

        var found = _service.FindOnline(FindWords);
        var shown = found.Take(MostPeopleListed).ToList();

        // Replaced only when someone came or went: a list that rebuilds under the pointer loses the click.
        if (!People.Select(p => p.Person).SequenceEqual(shown))
        {
            People.Clear();
            foreach (var person in shown) People.Add(new MessagesPersonVm(person));
        }

        FindNote = found.Count == 0 ? (FindWords.Trim().Length > 0 ? MessagesText.FindNobodyMatches : MessagesText.FindNobodyOnline)
                 : found.Count > shown.Count ? string.Format(CultureInfo.CurrentCulture, MessagesText.FindMore, found.Count - shown.Count)
                 : "";
    }

    /// <summary>Opens the conversation with someone found: the one already there, or a new one for a first message.</summary>
    public Task StartConversationAsync(MessagesPersonVm person)
    {
        IsFinding = false;
        return OpenTabAsync(person.Person.Key, person.Name);
    }

    /// <summary>Enter in the search box: whoever is at the top of the list, as they are the best match.</summary>
    public Task StartWithFirstFoundAsync() => People.Count > 0 ? StartConversationAsync(People[0]) : Task.CompletedTask;

    public ObservableCollection<MessagesTabVm> Tabs { get; } = [];
    public ObservableCollection<MessagesConversationVm> Conversations { get; } = [];

    /// <summary>The tool should come up, or forward, wherever it is.</summary>
    public event Action? ShowRequested;

    /// <summary>Set by the view: asks before something that cannot be undone.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    private MessagesTabVm? _selectedTab;
    public MessagesTabVm? SelectedTab
    {
        get => _selectedTab;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTab, value);
            this.RaisePropertyChanged(nameof(HasTab));
            this.RaisePropertyChanged(nameof(ShowNothingOpen));
            FollowTab();
            SaveTabs();
            _ = MarkOpenReadAsync();
        }
    }

    public bool HasTab => SelectedTab is not null;
    public bool ShowNothingOpen => SelectedTab is null;

    private MessagesConversationVm? _selectedConversation;

    /// <summary>A line of the list, which is only ever a way of opening its tab.</summary>
    public MessagesConversationVm? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedConversation, value);
            if (value is not null && !_following) _ = OpenTabAsync(value.Summary.Key, value.Name);
        }
    }

    private bool _following;

    /// <summary>The list marks whichever conversation is showing. With its tab closed it marks nothing, so a click can open it again.</summary>
    private void FollowTab()
    {
        _following = true;
        SelectedConversation = SelectedTab is { } tab ? Conversations.FirstOrDefault(c => c.Summary.Key == tab.Key) : null;
        _following = false;
    }

    private bool _showList;
    public bool ShowList
    {
        get => _showList;
        set
        {
            this.RaiseAndSetIfChanged(ref _showList, value);
            _ = _prefs.SetBoolAsync(ShowListPref, value);
        }
    }

    private string _search = "";
    public string Search
    {
        get => _search;
        set
        {
            this.RaiseAndSetIfChanged(ref _search, value);
            FillList();
        }
    }

    private bool _hasConversations;
    public bool HasNoConversations => !_hasConversations;

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

    private int _unread;
    public int TotalUnread
    {
        get => _unread;
        private set
        {
            this.RaiseAndSetIfChanged(ref _unread, value);
            this.RaisePropertyChanged(nameof(HasUnread));
            this.RaisePropertyChanged(nameof(UnreadText));
            this.RaisePropertyChanged(nameof(WindowTitle));
        }
    }
    public bool HasUnread => TotalUnread > 0;
    public string UnreadText => TotalUnread.ToString("N0", CultureInfo.CurrentCulture);

    // ── For the Direct Messages control on panels ─────────────────────────────
    //
    // A panel shows conversations of its own, apart from this tool's tabs, so picking one there
    // doesn't change what the tool shows.

    /// <summary>The list of conversations was read again.</summary>
    public event Action? ConversationsChanged;

    /// <summary>A message came or went, in any conversation. On the UI thread.</summary>
    public event Action<DmKey, DmLine>? LineArrived;

    /// <summary>Every saved conversation, newest first, whatever the tool's list is searched for.</summary>
    public IReadOnlyList<MessagesConversationVm> AllConversations() =>
        [.. _all.OrderByDescending(c => c.LastAt).Select(c => new MessagesConversationVm(c))];

    /// <summary>A conversation of its own, loaded, for a panel. Null for one whose service has gone.</summary>
    public async Task<MessagesTabVm?> OpenForPanelAsync(DmKey key)
    {
        if (_service.SourceOf(key) is not { } source || _all.FirstOrDefault(c => c.Key == key) is not { } known) return null;
        var tab = new MessagesTabVm(source, known, EmotesOf(key)) { Confirm = AskAsync, MyName = source.MyName(key.ServerId) };
        await tab.LoadAsync();
        return tab;
    }

    /// <summary>Marks a conversation read, as looking at it in the tool does.</summary>
    public async Task MarkReadAsync(DmKey key)
    {
        if (TabOf(key) is { } tab) tab.Unread = 0;
        if (_service.SourceOf(key) is { } source) await Task.Run(() => source.MarkReadAsync(key));
    }

    /// <summary>The conversations with something not yet read, newest first, whatever the list is searched for. For a panel's icon.</summary>
    public IReadOnlyList<MessagesConversationVm> UnreadConversations() =>
        [.. _all.Where(c => c.Unread > 0).OrderByDescending(c => c.LastAt).Select(c => new MessagesConversationVm(c))];

    public string WindowTitle => TotalUnread > 0 ? $"{MessagesText.WindowTitle} ({UnreadText})" : MessagesText.WindowTitle;

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
    private bool _isBeingLookedAt;

    /// <summary>Brings the tool up with a conversation open, whether or not anything is saved for it yet.</summary>
    public void Open(DmKey key, string name)
    {
        ShowRequested?.Invoke();
        _ = OpenAfterRestoringAsync(key, name);
    }

    private async Task OpenAfterRestoringAsync(DmKey key, string name)
    {
        await RestoreTabsAsync();
        await OpenTabAsync(key, name);
    }

    /// <summary>Brings the tool up as it was.</summary>
    public void Show() => ShowRequested?.Invoke();

    /// <summary>Brings the tool up on what most needs reading.</summary>
    public async Task ShowUnreadAsync()
    {
        ShowRequested?.Invoke();
        await RestoreTabsAsync();
        await RefreshAsync();
        var next = _all.FirstOrDefault(c => c.Alerts > 0) ?? _all.FirstOrDefault(c => c.Unread > 0);
        if (next is not null) await OpenTabAsync(next.Key, next.Name);
    }

    private MessagesTabVm? TabOf(DmKey key) => Tabs.FirstOrDefault(t => t.Key == key);

    private async Task OpenTabAsync(DmKey key, string name)
    {
        var tab = TabOf(key);
        if (tab is null)
        {
            if (_service.SourceOf(key) is not { } source) return;

            tab = new MessagesTabVm(source, _all.FirstOrDefault(c => c.Key == key) ?? source.Describe(key, name), EmotesOf(key)) { Confirm = AskAsync, MyName = source.MyName(key.ServerId) };
            Tabs.Add(tab);
            tab.Unread = _all.FirstOrDefault(c => c.Key == key)?.Unread ?? 0;
            await tab.LoadAsync();
        }
        SelectedTab = tab;
    }

    public void CloseTab(MessagesTabVm tab)
    {
        var at = Tabs.IndexOf(tab);
        if (at < 0) return;

        Tabs.RemoveAt(at);
        if (ReferenceEquals(SelectedTab, tab)) SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(at, Tabs.Count - 1)];
        else SaveTabs();
    }

    /// <summary>Asked of whichever view is showing now: a tab outlives any one of them, being dragged between windows.</summary>
    private Task<bool> AskAsync(string message) => Confirm?.Invoke(message) ?? Task.FromResult(false);

    private bool _restored;
    private Task? _restoring;

    /// <summary>The tabs left open last time, once the tool first comes up. Waits for the list, which says what is still there.</summary>
    public Task RestoreTabsAsync() => _restoring ??= RestoreAsync();

    private async Task RestoreAsync()
    {
        while (_refreshing) await Task.Delay(20);
        await RefreshAsync();

        List<string>? saved = null;
        try { saved = JsonSerializer.Deserialize<List<string>>(_prefs.Get(OpenTabsPref) ?? "[]"); }
        catch (JsonException) { /* an unreadable list is an empty one */ }

        var selected = DmKey.Parse(_prefs.Get(SelectedTabPref));
        foreach (var key in (saved ?? []).Select(DmKey.Parse).OfType<DmKey>())
        {
            if (_service.SourceOf(key) is not { } source || TabOf(key) is not null) continue;

            var known = _all.FirstOrDefault(c => c.Key == key);

            // Nothing saved any more, as when its server was deleted: there is nothing to come back to.
            if (known is null) continue;

            var tab = new MessagesTabVm(source, known, EmotesOf(key)) { Confirm = AskAsync, Unread = known.Unread, MyName = source.MyName(key.ServerId) };
            Tabs.Add(tab);
            await tab.LoadAsync();
        }

        _restored = true;
        if (SelectedTab is null) SelectedTab = Tabs.FirstOrDefault(t => t.Key == selected) ?? Tabs.FirstOrDefault();
    }

    private void SaveTabs()
    {
        // ⚠️ Not before the last list has been read back, or opening one conversation would forget the rest.
        if (!_restored) return;

        _ = _prefs.SetAsync(OpenTabsPref, JsonSerializer.Serialize(Tabs.Select(t => t.Key.Id).ToList()));
        _ = _prefs.SetAsync(SelectedTabPref, SelectedTab?.Key.Id ?? "");
    }

    private async Task MarkOpenReadAsync()
    {
        if (!IsBeingLookedAt || SelectedTab is not { } tab) return;

        tab.Unread = 0;
        if (_service.SourceOf(tab.Key) is { } source) await Task.Run(() => source.MarkReadAsync(tab.Key));
    }

    private void OnArrived(DmKey key, DmLine line, bool alert)
    {
        TabOf(key)?.Add(line);
        LineArrived?.Invoke(key, line);

        var beingRead = IsBeingLookedAt && SelectedTab?.Key == key;
        if (beingRead) _ = MarkOpenReadAsync();

        if (alert && !beingRead && SoundOn && !_mute.Muted) _ = PlaySoundAsync();
        QueueRefresh();
    }

    private async Task PlaySoundAsync()
    {
        try { await _sound.PlayAsync(AlarmSoundService.DefaultKey); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { /* no sound is not worth interrupting a conversation for */ }
    }

    /// <summary>Many changes come in a burst on connecting: one refresh for the lot.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;

        _ = Task.Delay(TimeSpan.FromMilliseconds(200)).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        }, TaskScheduler.Default);
    }

    private bool _refreshing, _refreshAgain;

    public async Task RefreshAsync()
    {
        if (_refreshing) { _refreshAgain = true; return; }

        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                _all = [.. await Task.Run(() => _service.ConversationsAsync())];
                TotalUnread = _all.Sum(c => c.Unread);
                FillList();
                FillPeople();

                foreach (var tab in Tabs)
                {
                    var known = _all.FirstOrDefault(c => c.Key == tab.Key);
                    if (known is not null)
                    {
                        tab.Apply(known);
                        tab.Unread = IsBeingLookedAt && ReferenceEquals(tab, SelectedTab) ? 0 : known.Unread;
                    }
                    else if (_service.SourceOf(tab.Key) is { } source) tab.Apply(source.Describe(tab.Key, tab.Name));
                }
            }
            while (_refreshAgain);
            ConversationsChanged?.Invoke();
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException) { /* the next change asks again */ }
        finally { _refreshing = false; }
    }

    private void FillList()
    {
        var words = Search.Trim();
        var shown = _all.Where(c => words.Length == 0
                                 || c.Name.Contains(words, StringComparison.CurrentCultureIgnoreCase)
                                 || c.Server.Contains(words, StringComparison.CurrentCultureIgnoreCase)
                                 || c.Service.Contains(words, StringComparison.CurrentCultureIgnoreCase)).ToList();

        // Replaced in place, and only when something shows differently: the list keeps its scroll and selection.
        var same = Conversations.Count == shown.Count && Conversations.Select(c => c.Summary).SequenceEqual(shown);
        if (!same)
        {
            _following = true;
            Conversations.Clear();
            foreach (var c in shown) Conversations.Add(new MessagesConversationVm(c));
            _following = false;
            FollowTab();
        }

        _hasConversations = _all.Count > 0;
        this.RaisePropertyChanged(nameof(HasNoConversations));
        this.RaisePropertyChanged(nameof(ShowNoSaved));
    }
}
