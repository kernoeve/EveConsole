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
    public static void Apply(AppDbContext db, IProgress<(double Pct, string Status)>? progress = null)
    {
        progress?.Report((30, "Preparing database…"));
        foreach (var sql in Tables)  db.Database.ExecuteSqlRaw(sql);

        progress?.Report((55, "Building indexes…"));
        foreach (var sql in Indexes) db.Database.ExecuteSqlRaw(sql);

        progress?.Report((75, "Writing defaults…"));
        foreach (var sql in Seeds)   db.Database.ExecuteSqlRaw(sql);
    }

    /// <summary>
    /// The only two tables that are not entities. Both are single-row settings the UI writes
    /// directly with ADO, never through EF, which is why the model has never known about them.
    /// </summary>
    private static readonly string[] Tables =
    [
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
        SELECT 'Region', 'The Forge', 10000002, 'Midpoint', true, 0, '', NULL, true, 1.0
        WHERE NOT EXISTS (SELECT 1 FROM "MarketPricingConfigs")
        UNION ALL
        SELECT 'Region', 'Domain',    10000043, 'Midpoint', true, 1, '', NULL, true, 1.0
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
