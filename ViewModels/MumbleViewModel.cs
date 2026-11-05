using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Mumble;
using ReactiveUI;

namespace EveConsole.ViewModels;

public enum MumbleRowKind { Server, Channel, User }

/// <summary>A line in the tree: a server, a channel or a person. Updated in place, so selection and scroll hold.</summary>
public sealed class MumbleRowVm : ReactiveObject
{
    public MumbleRowKind Kind { get; init; }
    public int  ServerId { get; init; }

    /// <summary>The channel's id or the user's session.</summary>
    public uint Id { get; init; }
    public int  Depth { get; init; }

    public string Identity => $"{Kind}:{ServerId}:{Id}";
    public Avalonia.Thickness Indent => new(Depth * 14, 0, 0, 0);

    public bool IsServer  => Kind == MumbleRowKind.Server;
    public bool IsChannel => Kind == MumbleRowKind.Channel;
    public bool IsUser    => Kind == MumbleRowKind.User;

    private string _text = "", _detail = "", _flags = "";
    private bool _talking, _good, _warn, _bad, _isMe;

    public string Text   { get => _text;   set => this.RaiseAndSetIfChanged(ref _text, value); }
    public string Detail { get => _detail; set { this.RaiseAndSetIfChanged(ref _detail, value); this.RaisePropertyChanged(nameof(HasDetail)); } }
    public bool HasDetail => _detail.Length > 0;

    /// <summary>Short marks for muted, deafened and the like.</summary>
    public string Flags  { get => _flags;  set => this.RaiseAndSetIfChanged(ref _flags, value); }

    public bool Talking { get => _talking; set => this.RaiseAndSetIfChanged(ref _talking, value); }
    public bool Good { get => _good; set => this.RaiseAndSetIfChanged(ref _good, value); }
    public bool Warn { get => _warn; set => this.RaiseAndSetIfChanged(ref _warn, value); }
    public bool Bad  { get => _bad;  set => this.RaiseAndSetIfChanged(ref _bad, value); }
    public bool IsMe { get => _isMe; set => this.RaiseAndSetIfChanged(ref _isMe, value); }

    public void Fill(MumbleRowVm from)
    {
        Text = from.Text; Detail = from.Detail; Flags = from.Flags; Talking = from.Talking;
        Good = from.Good; Warn = from.Warn; Bad = from.Bad; IsMe = from.IsMe;
    }
}

public sealed record MumbleKeyChoice(int Key, string Label);

public sealed class MumbleChatVm(MumbleChatLine line)
{
    public string Sender { get; } = line.Sender;
    public string Text   { get; } = line.Text;
    public string When   { get; } = line.When.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
    public bool IsPrivate => line.IsPrivate;
}

/// <summary>Somebody on the always-on-top list.</summary>
public sealed record MumbleOverlayLine(string Name, string Server, bool IsSelf, bool IsShout);

/// <summary>
/// The Mumble tool: each server's channels and people on the left, what is selected on the right.
///
/// <para>Separate from Jabber on purpose: they are different tools, for different purposes. They share
/// nothing but the way passwords are kept.</para>
/// </summary>
public sealed class MumbleViewModel : ReactiveObject
{
    public const string OverlayPref = "mumble.overlay";
    public const string OverlayXPref = "mumble.overlay.x";
    public const string OverlayYPref = "mumble.overlay.y";

    private const int MostDepth = 64;
    private const int MostChatShown = 1000;

    private readonly MumbleService _service;
    private readonly AppPreferencesService _prefs;
    private int _refreshQueued;
    private Task? _refreshing;
    private bool _refreshAgain;
    private bool _loadingDetail;

