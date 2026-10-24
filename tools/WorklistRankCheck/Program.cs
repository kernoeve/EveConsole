using EveConsole.Services;
using EveConsole.Services.Worklist;

// ─────────────────────────────────────────────────────────────────────────────
//  Worklist rank check
//
//  Checks the rule the job planner uses to decide which items final products are waiting ON
//  (FinalBottleneck): the blocked final JOBS, what each must have built, and the inputs of those
//  that are at least as short — on two constructed cases shaped like the two that went wrong.
//
//  WHY. Every slot and every unit of a shared material is handed out in ranking order, so a
//  ranking that favours the wrong item does not look wrong on screen — it looks like a full list.
//
//  1. Seals and regulators (2026-10-24). Every capital hull job was blocked for want of
//     Auto-Integrity Preservation Seals, half covered across all stock; Core Temperature
//     Regulators sat at a fraction of their need, feeding Capital Core Temperature Regulators
//     already in stock that no hull job was short of. Credited by coverage, the regulators won
//     every batch of Reinforced Carbon Fiber (500 a regulator, 10 for three seals) and the
//     capitals stayed stopped.
//  2. Tungsten Carbide (2026-10-20). Arks blocked for an empty composite whose part-stocked input,
//     and a broad part-stocked intermediate, outranked it until they were nearly full.
// ─────────────────────────────────────────────────────────────────────────────

// Nothing here reads the database, but it references the app: pinned before anything could ask.
DbEngine.Pin(DbBackend.Sqlite);

var failures = new List<string>();
var checks   = 0;

