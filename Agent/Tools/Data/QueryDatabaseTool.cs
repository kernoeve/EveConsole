using System.Text.RegularExpressions;
using System.Text;
using EveConsole.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EveConsole.Agent.Tools.Data;

/// <summary>
/// Generic read-only SQL tool. Gives the agent direct SELECT access to every table.
/// Only SELECT statements are permitted; any other keyword is rejected.
/// </summary>
public sealed class QueryDatabaseTool : IAgentTool
{
    private readonly string _connString;

    public string Name        => "query_database";
    public string Description =>
        $$"""
        Execute a read-only SQL SELECT query against the EVE Console database, which is
        {{AgentSqlDialect.Name}}. Write SQL for that engine.
        Returns up to 200 rows as a JSON array. Use this to answer any question about
        character data, skills, assets, industry jobs, market orders, wallet history, etc.

        ⚠️ WHAT FOLLOWS IS NOT THE SCHEMA. It is notes on about a quarter of the tables — the ones
        worth explaining, because their column names do not say what the values mean. The database
        has far more tables than are listed here, and the full list is in the table index in your
        instructions. If what you need is not below, that does NOT mean the data does not exist:
        find the table in the index and call describe_tables for its columns. Several real
        questions have been answered badly because the answer lived in a table this block never
        mentioned.

        NOTES ON KEY TABLES (partial — describe_tables is authoritative for columns):

        Characters: Id(long PK), Name, CorporationId, TotalSp, UnallocatedSp, SecurityStatus
        Corporations: Id(long PK), Name, Ticker, AuthCharacterId

        EsiWalletBalances: OwnerId, OwnerType('character'|'corporation'), Division(0=main), Balance
        EsiSkills: CharacterId, SkillId, TrainedSkillLevel, ActiveSkillLevel, SkillpointsInSkill
        EsiSkillQueue: CharacterId, QueuePosition, SkillId, FinishedLevel, TrainingStartSp, LevelEndSp, StartDate, FinishDate
        EsiImplants: CharacterId, TypeId
        EsiJumpClones: JumpCloneId, CharacterId, LocationId, LocationType
        EsiJumpCloneImplants: JumpCloneId, TypeId
        EsiCloneStates: CharacterId, LastCloneJumpDate, LastStationChangeDate

        EsiIndustryJobs: OwnerId, OwnerType, JobId, BlueprintTypeId, ProductTypeId,
            ActivityId(1=Manufacturing 3=TE 4=ME 5=Copying 8=Invention),
            Status('active'|'ready'|'delivered'|'cancelled'|'paused'),
            Runs, BlueprintRuns, MaterialEfficiency, TimeEfficiency, StartDate, EndDate

        EsiAssets: OwnerId, OwnerType, ItemId, TypeId, LocationId, LocationFlag, Quantity, IsSingleton
        EsiBlueprints: OwnerId, OwnerType, ItemId, TypeId, Runs(-1=BPO), MaterialEfficiency, TimeEfficiency

        EsiMarketOrders: OwnerId, OwnerType, OrderId, TypeId, LocationId, IsBuyOrder,
            VolumeTotal, VolumeRemain, Price, IsHistory(0=active 1=history), Issued, Escrow
        EsiWalletJournal: OwnerId, OwnerType, EsiId, Date, RefType, Description, Amount, Balance
        EsiWalletTransactions: OwnerId, OwnerType, TransactionId, Date, TypeId, Quantity, UnitPrice, IsBuy, ClientId, LocationId

        EsiContracts: OwnerId, OwnerType, ContractId, Type, Status, DateExpired, DateCompleted, Price, Reward, Collateral, Title, StartLocationId, EndLocationId
        EsiLoyaltyPoints: CharacterId, CorporationId, LoyaltyPoints
        EsiStandings: OwnerId, OwnerType, FromId, FromType, Standing
        EsiMining: CharacterId, Date, SolarSystemId, TypeId, Quantity
        EsiKillMailRefs: OwnerId, OwnerType, KillMailId, KillMailHash
        EsiNotifications: CharacterId, NotificationId, Type, SenderId, SenderType, Timestamp, IsRead, Text
        EsiContacts: OwnerId, OwnerType, ContactId, ContactType, Standing, IsWatched, IsBlocked
        EsiFittings: CharacterId, FittingId, Name, Description, ShipTypeId
        EsiFittingItems: Id, FittingId, CharacterId, TypeId, Quantity, Flag

        EsiCorpMembers: CorporationId, CharacterId, Title (note: character name not stored here — join via Characters)
        EsiCorpDivisions: CorporationId, Division, DivisionType('wallet'|'hangar'), Name

        SdeTypes: TypeId, Name, GroupId, Published, Description, Mass, Volume
        SdeGroups: GroupId, Name, CategoryId, Published
        SdeCategories: CategoryId, Name, Published
        SdeSolarSystems: SolarSystemId, Name, ConstellationId, RegionId, SecurityStatus, SunTypeId
        SdeRegions: RegionId, Name, FactionId
        SdeConstellations: ConstellationId, Name, RegionId
        SdeStations: StationId, Name, SolarSystemId, CorporationId, TypeId
        SdeBlueprints: TypeId, MaxProductionLimit
        SdeBlueprintMaterials: TypeId, Activity, MaterialTypeId, Quantity, IsConsumed
        SdeBlueprintProducts: TypeId, Activity, ProductTypeId, Quantity, Probability
        SdeBlueprintSkills: TypeId, Activity, SkillTypeId, Level

        MarketPricingConfigs: Id, Name, RegionId, UpdatedAt
        MarketItemPrices: ConfigId, TypeId, BuyMax, SellMin, UpdatedAt
        MarketRawOrders: ConfigId, OrderId, TypeId, Price, VolumeRemain, IsBuyOrder, LocationId

        LIVE CHARACTER STATE (polled from ESI while a character is online)
        CharacterStatuses: CharacterId(PK), Online(0/1), LastLogin, LastLogout, LoginCount,
            SolarSystemId, StationId, StructureId, ShipTypeId, ShipItemId,
            ShipName(the player's name for the ship, NOT the hull),
            OnlineCheckedAt, LocationCheckedAt, ShipCheckedAt
            - Current state only, one row per character — there is no history here.
            - Hull name: JOIN SdeTypes ON SdeTypes.TypeId = CharacterStatuses.ShipTypeId
            - In space (undocked) = StationId IS NULL AND StructureId IS NULL.
            - ShipItemId changes when the character boards a different ship.

        GAME LOGS (read from the EVE client's own log files on this PC)
        GameLogEvents: Id, OccurredAt(ISO 8601 UTC text, e.g. '2026-08-04T17:39:40Z'), Kind,
            CharacterId, CharacterName, SourceFile, LineNumber, Amount, SecondaryAmount,
            SourceName, SourceShip, SourceCorp, SourceAlliance,
            TargetName, TargetShip, TargetCorp, TargetAlliance,
            Weapon, Quality, FromSystem, ToSystem, LocationName, RawText
            - The message text column is RawText. There is NO "Message" column: querying one
              silently returns the string 'Message' for every row instead of failing.
            - Kind values in use: combat.damage_dealt, combat.damage_taken, combat.ewar,
              combat.miss_dealt, combat.miss_taken, combat.capsule_destroyed, combat.bounty,
              combat.remote_assist, movement.jumped, movement.undocked, unmatched.
            - movement.jumped fills FromSystem and ToSystem.
            - movement.undocked fills LocationName (the station) and ToSystem, but NOT
              SourceShip — the client does not name the ship on that line. For the ship, join
              CharacterStatuses on CharacterId.
            - "unmatched" is any line the parser did not classify; its text is still in RawText.
            - Only covers characters whose logs are on this PC, and only since log import was
              switched on.

        CHAT LOGS (read from the EVE client's own chat log files on this PC)
        ChatMessages: Id, OccurredAt(ISO 8601 UTC text), ChannelName, ChannelId,
            ListenerCharacterId, ListenerName, SenderName, Message, IsSystemMessage,
            SystemName, SourceFile, LineNumber
            - One row per message per listening character, deduplicated.

        INTEL (parsed from the chat channels marked as intel in Settings → Chat Logs)
        IntelReports: Id, ReportedAt, ChannelName, ReporterName, ReporterCharacterId,
            SystemId, SystemName, PlayerCount, Note, NoVisual, Obsolete, ObsoleteSetOn,
            ChatMessageId, Message(the original posted line)
        IntelReportCharacters: IntelReportId, CharacterId, CharacterName, ShipTypeId, ShipName
            - Obsolete=1 means a later report supersedes this one.

        ALARMS (see the manage_alarms tool rather than writing to these directly)
        Alarms: Id, Name, Enabled, ConditionType, ConditionJson, Repeat, PollSeconds,
            CooldownSeconds, Primed, CreatedBy, CreatedAt, LastCheckedAt, LastFiredAt,
            FireCount, LastError
        AlarmEvents: Id, AlarmId, FiredAt, Summary, DetailJson, MatchCount
        AlarmAlerts: Id, AlarmId, AlarmEventId, CreatedAt, Title, Body, Dismissed, DismissedAt

        KEY JOIN PATTERNS:
        - Skill/item name from TypeId: JOIN SdeTypes ON SdeTypes.TypeId = <table>.SkillId|TypeId → Name
        - Character's main wallet: WHERE OwnerType='character' AND Division=0
        - Active market orders: WHERE {{AgentSqlDialect.IsFalse("\"IsHistory\"")}}
        - Solar system name from LocationId (approximate for known stations): JOIN SdeStations ON SdeStations.StationId = LocationId, then JOIN SdeSolarSystems ON SdeSolarSystems.SolarSystemId = SdeStations.SolarSystemId

        Always use LIMIT to cap large result sets.

        {{AgentSqlDialect.Syntax}}

        {{AgentSqlDialect.Dates}}
        """;

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            sql = new
            {
                type = "string",
                description = "The SELECT query to execute. Only SELECT is allowed. Must be valid SQLite SQL.",
            },
        },
        required = new[] { "sql" },
    };

    private readonly AgentSchema? _schema;

    public QueryDatabaseTool(string connString, AgentSchema? schema = null)
    {
        _connString = connString;
        _schema     = schema;
    }

    /// <summary>
    /// Rejects a query naming a table that does not exist, and says which ones do.
    ///
    /// <para>⚠️ This exists because the failure it catches is silent. On SQLite an unknown
    /// double-quoted identifier is not an error — it is a string literal — so a guessed name comes
    /// back as rows full of the guess, which reads like data. The agent then answers confidently
    /// from nothing. Refusing up front, with the real names attached, turns that into one more
    /// round trip instead of a wrong answer.</para>
    ///
    /// <para>⚠️ Deliberately narrow. It only inspects what follows FROM and JOIN, and only rejects
    /// a name it is sure about — CTEs defined in the same statement are collected first and
    /// allowed. A validator that blocks working SQL would be worse than the fault it prevents, so
    /// anything it cannot classify is let through to the database to judge.</para>
    /// </summary>
    private string? ValidateTables(string sql)
    {
        if (_schema is null) return null;

        // Names introduced by this statement itself: WITH x AS (...), and any alias that follows.
        var cte = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(sql, @"(?:\bWITH\b|,)\s*""?([A-Za-z_][A-Za-z0-9_]*)""?\s+AS\s*\(",
                                          RegexOptions.IgnoreCase))
            cte.Add(m.Groups[1].Value);

        var unknown = new List<string>();

        foreach (Match m in Regex.Matches(sql, @"\b(?:FROM|JOIN)\s+""?([A-Za-z_][A-Za-z0-9_]*)""?",
                                          RegexOptions.IgnoreCase))
        {
            var name = m.Groups[1].Value;
            if (cte.Contains(name) || _schema.Has(name)) continue;
            if (!unknown.Contains(name, StringComparer.OrdinalIgnoreCase)) unknown.Add(name);
        }

        if (unknown.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append("Query not run — no such table: ")
          .Append(string.Join(", ", unknown))
          .Append('.');

        foreach (var name in unknown)
        {
            var near = _schema.Nearest(name, 5);
            if (near.Count > 0)
                sb.Append($" Closest to '{name}': {string.Join(", ", near)}.");
        }

        sb.Append(" Use describe_tables to confirm columns before retrying.");
        return sb.ToString();
    }

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        if (!input.TryGetProperty("sql", out var sqlProp))
            return """{"error":"Missing required parameter 'sql'."}""";

        var sql = sqlProp.GetString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(sql))
            return """{"error":"SQL query is empty."}""";

        // Security: only SELECT statements
        var firstWord = sql.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                           .FirstOrDefault() ?? "";
        if (!firstWord.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            && !firstWord.Equals("WITH", StringComparison.OrdinalIgnoreCase))
        {
            return """{"error":"Only SELECT (or CTEs starting with WITH...SELECT) are permitted."}""";
        }

        // Before the database sees it: a name that does not exist is answered with the ones that
        // do, rather than being allowed to return plausible rubbish.
        if (ValidateTables(sql) is { } complaint)
            return JsonSerializer.Serialize(new { error = complaint });

        try
        {
            await using var conn = AppDb.Connect();
            await conn.OpenAsync(ct);
            await using var cmd = conn.Command(sql);

            await using var rdr = await cmd.ExecuteReaderAsync(ct);

            var columnCount  = rdr.FieldCount;
            var columnNames  = Enumerable.Range(0, columnCount).Select(i => rdr.GetName(i)).ToArray();
            var rows         = new List<Dictionary<string, object?>>();
            const int maxRows = 200;

            while (rows.Count < maxRows && await rdr.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(columnCount);
                for (int i = 0; i < columnCount; i++)
                    row[columnNames[i]] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                rows.Add(row);
            }

            var truncated = rows.Count == maxRows && !rdr.IsClosed && await rdr.ReadAsync(ct);

            var sb = new StringBuilder();
            sb.Append('[');
            for (int r = 0; r < rows.Count; r++)
            {
                if (r > 0) sb.Append(',');
                sb.Append('{');
                int c = 0;
                foreach (var (key, value) in rows[r])
                {
                    if (c++ > 0) sb.Append(',');
                    sb.Append(JsonSerializer.Serialize(key));
                    sb.Append(':');
                    sb.Append(value is null ? "null" : JsonSerializer.Serialize(value));
                }
                sb.Append('}');
            }
            sb.Append(']');

            if (truncated)
                return $"{{\"rows\":{sb},\"truncated\":true,\"note\":\"Result set was capped at {maxRows} rows. Add LIMIT or more specific WHERE clauses.\"}}";

            return rows.Count == 0 ? "[]" : sb.ToString();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
