namespace EveConsole.Services.Fitting;

/// <summary>
/// An active Reactive Armor Hardener: its resistances as they settle against the damage the fit
/// is assumed to be taking (<see cref="DogmaEngine.DamageProfile"/>).
/// </summary>
/// <remarks>
/// <para>The game rule: the module starts with its own resistances (15% to each type, 60% in all)
/// and at the end of every cycle moves up to <c>resistanceShiftAmount</c> (6%) away from each of
/// the two types the ship took least damage from, sharing what it took equally between the two it
/// took most from. When only one type is hitting, the other three give to it. A type with less
/// than the shift left gives what it has. Damage taken is judged after the ship's resistances, the
/// hardener's own included but not the Damage Control's, so it settles on the ship's weak spots
/// against that damage rather than on the damage itself.</para>
///
/// <para>Repeated cycles end in a short loop of states rather than one; what is reported is the
/// average over the loop. Where two types take exactly the same damage, the earlier of EM,
/// thermal, kinetic, explosive counts as taking more.</para>
///
/// <para>Its resistances multiply the ship's before the percentages of other hardeners, as a
/// Damage Control's do, and share that stacking chain: in game the two penalize each other and
/// neither is penalized by any other hardener.</para>
/// </remarks>
internal sealed class AdaptiveArmor
{
    private static readonly string[] ResonanceNames =
        ["armorEmDamageResonance", "armorThermalDamageResonance", "armorKineticDamageResonance", "armorExplosiveDamageResonance"];

    /// <summary>Enough for any start to reach its loop: at 6% a cycle the 60% moves end to end in ten.</summary>
    private const int MaxCycles = 200;

    private readonly DogmaEngine _engine;
    private readonly DogmaItem   _module;
    private readonly int[]       _attrs;
    private double[]? _trial;     // the module's resonances while a cycle is being tried
    private double[]? _settled;

    private AdaptiveArmor(DogmaEngine engine, DogmaItem module)
    {
        _engine = engine;
        _module = module;
        _attrs  = ResonanceNames.Select(engine.Data.AttrId).ToArray();
    }

    public static AdaptiveArmor Register(DogmaEngine engine, DogmaItem module)
    {
        var a = new AdaptiveArmor(engine, module);
        for (var t = 0; t < 4; t++)
        {
            var type = t;
            engine.AddModification(engine.Ship, a._attrs[type],
                new Modification(module, DogmaEngine.OpPreMul, 0, () => a.Resonance(type)));
        }
        return a;
    }

    /// <summary>The module's resonances once settled — EM, thermal, kinetic, explosive; 1 − resistance.</summary>
    public IReadOnlyList<double> Settled => _settled ??= Settle();

    private double Resonance(int type) => _trial is { } trial ? trial[type] : Settled[type];

    private double[] Settle()
    {
        var p = _engine.DamageProfile;
        double[] incoming = [p.Em, p.Thermal, p.Kinetic, p.Explosive];
        var resists = _attrs.Select(a => 1 - _engine.Value(_module, a)).ToArray();
        if (incoming.All(d => d <= 0)) return resists.Select(r => 1 - r).ToArray();
        var shift = _engine.Value(_module, "resistanceShiftAmount") / 100;

        var seen = new List<double[]>();
        try
        {
            for (var cycle = 0; cycle < MaxCycles; cycle++)
            {
                seen.Add((double[])resists.Clone());
                Shift(resists, Taken(resists, incoming), shift);
                var again = seen.FindIndex(s => Same(s, resists));
                if (again >= 0)
                {
                    var loop = seen.Skip(again).ToList();
                    return Enumerable.Range(0, 4).Select(t => 1 - loop.Average(s => s[t])).ToArray();
                }
            }
            return resists.Select(r => 1 - r).ToArray();
        }
        finally { _trial = null; }
    }

    /// <summary>The damage the ship takes of each type in a cycle with the module at
    /// <paramref name="resists"/>, as the module judges it: through every resistance on the ship
    /// but those that share its stacking chain — the Damage Control's.</summary>
    private double[] Taken(double[] resists, double[] incoming)
    {
        _trial = resists.Select(r => 1 - r).ToArray();
        bool ChainMate(Modification m) => m.Operation == DogmaEngine.OpPreMul && m.Source != _module;
        return Enumerable.Range(0, 4).Select(t => incoming[t] * _engine.Recompute(_engine.Ship, _attrs[t], ChainMate)).ToArray();
    }

    private static void Shift(double[] resists, double[] taken, double shift)
    {
        // Most damaged first; on a tie, the earlier type.
        var order = Enumerable.Range(0, 4)
            .OrderByDescending(t => taken[t], Tolerant.Instance).ThenBy(t => t).ToArray();
        var gainers = taken.Count(d => d > 0) == 1 ? order[..1] : order[..2];
        var givers  = order[gainers.Length..];

        var moved = 0.0;
        foreach (var t in givers)
        {
            var give = Math.Min(shift, resists[t]);
            resists[t] -= give;
            moved      += give;
        }
        foreach (var t in gainers) resists[t] += moved / gainers.Length;
    }

    private static bool Same(double[] a, double[] b)
    {
        for (var i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-9) return false;
        return true;
    }

    /// <summary>Damage figures equal but for rounding count as equal, so a tie falls to type order.</summary>
    private sealed class Tolerant : IComparer<double>
    {
        public static readonly Tolerant Instance = new();
        public int Compare(double x, double y) =>
            Math.Abs(x - y) <= 1e-12 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y))) ? 0 : x.CompareTo(y);
    }
}
