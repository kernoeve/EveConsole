using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>How a unit price is read off the order book.</summary>
public enum AppraisalPriceMode
{
    /// <summary>The best order now: the lowest sell, the highest buy. What one unit costs or
    /// fetches this minute.</summary>
    Immediate,

    /// <summary>The volume-weighted average of the best orders that together hold the config's
    /// percentile of the side's volume — the price a whole stack would move at, less swayed by
    /// one small order at a silly price.</summary>
    Percentile,
}

/// <summary>One appraised line.</summary>
public sealed record AppraisalRow(
    string Name,
    int    TypeId,
    long   Quantity,
    double UnitVolume,
    double UnitBuy,
    double UnitSell,
    string Problem)
{
    public bool   Priced     => TypeId > 0 && (UnitBuy > 0 || UnitSell > 0);
    public double TotalVolume => UnitVolume * Quantity;
    public double TotalBuy    => UnitBuy * Quantity;
    public double TotalSell   => UnitSell * Quantity;
    public double TotalSplit  => (UnitBuy + UnitSell) / 2 * Quantity;
}

/// <summary>The whole appraisal: the rows, the lines that could not be read, and the totals.</summary>
public sealed record Appraisal(
    IReadOnlyList<AppraisalRow> Rows,
    IReadOnlyList<string>       Unparsed,
    string                      MarketName,
    DateTimeOffset?             PricesAsOf,
    AppraisalPriceMode          Mode,
    double                      PercentilePercent)
{
    public double TotalBuy    => Rows.Sum(r => r.TotalBuy);
    public double TotalSell   => Rows.Sum(r => r.TotalSell);
    public double TotalSplit  => Rows.Sum(r => r.TotalSplit);
    public double TotalVolume => Rows.Sum(r => r.TotalVolume);
    public long   TotalUnits  => Rows.Sum(r => r.Quantity);
    public int    Unpriced    => Rows.Count(r => !r.Priced);
}

/// <summary>
/// Prices a pasted list of items off one of the app's market sources, the way an appraisal site
/// does: each line resolved to a type, priced buy and sell, and totalled.
///
/// <para>Prices come from the order book the market source last fetched (the raw orders it keeps),
/// filtered to the source's station when it has one. Buy orders count when they were placed at
/// that station, or anywhere in its system with a range wide enough to reach it, or region-wide;
/// a buy order some jumps away that happens to reach the station is not counted, which is the one
/// simplification against "everything fillable here". A source without an order book — a
/// Fuzzwork feed, say — falls back to the prices it does hold.</para>
/// </summary>
public sealed class AppraisalService(IDbContextFactory<AppDbContext> dbFactory)
{
    private Dictionary<string, SdeType>? _byName;
    private Dictionary<int, SdeType>?    _byId;

