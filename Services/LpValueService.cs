using System.Globalization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.Services;

/// <summary>
/// Works out what a loyalty point is worth, per corporation.
///
/// Per offer: value what it hands you, subtract what it costs beyond the LP, and divide by
/// the LP. Both sides are reduced to a single unit first, because ammunition and charge
/// offers hand over thousands of units for one LP price and comparing a stack against a
/// per-unit price would overstate them by that factor.
///
///     unitValue     = market price of the item, or its contract price when there is no
///                     market price
///     cost          = ISK cost + the market value of every item the offer also consumes
///     costPerUnit   = cost / quantity
///     lpPerUnit     = LP cost / quantity
///     ISK per LP    = (unitValue - costPerUnit) / lpPerUnit
///
/// The corporation's figure is the mean across every offer that could be valued. Offers
/// with no price on either side are skipped rather than counted as zero: an unpriced item
/// is unknown, and folding it in as worthless would drag the average toward nothing on the
/// strength of missing data.
///
/// Arithmetic is double throughout, as a single LP is often worth a four-figure number of
/// ISK but individual offers land in the tens or hundredths.
/// </summary>
public class LpValueService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errors) : ReactiveObject
{
    /// <summary>
    /// ISK per LP for one offer. Null when it cannot be worked out — no price for the
    /// output, no LP cost, or no quantity — which is different from zero and must stay so.
    ///
    /// <paramref name="requiredValue"/> is the total market value of everything the offer
    /// consumes besides ISK, already summed by the caller; both callers resolve prices from
    /// the same lookups but hold them in different shapes.
    /// </summary>
    public static double? IskPerLp(double? unitValue, long iskCost, int quantity, int lpCost,
                                   double requiredValue)
    {
        if (unitValue is null || lpCost <= 0 || quantity <= 0) return null;

        double costPerUnit = (iskCost + requiredValue) / quantity;
        double lpPerUnit   = (double)lpCost / quantity;
        double result      = (unitValue.Value - costPerUnit) / lpPerUnit;

        return double.IsNaN(result) || double.IsInfinity(result) ? null : result;
    }

    /// <summary>
    /// Middle value of the set. Reported alongside the mean because an LP store's offers
    /// are not a well-behaved distribution: a handful demand tens of millions in tags for a
    /// cheap module and score thousands negative, while vanity apparel carries single asks
    /// in the billions and scores hundreds of thousands positive. Either kind drags a mean
    /// somewhere no actual offer sits. The median says what a typical offer is worth.
    /// </summary>
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private string _statusText = "LP values: never calculated";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public async Task RecalculateAsync(CancellationToken ct = default)
    {
        try { await RecalculateCoreAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            errors.Log("LpValueService", "RecalculateAsync", ex);
            StatusText = $"LP values: failed — {ex.Message}";
        }
    }

    private async Task RecalculateCoreAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var offers = await db.EsiLpStoreOffers.AsNoTracking().ToListAsync(ct);
        if (offers.Count == 0)
        {
            StatusText = "LP values: no LP store offers loaded yet";
            return;
        }

        // ── Price lookup ─────────────────────────────────────────────────────
        // The asset-value configuration, the same one type price snapshots use — this asks
        // "what is this item worth", not "what would it cost me to source".
        var settings = await db.MarketDefaultSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        int? configId = settings?.AssetValueConfigId;
        var priceType = settings?.AssetValuePriceType ?? "Midpoint";

        var market = new Dictionary<int, double>();
        if (configId.HasValue)
        {
            var rows = await db.MarketItemPrices.AsNoTracking()
                .Where(p => p.ConfigId == configId.Value)
                .Select(p => new { p.TypeId, p.BuyPrice, p.SellPrice, p.Midpoint })
                .ToListAsync(ct);
            foreach (var p in rows)
            {
                double v = priceType switch { "Buy" => p.BuyPrice, "Sell" => p.SellPrice, _ => p.Midpoint };
                if (v > 0) market[p.TypeId] = v;
            }
        }

        var contract = (await db.ContractPrices.AsNoTracking().ToListAsync(ct))
            .Select(cp => new { cp.TypeId, Price = ContractPricing.EffectivePrice(cp) })
            .Where(x => x.Price is > 0m)
            .ToDictionary(x => x.TypeId, x => (double)x.Price!.Value);

