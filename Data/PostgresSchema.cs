using Microsoft.EntityFrameworkCore;

namespace EveConsole.Data;

/// <summary>
/// Everything a PostgreSQL database needs beyond what <c>EnsureCreated</c> builds from the model.
///
/// <para>The SQLite path in App.axaml.cs is nearly 2,400 lines, and almost none of it applies
/// here. Of its 158 <c>CREATE TABLE</c> statements, 156 duplicate an entity EF has already
/// created — they are dead code on any fresh database, and its 116 <c>ALTER TABLE</c> patches
/// exist to carry old SQLite files forward. A server created today starts at the current schema
/// and has no history to catch up on.</para>
///
/// <para>⚠️ It could not simply be run anyway: 47 of those statements use <c>AUTOINCREMENT</c>,
/// which PostgreSQL rejects at parse time even under <c>IF NOT EXISTS</c>, so a table already
/// present would still fail. What remains below is the part that is genuinely load-bearing.</para>
///
/// <para>⚠️ This file and the SQLite block have to be kept in step by hand: a new index or seed
/// row added there and not here works for every existing user and silently does not exist for
/// Postgres ones. <c>tools/PgSchemaCheck</c> compares the two index lists and fails on drift,
/// which covers the case that is easiest to forget.</para>
/// </summary>
public static class PostgresSchema
{
    /// <param name="includeSeeds">
    /// ⚠️ False when the database is about to receive a copy of an existing one. The seeds
    /// write fixed primary keys — MarketPricingConfigs 1 and 2, AlertSettings 1 — and the
    /// copy then carries the same keys across, so seeding first turns the migration into a
    /// duplicate-key failure partway through. The rows arrive with the data instead, and the
    /// next ordinary start seeds anything genuinely absent, every seed being written to no-op
    /// when its table is already populated.
    /// </param>
    public static void Apply(
        AppDbContext db,
        IProgress<(double Pct, string Status)>? progress = null,
        bool includeSeeds = true)
    {
        progress?.Report((30, "Preparing database…"));
        foreach (var sql in Tables)  db.Database.ExecuteSqlRaw(sql);

        progress?.Report((55, "Building indexes…"));
        foreach (var sql in Indexes) db.Database.ExecuteSqlRaw(sql);

        if (!includeSeeds) return;

        progress?.Report((75, "Writing defaults…"));
        foreach (var sql in Seeds)   db.Database.ExecuteSqlRaw(sql);
    }

