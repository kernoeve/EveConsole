using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>Which side of the order book a value is read from.</summary>
public enum PriceBasis
{
    /// <summary>The lowest sell order: what a unit costs to buy here, or lists at.</summary>
    Sell,
    /// <summary>The highest buy order: what a unit fetches sold into the book right now.</summary>
    Buy,
    /// <summary>Halfway between the two, or whichever side exists when one is empty.</summary>
    Split,
}

/// <summary>
/// A station or structure with orders in one of the app's order books: where a valuation is
/// priced. ⚠️ A place, not a market source: a source may hold a whole region's orders, and every
/// station in that region with orders is listed on its own, so two stations of the same region
/// can be compared.
/// </summary>
public sealed record MarketStation(long LocationId, string Name, int SystemId, int ConfigId, int Orders)
{
    public override string ToString() => Name;
}

/// <summary>An item of the list being valued: the type it resolved to (0 for a name the SDE does
/// not know), how many, and which part of the result it belongs to when reprocessing.</summary>
public sealed record ValuedItem(int TypeId, string Name, long Quantity, double UnitVolume, string Section, string Problem)
{
    public double TotalVolume => UnitVolume * Quantity;
}

/// <summary>What one item is worth per unit, three ways, at the primary station.</summary>
public sealed record ItemValues(ValuedItem Item, double? MarketUnit, bool MarketFromContract, double? BuildUnit, double? ReprocessUnit);

/// <summary>Unit prices at one station for the types asked about, on the chosen basis.</summary>
public sealed record StationPrices(MarketStation Station, IReadOnlyDictionary<int, double> UnitByType, DateTimeOffset? AsOf);

/// <summary>The whole valuation: every item three ways at the primary station, the same items at
/// each station compared, and what could not be read.</summary>
public sealed record Valuation(
    IReadOnlyList<ItemValues>    Values,
    IReadOnlyList<StationPrices> Stations,
    IReadOnlyList<string>        Unparsed,
    PriceBasis                   Basis,
    bool                         Reprocessed)
{
    public double TotalVolume    => Values.Sum(v => v.Item.TotalVolume);
    public long   TotalUnits     => Values.Sum(v => v.Item.Quantity);
    public double TotalMarket    => Values.Sum(v => (v.MarketUnit    ?? 0) * v.Item.Quantity);
    public double TotalBuild     => Values.Sum(v => (v.BuildUnit     ?? 0) * v.Item.Quantity);
    public double TotalReprocess => Values.Sum(v => (v.ReprocessUnit ?? 0) * v.Item.Quantity);
    public int    Unpriced       => Values.Count(v => v.MarketUnit is null && v.BuildUnit is null && v.ReprocessUnit is null);
}

/// <summary>
/// Values a pasted list of items the way an appraisal site does, at a station of the user's
/// choosing rather than at a market source: each line resolved to a type, priced off the orders
/// at that station, and set beside what it would cost to build and what it would yield
/// reprocessed.
///
/// <para>Sells count at the station itself. Buy orders count when placed at the station, anywhere
/// in its system with a range wide enough to reach it, or region-wide; NPC buy orders (the
/// 365-day ones at a floor price) and buy orders some jumps away that happen to reach the station
/// are left out — the latter the one simplification against "everything fillable here". A type
/// with no orders at the station takes its contract price where the app has one, which is how
/// blueprint copies and the like get a value at all.</para>
///
/// <para>The reprocessed value uses the app's reprocessing yields and prices the materials at the
/// same station on the same basis, so the three columns are comparable. Valuing the list "as
/// reprocessed" turns the items into their materials first, batch by batch, and keeps whatever
/// could not be reprocessed as items.</para>
/// </summary>
public sealed class AppraisalService(IDbContextFactory<AppDbContext> dbFactory)
{
    private Dictionary<string, SdeType>? _byName;
    private Dictionary<int, SdeType>?    _byId;
    private Dictionary<int, int>?        _categoryByGroup;

    private List<MarketStation>? _stations;
    private DateTime _stationsAt;

    // ── Stations ───────────────────────────────────────────────────────────