void Check(string name, bool ok, string detail = "")
{
    checks++;
    if (!ok) failures.Add($"  FAIL  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
}

double FinOf(Dictionary<int, Item> items, List<IReadOnlySet<int>> blocked, Item it) =>
    FinalBottleneck.FinalsHeldUp(it.Type, false, it.Starving, it.Dependents, blocked,
                                 t => items.TryGetValue(t, out var m) ? m.Starving : 0);

// The planner's pick, reduced: one job per pick, the highest finals-held-up first, the emptiest
// to break ties, every key recomputed after each pick. Each job makes a tenth of the need.
List<int> Picks(Dictionary<int, Item> items, List<IReadOnlySet<int>> blocked, int count)
{
    var picks = new List<int>();
    for (var i = 0; i < count; i++)
    {
        var pick = items.Values
            .Where(x => x.Starving > 0)
            .OrderByDescending(x => FinOf(items, blocked, x))
            .ThenByDescending(x => x.Starving)
            .ThenBy(x => x.Type)
            .FirstOrDefault();
        if (pick is null) break;
        picks.Add(pick.Type);
        pick.Have = Math.Min(pick.Need, pick.Have + pick.Need / 10);
    }
    return picks;
}

// ── 1. Seals and regulators ───────────────────────────────────────────────────────
//
// Invented type ids. Twelve hull jobs, each blocked for want of seals (owned only where other
// work needs them). Seal ← Fiber. Regulator ← Fiber; CapReg ← Regulator. The hulls use CapReg
// too, but it is in stock at the yard: no hull job is short of it.
{
    const int Seal = 1, Regulator = 2, CapReg = 3, Fiber = 4;
    int[] hulls = Enumerable.Range(101, 12).ToArray();

    Dictionary<int, Item> Chain() => new()
    {
        [Seal]      = new(Seal,      "Seal",      need: 31_494, have: 24_217, dependents: hulls),
        [Regulator] = new(Regulator, "Regulator", need:  3_259, have:     56, dependents: [CapReg, .. hulls]),
        [CapReg]    = new(CapReg,    "CapReg",    need:    170, have:    109, dependents: hulls),
        [Fiber]     = new(Fiber,     "Fiber",     need:  1_000, have:    330, dependents: [Seal, Regulator, CapReg, .. hulls]),
    };
    var items = Chain();
    List<IReadOnlySet<int>> blocked = [.. hulls.Select(_ => (IReadOnlySet<int>)new HashSet<int> { Seal })];
    double Fin(Item it) => FinOf(items, blocked, it);

    Check("seals: the item the hull jobs are short of holds them up",
          Fin(items[Seal]) > 0, $"fin {Fin(items[Seal]):0.00}");
    Check("seals: a part no hull job is short of holds nothing up, however low its stock",
          Fin(items[Regulator]) == 0, $"fin {Fin(items[Regulator]):0.00}, starving {items[Regulator].Starving:0.00}");
    Check("seals: nor does the stocked component it feeds",
          Fin(items[CapReg]) == 0, $"fin {Fin(items[CapReg]):0.00}");
    Check("seals: the shared input, shorter than the seals, holds the same hulls up",
          Fin(items[Fiber]) > 0, $"fin {Fin(items[Fiber]):0.00}");

    var order = Picks(Chain(), blocked, 6);
    var named = string.Join(" ", order.Select(p => Chain()[p].Name));
    Check("seals: picked before the regulators that would eat the fiber",
          order.Contains(Seal) && (!order.Contains(Regulator) || order.IndexOf(Seal) < order.IndexOf(Regulator)), named);

    // The same hull jobs short of the capital regulator as well: then the regulators hold them up.
    List<IReadOnlySet<int>> both = [.. hulls.Select(_ => (IReadOnlySet<int>)new HashSet<int> { Seal, CapReg })];
    Check("seals: once hull jobs are short of the capital regulator, the regulators count them",
          FinOf(items, both, items[Regulator]) > 0, $"fin {FinOf(items, both, items[Regulator]):0.00}");
}

// ── 2. Tungsten Carbide ───────────────────────────────────────────────────────────
//
// Arks A1–A3 blocked for Carbide. O1–O2 blocked for Alloy, O3–O6 for Fiber, O7–O12 for Resin.
// Carbide ← Alloy. Fiber feeds every final but only O3–O6 are short of it.
{
    const int Carbide = 10, Alloy = 11, Fiber = 12, Resin = 13;
    int[] arks   = [101, 102, 103];
    int[] others = Enumerable.Range(201, 12).ToArray();

    Dictionary<int, Item> Chain() => new()
    {
        [Carbide] = new(Carbide, "Carbide", need: 100, have:  0, dependents: arks),
        [Alloy]   = new(Alloy,   "Alloy",   need: 100, have: 23, dependents: [Carbide, .. arks, others[0], others[1]]),
        [Fiber]   = new(Fiber,   "Fiber",   need: 100, have: 30, dependents: [.. arks, .. others]),
        [Resin]   = new(Resin,   "Resin",   need: 100, have:  0, dependents: others[6..]),
    };
    var items = Chain();

    List<IReadOnlySet<int>> blocked = [];
    foreach (var _ in arks)          blocked.Add(new HashSet<int> { Carbide });
    foreach (var _ in others[..2])   blocked.Add(new HashSet<int> { Alloy });
    foreach (var _ in others[2..6])  blocked.Add(new HashSet<int> { Fiber });
    foreach (var _ in others[6..])   blocked.Add(new HashSet<int> { Resin });
    double Fin(Item it) => FinOf(items, blocked, it);

    Check("carbide: holds up the three Arks short of it: 3 × 1.0",
          Math.Abs(Fin(items[Carbide]) - 3) < 1e-9, $"fin {Fin(items[Carbide]):0.00}");
    // Alloy feeds Carbide, but at 0.77 it is less short than Carbide's 1.0: the Arks wait on the
    // carbide, which can start on the alloy on hand. Only O1 and O2 are its own: 2 × 0.77.
    Check("carbide: the stocked input does not take the Arks its empty consumer is stopping",
          Math.Abs(Fin(items[Alloy]) - 2 * 0.77) < 1e-9, $"fin {Fin(items[Alloy]):0.00}");
    Check("carbide: the empty composite outranks its own stocked input",
          Fin(items[Carbide]) > Fin(items[Alloy]), $"carbide {Fin(items[Carbide]):0.00}, alloy {Fin(items[Alloy]):0.00}");
    // Fiber feeds all fifteen, but only O3–O6 are short of it: 4 × 0.70.
    Check("carbide: the broad intermediate counts only the jobs actually short of it",
          Math.Abs(Fin(items[Fiber]) - 4 * 0.70) < 1e-9, $"fin {Fin(items[Fiber]):0.00}");
    Check("carbide: an empty input holds up every job short of it: 6 × 1.0",
          Math.Abs(Fin(items[Resin]) - 6) < 1e-9, $"fin {Fin(items[Resin]):0.00}");

    var order = Picks(Chain(), blocked, 12);
    var named = string.Join(" ", order.Select(p => Chain()[p].Name));
    Check("carbide: with limited slots the empty composite is picked before its stocked input",
          order.Contains(Carbide) && (!order.Contains(Alloy) || order.IndexOf(Carbide) < order.IndexOf(Alloy)), named);
    Check("carbide: the stocked input still gets slots once its consumer has been fed",
          order.Contains(Alloy), named);

    // Once the composite is fuller than its input, the Arks wait on the input too.
    items[Carbide].Have = 40;   // starving 0.60, now less short than Alloy's 0.77
    Check("carbide: an input shorter than its consumer takes the jobs that consumer stops: 5 × 0.77",
          Math.Abs(Fin(items[Alloy]) - 5 * 0.77) < 1e-9, $"fin {Fin(items[Alloy]):0.00}");
}

// ── The rule's edges ──────────────────────────────────────────────────────────────
{
    IReadOnlySet<int> none = new HashSet<int>();
    List<IReadOnlySet<int>> one = [new HashSet<int> { 7 }];
    Check("edges: a final counts itself even with nothing blocked",
          FinalBottleneck.FinalsHeldUp(1, true, 1.0, none, [], _ => 0) == 1);
    Check("edges: nothing starving holds nothing up",
          FinalBottleneck.FinalsHeldUp(7, true, 0.0, none, one, _ => 0) == 0);
    Check("edges: the claim is scaled by how short the item is",
          Math.Abs(FinalBottleneck.FinalsHeldUp(7, false, 0.5, none, one, _ => 0) - 0.5) < 1e-12);
    Check("edges: an item no blocked job needs, directly or through what it feeds, counts nothing",
          FinalBottleneck.FinalsHeldUp(8, false, 1.0, new HashSet<int> { 9 }, one, _ => 1.0) == 0);
    Check("edges: an input as short as the item a job needs, within float noise, counts that job",
          FinalBottleneck.HoldsUp(8, 1.0 - 1e-12, new HashSet<int> { 7 }, new HashSet<int> { 7 }, _ => 1.0));
}

Console.WriteLine($"Worklist rank check: {checks} check(s), {failures.Count} failure(s).");
foreach (var f in failures) Console.WriteLine(f);
return failures.Count == 0 ? 0 : 1;

/// <summary>One item in a constructed chain: how much is needed, how much is had.</summary>
sealed class Item(int type, string name, long need, long have, int[] dependents)
{
    public int               Type       { get; } = type;
    public string            Name       { get; } = name;
    public long              Need       { get; } = need;
    public long              Have       { get; set; } = have;
    public IReadOnlySet<int> Dependents { get; } = dependents.ToHashSet();

    /// <summary>Share of the need still unmet, as the planner measures it.</summary>
    public double Starving => Need <= 0 ? 0 : Math.Clamp(1.0 - (double)Have / Need, 0.0, 1.0);
}