    /// <summary>
    /// Tables <c>EnsureCreated</c> will not add.
    ///
    /// <para>It builds a schema only into an empty database, so anything introduced after a
    /// database first existed has to be spelled out here. Two of these are not entities at all —
    /// single-row settings the UI writes directly with ADO, which the model has never known
    /// about. The third is an ordinary entity that simply arrived later, and needs saying for
    /// exactly the same reason.</para>
    /// </summary>
    private static readonly string[] Tables =
    [
        // The NPC corporation facts the SDE import drops, fetched from ESI when a page is opened.
        // ⚠️ DOUBLE PRECISION for the tax rate: REAL is float4 on PostgreSQL and would round it.
        // Map and station fields the import was leaving in the file.
        """
        ALTER TABLE "SdeConstellations" ADD COLUMN IF NOT EXISTS "WormholeClassId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeRegions" ADD COLUMN IF NOT EXISTS "Description" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeRegions" ADD COLUMN IF NOT EXISTS "NebulaId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeRegions" ADD COLUMN IF NOT EXISTS "WormholeClassId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeSolarSystems" ADD COLUMN IF NOT EXISTS "WormholeClassId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeSolarSystems" ADD COLUMN IF NOT EXISTS "Border" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeSolarSystems" ADD COLUMN IF NOT EXISTS "Corridor" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeSolarSystems" ADD COLUMN IF NOT EXISTS "Fringe" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeSolarSystems" ADD COLUMN IF NOT EXISTS "Hub" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeSolarSystems" ADD COLUMN IF NOT EXISTS "International" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeSolarSystems" ADD COLUMN IF NOT EXISTS "Regional" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeSolarSystems" ADD COLUMN IF NOT EXISTS "Luminosity" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeSolarSystems" ADD COLUMN IF NOT EXISTS "VisualEffect" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeSolarSystems" ADD COLUMN IF NOT EXISTS "StarId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeStationOperations" ADD COLUMN IF NOT EXISTS "ActivityId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeStationOperations" ADD COLUMN IF NOT EXISTS "Description" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeStationOperations" ADD COLUMN IF NOT EXISTS "ManufacturingFactor" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeStationOperations" ADD COLUMN IF NOT EXISTS "ResearchFactor" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeStationOperations" ADD COLUMN IF NOT EXISTS "Ratio" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeStationOperations" ADD COLUMN IF NOT EXISTS "Border" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeStationOperations" ADD COLUMN IF NOT EXISTS "Corridor" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeStationOperations" ADD COLUMN IF NOT EXISTS "Fringe" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeStationOperations" ADD COLUMN IF NOT EXISTS "Hub" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeStations" ADD COLUMN IF NOT EXISTS "CelestialIndex" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeStations" ADD COLUMN IF NOT EXISTS "OrbitId" BIGINT NULL
        """,
        """
        ALTER TABLE "SdeStations" ADD COLUMN IF NOT EXISTS "OrbitIndex" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeStations" ADD COLUMN IF NOT EXISTS "ReprocessingHangarFlag" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeStations" ADD COLUMN IF NOT EXISTS "UseOperationName" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeStations" ADD COLUMN IF NOT EXISTS "X" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeStations" ADD COLUMN IF NOT EXISTS "Y" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeStations" ADD COLUMN IF NOT EXISTS "Z" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,

        // Fields the SDE has always carried that the import did not read.
        // ⚠️ Additive only, and every NOT NULL carries a DEFAULT: an older build inserting
        // without naming these columns has to go on working against the same database.
        """
        ALTER TABLE "SdeCategories" ADD COLUMN IF NOT EXISTS "IconId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeDogmaAttributes" ADD COLUMN IF NOT EXISTS "Description" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeDogmaAttributes" ADD COLUMN IF NOT EXISTS "IconId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeDogmaAttributes" ADD COLUMN IF NOT EXISTS "MinAttributeId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeDogmaAttributes" ADD COLUMN IF NOT EXISTS "MaxAttributeId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeDogmaAttributes" ADD COLUMN IF NOT EXISTS "TooltipTitle" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeDogmaAttributes" ADD COLUMN IF NOT EXISTS "TooltipDescription" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeDogmaAttributes" ADD COLUMN IF NOT EXISTS "DataType" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeDogmaAttributes" ADD COLUMN IF NOT EXISTS "DisplayWhenZero" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeDogmaAttributes" ADD COLUMN IF NOT EXISTS "ChargeRechargeTimeId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeFactions" ADD COLUMN IF NOT EXISTS "IconId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeFactions" ADD COLUMN IF NOT EXISTS "ShortDescription" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeFactions" ADD COLUMN IF NOT EXISTS "SizeFactor" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeFactions" ADD COLUMN IF NOT EXISTS "UniqueName" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeGroups" ADD COLUMN IF NOT EXISTS "IconId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeGroups" ADD COLUMN IF NOT EXISTS "FittableNonSingleton" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeGroups" ADD COLUMN IF NOT EXISTS "UseBasePrice" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeMetaGroups" ADD COLUMN IF NOT EXISTS "Description" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeMetaGroups" ADD COLUMN IF NOT EXISTS "IconId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeMetaGroups" ADD COLUMN IF NOT EXISTS "IconSuffix" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeMetaGroups" ADD COLUMN IF NOT EXISTS "ColorHex" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeRaces" ADD COLUMN IF NOT EXISTS "IconId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeRaces" ADD COLUMN IF NOT EXISTS "ShipTypeId" INTEGER NULL
        """,

        // Where each structure and rig industry bonus actually comes from, which the app has been
        // inferring from rig description text instead.
        """
        CREATE TABLE IF NOT EXISTS "SdeIndustryModifierSources" (
            "TypeId"           INTEGER NOT NULL,
            "Activity"         TEXT    NOT NULL,
            "BonusKind"        TEXT    NOT NULL,
            "DogmaAttributeId" INTEGER NOT NULL,
            "FilterId"         INTEGER NULL,
            PRIMARY KEY ("TypeId", "Activity", "BonusKind", "DogmaAttributeId")
        )
        """,

        // ⚠️ PackagedVolume above all: haul volumes were computed from the ASSEMBLED
        // figure, which is 115,000 against 10,000 for a Vexor.
        """
        ALTER TABLE "SdeTypes" ADD COLUMN IF NOT EXISTS "PackagedVolume" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeTypes" ADD COLUMN IF NOT EXISTS "MetaLevel" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeTypes" ADD COLUMN IF NOT EXISTS "TechLevel" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeTypes" ADD COLUMN IF NOT EXISTS "IsRepackable" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeTypes" ADD COLUMN IF NOT EXISTS "IsDynamicType" BOOLEAN NOT NULL DEFAULT FALSE
        """,
        """
        ALTER TABLE "SdeTypes" ADD COLUMN IF NOT EXISTS "Radius" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeTypes" ADD COLUMN IF NOT EXISTS "VariationParentTypeId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeTypes" ADD COLUMN IF NOT EXISTS "SoundId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeTypes" ADD COLUMN IF NOT EXISTS "ShipTreeGroupId" INTEGER NULL
        """,

        // npcCorporations.yaml has thirty-two fields; the import read three. These are the rest
        // of the ones worth having, and every existing database needs them added by hand.
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "StationId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "SolarSystemId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "Ticker" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "Description" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "CeoId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "TaxRate" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "Size" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "Extent" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "MemberLimit" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "MinSecurity" DOUBLE PRECISION NOT NULL DEFAULT 0
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "MinimumJoinStanding" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "EnemyId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "FriendId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "RaceId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "IconId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "MainActivityId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "SecondaryActivityId" INTEGER NULL
        """,
        """
        ALTER TABLE "SdeNpcCorporations" ADD COLUMN IF NOT EXISTS "Deleted" BOOLEAN NOT NULL DEFAULT FALSE
        """,

        """
        CREATE TABLE IF NOT EXISTS "EsiNpcCorpProfiles" (
            "CorporationId" BIGINT PRIMARY KEY,
            "Ticker"        TEXT             NOT NULL DEFAULT '',
            "Description"   TEXT             NOT NULL DEFAULT '',
            "Url"           TEXT             NOT NULL DEFAULT '',
            "CeoId"         BIGINT           NOT NULL DEFAULT 0,
            "HomeStationId" BIGINT           NOT NULL DEFAULT 0,
            "MemberCount"   INTEGER          NOT NULL DEFAULT 0,
            "TaxRate"       DOUBLE PRECISION NOT NULL DEFAULT 0,
            "FetchedUtc"    TIMESTAMPTZ      NOT NULL DEFAULT NOW()
        )
        """,

        """
        CREATE TABLE IF NOT EXISTS "TradeOpportunitiesSettings" (
            "Id"                     INTEGER NOT NULL PRIMARY KEY,
            "ExcludedMarketGroupIds" TEXT    NOT NULL DEFAULT ''
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS "IndustryOpportunitiesSettings" (
            "Id"                     INTEGER NOT NULL PRIMARY KEY,
            "ExcludedMarketGroupIds" TEXT    NOT NULL DEFAULT ''
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS "BackgroundWorkerStatus" (
            "Id"            INTEGER     NOT NULL PRIMARY KEY,
            "Version"       TEXT        NOT NULL DEFAULT '',
            "HostName"      TEXT        NOT NULL DEFAULT '',
            "ProcessId"     INTEGER     NOT NULL DEFAULT 0,
            "Headless"      BOOLEAN     NOT NULL DEFAULT FALSE,
            "LeaseTakenUtc" TIMESTAMPTZ NOT NULL DEFAULT now(),
            "HeartbeatUtc"  TIMESTAMPTZ NOT NULL DEFAULT now()
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS "WorkerActivity" (
            "Key"        TEXT        NOT NULL PRIMARY KEY,
            "Status"     TEXT        NOT NULL DEFAULT '',
            "Running"    BOOLEAN     NOT NULL DEFAULT FALSE,
            "LastRunUtc" TIMESTAMPTZ NULL,
            "NextRunUtc" TIMESTAMPTZ NULL,
            "Count"      INTEGER     NULL,
            "UpdatedUtc" TIMESTAMPTZ NOT NULL DEFAULT now()
        )
        """,
        // Count arrived after the table did, within this same unreleased branch — so a database
        // that already has the table needs it added rather than created.
        """
        ALTER TABLE "WorkerActivity" ADD COLUMN IF NOT EXISTS "Count" INTEGER NULL
        """,

        // ⚠️ AppErrorLog is built by EnsureCreated from the model, which only builds into an EMPTY
        // database — so every install that already exists needs these two added by hand. They say
        // which client wrote a row, which stopped being obvious the moment several of them began
        // sharing one log.
        //
        // ⚠️ NOT NULL with a default rather than nullable: a client still on an older build inserts
        // without naming these columns at all, and the default is what lets that go on working.
        """
        ALTER TABLE "AppErrorLog" ADD COLUMN IF NOT EXISTS "HostName" TEXT NOT NULL DEFAULT ''
        """,
        """
        ALTER TABLE "AppErrorLog" ADD COLUMN IF NOT EXISTS "Headless" BOOLEAN NOT NULL DEFAULT FALSE
        """,

        // Packaged-only arrived after InvLevelGroups did.
        """
        ALTER TABLE "InvLevelGroups" ADD COLUMN IF NOT EXISTS "PackagedOnly" BOOLEAN NOT NULL DEFAULT FALSE
        """,

        // ── Agent telemetry ──────────────────────────────────────────────────
        //
        // ⚠️ BIGSERIAL, not AUTOINCREMENT: PostgreSQL rejects the SQLite spelling at parse time
        // even under IF NOT EXISTS. BOOLEAN, not INTEGER. And BIGINT for the unit counts —
        // deliberately not REAL, which is float4 here and has silently truncated numbers in this
        // codebase before.
        //
        // The SQLite spelling of these three is in AgentTelemetrySchema; keep the two in step.
        """
        CREATE TABLE IF NOT EXISTS "AgentInteractions" (
            "Id"             BIGSERIAL   PRIMARY KEY,
            "ConversationId" TEXT        NOT NULL DEFAULT '',
            "StartedAt"      TIMESTAMPTZ NOT NULL DEFAULT now(),
            "DurationMs"     INTEGER     NOT NULL DEFAULT 0,
            "Provider"       TEXT        NOT NULL DEFAULT '',
            "Model"          TEXT        NOT NULL DEFAULT '',
            "RoundTrips"     INTEGER     NOT NULL DEFAULT 0,
            "ToolCallCount"  INTEGER     NOT NULL DEFAULT 0,
            "QueryCount"     INTEGER     NOT NULL DEFAULT 0,
            "ToolsUsed"      TEXT        NOT NULL DEFAULT '',
            "StopReason"     TEXT        NOT NULL DEFAULT '',
            "Error"          TEXT        NOT NULL DEFAULT '',
            "UserChars"      INTEGER     NOT NULL DEFAULT 0,
            "ResponseChars"  INTEGER     NOT NULL DEFAULT 0
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS "AgentToolCalls" (
            "Id"            BIGSERIAL   PRIMARY KEY,
            "InteractionId" BIGINT      NOT NULL DEFAULT 0,
            "Sequence"      INTEGER     NOT NULL DEFAULT 0,
            "OccurredAt"    TIMESTAMPTZ NOT NULL DEFAULT now(),
            "ToolName"      TEXT        NOT NULL DEFAULT '',
            "DurationMs"    INTEGER     NOT NULL DEFAULT 0,
            "InputJson"     TEXT        NOT NULL DEFAULT '',
            "ResultChars"   INTEGER     NOT NULL DEFAULT 0,
            "RowCount"      INTEGER     NOT NULL DEFAULT -1,
            "Error"         TEXT        NOT NULL DEFAULT ''
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS "ServiceUsage" (
            "Id"                BIGSERIAL   PRIMARY KEY,
            "OccurredAt"        TIMESTAMPTZ NOT NULL DEFAULT now(),
            "InteractionId"     BIGINT      NULL,
            "Kind"              TEXT        NOT NULL DEFAULT '',
            "Provider"          TEXT        NOT NULL DEFAULT '',
            "Model"             TEXT        NOT NULL DEFAULT '',
            "IsLocal"           BOOLEAN     NOT NULL DEFAULT FALSE,
            "UnitKind"          TEXT        NOT NULL DEFAULT '',
            "InputUnits"        BIGINT      NOT NULL DEFAULT 0,
            "OutputUnits"       BIGINT      NOT NULL DEFAULT 0,
            "CacheReadUnits"    BIGINT      NOT NULL DEFAULT 0,
            "CacheWriteUnits"   BIGINT      NOT NULL DEFAULT 0,
            "UnitsAreEstimated" BOOLEAN     NOT NULL DEFAULT FALSE,
            "DurationMs"        INTEGER     NOT NULL DEFAULT 0,
            "Error"             TEXT        NOT NULL DEFAULT ''
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS "ServiceRates" (
            "Id"                BIGSERIAL      PRIMARY KEY,
            "Kind"              TEXT           NOT NULL DEFAULT '',
            "Provider"          TEXT           NOT NULL DEFAULT '',
            "Model"             TEXT           NOT NULL DEFAULT '',
            "InputPerUnit"      NUMERIC(18,10) NOT NULL DEFAULT 0,
            "OutputPerUnit"     NUMERIC(18,10) NOT NULL DEFAULT 0,
            "CacheReadPerUnit"  NUMERIC(18,10) NOT NULL DEFAULT 0,
            "CacheWritePerUnit" NUMERIC(18,10) NOT NULL DEFAULT 0,
            "Notes"             TEXT           NOT NULL DEFAULT '',
            "UpdatedAt"         TIMESTAMPTZ    NOT NULL DEFAULT now()
        )
        """,
    ];

