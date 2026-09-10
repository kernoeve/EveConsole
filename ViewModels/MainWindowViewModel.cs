using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveConsole.Agent;
using EveConsole.Data;
using EveConsole.Api;
using EveConsole.Auth;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Avalonia.Media;

namespace EveConsole.ViewModels;

public class MainWindowViewModel : ReactiveObject
{
    public OverviewViewModel              OverviewVm             { get; }
    public AlertSettingsViewModel         AlertSettingsVm        { get; }
    public CharacterViewModel             CharacterVm            { get; }
    public SdeViewModel                   SdeVm                  { get; }
    public UpdateViewModel                UpdateVm               { get; }

    // ── Which database this window is talking to ──────────────────────────────

    /// <summary>"PostgreSQL" or "SQLite", for the title bar.</summary>
    public string DbEngineLabel => DbEngine.DisplayName;

    /// <summary>Drives the icon's colour, so the two are told apart before the word is read.</summary>
    public bool DbIsPostgres => DbEngine.IsPostgres;

    private string _dbEngineTip = "";
    /// <summary>
    /// Where the data actually lives, on hover.
    ///
    /// <para>⚠️ Refreshed every ten minutes, not filled once. The size is the part worth hovering
    /// for and it is the part that moves — an import, a copy, a month of polling — so a figure fixed
    /// at startup goes on quoting what the database was when the window opened, which on a client
    /// left running for days is simply wrong.</para>
    ///
    /// <para>Still not on hover: a tooltip is no reason to touch the database every time a pointer
    /// crosses it. Ten minutes is often enough to never be far out and rare enough to cost nothing
    /// — the SQLite figure is a file length, the PostgreSQL one a single pg_database_size call.</para>
    /// </summary>
    public string DbEngineTip
    {
        get => _dbEngineTip;
        private set => this.RaiseAndSetIfChanged(ref _dbEngineTip, value);
    }

    private async Task LoadDbEngineTipAsync()
    {
        try
        {
            if (DbEngine.IsPostgres)
            {
                var cs = AppConfig.GetPostgresConnection() ?? "";
                var b  = new Npgsql.NpgsqlConnectionStringBuilder(cs);

                await using var conn = new Npgsql.NpgsqlConnection(AppDb.PostgresConnectionString(cs));
                await conn.OpenAsync();
                await using var cmd = new Npgsql.NpgsqlCommand(
                    "SELECT pg_size_pretty(pg_database_size(current_database()))", conn);
                var size = (await cmd.ExecuteScalarAsync())?.ToString() ?? "unknown";

                DbEngineTip = $"PostgreSQL on {b.Host}\nDatabase: {b.Database}\nSize: {size}";
            }
            else
            {
                var path = AppConfig.GetDbPath();
                var size = File.Exists(path)
                    ? $"{new FileInfo(path).Length / 1024d / 1024d:N0} MB"
                    : "file not found";
                DbEngineTip = $"SQLite\n{path}\nSize: {size}";
            }
        }
        catch (Exception ex)
        {
            // The label still names the engine; only the detail is missing.
            DbEngineTip = $"{DbEngine.DisplayName} — could not read details: "
                        + ex.Message.Split('\n')[0];
        }
    }

    // ── Which client is doing the background work ─────────────────────────────

    private readonly WorkerLease    _workerLease;
    private readonly AlarmMuteState _mute;
    private readonly DispatcherTimer _workerTimer;

    private string _workerOwner = "…";
    /// <summary>
    /// Who holds the lease, in as few words as the bar has room for: <c>this client</c>, the
    /// other client's host name, or <c>none</c>.
    /// </summary>
    public string WorkerOwner
    {
        get => _workerOwner;
        private set => this.RaiseAndSetIfChanged(ref _workerOwner, value);
    }

    private WorkerOwnership _workerState = WorkerOwnership.Unknown;
    /// <summary>Drives the colour, so "nobody is polling" reads before the word does.</summary>
    public WorkerOwnership WorkerState
    {
        get => _workerState;
        private set => this.RaiseAndSetIfChanged(ref _workerState, value);
    }

    private string _workerTip = "Checking which client is doing the background work…";
    /// <summary>Host, pid and version of the holder, on hover.</summary>
    public string WorkerTip
    {
        get => _workerTip;
        private set => this.RaiseAndSetIfChanged(ref _workerTip, value);
    }

    /// <summary>⚠️ The lease raises its events from its own loop, never the UI thread.</summary>
    private void OnLeaseChanged() => Dispatcher.UIThread.Post(() => _ = RefreshWorkerAsync());

    /// <summary>
    /// Re-reads who holds the lease.
    ///
    /// <para>⚠️ Polled as well as event-driven. Gained and Lost describe THIS process, which is
    /// only half the question — another client taking over, or dying, changes the answer with
    /// nothing here to notice it.</para>
    /// </summary>
    private async Task RefreshWorkerAsync()
    {
        // One process, no contest, nothing to report but itself.
        if (!DbEngine.IsPostgres)
        {
            WorkerOwner = "this client";
            WorkerState = WorkerOwnership.Mine;
            WorkerTip   = "Background processes run in this client.\n\n"
                        + $"Host: {Environment.MachineName}\n"
                        + $"PID: {Environment.ProcessId}\n"
                        + $"Version: {AppVersion.Number}\n\n"
                        + "SQLite allows one client at a time, so there is nothing to hand over to.";
            return;
        }

        var s = await WorkerLease.ReadStatusAsync();

        // ⚠️ IsHolder decides "mine", not a host name match. Two clients on one machine report the
        // same host, and just after a handover the row can still name the previous holder — the
        // lock is the only thing that actually knows.
        if (_workerLease.IsHolder)
        {
            WorkerOwner = "this client";
            WorkerState = WorkerOwnership.Mine;
            WorkerTip   = s is null
                ? "Background processes run in this client."
                : Describe("Background processes run in this client.", s);
            return;
        }

        if (s is null)
        {
            WorkerOwner = "none";
            WorkerState = WorkerOwnership.None;
            WorkerTip   = "No client has claimed the background work.\n\n"
                        + "ESI polling, build costs and backups are not running.";
            return;
        }

        if (!WorkerLease.IsLive(s))
        {
            WorkerOwner = "none";
            WorkerState = WorkerOwnership.None;
            WorkerTip   = Describe("Nothing is doing the background work — this is the last client that did.", s);
            return;
        }

        WorkerOwner = s.HostName;
        WorkerState = WorkerOwnership.Other;
        WorkerTip   = Describe("Background processes run in another client.", s);
    }

    private static string Describe(string headline, BackgroundWorkerStatus s) =>
        $"{headline}\n\n"
      + $"Host: {s.HostName}{(s.Headless ? "  (headless)" : "")}\n"
      + $"PID: {s.ProcessId}\n"
      + $"Version: {s.Version}\n"
      + $"Since: {s.LeaseTakenUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n"
      + $"Last heartbeat: {Ago(DateTimeOffset.UtcNow - s.HeartbeatUtc)}";

    private static string Ago(TimeSpan t) =>
        t < TimeSpan.FromMinutes(1) ? $"{Math.Max(0, (int)t.TotalSeconds)}s ago"
      : t < TimeSpan.FromHours(1)   ? $"{(int)t.TotalMinutes} min ago"
      :                               $"{(int)t.TotalHours} h ago";