    /// <summary>Every station and structure with orders in any of the app's order books, busiest
    /// first, so the trade hubs lead. Cached for a while: it is a pass over every order.</summary>
    public async Task<IReadOnlyList<MarketStation>> StationsAsync(CancellationToken ct = default)
    {
        if (_stations is not null && DateTime.UtcNow - _stationsAt < TimeSpan.FromMinutes(10)) return _stations;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var counts = await db.MarketRawOrders.AsNoTracking()
            .GroupBy(o => new { o.LocationId, o.SystemId, o.ConfigId })
            .Select(g => new { g.Key.LocationId, g.Key.SystemId, g.Key.ConfigId, Orders = g.Count() })
            .ToListAsync(ct);

        // A station in two books — a region's and a structure's own — is listed once, under the
        // fuller one.
        var best = counts.GroupBy(c => c.LocationId).Select(g => g.OrderByDescending(c => c.Orders).First()).ToList();
        var stationIds   = best.Where(b => b.LocationId < 100_000_000).Select(b => (int)b.LocationId).ToList();
        var structureIds = best.Where(b => b.LocationId >= 100_000_000).Select(b => b.LocationId).ToList();

        var stationNames = await db.SdeStations.AsNoTracking()
            .Where(s => stationIds.Contains(s.StationId))
            .ToDictionaryAsync(s => (long)s.StationId, s => s.Name, ct);
        var structureNames = await db.EsiStructureNames.AsNoTracking()
            .Where(s => structureIds.Contains(s.StructureId))
            .ToDictionaryAsync(s => s.StructureId, s => s.Name, ct);
        var sourceNames = await db.MarketPricingConfigs.AsNoTracking()
            .Where(c => structureIds.Contains(c.LocationId))
            .ToDictionaryAsync(c => c.LocationId, c => c.LocationName, ct);

        var list = best
            .Select(b => new MarketStation(b.LocationId,
                stationNames.GetValueOrDefault(b.LocationId)
                    ?? structureNames.GetValueOrDefault(b.LocationId)
                    ?? sourceNames.GetValueOrDefault(b.LocationId)
                    ?? $"Structure {b.LocationId}",
                b.SystemId, b.ConfigId, b.Orders))
            .OrderByDescending(s => s.Orders).ThenBy(s => s.Name)
            .ToList();

        _stations   = list;
        _stationsAt = DateTime.UtcNow;
        return list;
    }

