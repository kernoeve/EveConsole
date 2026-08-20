using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveConsole.Agent;
using EveConsole.Data;
using EveConsole.Api;
using EveConsole.Auth;
using EveConsole.Monitoring;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace EveConsole.ViewModels;

public class MainWindowViewModel : ReactiveObject
{
    public OverviewViewModel              OverviewVm             { get; }
    public AlertSettingsViewModel         AlertSettingsVm        { get; }
    public CharacterViewModel             CharacterVm            { get; }
    public SdeViewModel                   SdeVm                  { get; }
    public UpdateViewModel                UpdateVm               { get; }
    public ApiActivityViewModel           ActivityVm             { get; }
    public EsiExplorerViewModel           ExplorerVm             { get; }
    public ErrorLogViewModel              ErrorLogVm             { get; }
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

    private string _alarmLightColor = "#2a2a34";
    public string AlarmLightColor
    {
        get => _alarmLightColor;
        private set => this.RaiseAndSetIfChanged(ref _alarmLightColor, value);
    }

    private string _alarmLightRing = "#3a3a48";
    public string AlarmLightRing
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
    private void BindAlarmLight(AlarmService alarms)
    {
        alarms.WhenAnyValue(x => x.ArmedCount)
            .Subscribe(count => Dispatcher.UIThread.Post(() =>
            {
                ActiveAlarmCount = count;
                HasActiveAlarms  = count > 0;

                AlarmLightColor   = count > 0 ? "#c0392b" : "#2a2a34";
                AlarmLightRing    = count > 0 ? "#e05a4a" : "#3a3a48";
                AlarmGleamOpacity = count > 0 ? 0.55 : 0.18;

                AlarmsTip = count switch
                {
                    0 => "Alarms — none armed",
                    1 => "Alarms — 1 armed",
                    _ => $"Alarms — {count} armed",
                };
            }));
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
    private string _onlineCharactersColor = "#444455";
    public string OnlineCharactersColor
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
                OnlineCharactersColor = online.Count > 0 ? "#70ad47" : "#444455";
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

    // ── Tranquility status (shown beside the EVE clock) ─────────────────────────

    private string _serverStatusText = "Online";
    public string ServerStatusText { get => _serverStatusText; private set => this.RaiseAndSetIfChanged(ref _serverStatusText, value); }

    private string _serverStatusColor = "#70ad47";
    public string ServerStatusColor { get => _serverStatusColor; private set => this.RaiseAndSetIfChanged(ref _serverStatusColor, value); }

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
            if (toolId == "alarms") _ = AlarmsVm.LoadAsync();
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
            "jump_planner"    => ("Jump Planner",    JumpPlannerVm,     true),
            "trade"           => ("Trade",           TradeOpportunitiesVm,     true),
            "industry_opps"   => ("Industry Opps",   IndustryOpportunitiesVm,  true),
            "market_levels"   => ("Market Levels",   MarketLevelVm,            true),
            "inv_levels"      => ("Inv. Levels",     InvLevelVm,               true),
            "sale_posting"    => ("Sale Posting",    SalePostingVm,            true),
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
        if (toolId == "alarms") _ = AlarmsVm.LoadAsync();

        var navItem = _allNavItems.FirstOrDefault(i => i.ToolId == toolId);
        if (navItem is not null) navItem.IsOpen = true;
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
        ApiActivityLog                  activityLog,
        MarketPricingService            marketPricing,
        MarketLevelService              marketLevelService,
        InvLevelService                 invLevelService,
        SalePostingService              salePostingService,
        BatchAddService                 batchAddService,
        CorpActivityService             corpActivityService,
        CharacterSummaryService         characterSummaryService,
        StandingBuyOrderService         standingBuyOrderService,
        EveConsole.Services.Worklist.WorklistService worklistService,
        EveConsole.Services.Worklist.WorklistMarketAltService worklistMarketAltService,
        EveConsole.Services.Worklist.WorklistCorpAltService worklistCorpAltService,
        EveConsole.Services.Worklist.IndustryAssignmentService industryAssignmentService,
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
        LpValueService                  lpValueService)
    {
        AlarmActions = alarmActions;
        _uiLinks        = uiLinks;
        OtherSettingsVm = new OtherSettingsViewModel(uiLinks);
        DataRetentionVm = new DataRetentionSettingsViewModel(dataRetention);
        BindServerStatus(serverStatus);

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
        ActivityVm        = new ApiActivityViewModel(activityLog, scopeFactory, pollingService, timerSettings, historyService, contractsService,
                                                     zkillboardSettings, zkbPolling, zkbFirehose, zkbBackfill, zkbPost,
                                                     intelService, monitoringSettings, entityNames, alarmService, orderFulfilment, lpStoreService);
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
        CorpActivityVm    = new CorpActivityViewModel(corpActivityService, CharacterVm.Corporations, corpTop10Exclude, slackService, exportFormat);
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
        CorpTop10SettingsVm    = new CorpTop10SettingsViewModel(corpTop10Exclude);
        ItemBrowserVm          = new ItemBrowserViewModel(dbFactory.CreateDbContext(), historyService, dbFactory, appPrefs);
        IndyParksVm            = new IndyParksViewModel(dbFactory, corpActivityService, errorLogger,
                                                        indyStructureLink, indyBulkAdd, pollingService);
        WalletVm               = new WalletViewModel(dbFactory, errorLogger);
        ContractsVm            = new ContractsViewModel(dbFactory, esi, errorLogger);
        NotificationsVm        = new NotificationsViewModel(dbFactory, esi, errorLogger);
        MarketViewerVm         = new MarketViewerViewModel(dbFactory, errorLogger);
        SalesTrackerVm         = new SalesTrackerViewModel(dbFactory, errorLogger, corpActivityService);
        SaleListingBuildVm     = new SaleListingViewModel(dbFactory, errorLogger, corpActivityService, SaleCostBasis.BuildCost);
        SaleListingMarketVm    = new SaleListingViewModel(dbFactory, errorLogger, corpActivityService, SaleCostBasis.MarketValue);
        OverviewVm.SaleListingBuild  = SaleListingBuildVm;   // let the Overview embed them as sections
        OverviewVm.SaleListingMarket = SaleListingMarketVm;
        OverviewVm.IncomeExpense     = IncomeExpenseVm;
        SaleListingBuildVm.OpenSalesTracker  = () => OpenTool("sales_tracker");
        SaleListingMarketVm.OpenSalesTracker = () => OpenTool("sales_tracker");
        OrderTrackerVm         = new OrderTrackerViewModel(dbFactory, errorLogger);
        StandingBuyOrdersVm    = new StandingBuyOrdersViewModel(standingBuyOrderService, corpActivityService);
        WorklistVm             = new WorklistViewModel(worklistService,
                                     new WorklistMarketAltsViewModel(worklistMarketAltService, corpActivityService, dbFactory),
                                     new WorklistInvRulesViewModel(dbFactory, corpActivityService, worklistMarketAltService),
                                     new WorklistCorpAltsViewModel(dbFactory, worklistCorpAltService),
                                     new WorklistIndustryViewModel(dbFactory, industryAssignmentService, worklistSettings, errorLogger, corpActivityService, worklistMarketAltService),
                                     new WorklistStationLevelsViewModel(dbFactory, corpActivityService, worklistSettings));

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
        var entityBrowser      = new EntityBrowserService(dbFactory, esi);
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
        AlarmsVm               = new AlarmsViewModel(dbFactory, alarmService, alarmSounds);
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
        BindAlarmLight(alarmService);

        _pollingService
            .WhenAnyValue(p => p.StatusText)
            .Subscribe(t => PollingStatusText = t);

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
