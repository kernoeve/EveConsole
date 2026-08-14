using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EveConsole.Agent;
using EveConsole.Alarms;
using EveConsole.Data;
using EveConsole.Views;
using EveConsole.ViewModels;
using EveConsole.Auth;
using EveConsole.Api;
using EveConsole.Monitoring;
using EveConsole.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace EveConsole;

public class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        LiveCharts.Configure(config => config.AddSkiaSharp().AddDefaultMappers());
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        // Build the DI container (fast — no I/O)
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // Wire up global exception handlers so truly unhandled failures are persisted
        var errorLogger = Services.GetRequiredService<AppErrorLogger>();

        // Installed here, before any view model exists: ObserveOn captures the scheduler when a
        // subscription is created, so anything wired earlier would never be measured.
        ReactiveUI.RxApp.MainThreadScheduler =
            new TimedMainThreadScheduler(ReactiveUI.RxApp.MainThreadScheduler, errorLogger);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                errorLogger.Log("AppDomain", "UnhandledException", ex);
        };
        // The one that can actually prevent a crash. AppDomain.UnhandledException below only
        // observes — the process still dies. An async void event handler that throws posts to
        // this dispatcher, and there are thirty such handlers across the views, so guarding them
        // individually would be a list to keep in step forever.
        //
        // Marking it handled is the right trade for what actually lands here: a transient
        // SQLITE_BUSY from two writers colliding, already retried for thirty seconds. Losing the
        // action is a nuisance; losing the session mid-setup is not. Everything is logged, so a
        // real defect still surfaces in the error log rather than vanishing.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            errorLogger.Log("Dispatcher", "UnhandledException", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            errorLogger.Log("TaskScheduler", "UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        EsiPollingService?    polling       = null;
        MarketPricingService? marketPricing = null;
        MarketHistoryService? marketHistory = null;
        ContractsService?     contracts     = null;
        LpStoreService?       lpStore       = null;
        GameLogImportService?       gameLogs      = null;
        ChatLogImportService?       chatLogs      = null;
        ZkillboardPollingService?   zkbPolling    = null;
        ZkillboardFirehoseService?  zkbFirehose   = null;
        ZkillboardBackfillService?  zkbBackfill   = null;
        ZkillboardPostService?      zkbPost       = null;
        MainWindow?           mainWindow    = null;
        SplashWindow?         splash        = null;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Keep the app alive via OnLastWindowClose while only the splash is open.
            // We switch back to OnMainWindowClose once the main window is shown.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;

            polling       = Services.GetRequiredService<EsiPollingService>();
            marketPricing = Services.GetRequiredService<MarketPricingService>();
            marketHistory = Services.GetRequiredService<MarketHistoryService>();
            contracts     = Services.GetRequiredService<ContractsService>();
            lpStore       = Services.GetRequiredService<LpStoreService>();
            gameLogs      = Services.GetRequiredService<GameLogImportService>();
            chatLogs      = Services.GetRequiredService<ChatLogImportService>();
            zkbPolling    = Services.GetRequiredService<ZkillboardPollingService>();

            // Intel is parsed straight after each chat tail, so a sighting reaches the map on
            // the same pass that stored the message rather than on a timer of its own — and any
            // intel alarm is then evaluated immediately rather than at its next interval, which
            // is the difference between hearing about a hostile in a second and in a minute.
            var intel = Services.GetRequiredService<IntelService>();
            chatLogs.AfterTail = async ct =>
            {
                var written = await intel.ProcessNewAsync(ct);
                if (written > 0)
                    await Services.GetRequiredService<AlarmService>().TriggerAsync("intel", ct);
            };
            zkbFirehose   = Services.GetRequiredService<ZkillboardFirehoseService>();
            zkbBackfill   = Services.GetRequiredService<ZkillboardBackfillService>();
            zkbPost       = Services.GetRequiredService<ZkillboardPostService>();

            var buildCostService = Services.GetRequiredService<BuildCostService>();
            var reprService      = Services.GetRequiredService<ReprocessingValueService>();
            var typePriceHistory = Services.GetRequiredService<TypePriceHistoryService>();
            marketPricing.AfterRefresh        = ct => buildCostService.RunAfterMarketRefreshAsync(ct);
            // Fill price gaps first, then snapshot today's per-type prices (market + build now final).
            buildCostService.AfterRecalculate += async ct =>
            {
                await marketPricing.FillAllGapsAsync(ct);
                await typePriceHistory.RecalculateAsync(ct);
            };
            buildCostService.AfterRecalculate += ct => reprService.RecalculateAllAsync(ct);

            // LP values are priced off the market, so they follow the same trigger as build
            // costs — and run after the gap fill above, so they see final prices rather than
            // the holes it just closed.
            var lpValues = Services.GetRequiredService<LpValueService>();
            buildCostService.AfterRecalculate += ct => lpValues.RecalculateAsync(ct);

            // Contract prices refresh on their own loop — re-snapshot when they do. LP
            // valuation falls back to contract prices where an item has no market price, so
            // it has the same reason to re-run.
            contracts.AfterPricing += ct => typePriceHistory.RecalculateAsync(ct);
            contracts.AfterPricing += ct => lpValues.RecalculateAsync(ct);

            desktop.ShutdownRequested += async (_, e) =>
            {
                e.Cancel = true;
                var tasks = new List<Task>();
                if (polling       is not null) tasks.Add(polling.StopAsync());
                if (marketPricing is not null) tasks.Add(marketPricing.StopAsync());
                if (marketHistory is not null) tasks.Add(marketHistory.StopAsync());
                if (contracts     is not null) tasks.Add(contracts.StopAsync());
                if (lpStore       is not null) tasks.Add(lpStore.StopAsync());
                if (gameLogs      is not null) tasks.Add(gameLogs.StopAsync());
                if (chatLogs      is not null) tasks.Add(chatLogs.StopAsync());
                await Task.WhenAll(tasks);
                desktop.Shutdown();
            };

            splash = new SplashWindow();
            PositionSplashOnLastMonitor(splash);
        }

        base.OnFrameworkInitializationCompleted();
        splash?.Show();

        // Progress relay — IProgress<T> always posts back to the UI thread.
        var progress = new Progress<(double Pct, string Status)>(r =>
            splash?.ReportProgress(r.Pct, r.Status));
        var p = (IProgress<(double, string)>)progress;

        // ── Heavy startup on a thread-pool thread ──────────────────────────────
        await Task.Run(() =>
        {
        p.Report((5, "Initializing database…"));
        // Ensure the database is created / migrated
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "SdeDogmaAttributeCategories" (
                    "CategoryId" INTEGER NOT NULL PRIMARY KEY,
                    "Name"       TEXT    NOT NULL
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "SdeBuildInfos" (
                    "Id"          INTEGER NOT NULL CONSTRAINT "PK_SdeBuildInfos" PRIMARY KEY,
                    "BuildNumber" INTEGER NOT NULL,
                    "ReleaseDate" TEXT    NOT NULL,
                    "ImportedAt"  TEXT    NOT NULL
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "Corporations" (
                    "Id"                   INTEGER NOT NULL CONSTRAINT "PK_Corporations" PRIMARY KEY,
                    "Name"                 TEXT    NOT NULL,
                    "Ticker"               TEXT    NOT NULL,
                    "AuthCharacterId"      INTEGER NOT NULL,
                    "RefreshToken"         TEXT    NOT NULL DEFAULT '',
                    "GrantedScopes"        TEXT    NOT NULL DEFAULT '',
                    "AccessTokenExpiresAt" TEXT,
                    "IsPersonal"           INTEGER NOT NULL DEFAULT 0,
                    "LastUpdated"          TEXT    NOT NULL
                )
                """);
            // Net worth history — one row per owner per UTC day
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "NetWorthSnapshots" (
                    "OwnerId"            INTEGER NOT NULL,
                    "OwnerType"          TEXT    NOT NULL,
                    "Date"               TEXT    NOT NULL,
                    "AssetValue"         REAL    NOT NULL DEFAULT 0,
                    "IndustryJobValue"   REAL    NOT NULL DEFAULT 0,
                    "WalletBalance"      REAL    NOT NULL DEFAULT 0,
                    "SellOrderValue"     REAL    NOT NULL DEFAULT 0,
                    "BuyOrderEscrow"     REAL    NOT NULL DEFAULT 0,
                    "ContractCollateral" REAL    NOT NULL DEFAULT 0,
                    "ContractValue"      REAL    NOT NULL DEFAULT 0,
                    "Total"              REAL    NOT NULL DEFAULT 0,
                    "ComputedAt"         TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("OwnerId", "OwnerType", "Date")
                )
                """);

            // Per-type price history — one row per TypeId per UTC day (market / build / contract).
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "TypePriceSnapshots" (
                    "TypeId"        INTEGER NOT NULL,
                    "Date"          TEXT    NOT NULL,
                    "MarketValue"   REAL,
                    "BuildCost"     REAL,
                    "ContractPrice" REAL,
                    "ComputedAt"    TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("TypeId", "Date")
                )
                """);

            // Order Tracker — user-entered outgoing orders.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "TrackedOrders" (
                    "Id"            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "TypeId"        INTEGER NOT NULL DEFAULT 0,
                    "Units"         INTEGER NOT NULL DEFAULT 1,
                    "Buyer"         TEXT    NOT NULL DEFAULT '',
                    "EstimatedDate" TEXT,
                    "PurchasePrice" REAL    NOT NULL DEFAULT 0,
                    "Status"        TEXT    NOT NULL DEFAULT 'pending',
                    "CreatedAt"     TEXT    NOT NULL DEFAULT ''
                )
                """);

            // Sale Posting — postings → sections → items (see SalePostingModels.cs)
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "SalePostings" (
                    "Id"               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "Name"             TEXT    NOT NULL DEFAULT '',
                    "Scope"            TEXT    NOT NULL DEFAULT 'Everywhere',
                    "LocationId"       INTEGER,
                    "LocationName"     TEXT    NOT NULL DEFAULT '',
                    "PricingBasis"      TEXT    NOT NULL DEFAULT 'Build',
                    "PricePercent"      REAL    NOT NULL DEFAULT 110,
                    "MarketStationId"   INTEGER,
                    "MarketStationName" TEXT    NOT NULL DEFAULT '',
                    "MarketPriceType"   TEXT    NOT NULL DEFAULT 'Sell',
                    "ShowInStock"       INTEGER NOT NULL DEFAULT 1,
                    "ShowInBuild"       INTEGER NOT NULL DEFAULT 1,
                    "ShowReserved"      INTEGER NOT NULL DEFAULT 1,
                    "IncludeCompletionDate" INTEGER NOT NULL DEFAULT 0,
                    "OnlyPackaged"      INTEGER NOT NULL DEFAULT 0
                )
                """);
            // Existing installs created before the Market-basis reworked to station pricing.
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "SalePostings" ADD COLUMN "MarketStationId" INTEGER"""); } catch { }
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "SalePostings" ADD COLUMN "MarketStationName" TEXT NOT NULL DEFAULT ''"""); } catch { }
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "SalePostings" ADD COLUMN "MarketPriceType" TEXT NOT NULL DEFAULT 'Sell'"""); } catch { }
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "SalePostings" ADD COLUMN "IncludeCompletionDate" INTEGER NOT NULL DEFAULT 0"""); } catch { }
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "SalePostings" ADD COLUMN "OnlyPackaged" INTEGER NOT NULL DEFAULT 0"""); } catch { }
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "SalePostingSections" (
                    "Id"                INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "PostingId"         INTEGER NOT NULL DEFAULT 0,
                    "Name"              TEXT    NOT NULL DEFAULT '',
                    "Prefix"            TEXT    NOT NULL DEFAULT '',
                    "OverrideScope"     INTEGER NOT NULL DEFAULT 0,
                    "Scope"             TEXT    NOT NULL DEFAULT 'Everywhere',
                    "LocationId"        INTEGER,
                    "LocationName"      TEXT    NOT NULL DEFAULT '',
                    "OverridePricing"   INTEGER NOT NULL DEFAULT 0,
                    "PricingBasis"      TEXT    NOT NULL DEFAULT 'Build',
                    "PricePercent"      REAL    NOT NULL DEFAULT 110,
                    "MarketStationId"   INTEGER,
                    "MarketStationName" TEXT    NOT NULL DEFAULT '',
                    "MarketPriceType"   TEXT    NOT NULL DEFAULT 'Sell',
                    "OverrideOnlyPackaged" INTEGER NOT NULL DEFAULT 0,
                    "OnlyPackaged"      INTEGER NOT NULL DEFAULT 0
                )
                """);
            // Existing installs created before section-level overrides.
            foreach (var col in new[] {
                """ALTER TABLE "SalePostingSections" ADD COLUMN "Prefix" TEXT NOT NULL DEFAULT ''""",
                """ALTER TABLE "SalePostingSections" ADD COLUMN "OverrideScope" INTEGER NOT NULL DEFAULT 0""",
                """ALTER TABLE "SalePostingSections" ADD COLUMN "Scope" TEXT NOT NULL DEFAULT 'Everywhere'""",
                """ALTER TABLE "SalePostingSections" ADD COLUMN "LocationId" INTEGER""",
                """ALTER TABLE "SalePostingSections" ADD COLUMN "LocationName" TEXT NOT NULL DEFAULT ''""",
                """ALTER TABLE "SalePostingSections" ADD COLUMN "OverridePricing" INTEGER NOT NULL DEFAULT 0""",
                """ALTER TABLE "SalePostingSections" ADD COLUMN "PricingBasis" TEXT NOT NULL DEFAULT 'Build'""",
                """ALTER TABLE "SalePostingSections" ADD COLUMN "PricePercent" REAL NOT NULL DEFAULT 110""",
                """ALTER TABLE "SalePostingSections" ADD COLUMN "MarketStationId" INTEGER""",
                """ALTER TABLE "SalePostingSections" ADD COLUMN "MarketStationName" TEXT NOT NULL DEFAULT ''""",
                """ALTER TABLE "SalePostingSections" ADD COLUMN "MarketPriceType" TEXT NOT NULL DEFAULT 'Sell'""",
                """ALTER TABLE "SalePostingSections" ADD COLUMN "OverrideOnlyPackaged" INTEGER NOT NULL DEFAULT 0""",
                """ALTER TABLE "SalePostingSections" ADD COLUMN "OnlyPackaged" INTEGER NOT NULL DEFAULT 0""",
            }) { try { db.Database.ExecuteSqlRaw(col); } catch { } }
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "SalePostingItems" (
                    "Id"               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "SectionId"        INTEGER NOT NULL DEFAULT 0,
                    "TypeId"           INTEGER NOT NULL DEFAULT 0,
                    "NameOverride"     TEXT,
                    "NamePrefix"       TEXT,
                    "InStockOverride"  INTEGER,
                    "InBuildOverride"  INTEGER,
                    "ReservedOverride" INTEGER
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "SalePostingPosts" (
                    "Id"            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "PostingId"     INTEGER NOT NULL DEFAULT 0,
                    "Ordinal"       INTEGER NOT NULL DEFAULT 0,
                    "PostType"      TEXT    NOT NULL DEFAULT 'Summary',
                    "Name"          TEXT    NOT NULL DEFAULT '',
                    "StaticContent" TEXT,
                    "Header"        TEXT    NOT NULL DEFAULT '',
                    "Footer"        TEXT    NOT NULL DEFAULT ''
                )
                """);
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "SalePostingPosts" ADD COLUMN "Header" TEXT NOT NULL DEFAULT ''"""); } catch { }
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "SalePostingPosts" ADD COLUMN "Footer" TEXT NOT NULL DEFAULT ''"""); } catch { }

            // Market price history — on-demand ESI fetch cache
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "MarketTypeHistories" (
                    "RegionId"   INTEGER NOT NULL,
                    "TypeId"     INTEGER NOT NULL,
                    "Date"       TEXT    NOT NULL,
                    "Average"    REAL    NOT NULL,
                    "Highest"    REAL    NOT NULL,
                    "Lowest"     REAL    NOT NULL,
                    "Volume"     INTEGER NOT NULL,
                    "OrderCount" INTEGER NOT NULL,
                    PRIMARY KEY ("RegionId", "TypeId", "Date")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "MarketHistoryFetches" (
                    "RegionId"  INTEGER NOT NULL,
                    "TypeId"    INTEGER NOT NULL,
                    "FetchedAt" TEXT    NOT NULL,
                    "HadData"   INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY ("RegionId", "TypeId")
                )
                """);
            // HadData added later — backfill on existing DBs.
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "MarketHistoryFetches" ADD COLUMN "HadData" INTEGER NOT NULL DEFAULT 1"""); }
            catch { /* column already present */ }

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "PriceHistoryRegions" (
                    "RegionId"   INTEGER NOT NULL CONSTRAINT "PK_PriceHistoryRegions" PRIMARY KEY,
                    "RegionName" TEXT    NOT NULL
                )
                """);
            // Seed default price-history regions on first run: The Forge and Domain.
            db.Database.ExecuteSqlRaw("""
                INSERT INTO "PriceHistoryRegions" ("RegionId", "RegionName")
                SELECT 10000002, 'The Forge' WHERE NOT EXISTS (SELECT 1 FROM "PriceHistoryRegions")
                UNION ALL
                SELECT 10000043, 'Domain'    WHERE NOT EXISTS (SELECT 1 FROM "PriceHistoryRegions")
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "MarketLevelGroups" (
                    "Id"              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "Name"            TEXT    NOT NULL DEFAULT '',
                    "StationId"       INTEGER NOT NULL DEFAULT 0,
                    "StationName"     TEXT    NOT NULL DEFAULT '',
                    "MarketSourceId"  INTEGER,
                    "MaxPriceOverPct" REAL,
                    "CollectionId"    INTEGER,
                    "Multiplier"      INTEGER NOT NULL DEFAULT 1
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "MarketLevelItems" (
                    "Id"             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "GroupId"        INTEGER NOT NULL DEFAULT 0,
                    "TypeId"         INTEGER NOT NULL DEFAULT 0,
                    "TargetQuantity" INTEGER NOT NULL DEFAULT 1
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "InvLevelGroups" (
                    "Id"                     INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "Name"                   TEXT    NOT NULL DEFAULT '',
                    "Multiplier"             INTEGER NOT NULL DEFAULT 1,
                    "Scope"                  TEXT    NOT NULL DEFAULT 'Everywhere',
                    "LocationId"             INTEGER,
                    "LocationName"           TEXT    NOT NULL DEFAULT '',
                    "IncludeAssets"          INTEGER NOT NULL DEFAULT 1,
                    "IncludeIndustryJobs"    INTEGER NOT NULL DEFAULT 0,
                    "IncludeMarketBuyOrders" INTEGER NOT NULL DEFAULT 0,
                    "IncludeContractsBuying" INTEGER NOT NULL DEFAULT 0,
                    "CollectionId"           INTEGER
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "InvLevelItems" (
                    "Id"             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "GroupId"        INTEGER NOT NULL DEFAULT 0,
                    "TypeId"         INTEGER NOT NULL DEFAULT 0,
                    "TargetQuantity" INTEGER NOT NULL DEFAULT 1
                )
                """);

            // ── Collections (new tables + alter existing tables) ─────────────
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "MarketLevelCollections" (
                    "Id"   INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "Name" TEXT    NOT NULL DEFAULT ''
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "InvLevelCollections" (
                    "Id"   INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "Name" TEXT    NOT NULL DEFAULT ''
                )
                """);

            p.Report((20, "Building character tables…"));
            // ── Polled-data tables — drop old names, create Esi* names ──────────

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCallRecords" (
                    "OwnerId"        INTEGER NOT NULL,
                    "OwnerType"      TEXT    NOT NULL,
                    "Endpoint"       TEXT    NOT NULL,
                    "LastCalledAt"   TEXT    NOT NULL,
                    "LastStatusCode" INTEGER NOT NULL DEFAULT 200,
                    PRIMARY KEY ("OwnerId", "OwnerType", "Endpoint")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "ApiTimerSettings" (
                    "Key"             TEXT    NOT NULL,
                    "IntervalSeconds" INTEGER NOT NULL DEFAULT 3600,
                    PRIMARY KEY ("Key")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiWalletBalances" (
                    "OwnerId"   INTEGER NOT NULL,
                    "OwnerType" TEXT    NOT NULL,
                    "Division"  INTEGER NOT NULL,
                    "Balance"   TEXT    NOT NULL DEFAULT '0',
                    "UpdatedAt" TEXT    NOT NULL,
                    PRIMARY KEY ("OwnerId", "OwnerType", "Division")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCharacterAttributes" (
                    "CharacterId"               INTEGER NOT NULL CONSTRAINT "PK_EsiCharacterAttributes" PRIMARY KEY,
                    "Charisma"                  INTEGER NOT NULL DEFAULT 0,
                    "Intelligence"              INTEGER NOT NULL DEFAULT 0,
                    "Memory"                    INTEGER NOT NULL DEFAULT 0,
                    "Perception"                INTEGER NOT NULL DEFAULT 0,
                    "Willpower"                 INTEGER NOT NULL DEFAULT 0,
                    "BonusRemaps"               INTEGER NOT NULL DEFAULT 0,
                    "LastRemapDate"             TEXT,
                    "AccruingRemapCooldownDate" TEXT,
                    "UpdatedAt"                 TEXT    NOT NULL
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCloneStates" (
                    "CharacterId"           INTEGER NOT NULL CONSTRAINT "PK_EsiCloneStates" PRIMARY KEY,
                    "HomeLocationId"        INTEGER,
                    "HomeLocationType"      TEXT,
                    "LastCloneJumpDate"     TEXT,
                    "LastStationChangeDate" TEXT,
                    "UpdatedAt"             TEXT    NOT NULL
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCharacterFatigues" (
                    "CharacterId"           INTEGER NOT NULL CONSTRAINT "PK_EsiCharacterFatigues" PRIMARY KEY,
                    "LastJumpDate"          TEXT,
                    "JumpFatigueExpireDate" TEXT,
                    "LastUpdateDate"        TEXT,
                    "UpdatedAt"             TEXT    NOT NULL
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiSkills" (
                    "CharacterId"        INTEGER NOT NULL,
                    "SkillId"            INTEGER NOT NULL,
                    "TrainedSkillLevel"  INTEGER NOT NULL DEFAULT 0,
                    "ActiveSkillLevel"   INTEGER NOT NULL DEFAULT 0,
                    "SkillpointsInSkill" INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("CharacterId", "SkillId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiSkillQueue" (
                    "CharacterId"     INTEGER NOT NULL,
                    "QueuePosition"   INTEGER NOT NULL,
                    "SkillId"         INTEGER NOT NULL DEFAULT 0,
                    "FinishedLevel"   INTEGER NOT NULL DEFAULT 0,
                    "TrainingStartSp" INTEGER NOT NULL DEFAULT 0,
                    "LevelStartSp"    INTEGER NOT NULL DEFAULT 0,
                    "LevelEndSp"      INTEGER NOT NULL DEFAULT 0,
                    "StartDate"       TEXT,
                    "FinishDate"      TEXT,
                    PRIMARY KEY ("CharacterId", "QueuePosition")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiJumpClones" (
                    "JumpCloneId"  INTEGER NOT NULL CONSTRAINT "PK_EsiJumpClones" PRIMARY KEY,
                    "CharacterId"  INTEGER NOT NULL,
                    "LocationId"   INTEGER NOT NULL DEFAULT 0,
                    "LocationType" TEXT    NOT NULL DEFAULT '',
                    "Name"         TEXT
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiJumpCloneImplants" (
                    "JumpCloneId" INTEGER NOT NULL,
                    "TypeId"      INTEGER NOT NULL,
                    PRIMARY KEY ("JumpCloneId", "TypeId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiImplants" (
                    "CharacterId" INTEGER NOT NULL,
                    "TypeId"      INTEGER NOT NULL,
                    PRIMARY KEY ("CharacterId", "TypeId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiWalletJournal" (
                    "EsiId"         INTEGER NOT NULL,
                    "OwnerId"       INTEGER NOT NULL,
                    "OwnerType"     TEXT    NOT NULL,
                    "Division"      INTEGER,
                    "Date"          TEXT    NOT NULL,
                    "RefType"       TEXT    NOT NULL DEFAULT '',
                    "FirstPartyId"  INTEGER,
                    "SecondPartyId" INTEGER,
                    "Amount"        TEXT    NOT NULL DEFAULT '0',
                    "Balance"       TEXT    NOT NULL DEFAULT '0',
                    "Description"   TEXT,
                    "Reason"        TEXT,
                    "Tax"           TEXT,
                    "TaxReceiverId" INTEGER,
                    "ContextId"     INTEGER,
                    "ContextIdType" TEXT,
                    PRIMARY KEY ("OwnerId", "OwnerType", "EsiId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiWalletTransactions" (
                    "TransactionId" INTEGER NOT NULL,
                    "OwnerId"       INTEGER NOT NULL,
                    "OwnerType"     TEXT    NOT NULL,
                    "Division"      INTEGER,
                    "Date"          TEXT    NOT NULL,
                    "ClientId"      INTEGER NOT NULL DEFAULT 0,
                    "LocationId"    INTEGER NOT NULL DEFAULT 0,
                    "Quantity"      INTEGER NOT NULL DEFAULT 0,
                    "TypeId"        INTEGER NOT NULL DEFAULT 0,
                    "UnitPrice"     TEXT    NOT NULL DEFAULT '0',
                    "IsBuy"         INTEGER NOT NULL DEFAULT 0,
                    "IsPersonal"    INTEGER NOT NULL DEFAULT 0,
                    "JournalRefId"  INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("OwnerId", "OwnerType", "TransactionId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiIndustryJobs" (
                    "JobId"                INTEGER NOT NULL,
                    "OwnerId"              INTEGER NOT NULL,
                    "OwnerType"            TEXT    NOT NULL,
                    "InstallerId"          INTEGER NOT NULL DEFAULT 0,
                    "FacilityId"           INTEGER NOT NULL DEFAULT 0,
                    "StationId"            INTEGER NOT NULL DEFAULT 0,
                    "ActivityId"           INTEGER NOT NULL DEFAULT 0,
                    "BlueprintId"          INTEGER NOT NULL DEFAULT 0,
                    "BlueprintTypeId"      INTEGER NOT NULL DEFAULT 0,
                    "BlueprintLocationId"  INTEGER NOT NULL DEFAULT 0,
                    "OutputLocationId"     INTEGER NOT NULL DEFAULT 0,
                    "Runs"                 INTEGER NOT NULL DEFAULT 0,
                    "Cost"                 TEXT    NOT NULL DEFAULT '0',
                    "LicensedRuns"         INTEGER,
                    "Probability"          REAL,
                    "ProductTypeId"        INTEGER,
                    "Status"               TEXT    NOT NULL DEFAULT '',
                    "Duration"             INTEGER NOT NULL DEFAULT 0,
                    "StartDate"            TEXT    NOT NULL,
                    "EndDate"              TEXT    NOT NULL,
                    "PauseDate"            TEXT,
                    "CompletedDate"        TEXT,
                    "CompletedCharacterId" INTEGER,
                    "SuccessfulRuns"       INTEGER,
                    PRIMARY KEY ("OwnerId", "OwnerType", "JobId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiMarketOrders" (
                    "OrderId"       INTEGER NOT NULL,
                    "OwnerId"       INTEGER NOT NULL,
                    "OwnerType"     TEXT    NOT NULL,
                    "IsHistory"     INTEGER NOT NULL DEFAULT 0,
                    "TypeId"        INTEGER NOT NULL DEFAULT 0,
                    "LocationId"    INTEGER NOT NULL DEFAULT 0,
                    "VolumeTotal"   INTEGER NOT NULL DEFAULT 0,
                    "VolumeRemain"  INTEGER NOT NULL DEFAULT 0,
                    "MinVolume"     INTEGER NOT NULL DEFAULT 0,
                    "Price"         TEXT    NOT NULL DEFAULT '0',
                    "IsBuyOrder"    INTEGER NOT NULL DEFAULT 0,
                    "Duration"      INTEGER NOT NULL DEFAULT 0,
                    "Issued"        TEXT    NOT NULL,
                    "Range"         TEXT    NOT NULL DEFAULT '',
                    "Escrow"        TEXT,
                    "IsCorporation" INTEGER,
                    "RegionId"      INTEGER,
                    "State"         TEXT,
                    PRIMARY KEY ("OwnerId", "OwnerType", "OrderId", "IsHistory")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiContracts" (
                    "ContractId"          INTEGER NOT NULL,
                    "OwnerId"             INTEGER NOT NULL,
                    "OwnerType"           TEXT    NOT NULL,
                    "IssuerId"            INTEGER NOT NULL DEFAULT 0,
                    "IssuerCorporationId" INTEGER NOT NULL DEFAULT 0,
                    "AssigneeId"          INTEGER,
                    "AcceptorId"          INTEGER,
                    "StartLocationId"     INTEGER,
                    "EndLocationId"       INTEGER,
                    "Type"                TEXT    NOT NULL DEFAULT '',
                    "Status"              TEXT    NOT NULL DEFAULT '',
                    "Title"               TEXT,
                    "ForCorporation"      INTEGER NOT NULL DEFAULT 0,
                    "Availability"        TEXT    NOT NULL DEFAULT '',
                    "DateIssued"          TEXT    NOT NULL,
                    "DateExpired"         TEXT,
                    "DateAccepted"        TEXT,
                    "DateCompleted"       TEXT,
                    "DaysToComplete"      INTEGER NOT NULL DEFAULT 0,
                    "Price"               TEXT    NOT NULL DEFAULT '0',
                    "Reward"              TEXT    NOT NULL DEFAULT '0',
                    "Collateral"          TEXT    NOT NULL DEFAULT '0',
                    "Buyout"              TEXT    NOT NULL DEFAULT '0',
                    "Volume"              TEXT    NOT NULL DEFAULT '0',
                    "RegionId"            INTEGER NOT NULL DEFAULT 0,
                    "ItemsPulled"         INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("OwnerId", "OwnerType", "ContractId")
                )
                """);
            // Columns added for the contracts feature — backfill on existing DBs.
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "EsiContracts" ADD COLUMN "RegionId" INTEGER NOT NULL DEFAULT 0"""); } catch { }
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "EsiContracts" ADD COLUMN "ItemsPulled" INTEGER NOT NULL DEFAULT 0"""); } catch { }

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiContractItems" (
                    "ContractId"         INTEGER NOT NULL,
                    "RecordId"           INTEGER NOT NULL,
                    "TypeId"             INTEGER NOT NULL DEFAULT 0,
                    "Quantity"           INTEGER NOT NULL DEFAULT 0,
                    "IsIncluded"         INTEGER NOT NULL DEFAULT 0,
                    "IsSingleton"        INTEGER NOT NULL DEFAULT 0,
                    "RawQuantity"        INTEGER,
                    "IsBlueprintCopy"    INTEGER,
                    "MaterialEfficiency" INTEGER,
                    "TimeEfficiency"     INTEGER,
                    "Runs"               INTEGER,
                    PRIMARY KEY ("ContractId", "RecordId")
                )
                """);

            // Persistent id→name cache, shared with the Industry Browser (which also creates it
            // on demand). Names are immutable so rows are kept across sessions.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "UniverseNames" (
                    "EntityId" INTEGER NOT NULL,
                    "Name"     TEXT    NOT NULL DEFAULT '',
                    "Category" TEXT    NOT NULL DEFAULT '',
                    "PulledAt" TEXT,
                    PRIMARY KEY ("EntityId")
                )
                """);
            // Added after the table shipped — existing installs need the column grafted on.
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "UniverseNames" ADD COLUMN "PulledAt" TEXT"""); }
            catch { /* already present */ }

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "WalletBackfillState" (
                    "OwnerId"   INTEGER NOT NULL,
                    "OwnerType" TEXT    NOT NULL,
                    "Kind"      TEXT    NOT NULL,
                    "Division"  INTEGER NOT NULL,
                    "Complete"  INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("OwnerId", "OwnerType", "Kind", "Division")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "ContractPrices" (
                    "TypeId"      INTEGER NOT NULL,
                    "BestPrice"   TEXT,
                    "Avg30Best"   TEXT,
                    "ActiveCount" INTEGER NOT NULL DEFAULT 0,
                    "SampleDays"  INTEGER NOT NULL DEFAULT 0,
                    "UpdatedAt"   TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("TypeId")
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "ContractBpcPrices" (
                    "TypeId"      INTEGER NOT NULL,
                    "Me"          INTEGER NOT NULL,
                    "BestPerRun"  TEXT,
                    "Avg30PerRun" TEXT,
                    "ActiveCount" INTEGER NOT NULL DEFAULT 0,
                    "SampleDays"  INTEGER NOT NULL DEFAULT 0,
                    "UpdatedAt"   TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("TypeId","Me")
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "PriceOverrides" (
                    "TypeId"        INTEGER NOT NULL,
                    "TypeName"      TEXT    NOT NULL DEFAULT '',
                    "BuildCost"     TEXT,
                    "MarketValue"   TEXT,
                    "ContractValue" TEXT,
                    "UpdatedAt"     TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("TypeId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiAssets" (
                    "OwnerId"         INTEGER NOT NULL,
                    "OwnerType"       TEXT    NOT NULL,
                    "ItemId"          INTEGER NOT NULL,
                    "TypeId"          INTEGER NOT NULL DEFAULT 0,
                    "LocationId"      INTEGER NOT NULL DEFAULT 0,
                    "LocationType"    TEXT    NOT NULL DEFAULT '',
                    "LocationFlag"    TEXT    NOT NULL DEFAULT '',
                    "Quantity"        INTEGER NOT NULL DEFAULT 0,
                    "IsSingleton"     INTEGER NOT NULL DEFAULT 0,
                    "IsBlueprintCopy" INTEGER,
                    "RootLocationId"   INTEGER NOT NULL DEFAULT 0,
                    "RootLocationType" TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("OwnerId", "OwnerType", "ItemId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiBlueprints" (
                    "OwnerId"            INTEGER NOT NULL,
                    "OwnerType"          TEXT    NOT NULL,
                    "ItemId"             INTEGER NOT NULL,
                    "TypeId"             INTEGER NOT NULL DEFAULT 0,
                    "LocationId"         INTEGER NOT NULL DEFAULT 0,
                    "LocationFlag"       TEXT    NOT NULL DEFAULT '',
                    "Quantity"           INTEGER NOT NULL DEFAULT 0,
                    "TimeEfficiency"     INTEGER NOT NULL DEFAULT 0,
                    "MaterialEfficiency" INTEGER NOT NULL DEFAULT 0,
                    "Runs"               INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("OwnerId", "OwnerType", "ItemId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiMining" (
                    "CharacterId"   INTEGER NOT NULL,
                    "Date"          TEXT    NOT NULL,
                    "SolarSystemId" INTEGER NOT NULL,
                    "TypeId"        INTEGER NOT NULL,
                    "Quantity"      INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("CharacterId", "Date", "SolarSystemId", "TypeId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiNotifications" (
                    "CharacterId"    INTEGER NOT NULL,
                    "NotificationId" INTEGER NOT NULL,
                    "Type"           TEXT    NOT NULL DEFAULT '',
                    "SenderId"       INTEGER NOT NULL DEFAULT 0,
                    "SenderType"     TEXT    NOT NULL DEFAULT '',
                    "Timestamp"      TEXT    NOT NULL,
                    "IsRead"         INTEGER NOT NULL DEFAULT 0,
                    "Text"           TEXT,
                    PRIMARY KEY ("CharacterId", "NotificationId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiContacts" (
                    "OwnerId"     INTEGER NOT NULL,
                    "OwnerType"   TEXT    NOT NULL,
                    "ContactId"   INTEGER NOT NULL,
                    "ContactType" TEXT    NOT NULL DEFAULT '',
                    "Standing"    REAL    NOT NULL DEFAULT 0,
                    "IsWatched"   INTEGER NOT NULL DEFAULT 0,
                    "IsBlocked"   INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("OwnerId", "OwnerType", "ContactId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiKillMailRefs" (
                    "OwnerId"      INTEGER NOT NULL,
                    "OwnerType"    TEXT    NOT NULL,
                    "KillMailId"   INTEGER NOT NULL,
                    "KillMailHash" TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("OwnerId", "OwnerType", "KillMailId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiPlanetaryColonies" (
                    "CharacterId"   INTEGER NOT NULL,
                    "PlanetId"      INTEGER NOT NULL,
                    "PlanetType"    TEXT    NOT NULL DEFAULT '',
                    "SolarSystemId" INTEGER NOT NULL DEFAULT 0,
                    "LastUpdate"    TEXT    NOT NULL,
                    "NumPins"       INTEGER NOT NULL DEFAULT 0,
                    "UpgradeLevel"  INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("CharacterId", "PlanetId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiAgentResearch" (
                    "CharacterId"     INTEGER NOT NULL,
                    "AgentId"         INTEGER NOT NULL,
                    "SkillTypeId"     INTEGER NOT NULL DEFAULT 0,
                    "StartedAt"       TEXT    NOT NULL,
                    "PointsPerDay"    REAL    NOT NULL DEFAULT 0,
                    "RemainderPoints" REAL    NOT NULL DEFAULT 0,
                    PRIMARY KEY ("CharacterId", "AgentId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiLoyaltyPoints" (
                    "CharacterId"   INTEGER NOT NULL,
                    "CorporationId" INTEGER NOT NULL,
                    "Points"        INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("CharacterId", "CorporationId")
                )
                """);

            // ── LP store ────────────────────────────────────────────────────────
            // Offers are public and exist nowhere in the SDE, so ESI is the only source.
            //
            // Keyed on (CorporationId, OfferId). Offer ids are NOT unique across
            // corporations — id 3414 is the same offer in Perkone's, Lai Dai's and Federal
            // Navy Academy's stores — and the first cut of these tables keyed on OfferId
            // alone, which aborted the sweep on the second corporation with a UNIQUE
            // violation. SQLite cannot alter a primary key, so the tables are dropped and
            // rebuilt. They hold nothing but a re-fetchable cache, refreshed daily.
            // Guarded so it happens once, on a database still carrying the old key. Written
            // unguarded at first, it wiped the catalogue on every launch and forced a fresh
            // sweep each start — the tab vanished after every restart until the sweep caught
            // up again.
            int legacyLpSchema = 0;
            try
            {
                legacyLpSchema = db.Database.SqlQueryRaw<int>("""
                    SELECT COUNT(*) AS "Value" FROM sqlite_master
                    WHERE type = 'table'
                      AND name = 'EsiLpStoreOfferItems'
                      AND sql NOT LIKE '%CorporationId%'
                    """).AsEnumerable().First();
            }
            catch { /* table absent on a fresh database — nothing to migrate */ }

            if (legacyLpSchema > 0)
            {
                db.Database.ExecuteSqlRaw("""DROP TABLE IF EXISTS "EsiLpStoreOfferItems" """);
                db.Database.ExecuteSqlRaw("""DROP TABLE IF EXISTS "EsiLpStoreOffers" """);
                try { db.Database.ExecuteSqlRaw("""DELETE FROM "EsiLpStoreCorps" """); } catch { }
            }
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiLpStoreOffers" (
                    "CorporationId" INTEGER NOT NULL,
                    "OfferId"       INTEGER NOT NULL,
                    "TypeId"        INTEGER NOT NULL DEFAULT 0,
                    "Quantity"      INTEGER NOT NULL DEFAULT 0,
                    "LpCost"        INTEGER NOT NULL DEFAULT 0,
                    "IskCost"       INTEGER NOT NULL DEFAULT 0,
                    "AkCost"        INTEGER NOT NULL DEFAULT 0,
                    "UpdatedAt"     TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("CorporationId", "OfferId")
                )
                """);
            // The Item Browser looks these up by type, not by corporation.
            db.Database.ExecuteSqlRaw(
                """CREATE INDEX IF NOT EXISTS "IX_EsiLpStoreOffers_Type" ON "EsiLpStoreOffers" ("TypeId")""");
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiLpStoreOfferItems" (
                    "CorporationId" INTEGER NOT NULL,
                    "OfferId"       INTEGER NOT NULL,
                    "TypeId"        INTEGER NOT NULL,
                    "Quantity"      INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("CorporationId", "OfferId", "TypeId")
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "LpCorpValues" (
                    "CorporationId" INTEGER NOT NULL PRIMARY KEY,
                    "IskPerLp"      REAL    NOT NULL DEFAULT 0,
                    "MedianIskPerLp" REAL   NOT NULL DEFAULT 0,
                    "ValuedOffers"  INTEGER NOT NULL DEFAULT 0,
                    "TotalOffers"   INTEGER NOT NULL DEFAULT 0,
                    "BestIskPerLp"  REAL    NOT NULL DEFAULT 0,
                    "BestTypeId"    INTEGER NOT NULL DEFAULT 0,
                    "ComputedAt"    TEXT    NOT NULL DEFAULT ''
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "LpCorpValueSnapshots" (
                    "CorporationId" INTEGER NOT NULL,
                    "Date"          TEXT    NOT NULL,
                    "IskPerLp"      REAL    NOT NULL DEFAULT 0,
                    "MedianIskPerLp" REAL   NOT NULL DEFAULT 0,
                    "ValuedOffers"  INTEGER NOT NULL DEFAULT 0,
                    "ComputedAt"    TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("CorporationId", "Date")
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiLpStoreCorps" (
                    "CorporationId" INTEGER NOT NULL PRIMARY KEY,
                    "HasStore"      INTEGER NOT NULL DEFAULT 0,
                    "OfferCount"    INTEGER NOT NULL DEFAULT 0,
                    "LastCheckedAt" TEXT    NULL
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiMedals" (
                    "Id"            INTEGER NOT NULL CONSTRAINT "PK_EsiMedals" PRIMARY KEY AUTOINCREMENT,
                    "CharacterId"   INTEGER NOT NULL,
                    "MedalId"       INTEGER NOT NULL DEFAULT 0,
                    "CorporationId" INTEGER NOT NULL DEFAULT 0,
                    "IssuerId"      INTEGER NOT NULL DEFAULT 0,
                    "Date"          TEXT    NOT NULL,
                    "Reason"        TEXT    NOT NULL DEFAULT '',
                    "Status"        TEXT    NOT NULL DEFAULT ''
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiStandings" (
                    "OwnerId"   INTEGER NOT NULL,
                    "OwnerType" TEXT    NOT NULL,
                    "FromId"    INTEGER NOT NULL,
                    "FromType"  TEXT    NOT NULL DEFAULT '',
                    "Standing"  REAL    NOT NULL DEFAULT 0,
                    PRIMARY KEY ("OwnerId", "OwnerType", "FromId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiTitles" (
                    "CharacterId" INTEGER NOT NULL,
                    "TitleId"     INTEGER NOT NULL,
                    "Name"        TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("CharacterId", "TitleId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiRoles" (
                    "CharacterId" INTEGER NOT NULL,
                    "Role"        TEXT    NOT NULL,
                    "RoleType"    TEXT    NOT NULL,
                    PRIMARY KEY ("CharacterId", "Role", "RoleType")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiFittings" (
                    "CharacterId" INTEGER NOT NULL,
                    "FittingId"   INTEGER NOT NULL,
                    "Name"        TEXT    NOT NULL DEFAULT '',
                    "Description" TEXT    NOT NULL DEFAULT '',
                    "ShipTypeId"  INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("CharacterId", "FittingId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiFittingItems" (
                    "Id"        INTEGER NOT NULL CONSTRAINT "PK_EsiFittingItems" PRIMARY KEY AUTOINCREMENT,
                    "FittingId" INTEGER NOT NULL,
                    "TypeId"    INTEGER NOT NULL DEFAULT 0,
                    "Flag"      TEXT    NOT NULL DEFAULT '',
                    "Quantity"  INTEGER NOT NULL DEFAULT 0
                )
                """);

            p.Report((45, "Building corporation tables…"));
            // ── Corp tables ───────────────────────────────────────────────────────

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpDivisions" (
                    "CorporationId" INTEGER NOT NULL,
                    "Division"      INTEGER NOT NULL,
                    "DivisionType"  TEXT    NOT NULL,
                    "Name"          TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("CorporationId", "Division", "DivisionType")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpMembers" (
                    "CorporationId" INTEGER NOT NULL,
                    "CharacterId"   INTEGER NOT NULL,
                    PRIMARY KEY ("CorporationId", "CharacterId")
                )
                """);

            // Current member-tracking values, overwritten on each poll.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpMemberTracking" (
                    "CorporationId" INTEGER NOT NULL,
                    "CharacterId"   INTEGER NOT NULL,
                    "StartDate"     TEXT,
                    "LogonDate"     TEXT,
                    "LogoffDate"    TEXT,
                    "LocationId"    INTEGER,
                    "ShipTypeId"    INTEGER,
                    "BaseId"        INTEGER,
                    "UpdatedAt"     TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY ("CorporationId", "CharacterId")
                )
                """);

            // Accumulated login history — one row per distinct logon we observe. The unique
            // index is what makes repeated polls idempotent: the same logon seen again is
            // rejected rather than duplicated.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpMemberSessions" (
                    "Id"            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "CorporationId" INTEGER NOT NULL,
                    "CharacterId"   INTEGER NOT NULL,
                    "LogonDate"     TEXT    NOT NULL,
                    "LogoffDate"    TEXT,
                    "LocationId"    INTEGER,
                    "ShipTypeId"    INTEGER,
                    "RecordedAt"    TEXT    NOT NULL DEFAULT ''
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_EsiCorpMemberSessions_Key"
                ON "EsiCorpMemberSessions" ("CorporationId", "CharacterId", "LogonDate")
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpMemberRoles" (
                    "CorporationId" INTEGER NOT NULL,
                    "CharacterId"   INTEGER NOT NULL,
                    "Role"          TEXT    NOT NULL,
                    "RoleType"      TEXT    NOT NULL,
                    PRIMARY KEY ("CorporationId", "CharacterId", "Role", "RoleType")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpTitles" (
                    "CorporationId" INTEGER NOT NULL,
                    "TitleId"       INTEGER NOT NULL,
                    "Name"          TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("CorporationId", "TitleId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpMedals" (
                    "CorporationId" INTEGER NOT NULL,
                    "MedalId"       INTEGER NOT NULL,
                    "Title"         TEXT    NOT NULL DEFAULT '',
                    "Description"   TEXT    NOT NULL DEFAULT '',
                    "CreatorId"     INTEGER NOT NULL DEFAULT 0,
                    "CreatedAt"     TEXT    NOT NULL,
                    PRIMARY KEY ("CorporationId", "MedalId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpStructures" (
                    "CorporationId"      INTEGER NOT NULL,
                    "StructureId"        INTEGER NOT NULL,
                    "Name"               TEXT    NOT NULL DEFAULT '',
                    "TypeId"             INTEGER NOT NULL DEFAULT 0,
                    "SystemId"           INTEGER NOT NULL DEFAULT 0,
                    "ProfileId"          INTEGER,
                    "State"              TEXT    NOT NULL DEFAULT '',
                    "StateTimerStart"    TEXT,
                    "StateTimerEnd"      TEXT,
                    "UnanchorsAt"        TEXT,
                    "FuelExpires"        TEXT,
                    "NextReinforceApply" TEXT,
                    "NextReinforceHour"  INTEGER,
                    "ReinforceHour"      INTEGER,
                    PRIMARY KEY ("CorporationId", "StructureId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiStructureNames" (
                    "StructureId"   INTEGER NOT NULL PRIMARY KEY,
                    "Name"          TEXT    NOT NULL DEFAULT '',
                    "SolarSystemId" INTEGER NOT NULL DEFAULT 0,
                    "OwnerId"       INTEGER NOT NULL DEFAULT 0,
                    "AllianceId"    INTEGER NOT NULL DEFAULT 0,
                    "TypeId"        INTEGER NOT NULL DEFAULT 0,
                    "X"             REAL    NOT NULL DEFAULT 0,
                    "Y"             REAL    NOT NULL DEFAULT 0,
                    "Z"             REAL    NOT NULL DEFAULT 0,
                    "NearestCelestialId" INTEGER NOT NULL DEFAULT 0,
                    "NearestCelestial"   TEXT    NOT NULL DEFAULT '',
                    "Status"        INTEGER NOT NULL DEFAULT 0,
                    "PulledAt"      TEXT    NOT NULL DEFAULT '2000-01-01T00:00:00+00:00'
                )
                """);
            foreach (var col in new[] {
                """ALTER TABLE "EsiStructureNames" ADD COLUMN "OwnerId" INTEGER NOT NULL DEFAULT 0""",
                """ALTER TABLE "EsiStructureNames" ADD COLUMN "AllianceId" INTEGER NOT NULL DEFAULT 0""",
                """ALTER TABLE "EsiStructureNames" ADD COLUMN "TypeId" INTEGER NOT NULL DEFAULT 0""",
                """ALTER TABLE "EsiStructureNames" ADD COLUMN "X" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "EsiStructureNames" ADD COLUMN "Y" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "EsiStructureNames" ADD COLUMN "Z" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "EsiStructureNames" ADD COLUMN "NearestCelestialId" INTEGER NOT NULL DEFAULT 0""",
                """ALTER TABLE "EsiStructureNames" ADD COLUMN "NearestCelestial" TEXT NOT NULL DEFAULT ''""",
                """ALTER TABLE "EsiStructureNames" ADD COLUMN "Status" INTEGER NOT NULL DEFAULT 0""",
            }) { try { db.Database.ExecuteSqlRaw(col); } catch { } }

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiStructureNameFailures" (
                    "StructureId" INTEGER NOT NULL PRIMARY KEY,
                    "FailedAt"    TEXT    NOT NULL DEFAULT '2000-01-01T00:00:00+00:00',
                    "StatusCode"  INTEGER NOT NULL DEFAULT 0
                )
                """);
            // Celestial positions for nearest-structure labelling. Normally created/populated by the
            // SDE import; created here (empty) so queries don't fail before the user re-imports.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "SdeCelestials" (
                    "ItemId"        INTEGER NOT NULL PRIMARY KEY,
                    "SolarSystemId" INTEGER NOT NULL DEFAULT 0,
                    "TypeId"        INTEGER NOT NULL DEFAULT 0,
                    "Kind"          INTEGER NOT NULL DEFAULT 0,
                    "X"             REAL    NOT NULL DEFAULT 0,
                    "Y"             REAL    NOT NULL DEFAULT 0,
                    "Z"             REAL    NOT NULL DEFAULT 0,
                    "Name"          TEXT    NOT NULL DEFAULT ''
                )
                """);
            db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_SdeCelestials_System" ON "SdeCelestials" ("SolarSystemId")""");

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpStarbases" (
                    "CorporationId"   INTEGER NOT NULL,
                    "StarbaseId"      INTEGER NOT NULL,
                    "TypeId"          INTEGER NOT NULL DEFAULT 0,
                    "SystemId"        INTEGER NOT NULL DEFAULT 0,
                    "MoonId"          INTEGER NOT NULL DEFAULT 0,
                    "State"           TEXT    NOT NULL DEFAULT '',
                    "UnanchorAt"      TEXT,
                    "ReinforcedUntil" TEXT,
                    "OnlinedSince"    TEXT,
                    PRIMARY KEY ("CorporationId", "StarbaseId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpFacilities" (
                    "CorporationId" INTEGER NOT NULL,
                    "FacilityId"    INTEGER NOT NULL,
                    "TypeId"        INTEGER NOT NULL DEFAULT 0,
                    "SystemId"      INTEGER NOT NULL DEFAULT 0,
                    "RegionId"      INTEGER,
                    "TaxRate"       REAL,
                    PRIMARY KEY ("CorporationId", "FacilityId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpMiningExtractions" (
                    "CorporationId"       INTEGER NOT NULL,
                    "MoonId"              INTEGER NOT NULL,
                    "StructureId"         INTEGER NOT NULL,
                    "ExtractionStartTime" TEXT    NOT NULL,
                    "ChunkArrivalTime"    TEXT    NOT NULL,
                    "NaturalDecayTime"    TEXT    NOT NULL,
                    PRIMARY KEY ("CorporationId", "MoonId", "StructureId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpMiningObservers" (
                    "CorporationId" INTEGER NOT NULL,
                    "ObserverId"    INTEGER NOT NULL,
                    "ObserverType"  TEXT    NOT NULL DEFAULT '',
                    "LastUpdated"   TEXT    NOT NULL,
                    PRIMARY KEY ("CorporationId", "ObserverId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpMiningLedger" (
                    "CorporationId"         INTEGER NOT NULL,
                    "ObserverId"            INTEGER NOT NULL,
                    "CharacterId"           INTEGER NOT NULL,
                    "TypeId"                INTEGER NOT NULL,
                    "Quantity"              INTEGER NOT NULL DEFAULT 0,
                    "RecordedCorporationId" INTEGER NOT NULL DEFAULT 0,
                    "LastUpdated"           TEXT    NOT NULL,
                    PRIMARY KEY ("CorporationId", "ObserverId", "CharacterId", "TypeId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpProjects" (
                    "CorporationId"   INTEGER NOT NULL,
                    "ProjectId"       TEXT    NOT NULL,
                    "Name"            TEXT    NOT NULL DEFAULT '',
                    "State"           TEXT    NOT NULL DEFAULT '',
                    "LastModified"    TEXT    NOT NULL DEFAULT '',
                    "ProgressCurrent" INTEGER NOT NULL DEFAULT 0,
                    "ProgressDesired" INTEGER NOT NULL DEFAULT 0,
                    "RewardInitial"   INTEGER NOT NULL DEFAULT 0,
                    "RewardRemaining" INTEGER NOT NULL DEFAULT 0,
                    "Description"     TEXT    NOT NULL DEFAULT '',
                    "Career"          TEXT    NOT NULL DEFAULT '',
                    "Created"         TEXT,
                    "RewardPerContrib" INTEGER NOT NULL DEFAULT 0,
                    "CreatorId"       INTEGER,
                    "CreatorName"     TEXT    NOT NULL DEFAULT '',
                    "UpdatedAt"       TEXT    NOT NULL DEFAULT '',
                    "IsStatic"        INTEGER NOT NULL DEFAULT 0,
                    "DetailUnavailable" INTEGER NOT NULL DEFAULT 0,
                    "ConfigType"      TEXT,
                    "ConfigurationJson" TEXT,
                    PRIMARY KEY ("CorporationId", "ProjectId")
                )
                """);
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "EsiCorpProjects" ADD COLUMN "DetailUnavailable" INTEGER NOT NULL DEFAULT 0"""); } catch { }
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "Corporations" ADD COLUMN "DeniedEndpoints" TEXT NOT NULL DEFAULT ''"""); } catch { }
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "CorpTop10Excludes" (
                    "EntityId"   INTEGER NOT NULL,
                    "EntityType" TEXT    NOT NULL,
                    "EntityName" TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY ("EntityId", "EntityType")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiCorpProjectContributors" (
                    "CorporationId" INTEGER NOT NULL,
                    "ProjectId"     TEXT    NOT NULL,
                    "CharacterId"   INTEGER NOT NULL,
                    "Name"          TEXT    NOT NULL DEFAULT '',
                    "Contributed"   INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("CorporationId", "ProjectId", "CharacterId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "CorpStandingProjects" (
                    "Id"              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "CorporationId"   INTEGER NOT NULL,
                    "ProjectType"     TEXT    NOT NULL DEFAULT 'destroy_npc',
                    "ItemTypeId"      INTEGER,
                    "ItemTypeName"    TEXT    NOT NULL DEFAULT '',
                    "StationId"       INTEGER,
                    "StationName"     TEXT    NOT NULL DEFAULT '',
                    "ScopeType"       TEXT    NOT NULL DEFAULT 'system',
                    "SolarSystemId"   INTEGER,
                    "SolarSystemName" TEXT    NOT NULL DEFAULT '',
                    "ScopeEntityId"   INTEGER,
                    "ScopeEntityName" TEXT    NOT NULL DEFAULT '',
                    "MinAdm"          REAL,
                    "CreatedAt"       TEXT    NOT NULL DEFAULT ''
                )
                """);

            p.Report((65, "Building market tables…"));
            // ── Market pricing ────────────────────────────────────────────────────

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "MarketPricingConfigs" (
                    "Id"            INTEGER NOT NULL CONSTRAINT "PK_MarketPricingConfigs" PRIMARY KEY AUTOINCREMENT,
                    "Method"        TEXT    NOT NULL DEFAULT 'Fuzzwork',
                    "LocationName"  TEXT    NOT NULL DEFAULT '',
                    "LocationId"    INTEGER NOT NULL DEFAULT 0,
                    "PriceType"     TEXT    NOT NULL DEFAULT 'Midpoint',
                    "AuthCharId"    INTEGER,
                    "IsEnabled"     INTEGER NOT NULL DEFAULT 1,
                    "SortOrder"     INTEGER NOT NULL DEFAULT 0,
                    "LastRefreshed" TEXT,
                    "LastStatus"    TEXT    NOT NULL DEFAULT '',
                    "StationFilter"       INTEGER,
                    "UsePercentileFilter" INTEGER NOT NULL DEFAULT 1,
                    "PercentilePercent"   REAL    NOT NULL DEFAULT 5.0
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "MarketItemPrices" (
                    "ConfigId"   INTEGER NOT NULL,
                    "TypeId"     INTEGER NOT NULL,
                    "BuyPrice"   REAL    NOT NULL DEFAULT 0,
                    "SellPrice"  REAL    NOT NULL DEFAULT 0,
                    "Midpoint"   REAL    NOT NULL DEFAULT 0,
                    "FetchedAt"  TEXT    NOT NULL,
                    "FromMarketData" INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("ConfigId", "TypeId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "MarketRawOrders" (
                    "ConfigId"     INTEGER NOT NULL,
                    "OrderId"      INTEGER NOT NULL,
                    "TypeId"       INTEGER NOT NULL,
                    "IsBuyOrder"   INTEGER NOT NULL DEFAULT 0,
                    "Price"        REAL    NOT NULL DEFAULT 0,
                    "VolumeRemain" INTEGER NOT NULL DEFAULT 0,
                    "VolumeTotal"  INTEGER NOT NULL DEFAULT 0,
                    "MinVolume"    INTEGER NOT NULL DEFAULT 1,
                    "LocationId"   INTEGER NOT NULL DEFAULT 0,
                    "SystemId"     INTEGER NOT NULL DEFAULT 0,
                    "Range"        TEXT    NOT NULL DEFAULT '',
                    "Issued"       TEXT    NOT NULL DEFAULT '2000-01-01T00:00:00+00:00',
                    "Duration"     INTEGER NOT NULL DEFAULT 0,
                    "FetchedAt"    TEXT    NOT NULL,
                    PRIMARY KEY ("ConfigId", "OrderId")
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE INDEX IF NOT EXISTS "IX_MarketRawOrders_TypeId"
                ON "MarketRawOrders" ("ConfigId", "TypeId", "IsBuyOrder")
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "MarketDefaultSettings" (
                    "Id"                    INTEGER NOT NULL PRIMARY KEY,
                    "AssetValueConfigId"    INTEGER,
                    "AssetValuePriceType"   TEXT    NOT NULL DEFAULT 'Midpoint',
                    "ManufacturingConfigId" INTEGER,
                    "ManufacturingPriceType" TEXT   NOT NULL DEFAULT 'Sell',
                    "MissingPriceMarkupPct"      REAL    NOT NULL DEFAULT 15.0,
                    "FilterLowballBuyOrders"     INTEGER NOT NULL DEFAULT 1,
                    "LowballBuyOrderThresholdPct" REAL   NOT NULL DEFAULT 25.0,
                    "PurchaseWhenCheaper"        INTEGER NOT NULL DEFAULT 0,
                    "PurchaseThresholdPct"       REAL    NOT NULL DEFAULT 100.0
                )
                """);
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "MarketDefaultSettings" ADD COLUMN "PurchaseWhenCheaper" INTEGER NOT NULL DEFAULT 0"""); } catch { }
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "MarketDefaultSettings" ADD COLUMN "PurchaseThresholdPct" REAL NOT NULL DEFAULT 100.0"""); } catch { }

            // Seed default region price sources on first run: The Forge and Domain,
            // all stations, high/low order filtering at 1%. Both rows evaluate their
            // NOT EXISTS guard against the pre-insert table state, so they seed together
            // only on a fresh install and never on an existing one.
            db.Database.ExecuteSqlRaw("""
                INSERT INTO "MarketPricingConfigs"
                    ("Method", "LocationName", "LocationId", "PriceType", "IsEnabled", "SortOrder", "LastStatus", "StationFilter", "UsePercentileFilter", "PercentilePercent")
                SELECT 'Region', 'The Forge', 10000002, 'Midpoint', 1, 0, '', NULL, 1, 1.0
                WHERE NOT EXISTS (SELECT 1 FROM "MarketPricingConfigs")
                UNION ALL
                SELECT 'Region', 'Domain',    10000043, 'Midpoint', 1, 1, '', NULL, 1, 1.0
                WHERE NOT EXISTS (SELECT 1 FROM "MarketPricingConfigs")
                """);

            p.Report((78, "Building industry tables…"));
            // ── Indy Parks ───────────────────────────────────────────────────────
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "IndyParks" (
                    "Id"        INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "Name"      TEXT    NOT NULL DEFAULT 'New Park',
                    "IsDefault" INTEGER NOT NULL DEFAULT 0,
                    "DefaultStructureId" INTEGER NULL
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "IndyStructures" (
                    "Id"               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "ParkId"           INTEGER NOT NULL,
                    "DisplayName"      TEXT    NOT NULL DEFAULT '',
                    "StructureTypeKey" TEXT    NOT NULL DEFAULT 'raitaru',
                    "SystemName"       TEXT    NOT NULL DEFAULT '',
                    "SecurityClass"    TEXT    NOT NULL DEFAULT 'nullsec',
                    "FacilityTax"      REAL    NOT NULL DEFAULT 1.0,
                    "RealStructureId"   INTEGER,
                    "RealStructureName" TEXT NOT NULL DEFAULT ''
                )
                """);
            // Existing parks predate the link to a real in-game facility.
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "IndyStructures" ADD COLUMN "RealStructureId" INTEGER"""); } catch { }
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "IndyStructures" ADD COLUMN "RealStructureName" TEXT NOT NULL DEFAULT ''"""); } catch { }
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "IndyStructureRigs" (
                    "Id"          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "StructureId" INTEGER NOT NULL,
                    "SlotIndex"   INTEGER NOT NULL,
                    "RigTypeId"   INTEGER NOT NULL DEFAULT 0
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "IndyCategoryAssignments" (
                    "Id"          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "ParkId"      INTEGER NOT NULL,
                    "CategoryKey" TEXT    NOT NULL DEFAULT '',
                    "StructureId" INTEGER
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "IndyItemExceptions" (
                    "Id"          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "ParkId"      INTEGER NOT NULL,
                    "TypeId"      INTEGER NOT NULL DEFAULT 0,
                    "TypeName"    TEXT    NOT NULL DEFAULT '',
                    "StructureId" INTEGER
                )
                """);


            // ── Build cost tables ─────────────────────────────────────────────────
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiAdjustedPrices" (
                    "TypeId"        INTEGER NOT NULL CONSTRAINT "PK_EsiAdjustedPrices" PRIMARY KEY,
                    "AdjustedPrice" REAL    NOT NULL DEFAULT 0,
                    "AveragePrice"  REAL    NOT NULL DEFAULT 0
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "IndustryCostIndices" (
                    "SolarSystemId" INTEGER NOT NULL,
                    "Activity"      TEXT    NOT NULL,
                    "CostIndex"     REAL    NOT NULL DEFAULT 0,
                    CONSTRAINT "PK_IndustryCostIndices" PRIMARY KEY ("SolarSystemId", "Activity")
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "BuildCosts" (
                    "TypeId"       INTEGER NOT NULL CONSTRAINT "PK_BuildCosts" PRIMARY KEY,
                    "TypeName"     TEXT    NOT NULL DEFAULT '',
                    "TotalCost"    REAL    NOT NULL DEFAULT 0,
                    "MaterialCost" REAL    NOT NULL DEFAULT 0,
                    "JobCost"      REAL    NOT NULL DEFAULT 0,
                    "BuildSeconds" REAL    NOT NULL DEFAULT 0,
                    "Bought"       INTEGER NOT NULL DEFAULT 0,
                    "UpdatedAt"    TEXT    NOT NULL DEFAULT ''
                )
                """);
            // BuildSeconds added after the schema squash — backfill it on existing DBs.
            // ALTER throws if the column already exists, so swallow that one case.
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "BuildCosts" ADD COLUMN "BuildSeconds" REAL NOT NULL DEFAULT 0"""); }
            catch { /* column already present */ }
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "BuildCosts" ADD COLUMN "Bought" INTEGER NOT NULL DEFAULT 0"""); }
            catch { /* column already present */ }

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "ReprocessingValues" (
                    "TypeId" INTEGER NOT NULL CONSTRAINT "PK_ReprocessingValues" PRIMARY KEY,
                    "Value"  REAL    NOT NULL DEFAULT 0
                )
                """);

            // Seed default pricing on first run: value assets and manufacturing cost from
            // The Forge Sell prices, 15% markup for items with no sell orders, and treat
            // buy orders below 10% of build cost as lowball. Runs only when the singleton
            // row is absent (fresh install). Resolves the Forge config id by region so it
            // does not depend on autoincrement ordering.
            db.Database.ExecuteSqlRaw("""
                INSERT INTO "MarketDefaultSettings"
                    ("Id", "AssetValueConfigId", "AssetValuePriceType", "ManufacturingConfigId", "ManufacturingPriceType",
                     "MissingPriceMarkupPct", "FilterLowballBuyOrders", "LowballBuyOrderThresholdPct")
                SELECT 1,
                       (SELECT "Id" FROM "MarketPricingConfigs" WHERE "LocationId" = 10000002 LIMIT 1), 'Sell',
                       (SELECT "Id" FROM "MarketPricingConfigs" WHERE "LocationId" = 10000002 LIMIT 1), 'Sell',
                       15.0, 1, 10.0
                WHERE NOT EXISTS (SELECT 1 FROM "MarketDefaultSettings")
                """);

            p.Report((90, "Finalizing schema…"));
            // ── Application error log ─────────────────────────────────────────────

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "AppErrorLog" (
                    "Id"           INTEGER NOT NULL CONSTRAINT "PK_AppErrorLog" PRIMARY KEY AUTOINCREMENT,
                    "OccurredAt"   TEXT    NOT NULL,
                    "Source"       TEXT    NOT NULL DEFAULT '',
                    "Context"      TEXT    NOT NULL DEFAULT '',
                    "Message"      TEXT    NOT NULL DEFAULT '',
                    "InnerMessage" TEXT
                )
                """);

            // ── Standing buy orders ──────────────────────────────────────────
            // User-declared intent; the live counterpart lives in EsiMarketOrders.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "StandingBuyOrders" (
                    "Id"           INTEGER NOT NULL CONSTRAINT "PK_StandingBuyOrders" PRIMARY KEY AUTOINCREMENT,
                    "TypeId"       INTEGER NOT NULL DEFAULT 0,
                    "TypeName"     TEXT    NOT NULL DEFAULT '',
                    "LocationId"   INTEGER NOT NULL DEFAULT 0,
                    "LocationName" TEXT    NOT NULL DEFAULT '',
                    "CreatedAt"    TEXT    NOT NULL DEFAULT ''
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_StandingBuyOrders_TypeId_LocationId"
                ON "StandingBuyOrders" ("TypeId", "LocationId")
                """);

            // ── Worklist ─────────────────────────────────────────────────────
            // Only configuration and per-item state are stored. The items themselves are
            // recomputed from live data every refresh, so there is nothing here to keep in
            // step with the game.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "WorklistMarketAlts" (
                    "Id"            INTEGER NOT NULL CONSTRAINT "PK_WorklistMarketAlts" PRIMARY KEY AUTOINCREMENT,
                    "LocationId"    INTEGER NOT NULL DEFAULT 0,
                    "LocationName"  TEXT    NOT NULL DEFAULT '',
                    "CharacterId"   INTEGER NOT NULL DEFAULT 0,
                    "CharacterName" TEXT    NOT NULL DEFAULT '',
                    "Note"          TEXT    NOT NULL DEFAULT ''
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorklistMarketAlts_LocationId"
                ON "WorklistMarketAlts" ("LocationId")
                """);

            // Carry over rows from the table's former name. This never shipped, so the only
            // databases holding WorklistDesks are ones used to test the branch — but losing
            // someone's configuration to a rename is a poor trade for deleting four lines.
            try
            {
                db.Database.ExecuteSqlRaw("""
                    INSERT OR IGNORE INTO "WorklistMarketAlts"
                        ("LocationId", "LocationName", "CharacterId", "CharacterName", "Note")
                    SELECT "LocationId", "LocationName", "CharacterId", "CharacterName", "Note"
                    FROM "WorklistDesks"
                    """);
                db.Database.ExecuteSqlRaw("""DROP TABLE "WorklistDesks" """);
            }
            catch { /* no old table — the normal case on a fresh install */ }

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "WorklistInvRules" (
                    "Id"                INTEGER NOT NULL CONSTRAINT "PK_WorklistInvRules" PRIMARY KEY AUTOINCREMENT,
                    "GroupId"           INTEGER NOT NULL DEFAULT 0,
                    "ThresholdPercent"  REAL    NOT NULL DEFAULT 100,
                    "FillTargetPercent" REAL    NOT NULL DEFAULT 100,
                    "LocationId"        INTEGER NOT NULL DEFAULT 0,
                    "LocationName"      TEXT    NOT NULL DEFAULT '',
                    "Enabled"           INTEGER NOT NULL DEFAULT 1
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "WorklistIndyChars" (
                    "Id"                    INTEGER NOT NULL CONSTRAINT "PK_WorklistIndyChars" PRIMARY KEY AUTOINCREMENT,
                    "CharacterId"           INTEGER NOT NULL DEFAULT 0,
                    "CharacterName"         TEXT    NOT NULL DEFAULT '',
                    "Manufacturing"         INTEGER NOT NULL DEFAULT 1,
                    "Reactions"             INTEGER NOT NULL DEFAULT 1,
                    "Science"               INTEGER NOT NULL DEFAULT 0,
                    "IncludeCorpAssets"     INTEGER NOT NULL DEFAULT 1,
                    "IncludePersonalAssets" INTEGER NOT NULL DEFAULT 1,
                    "Note"                  TEXT    NOT NULL DEFAULT ''
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorklistIndyChars_CharacterId"
                ON "WorklistIndyChars" ("CharacterId")
                """);

            // Added after the rules table shipped on this branch, so it needs its own ALTER —
            // CREATE TABLE IF NOT EXISTS will not add a column to a table that already exists.
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "WorklistInvRules" ADD COLUMN "Action" TEXT NOT NULL DEFAULT 'Buy' """); } catch { }

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "WorklistCorpAlts" (
                    "Id"              INTEGER NOT NULL CONSTRAINT "PK_WorklistCorpAlts" PRIMARY KEY AUTOINCREMENT,
                    "CorporationId"   INTEGER NOT NULL DEFAULT 0,
                    "CorporationName" TEXT    NOT NULL DEFAULT '',
                    "CharacterId"     INTEGER NOT NULL DEFAULT 0,
                    "CharacterName"   TEXT    NOT NULL DEFAULT '',
                    "Note"            TEXT    NOT NULL DEFAULT ''
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorklistCorpAlts_CorporationId"
                ON "WorklistCorpAlts" ("CorporationId")
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "WorklistIndyScopeStations" (
                    "Id"           INTEGER NOT NULL CONSTRAINT "PK_WorklistIndyScopeStations" PRIMARY KEY AUTOINCREMENT,
                    "LocationId"   INTEGER NOT NULL DEFAULT 0,
                    "LocationName" TEXT    NOT NULL DEFAULT ''
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorklistIndyScopeStations_LocationId"
                ON "WorklistIndyScopeStations" ("LocationId")
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "WorklistOrderRules" (
                    "Id"           INTEGER NOT NULL CONSTRAINT "PK_WorklistOrderRules" PRIMARY KEY AUTOINCREMENT,
                    "ParkId"       INTEGER NOT NULL DEFAULT 0,
                    "LocationId"   INTEGER NOT NULL DEFAULT 0,
                    "LocationName" TEXT    NOT NULL DEFAULT '',
                    "Enabled"      INTEGER NOT NULL DEFAULT 1
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "WorklistItemStates" (
                    "Key"          TEXT NOT NULL CONSTRAINT "PK_WorklistItemStates" PRIMARY KEY,
                    "FirstSeenAt"  TEXT NOT NULL DEFAULT '',
                    "SnoozedUntil" TEXT NULL
                )
                """);

            // ── Client activity monitoring ───────────────────────────────────
            // Live session state per character, refreshed by the char.online /
            // char.location / char.ship polling endpoints.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "CharacterStatuses" (
                    "CharacterId"       INTEGER NOT NULL CONSTRAINT "PK_CharacterStatuses" PRIMARY KEY,
                    "Online"            INTEGER NOT NULL DEFAULT 0,
                    "LastLogin"         TEXT,
                    "LastLogout"        TEXT,
                    "LoginCount"        INTEGER,
                    "SolarSystemId"     INTEGER,
                    "StationId"         INTEGER,
                    "StructureId"       INTEGER,
                    "ShipTypeId"        INTEGER,
                    "ShipItemId"        INTEGER,
                    "ShipName"          TEXT,
                    "OnlineCheckedAt"   TEXT,
                    "LocationCheckedAt" TEXT,
                    "ShipCheckedAt"     TEXT
                )
                """);

            // Per-file parse position, so restarts resume rather than re-read or skip.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "GameLogFiles" (
                    "Path"           TEXT    NOT NULL CONSTRAINT "PK_GameLogFiles" PRIMARY KEY,
                    "CharacterId"    INTEGER,
                    "CharacterName"  TEXT,
                    "LastOffset"     INTEGER NOT NULL DEFAULT 0,
                    "LastLineNumber" INTEGER NOT NULL DEFAULT 0,
                    "LastFileLength" INTEGER NOT NULL DEFAULT 0,
                    "FirstSeenAt"    TEXT    NOT NULL DEFAULT '',
                    "LastParsedAt"   TEXT    NOT NULL DEFAULT ''
                )
                """);

            // Parsed log lines. OccurredAt is an ISO string, not a DateTimeOffset —
            // EF Core + SQLite cannot translate DateTimeOffset comparisons in a Where.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "GameLogEvents" (
                    "Id"             INTEGER NOT NULL CONSTRAINT "PK_GameLogEvents" PRIMARY KEY AUTOINCREMENT,
                    "OccurredAt"     TEXT    NOT NULL DEFAULT '',
                    "Kind"           TEXT    NOT NULL DEFAULT '',
                    "CharacterId"    INTEGER,
                    "CharacterName"  TEXT,
                    "SourceFile"     TEXT    NOT NULL DEFAULT '',
                    "LineNumber"     INTEGER NOT NULL DEFAULT 0,
                    "Amount"         INTEGER,
                    "SecondaryAmount" INTEGER,
                    "SourceName"     TEXT,
                    "SourceShip"     TEXT,
                    "SourceCorp"     TEXT,
                    "SourceAlliance" TEXT,
                    "TargetName"     TEXT,
                    "TargetShip"     TEXT,
                    "TargetCorp"     TEXT,
                    "TargetAlliance" TEXT,
                    "Weapon"         TEXT,
                    "Quality"        TEXT,
                    "FromSystem"     TEXT,
                    "ToSystem"       TEXT,
                    "LocationName"   TEXT,
                    "RawText"        TEXT
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_GameLogEvents_SourceFile_LineNumber"
                ON "GameLogEvents" ("SourceFile", "LineNumber")
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE INDEX IF NOT EXISTS "IX_GameLogEvents_OccurredAt"
                ON "GameLogEvents" ("OccurredAt")
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE INDEX IF NOT EXISTS "IX_GameLogEvents_CharacterId_Kind"
                ON "GameLogEvents" ("CharacterId", "Kind")
                """);

            // Chat log import. Off by default and gated on a per-channel allowlist —
            // these rows contain other people's words, including private conversations.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "ChatLogFiles" (
                    "Path"                TEXT    NOT NULL CONSTRAINT "PK_ChatLogFiles" PRIMARY KEY,
                    "ChannelName"         TEXT    NOT NULL DEFAULT '',
                    "ChannelId"           TEXT,
                    "ListenerCharacterId" INTEGER,
                    "ListenerName"        TEXT,
                    "LastOffset"          INTEGER NOT NULL DEFAULT 0,
                    "LastLineNumber"      INTEGER NOT NULL DEFAULT 0,
                    "LastFileLength"      INTEGER NOT NULL DEFAULT 0,
                    "FirstSeenAt"         TEXT    NOT NULL DEFAULT '',
                    "LastParsedAt"        TEXT    NOT NULL DEFAULT ''
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "ChatMessages" (
                    "Id"                  INTEGER NOT NULL CONSTRAINT "PK_ChatMessages" PRIMARY KEY AUTOINCREMENT,
                    "OccurredAt"          TEXT    NOT NULL DEFAULT '',
                    "ChannelName"         TEXT    NOT NULL DEFAULT '',
                    "ChannelId"           TEXT,
                    "ListenerCharacterId" INTEGER,
                    "ListenerName"        TEXT,
                    "SenderName"          TEXT    NOT NULL DEFAULT '',
                    "Message"             TEXT    NOT NULL DEFAULT '',
                    "IsSystemMessage"     INTEGER NOT NULL DEFAULT 0,
                    "SystemName"          TEXT,
                    "SourceFile"          TEXT    NOT NULL DEFAULT '',
                    "LineNumber"          INTEGER NOT NULL DEFAULT 0
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChatMessages_SourceFile_LineNumber"
                ON "ChatMessages" ("SourceFile", "LineNumber")
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE INDEX IF NOT EXISTS "IX_ChatMessages_OccurredAt"
                ON "ChatMessages" ("OccurredAt")
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE INDEX IF NOT EXISTS "IX_ChatMessages_ChannelName_OccurredAt"
                ON "ChatMessages" ("ChannelName", "OccurredAt")
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "AlertSettings" (
                    "Id"                    INTEGER NOT NULL PRIMARY KEY,
                    "SkillQueueEmpty"       INTEGER NOT NULL DEFAULT 1,
                    "SkillQueuePaused"      INTEGER NOT NULL DEFAULT 1,
                    "SkillQueueEmptyInDays" INTEGER NOT NULL DEFAULT 1,
                    "SkillQueueEmptyDays"   INTEGER NOT NULL DEFAULT 30,
                    "AssetSafety"                INTEGER NOT NULL DEFAULT 1,
                    "InactiveStandingProjects"   INTEGER NOT NULL DEFAULT 1,
                    "StandingBuyOrdersAttention" INTEGER NOT NULL DEFAULT 1,
                    "UnriggedIndustryJobs"       INTEGER NOT NULL DEFAULT 1
                )
                """);
            // Existing installs predate these alerts.
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "AlertSettings" ADD COLUMN "StandingBuyOrdersAttention" INTEGER NOT NULL DEFAULT 1"""); } catch { }
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "AlertSettings" ADD COLUMN "UnriggedIndustryJobs" INTEGER NOT NULL DEFAULT 1"""); } catch { }
            db.Database.ExecuteSqlRaw("""
                INSERT OR IGNORE INTO "AlertSettings" ("Id") VALUES (1)
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "TradeOpportunitiesSettings" (
                    "Id"                     INTEGER NOT NULL PRIMARY KEY,
                    "ExcludedMarketGroupIds" TEXT    NOT NULL DEFAULT ''
                )
                """);
            // Defaults for new installs: Blueprints & Reactions (2), Ship SKINs (1954),
            // Special Edition Assets (1659), Apparel (1396), Skills (150), Trade Goods (19).
            db.Database.ExecuteSqlRaw("""
                INSERT OR IGNORE INTO "TradeOpportunitiesSettings" ("Id", "ExcludedMarketGroupIds") VALUES (1, '2,1954,1659,1396,150,19')
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "IndustryOpportunitiesSettings" (
                    "Id"                     INTEGER NOT NULL PRIMARY KEY,
                    "ExcludedMarketGroupIds" TEXT    NOT NULL DEFAULT ''
                )
                """);
            // No default exclusions for Industry Opportunities.
            db.Database.ExecuteSqlRaw("""
                INSERT OR IGNORE INTO "IndustryOpportunitiesSettings" ("Id", "ExcludedMarketGroupIds") VALUES (1, '')
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "DismissedAlerts" (
                    "CharacterId"    INTEGER NOT NULL,
                    "NotificationId" INTEGER NOT NULL,
                    PRIMARY KEY ("CharacterId", "NotificationId")
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "AppPreferences" (
                    "Key"   TEXT NOT NULL PRIMARY KEY,
                    "Value" TEXT NOT NULL
                )
                """);
            // ── Eve Mail ─────────────────────────────────────────────────────────
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiMailHeaders" (
                    "MailId"       INTEGER NOT NULL,
                    "CharacterId"  INTEGER NOT NULL,
                    "FromId"       INTEGER NOT NULL DEFAULT 0,
                    "FromName"     TEXT    NOT NULL DEFAULT '',
                    "Subject"      TEXT    NOT NULL DEFAULT '',
                    "Timestamp"    TEXT    NOT NULL DEFAULT '',
                    "IsRead"       INTEGER NOT NULL DEFAULT 0,
                    "Labels"       TEXT    NOT NULL DEFAULT '',
                    "BodyFetched"  INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("MailId", "CharacterId")
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiMailBodies" (
                    "MailId" INTEGER NOT NULL PRIMARY KEY,
                    "Body"   TEXT    NOT NULL DEFAULT ''
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiMailRecipients" (
                    "Id"            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "MailId"        INTEGER NOT NULL,
                    "RecipientId"   INTEGER NOT NULL DEFAULT 0,
                    "RecipientType" TEXT    NOT NULL DEFAULT '',
                    "RecipientName" TEXT    NOT NULL DEFAULT ''
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiMailLabels" (
                    "CharacterId"  INTEGER NOT NULL,
                    "LabelId"      INTEGER NOT NULL,
                    "Name"         TEXT    NOT NULL DEFAULT '',
                    "Color"        TEXT    NOT NULL DEFAULT '',
                    "UnreadCount"  INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("CharacterId", "LabelId")
                )
                """);
            // One-time migration: copy data from old EveMail* tables then drop them
            foreach (var (oldTbl, newTbl) in new[] {
                ("EveMailHeaders", "EsiMailHeaders"), ("EveMailBodies", "EsiMailBodies"),
                ("EveMailRecipients", "EsiMailRecipients"), ("EveMailLabels", "EsiMailLabels") })
            {
                try
                {
                    // oldTbl/newTbl come from the fixed array above, not external input — table
                    // identifiers can't be parameterized via ExecuteSql anyway, so ExecuteSqlRaw
                    // is the correct tool here despite the analyzer's generic warning.
#pragma warning disable EF1002
                    db.Database.ExecuteSqlRaw(
                        $"INSERT OR IGNORE INTO \"{newTbl}\" SELECT * FROM \"{oldTbl}\"");
                    db.Database.ExecuteSqlRaw($"DROP TABLE \"{oldTbl}\"");
#pragma warning restore EF1002
                }
                catch { /* table already gone — migration already ran */ }
            }

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "KillMailDetails" (
                    "KillMailId"        INTEGER NOT NULL PRIMARY KEY,
                    "KillMailHash"      TEXT    NOT NULL DEFAULT '',
                    "KillMailTime"      TEXT    NOT NULL DEFAULT '',
                    "SolarSystemId"     INTEGER NOT NULL DEFAULT 0,
                    "MoonId"            INTEGER,
                    "WarId"             INTEGER,
                    "VictimCharId"      INTEGER NOT NULL DEFAULT 0,
                    "VictimCorpId"      INTEGER NOT NULL DEFAULT 0,
                    "VictimAllianceId"  INTEGER,
                    "VictimFactionId"   INTEGER,
                    "VictimShipTypeId"  INTEGER NOT NULL DEFAULT 0,
                    "VictimDamageTaken" INTEGER NOT NULL DEFAULT 0,
                    "VictimPosX"        REAL,
                    "VictimPosY"        REAL,
                    "VictimPosZ"        REAL
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "KillMailAttackers" (
                    "Id"             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "KillMailId"     INTEGER NOT NULL,
                    "CharacterId"    INTEGER,
                    "CorporationId"  INTEGER,
                    "AllianceId"     INTEGER,
                    "FactionId"      INTEGER,
                    "DamageDone"     INTEGER NOT NULL DEFAULT 0,
                    "FinalBlow"      INTEGER NOT NULL DEFAULT 0,
                    "SecurityStatus" REAL    NOT NULL DEFAULT 0.0,
                    "ShipTypeId"     INTEGER,
                    "WeaponTypeId"   INTEGER
                )
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "KillMailItems" (
                    "Id"                INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "KillMailId"        INTEGER NOT NULL,
                    "Flag"              INTEGER NOT NULL DEFAULT 0,
                    "ItemTypeId"        INTEGER NOT NULL DEFAULT 0,
                    "QuantityDestroyed" INTEGER,
                    "QuantityDropped"   INTEGER,
                    "Singleton"         INTEGER NOT NULL DEFAULT 0
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "ZkbKillFlags" (
                    "KillMailId"  INTEGER NOT NULL PRIMARY KEY,
                    "SeenOnZkbAt" TEXT,
                    "PostedAt"    TEXT,
                    "PostResult"  TEXT    NOT NULL DEFAULT ''
                )
                """);

            // Added once zKillboard import pushed KillMailDetails/Attackers/Items well past
            // the row counts these tables saw before (100K+ and growing continuously via
            // the firehose) — without these, the Kills browser's "most recent N" query and
            // its per-kill attacker/item lookups were full-table scans.
            db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_KillMailDetails_KillMailTime" ON "KillMailDetails" ("KillMailTime")""");
            db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_KillMailAttackers_KillMailId" ON "KillMailAttackers" ("KillMailId")""");
            db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_KillMailItems_KillMailId" ON "KillMailItems" ("KillMailId")""");

            // ── Structures — the app's own editable record ──────────────────────
            // Fed from EsiStructureNames by the polling sync, but never written by it: the UI
            // edits this table, so ESI-owned data stays ESI-owned. StructureId is the in-game
            // location id and is the primary key, which is what makes a hand-added row and a
            // polled row the same record.
            foreach (var sql in new[]
            {
                """
                CREATE TABLE IF NOT EXISTS "Structures" (
                    "StructureId"        INTEGER NOT NULL PRIMARY KEY,
                    "Name"               TEXT    NOT NULL DEFAULT '',
                    "SolarSystemId"      INTEGER NOT NULL DEFAULT 0,
                    "TypeId"             INTEGER NOT NULL DEFAULT 0,
                    "OwnerId"            INTEGER NOT NULL DEFAULT 0,
                    "AllianceId"         INTEGER NOT NULL DEFAULT 0,
                    "X"                  REAL    NOT NULL DEFAULT 0,
                    "Y"                  REAL    NOT NULL DEFAULT 0,
                    "Z"                  REAL    NOT NULL DEFAULT 0,
                    "NearestCelestialId" INTEGER NOT NULL DEFAULT 0,
                    "NearestCelestial"   TEXT    NOT NULL DEFAULT '',
                    "Status"             INTEGER NOT NULL DEFAULT 0,
                    "Notes"              TEXT    NOT NULL DEFAULT '',
                    "UpdatedBy"          TEXT    NOT NULL DEFAULT 'esi',
                    "UpdatedAt"          TEXT    NOT NULL DEFAULT '2000-01-01 00:00:00+00:00')
                """,
                """
                CREATE TABLE IF NOT EXISTS "StructureFittings" (
                    "Id"          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "StructureId" INTEGER NOT NULL,
                    "Band"        TEXT    NOT NULL DEFAULT '',
                    "SlotIndex"   INTEGER NOT NULL DEFAULT 0,
                    "TypeId"      INTEGER NOT NULL DEFAULT 0)
                """,
                // Unique so a slot can only hold one module — the constraint, not the UI, is what
                // guarantees it.
                """CREATE UNIQUE INDEX IF NOT EXISTS "IX_StructureFittings_Slot" ON "StructureFittings" ("StructureId","Band","SlotIndex")""",
                """
                CREATE TABLE IF NOT EXISTS "EveRefStructures" (
                    "StructureId"   INTEGER NOT NULL PRIMARY KEY,
                    "Name"          TEXT    NOT NULL DEFAULT '',
                    "OwnerId"       INTEGER NOT NULL DEFAULT 0,
                    "SolarSystemId" INTEGER NOT NULL DEFAULT 0,
                    "RegionId"      INTEGER NOT NULL DEFAULT 0,
                    "TypeId"        INTEGER NOT NULL DEFAULT 0,
                    "X"             REAL    NOT NULL DEFAULT 0,
                    "Y"             REAL    NOT NULL DEFAULT 0,
                    "Z"             REAL    NOT NULL DEFAULT 0,
                    "IsPublic"      INTEGER NOT NULL DEFAULT 0,
                    "IsMarket"      INTEGER NOT NULL DEFAULT 0,
                    "FirstSeen"     TEXT    NOT NULL DEFAULT '',
                    "FetchedAt"     TEXT    NOT NULL DEFAULT '2000-01-01 00:00:00+00:00')
                """,
                """
                CREATE TABLE IF NOT EXISTS "IndyStructureServices" (
                    "Id"          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "StructureId" INTEGER NOT NULL,
                    "TypeId"      INTEGER NOT NULL DEFAULT 0)
                """,
                """CREATE INDEX IF NOT EXISTS "IX_IndyStructureServices_StructureId" ON "IndyStructureServices" ("StructureId")""",
            })
            {
                try { db.Database.ExecuteSqlRaw(sql); } catch { /* already present */ }
            }

            // "Which killmails was this character an attacker on" — the Overview's kill count,
            // and the one direction the KillMailId index above cannot serve. At 7.8M attacker
            // rows it was a full SCAN taking ~600 ms, repeated on every 60-second Overview
            // refresh. Both columns are in the index so the sub-query is answered from the
            // index alone: measured 599 ms -> under 1 ms, plan SCAN -> SEARCH USING COVERING
            // INDEX. Worth its disk on a table this size.
            db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_KillMailAttackers_CharacterId" ON "KillMailAttackers" ("CharacterId", "KillMailId")""");

            // ── Map statistics — hourly buckets + daily rollup ──────────────────
            // Keyed by the CCP hour bucket, not by fetch time, so a row from the live ESI
            // poll and the same hour recovered later from the EVE Ref archive collide on the
            // primary key rather than duplicating.
            foreach (var sql in new[]
            {
                """
                CREATE TABLE IF NOT EXISTS "MapSystemJumps" (
                    "Bucket"    TEXT    NOT NULL,
                    "SystemId"  INTEGER NOT NULL,
                    "ShipJumps" INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("Bucket", "SystemId")
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "MapSystemKills" (
                    "Bucket"    TEXT    NOT NULL,
                    "SystemId"  INTEGER NOT NULL,
                    "ShipKills" INTEGER NOT NULL DEFAULT 0,
                    "PodKills"  INTEGER NOT NULL DEFAULT 0,
                    "NpcKills"  INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("Bucket", "SystemId")
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "MapSystemDailies" (
                    "Day"       TEXT    NOT NULL,
                    "SystemId"  INTEGER NOT NULL,
                    "ShipJumps" INTEGER NOT NULL DEFAULT 0,
                    "ShipKills" INTEGER NOT NULL DEFAULT 0,
                    "PodKills"  INTEGER NOT NULL DEFAULT 0,
                    "NpcKills"  INTEGER NOT NULL DEFAULT 0,
                    "Hours"     INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("Day", "SystemId")
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "MapSovereignties" (
                    "Bucket"        TEXT    NOT NULL,
                    "SystemId"      INTEGER NOT NULL,
                    "FactionId"     INTEGER,
                    "CorporationId" INTEGER,
                    "AllianceId"    INTEGER,
                    PRIMARY KEY ("Bucket", "SystemId")
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "MapSovStructures" (
                    "Bucket"          TEXT    NOT NULL,
                    "StructureId"     INTEGER NOT NULL,
                    "SystemId"        INTEGER NOT NULL,
                    "AllianceId"      INTEGER,
                    "StructureTypeId" INTEGER NOT NULL DEFAULT 0,
                    "Adm"             REAL,
                    "VulnerableStart" TEXT,
                    "VulnerableEnd"   TEXT,
                    PRIMARY KEY ("Bucket", "StructureId")
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "MapIndustryIndices" (
                    "Bucket"    TEXT    NOT NULL,
                    "SystemId"  INTEGER NOT NULL,
                    "Activity"  TEXT    NOT NULL,
                    "CostIndex" REAL    NOT NULL DEFAULT 0,
                    PRIMARY KEY ("Bucket", "SystemId", "Activity")
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "MapFactionWarfares" (
                    "Bucket"                 TEXT    NOT NULL,
                    "SystemId"               INTEGER NOT NULL,
                    "OwnerFactionId"         INTEGER NOT NULL DEFAULT 0,
                    "OccupierFactionId"      INTEGER NOT NULL DEFAULT 0,
                    "ContestedState"         TEXT    NOT NULL DEFAULT '',
                    "VictoryPoints"          INTEGER NOT NULL DEFAULT 0,
                    "VictoryPointsThreshold" INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("Bucket", "SystemId")
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "MapIncursions" (
                    "Bucket"          TEXT    NOT NULL,
                    "ConstellationId" INTEGER NOT NULL,
                    "StagingSystemId" INTEGER NOT NULL DEFAULT 0,
                    "FactionId"       INTEGER NOT NULL DEFAULT 0,
                    "State"           TEXT    NOT NULL DEFAULT '',
                    "Influence"       REAL    NOT NULL DEFAULT 0,
                    "HasBoss"         INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("Bucket", "ConstellationId")
                )
                """,
                // Records that a bucket was fetched at all. A quiet hour legitimately produces
                // no stat rows, so without this an empty hour is indistinguishable from one we
                // never had — and every gap-fill pass would re-download it forever.
                """
                CREATE TABLE IF NOT EXISTS "MapStatBuckets" (
                    "Dataset"  TEXT    NOT NULL,
                    "Bucket"   TEXT    NOT NULL,
                    "StoredAt" TEXT    NOT NULL,
                    "Source"   TEXT    NOT NULL DEFAULT '',
                    "RowCount" INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY ("Dataset", "Bucket")
                )
                """,
                """CREATE INDEX IF NOT EXISTS "IX_MapSystemJumps_Bucket"     ON "MapSystemJumps"    ("Bucket")""",
                """CREATE INDEX IF NOT EXISTS "IX_MapSystemKills_Bucket"     ON "MapSystemKills"    ("Bucket")""",
                """CREATE INDEX IF NOT EXISTS "IX_MapSystemDailies_Day"      ON "MapSystemDailies"  ("Day")""",
                """CREATE INDEX IF NOT EXISTS "IX_MapSovereignties_Bucket"   ON "MapSovereignties"  ("Bucket")""",
                """CREATE INDEX IF NOT EXISTS "IX_MapSovStructures_SystemId" ON "MapSovStructures"  ("SystemId")""",
                // The system view lists recent kills for one system; without this it is a full
                // scan of a table that is well past half a million rows.
                """CREATE INDEX IF NOT EXISTS "IX_KillMailDetails_SolarSystemId" ON "KillMailDetails" ("SolarSystemId")""",
                """CREATE INDEX IF NOT EXISTS "IX_EsiStructureNames_SolarSystemId" ON "EsiStructureNames" ("SolarSystemId")""",

                // ── SDE tables added after this database was last imported ──────────
                // The SDE importer creates its own tables, but only while an import runs. A
                // database imported before one of these was introduced therefore has code
                // querying a table that does not exist yet, which throws rather than returning
                // nothing — the Universe tool died on "no such table: SdePlanetResources".
                // Creating them empty here means the feature is simply blank until the next
                // import instead of breaking the page.
                """CREATE TABLE IF NOT EXISTS "SdePlanetResources" ("PlanetId" INTEGER NOT NULL PRIMARY KEY, "Power" INTEGER NOT NULL DEFAULT 0, "Workforce" INTEGER NOT NULL DEFAULT 0, "ReagentPerCycle" INTEGER NOT NULL DEFAULT 0, "ReagentCycleTime" INTEGER NOT NULL DEFAULT 0, "SecuredCapacity" INTEGER NOT NULL DEFAULT 0)""",
                """CREATE TABLE IF NOT EXISTS "SdeAgents" ("AgentId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL DEFAULT '', "CorporationId" INTEGER NOT NULL DEFAULT 0, "LocationId" INTEGER NOT NULL DEFAULT 0, "AgentTypeId" INTEGER NOT NULL DEFAULT 0, "DivisionId" INTEGER NOT NULL DEFAULT 0, "Level" INTEGER NOT NULL DEFAULT 0, "IsLocator" INTEGER NOT NULL DEFAULT 0)""",
                """CREATE INDEX IF NOT EXISTS "IX_SdeAgents_Location" ON "SdeAgents" ("LocationId")""",
                """CREATE TABLE IF NOT EXISTS "SdeAgentTypes" ("AgentTypeId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL DEFAULT '')""",
                """CREATE TABLE IF NOT EXISTS "SdeCorpDivisions" ("DivisionId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL DEFAULT '')""",
                """CREATE TABLE IF NOT EXISTS "SdeStationServices" ("ServiceId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL DEFAULT '')""",
                """CREATE TABLE IF NOT EXISTS "SdeStationOperations" ("OperationId" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL DEFAULT '')""",
                """CREATE TABLE IF NOT EXISTS "SdeStationOperationServices" ("OperationId" INTEGER NOT NULL, "ServiceId" INTEGER NOT NULL, PRIMARY KEY ("OperationId", "ServiceId"))""",

                // ── LP values: median alongside the mean ────────────────────────────
                // Added to the CREATE TABLE after those tables already existed, and
                // CREATE TABLE IF NOT EXISTS does not alter an existing table — so every
                // database that had already run the LP valuation was missing the column
                // and the tool failed with "no such column: l.MedianIskPerLp".
                """ALTER TABLE "LpCorpValues"         ADD COLUMN "MedianIskPerLp" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "LpCorpValueSnapshots" ADD COLUMN "MedianIskPerLp" REAL NOT NULL DEFAULT 0""",

                // ── Indy Parks: catch-all facility ──────────────────────────────────
                // Where jobs go when no category assignment covers the item. Before this
                // existed such an item aborted the whole calculation.
                """ALTER TABLE "IndyParks" ADD COLUMN "DefaultStructureId" INTEGER NULL""",

                // ── SDE COLUMNS added after this database was last imported ─────────
                // Same problem as the tables above, one level down. SdeImportService adds
                // these with ALTER, but only while an import runs, so a database imported
                // before a column existed has EF querying a column the table lacks — and
                // that throws on the whole entity, not just the missing value. The
                // Production Calculator died on "no such column: s.Radius" this way.
                // Mirror of the alters list in SdeImportService.EnsureSdeSchemaAsync;
                // keep the two in step.
                """ALTER TABLE "SdeStations"       ADD COLUMN "OperationId" INTEGER""",
                """ALTER TABLE "SdeGroups"         ADD COLUMN "Anchorable" INTEGER NOT NULL DEFAULT 0""",
                """ALTER TABLE "SdeGroups"         ADD COLUMN "Anchored"   INTEGER NOT NULL DEFAULT 0""",
                """ALTER TABLE "SdeTypes"          ADD COLUMN "GraphicId"  INTEGER""",
                """ALTER TABLE "SdeTypes"          ADD COLUMN "FactionId"  INTEGER""",
                """ALTER TABLE "SdeTypes"          ADD COLUMN "RaceId"     INTEGER""",
                """ALTER TABLE "SdeTypes"          ADD COLUMN "MetaGroupId" INTEGER""",
                """ALTER TABLE "SdeRegions"        ADD COLUMN "X" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "SdeRegions"        ADD COLUMN "Y" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "SdeRegions"        ADD COLUMN "Z" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "SdeConstellations" ADD COLUMN "X" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "SdeConstellations" ADD COLUMN "Y" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "SdeConstellations" ADD COLUMN "Z" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "X" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "Y" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "Z" REAL NOT NULL DEFAULT 0""",
                """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "X2D" REAL""",
                """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "Y2D" REAL""",
                """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "SecurityClass" TEXT NOT NULL DEFAULT ''""",
                """ALTER TABLE "SdeSolarSystems"   ADD COLUMN "Radius" REAL NOT NULL DEFAULT 0""",

                // ── Intel channels ──────────────────────────────────────────────
                // One-time removal of chat already stored twice — the same conversation logged
                // by two of the user's characters, or imported from a second PC's log folder.
                // The unique index on (SourceFile, LineNumber) only ever stopped one file being
                // read twice; it cannot see that two files hold the same messages. Keeps the
                // lowest Id of each group, so provenance points at whichever arrived first.
                """DELETE FROM "ChatMessages" WHERE "Id" IN (SELECT "Id" FROM (SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "ChannelName", "OccurredAt", "SenderName", "Message" ORDER BY "Id") AS rn FROM "ChatMessages") WHERE rn > 1)""",

                """CREATE TABLE IF NOT EXISTS "IntelReports" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "ReportedAt" TEXT NOT NULL DEFAULT '', "ChannelName" TEXT NOT NULL DEFAULT '', "ReporterName" TEXT NOT NULL DEFAULT '', "SystemId" INTEGER NOT NULL DEFAULT 0, "SystemName" TEXT NOT NULL DEFAULT '', "PlayerCount" INTEGER NOT NULL DEFAULT 0, "Note" TEXT NULL, "Obsolete" INTEGER NOT NULL DEFAULT 0, "ObsoleteSetOn" TEXT NULL, "ChatMessageId" INTEGER NOT NULL DEFAULT 0)""",
                """CREATE UNIQUE INDEX IF NOT EXISTS "IX_IntelReports_ChatMessageId" ON "IntelReports" ("ChatMessageId")""",
                """CREATE INDEX IF NOT EXISTS "IX_IntelReports_System_Time" ON "IntelReports" ("SystemId", "ReportedAt")""",
                """CREATE INDEX IF NOT EXISTS "IX_IntelReports_Obsolete_Time" ON "IntelReports" ("Obsolete", "ReportedAt")""",

                """CREATE TABLE IF NOT EXISTS "IntelReportCharacters" ("IntelReportId" INTEGER NOT NULL, "CharacterId" INTEGER NOT NULL, "CharacterName" TEXT NOT NULL DEFAULT '', PRIMARY KEY ("IntelReportId", "CharacterId"))""",
                """CREATE INDEX IF NOT EXISTS "IX_IntelReportCharacters_CharacterId" ON "IntelReportCharacters" ("CharacterId")""",
                """ALTER TABLE "IntelReportCharacters" ADD COLUMN "ShipTypeId" INTEGER NULL""",
                """ALTER TABLE "IntelReportCharacters" ADD COLUMN "ShipName" TEXT NULL""",
                """ALTER TABLE "IntelReports" ADD COLUMN "ReporterCharacterId" INTEGER NULL""",
                """ALTER TABLE "IntelReports" ADD COLUMN "NoVisual" INTEGER NOT NULL DEFAULT 0""",
                """ALTER TABLE "IntelReports" ADD COLUMN "Message" TEXT NOT NULL DEFAULT ''""",
                // Intel whose chat message no longer exists. Only two things ever delete a chat
                // message — the dedupe above, and a log file being re-read after its length
                // appeared to go backwards — and in both cases the surviving copy of the message
                // has been re-parsed into a fresh report. So these are duplicates of a report
                // that is already present, and they show as repeated sightings in the UI.
                // Nothing purges chat messages on age, so this cannot reach real history.
                """DELETE FROM "IntelReportCharacters" WHERE "IntelReportId" IN (SELECT "Id" FROM "IntelReports" r WHERE NOT EXISTS (SELECT 1 FROM "ChatMessages" m WHERE m."Id" = r."ChatMessageId"))""",
                """DELETE FROM "IntelReports" WHERE NOT EXISTS (SELECT 1 FROM "ChatMessages" m WHERE m."Id" = "IntelReports"."ChatMessageId")""",

                """CREATE TABLE IF NOT EXISTS "NameLookupMisses" ("Name" TEXT NOT NULL PRIMARY KEY, "CheckedAt" TEXT NULL)""",
                """CREATE TABLE IF NOT EXISTS "CharacterAffiliations" ("CharacterId" INTEGER NOT NULL PRIMARY KEY, "CorporationId" INTEGER NOT NULL DEFAULT 0, "AllianceId" INTEGER NOT NULL DEFAULT 0, "PulledAt" TEXT NULL)""",

                """CREATE TABLE IF NOT EXISTS "SaleExclusions" ("Kind" TEXT NOT NULL, "SaleId" INTEGER NOT NULL, "MarkedAt" TEXT NOT NULL DEFAULT '', PRIMARY KEY ("Kind", "SaleId"))""",

                // ── Alarms ───────────────────────────────────────────────────
                // NB: braces are doubled. ExecuteSqlRaw runs the statement through string.Format,
                // so a literal '{}' default is read as a format placeholder and throws — and
                // since this loop swallows exceptions, the table would simply never be created.
                """CREATE TABLE IF NOT EXISTS "Alarms" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL DEFAULT '', "Enabled" INTEGER NOT NULL DEFAULT 1, "ConditionType" TEXT NOT NULL DEFAULT '', "ConditionJson" TEXT NOT NULL DEFAULT '{{}}', "Repeat" INTEGER NOT NULL DEFAULT 1, "PollSeconds" INTEGER NOT NULL DEFAULT 60, "CooldownSeconds" INTEGER NOT NULL DEFAULT 0, "Primed" INTEGER NOT NULL DEFAULT 0, "CreatedBy" TEXT NOT NULL DEFAULT 'user', "CreatedAt" TEXT NOT NULL DEFAULT '', "LastCheckedAt" TEXT NULL, "LastFiredAt" TEXT NULL, "FireCount" INTEGER NOT NULL DEFAULT 0, "LastError" TEXT NULL)""",
                """CREATE INDEX IF NOT EXISTS "IX_Alarms_Enabled" ON "Alarms" ("Enabled")""",

                """CREATE TABLE IF NOT EXISTS "AlarmActions" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "AlarmId" INTEGER NOT NULL DEFAULT 0, "Kind" INTEGER NOT NULL DEFAULT 0, "ConfigJson" TEXT NOT NULL DEFAULT '{{}}', "Ordinal" INTEGER NOT NULL DEFAULT 0)""",
                """CREATE INDEX IF NOT EXISTS "IX_AlarmActions_AlarmId" ON "AlarmActions" ("AlarmId")""",

                // The ledger that stops an alarm re-announcing what it has already announced.
                """CREATE TABLE IF NOT EXISTS "AlarmSeenKeys" ("AlarmId" INTEGER NOT NULL, "MatchKey" TEXT NOT NULL, "FirstSeenAt" TEXT NOT NULL DEFAULT '', PRIMARY KEY ("AlarmId", "MatchKey"))""",
                """CREATE INDEX IF NOT EXISTS "IX_AlarmSeenKeys_Alarm_Seen" ON "AlarmSeenKeys" ("AlarmId", "FirstSeenAt")""",

                """CREATE TABLE IF NOT EXISTS "AlarmEvents" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "AlarmId" INTEGER NOT NULL DEFAULT 0, "FiredAt" TEXT NOT NULL DEFAULT '', "Summary" TEXT NOT NULL DEFAULT '', "DetailJson" TEXT NULL, "MatchCount" INTEGER NOT NULL DEFAULT 0)""",
                """CREATE INDEX IF NOT EXISTS "IX_AlarmEvents_Alarm_Fired" ON "AlarmEvents" ("AlarmId", "FiredAt")""",

                """CREATE TABLE IF NOT EXISTS "AlarmAlerts" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "AlarmId" INTEGER NOT NULL DEFAULT 0, "AlarmEventId" INTEGER NOT NULL DEFAULT 0, "CreatedAt" TEXT NOT NULL DEFAULT '', "Title" TEXT NOT NULL DEFAULT '', "Body" TEXT NULL, "Dismissed" INTEGER NOT NULL DEFAULT 0, "DismissedAt" TEXT NULL)""",
                """CREATE INDEX IF NOT EXISTS "IX_AlarmAlerts_Dismissed_Created" ON "AlarmAlerts" ("Dismissed", "CreatedAt")""",

                // Intel alarm keys used to be the report's row id, which changes whenever a chat
                // log is re-read — so old sightings kept looking new. They are now content-based
                // and contain a '|'. Re-prime any alarm still holding the old style so the
                // switch banks what is currently visible instead of announcing all of it, then
                // drop those keys. Both statements no-op once there are no old keys left, so
                // this is safe to run on every start.
                """UPDATE "Alarms" SET "Primed" = 0 WHERE "ConditionType" = 'intel' AND EXISTS (SELECT 1 FROM "AlarmSeenKeys" k WHERE k."AlarmId" = "Alarms"."Id" AND k."MatchKey" LIKE 'intel:%' AND k."MatchKey" NOT LIKE '%|%')""",
                """DELETE FROM "AlarmSeenKeys" WHERE "MatchKey" LIKE 'intel:%' AND "MatchKey" NOT LIKE '%|%'""",
            }) { try { db.Database.ExecuteSqlRaw(sql); } catch { } }
            // Repairs stations imported before ConstellationId/RegionId/Security were populated
            // from the solar system. The importer now fills them, but an existing install only
            // gets correct values on its next SDE import, which may be months away — and a zero
            // here reads as a legitimate id, so queries grouping on it silently return nothing
            // rather than failing. Restricted to rows that still need it, so it costs nothing
            // once done and is safe to run on every start.
            try
            {
                db.Database.ExecuteSqlRaw("""
                    UPDATE "SdeStations"
                    SET "ConstellationId" = (SELECT s."ConstellationId" FROM "SdeSolarSystems" s
                                             WHERE s."SolarSystemId" = "SdeStations"."SolarSystemId"),
                        "RegionId"        = (SELECT s."RegionId"        FROM "SdeSolarSystems" s
                                             WHERE s."SolarSystemId" = "SdeStations"."SolarSystemId"),
                        "Security"        = (SELECT s."Security"        FROM "SdeSolarSystems" s
                                             WHERE s."SolarSystemId" = "SdeStations"."SolarSystemId")
                    WHERE ("ConstellationId" = 0 OR "RegionId" = 0)
                      AND EXISTS (SELECT 1 FROM "SdeSolarSystems" s
                                  WHERE s."SolarSystemId" = "SdeStations"."SolarSystemId")
                    """);
            }
            catch { /* nothing to repair on a database that has never had an SDE import */ }
        }
        }); // end Task.Run — schema migration complete

        p.Report((94, "Loading settings…"));
        var timerSettings = Services.GetRequiredService<TimerSettingsService>();
        await timerSettings.LoadAsync();
        var appPrefs = Services.GetRequiredService<AppPreferencesService>();
        await appPrefs.LoadAsync();
        var corpTop10Exclude = Services.GetRequiredService<CorpTop10ExcludeService>();
        await corpTop10Exclude.LoadAsync();

        p.Report((98, "Starting…"));
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopFinal)
        {
            mainWindow            = new MainWindow();
            mainWindow.DataContext = Services.GetRequiredService<MainWindowViewModel>();
            desktopFinal.MainWindow   = mainWindow;
            desktopFinal.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            mainWindow.Show();

            await Task.Delay(350); // brief pause so the 100 % state is visible
            splash?.Close();
        }

        // Start background services
        polling?.Start();
        marketPricing?.Start();
        marketHistory?.Start();
        contracts?.Start();
        lpStore?.Start();
        Services.GetRequiredService<DatabaseBackupService>().Start();
        gameLogs?.Start();
        chatLogs?.Start();
        zkbPolling?.Start();
        zkbFirehose?.Start();
        zkbBackfill?.Start();
        zkbPost?.Start();
        Services.GetRequiredService<EntityNameBackfillService>().Start();
        // Started early and independently: everything else consults its verdict.
        Services.GetRequiredService<EveServerStatusService>().Start();

        // Map statistics for the Universe tool. Both loops write rows keyed by CCP's hour
        // bucket, so the archive catch-up and the live poller cannot collide even when they
        // run over the same hour at the same time.
        Services.GetRequiredService<MapStatsBackfillService>().Start();
        Services.GetRequiredService<MapStatsPollingService>().Start();

        // Cheap when idle: the loop only touches the database for alarms whose interval is up.
        Services.GetRequiredService<AlarmService>().Start();

        // Writes to the error log only when the UI thread actually freezes, so a healthy
        // session records nothing.
        Services.GetRequiredService<UiStallMonitor>().Start();
    }

    private static void PositionSplashOnLastMonitor(SplashWindow splash)
    {
        var pos = AppConfig.GetWindowPosition();
        if (pos is null) return;

        // Find the screen that contains the saved position.
        var screens = splash.Screens?.All;
        if (screens is null || screens.Count == 0) return;

        var target = screens.FirstOrDefault(s => s.Bounds.Contains(new Avalonia.PixelPoint(pos.Value.X, pos.Value.Y)))
                  ?? screens.First();

        // WorkingArea is in physical pixels; splash Width/Height are logical pixels.
        // Multiply by scaling to get physical pixel size for centering.
        var scale = target.Scaling;
        var splashW = (int)(900 * scale);
        var splashH = (int)(360 * scale);

        var b = target.WorkingArea;
        int x = b.X + Math.Max(0, (b.Width  - splashW) / 2);
        int y = b.Y + Math.Max(0, (b.Height - splashH) / 2);

        splash.WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.Manual;
        splash.Position = new Avalonia.PixelPoint(x, y);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Database — path can be overridden via config.json (see AppConfig)
        var dbPath = AppConfig.GetDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}")
                   .AddInterceptors(new DisableForeignKeysInterceptor()));

        // Named HTTP client for the ESI API (used by singleton EsiClient)
        services.AddHttpClient("esi", client =>
        {
            client.BaseAddress = new Uri("https://esi.evetech.net/latest/");
            client.DefaultRequestHeaders.Add("User-Agent", "EveConsole/1.0 (EVE Online companion app)");

            // Pins the ESI schema version. Without it the API answers as of its own default
            // date and newer fields are simply absent from the payload — achievement_score
            // on /characters/{id}/ is the case that surfaced this. Raise it deliberately
            // when adopting a newer field, having checked nothing else in that release
            // changes shape underneath us.
            client.DefaultRequestHeaders.Add("X-Compatibility-Date", "2026-08-01");
        });

        // Separate client for the public /status/ check. Kept apart from "esi" on purpose:
        // it is the one call that must still run while everything else is paused for
        // downtime, and it needs a short timeout so a hung request cannot stall detection.
        services.AddHttpClient("esi-public", client =>
        {
            client.BaseAddress = new Uri("https://esi.evetech.net/latest/");
            client.DefaultRequestHeaders.Add("User-Agent", "EveConsole/1.0 (EVE Online companion app)");
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        // Named HTTP client for Fuzzwork market aggregates
        services.AddHttpClient("fuzzwork", client =>
        {
            client.BaseAddress = new Uri("https://market.fuzzwork.co.uk/aggregates/");
            client.DefaultRequestHeaders.Add("User-Agent", "EveConsole/1.0 (EVE Online companion app)");
        });

        // Named HTTP client for the Slack Web API (posts as the user via their xoxp- token)
        services.AddHttpClient("slack", client =>
        {
            client.BaseAddress = new Uri("https://slack.com/api/");
            client.DefaultRequestHeaders.Add("User-Agent", "EveConsole/1.0 (EVE Online companion app)");
        });

        // Named HTTP client for zKillboard (zkillboard.com + r2z2.zkillboard.com). No
        // BaseAddress — the API and history/firehose endpoints live on different hosts,
        // so callers use absolute URLs. Automatic gzip decompression since daily dumps
        // and killmail bodies benefit from it; zKillboard's own etiquette asks for a
        // User-Agent identifying the app/maintainer.
        services.AddHttpClient("zkillboard", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "EveConsole/1.0 (https://github.com/kernoeve/EveConsole)");
            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

        // Services — EsiClient is singleton so it can hold per-character token state
        services.AddSingleton<EsiClient>();
        services.AddSingleton<EsiAuthService>();
        services.AddSingleton<SdeImportService>();
        services.AddSingleton<HoboImportService>();
        services.AddSingleton<ApiActivityLog>();
        services.AddSingleton<AppErrorLogger>();
        services.AddSingleton<UiStallMonitor>();
        services.AddSingleton<StructureSyncService>();
        services.AddSingleton<IndyStructureLinkService>();
        services.AddSingleton<IndyBulkAddService>();
        services.AddSingleton<EveRefStructureService>();
        services.AddSingleton<FittingOptionService>();
        services.AddSingleton<TimerSettingsService>();
        services.AddSingleton<AppPreferencesService>();
        services.AddSingleton<SlackAuthService>();
        services.AddSingleton<SlackService>();
        services.AddSingleton<DatabaseBackupService>();
        services.AddSingleton<EsiPollingService>();
        services.AddSingleton<NetWorthService>();
        services.AddSingleton<TypePriceHistoryService>();
        services.AddSingleton<MarketPricingService>();
        services.AddSingleton<MarketHistoryService>();
        services.AddSingleton<ContractsService>();
        services.AddSingleton<LpStoreService>();
        services.AddSingleton<LpValueService>();
        services.AddSingleton<BuildCostService>();
        services.AddSingleton<ReprocessingValueService>();
        services.AddSingleton<ProductionCalculatorService>();
        services.AddSingleton<AgentService>();
        services.AddSingleton<TtsService>();
        services.AddSingleton<SpeechInputService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<KillMailService>();
        services.AddSingleton<EveMailService>();
        services.AddSingleton<NewsService>();
        services.AddSingleton<MarketLevelService>();
        services.AddSingleton<InvLevelService>();
        services.AddSingleton<SalePostingService>();
        services.AddSingleton<BatchAddService>();
        services.AddSingleton<CorpActivityService>();
        services.AddSingleton<KillmailBrowserService>();
        services.AddSingleton<CorpTop10ExcludeService>();
        services.AddSingleton<MarketCompetitionService>();
        services.AddSingleton<StandingBuyOrderService>();

        // Worklist. Generators register as IWorklistGenerator so WorklistService picks up new
        // ones without being edited — the list of sources is the DI registrations.
        services.AddSingleton<EveConsole.Services.Worklist.WorklistMarketAltService>();
        services.AddSingleton<EveConsole.Services.Worklist.WorklistSettings>();
        services.AddSingleton<EveConsole.Services.Worklist.IWorklistGenerator,
                              EveConsole.Services.Worklist.StandingBuyOrderGenerator>();
        services.AddSingleton<EveConsole.Services.Worklist.IWorklistGenerator,
                              EveConsole.Services.Worklist.InventoryLevelGenerator>();
        // Customer orders no longer raise their own purchases. They and the inventory targets are
        // additive demand on one pool of stock, and each netting that stock against its own
        // shortfall meant neither ever asked for the real figure — so both feed
        // MaterialPurchaseGenerator instead.
        services.AddSingleton<EveConsole.Services.Worklist.IWorklistGenerator,
                              EveConsole.Services.Worklist.MaterialPurchaseGenerator>();
        services.AddSingleton<EveConsole.Services.Worklist.WorklistCorpAltService>();
        services.AddSingleton<EveConsole.Services.Worklist.IndustryAssignmentService>();
        services.AddSingleton<EveConsole.Services.Worklist.IndustryBlueprintService>();
        services.AddSingleton<EveConsole.Services.Worklist.IndustryTimeService>();
        services.AddSingleton<EveConsole.Services.Worklist.IWorklistGenerator,
                              EveConsole.Services.Worklist.StandingProjectGenerator>();
        services.AddSingleton<EveConsole.Services.Worklist.IWorklistGenerator,
                              EveConsole.Services.Worklist.IndustryJobGenerator>();
        services.AddSingleton<EveConsole.Services.Worklist.WorklistService>();
        services.AddSingleton<IndyFacilityCheckService>();

        // Game log import — reads EVE's own logs into GameLogEvents for tools to
        // query. Read-only; nothing is ever written back to an EVE-owned file.
        services.AddSingleton<MonitoringSettings>();
        services.AddSingleton<GameLogImportService>();
        // Chat import is off by default and additionally gated on a per-channel
        // allowlist — it stores other people's messages.
        services.AddSingleton<ChatLogImportService>();
        services.AddSingleton<IntelService>();

        // zKillboard integration — optional supplement to the ESI-based kill pull (see
        // ZkillboardPollingService/ZkillboardFirehoseService for why the "Mine + Corp"
        // and "All kills" scopes each use a different live mechanism). Off by default.
        services.AddSingleton<ZkillboardSettings>();
        services.AddSingleton<ZkillboardApiClient>();
        services.AddSingleton<ZkillboardKillImportService>();
        services.AddSingleton<ZkillboardPollingService>();
        services.AddSingleton<ZkillboardFirehoseService>();
        services.AddSingleton<ZkillboardBackfillService>();
        services.AddSingleton<ZkillboardPostService>();

        // Map statistics for the Universe tool. ESI serves only the current hour for these
        // endpoints, so the EVE Ref archive is the backbone and the poller only keeps the
        // newest bucket fresh — see MapStatsPollingService.
        services.AddHttpClient("everef", c =>
        {
            c.BaseAddress = new Uri(EveRefArchiveClient.BaseUrl);
            c.DefaultRequestHeaders.Add("User-Agent", "EveConsole/1.0 (+https://github.com/kernoeve/EveConsole)");
            c.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddSingleton<MapStatsSettings>();
        services.AddSingleton<EveRefArchiveClient>();
        services.AddSingleton<MapStatsService>();
        services.AddSingleton<MapStatsBackfillService>();
        services.AddSingleton<MapStatsPollingService>();
        services.AddSingleton<SystemViewService>();

        // Alarms. Nothing is defined out of the box — every alarm is one the user (or the agent
        // on their behalf) creates. See AlarmService for why firing is keyed on match identity
        // rather than on a condition merely being true.
        // Jump planning. Holds the reachable-system list once loaded, so it is a singleton.
        services.AddSingleton<JumpPlannerService>();

        services.AddSingleton<AlarmSoundService>();
        services.AddSingleton<SystemGraph>();
        services.AddSingleton(sp => AlarmConditionRegistry.CreateDefault(
            sp.GetRequiredService<SystemGraph>()));
        services.AddSingleton<AlarmActionRunner>();
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
            using var db = factory.CreateDbContext();
            return new AlarmService(
                factory,
                db.Database.GetConnectionString()!,
                sp.GetRequiredService<AlarmConditionRegistry>(),
                sp.GetRequiredService<AlarmActionRunner>(),
                sp.GetRequiredService<AppErrorLogger>());
        });

        services.AddSingleton<EntityNameBackfillService>();
        services.AddSingleton<EveServerStatusService>();
        services.AddSingleton<UiLinkSettings>();
        services.AddSingleton<ExportFormatSettings>();

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<CharacterViewModel>();
    }
}
