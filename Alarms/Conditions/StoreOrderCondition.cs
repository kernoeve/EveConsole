using System.Globalization;
using System.Text;
using System.Text.Json;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Alarms.Conditions;

/// <summary>
/// The EVE Mail store's life, as events: an order placed (with the store, the buyer, the item,
/// the price, and whether it is in stock or has to be built — which is what decides what to do
/// about it), an order newly fillable from stock or newly in build, a contract issued for it,
/// the contract accepted (the order complete), the order canceled. Each kind can be switched
/// off. Orders added by hand in the Order Tracker are not store orders and are not reported.
///
/// <para>The events are the transitions of a row that only has a current state, so each is a
/// key on the order and the state, and the seen-key ledger tells a transition from the same
/// state seen again. A new order is announced once, together with the state it arrived in; the
/// state it arrived in is banked silently at the same time, so the next pass does not announce
/// it again as a change. An event kind that is switched off is banked silently too, so switching
/// it on later does not replay the past.</para>
///
/// <para>Runs on the heels of the fulfilment pass, which the store mail runs the moment it books
/// an order — so "new order" already knows whether the thing is on the shelf.</para>
/// </summary>
public sealed class StoreOrderCondition : IAlarmCondition
{
    /// <summary>
    /// Keys are banked for thirty days; an order older than this is not re-announced as new when
    /// its keys have been pruned, and its state changes are not either. Its contract, acceptance
    /// and cancellation are new keys whenever they happen, and still are.
    /// </summary>
    private static readonly TimeSpan RecentOrder = TimeSpan.FromDays(25);
    private static readonly TimeSpan RecentClose = TimeSpan.FromDays(7);

    public string TypeKey     => "store_order";
    public string DisplayName => "Store order events";

    public string Description =>
        "Fires on what happens to EVE Mail store orders: a new order placed — with the store, the " +
        "buyer, the item, the price, and whether it is in stock or has to be built — an order newly " +
        "fillable from stock or newly in build, a contract issued for it, the contract accepted " +
        "(the order complete), or the order canceled. Tick the kinds you want. Orders added by hand " +
        "in the Order Tracker are not reported. Runs the moment the store books or updates an order.";

    public object ParameterSchema => new
    {
        type = "object",
        properties = new
        {
            new_orders = new { type = "boolean", @default = true, title = "New order placed",
                description = "With the store, the buyer, the item, the price, and its state on arrival." },
            in_stock   = new { type = "boolean", @default = true, title = "Newly fillable from stock",
                description = "An order the fulfilment pass now finds on the shelf — contract it." },
            in_build   = new { type = "boolean", @default = true, title = "Newly in build",
                description = "An industry job now claimed for the order." },
            contracted = new { type = "boolean", @default = true, title = "Contract issued",
                description = "A contract to the buyer carrying the order, awaiting acceptance." },
            accepted   = new { type = "boolean", @default = true, title = "Contract accepted — order complete" },
            canceled   = new { type = "boolean", @default = true, title = "Order canceled",
                description = "By the buyer's mail, a rejected contract, or by hand." },
            store      = new { type = "string", title = "Store",
                description = "Optional: only this store, by name. Blank for every store." },
        },
    };

    public string Describe(JsonElement config)
    {
        var kinds = Kinds(config).Where(k => k.On).Select(k => k.Label).ToList();
        var store = ReadStr(config, "store");
        var sb    = new StringBuilder("Store orders");
        if (!string.IsNullOrWhiteSpace(store)) sb.Append(" at ").Append(store.Trim());
        sb.Append(kinds.Count == 0 ? " — nothing switched on" : " — " + string.Join(", ", kinds));
        return sb.ToString();
    }

    public (string Title, string Body) DefaultText(
        string alarmName, JsonElement config, IReadOnlyList<AlarmMatch> matches)
    {
        var title = matches.Count == 1 && matches[0].Detail is { } d
            ? Str(d, "headline")
            : $"{matches.Count} store order events";
        return (string.IsNullOrEmpty(title) ? alarmName : title, IAlarmCondition.JoinSummaries(matches));
    }

