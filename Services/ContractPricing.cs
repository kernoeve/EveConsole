using EveCortex.Models;

namespace EveCortex.Services;

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
}