    // ── Whether this machine stays quiet for alarms ───────────────────────────

    /// <summary>
    /// Silences the alarm actions that interrupt someone here — sound, dialog, and the agent
    /// speaking — without touching what gets recorded.
    ///
    /// <para>⚠️ Backed by the shared <see cref="AlarmMuteState"/>, not a field of its own. The
    /// Alarms tool has its own button for this, and two copies would let one of them go on saying
    /// "on" after the other muted — with the beacon un-struck and the operator believing they are
    /// silent when they are not.</para>
    /// </summary>
    public bool AlarmsMuted
    {
        get => _mute.Muted;
        set => _mute.Muted = value;
    }

    /// <summary>The action a click would take, for a menu item or a button that toggles.</summary>
    public string AlarmsMuteMenuText => _mute.ToggleText;

    private void OnMuteChanged()
    {
        this.RaisePropertyChanged(nameof(AlarmsMuted));
        this.RaisePropertyChanged(nameof(AlarmsMuteMenuText));
        RefreshAlarmsTip();
    }

    public ApiActivityViewModel           ActivityVm             { get; }
    public EsiExplorerViewModel           ExplorerVm             { get; }
    public ErrorLogViewModel              ErrorLogVm             { get; }
    public AgentUsageViewModel            AgentUsageVm           { get; }
    public GameLogViewerViewModel         GameLogViewerVm        { get; }
    public ChatLogViewerViewModel         ChatLogViewerVm        { get; }
    public AssetBrowserViewModel          AssetBrowserVm         { get; }
    public IndustryBrowserViewModel       IndustryBrowserVm      { get; }
    public CharacterViewerViewModel       CharacterViewerVm      { get; }
    public ItemBrowserViewModel           ItemBrowserVm          { get; }
    public NetWorthViewModel              NetWorthVm             { get; }
    public IncomeExpenseViewModel         IncomeExpenseVm        { get; }
    public TradeOpportunitiesViewModel    TradeOpportunitiesVm   { get; }
    public IndustryOpportunitiesViewModel IndustryOpportunitiesVm { get; }
    public IndyParksViewModel             IndyParksVm            { get; }
    public ProductionCalculatorViewModel  ProductionCalcVm       { get; }
    public PriceOverrideViewModel         PriceOverrideVm        { get; }
    public StructureBrowserViewModel      StructureBrowserVm     { get; }
    public UniverseViewModel              UniverseVm             { get; }
    public AlarmsViewModel                AlarmsVm               { get; }
    public SchedulerViewModel             SchedulerVm            { get; }
    public JumpPlannerViewModel           JumpPlannerVm          { get; }
    public AlarmActionRunner              AlarmActions           { get; }
    public WalletViewModel                WalletVm               { get; }
    public ContractsViewModel             ContractsVm            { get; }
    public NotificationsViewModel         NotificationsVm        { get; }
    public MarketViewerViewModel          MarketViewerVm         { get; }
    public SalesTrackerViewModel          SalesTrackerVm         { get; }
    public SaleListingViewModel           SaleListingBuildVm     { get; }
    public SaleListingViewModel           SaleListingMarketVm    { get; }
    public OrderTrackerViewModel          OrderTrackerVm         { get; }
    public StandingBuyOrdersViewModel     StandingBuyOrdersVm    { get; }
    public WorklistViewModel              WorklistVm             { get; }
    public LpMarketValuesViewModel        LpMarketValuesVm       { get; }
    public PlayerEntitiesViewModel        PlayerEntitiesVm       { get; }
    public NpcEntitiesViewModel           NpcEntitiesVm          { get; }
    public MarketSettingsViewModel        MarketVm               { get; }
    public TimerSettingsViewModel         TimerVm                { get; }
    public AgentPanelViewModel            AgentVm                { get; }
    public MarketLevelViewModel           MarketLevelVm          { get; }
    public InvLevelViewModel              InvLevelVm             { get; }
    public SalePostingViewModel           SalePostingVm          { get; }
    public StoresViewModel                StoresVm               { get; }
    public CorpActivityViewModel          CorpActivityVm         { get; }
    public KillmailBrowserViewModel       KillmailBrowserVm      { get; }
    public EveMailViewModel               EveMailVm              { get; }
    public PriceHistorySettingsViewModel  PriceHistorySettingsVm { get; }
    public PollingSettingsViewModel       PollingSettingsVm      { get; }
    public CorpTop10SettingsViewModel     CorpTop10SettingsVm    { get; }
    public SlackSettingsViewModel         SlackSettingsVm        { get; }
    public GameLogSettingsViewModel       GameLogSettingsVm      { get; }
    public ChatLogSettingsViewModel       ChatLogSettingsVm      { get; }
    public ZkillboardSettingsViewModel    ZkbSettingsVm          { get; }
    public MapStatsSettingsViewModel      MapStatsSettingsVm     { get; }
    public SlackService                   Slack                  { get; }
    public TtsService                     TtsService             { get; }
    public SpeechInputService             SpeechInputService     { get; }
    public GlobalHotkeyService            HotkeyService          { get; }
    public AppPreferencesService          AppPrefs               { get; }
    public DatabaseBackupService          DbBackup               { get; }

    public EveMailService MailSvc { get; }

    private readonly EsiPollingService  _pollingService;
    private readonly BuildCostService   _buildCostService;

    private string _eveTimeText = "";
    public string EveTimeText
    {
        get => _eveTimeText;
        private set => this.RaiseAndSetIfChanged(ref _eveTimeText, value);
    }

