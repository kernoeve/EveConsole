using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EveCortex.Agent;
using EveCortex.Data;
using EveCortex.Views;
using EveCortex.ViewModels;
using EveCortex.Auth;
using EveCortex.Api;
using EveCortex.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace EveCortex;

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
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                errorLogger.Log("AppDomain", "UnhandledException", ex);
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
            // Contract prices refresh on their own loop — re-snapshot when they do.
            contracts.AfterPricing += ct => typePriceHistory.RecalculateAsync(ct);

            desktop.ShutdownRequested += async (_, e) =>
            {
                e.Cancel = true;
                var tasks = new List<Task>();
                if (polling       is not null) tasks.Add(polling.StopAsync());
                if (marketPricing is not null) tasks.Add(marketPricing.StopAsync());
                if (marketHistory is not null) tasks.Add(marketHistory.StopAsync());
                if (contracts     is not null) tasks.Add(contracts.StopAsync());
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
                    PRIMARY KEY ("EntityId")
                )
                """);

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
                    "PulledAt"      TEXT    NOT NULL DEFAULT '2000-01-01T00:00:00+00:00'
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "EsiStructureNameFailures" (
                    "StructureId" INTEGER NOT NULL PRIMARY KEY,
                    "FailedAt"    TEXT    NOT NULL DEFAULT '2000-01-01T00:00:00+00:00',
                    "StatusCode"  INTEGER NOT NULL DEFAULT 0
                )
                """);

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
                    "LowballBuyOrderThresholdPct" REAL   NOT NULL DEFAULT 25.0
                )
                """);

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
                    "IsDefault" INTEGER NOT NULL DEFAULT 0
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
                    "FacilityTax"      REAL    NOT NULL DEFAULT 1.0
                )
                """);
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
                    "UpdatedAt"    TEXT    NOT NULL DEFAULT ''
                )
                """);
            // BuildSeconds added after the schema squash — backfill it on existing DBs.
            // ALTER throws if the column already exists, so swallow that one case.
            try { db.Database.ExecuteSqlRaw("""ALTER TABLE "BuildCosts" ADD COLUMN "BuildSeconds" REAL NOT NULL DEFAULT 0"""); }
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

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "AlertSettings" (
                    "Id"                    INTEGER NOT NULL PRIMARY KEY,
                    "SkillQueueEmpty"       INTEGER NOT NULL DEFAULT 1,
                    "SkillQueuePaused"      INTEGER NOT NULL DEFAULT 1,
                    "SkillQueueEmptyInDays" INTEGER NOT NULL DEFAULT 1,
                    "SkillQueueEmptyDays"   INTEGER NOT NULL DEFAULT 30,
                    "AssetSafety"                INTEGER NOT NULL DEFAULT 1,
                    "InactiveStandingProjects"   INTEGER NOT NULL DEFAULT 1
                )
                """);
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
        Services.GetRequiredService<DatabaseBackupService>().Start();
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
            client.DefaultRequestHeaders.Add("User-Agent", "EveCortex/1.0 (EVE Online companion app)");
        });

        // Named HTTP client for Fuzzwork market aggregates
        services.AddHttpClient("fuzzwork", client =>
        {
            client.BaseAddress = new Uri("https://market.fuzzwork.co.uk/aggregates/");
            client.DefaultRequestHeaders.Add("User-Agent", "EveCortex/1.0 (EVE Online companion app)");
        });

        // Named HTTP client for the Slack Web API (posts as the user via their xoxp- token)
        services.AddHttpClient("slack", client =>
        {
            client.BaseAddress = new Uri("https://slack.com/api/");
            client.DefaultRequestHeaders.Add("User-Agent", "EveCortex/1.0 (EVE Online companion app)");
        });

        // Services — EsiClient is singleton so it can hold per-character token state
        services.AddSingleton<EsiClient>();
        services.AddSingleton<EsiAuthService>();
        services.AddSingleton<SdeImportService>();
        services.AddSingleton<HoboImportService>();
        services.AddSingleton<ApiActivityLog>();
        services.AddSingleton<AppErrorLogger>();
        services.AddSingleton<TimerSettingsService>();
        services.AddSingleton<AppPreferencesService>();
        services.AddSingleton<SlackService>();
        services.AddSingleton<DatabaseBackupService>();
        services.AddSingleton<EsiPollingService>();
        services.AddSingleton<NetWorthService>();
        services.AddSingleton<TypePriceHistoryService>();
        services.AddSingleton<MarketPricingService>();
        services.AddSingleton<MarketHistoryService>();
        services.AddSingleton<ContractsService>();
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
        services.AddSingleton<BatchAddService>();
        services.AddSingleton<CorpActivityService>();
        services.AddSingleton<KillmailBrowserService>();
        services.AddSingleton<CorpTop10ExcludeService>();

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<CharacterViewModel>();
    }
}
