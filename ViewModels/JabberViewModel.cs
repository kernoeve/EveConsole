using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Jabber;
using ReactiveUI;

namespace EveConsole.ViewModels;

public enum JabberRowKind { Account, Request }

/// <summary>A server's own line reads heavier than the people and rooms under it.</summary>
public static class JabberBold
{
    public static readonly Avalonia.Data.Converters.IValueConverter Convert =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.FontWeight>(
            b => b ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);
}

/// <summary>
/// A line in the left-hand list: a server, or someone asking to be a contact on it. Contacts themselves are in Messages.
///
/// <para>⚠️ Kept and updated in place. Rebuilt on each change, the list control dropped its selection
/// with every message that arrived, and the pane on the right went stale behind it.</para>
/// </summary>
public sealed class JabberRowVm : ReactiveObject
{
    public JabberRowKind Kind { get; init; }
    public int    AccountId   { get; init; }

    /// <summary>The address the line is about.</summary>
    public string Key         { get; init; } = "";
    public string Text        { get; init; } = "";
    public string Detail      { get; init; } = "";

    /// <summary>Red, amber, green or grey: what state it is in at a glance.</summary>
    public string Dot         { get; init; } = "";

    public bool IsAccount => Kind == JabberRowKind.Account;

    public string Identity => $"{Kind}:{AccountId}:{Key}";

    // What the screen binds to, so an update can reach it.
    private string _text = "", _detail = "", _dot = "";

    public string ShownText   { get => _text;   private set => this.RaiseAndSetIfChanged(ref _text, value); }
    public string ShownDetail { get => _detail; private set { this.RaiseAndSetIfChanged(ref _detail, value); this.RaisePropertyChanged(nameof(HasDetail)); } }
    public bool   HasDetail   => _detail.Length > 0;

    public string ShownDot
    {
        get => _dot;
        private set
        {
            this.RaiseAndSetIfChanged(ref _dot, value);
            foreach (var name in new[] { nameof(HasDot), nameof(DotGreen), nameof(DotAmber), nameof(DotRed) }) this.RaisePropertyChanged(name);
        }
    }
    public bool HasDot   => _dot.Length > 0;
    public bool DotGreen => _dot == "green";
    public bool DotAmber => _dot == "amber";
    public bool DotRed   => _dot == "red";

    /// <summary>Takes on what a freshly worked-out line says.</summary>
    public JabberRowVm Fill(JabberRowVm from)
    {
        ShownText = from.Text; ShownDetail = from.Detail; ShownDot = from.Dot;
        return this;
    }
}

public sealed record JabberShowChoice(XmppShow Show, string Label);

/// <summary>A pack on a server's page, in the order the server reads them.</summary>
public sealed class JabberEmotePackVm(EmotePackInfo info, bool canUp, bool canDown)
{
    public EmotePackInfo Info => info;
    public string Name => info.Name;
    public string Detail => string.Format(CultureInfo.CurrentCulture, JabberText.EmotePackDetail, info.Codes, info.Pictures);
    public string About => string.Join("\n", new[] { info.Description, info.Author }.Where(t => t.Length > 0));
    public bool HasAbout => About.Length > 0;
    public bool CanUp => canUp;
    public bool CanDown => canDown;
}
/// <summary>
/// The Jabber tool: every server down the left with its people, and how each is set up on the right. Conversations
/// are elsewhere: with one person in Messages, in a room in Group Chat.
/// </summary>
public sealed class JabberViewModel : ReactiveObject
{
    private readonly JabberService _service;
    private readonly AppPreferencesService _prefs;
    private readonly EmoteService? _emotes;
    private int _refreshQueued;