    public MumbleViewModel(MumbleService service, AppPreferencesService prefs)
    {
        _service = service;
        _prefs   = prefs;

        KeyChoices = [.. GlobalHotkeyService.KeyOptions
            .Where(k => GlobalHotkeyService.AvailableHere(k.WinVk))
            .Select(k => new MumbleKeyChoice(k.WinVk, k.WinVk == 0 ? MumbleText.KeyNone : k.Name))];

        var hasText = this.WhenAnyValue(x => x.Draft, d => !string.IsNullOrWhiteSpace(d));

        AddServerCommand      = ReactiveCommand.Create(BeginAdd);
        EditServerCommand     = ReactiveCommand.Create(BeginEdit);
        SaveServerCommand     = ReactiveCommand.CreateFromTask(SaveAsync);
        CancelEditCommand     = ReactiveCommand.Create(() => { IsEditing = false; });
        DeleteServerCommand   = ReactiveCommand.CreateFromTask(DeleteAsync);
        ConnectCommand        = ReactiveCommand.Create(Connect);
        DisconnectCommand     = ReactiveCommand.CreateFromTask(DisconnectAsync);
        TrustCertificateCommand = ReactiveCommand.CreateFromTask(TrustAsync);
        JoinChannelCommand    = ReactiveCommand.CreateFromTask(JoinSelectedAsync);
        ToggleMuteCommand     = ReactiveCommand.CreateFromTask(ToggleMuteAsync);
        ToggleDeafenCommand   = ReactiveCommand.CreateFromTask(ToggleDeafenAsync);
        SendCommand           = ReactiveCommand.CreateFromTask(SendAsync, hasText);
        ToggleAudioCommand    = ReactiveCommand.Create(() => { IsAudioOpen = !IsAudioOpen; if (IsAudioOpen) LoadDevices(); });

        foreach (var c in new IHandleObservableErrors[]
                 {
                     SaveServerCommand, DeleteServerCommand, DisconnectCommand, TrustCertificateCommand, JoinChannelCommand,
                     ToggleMuteCommand, ToggleDeafenCommand, SendCommand,
                 })
            c.ThrownExceptions.Subscribe(e => StatusText = e.Message);

        _speakerVolume = _prefs.GetLong(MumbleService.OutputVolumePref, 100);
        _micGain       = _prefs.GetLong(MumbleService.MicGainPref, 100);
        _showOverlay   = _prefs.GetBool(OverlayPref);

        _service.InputDevice   = _prefs.Get(MumbleService.InputDevicePref) ?? "";
        _service.OutputDevice  = _prefs.Get(MumbleService.OutputDevicePref) ?? "";
        _service.OutputVolume  = _speakerVolume / 100.0;
        _service.MicrophoneGain = _micGain / 100.0;

        _service.SessionChanged += _ => QueueRefresh();
        _service.TalkersChanged += () => { QueueRefresh(); Dispatcher.UIThread.Post(RefreshOverlay); };
        _service.ChatReceived += (session, line) => Dispatcher.UIThread.Post(() => OnChat(session, line));

        QueueRefresh();
    }

    // ── The tree ──────────────────────────────────────────────────────────────

    public ObservableCollection<MumbleRowVm> Rows { get; } = [];

