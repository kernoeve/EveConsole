using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EveConsole.Alarms.Conditions;

/// <summary>
/// Watches for an item being offered at or below a price — on the market, in public contracts,
/// or both.
///
/// <para>Each offer is its own match, keyed on the order or contract id, so an offer that sits
/// there unsold is announced once rather than on every check, and a genuinely new listing is
/// always news.</para>
/// </summary>
public sealed class MarketContractCondition : IAlarmCondition
{
    private const int MaxOffers = 40;

    public string TypeKey     => "market_contract";
    public string DisplayName => "Market / contract price";

    public string Description =>
        "Fires when an item is listed at or below a price you name — on the market, in public " +
        "contracts, or both. Reads the markets configured under Settings → Market and the " +
        "public contracts already being swept, so it sees what those cover and nothing else.";

    /// <summary>
    /// Offers are mutable: one is filled and gone, another appears, a price is revised down.
    /// A listing that leaves the result set and comes back is a real opportunity again, so
    /// keys are not kept forever.
    /// </summary>
    public bool ForgetsUnseenKeys => true;

    public object ParameterSchema => new
    {
        type = "object",
        properties = new
        {
            item = new
            {
                type        = "string",
                format      = "item-name",     // makes the editor offer a type-ahead picker
                description = "Exact item name, e.g. \"Sigil\" or \"Imperial Navy Slicer\". Must match " +
                              "the type name exactly; a name that does not resolve matches nothing.",
            },
            max_unit_price = new
            {
                type        = "number",
                description = "Fire when the price per unit is at or below this, in ISK.",
            },
            min_quantity = new
            {
                type        = "integer",
                description = "Only fire when at least this many are on offer in the one listing. Default 1.",
            },
            source = new
            {
                type        = "string",
                @enum       = new[] { "both", "market", "contracts" },
                description = "Where to look. Default both.",
            },
            market = new
            {
                type        = "string",
                description = "Optional. Restrict the market side to one configured market by name, " +
                              "e.g. \"Jita\". Omit to watch them all.",
            },
            bundled_contracts = new
            {
                type        = "boolean",
                description = "Include contracts holding more than just this item. Off by default, " +
                              "because the asking price then covers the other contents too and the " +
                              "resulting price per unit is not a real one.",
            },
        },
        required = new[] { "item", "max_unit_price" },
    };

    public string Describe(JsonElement config)
    {
        var item = ReadString(config, "item");
        if (string.IsNullOrWhiteSpace(item)) return "Market / contract (not configured)";

        var price = ReadDouble(config, "max_unit_price");
        if (price is null) return $"{item} (no price set)";

        var qty    = ReadInt(config, "min_quantity") ?? 1;
        var source = ReadSource(config);

        var where = source switch
        {
            "market"    => "on the market",
            "contracts" => "in contracts",
            _           => "on market or contracts",
        };

        var amount = qty > 1 ? $"{qty}+ " : "";
        return $"{amount}{item} at or below {price.Value:N0} ISK {where}";
    }

    public (string Title, string Body) DefaultText(
        string alarmName, JsonElement config, IReadOnlyList<AlarmMatch> matches)
    {
        var item = ReadString(config, "item") ?? "Item";

        // The cheapest offer is the reason to look, so it leads.
        var best = matches
            .Select(m => m.Detail is { } d && d.TryGetValue("unit_price", out var p) && p is not null
                ? Convert.ToDouble(p) : double.MaxValue)
            .DefaultIfEmpty(double.MaxValue)
            .Min();

        var title = best < double.MaxValue
            ? $"{item} from {best:N0} ISK"
            : $"{item} available";

        return (title, IAlarmCondition.JoinSummaries(matches));
    }