    /// <summary>Said as written; the sentences are the summaries with the ISK read as a number one can hear.</summary>
    public string? Announcement(JsonElement config, IReadOnlyList<AlarmMatch> matches)
    {
        if (matches.Count == 0) return null;
        var lines = matches.Take(4).Select(m => m.Detail is { } d && Str(d, "spoken") is { Length: > 0 } s ? s : m.Summary);
        var text  = string.Join(" ", lines);
        return matches.Count > 4 ? $"{text} And {matches.Count - 4} more." : text;
    }

    public async Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default)
    {
        var kinds     = Kinds(config).ToDictionary(k => k.Key, k => k.On);
        var storeName = ReadStr(config, "store")?.Trim();
        var now       = ctx.Now;

        await using var db = await ctx.DbFactory.CreateDbContextAsync(ct);

        // Store orders only, and only ones young enough that their keys are still on the ledger
        // — or still open, or closed lately. Dates are compared here: a DateTimeOffset in a
        // LINQ Where does not translate on SQLite, and the table is small.
        var orders = (await db.TrackedOrders.AsNoTracking()
                .Where(o => o.StoreId > 0)
                .ToListAsync(ct))
            .Where(o => o.CreatedAt >= now - RecentOrder
                     || o.Status == "pending"
                     || (ClosedOn(o) is { } closed && closed >= now - RecentClose))
            .ToList();
        if (orders.Count == 0) return [];

        var storeIds = orders.Select(o => o.StoreId).Distinct().ToList();
        var stores   = await db.Stores.AsNoTracking()
            .Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        if (!string.IsNullOrEmpty(storeName))
            orders = orders.Where(o => string.Equals(stores.GetValueOrDefault(o.StoreId), storeName, StringComparison.OrdinalIgnoreCase)).ToList();

        var typeIds = orders.Select(o => o.TypeId).Distinct().ToList();
        var types   = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId)).ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var matches = new List<AlarmMatch>();
        foreach (var o in orders.OrderBy(o => o.CreatedAt))
        {
            var item   = types.GetValueOrDefault(o.TypeId) ?? $"type {o.TypeId}";
            var store  = stores.GetValueOrDefault(o.StoreId) ?? $"store {o.StoreId}";
            var reff   = string.IsNullOrWhiteSpace(o.OrderRef) ? $"#{o.Id}" : o.OrderRef;
            var what   = o.Units == 1 ? item : $"{o.Units:N0}× {item}";
            var recent = o.CreatedAt >= now - RecentOrder;
            var newKey = $"order:{o.Id}:new";
            var isNew  = recent && !ctx.Seen.Contains(newKey);

            var detail = new Dictionary<string, object?>
            {
                ["order_id"]    = o.Id,
                ["order_ref"]   = reff,
                ["store"]       = store,
                ["buyer"]       = o.Buyer,
                ["contract_to"] = string.IsNullOrEmpty(o.ContractToName) ? null : o.ContractToName,
                ["item"]        = item,
                ["units"]       = o.Units,
                ["price"]       = o.PurchasePrice,
                ["status"]      = o.Status,
                ["source"]      = o.FulfilmentSource,
                ["contract_id"] = o.LinkedContractId,
                ["created_at"]  = o.CreatedAt,
            };

            // ── The order's arrival, said with the state it arrived in ──
            if (recent)
            {
                if (isNew)
                {
                    var state = o.FulfilmentSource switch
                    {
                        OrderFulfilmentService.SourceStock    => "in stock — contract it",
                        OrderFulfilmentService.SourceJob      => "in build",
                        OrderFulfilmentService.SourceContract => "already contracted",
                        _                                     => "not in stock and not in build — a new build",
                    };
                    var summary = $"New order {reff} at {store}: {what} for {o.Buyer}, {o.PurchasePrice:N0} ISK — {state}";
                    matches.Add(Match(newKey, summary,
                        $"New order at {store}: {what} for {o.Buyer}, {Isk(o.PurchasePrice)}. {Cap(state)}.",
                        $"New order: {what} for {o.Buyer}", detail, silent: !kinds["new_orders"]));
                }

                // The state it has now — announced as a change only when the order is not new
                // this pass; banked silently with a new order, so it is not announced twice.
                var sourceKey = o.FulfilmentSource switch
                {
                    OrderFulfilmentService.SourceStock => ("in_stock", $"order:{o.Id}:source:stock",
                        $"Order {reff} ({what} for {o.Buyer}) can now be filled from stock",
                        $"Order for {what} for {o.Buyer} can now be filled from stock."),
                    OrderFulfilmentService.SourceJob   => ("in_build", $"order:{o.Id}:source:job",
                        $"Order {reff} ({what} for {o.Buyer}) is now in build",
                        $"Order for {what} for {o.Buyer} is now in build."),
                    _ => default,
                };
                if (sourceKey != default)
                    matches.Add(Match(sourceKey.Item2, sourceKey.Item3, sourceKey.Item4,
                        $"Order {reff}: {sourceKey.Item3.Split(") ")[^1]}", detail,
                        silent: isNew || !kinds[sourceKey.Item1]));
            }

            // ── A contract, an acceptance, a cancellation: new keys whenever they happen ──
            if (o.LinkedContractId is { } contractId)
            {
                var accepted = o.Status == "completed";
                if (!accepted)
                    matches.Add(Match($"order:{o.Id}:contract:{contractId}",
                        $"Order {reff} ({what} for {o.Buyer}) contracted — awaiting acceptance",
                        $"Order for {what} for {o.Buyer} is contracted and awaiting acceptance.",
                        $"Order {reff} contracted", detail, silent: isNew || !kinds["contracted"]));
            }

            switch (o.Status)
            {
                case "completed":
                    matches.Add(Match($"order:{o.Id}:completed",
                        $"Order {reff} accepted by {o.Buyer} — {what} delivered, order complete",
                        $"{o.Buyer} accepted the contract for {what}. Order complete.",
                        $"Order {reff} complete", detail, silent: isNew || !kinds["accepted"]));
                    break;
                case "canceled":
                    matches.Add(Match($"order:{o.Id}:canceled",
                        $"Order {reff} ({what} for {o.Buyer}) canceled",
                        $"Order for {what} for {o.Buyer} was canceled.",
                        $"Order {reff} canceled", detail, silent: isNew || !kinds["canceled"]));
                    break;
            }
        }
        return matches;
    }

    private static AlarmMatch Match(
        string key, string summary, string spoken, string headline,
        IReadOnlyDictionary<string, object?> detail, bool silent)
    {
        var d = new Dictionary<string, object?>(detail) { ["spoken"] = spoken, ["headline"] = headline };
        return new AlarmMatch(key, summary) { Detail = d, Silent = silent };
    }

    /// <summary>When a closed order was closed, from its "yyyy-MM-dd"; null while open or unreadable.</summary>
    private static DateTimeOffset? ClosedOn(TrackedOrder o)
        => o.Status is "completed" or "canceled"
        && DateTimeOffset.TryParseExact(o.CompletedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal, out var when)
            ? when : null;

    private static IEnumerable<(string Key, string Label, bool On)> Kinds(JsonElement config) =>
    [
        ("new_orders", "new orders",  ReadBool(config, "new_orders", true)),
        ("in_stock",   "in stock",    ReadBool(config, "in_stock",   true)),
        ("in_build",   "in build",    ReadBool(config, "in_build",   true)),
        ("contracted", "contracted",  ReadBool(config, "contracted", true)),
        ("accepted",   "accepted",    ReadBool(config, "accepted",   true)),
        ("canceled",   "canceled",    ReadBool(config, "canceled",   true)),
    ];

    /// <summary>"12.4 billion ISK": a price one can hear, rather than ten digits read out.</summary>
    internal static string Isk(double isk) => Math.Abs(isk) switch
    {
        >= 1e9 => $"{isk / 1e9:0.##} billion ISK",
        >= 1e6 => $"{isk / 1e6:0.##} million ISK",
        >= 1e3 => $"{isk / 1e3:0.##} thousand ISK",
        _      => $"{isk:N0} ISK",
    };

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Str(IReadOnlyDictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) && v is string s ? s : "";

    private static string? ReadStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>A switch that is on unless it says otherwise: an older config, or the agent's, leaves it on.</summary>
    private static bool ReadBool(JsonElement e, string name, bool fallback) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.True
            : fallback;
}
