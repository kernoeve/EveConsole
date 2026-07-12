using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using EveCortex.Agent;
using EveCortex.Data;
using EveCortex.Api;
using EveCortex.Auth;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace EveCortex.ViewModels;

public class MainWindowViewModel : ReactiveObject
{
    public OverviewViewModel              OverviewVm             { get; }
    public AlertSettingsViewModel         AlertSettingsVm        { get; }
    public CharacterViewModel             CharacterVm            { get; }
    public SdeViewModel                   SdeVm                  { get; }
    public UpdateViewModel                UpdateVm               { get; }
    public ApiActivityViewModel           ActivityVm             { get; }
    public EsiExplorerViewModel           ExplorerVm             { get; }
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
    public WalletViewModel                WalletVm               { get; }
    public ContractsViewModel             ContractsVm            { get; }
    public NotificationsViewModel         NotificationsVm        { get; }
    public MarketViewerViewModel          MarketViewerVm         { get; }
    public SalesTrackerViewModel          SalesTrackerVm         { get; }
    public SaleListingViewModel           SaleListingBuildVm     { get; }
    public SaleListingViewModel           SaleListingMarketVm    { get; }
    public OrderTrackerViewModel          OrderTrackerVm         { get; }
    public MarketSettingsViewModel        MarketVm               { get; }
    public TimerSettingsViewModel         TimerVm                { get; }
    public AgentPanelViewModel            AgentVm                { get; }
    public MarketLevelViewModel           MarketLevelVm          { get; }
    public InvLevelViewModel              InvLevelVm             { get; }
    public CorpActivityViewModel          CorpActivityVm         { get; }
    public KillmailBrowserViewModel       KillmailBrowserVm      { get; }
    public EveMailViewModel               EveMailVm              { get; }
    public PriceHistorySettingsViewModel  PriceHistorySettingsVm { get; }
    public PollingSettingsViewModel       PollingSettingsVm      { get; }
    public CorpTop10SettingsViewModel     CorpTop10SettingsVm    { get; }
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
        if (existing is not null) { SelectedTab = existing; return; }

        var (title, vm, canClose) = toolId switch
        {
            "overview"   => ("Overview",       (object)OverviewVm,       false),
            "characters" => ("Characters",      CharacterViewerVm,        true),
            "assets"     => ("Assets",          AssetBrowserVm,           true),
            "items"      => ("Item Browser",    ItemBrowserVm,            true),
            "industry"   => ("Industry Jobs",   IndustryBrowserVm,        true),
            "indy_parks" => ("Indy Parks",      IndyParksVm,              true),
            "prod_calc"  => ("Production Calc", ProductionCalcVm,         true),
            "trade"           => ("Trade",           TradeOpportunitiesVm,     true),
            "industry_opps"   => ("Industry Opps",   IndustryOpportunitiesVm,  true),
            "market_levels"   => ("Market Levels",   MarketLevelVm,            true),
            "inv_levels"      => ("Inv. Levels",     InvLevelVm,               true),
            "net_worth"  => ("Net Worth",       NetWorthVm,               true),
            "income_expense" => ("Income & Expense", IncomeExpenseVm,     true),
            "wallet"         => ("Wallet",          WalletVm,          true),
            "contracts"      => ("Contracts",       ContractsVm,       true),
            "market_viewer"  => ("Market Overview", MarketViewerVm,    true),
            "sales_tracker"  => ("Sales Tracker",   SalesTrackerVm,    true),
            "sale_list_build"  => ("Sale Listing (Build)",  SaleListingBuildVm,  true),
            "sale_list_market" => ("Sale Listing (Market)", SaleListingMarketVm, true),
            "order_tracker"  => ("Order Tracker",   OrderTrackerVm,    true),
            "corp_activity"  => ("Corp Activity",  CorpActivityVm,    true),
            "killmails"      => ("Killmails",      KillmailBrowserVm, true),
            "eve_mail"       => ("Eve Mail",       EveMailVm,         true),
            "notifications"  => ("Notifications",  NotificationsVm,   true),
            "data"           => ("ESI Explorer",   ExplorerVm,        true),
            _                => throw new ArgumentException($"Unknown tool: {toolId}")
        };

