using System.Globalization;
using System.Text;
using System.Text.Json;
using EveConsole.Data;
using EveConsole.Localization;
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

    // The editor's words; the three above are the agent's and stay English.
    public string ScreenName        => AlarmsText.CheckStoreOrder;
    public string ScreenDescription => AlarmsText.CheckStoreOrderNote;

    public AlarmFieldText? ScreenField(string property) => property switch
    {
        "new_orders" => new(AlarmsText.StoreNewOrdersLabel,  AlarmsText.StoreNewOrdersNote),
        "in_stock"   => new(AlarmsText.StoreInStockLabel,    AlarmsText.StoreInStockNote),
        "in_build"   => new(AlarmsText.StoreInBuildLabel,    AlarmsText.StoreInBuildNote),
        "contracted" => new(AlarmsText.StoreContractedLabel, AlarmsText.StoreContractedNote),
        "accepted"   => new(AlarmsText.StoreAcceptedLabel),
        "canceled"   => new(AlarmsText.StoreCanceledLabel,   AlarmsText.StoreCanceledNote),
        "store"      => new(AlarmsText.StoreStoreLabel,      AlarmsText.StoreStoreNote),
        _            => null,
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
        var title = matches.Count == 1 && matches[0].Detail is not null
            ? ScreenWords(matches[0])?.Headline ?? ""
            : Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.StoreEventsOther), matches.Count);
        return (string.IsNullOrEmpty(title) ? alarmName : title, IAlarmCondition.JoinSummaries(matches));
    }

    /// <summary>Said as written; the sentences are the summaries with the ISK read as a number one can hear.</summary>
    public string? Announcement(JsonElement config, IReadOnlyList<AlarmMatch> matches)
    {
        if (matches.Count == 0) return null;
        var lines = matches.Take(4).Select(m => ScreenWords(m)?.Spoken ?? m.Summary);
        var text  = string.Join(" ", lines);
        return matches.Count > 4 ? text + " " + string.Format(AlarmsText.SaidAndMore, matches.Count - 4) : text;
    }

    /// <summary>
    /// One event's headline and spoken sentence, in the interface language; null for a match that
    /// is not one of this check's events.
    ///
    /// <para>⚠️ Rebuilt from the match rather than read from its detail. The detail's "headline"
    /// and "spoken" are English and stay so: the event keeps them and the agent reads them back.
    /// The kind of event is the third part of the key — "order:12:new", "order:12:source:job" —
    /// which cannot change once keys have been banked.</para>
    /// </summary>
    private static (string Headline, string Spoken)? ScreenWords(AlarmMatch m)
    {
        if (m.Detail is not { } d) return null;
        var parts = m.Key.Split(':');
        if (parts.Length < 3 || parts[0] != "order") return null;

        var reff      = Str(d, "order_ref");
        var buyer     = Str(d, "buyer");
        var item      = Str(d, "item");
        var units     = Num(d, "units");
        var what      = units == 1 ? item : $"{units:N0}× {item}";
        var delivered = Num(d, "units_delivered");

        return parts[2] switch
        {
            "new" => (string.Format(AlarmsText.StoreHeadNew, what, buyer),
                      string.Format(Str(d, "source") switch
                      {
                          OrderFulfilmentService.SourceStock    => AlarmsText.StoreSaidNewInStock,
                          OrderFulfilmentService.SourceJob      => AlarmsText.StoreSaidNewInBuild,
                          OrderFulfilmentService.SourceContract => AlarmsText.StoreSaidNewContracted,
                          _                                     => AlarmsText.StoreSaidNewToBuild,
                      }, Str(d, "store"), what, buyer,
                      SpokenIsk(d.TryGetValue("price", out var p) && p is double price ? price : 0))),

            "source" when parts.ElementAtOrDefault(3) == "stock"
                => (string.Format(AlarmsText.StoreHeadInStock, reff), string.Format(AlarmsText.StoreSaidInStock, what, buyer)),
            "source"
                => (string.Format(AlarmsText.StoreHeadInBuild, reff), string.Format(AlarmsText.StoreSaidInBuild, what, buyer)),

            // Part of the order on the contract, or all of it.
            "contract" => (string.Format(AlarmsText.StoreHeadContracted, reff),
                           Num(d, "units_on_contract") is var onContract && onContract > 0 && onContract < units
                               ? string.Format(AlarmsText.StoreSaidContractedPart, what, buyer, onContract, units)
                               : string.Format(AlarmsText.StoreSaidContracted, what, buyer)),

            "delivered" => (string.Format(AlarmsText.StoreHeadDelivered, reff, delivered, units),
                            string.Format(AlarmsText.StoreSaidDelivered, buyer, what, delivered, units)),
            "completed" => (string.Format(AlarmsText.StoreHeadComplete, reff),
                            string.Format(AlarmsText.StoreSaidComplete, buyer, what)),
            "canceled"  => (string.Format(AlarmsText.StoreHeadCanceled, reff),
                            string.Format(AlarmsText.StoreSaidCanceled, what, buyer)),
            _ => null,
        };

        static int Num(IReadOnlyDictionary<string, object?> d, string key)
            => d.TryGetValue(key, out var v) && v is int n ? n : 0;
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
                ["contract_ids"] = OrderContractLinks.Ids(o),
                ["units_delivered"]   = o.Status == "completed" ? o.Units : o.UnitsDelivered,
                ["units_on_contract"] = o.UnitsContracted,
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
            // ⚠️ Only while one is actually waiting on the buyer. An order can hold a contract
            // that is already accepted while the rest of it is still being built, and naming that
            // one as "awaiting acceptance" would announce a delivery that has happened.
            if (OrderContractLinks.AwaitsAcceptance(o) && o.LinkedContractId is { } contractId)
            {
                var part = o.UnitsContracted > 0 && o.UnitsContracted < o.Units
                    ? $"{o.UnitsContracted:N0} of {o.Units:N0} " : "";
                matches.Add(Match($"order:{o.Id}:contract:{contractId}",
                    $"Order {reff} ({what} for {o.Buyer}) {part}contracted — awaiting acceptance",
                    $"Order for {what} for {o.Buyer}: {part}contracted and awaiting acceptance.",
                    $"Order {reff} contracted", detail, silent: isNew || !kinds["contracted"]));
            }

            // Part of it accepted, the order still open for the rest: one announcement for each
            // new count, so the second hull of three is news in its own right.
            if (o.Status == "pending" && o.UnitsDelivered > 0)
                matches.Add(Match($"order:{o.Id}:delivered:{o.UnitsDelivered}",
                    $"Order {reff} ({what} for {o.Buyer}): {o.UnitsDelivered:N0} of {o.Units:N0} delivered",
                    $"{o.Buyer} accepted a contract for part of {what}: {o.UnitsDelivered:N0} of {o.Units:N0} delivered.",
                    $"Order {reff}: {o.UnitsDelivered:N0} of {o.Units:N0} delivered", detail,
                    silent: isNew || !kinds["accepted"]));

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

    /// <summary>"12.4 billion ISK": a price one can hear, rather than ten digits read out.
    /// English, for the event's detail; <see cref="SpokenIsk"/> is what the person hears.</summary>
    internal static string Isk(double isk) => Math.Abs(isk) switch
    {
        >= 1e9 => $"{isk / 1e9:0.##} billion ISK",
        >= 1e6 => $"{isk / 1e6:0.##} million ISK",
        >= 1e3 => $"{isk / 1e3:0.##} thousand ISK",
        _      => $"{isk:N0} ISK",
    };

    /// <summary>The same, in the interface language — counted in 10,000s where the language
    /// counts that way (Languages.CountsInMyriads).</summary>
    private static string SpokenIsk(double isk) => Languages.CountsInMyriads
        ? Math.Abs(isk) switch
        {
            >= 1e8 => string.Format(AlarmsText.IskHundredMillions, isk / 1e8),
            >= 1e4 => string.Format(AlarmsText.IskTenThousands,    isk / 1e4),
            _      => $"{isk:N0} ISK",
        }
        : Math.Abs(isk) switch
        {
            >= 1e9 => string.Format(AlarmsText.IskBillions,  isk / 1e9),
            >= 1e6 => string.Format(AlarmsText.IskMillions,  isk / 1e6),
            >= 1e3 => string.Format(AlarmsText.IskThousands, isk / 1e3),
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
