using System.Text;
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
        """
        Execute a read-only SQL SELECT query against the EVE Console local SQLite database.
        Returns up to 200 rows as a JSON array. Use this to answer any question about
        character data, skills, assets, industry jobs, market orders, wallet history, etc.

        DATABASE SCHEMA (key tables):

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

        KEY JOIN PATTERNS:
        - Skill/item name from TypeId: JOIN SdeTypes ON SdeTypes.TypeId = <table>.SkillId|TypeId → Name
        - Character's main wallet: WHERE OwnerType='character' AND Division=0
        - Active market orders: WHERE IsHistory=0
        - Solar system name from LocationId (approximate for known stations): JOIN SdeStations ON SdeStations.StationId = LocationId, then JOIN SdeSolarSystems ON SdeSolarSystems.SolarSystemId = SdeStations.SolarSystemId

        Always use LIMIT to cap large result sets. Dates are stored as ISO 8601 text.
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

    public QueryDatabaseTool(string connString) => _connString = connString;

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

        try
        {
            await using var conn = new SqliteConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

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