    private MumbleRowVm? _selected;
    public MumbleRowVm? SelectedRow
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            ShowChosen();
        }
    }

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;

        _ = Task.Delay(TimeSpan.FromMilliseconds(100)).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        }, TaskScheduler.Default);
    }

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
                await Task.Yield();
                Rebuild();
            }
            while (_refreshAgain);
        }
    }

    private void Rebuild()
    {
        var talking = _service.Talkers().Select(t => (t.ServerId, t.Session)).ToHashSet();
        var wanted  = new List<MumbleRowVm>();

        foreach (var session in _service.Sessions) AddServer(wanted, session, talking);

        Sync(wanted);
        HasServers = _service.Sessions.Count > 0;
        ShowDetail();
    }

    private static void AddServer(List<MumbleRowVm> rows, MumbleSession session, HashSet<(int, uint)> talking)
    {
        var id = session.Server.Id;
        rows.Add(new MumbleRowVm
        {
            Kind = MumbleRowKind.Server, ServerId = id,
            Text = session.Server.Name.Length > 0 ? session.Server.Name : session.Server.Host,
            Detail = StateText(session),
            Good = session.State == MumbleState.Connected,
            Warn = session.State is MumbleState.Connecting or MumbleState.Reconnecting,
            Bad = session.State == MumbleState.Failed,
            Flags = session.Server.SelfDeafened ? MumbleText.FlagDeaf : session.Server.SelfMuted ? MumbleText.FlagMuted : "",
        });

        if (session.State != MumbleState.Connected || session.Client is not { } client) return;

        // ⚠️ Looked up, not searched: this runs each time someone starts or stops talking, on servers of hundreds.
        var channels = client.Channels.ToDictionary(c => c.Id);
        var children = client.Channels.Where(c => c.Parent is not null).ToLookup(c => c.Parent!.Value);
        var users    = client.Users.ToLookup(u => u.ChannelId);
        var me       = client.Session;
        var shown    = new HashSet<uint>();

        void Add(uint channel, int depth)
        {
            // However a server describes its channels, this cannot go round in circles or too deep for the stack.
            if (depth > MostDepth || !shown.Add(channel) || !channels.TryGetValue(channel, out var c)) return;

            rows.Add(new MumbleRowVm
            {
                Kind = MumbleRowKind.Channel, ServerId = id, Id = c.Id, Depth = depth,
                Text = c.Name.Length > 0 ? c.Name : MumbleText.RootChannel,
            });

            foreach (var u in users[channel].OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase))
                rows.Add(new MumbleRowVm
                {
                    Kind = MumbleRowKind.User, ServerId = id, Id = u.Session, Depth = depth + 1,
                    Text = u.Name, IsMe = u.Session == me,
                    Talking = talking.Contains((id, u.Session)),
                    Flags = u.Deaf || u.SelfDeaf ? MumbleText.FlagDeaf : u.Mute || u.SelfMute || u.Suppress ? MumbleText.FlagMuted : "",
                });

            foreach (var child in children[channel]
                                          .OrderBy(x => x.Position).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                Add(child.Id, depth + 1);
        }

        Add(0, 1);
    }

    private void Sync(List<MumbleRowVm> wanted)
    {
        var have = Rows.ToDictionary(r => r.Identity);

        for (var i = 0; i < wanted.Count; i++)
            if (have.TryGetValue(wanted[i].Identity, out var kept) && kept.Depth == wanted[i].Depth)
            {
                kept.Fill(wanted[i]);
                wanted[i] = kept;
            }

        var keep = wanted.ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(Rows[i])) Rows.RemoveAt(i);

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], wanted[i])) continue;

            var at = Rows.IndexOf(wanted[i]);
            if (at >= 0) Rows.Move(at, i);
            else Rows.Insert(i, wanted[i]);
        }
    }

    private static string StateText(MumbleSession session) => session.State switch
    {
        MumbleState.Connected    => session.Client?.Me is { } me && session.Client.ChannelOf(me.ChannelId) is { } c && c.Name.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, MumbleText.StateInChannel, c.Name) : MumbleText.StateConnected,
        MumbleState.Connecting   => MumbleText.StateConnecting,
        MumbleState.Reconnecting => session.NextAttempt is { } next
            ? string.Format(CultureInfo.CurrentCulture, MumbleText.StateRetryAt, next.ToLocalTime().ToString("T", CultureInfo.CurrentCulture))
            : MumbleText.StateConnecting,
        MumbleState.Failed => session.Problem switch
        {
            MumbleProblem.Certificate => MumbleText.ProblemCertificate,
            MumbleProblem.Password    => MumbleText.ProblemPassword,
            MumbleProblem.Username    => MumbleText.ProblemUsername,
            _                         => session.Detail,
        },
        _ => MumbleText.StateOffline,
    };

    // ── The pane on the right ─────────────────────────────────────────────────

    private bool _hasServers;
    public bool HasServers { get => _hasServers; private set { this.RaiseAndSetIfChanged(ref _hasServers, value); this.RaisePropertyChanged(nameof(ShowWelcome)); } }
    public bool ShowWelcome => !HasServers && !IsEditing;

    private MumbleSession? Session => SelectedRow is { } row ? _service.SessionOf(row.ServerId) : null;

    public bool ShowServer  => SelectedRow is { IsServer: true } && !IsEditing;
    public bool ShowChannel => SelectedRow is { IsChannel: true } && !IsEditing;
    public bool ShowUser    => SelectedRow is { IsUser: true } && !IsEditing;
    public bool ShowChat    => !IsEditing && SelectedRow is not null && Session?.State == MumbleState.Connected;

    private string _title = "", _subtitle = "", _serverState = "", _serverDetail = "", _certificateText = "", _status = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => this.RaiseAndSetIfChanged(ref _subtitle, value); }
    public string ServerState { get => _serverState; private set => this.RaiseAndSetIfChanged(ref _serverState, value); }
    public string ServerDetail { get => _serverDetail; private set => this.RaiseAndSetIfChanged(ref _serverDetail, value); }
    public string CertificateText { get => _certificateText; private set => this.RaiseAndSetIfChanged(ref _certificateText, value); }
    public string StatusText { get => _status; set { this.RaiseAndSetIfChanged(ref _status, value); this.RaisePropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;

    private bool _isConnected, _canConnect, _hasCertificateProblem, _isMuted, _isDeafened;
    public bool IsConnected { get => _isConnected; private set { this.RaiseAndSetIfChanged(ref _isConnected, value); this.RaisePropertyChanged(nameof(ShowChat)); } }
    public bool CanConnect { get => _canConnect; private set => this.RaiseAndSetIfChanged(ref _canConnect, value); }
    public bool HasCertificateProblem { get => _hasCertificateProblem; private set => this.RaiseAndSetIfChanged(ref _hasCertificateProblem, value); }
    public bool IsMuted { get => _isMuted; private set => this.RaiseAndSetIfChanged(ref _isMuted, value); }
    public bool IsDeafened { get => _isDeafened; private set => this.RaiseAndSetIfChanged(ref _isDeafened, value); }

    private string _keysText = "";
    public string KeysText { get => _keysText; private set => this.RaiseAndSetIfChanged(ref _keysText, value); }

    private string _reachText = "";
    public string ReachText { get => _reachText; private set => this.RaiseAndSetIfChanged(ref _reachText, value); }

    private void ShowChosen()
    {
        foreach (var name in new[] { nameof(ShowServer), nameof(ShowChannel), nameof(ShowUser), nameof(ShowChat), nameof(ChatHint) })
            this.RaisePropertyChanged(name);

        StatusText = "";
        ShowDetail();
        ReloadChat();
    }

    private int _chatServer;

    private void ReloadChat()
    {
        var server = SelectedRow?.ServerId ?? 0;
        if (server == _chatServer && Chat.Count > 0) return;

        _chatServer = server;
        Chat.Clear();
        if (Session is { } session)
            foreach (var line in session.Chat) Chat.Add(new MumbleChatVm(line));
        ScrollChatRequested?.Invoke();
    }

    /// <summary>What each key reaches from where this client is now, read from the server's own channel links.</summary>
    private static string ReachOf(MumbleSession session)
    {
        if (session.State != MumbleState.Connected || session.Client is not { } client || client.Me is not { } me) return "";

        var linked = client.LinkedChannels(me.ChannelId).Select(Name).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        return linked.Count == 0
            ? string.Format(CultureInfo.CurrentCulture, MumbleText.ReachNoLinks, Name(me.ChannelId))
            : string.Format(CultureInfo.CurrentCulture, MumbleText.ReachLinks, Name(me.ChannelId), string.Join(", ", linked));

        // The root has no path, only its name.
        string Name(uint id) => MumbleService.PathOf(client, id) is { Length: > 0 } path ? path : client.ChannelOf(id)?.Name ?? "";
    }

    private void ShowDetail()
    {
        if (SelectedRow is not { } row || Session is not { } session) return;

        _loadingDetail = true;
        try
        {
            var server = session.Server;
            ServerState  = StateText(session);
            ServerDetail = session.State is MumbleState.Failed ? "" : session.Detail;
            IsConnected  = session.State == MumbleState.Connected;
            CanConnect   = session.State is MumbleState.Offline or MumbleState.Failed;
            IsMuted      = server.SelfMuted;
            IsDeafened   = server.SelfDeafened;

            HasCertificateProblem = session.Problem == MumbleProblem.Certificate && session.CertificateFingerprint.Length > 0;
            CertificateText = HasCertificateProblem
                ? string.Format(CultureInfo.CurrentCulture, MumbleText.CertificateAsk, session.CertificateSubject,
                                string.Join(' ', session.CertificateFingerprint.Chunk(4).Select(c => new string(c))))
                : "";

            KeysText = string.Format(CultureInfo.CurrentCulture, MumbleText.KeysSummary, KeyName(server.WhisperKey), KeyName(server.ShoutKey));
            ReachText = ReachOf(session);

            switch (row.Kind)
            {
                case MumbleRowKind.Server:
                    Title = row.Text;
                    Subtitle = $"{server.Username}@{server.Host}:{server.Port}"
                             + (session is { State: MumbleState.Connected, Client: { } voice } ? voice.IsUsingUdp ? " \u00B7 UDP" : " \u00B7 TCP" : "");
                    break;

                case MumbleRowKind.Channel:
                    var channel = session.Client?.ChannelOf(row.Id);
                    Title = row.Text;
                    Subtitle = channel is { Description.Length: > 0 } ? MumbleService.PlainText(channel.Description) : "";
                    break;

                case MumbleRowKind.User:
                    var user = session.Client?.UserOf(row.Id);
                    Title = row.Text;
                    Subtitle = "";
                    SelectedIsMe = row.IsMe;
                    if (user is not null) UserVolume = session.VolumeOf(user) * 100;
                    break;
            }
        }
        finally { _loadingDetail = false; }
    }

    private static string KeyName(int key) => key == 0 ? MumbleText.KeyNone : GlobalHotkeyService.VkName(key);

    private bool _selectedIsMe;
    public bool SelectedIsMe { get => _selectedIsMe; private set => this.RaiseAndSetIfChanged(ref _selectedIsMe, value); }

    private double _userVolume = 100;
    public double UserVolume
    {
        get => _userVolume;
        set
        {
            this.RaiseAndSetIfChanged(ref _userVolume, value);
            this.RaisePropertyChanged(nameof(UserVolumeText));
            if (_loadingDetail || SelectedRow is not { IsUser: true } row || Session is not { } session) return;
            if (session.Client?.UserOf(row.Id) is { } user) _ = _service.SetUserVolumeAsync(session, user, value / 100.0);
        }
    }
    public string UserVolumeText => $"{UserVolume:N0}%";

    // ── Text ──────────────────────────────────────────────────────────────────

    public ObservableCollection<MumbleChatVm> Chat { get; } = [];
    public event Action? ScrollChatRequested;

    public string ChatHint => SelectedRow is { IsUser: true, IsMe: false } row
        ? string.Format(CultureInfo.CurrentCulture, MumbleText.PrivateHint, row.Text)
        : MumbleText.MessageHint;

    private string _draft = "";
    public string Draft { get => _draft; set => this.RaiseAndSetIfChanged(ref _draft, value); }

    private void OnChat(MumbleSession session, MumbleChatLine line)
    {
        if (SelectedRow?.ServerId != session.Server.Id) return;
        Chat.Add(new MumbleChatVm(line));
        if (Chat.Count > MostChatShown) Chat.RemoveAt(0);
        ScrollChatRequested?.Invoke();
    }

    private async Task SendAsync()
    {
        if (Session is not { } session) return;
        var text = Draft;
        Draft = "";

        // With someone else selected, it goes to them alone: nothing about that person is said to the channel.
        var person = SelectedRow is { IsUser: true, IsMe: false } row ? session.Client?.UserOf(row.Id) : null;
        try
        {
            await Task.Run(() => person is not null
                ? _service.SendPrivateTextAsync(session, person, text)
                : _service.SendTextAsync(session, text));
        }
        catch { Draft = text; throw; }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> AddServerCommand { get; }
    public ReactiveCommand<Unit, Unit> EditServerCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveServerCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteServerCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> TrustCertificateCommand { get; }
    public ReactiveCommand<Unit, Unit> JoinChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDeafenCommand { get; }
    public ReactiveCommand<Unit, Unit> SendCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAudioCommand { get; }

    private void Connect() { if (Session is { } session) _service.Connect(session); }
    private async Task DisconnectAsync() { if (Session is { } session) await _service.DisconnectAsync(session); }
    private async Task TrustAsync() { if (Session is { } session) await _service.TrustCertificateAsync(session); }

    public async Task JoinSelectedAsync()
    {
        if (SelectedRow is { IsChannel: true } row && Session is { } session)
            await Task.Run(() => _service.JoinChannelAsync(session, row.Id));
    }

    private async Task ToggleMuteAsync()
    {
        if (Session is { } session) await Task.Run(() => _service.SetMuteAsync(session, !session.Server.SelfMuted, false));
    }

    private async Task ToggleDeafenAsync()
    {
        if (Session is { } session) await Task.Run(() => _service.SetMuteAsync(session, !session.Server.SelfDeafened, !session.Server.SelfDeafened));
    }

    /// <summary>Set by the view: asks before something that cannot be undone.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    // ── The server editor ─────────────────────────────────────────────────────

    public IReadOnlyList<MumbleKeyChoice> KeyChoices { get; }

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            this.RaiseAndSetIfChanged(ref _isEditing, value);
            foreach (var name in new[] { nameof(ShowServer), nameof(ShowChannel), nameof(ShowUser), nameof(ShowChat), nameof(ShowWelcome) })
                this.RaisePropertyChanged(name);
        }
    }

    private int _editId;
    public bool EditIsExisting => _editId != 0;

    private string _editName = "", _editHost = "", _editUsername = "", _editPassword = "", _editTokens = "";
    private int _editPort = 64738;
    private bool _editAutoConnect;
    private MumbleKeyChoice? _editWhisper, _editShout;

    public string EditName { get => _editName; set => this.RaiseAndSetIfChanged(ref _editName, value); }
    public string EditHost { get => _editHost; set => this.RaiseAndSetIfChanged(ref _editHost, value); }
    public int EditPort { get => _editPort; set => this.RaiseAndSetIfChanged(ref _editPort, value); }
    public string EditUsername { get => _editUsername; set => this.RaiseAndSetIfChanged(ref _editUsername, value); }
    public string EditPassword { get => _editPassword; set => this.RaiseAndSetIfChanged(ref _editPassword, value); }
    public string EditTokens { get => _editTokens; set => this.RaiseAndSetIfChanged(ref _editTokens, value); }
    public bool EditAutoConnect { get => _editAutoConnect; set => this.RaiseAndSetIfChanged(ref _editAutoConnect, value); }
    public MumbleKeyChoice? EditWhisper { get => _editWhisper; set => this.RaiseAndSetIfChanged(ref _editWhisper, value); }
    public MumbleKeyChoice? EditShout { get => _editShout; set => this.RaiseAndSetIfChanged(ref _editShout, value); }

    public string PasswordNote => SecretStore.Description;

    private void BeginAdd()
    {
        _editId = 0;
        EditName = EditHost = EditUsername = EditPassword = EditTokens = "";
        EditPort = 64738;
        EditAutoConnect = false;
        EditWhisper = EditShout = KeyChoices[0];
        this.RaisePropertyChanged(nameof(EditIsExisting));
        IsEditing = true;
    }

    private void BeginEdit()
    {
        if (Session?.Server is not { } s) return;

        _editId = s.Id;
        EditName = s.Name; EditHost = s.Host; EditPort = s.Port; EditUsername = s.Username; EditPassword = ""; EditTokens = s.Tokens;
        EditAutoConnect = s.AutoConnect;
        EditWhisper = KeyChoices.FirstOrDefault(k => k.Key == s.WhisperKey) ?? KeyChoices[0];
        EditShout   = KeyChoices.FirstOrDefault(k => k.Key == s.ShoutKey) ?? KeyChoices[0];
        this.RaisePropertyChanged(nameof(EditIsExisting));
        IsEditing = true;
    }

    private async Task SaveAsync()
    {
        if (EditHost.Trim().Length == 0 || EditUsername.Trim().Length == 0) { StatusText = MumbleText.HostAndNameRequired; return; }

        var old = _editId != 0 ? _service.SessionOf(_editId)?.Server : null;
        var server = new MumbleServer
        {
            Id = _editId, Name = EditName.Trim(), Host = EditHost.Trim(),
            Port = EditPort is > 0 and < 65536 ? EditPort : 64738,
            Username = EditUsername.Trim(), Tokens = EditTokens.Trim(), AutoConnect = EditAutoConnect,
            WhisperKey = EditWhisper?.Key ?? 0, ShoutKey = EditShout?.Key ?? 0,
            SelfMuted = old?.SelfMuted ?? false, SelfDeafened = old?.SelfDeafened ?? false, LastChannel = old?.LastChannel ?? "",
            TrustedCertificate = old is not null && old.Host == EditHost.Trim() && old.Port == EditPort ? old.TrustedCertificate : "",
        };

        // ⚠️ Keys must be unique, or one press talks on two servers at once.
        if (MumbleService.KeyClash(server, _service.Sessions.Select(s => s.Server)) is { } clash)
        {
            StatusText = string.Format(CultureInfo.CurrentCulture, MumbleText.KeyClash, clash);
            return;
        }

        var session = await _service.SaveServerAsync(server, EditPassword.Length > 0 ? EditPassword : null);
        EditPassword = "";
        StatusText = "";
        IsEditing = false;

        await RefreshAsync();
        SelectedRow = Rows.FirstOrDefault(r => r is { IsServer: true } && r.ServerId == session.Server.Id);

        if (old is null) _service.Connect(session);
    }

    private async Task DeleteAsync()
    {
        if (Session is not { } session) return;

        var name = session.Server.Name.Length > 0 ? session.Server.Name : session.Server.Host;
        if (Confirm is not null && !await Confirm(string.Format(CultureInfo.CurrentCulture, MumbleText.ConfirmDeleteServer, name))) return;

        IsEditing = false;
        SelectedRow = null;
        await _service.DeleteServerAsync(session.Server.Id);
        await RefreshAsync();
    }

    // ── Sound ─────────────────────────────────────────────────────────────────

    private bool _isAudioOpen;
    public bool IsAudioOpen { get => _isAudioOpen; set => this.RaiseAndSetIfChanged(ref _isAudioOpen, value); }

    public ObservableCollection<string> InputDevices { get; } = [];
    public ObservableCollection<string> OutputDevices { get; } = [];

    private bool _loadingDevices;
    private string? _inputChoice, _outputChoice;

    public string? InputChoice
    {
        get => _inputChoice;
        set
        {
            this.RaiseAndSetIfChanged(ref _inputChoice, value);
            if (_loadingDevices || value is null) return;
            var name = value == MumbleText.SystemDefault ? "" : value;
            _service.InputDevice = name;
            _ = _prefs.SetAsync(MumbleService.InputDevicePref, name);
        }
    }

    public string? OutputChoice
    {
        get => _outputChoice;
        set
        {
            this.RaiseAndSetIfChanged(ref _outputChoice, value);
            if (_loadingDevices || value is null) return;
            var name = value == MumbleText.SystemDefault ? "" : value;
            _service.OutputDevice = name;
            _ = _prefs.SetAsync(MumbleService.OutputDevicePref, name);
        }
    }

    private void LoadDevices()
    {
        _loadingDevices = true;
        try
        {
            InputDevices.Clear();
            OutputDevices.Clear();
            InputDevices.Add(MumbleText.SystemDefault);
            OutputDevices.Add(MumbleText.SystemDefault);
            foreach (var d in MumbleAudioDevices.InputNames()) InputDevices.Add(d);
            foreach (var d in MumbleAudioDevices.OutputNames()) OutputDevices.Add(d);

            InputChoice  = _service.InputDevice.Length > 0 && InputDevices.Contains(_service.InputDevice) ? _service.InputDevice : MumbleText.SystemDefault;
            OutputChoice = _service.OutputDevice.Length > 0 && OutputDevices.Contains(_service.OutputDevice) ? _service.OutputDevice : MumbleText.SystemDefault;
        }
        finally { _loadingDevices = false; }
    }

    private double _speakerVolume, _micGain;

    public double SpeakerVolume
    {
        get => _speakerVolume;
        set
        {
            this.RaiseAndSetIfChanged(ref _speakerVolume, value);
            _service.OutputVolume = value / 100.0;
            _ = _prefs.SetLongAsync(MumbleService.OutputVolumePref, (long)value);
        }
    }

    public double MicGain
    {
        get => _micGain;
        set
        {
            this.RaiseAndSetIfChanged(ref _micGain, value);
            _service.MicrophoneGain = value / 100.0;
            _ = _prefs.SetLongAsync(MumbleService.MicGainPref, (long)value);
        }
    }

    // ── The always-on-top list ────────────────────────────────────────────────

    private bool _showOverlay;
    public bool ShowOverlay
    {
        get => _showOverlay;
        set
        {
            this.RaiseAndSetIfChanged(ref _showOverlay, value);
            _ = _prefs.SetBoolAsync(OverlayPref, value);
        }
    }

    public ObservableCollection<MumbleOverlayLine> Talkers { get; } = [];

    private bool _nobodyTalking = true;
    public bool NobodyTalking { get => _nobodyTalking; private set => this.RaiseAndSetIfChanged(ref _nobodyTalking, value); }

    private void RefreshOverlay()
    {
        var now = _service.Talkers().Select(t => new MumbleOverlayLine(t.Name, t.Server, t.IsSelf, t.Mode == MumbleTalk.Shout)).ToList();

        for (var i = Talkers.Count - 1; i >= 0; i--)
            if (!now.Contains(Talkers[i])) Talkers.RemoveAt(i);
        foreach (var line in now)
            if (!Talkers.Contains(line)) Talkers.Add(line);

        NobodyTalking = Talkers.Count == 0;
    }

    public (double X, double Y)? OverlayPosition =>
        _prefs.Get(OverlayXPref) is { } x && _prefs.Get(OverlayYPref) is { } y
        && double.TryParse(x, CultureInfo.InvariantCulture, out var px) && double.TryParse(y, CultureInfo.InvariantCulture, out var py)
            ? (px, py) : null;

    public void SaveOverlayPosition(double x, double y)
    {
        _ = _prefs.SetAsync(OverlayXPref, x.ToString(CultureInfo.InvariantCulture));
        _ = _prefs.SetAsync(OverlayYPref, y.ToString(CultureInfo.InvariantCulture));
    }
}