    /// <summary>Stations whose name holds the text, busiest first.</summary>
    public async Task<IReadOnlyList<MarketStation>> SearchStationsAsync(string text, int limit = 30, CancellationToken ct = default)
    {
        var all = await StationsAsync(ct);
        var needle = text.Trim();
        if (needle.Length == 0) return all.Take(limit).ToList();
        return all.Where(s => s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList();
    }

    // ── Valuation ──────────────────────────────────────────────────────────

    public async Task<Valuation> ValueAsync(string text, MarketStation primary, IReadOnlyList<MarketStation> compare,
        PriceBasis basis, bool reprocess, CancellationToken ct = default)
    {
        await EnsureTypesAsync(ct);
        var (items, unparsed) = Resolve(text);
        if (reprocess) items = await ReprocessAsync(items, ct);

        var typeIds = items.Where(i => i.TypeId > 0).Select(i => i.TypeId).Distinct().ToList();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // The materials each item reprocesses into are priced at the primary station too, so the
        // reprocessed value sits on the same footing as the market one.
        var materials = await db.SdeTypeMaterials.AsNoTracking()
            .Where(m => typeIds.Contains(m.TypeId))
            .ToListAsync(ct);
        var allTypeIds = typeIds.Union(materials.Select(m => m.MaterialTypeId)).ToList();

        var stations = new List<StationPrices> { await PriceAtAsync(db, primary, allTypeIds, basis, ct) };
        foreach (var station in compare)
            stations.Add(await PriceAtAsync(db, station, typeIds, basis, ct));
        var primaryPrices = stations[0].UnitByType;

        var contracts = (await db.ContractPrices.AsNoTracking()
                .Where(c => typeIds.Contains(c.TypeId))
                .ToListAsync(ct))
            .ToDictionary(c => c.TypeId, c => ContractPricing.EffectivePrice(c));
        var builds = await db.BuildCosts.AsNoTracking()
            .Where(b => typeIds.Contains(b.TypeId) && b.TotalCost > 0)
            .ToDictionaryAsync(b => b.TypeId, b => (double)b.TotalCost, ct);
        var reprocessUnits = ReprocessValues(materials, primaryPrices);

        var values = new List<ItemValues>(items.Count);
        foreach (var item in items)
        {
            double? market = null; var fromContract = false;
            if (item.TypeId > 0)
            {
                if (primaryPrices.TryGetValue(item.TypeId, out var m)) market = m;
                else if (contracts.TryGetValue(item.TypeId, out var c) && c is { } cp && cp > 0) { market = (double)cp; fromContract = true; }
            }
            double? build = builds.TryGetValue(item.TypeId, out var b) ? b : null;
            double? reprocessed = reprocessUnits.TryGetValue(item.TypeId, out var r) ? r : null;
            values.Add(new ItemValues(item, market, fromContract, build, reprocessed));
        }

        return new Valuation(values, stations, unparsed, basis, reprocess);
    }

    /// <summary>The pasted lines as items: names resolved and merged by type, names the SDE does
    /// not know kept flagged so the count stays honest, and the lines nothing could be made of.</summary>
    private (List<ValuedItem> Items, List<string> Unparsed) Resolve(string text)
    {
        var quantities = new Dictionary<int, long>();
        var order      = new List<int>();
        var unknown    = new List<(string Name, long Quantity)>();
        var unparsed   = new List<string>();

        foreach (var candidate in ItemListParser.Parse(text))
        {
            var taken = false;
            foreach (var reading in candidate.Readings)
            {
                // A fit line yields a module and a charge as separate readings that both apply.
                if (reading.Source is "fit module" or "fit charge")
                {
                    if (_byName!.TryGetValue(reading.Name, out var part)) { Add(part.TypeId, reading.Quantity); taken = true; }
                    continue;
                }
                if (taken) break;
                if (_byName!.TryGetValue(reading.Name, out var type)) { Add(type.TypeId, reading.Quantity); taken = true; }
            }
            if (!taken)
            {
                // The reading that split a quantity off comes before the whole line: "Not An
                // Item 12" is more usefully shown as twelve of something unknown than as one of
                // "...12".
                var best = candidate.Readings.Where(r => r.Source is not ("fit module" or "fit charge"))
                    .OrderBy(r => r.Source == "name" ? 1 : 0).FirstOrDefault();
                if (best is not null && best.Name.Length > 0) unknown.Add((best.Name, best.Quantity));
                else unparsed.Add(candidate.Line);
            }
        }

        void Add(int typeId, long qty)
        {
            if (!quantities.ContainsKey(typeId)) { quantities[typeId] = 0; order.Add(typeId); }
            quantities[typeId] += qty;
        }

        var items = order.Select(id => { var t = _byId![id]; return new ValuedItem(id, t.Name, quantities[id], Volume(t), "", ""); }).ToList();
        items.AddRange(unknown.Select(u => new ValuedItem(0, u.Name, u.Quantity, 0, "", "not an item name")));
        return (items, unparsed);
    }

    /// <summary>The list as it comes out of a reprocessing plant: every item's materials, batch by
    /// batch at the app's yields, then whatever could not go in — names not known, items with no
    /// materials, and the short end of a stack that does not fill a whole batch.</summary>
    private async Task<List<ValuedItem>> ReprocessAsync(List<ValuedItem> items, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var typeIds = items.Where(i => i.TypeId > 0).Select(i => i.TypeId).ToList();
        var materials = (await db.SdeTypeMaterials.AsNoTracking()
                .Where(m => typeIds.Contains(m.TypeId))
                .ToListAsync(ct))
            .GroupBy(m => m.TypeId).ToDictionary(g => g.Key, g => g.ToList());

        var output = new Dictionary<int, long>();
        var outputOrder = new List<int>();
        var leftovers = new List<ValuedItem>();
        foreach (var item in items)
        {
            if (item.TypeId == 0 || !materials.TryGetValue(item.TypeId, out var mats))
            {
                leftovers.Add(item with { Section = "Left over", Problem = item.TypeId == 0 ? item.Problem : "cannot be reprocessed" });
                continue;
            }
            var type    = _byId![item.TypeId];
            var portion = Math.Max(1, type.PortionSize);
            var yield   = Yield(type);
            var batches = item.Quantity / portion;
            var short_  = item.Quantity - batches * portion;
            if (batches > 0)
                foreach (var m in mats)
                {
                    var units = (long)Math.Floor(batches * (double)m.Quantity * yield);
                    if (units <= 0) continue;
                    if (!output.ContainsKey(m.MaterialTypeId)) { output[m.MaterialTypeId] = 0; outputOrder.Add(m.MaterialTypeId); }
                    output[m.MaterialTypeId] += units;
                }
            if (short_ > 0)
                leftovers.Add(item with { Quantity = short_, Section = "Left over", Problem = $"{short_:N0} short of a batch of {portion:N0}" });
        }

        var result = outputOrder.Select(id => { var t = _byId![id]; return new ValuedItem(id, t.Name, output[id], Volume(t), "Output", ""); }).ToList();
        result.AddRange(leftovers);
        return result;
    }

    /// <summary>What a unit of each type yields reprocessed, at the given material prices.</summary>
    private Dictionary<int, double> ReprocessValues(List<SdeTypeMaterial> materials, IReadOnlyDictionary<int, double> prices)
    {
        var result = new Dictionary<int, double>();
        foreach (var g in materials.GroupBy(m => m.TypeId))
        {
            var type    = _byId![g.Key];
            var portion = Math.Max(1, type.PortionSize);
            var yield   = Yield(type);
            double perUnit = 0;
            foreach (var m in g)
                if (prices.TryGetValue(m.MaterialTypeId, out var p) && p > 0)
                    perUnit += m.Quantity / (double)portion * p * yield;
            if (perUnit > 0) result[g.Key] = perUnit;
        }
        return result;
    }

    private double Yield(SdeType type) =>
        _categoryByGroup!.GetValueOrDefault(type.GroupId) == ReprocessingValueService.OreIceCategoryId
            ? ReprocessingValueService.OreIceYield
            : ReprocessingValueService.GenItemYield;

    private static double Volume(SdeType t) => t.PackagedVolume > 0 ? t.PackagedVolume : t.Volume;

    // ── Prices at a station ────────────────────────────────────────────────

    /// <summary>Unit prices at one station on the basis asked for, off the book that holds it.</summary>
    private static async Task<StationPrices> PriceAtAsync(AppDbContext db, MarketStation station, List<int> typeIds, PriceBasis basis, CancellationToken ct)
    {
        var unit = new Dictionary<int, double>();
        if (typeIds.Count == 0) return new StationPrices(station, unit, null);

        var orders = await db.MarketRawOrders.AsNoTracking()
            .Where(o => o.ConfigId == station.ConfigId && typeIds.Contains(o.TypeId) && o.VolumeRemain > 0)
            .Select(o => new { o.TypeId, o.IsBuyOrder, o.Price, o.LocationId, o.SystemId, o.Range, o.Duration, o.FetchedAt })
            .ToListAsync(ct);
        DateTimeOffset? asOf = orders.Count > 0 ? orders.Max(o => o.FetchedAt) : null;

        foreach (var group in orders.GroupBy(o => o.TypeId))
        {
            double? sell = group.Where(o => !o.IsBuyOrder && o.LocationId == station.LocationId)
                                .Select(o => (double?)o.Price).Min();
            // ⚠️ NPC buy orders run for 365 days at a floor price; they are not what a seller gets.
            double? buy = group.Where(o => o.IsBuyOrder && o.Duration <= 90 && Reaches(o.LocationId, o.SystemId, o.Range, station))
                               .Select(o => (double?)o.Price).Max();
            double? v = basis switch
            {
                PriceBasis.Sell => sell,
                PriceBasis.Buy  => buy,
                _               => sell is { } s && buy is { } b ? (s + b) / 2 : sell ?? buy,
            };
            if (v is { } price && price > 0) unit[group.Key] = price;
        }
        return new StationPrices(station, unit, asOf);
    }

    private static bool Reaches(long location, int system, string range, MarketStation station)
    {
        if (location == station.LocationId) return true;   // placed here
        if (range == "region") return true;                 // reaches the whole region
        if (system == station.SystemId) return range != "station";   // elsewhere in the system, any wider range
        return false;                                       // some jumps away: not counted (see the class summary)
    }

    // ── Types ──────────────────────────────────────────────────────────────

    /// <summary>The type list by name and by id, once. Names are unique in the SDE except for a
    /// handful of unpublished duplicates; the lower type id wins those, which is the published
    /// one in practice.</summary>
    private async Task EnsureTypesAsync(CancellationToken ct)
    {
        if (_byName is not null) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var types = await db.SdeTypes.AsNoTracking()
            .Select(t => new SdeType { TypeId = t.TypeId, GroupId = t.GroupId, Name = t.Name, Volume = t.Volume, PackagedVolume = t.PackagedVolume, PortionSize = t.PortionSize })
            .ToListAsync(ct);
        var byName = new Dictionary<string, SdeType>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in types.OrderBy(t => t.TypeId))
            byName.TryAdd(t.Name, t);
        _categoryByGroup = await db.SdeGroups.AsNoTracking().ToDictionaryAsync(g => g.GroupId, g => g.CategoryId, ct);
        _byId   = types.ToDictionary(t => t.TypeId);
        _byName = byName;
    }
}