        var tab = new ToolTab(toolId, title, vm, canClose);
        OpenTabs.Add(tab);
        SelectedTab = tab;

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
        BatchAddService                 batchAddService,
        CorpActivityService             corpActivityService,
        KillmailBrowserService          killmailBrowserService,
        BuildCostService                buildCostService,
        ProductionCalculatorService     prodCalcService,
        IServiceScopeFactory            scopeFactory,
        TimerSettingsService            timerSettings,
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
        ContractsService                contractsService)
    {
        AlertSettingsVm   = new AlertSettingsViewModel(dbFactory.CreateDbContext());
        OverviewVm        = new OverviewViewModel(dbFactory.CreateDbContext(), AlertSettingsVm, errorLogger, newsService, appPrefs, corpActivityService, dbFactory, esi);
        CharacterVm       = new CharacterViewModel(auth, esi, dbFactory.CreateDbContext());
        SdeVm             = new SdeViewModel(sdeService, hoboService, dbFactory.CreateDbContext());
        ActivityVm        = new ApiActivityViewModel(activityLog, scopeFactory, pollingService, timerSettings, historyService, contractsService);
        CharacterViewerVm = new CharacterViewerViewModel(dbFactory.CreateDbContext(), CharacterVm.Characters);
        NetWorthVm        = new NetWorthViewModel(dbFactory);
        IncomeExpenseVm   = new IncomeExpenseViewModel(dbFactory, errorLogger);
        MarketVm          = new MarketSettingsViewModel(dbFactory.CreateDbContext(), dbFactory, marketPricing, esi, CharacterVm.Characters, buildCostService);
        var fittingsService = new FittingsService(esi, dbFactory);
        MarketLevelVm     = new MarketLevelViewModel(marketLevelService, dbFactory, fittingsService,
            CharacterVm.Characters, CharacterVm.Corporations, batchAddService, prodCalcService);
        InvLevelVm        = new InvLevelViewModel(invLevelService, dbFactory, batchAddService,
            prodCalcService, fittingsService, CharacterVm.Characters, CharacterVm.Corporations);
        CorpActivityVm    = new CorpActivityViewModel(corpActivityService, CharacterVm.Corporations, corpTop10Exclude);
        KillmailBrowserVm = new KillmailBrowserViewModel(killmailBrowserService, CharacterVm.Corporations);
        MailSvc           = eveMailService;
        EveMailVm         = new EveMailViewModel(eveMailService, CharacterVm.Characters);
        CorpActivityVm.RequestOpenKillmail = killMailId =>
        {
            OpenTool("killmails");
            // Sync the corp selection so the browser uses the same corp context
            if (CorpActivityVm.SelectedCorp is { } corp &&
                KillmailBrowserVm.SelectedCorp?.Id != corp.Id)
                KillmailBrowserVm.SelectedCorp = corp;
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
        OverviewVm.RequestOpenKillmail = killMailId =>
        {
            OpenTool("killmails");
            KillmailBrowserVm.SelectedCorp = KillmailBrowserViewModel.AllCorps;
            KillmailBrowserVm.SelectById(killMailId);
        };
        OverviewVm.OpenToolRequested = OpenTool;
        TimerVm           = new TimerSettingsViewModel(pollingService, timerSettings);
        _pollingService   = pollingService;
        _buildCostService = buildCostService;

        PriceHistorySettingsVm = new PriceHistorySettingsViewModel(dbFactory.CreateDbContext());
        PollingSettingsVm      = new PollingSettingsViewModel(appPrefs);
        CorpTop10SettingsVm    = new CorpTop10SettingsViewModel(corpTop10Exclude);
        ItemBrowserVm          = new ItemBrowserViewModel(dbFactory.CreateDbContext(), historyService);
        IndyParksVm            = new IndyParksViewModel(dbFactory);
        WalletVm               = new WalletViewModel(dbFactory, errorLogger);
        ContractsVm            = new ContractsViewModel(dbFactory, esi, errorLogger);
        NotificationsVm        = new NotificationsViewModel(dbFactory, esi, errorLogger);
        MarketViewerVm         = new MarketViewerViewModel(dbFactory, errorLogger);
        SalesTrackerVm         = new SalesTrackerViewModel(dbFactory, errorLogger, corpActivityService);
        SaleListingBuildVm     = new SaleListingViewModel(dbFactory, errorLogger, corpActivityService, SaleCostBasis.BuildCost);
        SaleListingMarketVm    = new SaleListingViewModel(dbFactory, errorLogger, corpActivityService, SaleCostBasis.MarketValue);
        OverviewVm.SaleListingBuild  = SaleListingBuildVm;   // let the Overview embed them as sections
        OverviewVm.SaleListingMarket = SaleListingMarketVm;
        SaleListingBuildVm.OpenSalesTracker  = () => OpenTool("sales_tracker");
        SaleListingMarketVm.OpenSalesTracker = () => OpenTool("sales_tracker");
        OrderTrackerVm         = new OrderTrackerViewModel(dbFactory, errorLogger);
        ProductionCalcVm       = new ProductionCalculatorViewModel(dbFactory, prodCalcService);
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

        using var tmpDb      = dbFactory.CreateDbContext();
        var connString       = tmpDb.Database.GetConnectionString()!;
        ExplorerVm           = new EsiExplorerViewModel(connString);
        AssetBrowserVm       = new AssetBrowserViewModel(connString);
        IndustryBrowserVm    = new IndustryBrowserViewModel(connString);
        TradeOpportunitiesVm = new TradeOpportunitiesViewModel(connString, historyService, batchAddService);
        IndustryOpportunitiesVm = new IndustryOpportunitiesViewModel(connString, historyService, batchAddService);

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

        _pollingService
            .WhenAnyValue(p => p.StatusText)
            .Subscribe(t => PollingStatusText = t);

        // BuildCostService.StatusText is set from a background thread — poll it via a timer.
        Observable.Interval(TimeSpan.FromSeconds(3))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => BuildCostStatusText = _buildCostService.StatusText);

        // ── Navigation setup ──────────────────────────────────────────────────

        NavGroup[] groups =
        [
            new("Character",
            [
                new NavItem("overview",    "Overview"),
                new NavItem("characters",  "Characters"),
            ]),
            new("Assets",
            [
                new NavItem("assets",     "Assets"),
                new NavItem("items",      "Item Browser"),
                new NavItem("inv_levels", "Inventory Levels"),
            ]),
            new("Industry",
            [
                new NavItem("industry",      "Industry Jobs"),
                new NavItem("indy_parks",    "Indy Parks"),
                new NavItem("prod_calc",     "Production Calc"),
                new NavItem("industry_opps", "Industry Opportunities"),
            ]),
            new("Market / Trade",
            [
                new NavItem("market_levels", "Market Levels"),
                new NavItem("market_viewer", "Market Overview"),
                new NavItem("sales_tracker", "Sales Tracker"),
                new NavItem("sale_list_build",  "Sale Listing (Build)"),
                new NavItem("sale_list_market", "Sale Listing (Market)"),
                new NavItem("order_tracker", "Order Tracker"),
                new NavItem("trade",         "Trade Opportunities"),
                new NavItem("contracts",     "Contracts"),
            ]),
            new("Finance",
            [
                new NavItem("net_worth",     "Net Worth"),
                new NavItem("income_expense","Income & Expense"),
                new NavItem("wallet",        "Wallet"),
                new NavItem("corp_activity", "Corp Activity"),
                new NavItem("killmails",     "Killmails"),
            ]),
            new("Communication",
            [
                new NavItem("eve_mail", "Eve Mail"),
                new NavItem("notifications", "Notifications"),
            ]),
            new("Tools",
            [
                new NavItem("data", "ESI Explorer"),
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