    /// <summary>Every enabled market source, for the picker.</summary>
    public async Task<List<MarketPricingConfig>> SourcesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MarketPricingConfigs.AsNoTracking()
            .Where(c => c.IsEnabled)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .ToListAsync(ct);
    }

    /// <summary>The source the app values assets with, as the picker's first choice.</summary>
    public async Task<int?> DefaultSourceIdAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var defaults = await db.MarketDefaultSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return defaults?.AssetValueConfigId;
    }

    public async Task<Appraisal> AppraiseAsync(string text, int configId, AppraisalPriceMode mode, CancellationToken ct = default)
    {
        await EnsureTypesAsync(ct);

        // ── Names to types ──
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
                // Read, but not a name the SDE knows: kept in the table so the count is honest.
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

        // ── Prices ──
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var config = await db.MarketPricingConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == configId, ct)
                     ?? throw new InvalidOperationException("That market source no longer exists.");
        var prices = await PriceAsync(db, config, order, mode, ct);

        var rows = new List<AppraisalRow>(order.Count + unknown.Count);
        foreach (var typeId in order)
        {
            var type = _byId![typeId];
            var (buy, sell) = prices.TryGetValue(typeId, out var p) ? p : (0, 0);
            var volume = type.PackagedVolume > 0 ? type.PackagedVolume : type.Volume;
            rows.Add(new AppraisalRow(type.Name, typeId, quantities[typeId], volume, buy, sell,
                buy <= 0 && sell <= 0 ? "no orders at this market" : ""));
        }
        foreach (var (name, qty) in unknown)
            rows.Add(new AppraisalRow(name, 0, qty, 0, 0, 0, "not an item name"));

        return new Appraisal(rows, unparsed, config.LocationName, config.LastRefreshed, mode, config.PercentilePercent);
    }

    /// <summary>Unit buy and sell for each type off the source's order book, or its price table
    /// when it keeps no book.</summary>
    private static async Task<Dictionary<int, (double Buy, double Sell)>> PriceAsync(
        AppDbContext db, MarketPricingConfig config, List<int> typeIds, AppraisalPriceMode mode, CancellationToken ct)
    {
        var result = new Dictionary<int, (double, double)>();
        if (typeIds.Count == 0) return result;

        var orders = await db.MarketRawOrders.AsNoTracking()
            .Where(o => o.ConfigId == config.Id && typeIds.Contains(o.TypeId) && o.VolumeRemain > 0)
            .Select(o => new { o.TypeId, o.IsBuyOrder, o.Price, o.VolumeRemain, o.LocationId, o.SystemId, o.Range, o.Duration })
            .ToListAsync(ct);

        if (orders.Count > 0)
        {
            // The station's system, for buy orders placed elsewhere in it with a range that reaches.
            int? stationSystem = null;
            if (config.StationFilter is { } station)
                stationSystem = orders.FirstOrDefault(o => o.LocationId == station)?.SystemId;

            var pct = config.UsePercentileFilter ? config.PercentilePercent : 5.0;
            foreach (var group in orders.GroupBy(o => o.TypeId))
            {
                var sells = group.Where(o => !o.IsBuyOrder && (config.StationFilter is null || o.LocationId == config.StationFilter))
                                 .Select(o => (o.Price, (long)o.VolumeRemain)).OrderBy(x => x.Price).ToList();
                // ⚠️ NPC buy orders run for 365 days and sit at a floor price; they are not what a
                // seller would get, so they are left out.
                var buys = group.Where(o => o.IsBuyOrder && o.Duration <= 90 && ReachesStation(o.LocationId, o.SystemId, o.Range, config.StationFilter, stationSystem))
                                .Select(o => (o.Price, (long)o.VolumeRemain)).OrderByDescending(x => x.Price).ToList();
                result[group.Key] = (Best(buys, mode, pct), Best(sells, mode, pct));
            }
        }

        // No book, or a type with no orders: the source's own price table, if it has the type.
        var missing = typeIds.Where(t => !result.TryGetValue(t, out var p) || (p.Item1 <= 0 && p.Item2 <= 0)).ToList();
        if (missing.Count > 0)
        {
            var table = await db.MarketItemPrices.AsNoTracking()
                .Where(p => p.ConfigId == config.Id && missing.Contains(p.TypeId))
                .ToListAsync(ct);
            foreach (var p in table)
                if (p.BuyPrice > 0 || p.SellPrice > 0) result[p.TypeId] = (p.BuyPrice, p.SellPrice);
        }

        return result;
    }

    private static bool ReachesStation(long location, int system, string range, long? station, int? stationSystem)
    {
        if (station is null) return true;                 // a regional source: every order counts
        if (location == station) return true;             // placed here
        if (range == "region") return true;               // reaches the whole region
        if (stationSystem is { } s && system == s)        // same system, any range but "station"
            return range != "station";
        return false;                                     // some jumps away: not counted (see the class summary)
    }

    /// <summary>The best price of a side, or the volume-weighted average of the best orders that
    /// hold the given percentage of the side's volume.</summary>
    private static double Best(List<(double Price, long Volume)> sorted, AppraisalPriceMode mode, double percent)
    {
        if (sorted.Count == 0) return 0;
        if (mode == AppraisalPriceMode.Immediate) return sorted[0].Price;

        var total = sorted.Sum(x => x.Volume);
        var want  = Math.Max(1, (long)Math.Ceiling(total * Math.Clamp(percent, 0.1, 100) / 100));
        double weighted = 0; long counted = 0;
        foreach (var (price, volume) in sorted)
        {
            var take = Math.Min(volume, want - counted);
            weighted += price * take;
            counted  += take;
            if (counted >= want) break;
        }
        return counted > 0 ? weighted / counted : sorted[0].Price;
    }

    /// <summary>The type list by name, once. Names are unique in the SDE except for a handful of
    /// unpublished duplicates; the lower type id wins those, which is the published one in practice.</summary>
    private async Task EnsureTypesAsync(CancellationToken ct)
    {
        if (_byName is not null) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var types = await db.SdeTypes.AsNoTracking()
            .Select(t => new SdeType { TypeId = t.TypeId, GroupId = t.GroupId, Name = t.Name, Volume = t.Volume, PackagedVolume = t.PackagedVolume })
            .ToListAsync(ct);
        var byName = new Dictionary<string, SdeType>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in types.OrderBy(t => t.TypeId))
            byName.TryAdd(t.Name, t);
        _byName = byName;
        _byId   = types.ToDictionary(t => t.TypeId);
    }
}