        // Blueprint copies are never on the market — they trade by contract — so their
        // price lives in ContractBpcPrices, per run and per ME, not in ContractPrices.
        // Build costing already falls back to it and this has to as well, or every BPC
        // offer goes unvalued. The lowest ME on record is used: LP store blueprints are
        // unresearched, so a researched copy's price would be the wrong comparison.
        //
        // The per-run price is taken as the price of the copy. ESI does not say how many
        // runs an LP store blueprint carries, and these are single-run in practice; a
        // multi-run copy would be undervalued here rather than overvalued.
        var bpc = (await db.ContractBpcPrices.AsNoTracking().ToListAsync(ct))
            .Select(b => new { b.TypeId, b.Me, Price = ContractPricing.EffectivePerRun(b) })
            .Where(x => x.Price is > 0m)
            .GroupBy(x => x.TypeId)
            .ToDictionary(g => g.Key, g => (double)g.OrderBy(x => x.Me).First().Price!.Value);

        // Market, then contract, then blueprint-copy contract. Null means genuinely
        // unpriced, which is different from zero and has to stay distinguishable.
        double? ValueOf(int typeId) =>
            market.TryGetValue(typeId, out var m) ? m
            : contract.TryGetValue(typeId, out var c) ? c
            : bpc.TryGetValue(typeId, out var b) ? b
            : null;

        var required = (await db.EsiLpStoreOfferItems.AsNoTracking().ToListAsync(ct))
            .GroupBy(i => (i.CorporationId, i.OfferId))
            .ToDictionary(g => g.Key, g => g.ToList());

        // ── Per-offer valuation ──────────────────────────────────────────────
        var perCorp = new Dictionary<int, List<(double IskPerLp, int TypeId)>>();
        var totals  = new Dictionary<int, int>();

        foreach (var o in offers)
        {
            totals[o.CorporationId] = totals.GetValueOrDefault(o.CorporationId) + 1;

            if (o.LpCost <= 0 || o.Quantity <= 0) continue;

            var unitValue = ValueOf(o.TypeId);
            if (unitValue is null) continue;               // nothing to value the output at

            double cost = o.IskCost;
            bool   priced = true;
            if (required.TryGetValue((o.CorporationId, o.OfferId), out var reqs))
                foreach (var r in reqs)
                {
                    var rv = ValueOf(r.TypeId);
                    // An unpriced required item would silently understate the cost and
                    // inflate the offer. Drop the offer instead of guessing at zero.
                    if (rv is null) { priced = false; break; }
                    cost += rv.Value * r.Quantity;
                }
            if (!priced) continue;

            var iskPerLp = IskPerLp(unitValue, o.IskCost, o.Quantity, o.LpCost, cost - o.IskCost);
            if (iskPerLp is null) continue;

            if (!perCorp.TryGetValue(o.CorporationId, out var list))
                perCorp[o.CorporationId] = list = [];
            list.Add((iskPerLp.Value, o.TypeId));
        }

        // ── Persist ──────────────────────────────────────────────────────────
        var now   = DateTimeOffset.UtcNow;
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var rowsOut = perCorp.Select(kv =>
        {
            var best = kv.Value.OrderByDescending(v => v.IskPerLp).First();
            return new LpCorpValue
            {
                CorporationId    = kv.Key,
                IskPerLp         = kv.Value.Average(v => v.IskPerLp),
                MedianIskPerLp   = Median(kv.Value.Select(v => v.IskPerLp)),
                ValuedOffers     = kv.Value.Count,
                TotalOffers      = totals.GetValueOrDefault(kv.Key),
                BestIskPerLp     = best.IskPerLp,
                BestTypeId       = best.TypeId,
                ComputedAt       = now,
            };
        }).ToList();

        // Replaced inside a transaction so the tool never reads a half-empty table — an
        // update should overwrite what is there, not blank it first.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.LpCorpValues.ExecuteDeleteAsync(ct);
            db.LpCorpValues.AddRange(rowsOut);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();

        // Today's snapshot, rewritten on each recalculation so the last run of the day is
        // the one that stands.
        foreach (var r in rowsOut)
        {
            var existing = await db.LpCorpValueSnapshots
                .FirstOrDefaultAsync(s => s.CorporationId == r.CorporationId && s.Date == today, ct);
            if (existing is null)
                db.LpCorpValueSnapshots.Add(new LpCorpValueSnapshot
                {
                    CorporationId = r.CorporationId, Date = today,
                    IskPerLp = r.IskPerLp, MedianIskPerLp = r.MedianIskPerLp,
                    ValuedOffers = r.ValuedOffers, ComputedAt = now,
                });
            else
            {
                existing.IskPerLp       = r.IskPerLp;
                existing.MedianIskPerLp = r.MedianIskPerLp;
                existing.ValuedOffers   = r.ValuedOffers;
                existing.ComputedAt     = now;
            }
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        StatusText = $"LP values: {rowsOut.Count:N0} corporation(s) valued from "
                   + $"{rowsOut.Sum(r => r.ValuedOffers):N0} of {offers.Count:N0} offers "
                   + $"— {DateTimeOffset.Now:t}";
    }
}