    public async Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default)
    {
        var item     = ReadString(config, "item");
        var maxPrice = ReadDouble(config, "max_unit_price");
        if (string.IsNullOrWhiteSpace(item) || maxPrice is null) return [];

        var minQty   = Math.Max(1, ReadInt(config, "min_quantity") ?? 1);
        var source   = ReadSource(config);
        var market   = ReadString(config, "market");
        var bundled  = ReadBool(config, "bundled_contracts");

        await using var conn = new SqliteConnection(ctx.ConnectionString);
        await conn.OpenAsync(ct);

        // An unresolvable name matches nothing rather than everything — same rule as the intel
        // check. A typo must go quiet.
        int typeId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """SELECT "TypeId" FROM "SdeTypes" WHERE upper("Name") = upper($n) LIMIT 1""";
            cmd.Parameters.AddWithValue("$n", item.Trim());
            var found = await cmd.ExecuteScalarAsync(ct);
            if (found is null or DBNull) return [];
            typeId = Convert.ToInt32(found);
        }

        var matches = new List<AlarmMatch>();

        if (source is "both" or "market")
            await AddMarketOffersAsync(conn, typeId, item, maxPrice.Value, minQty, market, matches, ct);

        if (source is "both" or "contracts")
            await AddContractOffersAsync(conn, typeId, item, maxPrice.Value, minQty, bundled, matches, ct);

        return matches;
    }

    private static async Task AddMarketOffersAsync(
        SqliteConnection conn, int typeId, string item, double maxPrice, int minQty,
        string? market, List<AlarmMatch> matches, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT o."OrderId", o."Price", o."VolumeRemain",
                   cfg."LocationName", s."Name"
            FROM "MarketRawOrders" o
            JOIN "MarketPricingConfigs" cfg ON cfg."Id" = o."ConfigId"
            LEFT JOIN "SdeSolarSystems" s ON s."SolarSystemId" = o."SystemId"
            WHERE o."TypeId" = $type AND o."IsBuyOrder" = 0
              AND o."Price" <= $price AND o."VolumeRemain" >= $qty
              {(string.IsNullOrWhiteSpace(market) ? "" : """AND upper(cfg."LocationName") LIKE upper($market)""")}
            ORDER BY o."Price"
            LIMIT {MaxOffers}
            """;
        cmd.Parameters.AddWithValue("$type", typeId);
        cmd.Parameters.AddWithValue("$price", maxPrice);
        cmd.Parameters.AddWithValue("$qty", minQty);
        if (!string.IsNullOrWhiteSpace(market))
            cmd.Parameters.AddWithValue("$market", "%" + market.Trim() + "%");

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var orderId = r.GetInt64(0);
            var price   = r.GetDouble(1);
            var volume  = r.GetInt32(2);
            var where   = r.IsDBNull(3) ? "" : r.GetString(3);
            var system  = r.IsDBNull(4) ? null : r.GetString(4);

            var place = system is null ? where : $"{system} ({where})";

            matches.Add(new AlarmMatch(
                $"order:{orderId}",
                $"{item} × {volume:N0} at {price:N2} ISK on the market in {place}")
            {
                Detail = new Dictionary<string, object?>
                {
                    ["source"] = "market",
                    ["order_id"] = orderId,
                    ["unit_price"] = price,
                    ["quantity"] = volume,
                    ["location"] = place,
                },
            });
        }
    }

    private static async Task AddContractOffersAsync(
        SqliteConnection conn, int typeId, string item, double maxPrice, int minQty,
        bool bundled, List<AlarmMatch> matches, CancellationToken ct)
    {
        // Price is stored as text, so it is cast before any comparison or division — string
        // ordering on a number would put 9 above 10.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT c."ContractId", CAST(c."Price" AS REAL) AS total, i."Quantity",
                   c."DateExpired", c."Title", c."RegionId"
            FROM "EsiContracts" c
            JOIN "EsiContractItems" i ON i."ContractId" = c."ContractId"
            WHERE c."OwnerType" = 'public' AND c."Type" = 'item_exchange'
              AND c."Status" = 'outstanding'
              AND i."TypeId" = $type AND i."IsIncluded" = 1
              AND i."Quantity" >= $qty
              AND CAST(c."Price" AS REAL) > 0
              AND CAST(c."Price" AS REAL) / i."Quantity" <= $price
              {(bundled ? "" : """
                AND (SELECT COUNT(DISTINCT x."TypeId") FROM "EsiContractItems" x
                     WHERE x."ContractId" = c."ContractId" AND x."IsIncluded" = 1) = 1
                """)}
            ORDER BY CAST(c."Price" AS REAL) / i."Quantity"
            LIMIT {MaxOffers}
            """;
        cmd.Parameters.AddWithValue("$type", typeId);
        cmd.Parameters.AddWithValue("$price", maxPrice);
        cmd.Parameters.AddWithValue("$qty", minQty);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var contractId = r.GetInt64(0);
            var total      = r.GetDouble(1);
            var quantity   = r.GetInt32(2);
            var expires    = r.IsDBNull(3) ? null : r.GetString(3);
            var title      = r.IsDBNull(4) ? null : r.GetString(4);

            var unit = quantity > 0 ? total / quantity : total;
            var name = string.IsNullOrWhiteSpace(title) ? "" : $" — \"{title}\"";

            matches.Add(new AlarmMatch(
                $"contract:{contractId}",
                $"{item} × {quantity:N0} at {unit:N2} ISK each on a contract " +
                $"({total:N2} ISK total){name}")
            {
                Detail = new Dictionary<string, object?>
                {
                    ["source"] = "contract",
                    ["contract_id"] = contractId,
                    ["unit_price"] = unit,
                    ["total_price"] = total,
                    ["quantity"] = quantity,
                    ["expires"] = expires,
                },
            });
        }
    }

    private static string ReadSource(JsonElement config)
    {
        var s = ReadString(config, "source")?.Trim().ToLowerInvariant();
        return s is "market" or "contracts" ? s : "both";
    }

    private static string? ReadString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? ReadInt(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static double? ReadDouble(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;

        // The editor writes every field as text, so a price arrives as a string; the agent
        // sends a number. Both have to work.
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n)) return n;

        if (p.ValueKind == JsonValueKind.String
            && double.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
            return s;

        return null;
    }

    private static bool ReadBool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.True;
}
