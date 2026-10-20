using EveConsole.Services;
using EveConsole.Services.Worklist;

// ─────────────────────────────────────────────────────────────────────────────
//  Worklist rank check
//
//  Checks the rule the job planner uses to decide what a waiting final product is waiting ON
//  (FinalBottleneck), on a constructed chain shaped like the one that went wrong: an empty
//  composite feeding a few hulls, its part-stocked input, and a broad part-stocked intermediate
//  that feeds those hulls and many more.
//
//  WHY. Every reaction slot is handed out in ranking order, so a ranking that favours stocked
//  items over an empty one does not look wrong on screen — it looks like a full list. With all
//  131 slots open, every racial composite was at zero and none of them had a job: broad
//  intermediates a third stocked counted every hull downstream, including hulls they could not
//  bring one step closer while the composite was missing, and outranked it until they were
//  nearly full. Nothing in the running app shows that as an error.
//
//  What is checked: a waiting final is credited only to its most-starved input; ties share it;
//  a final counts itself; nothing starving counts nothing; and, over a run of picks with limited
//  slots, the empty composite is picked before its own stocked input, and the input takes the
//  hulls back once the composite is fuller than it is.
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

// ── The chain ─────────────────────────────────────────────────────────────────────
//
// Invented type ids. Finals: three hulls the composite feeds (A1–A3) and twelve others (O1–O12).
// Carbide → A1–A3. Its input Alloy feeds Carbide, and through it A1–A3, and two more finals of
// its own (O1, O2). Fiber feeds every final. A Resin, at zero, also feeds O7–O12, so for those
// six it, not Fiber, is what they wait on.
const int Carbide = 10, Alloy = 11, Fiber = 12, Resin = 13;
int[] amarr  = [101, 102, 103];
int[] others = Enumerable.Range(201, 12).ToArray();
var finals   = amarr.Concat(others).ToHashSet();

var items = new Dictionary<int, Item>
{
    [Carbide] = new(Carbide, "Carbide", need: 100, have:  0, dependents: amarr),
    [Alloy]   = new(Alloy,   "Alloy",   need: 100, have: 23, dependents: [Carbide, .. amarr, others[0], others[1]]),
    [Fiber]   = new(Fiber,   "Fiber",   need: 100, have: 30, dependents: [.. amarr, .. others]),
    [Resin]   = new(Resin,   "Resin",   need: 100, have:  0, dependents: others[6..]),
};

// Every final is still waiting; the hulls themselves are not in the queue here.
bool Waiting(int t) => finals.Contains(t);

Dictionary<int, List<Item>> InputsOfFinal()
{
    var map = new Dictionary<int, List<Item>>();
    foreach (var it in items.Values)
        foreach (var t in it.Dependents.Where(finals.Contains))
        {
            if (!map.TryGetValue(t, out var list)) map[t] = list = [];
            list.Add(it);
        }
    return map;
}

var inputsOfFinal = InputsOfFinal();
var binding       = new Dictionary<int, double>();

int Fin(Item it) => FinalBottleneck.FinalsHeldUp(false, it.Starving, it.Dependents, Waiting, binding);

// ── Attribution, one pass ─────────────────────────────────────────────────────────
FinalBottleneck.Binding(inputsOfFinal, i => i.Starving, binding);

Check("binding: a hull's binding is its emptiest input",
      binding[amarr[0]] == 1.0, $"got {binding[amarr[0]]}");
Check("binding: a final with one input binds at that input's starving",
      Math.Abs(binding[others[2]] - items[Fiber].Starving) < 1e-12, $"got {binding[others[2]]}");

// Carbide is what the three hulls wait on: 3 × 1.0.
Check("carbide holds up the three hulls it is missing from", Fin(items[Carbide]) == 3, $"fin {Fin(items[Carbide])}");

// Alloy is upstream of the same hulls but stocked; while Carbide is emptier, those hulls do not
// count for it. It still holds up O1 and O2, where Fiber (0.70) is less short than it (0.77):
// ceil(2 × 0.77) = 2.
Check("the stocked input does not count the hulls its empty consumer is stopping",
      Fin(items[Alloy]) == 2, $"fin {Fin(items[Alloy])}");
Check("the empty composite outranks its own stocked input",
      Fin(items[Carbide]) > Fin(items[Alloy]), $"carbide {Fin(items[Carbide])}, alloy {Fin(items[Alloy])}");

// Fiber feeds all fifteen but is the bottleneck only of O3–O6: not the hulls (Carbide), not
// O1–O2 (Alloy), not O7–O12 (Resin). ceil(4 × 0.70) = 3. Counting every input gave it 15.
Check("the broad intermediate counts only the finals it is actually holding up",
      Fin(items[Fiber]) == 3, $"fin {Fin(items[Fiber])}");

