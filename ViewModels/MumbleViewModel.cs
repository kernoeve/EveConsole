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

    private MumbleTalk _talkMode;

    /// <summary>How they are talking, for the colour: the same as the standard client's.</summary>
    public MumbleTalk TalkMode
    {
        get => _talkMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _talkMode, value);
            this.RaisePropertyChanged(nameof(TalksChannel));
            this.RaisePropertyChanged(nameof(TalksShout));
            this.RaisePropertyChanged(nameof(TalksDirect));
        }
    }
    public bool TalksChannel => _talkMode == MumbleTalk.Channel;
    public bool TalksShout => _talkMode == MumbleTalk.Shout;
    public bool TalksDirect => _talkMode == MumbleTalk.Direct;
    public bool Good { get => _good; set => this.RaiseAndSetIfChanged(ref _good, value); }
    public bool Warn { get => _warn; set => this.RaiseAndSetIfChanged(ref _warn, value); }
    public bool Bad  { get => _bad;  set => this.RaiseAndSetIfChanged(ref _bad, value); }
    public bool IsMe { get => _isMe; set => this.RaiseAndSetIfChanged(ref _isMe, value); }

    private bool _isMuted, _isDeaf;
    public bool IsMuted { get => _isMuted; set => this.RaiseAndSetIfChanged(ref _isMuted, value); }
    public bool IsDeaf { get => _isDeaf; set => this.RaiseAndSetIfChanged(ref _isDeaf, value); }

    private bool _hasChildren, _isOpen;

    /// <summary>A channel with channels under it, which can be opened and closed.</summary>
    public bool HasChildren { get => _hasChildren; set => this.RaiseAndSetIfChanged(ref _hasChildren, value); }

    private bool _isListening, _isLocalMuted, _isPriority;

    /// <summary>Someone set as a priority speaker, whom others are turned down for.</summary>
    public bool IsPriority { get => _isPriority; set => this.RaiseAndSetIfChanged(ref _isPriority, value); }

    /// <summary>A channel this client is hearing without being in it.</summary>
    public bool IsListening { get => _isListening; set => this.RaiseAndSetIfChanged(ref _isListening, value); }

    /// <summary>Someone not heard on this client alone.</summary>
    public bool IsLocalMuted { get => _isLocalMuted; set => this.RaiseAndSetIfChanged(ref _isLocalMuted, value); }
    public bool IsOpen { get => _isOpen; set => this.RaiseAndSetIfChanged(ref _isOpen, value); }

    public void Fill(MumbleRowVm from)
    {
        Text = from.Text; Detail = from.Detail; Flags = from.Flags; Talking = from.Talking; TalkMode = from.TalkMode;
        Good = from.Good; Warn = from.Warn; Bad = from.Bad; IsMe = from.IsMe;
        IsMuted = from.IsMuted; IsDeaf = from.IsDeaf;
        HasChildren = from.HasChildren; IsOpen = from.IsOpen;
        IsListening = from.IsListening; IsLocalMuted = from.IsLocalMuted; IsPriority = from.IsPriority;
    }
}

public sealed record MumbleKeyChoice(int Key, string Label);

/// <summary>Someone on the always-on-top list: this client, or someone talking now or recently enough to still be there.</summary>
public sealed class MumbleOverlayLine(uint session, string name) : ReactiveObject
{
    public uint Session => session;
    public string Name => name;

    /// <summary>When they last had something to say, for how long they stay listed.</summary>
    public DateTime LastTalked { get; set; } = DateTime.UtcNow;

    private bool _isMuted, _isDeaf;
    private MumbleTalk _mode;

    /// <summary>How they are talking, or None. The colours are the standard client's own.</summary>
    public MumbleTalk Mode
    {
        get => _mode;
        set
        {
            this.RaiseAndSetIfChanged(ref _mode, value);
            foreach (var name in new[] { nameof(IsTalking), nameof(TalksChannel), nameof(TalksShout), nameof(TalksDirect) }) this.RaisePropertyChanged(name);
        }
    }
    public bool IsTalking => _mode != MumbleTalk.None;
    public bool TalksChannel => _mode == MumbleTalk.Channel;
    public bool TalksShout => _mode == MumbleTalk.Shout;
    public bool TalksDirect => _mode == MumbleTalk.Direct;

    public bool IsMuted { get => _isMuted; set => this.RaiseAndSetIfChanged(ref _isMuted, value); }
    public bool IsDeaf { get => _isDeaf; set => this.RaiseAndSetIfChanged(ref _isDeaf, value); }
}

/// <summary>One server on the always-on-top list: this client on it, and under that whoever is talking there.</summary>
public sealed class MumbleOverlayColumn(MumbleViewModel owner, MumbleSession session, MumbleOverlayLine me) : ReactiveObject
{
    /// <summary>For the sound settings, which are the same whichever server's gear opened them.</summary>
    public MumbleViewModel Owner => owner;
    public MumbleSession Session => session;
    public int ServerId => session.Server.Id;
    public string Server => session.Server.Name.Length > 0 ? session.Server.Name : session.Server.Host;
    public MumbleOverlayLine Me => me;

    public ObservableCollection<MumbleOverlayLine> Talking { get; } = [];

    /// <summary>This server's part of the channel tree, kept up only while its popup is open: a server can have hundreds on it.</summary>
    public ObservableCollection<MumbleRowVm> Rows { get; } = [];
    public bool IsTreeOpen => _treesOpen > 0;
    private int _treesOpen;

    /// <summary>⚠️ Counted: the toolbar's and the always-on-top window's can be open together, over these same rows.</summary>
    public void TreeOpened() => _treesOpen++;

    /// <summary>Whether that was the last one open.</summary>
    public bool TreeClosed() => --_treesOpen <= 0;

    private string _channel = "";
    private int _listening;

    /// <summary>The channel this client is in.</summary>
    public string Channel
    {
        get => _channel;
        set { this.RaiseAndSetIfChanged(ref _channel, value); this.RaisePropertyChanged(nameof(StatusTip)); }
    }

    /// <summary>How many in that channel are not deafened, this client among them if it is not.</summary>
    public int Listening
    {
        get => _listening;
        set { this.RaiseAndSetIfChanged(ref _listening, value); this.RaisePropertyChanged(nameof(StatusTip)); }
    }

    public string StatusTip => string.Format(CultureInfo.CurrentCulture, MumbleText.StatusTip, Server, Channel, Listening);