    public JabberViewModel(JabberService service, AppPreferencesService prefs, EmoteService? emotes = null)
    {
        _service = service;
        _prefs   = prefs;
        _emotes  = emotes;

        AddAccountCommand      = ReactiveCommand.Create(BeginAddAccount);
        EditAccountCommand     = ReactiveCommand.Create(BeginEditAccount);
        SaveAccountCommand     = ReactiveCommand.CreateFromTask(SaveAccountAsync);
        CancelEditCommand      = ReactiveCommand.Create(() => { IsEditingAccount = false; });
        DeleteAccountCommand   = ReactiveCommand.CreateFromTask(DeleteAccountAsync);
        ConnectCommand         = ReactiveCommand.Create(Connect);
        DisconnectCommand      = ReactiveCommand.CreateFromTask(DisconnectAsync);
        TrustCertificateCommand = ReactiveCommand.CreateFromTask(TrustCertificateAsync);
        BeginAddContactCommand = ReactiveCommand.Create(() => { ContactAddress = ""; ContactName = ""; IsAddingContact = true; });
        AddContactCommand      = ReactiveCommand.CreateFromTask(AddContactAsync);
        CancelAddContactCommand = ReactiveCommand.Create(() => { IsAddingContact = false; });
        AcceptRequestCommand   = ReactiveCommand.CreateFromTask(() => AnswerRequestAsync(true));
        DenyRequestCommand     = ReactiveCommand.CreateFromTask(() => AnswerRequestAsync(false));
        AddPackCommand         = ReactiveCommand.CreateFromTask(AddPackAsync);
        AddPackCommand.ThrownExceptions.Subscribe(e => EmoteStatus = e.Message);
        foreach (var command in new IHandleObservableErrors[]
                 {
                     SaveAccountCommand, DeleteAccountCommand, DisconnectCommand, TrustCertificateCommand,
                     AddContactCommand, AcceptRequestCommand, DenyRequestCommand,
                 })
            command.ThrownExceptions.Subscribe(e => StatusText = e.Message);

        ShowChoices =
        [
            new(XmppShow.Online, JabberText.ShowOnline), new(XmppShow.Chat, JabberText.ShowChat),
            new(XmppShow.Away, JabberText.ShowAway), new(XmppShow.ExtendedAway, JabberText.ShowExtendedAway),
            new(XmppShow.DoNotDisturb, JabberText.ShowDoNotDisturb),
        ];
        HistoryDays = int.TryParse(_prefs.Get(JabberService.HistoryDaysPref), out var days) ? days : JabberService.DefaultHistoryDays;

        _service.SessionChanged += _ => QueueRefresh();
        _service.UnreadChanged  += QueueRefresh;
        if (_emotes is not null) _emotes.PacksChanged += () => { if (ShowAccount) _ = LoadEmotePacksAsync(); };
        QueueRefresh();
    }

    // ── The list on the left ──────────────────────────────────────────────────

    public ObservableCollection<JabberRowVm> Rows { get; } = [];

    private JabberRowVm? _selectedRow;
    public JabberRowVm? SelectedRow
    {
        get => _selectedRow;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedRow, value);
            OpenSelected();
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

    private Task? _refreshing;
    private bool _refreshAgain;

    /// <summary>
    /// Brings the list up to date. ⚠️ One at a time: each reads the database first, and an older one
    /// finishing after a newer one put the old state back on screen. A change meanwhile runs it again.
    /// </summary>
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
        var rows = new List<JabberRowVm>();
        foreach (var session in _service.Sessions) AddRows(rows, session);

        SyncRows(rows);

