namespace EveConsole.Services.Fitting;

/// <summary>How a fit's capacitor holds up with every active module cycling.</summary>
public sealed record CapacitorResult(
    double Capacity,
    double RechargeSeconds,
    /// <summary>The recharge rate at its peak, at 25% capacitor: 2.5 × capacity ÷ recharge time.</summary>
    double PeakRecharge,
    /// <summary>Average GJ/s the active modules draw.</summary>
    double Drain,
    /// <summary>Average GJ/s cap boosters put back.</summary>
    double Injection,
    bool Stable,
    /// <summary>When stable, the lowest the capacitor falls once it has settled, as a fraction.</summary>
    double StableFraction,
    /// <summary>When not stable, seconds until the first activation finds too little capacitor.</summary>
    double LastsSeconds);

/// <summary>One thing that draws on (or, negative, adds to) the capacitor each cycle. A cap
/// booster runs <paramref name="Shots"/> cycles, then stops for <paramref name="ReloadSeconds"/> to
/// reload; null for anything that never reloads.</summary>
public sealed record CapacitorDrain(string Name, double Amount, double CycleSeconds, int? Shots = null, double ReloadSeconds = 0)
{
    /// <summary>The time from one activation to the next, on average: a reload spread over the load.</summary>
    public double AverageCycleSeconds => Shots is int n and > 0 && ReloadSeconds > 0 ? CycleSeconds + ReloadSeconds / n : CycleSeconds;
}

/// <summary>
/// The capacitor, simulated activation by activation.
/// </summary>
/// <remarks>
/// <para>Between activations the capacitor recharges along the game's curve,
/// dC/dt = (10·Cmax/τ)·(√(C/Cmax) − C/Cmax), whose solution is
/// √(C/Cmax) = 1 + (√(C₀/Cmax) − 1)·e^(−5t/τ). That is exact, so the simulation only needs to
/// stop where something happens.</para>
///
/// <para>A cap booster stops to reload when its load is spent, as in the game.</para>
///
/// <para>Every module starts at the same moment with the capacitor full — the moment a pilot
/// switches everything on. A module that finds too little capacitor for its next cycle is where
/// the fit "runs out". If that has not happened after <see cref="Horizon"/>, the fit is stable,
/// and the level reported is the lowest the capacitor reaches over the final hour.</para>
/// </remarks>
public static class CapacitorSim
{
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(6);

    public static CapacitorResult Run(double capacity, double rechargeMs, IReadOnlyList<CapacitorDrain> drains)
    {
        var tau  = rechargeMs / 1000;
        var peak = tau > 0 ? 2.5 * capacity / tau : 0;
        var drain  = drains.Where(d => d.CycleSeconds > 0 && d.Amount > 0).Sum(d => d.Amount / d.AverageCycleSeconds);
        var inject = drains.Where(d => d.CycleSeconds > 0 && d.Amount < 0).Sum(d => -d.Amount / d.AverageCycleSeconds);

        var active = drains.Where(d => d.CycleSeconds > 0 && d.Amount != 0).ToList();
        if (active.Count == 0 || capacity <= 0 || tau <= 0)
            return new CapacitorResult(capacity, tau, peak, drain, inject, true, 1, double.PositiveInfinity);

        // Next activation time for each drain; all fire at t = 0.
        var next  = active.Select(_ => 0.0).ToArray();
        var fired = new int[active.Count];
        var cap   = capacity;
        var now   = 0.0;
        var end   = Horizon.TotalSeconds;
        var low   = 1.0;
        var lowFrom = end - 3600;

        while (true)
        {
            // The next moment anything activates.
            var t = next.Min();
            if (t > end) break;

            cap = Recharge(cap, capacity, tau, t - now);
            now = t;

            // Everything due now. Drains before injections, so a booster cycling at the same
            // instant cannot pay for the activation it coincides with.
            for (var pass = 0; pass < 2; pass++)
                for (var i = 0; i < active.Count; i++)
                {
                    if (next[i] > now) continue;
                    var d = active[i];
                    if ((pass == 0) != (d.Amount > 0)) continue;

                    if (d.Amount > 0)
                    {
                        if (cap < d.Amount)
                            return new CapacitorResult(capacity, tau, peak, drain, inject, false, 0, now);
                        cap -= d.Amount;
                    }
                    else cap = Math.Min(capacity, cap - d.Amount);
                    // The last charge of a load: the cycle ends, then the reload.
                    var reload = d.Shots is int n and > 0 && ++fired[i] % n == 0 ? d.ReloadSeconds : 0;
                    next[i] = now + d.CycleSeconds + reload;
                }

            if (now >= lowFrom) low = Math.Min(low, cap / capacity);
        }
        return new CapacitorResult(capacity, tau, peak, drain, inject, true, low, double.PositiveInfinity);
    }

    /// <summary>The capacitor after <paramref name="seconds"/> of recharging from <paramref name="from"/>.</summary>
    public static double Recharge(double from, double capacity, double tau, double seconds)
    {
        if (seconds <= 0) return from;
        var root = 1 + (Math.Sqrt(Math.Max(0, from) / capacity) - 1) * Math.Exp(-5 * seconds / tau);
        return capacity * root * root;
    }
}