    private void StartEveTimeClock()
    {
        EveTimeText = DateTimeOffset.UtcNow.ToString("HH:mm:ss");
        var timer = new System.Timers.Timer(1000) { AutoReset = true };
        timer.Elapsed += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => EveTimeText = DateTimeOffset.UtcNow.ToString("HH:mm:ss"));
        timer.Start();
    }

    // ── Alarm light (shown beside the settings gear) ────────────────────────────

    private int _activeAlarmCount;
    public int ActiveAlarmCount
    {
        get => _activeAlarmCount;
        private set => this.RaiseAndSetIfChanged(ref _activeAlarmCount, value);
    }

    private bool _hasActiveAlarms;
    public bool HasActiveAlarms
    {
        get => _hasActiveAlarms;
        private set => this.RaiseAndSetIfChanged(ref _hasActiveAlarms, value);
    }

    private IBrush _alarmLightColor = Palette.SurfaceRaised;
    public IBrush AlarmLightColor
    {
        get => _alarmLightColor;
        private set => this.RaiseAndSetIfChanged(ref _alarmLightColor, value);
    }

    private IBrush _alarmLightRing = Palette.SurfaceRaised;
    public IBrush AlarmLightRing
    {
        get => _alarmLightRing;
        private set => this.RaiseAndSetIfChanged(ref _alarmLightRing, value);
    }

    /// <summary>The gleam on the dome dims with the lamp — a bright highlight on a dark dome
    /// reads as a lit bulb that is not lit.</summary>
    private double _alarmGleamOpacity = 0.18;
    public double AlarmGleamOpacity
    {
        get => _alarmGleamOpacity;
        private set => this.RaiseAndSetIfChanged(ref _alarmGleamOpacity, value);
    }

    private string _alarmsTip = "Alarms";
    public string AlarmsTip
    {
        get => _alarmsTip;
        private set => this.RaiseAndSetIfChanged(ref _alarmsTip, value);
    }

    /// <summary>
    /// The light follows the alarm loop's own armed count, which it republishes on every tick,
    /// so this needs no timer of its own and no query.
    /// </summary>
    /// <summary>
    /// Lights the beacon from whichever client is actually evaluating alarms.
    ///
    /// <para>⚠️ Not from the local service. Alarms are leader-only, so on a client that is not the
    /// worker AlarmService is never started and its ArmedCount stays nought — the beacon went dark
    /// and the badge vanished while the Alarms tab, which relays the worker, correctly said one was
    /// armed. Two readouts of the same fact, disagreeing, and the more prominent one wrong.</para>
    /// </summary>
    private void SetAlarmLight(int count) => Dispatcher.UIThread.Post(() =>
    {
        ActiveAlarmCount = count;
        HasActiveAlarms  = count > 0;

        AlarmLightColor   = count > 0 ? Palette.BadSurface : Palette.SurfaceRaised;
        AlarmLightRing    = count > 0 ? Palette.Bad : Palette.SurfaceRaised;
        AlarmGleamOpacity = count > 0 ? 0.55 : 0.18;

        _armedCount = count;
        RefreshAlarmsTip();
    });

    private void BindAlarmLight(AlarmService alarms, WorkerActivityService activity)
    {
        // This client's own service — right only while this client is the worker.
        alarms.WhenAnyValue(x => x.ArmedCount)
            .Subscribe(count => { if (_workerLease.IsHolder) SetAlarmLight(count); });

        // And the worker's, for when it is somebody else. Pushed on the same signal the Alarms tab
        // uses, so the beacon and the tab cannot disagree about how many are armed.
        activity.Changed += () =>
        {
            if (_workerLease.IsHolder) return;
            if (activity.Get(WorkerActivityService.Alarms)?.Count is { } armed) SetAlarmLight(armed);
        };
    }

    private int _armedCount;

    /// <summary>
    /// ⚠️ Armed and audible are different questions, and the tooltip answers both. Muted alarms
    /// stay armed and go on being recorded, so the count alone would let somebody read "3 armed"
    /// off a machine that will not make a sound about any of them.
    /// </summary>
    private void RefreshAlarmsTip()
    {
        var armed = _armedCount switch
        {
            0 => "Alarms — none armed",
            1 => "Alarms — 1 armed",
            _ => $"Alarms — {_armedCount} armed",
        };

        AlarmsTip = AlarmsMuted
            ? armed + "\n\nMuted on this client: no sound, dialog or agent notification will be "
                    + "raised here. Alerts are still recorded.\n\nRight-click to unmute."
            : armed + "\n\nRight-click to mute this client.";
    }

    // ── My characters online (shown beside the EVE clock) ───────────────────────

    private string _onlineCharactersText = "";
    public string OnlineCharactersText
    {
        get => _onlineCharactersText;
        private set => this.RaiseAndSetIfChanged(ref _onlineCharactersText, value);
    }

    private string _onlineCharactersTip = "";
    public string OnlineCharactersTip
    {
        get => _onlineCharactersTip;
        private set => this.RaiseAndSetIfChanged(ref _onlineCharactersTip, value);
    }

    /// <summary>Green while anyone is online, grey otherwise — same convention as the TQ dot.</summary>
    private IBrush _onlineCharactersColor = Palette.BorderStrong;
    public IBrush OnlineCharactersColor
    {
        get => _onlineCharactersColor;
        private set => this.RaiseAndSetIfChanged(ref _onlineCharactersColor, value);
    }

    /// <summary>
    /// Reads the online/location/ship state the poller keeps in CharacterStatuses. On its own
    /// timer rather than the clock's, because it costs a query — and off the UI thread, since
    /// SQLite has no real async I/O and awaiting it here would freeze the window.
    /// </summary>
    private void StartOnlineCharactersWatch(IDbContextFactory<AppDbContext> dbFactory)
    {
        _ = RefreshOnlineCharactersAsync(dbFactory);

        var timer = new System.Timers.Timer(TimeSpan.FromSeconds(30)) { AutoReset = true };
        timer.Elapsed += (_, _) => _ = RefreshOnlineCharactersAsync(dbFactory);
        timer.Start();
    }

    private async Task RefreshOnlineCharactersAsync(IDbContextFactory<AppDbContext> dbFactory)
    {
        try
        {
            var rows = await Task.Run(async () =>
            {
                await using var db = await dbFactory.CreateDbContextAsync();

                // Left joins throughout: a character who has just logged in may not have had a
                // location or ship poll yet, and should still be counted as online.
                return await (
                    from s in db.CharacterStatuses.AsNoTracking()
                    join c in db.Characters.AsNoTracking() on s.CharacterId equals c.Id
                    from sys in db.SdeSolarSystems.AsNoTracking()
                        .Where(x => x.SolarSystemId == s.SolarSystemId).DefaultIfEmpty()
                    from ship in db.SdeTypes.AsNoTracking()
                        .Where(x => x.TypeId == s.ShipTypeId).DefaultIfEmpty()
                    select new
                    {
                        c.Name,
                        s.Online,
                        System   = sys != null ? sys.Name : null,
                        Hull     = ship != null ? ship.Name : null,
                        s.ShipName,
                    }).ToListAsync();
            });

            var online = rows.Where(r => r.Online).OrderBy(r => r.Name).ToList();

            var text = $"{online.Count} of {rows.Count} Online";

            var tip = online.Count == 0
                ? "None of your characters are online."
                : string.Join("\n", online.Select(r =>
                {
                    var where = string.IsNullOrWhiteSpace(r.System) ? "location unknown" : r.System;

                    // The hull is what the ship IS; ShipName is what the pilot called it. Show
                    // both only when the pilot bothered to rename it.
                    var ship = string.IsNullOrWhiteSpace(r.Hull) ? "ship unknown" : r.Hull;
                    if (!string.IsNullOrWhiteSpace(r.ShipName)
                        && !string.Equals(r.ShipName, r.Hull, StringComparison.OrdinalIgnoreCase))
                        ship = $"{r.Hull} \"{r.ShipName}\"";

                    return $"{r.Name} — {where} — {ship}";
                }));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnlineCharactersText  = text;
                OnlineCharactersTip   = tip;
                OnlineCharactersColor = online.Count > 0 ? Palette.Good : Palette.BorderStrong;
            });
        }
        catch
        {
            // A header ornament must never be the thing that takes the window down.
        }
    }

    /// <summary>Where clicking the EVE clock goes. Read at click time rather than cached,
    /// so a change in Settings takes effect without reopening the window.</summary>
    private UiLinkSettings? _uiLinks;

    /// <summary>Held here because SettingsViewModel is built by hand when the window is
    /// opened rather than resolved from DI.</summary>
    public OtherSettingsViewModel OtherSettingsVm { get; private set; } = null!;
    public DataRetentionSettingsViewModel DataRetentionVm { get; private set; } = null!;

    public string EveTimeUrl    => _uiLinks?.EveTimeUrl ?? UiLinkSettings.EveOnlineTimeUrl;
    public string EveTimeLinkTip => $"EVE time (UTC) — click to open {EveTimeUrl}";

    // ── Theme (shown on the title bar, beside the alarm beacon) ─────────────────

    /// <summary>
    /// The theme's name, for the label that also picks it.
    ///
    /// <para>Read from ThemeService rather than stored, so the bar and the Settings window can
    /// never disagree about what is on — either can change it, and both follow the event.</para>
    /// </summary>
    public string ThemeName => ThemeService.All
        .FirstOrDefault(t => t.Key == ThemeService.Current)?.Name ?? "Theme";

    public string ThemeTip => $"Theme: {ThemeName} — click to change";

    private void OnThemeChanged()
    {
        this.RaisePropertyChanged(nameof(ThemeName));
        this.RaisePropertyChanged(nameof(ThemeTip));
    }

    // ── Tranquility status (shown beside the EVE clock) ─────────────────────────

    private string _serverStatusText = "Online";
    public string ServerStatusText { get => _serverStatusText; private set => this.RaiseAndSetIfChanged(ref _serverStatusText, value); }

    private IBrush _serverStatusColor = Palette.Good;
    public IBrush ServerStatusColor { get => _serverStatusColor; private set => this.RaiseAndSetIfChanged(ref _serverStatusColor, value); }

    private string _serverPlayersText = "";
    public string ServerPlayersText { get => _serverPlayersText; private set => this.RaiseAndSetIfChanged(ref _serverPlayersText, value); }

    private string _serverStatusTip = "Tranquility server status";
    public string ServerStatusTip { get => _serverStatusTip; private set => this.RaiseAndSetIfChanged(ref _serverStatusTip, value); }

    /// <summary>Mirrors EveServerStatusService onto the UI thread. The service raises
    /// changes from its own polling task, so everything here is marshalled explicitly.</summary>
    private void BindServerStatus(EveServerStatusService status)
    {
        void Apply() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ServerStatusText   = status.StatusText;
            ServerStatusColor  = status.StatusColor;
            ServerPlayersText  = status.PlayersText;
            ServerStatusTip    = status.IsOnline
                ? $"Tranquility is online{(status.Players > 0 ? $" — {status.Players:N0} players" : "")}"
                : "Tranquility is offline — ESI polling is paused until it returns";
        });

        status.PropertyChanged += (_, _) => Apply();
        Apply();
    }

    private string _pollingStatusText = "Polling: Not started";
    public string PollingStatusText
    {
        get => _pollingStatusText;
        private set => this.RaiseAndSetIfChanged(ref _pollingStatusText, value);
    }

    private string _buildCostStatusText = "Build costs: not yet calculated";
    public string BuildCostStatusText
    {
        get => _buildCostStatusText;
        private set => this.RaiseAndSetIfChanged(ref _buildCostStatusText, value);
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    public IReadOnlyList<NavGroup>       NavGroups { get; }
    public ObservableCollection<ToolTab> OpenTabs  { get; } = new();

    private readonly NavItem[] _allNavItems;

    private ToolTab? _selectedTab;
    public ToolTab? SelectedTab
    {
        get => _selectedTab;
        set => this.RaiseAndSetIfChanged(ref _selectedTab, value);
    }

    public ReactiveCommand<string,  Unit> OpenToolCommand { get; }
    public ReactiveCommand<ToolTab, Unit> CloseTabCommand { get; }

    public void OpenTool(string toolId)
    {
        var existing = OpenTabs.FirstOrDefault(t => t.Id == toolId);
        if (existing is not null)
        {
            SelectedTab = existing;
            // Returning to an already-open tab has to refresh too, or an alarm that fired while
            // the tab sat in the background shows nothing until something else triggers a load.
            if (toolId == "alarms")    _ = AlarmsVm.LoadAsync();
            if (toolId == "scheduler") _ = SchedulerVm.LoadAsync();

            // ⚠️ The error log is NOT refreshed here. Returning to a tab already open should not
            // re-read five thousand rows; the list is as old as the moment it was opened, and the
            // Refresh button says so.
            return;
        }

        var (title, vm, canClose) = toolId switch
        {
            "overview"   => ("Overview",       (object)OverviewVm,       false),
            "characters" => ("Characters",      CharacterViewerVm,        true),
            "assets"     => ("Assets",          AssetBrowserVm,           true),
            "items"      => ("Item Browser",    ItemBrowserVm,            true),
            "industry"   => ("Industry Jobs",   IndustryBrowserVm,        true),
            "indy_parks" => ("Indy Parks",      IndyParksVm,              true),
            "prod_calc"  => ("Production Calc", ProductionCalcVm,         true),
            "price_overrides" => ("Price Overrides", PriceOverrideVm,     true),
            "structure_browser" => ("Structure Browser", StructureBrowserVm, true),
            "universe"        => ("Universe",        UniverseVm,        true),
            "alarms"          => ("Alarms",          AlarmsVm,          true),
            "scheduler"       => ("Scheduler",       SchedulerVm,       true),
            "jump_planner"    => ("Jump Planner",    JumpPlannerVm,     true),
            "trade"           => ("Trade",           TradeOpportunitiesVm,     true),
            "industry_opps"   => ("Industry Opps",   IndustryOpportunitiesVm,  true),
            "market_levels"   => ("Market Levels",   MarketLevelVm,            true),
            "inv_levels"      => ("Inv. Levels",     InvLevelVm,               true),
            "sale_posting"    => ("Sale Posting",    SalePostingVm,            true),
            "stores"          => ("Stores",          StoresVm,                 true),
            "net_worth"  => ("Net Worth",       NetWorthVm,               true),
            "income_expense" => ("Income & Expense", IncomeExpenseVm,     true),
            "wallet"         => ("Wallet",          WalletVm,          true),
            "contracts"      => ("Contracts",       ContractsVm,       true),
            "market_viewer"  => ("Market Overview", MarketViewerVm,    true),
            "sales_tracker"  => ("Sales Tracker",   SalesTrackerVm,    true),
            "sale_list_build"  => ("Sale Listing (Build)",  SaleListingBuildVm,  true),
            "sale_list_market" => ("Sale Listing (Market)", SaleListingMarketVm, true),
            "order_tracker"  => ("Order Tracker",   OrderTrackerVm,    true),
            "standing_buy_orders" => ("Standing Buy Orders", StandingBuyOrdersVm, true),
            "worklist"       => ("Worklist",       WorklistVm,        true),
            "lp_market_values" => ("LP Market Values", LpMarketValuesVm, true),
            "player_entities"  => ("Player Entities", PlayerEntitiesVm, true),
            "npc_entities"     => ("NPC Entities",    NpcEntitiesVm,    true),
            "corp_activity"  => ("Corp Activity",  CorpActivityVm,    true),
            "killmails"      => ("Killmails",      KillmailBrowserVm, true),
            "eve_mail"       => ("Eve Mail",       EveMailVm,         true),
            "notifications"  => ("Notifications",  NotificationsVm,   true),
            "data"           => ("ESI Explorer",   ExplorerVm,        true),
            "error_log"      => ("Error Log",      ErrorLogVm,        true),
            "ai_usage"       => ("AI Usage",       AgentUsageVm,      true),
            "game_log"       => ("Game Log",       GameLogViewerVm,   true),
            "chat_log"       => ("Chat Log",       ChatLogViewerVm,   true),
            _                => throw new ArgumentException($"Unknown tool: {toolId}")
        };


        // From here the tool is on screen, so its own refresh timer is allowed to run. A latch,
        // not a visibility check — see IPeriodicRefresh.
        if (vm is IPeriodicRefresh periodic) periodic.AutoRefreshEnabled = true;
        var tab = new ToolTab(toolId, title, vm, canClose);
        OpenTabs.Add(tab);
        SelectedTab = tab;

        // Loaded on open rather than at construction — nothing else needs the alarm list, and
        // a fresh read also picks up anything the agent created since the tab was last shown.
        if (toolId == "alarms")    _ = AlarmsVm.LoadAsync();
        if (toolId == "scheduler") _ = SchedulerVm.LoadAsync();

        // ⚠️ Same reasoning, and it mattered more here. The error log used to read at application
        // start, so opening it showed a list from whenever the app was launched — which looks
        // current and is not. Reading on open means closing the tab and opening it again reads
        // afresh, which is what somebody doing that is asking for.
        if (toolId == "error_log") ErrorLogVm.Reload();
        if (toolId == "ai_usage")  AgentUsageVm.Reload();

        var navItem = _allNavItems.FirstOrDefault(i => i.ToolId == toolId);
        if (navItem is not null) navItem.IsOpen = true;
    }

    /// <summary>
    /// Distinguishes agent-opened tabs, which are many, from tools, which are one each.
    /// </summary>
    public const string AgentTabPrefix = "agent_output:";

    private int _agentTabCounter;

    /// <summary>
    /// Opens a tab holding something the agent produced, and selects it.
    ///
    /// <para>⚠️ Separate from OpenTool, and deliberately so. OpenTool maps a fixed id to a
    /// singleton view model — asking for the Worklist twice returns to the one Worklist. These
    /// are answers to particular questions, so every call gets an id of its own and a tab of its
    /// own; the previous answer stays open beside it.</para>
    ///
    /// <para>They are reachable ONLY this way. There is no nav entry, because there is nothing to
    /// open — an empty one of these would have no content and no reason to exist.</para>
    ///
    /// <para>⚠️ Marshalled to the UI thread by the caller's Dispatcher.Invoke. Agent tools run on
    /// a background thread and OpenTabs is bound to the tab strip.</para>
    /// </summary>
    public string OpenAgentTab(string title, object viewModel)
    {
        var id  = AgentTabPrefix + Interlocked.Increment(ref _agentTabCounter);
        var tab = new ToolTab(id, title, viewModel, canClose: true);
        OpenTabs.Add(tab);
        SelectedTab = tab;
        return id;
    }

    public void CloseTab(ToolTab tab)
    {
        if (!tab.CanClose) return;
        bool wasSelected = SelectedTab == tab;
        OpenTabs.Remove(tab);

        var navItem = _allNavItems.FirstOrDefault(i => i.ToolId == tab.Id);
        if (navItem is not null) navItem.IsOpen = false;

        if (wasSelected)
            SelectedTab = OpenTabs.FirstOrDefault(t => t.Id == "overview") ?? OpenTabs.FirstOrDefault();
    }

    // Called when a tab is detached into a floating window — removes it from the
    // strip but keeps the nav-item dot lit (the tool is still "open").
    public void MarkToolDetached(string toolId)
    {
        var tab = OpenTabs.FirstOrDefault(t => t.Id == toolId);
        if (tab is not null)
        {
            bool wasSelected = SelectedTab == tab;
            OpenTabs.Remove(tab);
            if (wasSelected)
                SelectedTab = OpenTabs.FirstOrDefault(t => t.Id == "overview") ?? OpenTabs.FirstOrDefault();
        }
        var navItem = _allNavItems.FirstOrDefault(i => i.ToolId == toolId);
        if (navItem is not null) navItem.IsOpen = true;
    }

    // Called when a detached window closes — extinguishes the nav-item dot.
    public void MarkToolReattached(string toolId)
    {
        var navItem = _allNavItems.FirstOrDefault(i => i.ToolId == toolId);
        if (navItem is not null) navItem.IsOpen = false;
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainWindowViewModel(
        EsiAuthService                  auth,
        EsiClient                       esi,
        IDbContextFactory<AppDbContext> dbFactory,
        SdeImportService                sdeService,
        HoboImportService               hoboService,
        EsiPollingService               pollingService,
        WorkerLease                     workerLease,
        WorkerActivityService           workerActivity,
        ApiActivityViewModel            activityVm,
        AlarmMuteState                  alarmMute,
        ApiActivityLog                  activityLog,
        MarketPricingService            marketPricing,
        MarketLevelService              marketLevelService,
        InvLevelService                 invLevelService,
        SalePostingService              salePostingService,
        StoreMailService                storeMailService,
        OrderLabelService               orderLabels,
        BatchAddService                 batchAddService,
        CorpActivityService             corpActivityService,
        CharacterSummaryService         characterSummaryService,
        StandingBuyOrderService         standingBuyOrderService,
        EveConsole.Services.Worklist.WorklistService worklistService,
        EveConsole.Services.Worklist.WorklistMarketAltService worklistMarketAltService,
        EveConsole.Services.Worklist.WorklistCorpAltService worklistCorpAltService,
        EveConsole.Services.Worklist.IndustryAssignmentService industryAssignmentService,
        EveConsole.Services.Worklist.IndustryBlueprintService  industryBlueprintService,
        EveConsole.Services.Worklist.WorklistSettings worklistSettings,
        IndyFacilityCheckService        indyFacilityCheck,
        IndyStructureLinkService        indyStructureLink,
        IndyBulkAddService              indyBulkAdd,
        KillmailBrowserService          killmailBrowserService,
        BuildCostService                buildCostService,
        ProductionCalculatorService     prodCalcService,
        IServiceScopeFactory            scopeFactory,
        TimerSettingsService            timerSettings,
        TimerForceService               timerForce,
        AgentService                    agentService,
        AppErrorLogger                  errorLogger,
        KillMailService                 killMailService,
        EveMailService                  eveMailService,
        TtsService                      ttsService,
        SpeechInputService              speechInputService,
        GlobalHotkeyService             hotkeyService,
        NewsService                     newsService,
        AppPreferencesService           appPrefs,
        DatabaseBackupService           dbBackup,
        CorpTop10ExcludeService         corpTop10Exclude,
        CorpReportTitles                 corpReportTitles,
        MarketHistoryService            historyService,
        ContractsService                contractsService,
        SlackService                    slackService,
        MonitoringSettings              monitoringSettings,
        GameLogImportService            gameLogImport,
        ChatLogImportService            chatLogImport,
        IntelService                    intelService,
        ZkillboardSettings              zkillboardSettings,
        ZkillboardPollingService        zkbPolling,
        ZkillboardFirehoseService       zkbFirehose,
        ZkillboardBackfillService       zkbBackfill,
        MapStatsSettings                mapStatsSettings,
        MapStatsBackfillService         mapStatsBackfill,
        MapStatsPollingService          mapStatsPolling,
        MapStatsService                 mapStatsService,
        SystemViewService               systemViewService,
        ZkillboardPostService           zkbPost,
        EntityNameBackfillService       entityNames,
        EveServerStatusService          serverStatus,
        UiLinkSettings                  uiLinks,
        DataRetentionService        dataRetention,
        OrderFulfilmentService      orderFulfilment,
        ExportFormatSettings            exportFormat,
        AlarmService                    alarmService,
        AlarmSoundService               alarmSounds,
        AlarmActionRunner               alarmActions,
        JumpPlannerService              jumpPlanner,
        LpStoreService                  lpStoreService,
        LpValueService                  lpValueService,
        SchedulerService                schedulerService,
        ScheduledBlockRenderer          blockRenderer)
    {
        AlarmActions = alarmActions;
        _uiLinks        = uiLinks;
        OtherSettingsVm = new OtherSettingsViewModel(uiLinks);
        DataRetentionVm = new DataRetentionSettingsViewModel(dataRetention);
        BindServerStatus(serverStatus);
        ThemeService.Changed += OnThemeChanged;

        Slack             = slackService;
        SlackSettingsVm   = new SlackSettingsViewModel(slackService);
        GameLogSettingsVm = new GameLogSettingsViewModel(monitoringSettings, gameLogImport);
        ChatLogSettingsVm = new ChatLogSettingsViewModel(monitoringSettings, chatLogImport, intelService);
        ZkbSettingsVm     = new ZkillboardSettingsViewModel(zkillboardSettings, zkbPolling, zkbFirehose, zkbBackfill, zkbPost);
        MapStatsSettingsVm = new MapStatsSettingsViewModel(mapStatsSettings, mapStatsBackfill, mapStatsPolling, mapStatsService);
        AlertSettingsVm   = new AlertSettingsViewModel(dbFactory.CreateDbContext());
        OverviewVm        = new OverviewViewModel(dbFactory.CreateDbContext(), AlertSettingsVm, errorLogger, newsService, appPrefs, corpActivityService, dbFactory, esi, standingBuyOrderService, indyFacilityCheck);
        CharacterVm       = new CharacterViewModel(auth, esi, dbFactory.CreateDbContext());
        SdeVm             = new SdeViewModel(sdeService, hoboService, dbFactory.CreateDbContext());

        // Not awaited: the engine name is right immediately, and only the hover detail is late.
        _ = LoadDbEngineTipAsync();

        // And kept current, because the size in it grows. See DbEngineTip for why ten minutes
        // rather than on hover.
        Observable.Interval(TimeSpan.FromMinutes(10))
            .ObserveOnUi("Db.TipRefresh")
            .Subscribe(tick => { _ = LoadDbEngineTipAsync(); });

        _mute            = alarmMute;
        alarmMute.Changed += OnMuteChanged;

        // Who is doing the background work, now and as it changes.
        _workerLease        = workerLease;
        workerLease.Gained += OnLeaseChanged;
        workerLease.Lost   += OnLeaseChanged;
        _ = RefreshWorkerAsync();

        // ⚠️ Polled as well as event-driven, at the lease's own tick, because the events say
        // nothing about what another client did.
        _workerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _workerTimer.Tick += (_, _) => _ = RefreshWorkerAsync();
        _workerTimer.Start();
        ActivityVm        = activityVm;
        CharacterViewerVm = new CharacterViewerViewModel(dbFactory.CreateDbContext(), CharacterVm.Characters,
            characterSummaryService);
        NetWorthVm        = new NetWorthViewModel(dbFactory);
        IncomeExpenseVm   = new IncomeExpenseViewModel(dbFactory, errorLogger);
        MarketVm          = new MarketSettingsViewModel(dbFactory.CreateDbContext(), dbFactory, marketPricing, esi, CharacterVm.Characters, buildCostService);
        var fittingsService = new FittingsService(esi, dbFactory);
        MarketLevelVm     = new MarketLevelViewModel(marketLevelService, dbFactory, fittingsService,
            CharacterVm.Characters, CharacterVm.Corporations, batchAddService, prodCalcService);
        // appPrefs is the constructor parameter, not the AppPrefs property — that is not assigned
        // until far below this line, and passing it here handed the view model a null.
        InvLevelVm        = new InvLevelViewModel(invLevelService, dbFactory, appPrefs,
            batchAddService, prodCalcService, fittingsService,
            CharacterVm.Characters, CharacterVm.Corporations);
        SalePostingVm     = new SalePostingViewModel(salePostingService, dbFactory, batchAddService, slackService, exportFormat);
        StoresVm          = new StoresViewModel(dbFactory, salePostingService, storeMailService, orderLabels, errorLogger);
        CorpActivityVm    = new CorpActivityViewModel(corpActivityService, CharacterVm.Corporations, corpTop10Exclude, corpReportTitles, slackService, exportFormat, errorLogger);
        KillmailBrowserVm = new KillmailBrowserViewModel(killmailBrowserService);
        MailSvc           = eveMailService;
        EveMailVm         = new EveMailViewModel(eveMailService, CharacterVm.Characters);
        CorpActivityVm.RequestOpenKillmail = killMailId =>
        {
            OpenTool("killmails");
            KillmailBrowserVm.SelectById(killMailId);
        };

        OverviewVm.NavigateToCharacterSkills = characterName =>
        {
            OpenTool("characters");
            CharacterViewerVm.ShowSkillsFor(characterName);
        };
        OverviewVm.NavigateToStandingProjects = () =>
        {
            OpenTool("corp_activity");
            CorpActivityVm.ShowStandingProjectsTab();
        };
        OverviewVm.NavigateToStandingBuyOrders = () => OpenTool("standing_buy_orders");
        OverviewVm.NavigateToIndustryJobs      = () => OpenTool("industry");
        OverviewVm.NavigateToOrderTracker      = () => OpenTool("order_tracker");
        OverviewVm.RequestOpenKillmail = killMailId =>
        {
            OpenTool("killmails");
            KillmailBrowserVm.SelectById(killMailId);
        };
        OverviewVm.OpenToolRequested = OpenTool;
        TimerVm           = new TimerSettingsViewModel(pollingService, timerSettings, timerForce);
        _pollingService   = pollingService;
        _buildCostService = buildCostService;

        PriceHistorySettingsVm = new PriceHistorySettingsViewModel(dbFactory.CreateDbContext());
        PollingSettingsVm      = new PollingSettingsViewModel(appPrefs);
        CorpTop10SettingsVm    = new CorpTop10SettingsViewModel(corpTop10Exclude, corpReportTitles);
        ItemBrowserVm          = new ItemBrowserViewModel(dbFactory.CreateDbContext(), historyService, dbFactory, appPrefs);
        IndyParksVm            = new IndyParksViewModel(dbFactory, corpActivityService, errorLogger,
                                                        indyStructureLink, indyBulkAdd, pollingService);
        WalletVm               = new WalletViewModel(dbFactory, errorLogger);
        ContractsVm            = new ContractsViewModel(dbFactory, esi, errorLogger);
        NotificationsVm        = new NotificationsViewModel(dbFactory, esi, errorLogger);
        MarketViewerVm         = new MarketViewerViewModel(dbFactory, errorLogger);
        SalesTrackerVm         = new SalesTrackerViewModel(dbFactory, errorLogger, corpActivityService, orderLabels);
        SaleListingBuildVm     = new SaleListingViewModel(dbFactory, errorLogger, corpActivityService, SaleCostBasis.BuildCost);
        SaleListingMarketVm    = new SaleListingViewModel(dbFactory, errorLogger, corpActivityService, SaleCostBasis.MarketValue);
        OverviewVm.SaleListingBuild  = SaleListingBuildVm;   // let the Overview embed them as sections
        OverviewVm.SaleListingMarket = SaleListingMarketVm;
        OverviewVm.IncomeExpense     = IncomeExpenseVm;
        SaleListingBuildVm.OpenSalesTracker  = () => OpenTool("sales_tracker");
        SaleListingMarketVm.OpenSalesTracker = () => OpenTool("sales_tracker");
        // Built here rather than further down: the order tracker's buyer picker needs it, and a
        // service constructed after its first consumer is a null nobody notices until the box is
        // typed into.
        var entityBrowser      = new EntityBrowserService(dbFactory, esi);

        OrderTrackerVm         = new OrderTrackerViewModel(dbFactory, orderLabels, entityBrowser, errorLogger);
        StandingBuyOrdersVm    = new StandingBuyOrdersViewModel(standingBuyOrderService, corpActivityService);
        WorklistVm             = new WorklistViewModel(worklistService,
                                     new WorklistMarketAltsViewModel(worklistMarketAltService, corpActivityService, dbFactory),
                                     new WorklistInvRulesViewModel(dbFactory, corpActivityService, worklistMarketAltService),
                                     new WorklistCorpAltsViewModel(dbFactory, worklistCorpAltService),
                                     new WorklistIndustryViewModel(dbFactory, industryAssignmentService, worklistSettings, errorLogger, corpActivityService, worklistMarketAltService),
                                     new WorklistStationLevelsViewModel(dbFactory, corpActivityService, worklistSettings),
                                     new WorklistFinalProductsViewModel(dbFactory, appPrefs, errorLogger),
                                     new EveConsole.Services.Worklist.BottleneckService(dbFactory, industryAssignmentService,
                                                           industryBlueprintService, worklistSettings),
                                     new EveConsole.Services.Worklist.ItemContentionService(dbFactory, worklistSettings),
                                     new EveConsole.Services.Worklist.HaulPressureService(dbFactory, worklistSettings));

        // ⚠️ After construction, not with the other Overview wiring above — WorklistVm does not
        // exist until this line, so assigning it earlier set null and left every worklist section
        // on the Overview permanently blank.
        OverviewVm.Worklist = WorklistVm;

        // Adding, renaming or deleting an inventory group changes what the Worklist's group
        // pickers should offer. They load once, so without this a new group is missing until a
        // restart and a renamed one keeps its old label — making a rule look like it points at a
        // group that no longer exists.
        InvLevelVm.GroupsChanged = async () =>
        {
            await WorklistVm.RulesVm.LoadAsync();
            await WorklistVm.StationLevelsVm.LoadAsync();
        };

        LpMarketValuesVm       = new LpMarketValuesViewModel(dbFactory, lpValueService);
        PlayerEntitiesVm       = new PlayerEntitiesViewModel(entityBrowser, killmailBrowserService);
        NpcEntitiesVm          = new NpcEntitiesViewModel(entityBrowser, killmailBrowserService);
        ProductionCalcVm       = new ProductionCalculatorViewModel(dbFactory, prodCalcService, appPrefs);
        PriceOverrideVm        = new PriceOverrideViewModel(new PriceOverrideService(dbFactory), buildCostService);
        StructureBrowserVm     = new StructureBrowserViewModel(
                                     dbFactory, pollingService, esi, new FittingOptionService(dbFactory),
                                     appPrefs, indyStructureLink);
        var universeMapService = new UniverseMapService(dbFactory);
        UniverseVm             = new UniverseViewModel(
            universeMapService, mapStatsService,
            new SystemPageViewModel(systemViewService, killmailBrowserService), appPrefs);
        AlarmsVm               = new AlarmsViewModel(dbFactory, alarmService, alarmSounds, alarmMute);
        SchedulerVm            = new SchedulerViewModel(dbFactory, schedulerService, blockRenderer, slackService,
                                                        corpActivityService, salePostingService, errorLogger);
        JumpPlannerVm          = new JumpPlannerViewModel(jumpPlanner);

        // One wiring for every killmail row in the app — browser, corp activity, system
        // page, entity viewers.
        EntityNavigator.Instance.OpenEntity = (kind, id) =>
        {
            var player = kind is EntityKind.Pilot or EntityKind.PlayerCorp or EntityKind.Alliance;
            OpenTool(player ? "player_entities" : "npc_entities");
            if (player) PlayerEntitiesVm.Open(kind, id);
            else        NpcEntitiesVm.Open(kind, id);
        };
        EntityNavigator.Instance.OpenSystem   = id => { OpenTool("universe"); _ = UniverseVm.OpenSystemCommand.Execute(id).Subscribe(); };
        EntityNavigator.Instance.OpenItem     = id => { OpenTool("items"); _ = ItemBrowserVm.NavigateToItemCommand.Execute(id).Subscribe(); };
        EntityNavigator.Instance.OpenKillmail = id => { OpenTool("killmails"); KillmailBrowserVm.SelectById(id); };
        EntityNavigator.Instance.OpenStructure = id => { OpenTool("structure_browser"); StructureBrowserVm.Open(id); };
        EntityNavigator.Instance.OpenContract  = id => { OpenTool("contracts"); ContractsVm.SelectById(id); };
        // FocusRegionAsync, not ShowRegionAsync: the separate per-region map is legacy — only
        // the system page still returns to it. A region is now territory you zoom to on the
        // one continuous universe map.
        EntityNavigator.Instance.OpenRegion   = id => { OpenTool("universe"); _ = UniverseVm.FocusRegionAsync(id); };
        EntityNavigator.Instance.OpenConstellation =
            name => { OpenTool("universe"); _ = UniverseVm.FocusConstellationAsync(name); };

        // Resolve the overlay here rather than in the agent tool: this is the list's home, so
        // an overlay added to the map is reachable by name without touching the tool.
        EntityNavigator.Instance.SetOverlay = text =>
        {
            var wanted = (text ?? "").Trim();
            var mode = UniverseVm.OverlayModes.FirstOrDefault(m =>
                           m.Key.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                           m.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? UniverseVm.OverlayModes.FirstOrDefault(m =>
                           m.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (mode is null) return "";

            OpenTool("universe");
            UniverseVm.SelectedOverlay = mode;
            return $"Map overlay set to {mode.Name}.";
        };

        Action<int> showSystem = systemId =>
        {
            OpenTool("universe");
            _ = UniverseVm.OpenSystemCommand.Execute(systemId).Subscribe();
        };
        PlayerEntitiesVm.NavigateToSystem = showSystem;
        NpcEntitiesVm.NavigateToSystem    = showSystem;

        NpcEntitiesVm.NavigateToItem = typeId =>
        {
            OpenTool("items");
            _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
        };
        PlayerEntitiesVm.NavigateToNpc = (kind, id) =>
        {
            OpenTool("npc_entities");
            NpcEntitiesVm.Open(kind, id);
        };
        ProductionCalcVm.NavigateToItemAction = typeId =>
        {
            OpenTool("items");
            _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
        };
        CharacterViewerVm.NavigateToItemAction = typeId =>
        {
            OpenTool("items");
            _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
        };
        LpMarketValuesVm.NavigateToItemAction = typeId =>
        {
            OpenTool("items");
            _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
        };
        KillmailBrowserVm.NavigateToItemAction = typeId =>
        {
            OpenTool("items");
            _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
        };
        if (UniverseVm.SystemPage is { } sysPage)
            sysPage.NavigateToItemAction = typeId =>
            {
                OpenTool("items");
                _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
            };

        KillmailBrowserVm.NavigateToSystemAction = systemId =>
        {
            OpenTool("universe");
            // Through the command rather than the method directly: it already routes failures
            // to the map's status line instead of leaving an unobserved task exception.
            _ = UniverseVm.OpenSystemCommand.Execute(systemId).Subscribe();
        };

        using var tmpDb      = dbFactory.CreateDbContext();
        var connString       = tmpDb.Database.GetConnectionString()!;
        ExplorerVm           = new EsiExplorerViewModel(connString);
        ErrorLogVm           = new ErrorLogViewModel(dbFactory, errorLogger);
        AgentUsageVm         = new AgentUsageViewModel(dbFactory, errorLogger);
        GameLogViewerVm      = new GameLogViewerViewModel(dbFactory, errorLogger);
        ChatLogViewerVm      = new ChatLogViewerViewModel(dbFactory, errorLogger, monitoringSettings);
        AssetBrowserVm       = new AssetBrowserViewModel(connString);
        IndustryBrowserVm    = new IndustryBrowserViewModel(connString, indyFacilityCheck);
        TradeOpportunitiesVm = new TradeOpportunitiesViewModel(connString, historyService, batchAddService);
        IndustryOpportunitiesVm = new IndustryOpportunitiesViewModel(connString, historyService, batchAddService);

        agentService.AlarmToolFactory =
            () => new EveConsole.Agent.Tools.Actions.ManageAlarmsTool(
                dbFactory, alarmService.Registry, alarmService);
        // Set before Initialize — that is where the tool list is built.
        agentService.EntityBrowser = entityBrowser;
        agentService.MapService    = universeMapService;
        agentService.Esi           = esi;
        agentService.Initialize(connString);
        TtsService         = ttsService;
        SpeechInputService = speechInputService;
        HotkeyService      = hotkeyService;
        AppPrefs           = appPrefs;
        UpdateVm           = new UpdateViewModel(appPrefs, errorLogger);
        DbBackup           = dbBackup;

        var s = agentService.Settings;
        ttsService.Configure(s);
        speechInputService.Configure(s.SpeechInputProvider, s.OpenAiApiKey,
                                     s.WhisperLocalModel, s.MicrophoneDeviceName);

        AgentVm = new AgentPanelViewModel(agentService, ttsService, speechInputService, hotkeyService);

        StartEveTimeClock();
        StartOnlineCharactersWatch(dbFactory);
        BindAlarmLight(alarmService, workerActivity);

        // ⚠️ Two sources, because only one of them is ever right. On the client holding the lease
        // this process really is polling and its own status is the truth; on any other the poller
        // is stopped, so its local text would read "Polling: Not started" about a client that is
        // polling away perfectly well on another machine.
        _pollingService
            .WhenAnyValue(p => p.StatusText)
            .Subscribe(t => { if (_workerLease.IsHolder) PollingStatusText = t; });

        workerActivity.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (_workerLease.IsHolder) return;

            PollingStatusText = workerActivity.Get(WorkerActivityService.Polling)?.Status
                                is { Length: > 0 } status
                ? status
                : "Polling: on another client";
        });

        // BuildCostService.StatusText is set from a background thread — poll it via a timer.
        Observable.Interval(TimeSpan.FromSeconds(3))
            .ObserveOnUi("MainWindow.BuildCostStatus")
            .Subscribe(_ => BuildCostStatusText = _buildCostService.StatusText);

        // ── Navigation setup ──────────────────────────────────────────────────

        NavGroup[] groups =
        [
            new("General",
            [
                new NavItem("overview",    "Overview"),
                new NavItem("worklist",    "Worklist"),
                new NavItem("characters",  "Characters"),
            ]),
            new("Assets",
            [
                new NavItem("assets",     "Assets"),
                new NavItem("items",      "Item Browser"),
                new NavItem("inv_levels", "Inventory Levels"),
            ]),
            new("Structures / Navigation",
            [
                new NavItem("structure_browser", "Structure Browser"),
                new NavItem("universe",          "Universe Map"),
                new NavItem("jump_planner",      "Jump Planner"),
            ]),
            new("Industry",
            [
                new NavItem("industry",      "Industry Jobs"),
                new NavItem("indy_parks",    "Indy Parks"),
                new NavItem("prod_calc",     "Production Calc"),
                new NavItem("price_overrides", "Price Overrides"),
                new NavItem("industry_opps", "Industry Opportunities"),
            ]),
            new("Market / Trade",
            [
                new NavItem("market_viewer", "Market Overview"),
                new NavItem("lp_market_values", "LP Market Values"),
                new NavItem("market_levels", "Market Levels"),
                new NavItem("contracts",     "Contracts"),
                new NavItem("trade",         "Trade Opportunities"),
                new NavItem("standing_buy_orders", "Standing Buy Orders"),
                new NavItem("order_tracker", "Order Tracker"),
                new NavItem("sales_tracker", "Sales Tracker"),
                new NavItem("sale_posting",  "Sale Posting"),
                new NavItem("stores",        "Stores"),
            ]),
            new("Finance",
            [
                new NavItem("net_worth",     "Net Worth"),
                new NavItem("income_expense","Income & Expense"),
                new NavItem("wallet",        "Wallet"),
            ]),
            new("Corp / Interactions",
            [
                new NavItem("corp_activity", "Corp Activity"),
                new NavItem("killmails",     "Killmails"),
                new NavItem("player_entities", "Player Entities"),
                new NavItem("npc_entities",    "NPC Entities"),
            ]),
            new("Communication",
            [
                new NavItem("eve_mail", "Eve Mail"),
                new NavItem("notifications", "Notifications"),
            ]),
            new("Data / Logs",
            [
                // Alarms is reached from the alarm light beside the settings gear, not from
                // here — it is a status indicator first and a tool second.
                new NavItem("data", "ESI Explorer"),
                new NavItem("error_log", "Error Log"),
                new NavItem("ai_usage",  "AI Usage"),
                new NavItem("game_log", "Game Log"),
                new NavItem("chat_log", "Chat Log"),
            ]),
        ];

        NavGroups    = groups;
        _allNavItems = groups.SelectMany(g => g.Items).ToArray();

        OpenToolCommand = ReactiveCommand.Create<string>(OpenTool);
        CloseTabCommand = ReactiveCommand.Create<ToolTab>(CloseTab);

        OpenTool("overview");
    }

    public Task ForceResolveNamesAsync() =>
        _pollingService.ForceResolveStructureNamesAsync();
}

/// <summary>Who is doing the background work, as far as the title bar cares.</summary>
public enum WorkerOwnership
{
    /// <summary>Not yet read. Shown as neither running nor missing — saying "none" before
    /// looking would raise an alarm about a worker that is very likely fine.</summary>
    Unknown,
    Mine,
    Other,
    None,
}