    /// <summary>
    /// Every index the app creates by hand. Copied verbatim from the SQLite block — the syntax is
    /// identical in both engines — and all of them are listed rather than only the 27 the model
    /// lacks, because <c>IF NOT EXISTS</c> makes the overlap free and a short list would need
    /// re-deriving each time the model changes.
    ///
    /// <para>⚠️ These are not optional decoration. <c>IX_KillMailAttackers_Corp</c> is the
    /// difference between a corporation's Kills tab taking 1.7 seconds and taking over ten
    /// minutes; a Postgres install without it would look broken rather than slow.</para>
    ///
    /// <para>Public so the drift check can read the list rather than parse this file.</para>
    /// </summary>
    public static readonly string[] Indexes =
    [
        """CREATE INDEX IF NOT EXISTS "IX_OrderLabels_Label" ON "OrderLabels" ("Label")""",
        """CREATE INDEX IF NOT EXISTS "IX_SaleLabels_Label" ON "SaleLabels" ("Label")""",
        """CREATE INDEX IF NOT EXISTS "IX_StoreMails_In" ON "StoreMails" ("StoreId", "MailId", "Direction")""",
        """CREATE INDEX IF NOT EXISTS "IX_StoreMails_Store_At" ON "StoreMails" ("StoreId", "At")""",
        """CREATE INDEX IF NOT EXISTS "IX_EsiLpStoreOffers_Type" ON "EsiLpStoreOffers" ("TypeId")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_EsiCorpMemberSessions_Key" ON "EsiCorpMemberSessions" ("CorporationId", "CharacterId", "LogonDate")""",
        """CREATE INDEX IF NOT EXISTS "IX_SdeCelestials_System" ON "SdeCelestials" ("SolarSystemId")""",
        """CREATE INDEX IF NOT EXISTS "IX_MarketRawOrders_TypeId" ON "MarketRawOrders" ("ConfigId", "TypeId", "IsBuyOrder")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_StandingBuyOrders_TypeId_LocationId" ON "StandingBuyOrders" ("TypeId", "LocationId")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorklistMarketAlts_LocationId" ON "WorklistMarketAlts" ("LocationId")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorklistIndyChars_CharacterId" ON "WorklistIndyChars" ("CharacterId")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorklistCorpAlts_CorporationId" ON "WorklistCorpAlts" ("CorporationId")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorklistStationLevels_GroupId_LocationId" ON "WorklistStationLevels" ("GroupId", "LocationId")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorklistIndyScopeStations_LocationId" ON "WorklistIndyScopeStations" ("LocationId")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_GameLogEvents_SourceFile_LineNumber" ON "GameLogEvents" ("SourceFile", "LineNumber")""",
        """CREATE INDEX IF NOT EXISTS "IX_GameLogEvents_OccurredAt" ON "GameLogEvents" ("OccurredAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_GameLogEvents_CharacterId_Kind" ON "GameLogEvents" ("CharacterId", "Kind")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChatMessages_SourceFile_LineNumber" ON "ChatMessages" ("SourceFile", "LineNumber")""",
        """CREATE INDEX IF NOT EXISTS "IX_ChatMessages_OccurredAt" ON "ChatMessages" ("OccurredAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_ChatMessages_ChannelName_OccurredAt" ON "ChatMessages" ("ChannelName", "OccurredAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_KillMailDetails_KillMailTime" ON "KillMailDetails" ("KillMailTime")""",
        """CREATE INDEX IF NOT EXISTS "IX_KillMailAttackers_KillMailId" ON "KillMailAttackers" ("KillMailId")""",
        """CREATE INDEX IF NOT EXISTS "IX_KillMailItems_KillMailId" ON "KillMailItems" ("KillMailId")""",
        """CREATE INDEX IF NOT EXISTS "IX_KillMailAttackers_Corp" ON "KillMailAttackers" ("CorporationId", "KillMailId", "CharacterId")""",
        """CREATE INDEX IF NOT EXISTS "IX_KillMailAttackers_Alliance" ON "KillMailAttackers" ("AllianceId", "KillMailId", "CorporationId")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_StructureFittings_Slot" ON "StructureFittings" ("StructureId","Band","SlotIndex")""",
        """CREATE INDEX IF NOT EXISTS "IX_IndyStructureServices_StructureId" ON "IndyStructureServices" ("StructureId")""",
        """CREATE INDEX IF NOT EXISTS "IX_KillMailAttackers_CharacterId" ON "KillMailAttackers" ("CharacterId", "KillMailId")""",
        """CREATE INDEX IF NOT EXISTS "IX_MapSystemJumps_Bucket" ON "MapSystemJumps" ("Bucket")""",
        """CREATE INDEX IF NOT EXISTS "IX_MapSystemKills_Bucket" ON "MapSystemKills" ("Bucket")""",
        """CREATE INDEX IF NOT EXISTS "IX_MapSystemDailies_Day" ON "MapSystemDailies" ("Day")""",
        """CREATE INDEX IF NOT EXISTS "IX_MapSovereignties_Bucket" ON "MapSovereignties" ("Bucket")""",
        """CREATE INDEX IF NOT EXISTS "IX_MapSovStructures_SystemId" ON "MapSovStructures" ("SystemId")""",
        """CREATE INDEX IF NOT EXISTS "IX_KillMailDetails_SolarSystemId" ON "KillMailDetails" ("SolarSystemId")""",
        """CREATE INDEX IF NOT EXISTS "IX_EsiStructureNames_SolarSystemId" ON "EsiStructureNames" ("SolarSystemId")""",
        """CREATE INDEX IF NOT EXISTS "IX_SdeAgents_Location" ON "SdeAgents" ("LocationId")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_IntelReports_ChatMessageId" ON "IntelReports" ("ChatMessageId")""",
        """CREATE INDEX IF NOT EXISTS "IX_IntelReports_System_Time" ON "IntelReports" ("SystemId", "ReportedAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_IntelReports_Obsolete_Time" ON "IntelReports" ("Obsolete", "ReportedAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_IntelReportCharacters_CharacterId" ON "IntelReportCharacters" ("CharacterId")""",
        """CREATE INDEX IF NOT EXISTS "IX_Alarms_Enabled" ON "Alarms" ("Enabled")""",
        """CREATE INDEX IF NOT EXISTS "IX_AlarmActions_AlarmId" ON "AlarmActions" ("AlarmId")""",
        """CREATE INDEX IF NOT EXISTS "IX_AlarmSeenKeys_Alarm_Seen" ON "AlarmSeenKeys" ("AlarmId", "FirstSeenAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_AlarmEvents_Alarm_Fired" ON "AlarmEvents" ("AlarmId", "FiredAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_AlarmAlerts_Dismissed_Created" ON "AlarmAlerts" ("Dismissed", "CreatedAt")""",

        // Agent telemetry. Same list as AgentTelemetrySchema.Indexes — keep the two in step.
        """CREATE INDEX IF NOT EXISTS "IX_AgentInteractions_StartedAt" ON "AgentInteractions" ("StartedAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_AgentInteractions_Conversation" ON "AgentInteractions" ("ConversationId", "StartedAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_AgentToolCalls_Interaction" ON "AgentToolCalls" ("InteractionId", "Sequence")""",
        """CREATE INDEX IF NOT EXISTS "IX_AgentToolCalls_OccurredAt" ON "AgentToolCalls" ("OccurredAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_ServiceUsage_OccurredAt" ON "ServiceUsage" ("OccurredAt")""",
        """CREATE INDEX IF NOT EXISTS "IX_ServiceUsage_Kind_OccurredAt" ON "ServiceUsage" ("Kind", "OccurredAt")""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ServiceRates_Key" ON "ServiceRates" ("Kind", "Provider", "Model")""",
    ];