        HasAccounts = Rows.Count > 0;
        RefreshDetail();
    }

    private static void AddRows(List<JabberRowVm> rows, JabberSession session)
    {
        var id = session.Account.Id;
        rows.Add(new JabberRowVm
        {
            Kind = JabberRowKind.Account, AccountId = id, Key = session.Account.Address,
            Text = session.Account.Name.Length > 0 ? session.Account.Name : session.Account.Address,
            Detail = StateText(session), Dot = StateDot(session.State),
        });

        // By the name already saved for them, if any: someone asking is often someone already on the list.
        foreach (var request in session.SubscriptionRequests)
        {
            var saved = session.Contacts.FirstOrDefault(c => c.Jid == request)?.Name;
            rows.Add(new JabberRowVm { Kind = JabberRowKind.Request, AccountId = id, Key = request, Text = saved is { Length: > 0 } ? saved : request, Detail = JabberText.RowWantsToAdd, Dot = "amber" });
        }

        // ⚠️ No contacts here (user, 2026-11-07): they are in Messages, which adds and removes them per conversation.
    }

    /// <summary>Makes the list say this, moving as little as it can. See <see cref="JabberRowVm"/>.</summary>
    private void SyncRows(List<JabberRowVm> wanted)
    {
        var have = Rows.ToDictionary(r => r.Identity);

        for (var i = 0; i < wanted.Count; i++)
            wanted[i] = have.TryGetValue(wanted[i].Identity, out var kept) ? kept.Fill(wanted[i]) : wanted[i].Fill(wanted[i]);

        var keep = wanted.Select(r => r.Identity).ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(Rows[i].Identity)) Rows.RemoveAt(i);

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], wanted[i])) continue;

            var at = Rows.IndexOf(wanted[i]);
            if (at >= 0) Rows.Move(at, i);
            else Rows.Insert(i, wanted[i]);
        }
    }

    public static string DisplayRoom(JabberRoom room) => room.Name.Length > 0 ? room.Name : XmppAddress.Parse(room.RoomJid).Local;

    public static string StateDot(JabberState state) => state switch
    {
        JabberState.Connected  => "green",
        JabberState.Failed     => "red",
        JabberState.Offline    => "grey",
        _                      => "amber",
    };

    public static string ShowDot(XmppShow show) => show switch
    {
        XmppShow.Online or XmppShow.Chat => "green",
        XmppShow.Away or XmppShow.ExtendedAway => "amber",
        XmppShow.DoNotDisturb => "red",
        _ => "grey",
    };

    public static string StateText(JabberSession session) => session.State switch
    {
        JabberState.Connected    => JabberText.StateConnected,
        JabberState.Connecting   => JabberText.StateConnecting,
        JabberState.Reconnecting => session.NextAttempt is { } next
            ? string.Format(CultureInfo.CurrentCulture, JabberText.StateRetryAt, ChatTime.Clock(next))
            : JabberText.StateConnecting,
        JabberState.Failed       => session.Problem switch
        {
            JabberProblem.Password     => JabberText.ProblemPassword,
            JabberProblem.Certificate  => JabberText.ProblemCertificate,
            JabberProblem.NoEncryption => JabberText.ProblemNoEncryption,
            JabberProblem.Replaced     => JabberText.ProblemReplaced,
            _                          => JabberText.StateOffline,
        },
        JabberState.WaitingForWorker => JabberText.StateWaitingForWorker,
        _                        => JabberText.StateOffline,
    };

    public static string RoomFailureText(XmppRoomFailure failure, string words)
    {
        var text = failure switch
        {
            XmppRoomFailure.NickInUse        => JabberText.RoomFailNick,
            XmppRoomFailure.PasswordRequired => JabberText.RoomFailPassword,
            XmppRoomFailure.Banned           => JabberText.RoomFailBanned,
            XmppRoomFailure.NotAMember       => JabberText.RoomFailMembers,
            XmppRoomFailure.NotFound         => JabberText.RoomFailNotFound,
            _                                => JabberText.RoomFailOther,
        };
        return words.Length > 0 ? $"{text} {words}" : text;
    }

    // ── The pane on the right ─────────────────────────────────────────────────

    private bool _hasAccounts;
    public bool HasAccounts { get => _hasAccounts; private set { this.RaiseAndSetIfChanged(ref _hasAccounts, value); this.RaisePropertyChanged(nameof(ShowWelcome)); } }

    public bool ShowWelcome => !HasAccounts && !IsEditingAccount;

    public bool ShowAccount      => SelectedRow is { IsAccount: true } && !IsEditingAccount;
    public bool ShowRequest      => SelectedRow is { Kind: JabberRowKind.Request } && !IsEditingAccount;
    public bool ShowNothing      => HasAccounts && SelectedRow is null && !IsEditingAccount;

    private JabberSession? Session => SelectedRow is { } row ? _service.SessionOf(row.AccountId) : null;

    private string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    private string _subtitle = "";
    public string Subtitle { get => _subtitle; private set => this.RaiseAndSetIfChanged(ref _subtitle, value); }

    /// <summary>Plain words for a size: the person is choosing what to do about it.</summary>
    public static string SizeText(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1048576.0:N1} MB"
        : $"{Math.Max(1, bytes / 1024):N0} KB";

    /// <summary>What to tell the server the file is. Its name decides, as a browser's fetch of it will.</summary>
    public static string ContentTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png"  => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif"  => "image/gif",
        ".webp" => "image/webp",
        ".pdf"  => "application/pdf",
        ".txt" or ".log" => "text/plain",
        ".zip"  => "application/zip",
        _       => "application/octet-stream",
    };

    private void OpenSelected()
    {
        foreach (var name in new[] { nameof(ShowAccount), nameof(ShowRequest), nameof(ShowNothing) })
            this.RaisePropertyChanged(name);

        StatusText = "";
        RefreshDetail();

        if (EmoteAccount != _emoteAccountShown) EmoteStatus = "";
        _ = LoadEmotePacksAsync();
    }

    private string _status = "";
    public string StatusText { get => _status; set { this.RaiseAndSetIfChanged(ref _status, value); this.RaisePropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;

    // ── Settings for the whole tool ───────────────────────────────────────────

    private int _historyDays;
    public int HistoryDays
    {
        get => _historyDays;
        set
        {
            var days = Math.Max(0, value);
            this.RaiseAndSetIfChanged(ref _historyDays, days);
            _ = _prefs.SetAsync(JabberService.HistoryDaysPref, days.ToString(CultureInfo.InvariantCulture));
        }
    }

    // ── Details of what is selected ───────────────────────────────────────────

    public IReadOnlyList<JabberShowChoice> ShowChoices { get; }

    private JabberShowChoice? _chosenShow;
    public JabberShowChoice? ChosenShow
    {
        get => _chosenShow;
        set
        {
            this.RaiseAndSetIfChanged(ref _chosenShow, value);
            if (value is not null && !_loadingDetail && Session is { } session && session.Show != value.Show)
                _ = _service.SetPresenceAsync(session, value.Show, StatusMessage);
        }
    }

    /// <summary>Sends what has been typed as this account's status, if it has changed.</summary>
    public void ApplyStatusMessage()
    {
        if (Session is not { } session) return;

        var words = StatusMessage.Trim();
        if (words != session.Status) _ = _service.SetPresenceAsync(session, session.Show, words);
    }

    private int _statusAccount;
    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _loadingDetail;
    private bool _isConnected, _canConnect, _hasCertificateProblem;
    private string _accountState = "", _accountDetail = "", _certificateText = "";

    public bool IsConnected { get => _isConnected; private set => this.RaiseAndSetIfChanged(ref _isConnected, value); }
    public bool CanConnect { get => _canConnect; private set => this.RaiseAndSetIfChanged(ref _canConnect, value); }
    public bool HasCertificateProblem { get => _hasCertificateProblem; private set => this.RaiseAndSetIfChanged(ref _hasCertificateProblem, value); }
    public string AccountState { get => _accountState; private set => this.RaiseAndSetIfChanged(ref _accountState, value); }
    public string AccountDetail { get => _accountDetail; private set => this.RaiseAndSetIfChanged(ref _accountDetail, value); }
    public string CertificateText { get => _certificateText; private set => this.RaiseAndSetIfChanged(ref _certificateText, value); }

    private string _requestHelp = "";
    public string RequestHelp { get => _requestHelp; private set => this.RaiseAndSetIfChanged(ref _requestHelp, value); }

    private string _archiveText = "";
    public string ArchiveText { get => _archiveText; private set => this.RaiseAndSetIfChanged(ref _archiveText, value); }

    /// <summary>What a server keeps for this account, for its own pane. Blank until it has been asked.</summary>
    public static string AccountArchiveLine(bool? keepsDirect, int roomsWithArchive, int roomsJoined)
    {
        var direct = keepsDirect switch { true => JabberText.ArchiveDirectYes, false => JabberText.ArchiveDirectNo, _ => "" };
        var rooms  = roomsJoined > 0 ? string.Format(CultureInfo.CurrentCulture, JabberText.ArchiveRoomsCount, roomsWithArchive, roomsJoined) : "";
        return string.Join(' ', new[] { direct, rooms }.Where(t => t.Length > 0));
    }

    /// <summary>What one room keeps, for the top of its conversation. Blank until it has been asked.</summary>
    public static string RoomArchiveLine(bool? keeps, bool refused) =>
        refused ? JabberText.ArchiveRoomRefused
        : keeps switch { true => JabberText.ArchiveRoomYes, false => JabberText.ArchiveRoomNo, _ => "" };

    private void RefreshDetail()
    {
        if (SelectedRow is not { } row || Session is not { } session) return;

        _loadingDetail = true;
        try
        {
            Title    = row.ShownText;
            Subtitle = row.Kind == JabberRowKind.Account ? session.Account.Address : row.Key;

            AccountState  = StateText(session);
            AccountDetail = session.State == JabberState.Reconnecting || session is { State: JabberState.Failed, Problem: JabberProblem.Other } ? session.Detail : "";
            IsConnected   = session.State == JabberState.Connected;
            CanConnect    = session.State is JabberState.Offline or JabberState.Failed;
            HasCertificateProblem = session.Problem == JabberProblem.Certificate && session.CertificateFingerprint.Length > 0;
            CertificateText = HasCertificateProblem
                ? string.Format(CultureInfo.CurrentCulture, JabberText.CertificateAsk, session.CertificateSubject, Grouped(session.CertificateFingerprint))
                : "";
            ChosenShow = ShowChoices.FirstOrDefault(c => c.Show == session.Show);

            // ⚠️ Only on coming to an account: this runs on every change, and would otherwise replace what is being typed.
            if (_statusAccount != session.Account.Id)
            {
                _statusAccount = session.Account.Id;
                StatusMessage = session.Status;
            }

            RequestHelp = row.Kind == JabberRowKind.Request ? JabberText.RequestHelp : "";

            ArchiveText = "";
            if (session.State == JabberState.Connected)
            {
                if (row.Kind == JabberRowKind.Account)
                {
                    var (keeping, joined) = session.Locked(s => (s.RoomMap.Values.Count(r => r.Joined && r.HasArchive == true && !r.ArchiveRefused), s.RoomMap.Values.Count(r => r.Joined)));
                    ArchiveText = AccountArchiveLine(session.HasArchive, keeping, joined);
                }
            }
        }
        finally { _loadingDetail = false; }
    }

    /// <summary>Hex in fours, as a person compares a fingerprint against what the server's admin published.</summary>
    private static string Grouped(string fingerprint) =>
        string.Join(' ', fingerprint.Chunk(4).Select(c => new string(c)));

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> AddAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> EditAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> TrustCertificateCommand { get; }
    public ReactiveCommand<Unit, Unit> BeginAddContactCommand { get; }
    public ReactiveCommand<Unit, Unit> AddContactCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAddContactCommand { get; }
    public ReactiveCommand<Unit, Unit> AcceptRequestCommand { get; }
    public ReactiveCommand<Unit, Unit> DenyRequestCommand { get; }

    private void Connect() { if (Session is { } session) _service.Connect(session); }

    private async Task DisconnectAsync() { if (Session is { } session) await _service.DisconnectAsync(session); }

    private async Task TrustCertificateAsync() { if (Session is { } session) await _service.TrustCertificateAsync(session); }

    /// <summary>Set by the view: asks before something that cannot be undone.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    // ── Emote packs: each server's own ────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> AddPackCommand { get; }

    public bool CanUseEmotes => _emotes is not null;

    public ObservableCollection<JabberEmotePackVm> EmotePacks { get; } = [];
    public bool HasEmotePacks => EmotePacks.Count > 0;

    private IReadOnlyList<EmotePackInfo> _otherPacks = [];

    /// <summary>Packs loaded for other servers that this one could have too, without loading them again.</summary>
    public IReadOnlyList<EmotePackInfo> OtherPacks
    {
        get => _otherPacks;
        private set { this.RaiseAndSetIfChanged(ref _otherPacks, value); this.RaisePropertyChanged(nameof(HasOtherPacks)); }
    }
    public bool HasOtherPacks => OtherPacks.Count > 0;

    private EmotePackInfo? _packToAdd;
    public EmotePackInfo? PackToAdd { get => _packToAdd; set => this.RaiseAndSetIfChanged(ref _packToAdd, value); }

    private bool _isLoadingPack;
    public bool IsLoadingPack { get => _isLoadingPack; private set => this.RaiseAndSetIfChanged(ref _isLoadingPack, value); }

    private string _emoteStatus = "";
    public string EmoteStatus { get => _emoteStatus; private set { this.RaiseAndSetIfChanged(ref _emoteStatus, value); this.RaisePropertyChanged(nameof(HasEmoteStatus)); } }
    public bool HasEmoteStatus => EmoteStatus.Length > 0;

    private int _emoteAccountShown;
    private int EmoteAccount => SelectedRow is { IsAccount: true } row ? row.AccountId : 0;

    private async Task LoadEmotePacksAsync()
    {
        var account = EmoteAccount;
        _emoteAccountShown = account;
        if (_emotes is null || account == 0) return;

        var all = await Task.Run(() => _emotes.PacksAsync());
        var mine = await Task.Run(() => _emotes.PackIdsOfAsync(account));
        if (EmoteAccount != account) return;

        var list = mine.Select(id => all.FirstOrDefault(p => p.Id == id)).OfType<EmotePackInfo>().ToList();
        EmotePacks.Clear();
        for (var i = 0; i < list.Count; i++) EmotePacks.Add(new JabberEmotePackVm(list[i], i > 0, i < list.Count - 1));
        this.RaisePropertyChanged(nameof(HasEmotePacks));

        OtherPacks = [.. all.Where(p => !mine.Contains(p.Id))];
        PackToAdd = OtherPacks.FirstOrDefault();
    }

    /// <summary>Loads a pack from a zip or a theme file and gives it to the server whose page is open.</summary>
    public async Task LoadPackAsync(string path)
    {
        if (_emotes is null || EmoteAccount is not (> 0 and var account)) return;

        IsLoadingPack = true;
        EmoteStatus = "";
        try
        {
            var result = await Task.Run(() => _emotes.ImportAsync(path, account));
            EmoteStatus = LoadedText(result);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            EmoteStatus = e.Message;
        }
        finally
        {
            IsLoadingPack = false;
        }
        await LoadEmotePacksAsync();
    }

    public static string LoadedText(EmoteImportResult r)
    {
        var text = string.Format(CultureInfo.CurrentCulture, r.Replaced ? JabberText.EmoteReplaced : JabberText.EmoteLoaded, r.Name, r.Codes, r.Pictures);
        if (r.Missing > 0) text += " " + string.Format(CultureInfo.CurrentCulture, JabberText.EmoteMissing, r.Missing);
        if (r.Unusable > 0) text += " " + string.Format(CultureInfo.CurrentCulture, JabberText.EmoteUnusable, r.Unusable);
        return text;
    }

    private async Task AddPackAsync()
    {
        if (_emotes is null || EmoteAccount is not (> 0 and var account) || PackToAdd is not { } pack) return;

        EmoteStatus = "";
        await Task.Run(() => _emotes.AddToAsync(account, pack.Id));
        await LoadEmotePacksAsync();
    }

    public async Task MovePackAsync(JabberEmotePackVm pack, int by)
    {
        if (_emotes is null || EmoteAccount is not (> 0 and var account)) return;

        await Task.Run(() => _emotes.MoveAsync(account, pack.Info.Id, by));
        await LoadEmotePacksAsync();
    }

    /// <summary>Takes a pack off this server. Asked first when no other server has it, as it is then deleted.</summary>
    public async Task RemovePackAsync(JabberEmotePackVm pack)
    {
        if (_emotes is null || EmoteAccount is not (> 0 and var account)) return;

        var last = pack.Info.AccountIds.All(a => a == account);
        if (last && (Confirm is null || !await Confirm(string.Format(CultureInfo.CurrentCulture, JabberText.EmoteRemoveLast, pack.Name)))) return;

        try
        {
            var deleted = await Task.Run(() => _emotes.RemoveFromAsync(account, pack.Info.Id));
            EmoteStatus = string.Format(CultureInfo.CurrentCulture, deleted ? JabberText.EmoteDeleted : JabberText.EmoteRemoved, pack.Name);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            EmoteStatus = e.Message;
        }
        await LoadEmotePacksAsync();
    }

    // ── The account editor ────────────────────────────────────────────────────

    private bool _isEditingAccount;
    public bool IsEditingAccount
    {
        get => _isEditingAccount;
        set
        {
            this.RaiseAndSetIfChanged(ref _isEditingAccount, value);
            foreach (var name in new[] { nameof(ShowAccount), nameof(ShowRequest), nameof(ShowNothing), nameof(ShowWelcome) })
                this.RaisePropertyChanged(name);
        }
    }

    private int _editId;
    public bool EditIsExisting => _editId != 0;

    private string _editName = "", _editAddress = "", _editPassword = "", _editHost = "", _editResource = "", _editNickname = "", _editAlertWords = "";
    private int _editPort = XmppDns.DefaultPort;
    private bool _editDirectTls, _editAutoConnect = true;

    public string EditName { get => _editName; set => this.RaiseAndSetIfChanged(ref _editName, value); }
    public string EditAddress { get => _editAddress; set => this.RaiseAndSetIfChanged(ref _editAddress, value); }
    public string EditPassword { get => _editPassword; set => this.RaiseAndSetIfChanged(ref _editPassword, value); }
    public string EditHost { get => _editHost; set => this.RaiseAndSetIfChanged(ref _editHost, value); }
    public int EditPort { get => _editPort; set => this.RaiseAndSetIfChanged(ref _editPort, value); }
    public bool EditDirectTls { get => _editDirectTls; set => this.RaiseAndSetIfChanged(ref _editDirectTls, value); }
    public string EditResource { get => _editResource; set => this.RaiseAndSetIfChanged(ref _editResource, value); }
    public string EditNickname { get => _editNickname; set => this.RaiseAndSetIfChanged(ref _editNickname, value); }
    public bool EditAutoConnect { get => _editAutoConnect; set => this.RaiseAndSetIfChanged(ref _editAutoConnect, value); }
    public string EditAlertWords { get => _editAlertWords; set => this.RaiseAndSetIfChanged(ref _editAlertWords, value); }

    /// <summary>States how the password is held here, because "protected" must never be claimed loosely.</summary>
    public string PasswordNote => SecretStore.Description;

    private void BeginAddAccount()
    {
        _editId = 0;
        EditName = EditAddress = EditPassword = EditHost = EditResource = EditNickname = EditAlertWords = "";
        EditPort = XmppDns.DefaultPort;
        EditDirectTls = false;
        EditAutoConnect = true;
        this.RaisePropertyChanged(nameof(EditIsExisting));
        IsEditingAccount = true;
    }

    private void BeginEditAccount()
    {
        if (Session?.Account is not { } a) return;

        _editId = a.Id;
        EditName = a.Name; EditAddress = a.Address; EditPassword = ""; EditHost = a.Host; EditPort = a.Port;
        EditDirectTls = a.DirectTls; EditResource = a.Resource; EditNickname = a.Nickname;
        EditAutoConnect = a.AutoConnect; EditAlertWords = a.AlertWords;
        this.RaisePropertyChanged(nameof(EditIsExisting));
        IsEditingAccount = true;
    }

    private async Task SaveAccountAsync()
    {
        var address = XmppAddress.Parse(EditAddress);
        if (address.Local.Length == 0 || address.Domain.Length == 0) { StatusText = JabberText.AddressInvalid; return; }
        if (_editId == 0 && EditPassword.Length == 0) { StatusText = JabberText.PasswordRequired; return; }

        var old = _editId != 0 ? _service.SessionOf(_editId)?.Account : null;
        var account = new JabberAccount
        {
            Id = _editId, Name = EditName.Trim(), Address = address.Bare, Host = EditHost.Trim(),
            Port = EditPort is > 0 and < 65536 ? EditPort : XmppDns.DefaultPort,
            DirectTls = EditDirectTls, Resource = EditResource.Trim(), Nickname = EditNickname.Trim(),
            AutoConnect = EditAutoConnect, AlertWords = EditAlertWords.Trim(),
            TrustedCertificate = old is not null && old.Address == address.Bare && old.Host == EditHost.Trim() ? old.TrustedCertificate : "",
            SavedShow = old?.SavedShow ?? (int)XmppShow.Online, SavedStatus = old?.SavedStatus ?? "",
        };

        var session = await _service.SaveAccountAsync(account, EditPassword.Length > 0 ? EditPassword : null);
        EditPassword = "";
        StatusText = "";
        IsEditingAccount = false;

        await RefreshAsync();
        SelectedRow = Rows.FirstOrDefault(r => r is { Kind: JabberRowKind.Account } && r.AccountId == session.Account.Id);

        // A login just entered is meant to be used, and is the quickest proof it is right.
        if (old is null) _service.Connect(session);
    }

    private async Task DeleteAccountAsync()
    {
        if (Session is not { } session) return;

        var name = session.Account.Name.Length > 0 ? session.Account.Name : session.Account.Address;
        if (Confirm is not null && !await Confirm(string.Format(CultureInfo.CurrentCulture, JabberText.ConfirmDeleteAccount, name))) return;

        IsEditingAccount = false;
        SelectedRow = null;
        await _service.DeleteAccountAsync(session.Account.Id);
        await RefreshAsync();
    }

    // ── People ────────────────────────────────────────────────────────────────

    private bool _isAddingContact;
    public bool IsAddingContact { get => _isAddingContact; set => this.RaiseAndSetIfChanged(ref _isAddingContact, value); }

    private string _contactAddress = "", _contactName = "";
    public string ContactAddress { get => _contactAddress; set => this.RaiseAndSetIfChanged(ref _contactAddress, value); }
    public string ContactName { get => _contactName; set => this.RaiseAndSetIfChanged(ref _contactName, value); }

    private async Task AddContactAsync()
    {
        if (Session is not { } session) return;
        var address = XmppAddress.Parse(ContactAddress);
        if (address.Local.Length == 0 || address.Domain.Length == 0) { StatusText = JabberText.AddressInvalid; return; }

        await Task.Run(() => _service.AddContactAsync(session, address.Bare, ContactName.Trim()));
        IsAddingContact = false;
    }

    private async Task AnswerRequestAsync(bool accept)
    {
        if (SelectedRow is not { Kind: JabberRowKind.Request } row || Session is not { } session) return;

        await Task.Run(() => _service.AnswerRequestAsync(session, row.Key, accept));

        SelectedRow = null;
    }
}