    public required ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; init; }
    public required ReactiveCommand<Unit, Unit> ToggleDeafenCommand { get; init; }
}

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

    private readonly MumbleService _service;
    private readonly AppPreferencesService _prefs;
    private int _refreshQueued;
    private Task? _refreshing;
    private bool _refreshAgain;
    private bool _loadingDetail;
    private readonly Dictionary<int, MumbleChannelTree> _trees = [];

    public MumbleViewModel(MumbleService service, AppPreferencesService prefs)
    {
        _service = service;
        _prefs   = prefs;

        CaptureWhisperCommand = ReactiveCommand.Create(() => Capture(KeyBeingSet.Whisper));
        CaptureShoutCommand   = ReactiveCommand.Create(() => Capture(KeyBeingSet.Shout));
        ClearWhisperCommand   = ReactiveCommand.Create(() => { Capture(KeyBeingSet.None); EditWhisper = ChoiceFor(0); });
        ClearShoutCommand     = ReactiveCommand.Create(() => { Capture(KeyBeingSet.None); EditShout = ChoiceFor(0); });

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
        ToggleAudioCommand    = ReactiveCommand.Create(() => { IsAudioOpen = !IsAudioOpen; if (IsAudioOpen) LoadDevices(); });

        ToggleServerMuteCommand = ReactiveCommand.CreateFromTask(() => OnPersonAsync((c, u) => c.SetServerMuteAsync(u.Session, !u.Mute, default)));
        ToggleServerDeafCommand = ReactiveCommand.CreateFromTask(() => OnPersonAsync((c, u) => c.SetServerDeafAsync(u.Session, !u.Deaf, default)));
        TogglePriorityCommand   = ReactiveCommand.CreateFromTask(() => OnPersonAsync((c, u) => c.SetPrioritySpeakerAsync(u.Session, !u.PrioritySpeaker, default)));
        ToggleLocalMuteCommand  = ReactiveCommand.CreateFromTask(() => OnPersonAsync((_, u) => _service.SetLocalMuteAsync(Session!, u, !Session!.IsLocalMuted(u))));
        ToggleIgnoreCommand     = ReactiveCommand.CreateFromTask(() => OnPersonAsync((_, u) => _service.SetIgnoredAsync(Session!, u, !Session!.IsIgnored(u))));
        MoveToMineCommand       = ReactiveCommand.CreateFromTask(MoveToMineAsync);
        MovePersonCommand       = ReactiveCommand.CreateFromTask(MovePersonAsync);
        KickCommand             = ReactiveCommand.CreateFromTask(() => RemovePersonAsync(ban: false));
        BanCommand              = ReactiveCommand.CreateFromTask(() => RemovePersonAsync(ban: true));
        RegisterCommand         = ReactiveCommand.CreateFromTask(RegisterAsync);
        SaveCommentCommand      = ReactiveCommand.CreateFromTask(SaveCommentAsync);
        ToggleListenCommand     = ReactiveCommand.CreateFromTask(ToggleListenAsync);
        CreateChannelCommand    = ReactiveCommand.CreateFromTask(CreateChannelAsync);
        RemoveChannelCommand    = ReactiveCommand.CreateFromTask(RemoveChannelAsync);
        ToggleLinkCommand       = ReactiveCommand.CreateFromTask(ToggleLinkAsync);
        SaveChannelCommand      = ReactiveCommand.CreateFromTask(SaveChannelAsync);
        ToggleBansCommand       = ReactiveCommand.CreateFromTask(ToggleBansAsync);

        // Back from the server, later, in its own time. Blank means it did not say why.
        _service.Refused += (session, why) => Dispatcher.UIThread.Post(() =>
        {
            if (Session == session) StatusText = why.Length > 0 ? MumbleService.PlainText(why) : MumbleText.Refused;
        });

        foreach (var c in new IHandleObservableErrors[]
                 {
                     SaveServerCommand, DeleteServerCommand, DisconnectCommand, TrustCertificateCommand, JoinChannelCommand,
                     ToggleMuteCommand, ToggleDeafenCommand,
                     ToggleServerMuteCommand, ToggleServerDeafCommand, TogglePriorityCommand, ToggleLocalMuteCommand, ToggleIgnoreCommand,
                     MoveToMineCommand, MovePersonCommand, KickCommand, BanCommand, RegisterCommand, SaveCommentCommand,
                     ToggleListenCommand, CreateChannelCommand, RemoveChannelCommand, ToggleLinkCommand, SaveChannelCommand, ToggleBansCommand,
                 })
            c.ThrownExceptions.Subscribe(e => StatusText = e.Message);

        _speakerVolume = _prefs.GetLong(MumbleService.OutputVolumePref, 100);
        _micGain       = _prefs.GetLong(MumbleService.MicGainPref, 100);
        _autoLevel     = _prefs.GetBool(MumbleService.AutoLevelPref, true);
        _service.AutoLevel = _autoLevel;
        _echoCancel    = _prefs.GetBool(MumbleService.EchoCancelPref);
        _noiseReduce   = _prefs.GetBool(MumbleService.NoiseReducePref);
        _service.EchoCancel = _echoCancel;
        _service.NoiseReduce = _noiseReduce;
        _talkTone      = _prefs.GetBool(MumbleService.TalkTonePref);
        _service.TalkTone = _talkTone;
        _voiceLevel    = _prefs.GetLong(MumbleService.VoiceLevelPref, (long)MumbleService.DefaultVoiceLevelDb);
        _service.VoiceLevelDb = _voiceLevel;
        _showOverlay   = _prefs.GetBool(OverlayPref);

        _service.InputDevice   = _prefs.Get(MumbleService.InputDevicePref) ?? "";
        _service.OutputDevice  = _prefs.Get(MumbleService.OutputDevicePref) ?? "";
        _service.OutputVolume  = _speakerVolume / 100.0;
        _service.MicrophoneGain = _micGain / 100.0;

        _service.SessionChanged += _ => { QueueRefresh(); Dispatcher.UIThread.Post(RefreshOverlay); };
        _service.TalkersChanged += () => { QueueRefresh(); Dispatcher.UIThread.Post(RefreshOverlay); };

        // Someone stops being listed by time passing, and their mute can change while they are quiet.
        _overlayTimer = new Timer(_ => Dispatcher.UIThread.Post(() =>
        {
            RefreshOverlay();
            if (IsAudioOpen) this.RaisePropertyChanged(nameof(MicLevelText));
        }), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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
        var talking = _service.Talkers().ToDictionary(t => (t.ServerId, t.Session), t => t.Mode);
        var wanted  = new List<MumbleRowVm>();

        foreach (var session in _service.Sessions) AddServer(wanted, session, talking);

        var chosen = SelectedRow?.Identity;
        Sync(wanted);
        foreach (var column in OverlayColumns)
            if (column.IsTreeOpen) FillTree(column);

        // ⚠️ Someone moving gets a new row, at their new depth. Whoever was selected stays selected.
        if (chosen is not null && (SelectedRow is null || !Rows.Contains(SelectedRow)) && Rows.FirstOrDefault(r => r.Identity == chosen) is { } again)
            SelectedRow = again;
        HasServers = _service.Sessions.Count > 0;
        ShowDetail();
    }

    /// <summary>Opens or closes a channel in the list, at the person's own click.</summary>
    public void ToggleChannel(MumbleRowVm row)
    {
        if (row is not { IsChannel: true, HasChildren: true } || !_trees.TryGetValue(row.ServerId, out var tree)) return;

        tree.Toggle(row.Id);
        _ = RefreshAsync();
    }

    private void AddServer(List<MumbleRowVm> rows, MumbleSession session, Dictionary<(int, uint), MumbleTalk> talking)
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

        // A new connection starts from who is there then, not from what was opened on the last one.
        if (session.State != MumbleState.Connected || session.Client is not { } client)
        {
            _trees.Remove(id);
            return;
        }

        // ⚠️ Looked up, not searched: this runs each time someone starts or stops talking, on servers of hundreds.
        var channels = client.Channels.ToDictionary(c => c.Id);
        var children = client.Channels.Where(c => c.Parent is not null).ToLookup(c => c.Parent!.Value);
        var users    = client.Users.ToLookup(u => u.ChannelId);
        var me       = client.Session;
        var shown    = new HashSet<uint>();

        if (!_trees.TryGetValue(id, out var tree)) _trees[id] = tree = new MumbleChannelTree();
        tree.Update(client.Channels.Select(c => (c.Id, c.Parent)), client.Users.Select(u => u.ChannelId), client.Me?.ChannelId);

        void Add(uint channel, int depth)
        {
            // However a server describes its channels, this cannot go round in circles or too deep for the stack.
            if (depth > MostDepth || !shown.Add(channel) || !channels.TryGetValue(channel, out var c)) return;

            var hasChildren = children[channel].Any();
            var open = tree.IsOpen(channel);

            rows.Add(new MumbleRowVm
            {
                Kind = MumbleRowKind.Channel, ServerId = id, Id = c.Id, Depth = depth,
                Text = c.Name.Length > 0 ? c.Name : MumbleText.RootChannel,
                HasChildren = hasChildren, IsOpen = open,
                IsListening = client.Me?.ListeningTo.Contains(c.Id) == true,
            });

            // With nothing under it there is nothing to close it with, so whoever is in it always shows.
            if (hasChildren && !open) return;

            foreach (var u in users[channel].OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase))
                rows.Add(new MumbleRowVm
                {
                    Kind = MumbleRowKind.User, ServerId = id, Id = u.Session, Depth = depth + 1,
                    Text = u.Name, IsMe = u.Session == me,
                    Talking = talking.ContainsKey((id, u.Session)),
                    TalkMode = talking.GetValueOrDefault((id, u.Session)),
                    Flags = u.Deaf || u.SelfDeaf ? MumbleText.FlagDeaf : u.Mute || u.SelfMute || u.Suppress ? MumbleText.FlagMuted : "",
                    IsDeaf = u.Deaf || u.SelfDeaf,
                    IsMuted = u.Mute || u.SelfMute || u.Suppress,
                    IsLocalMuted = session.IsLocalMuted(u),
                    IsPriority = u.PrioritySpeaker,
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

    public static string StateText(MumbleSession session) => session.State switch
    {
        MumbleState.Connected    => session.Client?.Me is { } me && session.Client.ChannelOf(me.ChannelId) is { } c && c.Name.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, MumbleText.StateInChannel, c.Name) : MumbleText.StateConnected,
        MumbleState.Connecting   => MumbleText.StateConnecting,
        MumbleState.Reconnecting => session.NextAttempt is { } next
            ? string.Format(CultureInfo.CurrentCulture, MumbleText.StateRetryAt, ChatTime.Clock(next))
            : MumbleText.StateConnecting,
        MumbleState.Failed => session.Problem switch
        {
            MumbleProblem.Certificate => MumbleText.ProblemCertificate,
            MumbleProblem.Password    => MumbleText.ProblemPassword,
            MumbleProblem.Username    => MumbleText.ProblemUsername,
            MumbleProblem.Removed     => string.Format(CultureInfo.CurrentCulture, MumbleText.ProblemRemoved, session.Detail),
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

    private string _title = "", _subtitle = "", _serverState = "", _serverDetail = "", _certificateText = "", _status = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => this.RaiseAndSetIfChanged(ref _subtitle, value); }
    public string ServerState { get => _serverState; private set => this.RaiseAndSetIfChanged(ref _serverState, value); }
    public string ServerDetail { get => _serverDetail; private set => this.RaiseAndSetIfChanged(ref _serverDetail, value); }
    public string CertificateText { get => _certificateText; private set => this.RaiseAndSetIfChanged(ref _certificateText, value); }
    public string StatusText { get => _status; set { this.RaiseAndSetIfChanged(ref _status, value); this.RaisePropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;

    private bool _isConnected, _canConnect, _hasCertificateProblem, _isMuted, _isDeafened;
    public bool IsConnected { get => _isConnected; private set => this.RaiseAndSetIfChanged(ref _isConnected, value); }
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
        foreach (var name in new[] { nameof(ShowServer), nameof(ShowChannel), nameof(ShowUser) })
            this.RaisePropertyChanged(name);

        StatusText = "";

        // What is being written belongs to who it was for.
        AdminReason = "";
        MoveTarget = "";
        NewChannelName = "";
        _commentLoadedFor = null;
        _channelLoadedFor = null;
        ShowBans = false;
        if (SelectedRow is { IsUser: true } picked && Session?.Client is { } asked) asked.AskStats(picked.Id);
        FillMoveChoices();
        ShowDetail();
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
            IsMuted      = server.Silent;
            IsDeafened   = server.SelfDeafened;

            HasCertificateProblem = session.Problem == MumbleProblem.Certificate && session.CertificateFingerprint.Length > 0;
            CertificateText = HasCertificateProblem
                ? string.Format(CultureInfo.CurrentCulture, MumbleText.CertificateAsk, session.CertificateSubject,
                                string.Join(' ', session.CertificateFingerprint.Chunk(4).Select(c => new string(c))))
                : "";

            KeysText = string.Format(CultureInfo.CurrentCulture, MumbleText.KeysSummary, KeyName(server.WhisperKey), KeyName(server.ShoutKey));
            ReachText = ReachOf(session);
            ShowServerAbout(session);

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
                    if (channel is not null && session.Client is { } here) ShowChannelControls(here, channel);
                    break;

                case MumbleRowKind.User:
                    var user = session.Client?.UserOf(row.Id);
                    Title = row.Text;
                    Subtitle = "";
                    SelectedIsMe = row.IsMe;
                    if (user is not null)
                    {
                        UserVolume = session.VolumeOf(user) * 100;
                        if (session.Client is { } there) ShowPersonControls(session, there, user, row);
                    }
                    break;
            }
        }
        finally { _loadingDetail = false; }
    }

    private static string KeyName(int key) => key == 0 ? MumbleText.KeyNone : GlobalHotkeyService.VkName(key);

    // ── What can be done to a person ──────────────────────────────────────────

    private bool _canServerMute, _canMovePerson, _canKick, _canBan, _canRegister, _isServerMuted, _isServerDeaf, _isPriority, _isPersonLocalMuted, _isPersonIgnored;
    private string _personStatus = "", _personComment = "", _myComment = "", _adminReason = "", _moveTarget = "";
    private uint? _commentLoadedFor;

    /// <summary>Each by what the server says this client may do, so nothing is offered only to be refused.</summary>
    public bool CanServerMute { get => _canServerMute; private set { this.RaiseAndSetIfChanged(ref _canServerMute, value); this.RaisePropertyChanged(nameof(ShowPersonAdmin)); } }
    public bool CanMovePerson { get => _canMovePerson; private set { this.RaiseAndSetIfChanged(ref _canMovePerson, value); this.RaisePropertyChanged(nameof(ShowPersonAdmin)); } }
    public bool CanKick { get => _canKick; private set { this.RaiseAndSetIfChanged(ref _canKick, value); this.RaisePropertyChanged(nameof(ShowPersonAdmin)); this.RaisePropertyChanged(nameof(CanRemovePerson)); } }
    public bool CanBan { get => _canBan; private set { this.RaiseAndSetIfChanged(ref _canBan, value); this.RaisePropertyChanged(nameof(ShowPersonAdmin)); this.RaisePropertyChanged(nameof(CanRemovePerson)); } }
    public bool CanRegister { get => _canRegister; private set => this.RaiseAndSetIfChanged(ref _canRegister, value); }
    public bool CanRemovePerson => CanKick || CanBan;
    public bool ShowPersonAdmin => CanServerMute || CanMovePerson || CanKick || CanBan;

    public bool IsServerMuted { get => _isServerMuted; private set => this.RaiseAndSetIfChanged(ref _isServerMuted, value); }
    public bool IsServerDeaf { get => _isServerDeaf; private set => this.RaiseAndSetIfChanged(ref _isServerDeaf, value); }
    public bool IsPriority { get => _isPriority; private set => this.RaiseAndSetIfChanged(ref _isPriority, value); }
    public bool IsPersonLocalMuted { get => _isPersonLocalMuted; private set => this.RaiseAndSetIfChanged(ref _isPersonLocalMuted, value); }
    public bool IsPersonIgnored { get => _isPersonIgnored; private set => this.RaiseAndSetIfChanged(ref _isPersonIgnored, value); }

    public string PersonStatus { get => _personStatus; private set { this.RaiseAndSetIfChanged(ref _personStatus, value); this.RaisePropertyChanged(nameof(HasPersonStatus)); } }
    public bool HasPersonStatus => PersonStatus.Length > 0;
    public string PersonComment { get => _personComment; private set { this.RaiseAndSetIfChanged(ref _personComment, value); this.RaisePropertyChanged(nameof(HasPersonComment)); } }
    public bool HasPersonComment => PersonComment.Length > 0 && !SelectedIsMe;

    /// <summary>What this client says about itself, being edited.</summary>
    public string MyComment { get => _myComment; set => this.RaiseAndSetIfChanged(ref _myComment, value); }
    public string AdminReason { get => _adminReason; set => this.RaiseAndSetIfChanged(ref _adminReason, value); }
    public string MoveTarget { get => _moveTarget; set => this.RaiseAndSetIfChanged(ref _moveTarget, value); }

    public ObservableCollection<string> MoveChoices { get; } = [];
    private readonly Dictionary<string, uint> _moveTargets = new(StringComparer.CurrentCultureIgnoreCase);

    public ReactiveCommand<Unit, Unit> ToggleServerMuteCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleServerDeafCommand { get; }
    public ReactiveCommand<Unit, Unit> TogglePriorityCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLocalMuteCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleIgnoreCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveToMineCommand { get; }
    public ReactiveCommand<Unit, Unit> MovePersonCommand { get; }
    public ReactiveCommand<Unit, Unit> KickCommand { get; }
    public ReactiveCommand<Unit, Unit> BanCommand { get; }
    public ReactiveCommand<Unit, Unit> RegisterCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommentCommand { get; }

    private void ShowPersonControls(MumbleSession session, MumbleClient client, MumbleUser user, MumbleRowVm row)
    {
        client.AskPermissions(user.ChannelId);
        client.AskPermissions(0);
        client.AskComment(user);

        var here = client.PermissionsIn(user.ChannelId) ?? MumblePermission.None;
        var root = client.PermissionsIn(0) ?? MumblePermission.None;

        CanServerMute = here.HasFlag(MumblePermission.MuteDeafen);
        CanMovePerson = here.HasFlag(MumblePermission.Move) && !row.IsMe;
        CanKick = root.HasFlag(MumblePermission.Kick) && !row.IsMe;
        CanBan = root.HasFlag(MumblePermission.Ban) && !row.IsMe;
        CanRegister = user.UserId is null && root.HasFlag(row.IsMe ? MumblePermission.SelfRegister : MumblePermission.Register);

        IsServerMuted = user.Mute;
        IsServerDeaf = user.Deaf;
        IsPriority = user.PrioritySpeaker;
        IsPersonLocalMuted = session.IsLocalMuted(user);
        IsPersonIgnored = session.IsIgnored(user);

        PersonStatus = string.Join(" \u00B7 ", new[]
        {
            user.UserId is not null ? MumbleText.PersonRegistered : "",
            user.Mute ? MumbleText.PersonServerMuted : "",
            user.Deaf ? MumbleText.PersonServerDeafened : "",
            user.SelfDeaf ? MumbleText.PersonSelfDeafened : user.SelfMute ? MumbleText.PersonSelfMuted : "",
            user.Suppress ? MumbleText.PersonSuppressed : "",
            user.PrioritySpeaker ? MumbleText.PersonPriority : "",
            user.Recording ? MumbleText.PersonRecording : "",
        }.Where(s => s.Length > 0));

        PersonInfo = PersonInfoOf(user);
        PersonComment = MumbleService.PlainText(user.Comment);
        this.RaisePropertyChanged(nameof(HasPersonComment));

        // Not while it is being written: this runs whenever anything on the server changes.
        if (_commentLoadedFor != row.Id && (row.IsMe || user.Comment.Length > 0))
        {
            _commentLoadedFor = row.Id;
            if (row.IsMe) MyComment = PersonComment;
        }
    }

    private string _personInfo = "";
    public string PersonInfo { get => _personInfo; private set { this.RaiseAndSetIfChanged(ref _personInfo, value); this.RaisePropertyChanged(nameof(HasPersonInfo)); } }
    public bool HasPersonInfo => PersonInfo.Length > 0;

    /// <summary>Whatever the server was willing to say about them: how much depends on what this client may do.</summary>
    private static string PersonInfoOf(MumbleUser user) => string.Join(" \u00B7 ", new[]
    {
        user.OnlineSeconds is { } online ? string.Format(CultureInfo.CurrentCulture, MumbleText.PersonOnlineFor, Span(online)) : "",
        user.IdleSeconds is { } idle ? string.Format(CultureInfo.CurrentCulture, MumbleText.PersonIdleFor, Span(idle)) : "",
        user.ClientRelease.Length > 0 ? (user.ClientOs.Length > 0 ? $"{MumbleService.PlainText(user.ClientRelease)} ({MumbleService.PlainText(user.ClientOs)})" : MumbleService.PlainText(user.ClientRelease)) : "",
    }.Where(s => s.Length > 0));

    private static string Span(uint seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} m" : time.TotalMinutes >= 1 ? $"{time.Minutes} m" : $"{time.Seconds} s";
    }

    // ── What is shown about a server, and who it has banned ───────────────────

    private string _serverAbout = "", _serverWelcome = "";
    private bool _canSeeBans, _showBans;
    private MumbleClient? _bansFrom;

    public string ServerAbout { get => _serverAbout; private set => this.RaiseAndSetIfChanged(ref _serverAbout, value); }
    public string ServerWelcome { get => _serverWelcome; private set { this.RaiseAndSetIfChanged(ref _serverWelcome, value); this.RaisePropertyChanged(nameof(HasServerWelcome)); } }
    public bool HasServerWelcome => ServerWelcome.Length > 0;
    public bool CanSeeBans { get => _canSeeBans; private set => this.RaiseAndSetIfChanged(ref _canSeeBans, value); }
    public bool ShowBans { get => _showBans; private set { this.RaiseAndSetIfChanged(ref _showBans, value); this.RaisePropertyChanged(nameof(HasNoBans)); } }
    public bool HasNoBans => ShowBans && Bans.Count == 0;

    public ObservableCollection<MumbleBan> Bans { get; } = [];

    public ReactiveCommand<Unit, Unit> ToggleBansCommand { get; }

    private void ShowServerAbout(MumbleSession session)
    {
        if (session.State != MumbleState.Connected || session.Client is not { } client)
        {
            ServerAbout = ServerWelcome = "";
            CanSeeBans = false;
            return;
        }

        ServerAbout = string.Format(CultureInfo.CurrentCulture, MumbleText.ServerAbout,
            MumbleService.PlainText(client.ServerRelease), (client.MaxBandwidth / 1000).ToString("N0", CultureInfo.CurrentCulture),
            client.Users.Count.ToString("N0", CultureInfo.CurrentCulture));

        var welcome = MumbleService.PlainText(client.WelcomeText);
        ServerWelcome = welcome.Length > 800 ? welcome[..800] + "\u2026" : welcome;

        CanSeeBans = (client.PermissionsIn(0) ?? MumblePermission.None).HasFlag(MumblePermission.Ban);
    }

    private async Task ToggleBansAsync()
    {
        if (ShowBans) { ShowBans = false; return; }
        if (Session?.Client is not { } client) return;

        if (!ReferenceEquals(_bansFrom, client))
        {
            _bansFrom = client;
            client.BansReceived += bans => Dispatcher.UIThread.Post(() =>
            {
                Bans.Clear();
                foreach (var ban in bans.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)) Bans.Add(ban);
                this.RaisePropertyChanged(nameof(HasNoBans));
            });
        }

        Bans.Clear();
        ShowBans = true;
        await Task.Run(() => client.AskBansAsync(default));
    }

    /// <summary>Lifts one ban. ⚠️ The whole list goes back, as that is how a server takes it, so it must be the list just read.</summary>
    public async Task UnbanAsync(MumbleBan ban)
    {
        if (Session?.Client is not { } client) return;
        if (Confirm is null || !await Confirm(string.Format(CultureInfo.CurrentCulture, MumbleText.ConfirmUnban, ban.Name))) return;

        Bans.Remove(ban);
        this.RaisePropertyChanged(nameof(HasNoBans));
        var left = Bans.ToList();
        await Task.Run(() => client.SetBansAsync(left, default));
    }

    public static string BanText(MumbleBan ban) => string.Join(" \u00B7 ", new[]
    {
        ban.Address,
        ChatTime.Of(ban.Started) is { Length: > 0 } when ? when : ban.Started,
        MumbleService.PlainText(ban.Reason),
    }.Where(s => s.Length > 0));

    private async Task OnPersonAsync(Func<MumbleClient, MumbleUser, Task> act)
    {
        if (SelectedRow is { IsUser: true } row && Session?.Client is { } client && client.UserOf(row.Id) is { } user)
            await Task.Run(() => act(client, user));
    }

    private void FillMoveChoices()
    {
        MoveChoices.Clear();
        _moveTargets.Clear();
        if (SelectedRow is not { IsUser: true } || Session?.Client is not { } client) return;

        foreach (var channel in client.Channels)
        {
            var path = MumbleService.PathOf(client, channel.Id);
            if (path.Length == 0) path = channel.Name;
            _moveTargets[path] = channel.Id;
        }
        foreach (var path in _moveTargets.Keys.Order(StringComparer.CurrentCultureIgnoreCase)) MoveChoices.Add(path);
    }

    private Task MoveToMineAsync() => OnPersonAsync((c, u) => c.Me is { } me ? c.MoveUserAsync(u.Session, me.ChannelId, default) : Task.CompletedTask);

    private async Task MovePersonAsync()
    {
        if (!_moveTargets.TryGetValue(MoveTarget.Trim(), out var channel))
        {
            StatusText = MumbleText.MoveNoSuchChannel;
            return;
        }

        await OnPersonAsync((c, u) => c.MoveUserAsync(u.Session, channel, default));
        MoveTarget = "";
    }

    private async Task RemovePersonAsync(bool ban)
    {
        if (SelectedRow is not { IsUser: true } row) return;

        var ask = string.Format(CultureInfo.CurrentCulture, ban ? MumbleText.ConfirmBan : MumbleText.ConfirmKick, row.Text);
        if (Confirm is null || !await Confirm(ask)) return;

        var reason = AdminReason.Trim();
        await OnPersonAsync((c, u) => c.KickOrBanAsync(u.Session, reason, ban, default));
        AdminReason = "";
    }

    private async Task RegisterAsync()
    {
        if (SelectedRow is not { IsUser: true } row) return;
        if (Confirm is null || !await Confirm(string.Format(CultureInfo.CurrentCulture, MumbleText.ConfirmRegister, row.Text))) return;

        await OnPersonAsync((c, u) => c.RegisterUserAsync(u.Session, default));
    }

    private async Task SaveCommentAsync()
    {
        if (Session?.Client is not { } client) return;

        var html = System.Net.WebUtility.HtmlEncode(MyComment.Trim()).Replace("\n", "<br>", StringComparison.Ordinal);
        await Task.Run(() => client.SetCommentAsync(html, default));
    }

    // ── What can be done to a channel ─────────────────────────────────────────

    private bool _canListen, _isListening, _canMakeChannel, _canMakeTempChannel, _canRemoveChannel, _canLinkChannel, _isLinkedToMine, _newChannelTemporary;
    private string _newChannelName = "";

    public bool CanListen { get => _canListen; private set => this.RaiseAndSetIfChanged(ref _canListen, value); }
    public bool IsListening { get => _isListening; private set { this.RaiseAndSetIfChanged(ref _isListening, value); this.RaisePropertyChanged(nameof(ListenText)); } }
    public string ListenText => IsListening ? MumbleText.StopListening : MumbleText.Listen;

    public bool CanMakeChannel { get => _canMakeChannel; private set { this.RaiseAndSetIfChanged(ref _canMakeChannel, value); this.RaisePropertyChanged(nameof(ShowNewChannel)); } }
    public bool CanMakeTempChannel { get => _canMakeTempChannel; private set { this.RaiseAndSetIfChanged(ref _canMakeTempChannel, value); this.RaisePropertyChanged(nameof(ShowNewChannel)); } }
    public bool ShowNewChannel => CanMakeChannel || CanMakeTempChannel;
    public bool CanChooseTemporary => CanMakeChannel && CanMakeTempChannel;

    public bool CanRemoveChannel { get => _canRemoveChannel; private set => this.RaiseAndSetIfChanged(ref _canRemoveChannel, value); }
    public bool CanLinkChannel { get => _canLinkChannel; private set => this.RaiseAndSetIfChanged(ref _canLinkChannel, value); }
    public bool IsLinkedToMine { get => _isLinkedToMine; private set { this.RaiseAndSetIfChanged(ref _isLinkedToMine, value); this.RaisePropertyChanged(nameof(LinkText)); } }
    public string LinkText => IsLinkedToMine ? MumbleText.UnlinkFromMine : MumbleText.LinkToMine;

    public string NewChannelName { get => _newChannelName; set => this.RaiseAndSetIfChanged(ref _newChannelName, value); }
    public bool NewChannelTemporary { get => _newChannelTemporary; set => this.RaiseAndSetIfChanged(ref _newChannelTemporary, value); }

    public ReactiveCommand<Unit, Unit> ToggleListenCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLinkCommand { get; }

    private string _editChannelName = "", _editChannelDescription = "", _loadedChannelName = "", _loadedChannelDescription = "";
    private uint? _channelLoadedFor;

    public string EditChannelName { get => _editChannelName; set => this.RaiseAndSetIfChanged(ref _editChannelName, value); }
    public string EditChannelDescription { get => _editChannelDescription; set => this.RaiseAndSetIfChanged(ref _editChannelDescription, value); }
    public ReactiveCommand<Unit, Unit> SaveChannelCommand { get; }

    private async Task SaveChannelAsync()
    {
        if (SelectedRow is not { IsChannel: true } row || Session?.Client is not { } client) return;

        // ⚠️ Only what was changed: a description written by hand with formatting is shown here as plain words, and
        // sending that back unasked would lose the formatting.
        var name = EditChannelName.Trim();
        var description = EditChannelDescription.Trim();
        var newName = name.Length > 0 && name != _loadedChannelName ? name : null;
        var newDescription = description != _loadedChannelDescription
            ? System.Net.WebUtility.HtmlEncode(description).Replace("\n", "<br>", StringComparison.Ordinal) : null;
        if (newName is null && newDescription is null) return;

        await Task.Run(() => client.EditChannelAsync(row.Id, newName, newDescription, default));
        _loadedChannelName = newName ?? _loadedChannelName;
        _loadedChannelDescription = newDescription is null ? _loadedChannelDescription : description;
    }

    private void ShowChannelControls(MumbleClient client, MumbleChannel channel)
    {
        // Not while it is being written: this runs whenever anything on the server changes. A description may arrive late.
        var description = MumbleService.PlainText(channel.Description);
        if (_channelLoadedFor != channel.Id || (_editChannelDescription == _loadedChannelDescription && description != _loadedChannelDescription))
        {
            _channelLoadedFor = channel.Id;
            _loadedChannelName = channel.Name;
            _loadedChannelDescription = description;
            EditChannelName = channel.Name;
            EditChannelDescription = description;
        }

        client.AskPermissions(channel.Id);
        client.AskDescription(channel);

        var here = client.PermissionsIn(channel.Id) ?? MumblePermission.None;
        var me = client.Me;

        if (me is not null) client.AskPermissions(me.ChannelId);
        var mine = me is null ? MumblePermission.None : client.PermissionsIn(me.ChannelId) ?? MumblePermission.None;

        CanListen = here.HasFlag(MumblePermission.Listen) && me?.ChannelId != channel.Id;
        IsListening = me?.ListeningTo.Contains(channel.Id) == true;

        CanMakeChannel = here.HasFlag(MumblePermission.MakeChannel);
        CanMakeTempChannel = here.HasFlag(MumblePermission.MakeTempChannel);
        this.RaisePropertyChanged(nameof(CanChooseTemporary));
        CanRemoveChannel = here.HasFlag(MumblePermission.Write) && channel.Id != 0;

        CanLinkChannel = me is not null && me.ChannelId != channel.Id && here.HasFlag(MumblePermission.LinkChannel) && mine.HasFlag(MumblePermission.LinkChannel);
        IsLinkedToMine = me is not null && channel.Links.Contains(me.ChannelId);
    }

    private async Task ToggleListenAsync()
    {
        if (SelectedRow is not { IsChannel: true } row || Session?.Client is not { } client) return;

        var listen = client.Me?.ListeningTo.Contains(row.Id) != true;
        await Task.Run(() => client.SetListeningAsync(row.Id, listen, default));
    }

    private async Task CreateChannelAsync()
    {
        var name = NewChannelName.Trim();
        if (name.Length == 0 || SelectedRow is not { IsChannel: true } row || Session?.Client is not { } client) return;

        // Only one sort is open to them: that is the sort it is.
        var temporary = CanChooseTemporary ? NewChannelTemporary : CanMakeTempChannel;
        await Task.Run(() => client.CreateChannelAsync(row.Id, name, temporary, default));
        NewChannelName = "";
    }

    private async Task RemoveChannelAsync()
    {
        if (SelectedRow is not { IsChannel: true } row || Session?.Client is not { } client) return;
        if (Confirm is null || !await Confirm(string.Format(CultureInfo.CurrentCulture, MumbleText.ConfirmRemoveChannel, row.Text))) return;

        await Task.Run(() => client.RemoveChannelAsync(row.Id, default));
    }

    private async Task ToggleLinkAsync()
    {
        if (SelectedRow is not { IsChannel: true } row || Session?.Client is not { Me: { } me } client) return;

        var linked = client.ChannelOf(row.Id)?.Links.Contains(me.ChannelId) != true;
        await Task.Run(() => client.SetChannelLinkAsync(row.Id, me.ChannelId, linked, default));
    }

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

    /// <summary>Opens a conversation in the Messages tool: server, who it is filed under, and their name.</summary>
    public Action<int, string, string>? OpenDirect { get; set; }

    /// <summary>Opens the conversation with the person selected. Not for this client itself.</summary>
    public void MessageSelectedPerson()
    {
        if (SelectedRow is { IsUser: true, IsMe: false } row && Session is { } session && session.Client?.UserOf(row.Id) is { } person)
            OpenDirect?.Invoke(session.Server.Id, MumbleService.UserKey(person), person.Name);
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
        if (Session is { } session) await ToggleMuteOf(session);
    }

    private async Task ToggleDeafenAsync()
    {
        if (Session is { } session) await ToggleDeafenOf(session);
    }

    private Task ToggleMuteOf(MumbleSession session) => Task.Run(() => _service.ToggleMuteAsync(session));
    private Task ToggleDeafenOf(MumbleSession session) => Task.Run(() => _service.ToggleDeafenAsync(session));

    /// <summary>Set by the view: asks before something that cannot be undone.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    // ── The server editor ─────────────────────────────────────────────────────

    private enum KeyBeingSet { None, Whisper, Shout }
    private KeyBeingSet _keyBeingSet;

    public ReactiveCommand<Unit, Unit> CaptureWhisperCommand { get; }
    public ReactiveCommand<Unit, Unit> CaptureShoutCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearWhisperCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearShoutCommand { get; }

    public static MumbleKeyChoice ChoiceFor(int vk) => new(vk, vk == 0 ? MumbleText.KeyNone : GlobalHotkeyService.VkName(vk));

    /// <summary>The next key or button pressed in this tool goes to one of the two talk keys.</summary>
    public bool IsCapturingKey => _keyBeingSet != KeyBeingSet.None;

    public string WhisperKeyText => _keyBeingSet == KeyBeingSet.Whisper ? MumbleText.KeyPressNow : EditWhisper?.Label ?? MumbleText.KeyNone;
    public string ShoutKeyText   => _keyBeingSet == KeyBeingSet.Shout ? MumbleText.KeyPressNow : EditShout?.Label ?? MumbleText.KeyNone;

    private void Capture(KeyBeingSet which)
    {
        _keyBeingSet = which;
        this.RaisePropertyChanged(nameof(IsCapturingKey));
        this.RaisePropertyChanged(nameof(WhisperKeyText));
        this.RaisePropertyChanged(nameof(ShoutKeyText));
    }

    public void CancelKeyCapture() => Capture(KeyBeingSet.None);

    /// <summary>Takes what was pressed for the talk key being chosen. Zero is something that cannot be used: it keeps waiting.</summary>
    public void TakeCapturedKey(int vk)
    {
        if (vk == 0) return;

        if (_keyBeingSet == KeyBeingSet.Whisper) EditWhisper = ChoiceFor(vk);
        else if (_keyBeingSet == KeyBeingSet.Shout) EditShout = ChoiceFor(vk);
        Capture(KeyBeingSet.None);
    }

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            this.RaiseAndSetIfChanged(ref _isEditing, value);
            foreach (var name in new[] { nameof(ShowServer), nameof(ShowChannel), nameof(ShowUser), nameof(ShowWelcome) })
                this.RaisePropertyChanged(name);
        }
    }

    private int _editId;
    public bool EditIsExisting => _editId != 0;

    private string _editName = "", _editHost = "", _editUsername = "", _editPassword = "", _editTokens = "";
    private int _editPort = 64738;
    private bool _editAutoConnect, _editVoiceActivated;
    public bool EditVoiceActivated { get => _editVoiceActivated; set => this.RaiseAndSetIfChanged(ref _editVoiceActivated, value); }
    private MumbleKeyChoice? _editWhisper, _editShout;

    public string EditName { get => _editName; set => this.RaiseAndSetIfChanged(ref _editName, value); }
    public string EditHost { get => _editHost; set => this.RaiseAndSetIfChanged(ref _editHost, value); }
    public int EditPort { get => _editPort; set => this.RaiseAndSetIfChanged(ref _editPort, value); }
    public string EditUsername { get => _editUsername; set => this.RaiseAndSetIfChanged(ref _editUsername, value); }
    public string EditPassword { get => _editPassword; set => this.RaiseAndSetIfChanged(ref _editPassword, value); }
    public string EditTokens { get => _editTokens; set => this.RaiseAndSetIfChanged(ref _editTokens, value); }
    public bool EditAutoConnect { get => _editAutoConnect; set => this.RaiseAndSetIfChanged(ref _editAutoConnect, value); }
    public MumbleKeyChoice? EditWhisper
    {
        get => _editWhisper;
        set { this.RaiseAndSetIfChanged(ref _editWhisper, value); this.RaisePropertyChanged(nameof(WhisperKeyText)); }
    }

    public MumbleKeyChoice? EditShout
    {
        get => _editShout;
        set { this.RaiseAndSetIfChanged(ref _editShout, value); this.RaisePropertyChanged(nameof(ShoutKeyText)); }
    }

    public string PasswordNote => SecretStore.Description;

    private void BeginAdd()
    {
        _editId = 0;
        EditName = EditHost = EditUsername = EditPassword = EditTokens = "";
        EditPort = 64738;
        EditAutoConnect = false;
        EditVoiceActivated = false;
        EditWhisper = EditShout = ChoiceFor(0);
        CancelKeyCapture();
        this.RaisePropertyChanged(nameof(EditIsExisting));
        IsEditing = true;
    }

    private void BeginEdit()
    {
        if (Session?.Server is not { } s) return;

        _editId = s.Id;
        EditName = s.Name; EditHost = s.Host; EditPort = s.Port; EditUsername = s.Username; EditPassword = ""; EditTokens = s.Tokens;
        EditAutoConnect = MumbleService.AutoConnectsHere(s.Id);
        EditVoiceActivated = s.VoiceActivated;
        EditWhisper = ChoiceFor(s.WhisperKey);
        EditShout   = ChoiceFor(s.ShoutKey);
        CancelKeyCapture();
        this.RaisePropertyChanged(nameof(EditIsExisting));
        IsEditing = true;
    }

    private async Task SaveAsync()
    {
        CancelKeyCapture();
        if (EditHost.Trim().Length == 0 || EditUsername.Trim().Length == 0) { StatusText = MumbleText.HostAndNameRequired; return; }

        var old = _editId != 0 ? _service.SessionOf(_editId)?.Server : null;
        var server = new MumbleServer
        {
            Id = _editId, Name = EditName.Trim(), Host = EditHost.Trim(),
            Port = EditPort is > 0 and < 65536 ? EditPort : 64738,
            Username = EditUsername.Trim(), Tokens = EditTokens.Trim(), VoiceActivated = EditVoiceActivated,
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
        MumbleService.SetAutoConnectHere(session.Server.Id, EditAutoConnect);
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

    /// <summary>A fresh reading of the devices. ⚠️ In place: the settings and the overlay's popup can both be showing these lists.</summary>
    public void LoadDevices()
    {
        _loadingDevices = true;
        try
        {
            ListSync.Sync(InputDevices, [MumbleText.SystemDefault, .. MumbleAudioDevices.InputNames()], d => d);
            ListSync.Sync(OutputDevices, [MumbleText.SystemDefault, .. MumbleAudioDevices.OutputNames()], d => d);

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

    private bool _talkTone;
    public bool TalkTone
    {
        get => _talkTone;
        set
        {
            this.RaiseAndSetIfChanged(ref _talkTone, value);
            _service.TalkTone = value;
            _ = _prefs.SetBoolAsync(MumbleService.TalkTonePref, value);
        }
    }

    private bool _echoCancel, _noiseReduce;
    private double _voiceLevel;

    /// <summary>Whether echo cancelling can run on this machine: its native library may not load.</summary>
    public bool EchoCancelAvailable => MumbleEchoCanceller.Available;

    public bool EchoCancel
    {
        get => _echoCancel;
        set
        {
            this.RaiseAndSetIfChanged(ref _echoCancel, value);
            _service.EchoCancel = value;
            _ = _prefs.SetBoolAsync(MumbleService.EchoCancelPref, value);
        }
    }

    public bool NoiseReduce
    {
        get => _noiseReduce;
        set
        {
            this.RaiseAndSetIfChanged(ref _noiseReduce, value);
            _service.NoiseReduce = value;
            _ = _prefs.SetBoolAsync(MumbleService.NoiseReducePref, value);
        }
    }

    /// <summary>How loud a voice must be to count, in dB, for any server set to talk by voice.</summary>
    public double VoiceLevel
    {
        get => _voiceLevel;
        set
        {
            this.RaiseAndSetIfChanged(ref _voiceLevel, value);
            this.RaisePropertyChanged(nameof(VoiceLevelText));
            _service.VoiceLevelDb = value;
            _ = _prefs.SetLongAsync(MumbleService.VoiceLevelPref, (long)value);
        }
    }
    public string VoiceLevelText => $"{VoiceLevel:N0} dB";
    public string MicLevelText => string.Format(CultureInfo.CurrentCulture, MumbleText.MicLevelNow, _service.MicLevelDb.ToString("N0", CultureInfo.CurrentCulture));

    private bool _autoLevel;
    public bool AutoLevelVoice
    {
        get => _autoLevel;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoLevel, value);
            _service.AutoLevel = value;
            _ = _prefs.SetBoolAsync(MumbleService.AutoLevelPref, value);
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

    public ObservableCollection<MumbleOverlayColumn> OverlayColumns { get; } = [];

    /// <summary>The standard client's own default for how long someone stays in its talking list.</summary>
    private static readonly TimeSpan KeptAfterTalking = TimeSpan.FromSeconds(10);

    private readonly Timer _overlayTimer;

    private bool _nobodyTalking = true;
    public bool NobodyTalking { get => _nobodyTalking; private set => this.RaiseAndSetIfChanged(ref _nobodyTalking, value); }

    /// <summary>The list changed in a way that changes how big it is. The window has to be told: see <see cref="MumbleOverlayWindow"/>.</summary>
    public event Action? OverlayChanged;

    private string _overlayShape = "";

    private void RefreshOverlay()
    {
        var talking = _service.Talkers();
        var when = DateTime.UtcNow;

        // A column for each server this client is connected to. A reconnection is a new session, and so a new column.
        var mine = _service.Sessions
            .Where(s => s.State == MumbleState.Connected && s.Client?.Me is not null)
            .Select(s => (Session: s, Me: s.Client!.Me!))
            .ToList();

        for (var i = OverlayColumns.Count - 1; i >= 0; i--)
            if (!mine.Any(m => m.Session == OverlayColumns[i].Session && m.Me.Session == OverlayColumns[i].Me.Session))
                OverlayColumns.RemoveAt(i);

        var next = 0;
        foreach (var (session, me) in mine)
        {
            if (!OverlayColumns.Any(c => c.Session == session))
                OverlayColumns.Insert(next, NewColumn(session, new MumbleOverlayLine(me.Session, me.Name)));
            next++;
        }

        foreach (var column in OverlayColumns)
        {
            var here = talking.Where(t => t.ServerId == column.ServerId).ToList();

            foreach (var t in here)
            {
                if (t.Session == column.Me.Session) continue;

                var line = column.Talking.FirstOrDefault(l => l.Session == t.Session);
                if (line is null) column.Talking.Add(line = new MumbleOverlayLine(t.Session, t.Name));
                line.LastTalked = when;
            }

            ShowState(column, column.Me, here);

            if (column.Session.Client is { Me: { } self } client)
            {
                var name = client.Channels.FirstOrDefault(c => c.Id == self.ChannelId)?.Name ?? "";
                column.Channel = name.Length > 0 ? name : MumbleText.RootChannel;
                column.Listening = MumbleService.ListeningIn(client.Users, self.ChannelId);
            }

            // Gone from the server, or quiet for long enough. Until then they stay, as in the standard client's list.
            for (var i = column.Talking.Count - 1; i >= 0; i--)
            {
                var line = column.Talking[i];
                if (when - line.LastTalked > KeptAfterTalking || !ShowState(column, line, here)) column.Talking.RemoveAt(i);
            }
        }

        NobodyTalking = OverlayColumns.Count == 0;

        var shape = string.Join('|', OverlayColumns.Select(c =>
            $"{c.ServerId}:{c.Me.Session}>" + string.Join(',', c.Talking.Select(l => $"{l.Session}:{l.IsMuted}:{l.IsDeaf}"))));
        if (shape != _overlayShape)
        {
            _overlayShape = shape;
            OverlayChanged?.Invoke();
        }
    }

    /// <summary>Whether they are still there, having brought their line up to date if so.</summary>
    private bool ShowState(MumbleOverlayColumn column, MumbleOverlayLine line, List<MumbleTalker> talking)
    {
        if (_service.StateOf(column.ServerId, line.Session) is not { } state) return false;

        line.IsMuted = state.Muted;
        line.IsDeaf = state.Deaf;
        line.Mode = talking.FirstOrDefault(t => t.Session == line.Session)?.Mode ?? MumbleTalk.None;
        return true;
    }

    private MumbleOverlayColumn NewColumn(MumbleSession session, MumbleOverlayLine me)
    {
        var column = new MumbleOverlayColumn(this, session, me)
        {
            ToggleMuteCommand   = ReactiveCommand.CreateFromTask(() => ToggleMuteOf(session)),
            ToggleDeafenCommand = ReactiveCommand.CreateFromTask(() => ToggleDeafenOf(session)),
        };
        column.ToggleMuteCommand.ThrownExceptions.Subscribe(e => StatusText = e.Message);
        column.ToggleDeafenCommand.ThrownExceptions.Subscribe(e => StatusText = e.Message);
        return column;
    }

    /// <summary>Brings a column's popup in line with the tree. Same rows as the tree itself, so opening a channel opens it in both.</summary>
    public void FillTree(MumbleOverlayColumn column) =>
        ListSync.Sync(column.Rows, [.. Rows.Where(r => r.ServerId == column.ServerId && !r.IsServer)], r => r);

    public async Task JoinFromOverlayAsync(MumbleOverlayColumn column, uint channel)
    {
        try { await Task.Run(() => _service.JoinChannelAsync(column.Session, channel)); }
        catch (Exception e) { StatusText = e.Message; }
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