    /// <summary>
    /// The rows a new install cannot start without: a market to price against, the settings row
    /// every preferences screen reads, and the two opportunity filters.
    ///
    /// <para>⚠️ Every <c>1</c> and <c>0</c> from the SQLite originals that lands in a bool column
    /// is written <c>true</c>/<c>false</c> here. SQLite stores a bool as INTEGER and accepts
    /// either; PostgreSQL maps it to <c>boolean</c> and rejects the integer outright. Same reason
    /// <c>INSERT OR IGNORE</c> becomes <c>ON CONFLICT DO NOTHING</c> — the SQLite spelling is not
    /// SQL PostgreSQL will parse.</para>
    ///
    /// <para>The <c>WHERE NOT EXISTS</c> forms are left exactly as they are: they are already
    /// portable, and they mean "seed only an empty table", which is not the same thing as
    /// per-row conflict handling and must not be rewritten into it.</para>
    ///
    /// <para>⚠️ <c>NULL::bigint</c>, not a bare <c>NULL</c>. In an <c>INSERT … SELECT … UNION
    /// ALL</c> PostgreSQL settles the union's column types before it ever looks at the target,
    /// and an untyped NULL settles as <c>text</c> — so the insert fails with "column
    /// StationFilter is of type bigint but expression is of type text". SQLite is dynamically
    /// typed and never had an opinion. This was caught by applying the file to a real server;
    /// no amount of reading it would have shown it.</para>
    /// </summary>
    private static readonly string[] Seeds =
    [
        """
        INSERT INTO "PriceHistoryRegions" ("RegionId", "RegionName")
        SELECT 10000002, 'The Forge' WHERE NOT EXISTS (SELECT 1 FROM "PriceHistoryRegions")
        UNION ALL
        SELECT 10000043, 'Domain'    WHERE NOT EXISTS (SELECT 1 FROM "PriceHistoryRegions")
        """,
        """
        INSERT INTO "MarketPricingConfigs"
            ("Method", "LocationName", "LocationId", "PriceType", "IsEnabled", "SortOrder", "LastStatus", "StationFilter", "UsePercentileFilter", "PercentilePercent")
        SELECT 'Region', 'The Forge', 10000002, 'Midpoint', true, 0, '', NULL::bigint, true, 1.0
        WHERE NOT EXISTS (SELECT 1 FROM "MarketPricingConfigs")
        UNION ALL
        SELECT 'Region', 'Domain',    10000043, 'Midpoint', true, 1, '', NULL::bigint, true, 1.0
        WHERE NOT EXISTS (SELECT 1 FROM "MarketPricingConfigs")
        """,
        """
        INSERT INTO "MarketDefaultSettings"
            ("Id", "AssetValueConfigId", "AssetValuePriceType", "ManufacturingConfigId", "ManufacturingPriceType",
             "MissingPriceMarkupPct", "FilterLowballBuyOrders", "LowballBuyOrderThresholdPct",
             "PurchaseWhenCheaper", "PurchaseThresholdPct")
        SELECT 1,
               (SELECT "Id" FROM "MarketPricingConfigs" WHERE "LocationId" = 10000002 LIMIT 1), 'Sell',
               (SELECT "Id" FROM "MarketPricingConfigs" WHERE "LocationId" = 10000002 LIMIT 1), 'Sell',
               15.0, true, 10.0,
               false, 100.0
        WHERE NOT EXISTS (SELECT 1 FROM "MarketDefaultSettings")
        """,
        """
        INSERT INTO "AlertSettings"
            ("Id", "SkillQueueEmpty", "SkillQueuePaused", "SkillQueueEmptyInDays", "SkillQueueEmptyDays",
             "AssetSafety", "InactiveStandingProjects", "StandingBuyOrdersAttention", "UnriggedIndustryJobs")
        VALUES (1, true, true, true, 30, true, true, true, true)
        ON CONFLICT DO NOTHING
        """,
        """
        INSERT INTO "TradeOpportunitiesSettings" ("Id", "ExcludedMarketGroupIds")
        VALUES (1, '2,1954,1659,1396,150,19')
        ON CONFLICT DO NOTHING
        """,
        """
        INSERT INTO "IndustryOpportunitiesSettings" ("Id", "ExcludedMarketGroupIds")
        VALUES (1, '')
        ON CONFLICT DO NOTHING
        """,
    ];
}