// Resin and nothing else at zero for O7–O12.
Check("an empty input holds up every final it is the emptiest input of",
      Fin(items[Resin]) == 6, $"fin {Fin(items[Resin])}");

// ── Ties, finals, nothing starving ────────────────────────────────────────────────
{
    var tie = new Dictionary<int, double> { [1] = 1.0 };
    Check("a tie is shared: an input as short as the binding one counts the final",
          FinalBottleneck.FinalsHeldUp(false, 1.0, [1], _ => true, tie) == 1);
    Check("a tie within float noise is still a tie",
          FinalBottleneck.FinalsHeldUp(false, 1.0 - 1e-12, [1], _ => true, tie) == 1);
    Check("a less-short input does not count the final",
          FinalBottleneck.FinalsHeldUp(false, 0.5, [1], _ => true, tie) == 0);
    Check("a final counts itself even with nothing above it",
          FinalBottleneck.FinalsHeldUp(true, 1.0, [], _ => true, tie) == 1);
    Check("nothing starving holds nothing up",
          FinalBottleneck.FinalsHeldUp(true, 0.0, [1], _ => true, tie) == 0);
    Check("a dependent that is not a waiting final is not counted",
          FinalBottleneck.FinalsHeldUp(false, 1.0, [1], _ => false, tie) == 0);
    var empty = new Dictionary<int, double>();
    FinalBottleneck.Binding(new Dictionary<int, List<Item>> { [1] = [] }, i => i.Starving, empty);
    Check("a final with no inputs binds at zero", empty[1] == 0);
}

// ── A run of picks with limited slots ─────────────────────────────────────────────
//
// The planner's shape, reduced: one job per pick, the highest finals-held-up first, coverage to
// break ties, every key recomputed after each pick. Each job makes 10 of its item's 100.
{
    foreach (var it in items.Values) it.Reset();

    var picks = new List<int>();
    for (var slot = 0; slot < 12; slot++)
    {
        FinalBottleneck.Binding(inputsOfFinal, i => i.Starving, binding);
        var pick = items.Values
            .Where(i => i.Starving > 0)
            .OrderByDescending(Fin)
            .ThenByDescending(i => i.Starving)
            .ThenBy(i => i.Type)
            .First();
        picks.Add(pick.Type);
        pick.Have += 10;
    }

    var firstCarbide = picks.IndexOf(Carbide);
    var firstAlloy   = picks.IndexOf(Alloy);
    var order        = string.Join(" ", picks.Select(p => items[p].Name));

    // Resin, at zero under six finals, rightly goes first, and Fiber shares those six once Resin
    // has been fed to Fiber's own level — they are then equally short, which is a real tie. What
    // must not happen is what did: the empty composite waiting behind its own stocked input. (That
    // Fiber no longer claims the hulls it cannot help is checked above, pass by pass.)
    Check("with limited slots the empty composite is picked before its stocked input",
          firstCarbide >= 0 && (firstAlloy < 0 || firstCarbide < firstAlloy), order);
    Check("the composite gets slots inside the run at all",
          firstCarbide >= 0, order);
    Check("the stocked input still gets slots once its consumer has been fed",
          firstAlloy > firstCarbide, order);
}

// ── Once the consumer is fuller than its input ────────────────────────────────────
//
// The hulls then wait on the input instead, and its claim follows: the two interleave rather than
// one running to completion while the other is never offered a slot.
{
    foreach (var it in items.Values) it.Reset();
    items[Carbide].Have = 40;   // starving 0.60, now less short than Alloy's 0.77
    FinalBottleneck.Binding(inputsOfFinal, i => i.Starving, binding);

    Check("a consumer fuller than its input stops counting the hulls",
          Fin(items[Carbide]) == 0, $"fin {Fin(items[Carbide])}");
    Check("the input, now the emptiest, takes them: ceil(5 × 0.77) = 4",
          Fin(items[Alloy]) == 4, $"fin {Fin(items[Alloy])}");
}

Console.WriteLine($"Worklist rank check: {checks} check(s), {failures.Count} failure(s).");
foreach (var f in failures) Console.WriteLine(f);
return failures.Count == 0 ? 0 : 1;

/// <summary>One item in the constructed chain: how much is needed, how much is had.</summary>
sealed class Item(int type, string name, long need, long have, int[] dependents)
{
    private readonly long _start = have;

    public int    Type       { get; } = type;
    public string Name       { get; } = name;
    public long   Need       { get; } = need;
    public long   Have       { get; set; } = have;
    public int[]  Dependents { get; } = dependents;

    /// <summary>Share of the need still unmet, as the planner measures it.</summary>
    public double Starving => Need <= 0 ? 0 : Math.Clamp(1.0 - (double)Have / Need, 0.0, 1.0);

    public void Reset() => Have = _start;
}
