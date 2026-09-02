using EveConsole.Models;

namespace EveConsole.Services;

// Shared rule for turning a ContractPrice row into a single usable value.
public static class ContractPricing
{
    // The price to use for an item sold on contracts (e.g. a BPC): the lowest currently-active
    // contract price, unless that is more than 50% above the 30-day average of the daily-best
    // price — in which case the (steadier) 30-day average is used instead. Falls back to whichever
    // side is present when the other is missing. Returns null when neither is available.
    public static decimal? EffectivePrice(ContractPrice cp)
    {
        var best = cp.BestPrice;
        var avg  = cp.Avg30Best;
        if (best is null) return avg;
        if (avg  is null) return best;
        return best.Value > 1.5m * avg.Value ? avg : best;
    }

    // Same best-vs-30-day rule for a BPC's per-run price, then the last price seen at any age.
    //
    // ⚠️ Recent prices are still favoured; the fallback applies only when there is nothing
    // recent at all. It matters because every consumer of BPC prices — build costs, the
    // production calculator, killmail valuation, LP valuation, the item browser — reads
    // through this one method, so a null here reads as "free" in all of them at once.
    public static decimal? EffectivePerRun(ContractBpcPrice cp)
    {
        var best = cp.BestPerRun;
        var avg  = cp.Avg30PerRun;
        if (best is null && avg is null) return cp.LastPerRun;
        if (best is null) return avg;
        if (avg  is null) return best;
        return best.Value > 1.5m * avg.Value ? avg : best;
    }

    /// <summary>
    /// True when the only figure available is the fallback: nothing listed now, and nothing in
    /// the last 30 days. The price is still usable — it is just not going to move.
    /// </summary>
    public static bool IsStale(ContractBpcPrice cp) =>
        cp.BestPerRun is null && cp.Avg30PerRun is null && cp.LastPerRun is not null;
}
