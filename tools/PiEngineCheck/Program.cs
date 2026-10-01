using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Pi;
using EveConsole.Services.Worklist;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// ─────────────────────────────────────────────────────────────────────────────
//  PI engine check
//
//  Runs the Planetary Industry engine on colonies built here, not read from anywhere, and
//  checks what it says against numbers worked out independently: the extractor yield against
//  CCP's published formula (values computed separately from developers.eveonline.com/docs/
//  guides/pi/, not by calling the engine), production chains by hand arithmetic, and the
//  storage, input and tax figures by conservation — what went in, came out and is left must add
//  up.
//
//  WHY. The engine forecasts what nobody can see: ESI describes a colony only as of the last
//  time its owner looked at it, and every "storage full at" and "inputs run out at" after that is
//  simulated. Nothing in the running app would show a wrong forecast as wrong — it would show a
//  confident date. And tax learning cannot be checked on real data at all: the journal entries it
//  learns from did not exist on any account this was written against.
//
//  The colonies: an extractor planet with P1 processors, a factory planet making P2 from imported
//  P1, a full launchpad, an idle processor, an expired extractor; then the per-planet tax rate
//  learned from constructed journal entries, in memory and through a throwaway SQLite database;
//  then the PI worklist tasks — which colony gets which, hauls grouped by system, characters with
//  the PI box cleared left out — on constructed colonies and through the same database.
// ─────────────────────────────────────────────────────────────────────────────

// ⚠️ SQLite, pinned, before anything reads the engine — the database section below must never
// fall through to a developer machine's PostgreSQL config and touch a real server.
DbEngine.Pin(DbBackend.Sqlite);

var failures = new List<string>();
var checks   = 0;

void Check(string name, bool ok, string detail = "")
{
    checks++;
    if (!ok) failures.Add($"  FAIL  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
}

bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) <= tol * Math.Max(1, Math.Abs(b));

// ── Constructed static data ─────────────────────────────────────────────────────────
//
// Type ids are invented and kept clear of real ones; the quantities are the game's own shapes
// (a basic schematic is many raw into a few P1, an advanced one P1 + P1 into P2).
const int CcType = 1, EcuType = 2, BasicType = 3, AdvType = 4, StorageType = 5, PadType = 6;
const int A = 100;            // P0, 0.005 m³
const int X = 200, Y = 201;   // P1, 0.19 m³
const int Z = 300;            // P2, 0.75 m³
const int SBasic = 1001;      // 1000 A → 20 X every 30 min
const int SAdv   = 1002;      // 40 X + 40 Y → 5 Z every hour

var sd = new PiStaticData
{
    PinTypes = new Dictionary<int, PiPinType>
    {
        [CcType]      = new(CcType,      PiPinKind.CommandCenter,        500,    PiProcessorTier.None),
        [EcuType]     = new(EcuType,     PiPinKind.ExtractorControlUnit, 0,      PiProcessorTier.None),
        [BasicType]   = new(BasicType,   PiPinKind.Processor,            0,      PiProcessorTier.Basic),
        [AdvType]     = new(AdvType,     PiPinKind.Processor,            0,      PiProcessorTier.Advanced),
        [StorageType] = new(StorageType, PiPinKind.Storage,              12_000, PiProcessorTier.None),
        [PadType]     = new(PadType,     PiPinKind.Launchpad,            10_000, PiProcessorTier.None),
    },
    Commodities = new Dictionary<int, PiCommodity>
    {
        [A] = new(A, PiTier.P0, 0.005),
        [X] = new(X, PiTier.P1, 0.19),
        [Y] = new(Y, PiTier.P1, 0.19),
        [Z] = new(Z, PiTier.P2, 0.75),
    },
    Schematics = new Dictionary<int, PiSchematic>
    {
        [SBasic] = new(SBasic, "Basic", 1800, [new PiSchematicInput(A, 1000)], X, 20),
        [SAdv]   = new(SAdv, "Advanced", 3600, [new PiSchematicInput(X, 40), new PiSchematicInput(Y, 40)], Z, 5),
    },
};

var t0 = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
static DateTimeOffset H(DateTimeOffset t, double hours) => t.AddHours(hours);

static PiLayoutPin Pin(long id, int type, Dictionary<int, long>? contents = null) =>
    new() { PinId = id, TypeId = type, Contents = contents ?? [] };

// ── 1. The yield formula against CCP's own example ──────────────────────────────────
//
// The page's example: duration 171,000 s (1 d 23 h 30 m), 30-minute cycles, qty_per_cycle
// 6,965 — 95 cycles. Values computed separately from the documented formula (doubles, the
// Kotlin listing's rounding), not by this engine.
{
    long[] firstTen = [24261, 20481, 16053, 13903, 14488, 15656, 14854, 11805, 11569, 11343];
    var cycles = PiYield.TotalCycles(t0, t0.AddSeconds(171_000), 1800);
    Check("yield: 171,000 s of 30-minute cycles is 95 cycles", cycles == 95, $"got {cycles}");
    var values = PiYield.ProgramOutputs(6965, 1800, 95);
    Check("yield: first ten cycles of CCP's example", values.Take(10).SequenceEqual(firstTen),
          string.Join(",", values.Take(10)));
    Check("yield: CCP's example program totals 789,314", values.Sum() == 789_314, $"got {values.Sum()}");
    Check("yield: last cycle of CCP's example is 5,389", values[^1] == 5389, $"got {values[^1]}");
    Check("yield: a missing qty_per_cycle yields nothing", PiYield.CycleOutput(0, 1800, 0) == 0);
}

// ── 2. Extractor planet with a P1 processor ─────────────────────────────────────────
//
// One extractor (500 base, 30-minute cycles, 24 hours from the snapshot) feeding storage; one
// basic processor drawing 1000 raw a cycle from it and sending 20 P1 back.
var extractorPlanet = new PiColonyLayout
{
    CharacterId = 1, PlanetId = 1, PlanetType = "barren", LastUpdate = t0,
    Pins =
    [
        Pin(1, CcType),
        Pin(2, EcuType) with { InstallTime = t0, ExpiryTime = H(t0, 24), Extractor = new PiLayoutExtractor(A, 1800, 500, 4) },
        Pin(3, BasicType) with { SchematicId = SBasic, LastCycleStart = H(t0, -2) },
        Pin(5, StorageType),
    ],
    Routes =
    [
        new PiLayoutRoute(1, 2, 5, A, 500),
        new PiLayoutRoute(2, 5, 3, A, 1000),
        new PiLayoutRoute(3, 3, 5, X, 20),
    ],
};
{
    // Independently: the 48 cycles of a 500-base, 30-minute program by the documented formula.
    long[] firstFive = [1151, 965, 943, 922, 902];
    const long programTotal = 35_601, firstTwenty = 17_541;

    var f = PiEngine.Forecast(extractorPlanet, sd, H(t0, 10), new PiChargeRate(0.10, PiChargeKind.CustomsOffice));
    Check("extractor planet: classified as an extractor planet", f.Kind == PiColonyKind.Extractor, f.Kind.ToString());
    var x = f.Extractors.Single();
    Check("extractor planet: 48 cycles in 24 hours", x.TotalCycles == 48, $"got {x.TotalCycles}");
    Check("extractor planet: per-cycle output by the formula", x.CycleOutputs.Take(5).SequenceEqual(firstFive),
          string.Join(",", x.CycleOutputs.Take(5)));
    Check("extractor planet: program total", x.TotalOutput == programTotal, $"got {x.TotalOutput}");
    Check("extractor planet: 20 cycles done after 10 hours", x.CyclesDone == 20, $"got {x.CyclesDone}");
    Check("extractor planet: output done and remaining", x.OutputDone == firstTwenty && x.RemainingOutput == programTotal - firstTwenty,
          $"{x.OutputDone} / {x.RemainingOutput}");
    Check("extractor planet: not expired at 10 hours", !x.IsExpired);
    Check("extractor planet: expiry exact", f.ExtractorsStopAt == H(t0, 24));
    Check("extractor planet: forecast marked as an estimate, 10 hours old",
          f.IsEstimate && f.DataAge == TimeSpan.FromHours(10));

    // Conservation: everything extracted by now is in storage, in the processor's buffer, or was
    // consumed — 1000 for every cycle finished and for the one running.
    var storage = f.Storage.Single(s => s.PinId == 5);
    var proc    = f.Factories.Single();
    var consumed = 1000L * (proc.CyclesCompleted + (proc.StateAt == PiFactoryState.Running ? 1 : 0));
    var accounted = storage.ContentsAt.GetValueOrDefault(A) + proc.BufferAt.GetValueOrDefault(A) + consumed;
    Check("extractor planet: raw material conserved", accounted == firstTwenty,
          $"storage {storage.ContentsAt.GetValueOrDefault(A)} + buffer {proc.BufferAt.GetValueOrDefault(A)} + consumed {consumed} vs {firstTwenty}");
    Check("extractor planet: P1 in storage is 20 per finished cycle",
          storage.ContentsAt.GetValueOrDefault(X) == 20L * proc.CyclesCompleted && proc.CyclesCompleted > 0,
          $"{storage.ContentsAt.GetValueOrDefault(X)} for {proc.CyclesCompleted} cycles");
    Check("extractor planet: storage does not fill", storage.FullAt is null);
    Check("extractor planet: nothing imported", f.Inputs.Count == 0);

    // Steady flows: the extractor averages 35,601 a day; the processor would use 48,000, so it
    // runs at 35,601 / 48,000 and makes 960 × that much P1.
    var share = programTotal / 48_000.0;
    var flowX = f.Flows.Single(fl => fl.TypeId == X);
    var flowA = f.Flows.Single(fl => fl.TypeId == A);
    Check("extractor planet: raw produced per day", Near(flowA.ProducedPerDay, programTotal), $"{flowA.ProducedPerDay}");
    Check("extractor planet: all raw used on the planet", Near(flowA.ConsumedPerDay, programTotal) && flowA.ExportedPerDay < 1e-6,
          $"{flowA.ConsumedPerDay} / {flowA.ExportedPerDay}");
    Check("extractor planet: P1 exported per day", Near(flowX.ExportedPerDay, 960 * share), $"{flowX.ExportedPerDay} vs {960 * share}");
    var charge = f.Charges.Single(c => c.TypeId == X);
    Check("extractor planet: P1 export charge is 400 × 10 %", Near(charge.ExportPerUnit, 40), $"{charge.ExportPerUnit}");
    Check("extractor planet: P1 export charge per day", Near(charge.ExportPerDay, 40 * 960 * share), $"{charge.ExportPerDay}");
    Check("extractor planet: no import charges", Near(f.ImportChargesPerDay, 0));

    var later = PiEngine.Forecast(extractorPlanet, sd, H(t0, 24));
    Check("extractor planet: expired at 24 hours", later.Extractors.Single().IsExpired
          && later.Extractors.Single().RemainingOutput == 0);
}

// ── 3. Factory planet: P2 from imported P1 ──────────────────────────────────────────
//
// 4000 X and 2000 Y on the launchpad; one advanced processor taking 40 + 40 an hour into 5 Z,
// sent to a storage facility with room for exactly 100 more Z.
PiColonyLayout FactoryPlanet() => new()
{
    CharacterId = 1, PlanetId = 2, PlanetType = "temperate", LastUpdate = t0,
    Pins =
    [
        Pin(1, CcType),
        Pin(4, AdvType) with { SchematicId = SAdv, LastCycleStart = H(t0, -2) },
        Pin(5, StorageType, new() { [Z] = 15_900 }),       // 11,925 m³ of 12,000
        Pin(6, PadType, new() { [X] = 4000, [Y] = 2000 }),
    ],
    Routes =
    [
        new PiLayoutRoute(1, 6, 4, X, 40),
        new PiLayoutRoute(2, 6, 4, Y, 40),
        new PiLayoutRoute(3, 4, 5, Z, 5),
    ],
};
{
    var f = PiEngine.Forecast(FactoryPlanet(), sd, H(t0, 10), new PiChargeRate(0.10, PiChargeKind.CustomsOffice));
    Check("factory planet: classified as a factory planet", f.Kind == PiColonyKind.Factory, f.Kind.ToString());
    var proc = f.Factories.Single();
    // Idle at the snapshot (its last cycle ended an hour before), so it starts at once: starts at
    // 0..10 hours are eleven, completions at 1..10 hours ten.
    Check("factory planet: running at 10 hours", proc.StateAt == PiFactoryState.Running, proc.StateAt.ToString());
    Check("factory planet: ten cycles finished", proc.CyclesCompleted == 10, $"got {proc.CyclesCompleted}");

    var y = f.Inputs.SingleOrDefault(i => i.TypeId == Y);
    var xin = f.Inputs.SingleOrDefault(i => i.TypeId == X);
    Check("factory planet: both P1 inputs are imports", y is not null && xin is not null);
    Check("factory planet: Y on hand after eleven starts", y?.OnHandAt == 2000 - 11 * 40, $"got {y?.OnHandAt}");
    Check("factory planet: X on hand after eleven starts", xin?.OnHandAt == 4000 - 11 * 40, $"got {xin?.OnHandAt}");
    // 2000 Y is 50 cycles: the 50th starts at 49 h, finishes at 50 h, and the 51st cannot start.
    Check("factory planet: Y runs out at 50 hours", y?.RunsOutAt == H(t0, 50), $"got {y?.RunsOutAt}");
    Check("factory planet: X does not run out first", xin?.RunsOutAt is null, $"got {xin?.RunsOutAt}");
    Check("factory planet: inputs run out at 50 hours", f.InputsRunOutAt == H(t0, 50));

    // Room for 100 Z at 5 an hour: full at the 20th completion.
    var storage = f.Storage.Single(s => s.PinId == 5);
    Check("factory planet: Z in storage after ten cycles", storage.ContentsAt.GetValueOrDefault(Z) == 15_950,
          $"got {storage.ContentsAt.GetValueOrDefault(Z)}");
    Check("factory planet: storage full at 20 hours", storage.FullAt == H(t0, 20), $"got {storage.FullAt}");

    // Mid-cycle at the snapshot: its inputs are already in it, so nothing more is drawn until it
    // finishes half an hour later.
    var midCycle = new PiColonyLayout
    {
        CharacterId = 1, PlanetId = 2, LastUpdate = t0,
        Pins = FactoryPlanet().Pins.Select(p => p.PinId == 4 ? p with { LastCycleStart = H(t0, -0.5) } : p).ToList(),
        Routes = FactoryPlanet().Routes,
    };
    var early = PiEngine.Forecast(midCycle, sd, H(t0, 0.25));
    Check("factory planet: running at the snapshot when mid-cycle", early.Factories.Single().StateAt == PiFactoryState.Running
          && early.Inputs.Single(i => i.TypeId == Y).OnHandAt == 2000,
          $"{early.Factories.Single().StateAt}, Y {early.Inputs.Single(i => i.TypeId == Y).OnHandAt}");
    var firstDone = PiEngine.Forecast(midCycle, sd, H(t0, 0.75));
    Check("factory planet: the running cycle delivers at its end",
          firstDone.Factories.Single().CyclesCompleted == 1
          && firstDone.Storage.Single(s => s.PinId == 5).ContentsAt.GetValueOrDefault(Z) == 15_905);

    // A schematic set when the colony was saved, with no input anywhere: the game stamps
    // last_cycle_start with the save itself, and that is not a cycle (seen on a live colony whose
    // plants had never been given input).
    var neverFed = new PiColonyLayout
    {
        CharacterId = 1, PlanetId = 2, LastUpdate = t0,
        Pins = FactoryPlanet().Pins.Select(p => p with
        {
            Contents       = new Dictionary<int, long>(),
            LastCycleStart = p.PinId == 4 ? t0 : p.LastCycleStart,
        }).ToList(),
        Routes = FactoryPlanet().Routes,
    };
    var unfed = PiEngine.Forecast(neverFed, sd, H(t0, 3));
    Check("factory planet: a cycle stamped at the save is not a cycle",
          unfed.Factories.Single().StateAt == PiFactoryState.Idle && unfed.Factories.Single().CyclesCompleted == 0
          && unfed.Storage.All(s => s.ContentsAt.Count == 0),
          $"{unfed.Factories.Single().StateAt}, cycles {unfed.Factories.Single().CyclesCompleted}");

    var after = PiEngine.Forecast(FactoryPlanet(), sd, H(t0, 25));
    Check("factory planet: output lost once storage is full", after.LostAt.GetValueOrDefault(Z) == 25,
          $"got {after.LostAt.GetValueOrDefault(Z)}");

    var fx = f.Flows.Single(fl => fl.TypeId == X);
    var fz = f.Flows.Single(fl => fl.TypeId == Z);
    Check("factory planet: imports 960 X a day", Near(fx.ImportedPerDay, 960), $"{fx.ImportedPerDay}");
    Check("factory planet: exports 120 Z a day", Near(fz.ExportedPerDay, 120), $"{fz.ExportedPerDay}");
    // Import is half of export: P1 400 × 10 % ÷ 2 = 20 a unit; P2 7,200 × 10 % = 720.
    var cx = f.Charges.Single(c => c.TypeId == X);
    var cz = f.Charges.Single(c => c.TypeId == Z);
    Check("factory planet: P1 import charge is half of export", Near(cx.ImportPerUnit, 20) && Near(cx.ExportPerUnit, 40),
          $"{cx.ImportPerUnit} / {cx.ExportPerUnit}");
    Check("factory planet: P2 export charge per day", Near(cz.ExportPerDay, 720 * 120), $"{cz.ExportPerDay}");
    Check("factory planet: import charges per day", Near(f.ImportChargesPerDay, 2 * 20 * 960), $"{f.ImportChargesPerDay}");

    var money = PiEconomics.For(f, new Dictionary<int, double> { [X] = 100, [Y] = 150, [Z] = 20_000 });
    Check("factory planet: output value", Near(money.OutputValuePerDay, 120 * 20_000.0), $"{money.OutputValuePerDay}");
    Check("factory planet: input cost at market", Near(money.InputCostPerDay, 960 * 100.0 + 960 * 150.0), $"{money.InputCostPerDay}");
    Check("factory planet: profit = output − inputs − import − export",
          Near(money.ProfitPerDay, 2_400_000 - 240_000 - 86_400 - 38_400), $"{money.ProfitPerDay}");
}

// ── 4. A full launchpad ─────────────────────────────────────────────────────────────
{
    var layout = new PiColonyLayout
    {
        CharacterId = 1, PlanetId = 3, LastUpdate = t0,
        Pins =
        [
            Pin(4, AdvType) with { SchematicId = SAdv },
            Pin(5, StorageType, new() { [X] = 1000, [Y] = 1000 }),
            Pin(6, PadType, new() { [Z] = 13_334 }),          // 10,000.5 m³: over capacity
        ],
        Routes =
        [
            new PiLayoutRoute(1, 5, 4, X, 40),
            new PiLayoutRoute(2, 5, 4, Y, 40),
            new PiLayoutRoute(3, 4, 6, Z, 5),
        ],
    };
    var f   = PiEngine.Forecast(layout, sd, H(t0, 3));
    var pad = f.Storage.Single(s => s.PinId == 6);
    Check("full launchpad: full at the snapshot", pad.FullAt == t0, $"got {pad.FullAt}");
    Check("full launchpad: reads as full", pad.FillAt >= 1);
    Check("full launchpad: every cycle's output lost", f.LostAt.GetValueOrDefault(Z) == 15,
          $"got {f.LostAt.GetValueOrDefault(Z)}");
    Check("full launchpad: the processor keeps running", f.Factories.Single().StateAt == PiFactoryState.Running);
}

// ── 5. An idle processor ────────────────────────────────────────────────────────────
{
    var layout = new PiColonyLayout
    {
        CharacterId = 1, PlanetId = 4, LastUpdate = t0,
        Pins = [Pin(1, CcType), Pin(3, BasicType) with { SchematicId = SBasic, LastCycleStart = H(t0, -5) }],
    };
    var f = PiEngine.Forecast(layout, sd, H(t0, 5));
    var p = f.Factories.Single();
    Check("idle processor: idle", p.StateAt == PiFactoryState.Idle, p.StateAt.ToString());
    Check("idle processor: idle since the snapshot", p.IdleSince == t0, $"got {p.IdleSince}");
    Check("idle processor: its input already ran out", f.Inputs.Single().RunsOutAt == t0);
    Check("idle processor: no cycles", p.CyclesCompleted == 0);
}

// ── 6. An expired extractor ─────────────────────────────────────────────────────────
{
    var layout = new PiColonyLayout
    {
        CharacterId = 1, PlanetId = 5, LastUpdate = t0,
        Pins =
        [
            Pin(1, CcType),
            Pin(2, EcuType) with { InstallTime = H(t0, -72), ExpiryTime = H(t0, -24), Extractor = new PiLayoutExtractor(A, 3600, 2000, 6) },
            Pin(5, StorageType, new() { [A] = 5000 }),
        ],
        Routes = [new PiLayoutRoute(1, 2, 5, A, 2000)],
    };
    var f = PiEngine.Forecast(layout, sd, H(t0, 6));
    var x = f.Extractors.Single();
    Check("expired extractor: expired", x.IsExpired);
    Check("expired extractor: every cycle done, nothing left", x.CyclesDone == x.TotalCycles && x.RemainingOutput == 0 && x.TotalCycles == 48,
          $"{x.CyclesDone}/{x.TotalCycles}");
    Check("expired extractor: stopped a day before the snapshot", f.ExtractorsStopAt == H(t0, -24));
    Check("expired extractor: nothing more arrives", f.Storage.Single(s => s.PinId == 5).ContentsAt.GetValueOrDefault(A) == 5000);
}

// ── 6b. An extractor routed straight into a processor ──────────────────────────────
//
// A processor holds one cycle's worth of each input and no more, so most of what an extractor
// sends it directly is lost. Everything extracted must still be accounted for.
{
    var layout = new PiColonyLayout
    {
        CharacterId = 1, PlanetId = 6, LastUpdate = t0,
        Pins =
        [
            Pin(1, CcType),
            Pin(2, EcuType) with { InstallTime = t0, ExpiryTime = H(t0, 24), Extractor = new PiLayoutExtractor(A, 1800, 500, 4) },
            Pin(3, BasicType) with { SchematicId = SBasic },
        ],
        Routes = [new PiLayoutRoute(1, 2, 3, A, 500), new PiLayoutRoute(2, 3, 1, X, 20)],
    };
    var f    = PiEngine.Forecast(layout, sd, H(t0, 10));
    var proc = f.Factories.Single();
    var consumed = 1000L * (proc.CyclesCompleted + (proc.StateAt == PiFactoryState.Running ? 1 : 0));
    var lost     = f.LostAt.GetValueOrDefault(A);
    Check("direct route: some raw lost to a full processor buffer", lost > 0, $"lost {lost}");
    Check("direct route: buffer never above one cycle", proc.BufferAt.GetValueOrDefault(A) <= 1000);
    Check("direct route: raw material conserved", consumed + proc.BufferAt.GetValueOrDefault(A) + lost == 17_541,
          $"consumed {consumed} + buffer {proc.BufferAt.GetValueOrDefault(A)} + lost {lost}");
}

// ── 7. Customs office or skyhook ────────────────────────────────────────────────────
Check("charge kind: high-sec is customs", PiCharges.KindFor(0.9, null, false) == PiChargeKind.CustomsOffice);
Check("charge kind: 0.02 shows as 0.1 and is customs", PiCharges.KindFor(0.02, null, false) == PiChargeKind.CustomsOffice);
Check("charge kind: sovereignty null-sec is a skyhook", PiCharges.KindFor(-0.4, null, false) == PiChargeKind.Skyhook);
Check("charge kind: NPC null-sec is customs", PiCharges.KindFor(-0.4, 500_010, false) == PiChargeKind.CustomsOffice);
Check("charge kind: a wormhole is customs", PiCharges.KindFor(-1.0, null, true) == PiChargeKind.CustomsOffice);

// ── 8. Skills ───────────────────────────────────────────────────────────────────────
Check("skills: Interplanetary Consolidation 3 allows 4 colonies", new PiSkills(1, 3, 0).ColoniesAllowed == 4);
Check("skills: never more than 6 colonies", new PiSkills(1, 5, 0).ColoniesAllowed == 6);
Check("skills: Command Center Upgrades 4 allows level 4", new PiSkills(1, 0, 4).MaxUpgradeLevel == 4);

// ── 9. Tax learning, in memory ──────────────────────────────────────────────────────
//
// The factory planet viewed again ten hours later: by then the owner had taken 2,000 Z off and
// brought 1,000 Y in. The newer snapshot is the forecast with those two changes; the movements
// must find exactly them, and an 8 % rate must come back out of the journal amounts.
{
    var older = FactoryPlanet();
    var at    = H(t0, 10);
    var sim   = PiEngine.Forecast(older, sd, at);

    var contents = new Dictionary<long, Dictionary<int, long>>();
    foreach (var s in sim.Storage)   contents[s.PinId] = new(s.ContentsAt);
    foreach (var p in sim.Factories) contents[p.PinId] = new(p.BufferAt);
    contents[5][Z] -= 2000;
    contents[6][Y] = contents[6].GetValueOrDefault(Y) + 1000;

    var newer = new PiColonyLayout
    {
        CharacterId = older.CharacterId, PlanetId = older.PlanetId, LastUpdate = at,
        Pins = older.Pins.Select(p => p with { Contents = contents.GetValueOrDefault(p.PinId) ?? [] }).ToList(),
        Routes = older.Routes,
    };

    var moves = PiLayoutStore.Movements(older, newer, sd);
    Check("movements: exactly the export and the import", moves.Count == 2
          && moves.Any(m => m.TypeId == Z && m.Removed == 2000 && m.Added == 0)
          && moves.Any(m => m.TypeId == Y && m.Added == 1000 && m.Removed == 0),
          string.Join("; ", moves.Select(m => $"{m.TypeId} -{m.Removed} +{m.Added}")));

    const double rate = 0.08;
    var exportTax = (decimal)(2000 * 7_200 * rate);       // P2 base 7,200
    var importTax = (decimal)(1000 * 400 * rate / 2);     // P1 base 400, import half
    var entries = new List<PiTaxEntry>
    {
        new(9001, 1, older.PlanetId, H(t0, 3), IsExport: true,  exportTax),
        new(9002, 1, older.PlanetId, H(t0, 4), IsExport: false, importTax),
    };
    var imported = PiEngine.Flows(newer, sd).Where(fl => fl.ImportedPerDay > 0).Select(fl => fl.TypeId).ToHashSet();

    var learned = PiTaxLearning.Learn(entries, moves, sd, _ => imported).SingleOrDefault();
    Check("tax: learns 8 % from an export and an import together", learned is not null && Near(learned.Rate, rate),
          $"got {learned?.Rate}");
    Check("tax: dated and sourced by the newest entry", learned?.JournalId == 9002 && learned.LearnedAt == H(t0, 4)
          && learned.Source == "both" && learned.Units == 3000, $"{learned?.JournalId} {learned?.Source} {learned?.Units}");

    var exportOnly = PiTaxLearning.Learn(entries.Take(1), moves, sd, _ => imported).SingleOrDefault();
    Check("tax: learns from an export alone", exportOnly is not null && Near(exportOnly.Rate, rate), $"got {exportOnly?.Rate}");

    // Output that ran ahead of the forecast also reads as "added". It must not be taken for an
    // import: only what the colony consumes and makes nowhere is.
    var noisy = new List<PiColonyMovement>
    {
        new() { CharacterId = 1, PlanetId = older.PlanetId, FromUpdate = t0, ToUpdate = at, TypeId = Y, Added = 1000 },
        new() { CharacterId = 1, PlanetId = older.PlanetId, FromUpdate = t0, ToUpdate = at, TypeId = Z, Added = 10 },
    };
    var importOnly = PiTaxLearning.Learn(entries.Skip(1), noisy, sd, _ => imported).SingleOrDefault();
    Check("tax: an import is counted in imported types only", importOnly is not null && Near(importOnly.Rate, rate),
          $"got {importOnly?.Rate}");

    var outside = new[] { new PiTaxEntry(9003, 1, older.PlanetId, H(t0, 11), true, exportTax) };
    Check("tax: an entry after the newer snapshot teaches nothing yet",
          PiTaxLearning.Learn(outside, moves, sd, _ => imported).Count == 0);

    var absurd = new[] { new PiTaxEntry(9004, 1, older.PlanetId, H(t0, 3), true, 1_000_000_000_000m) };
    Check("tax: a rate over 100 % is thrown away", PiTaxLearning.Learn(absurd, moves, sd, _ => imported).Count == 0);

    var otherPlanet = new[] { new PiTaxEntry(9005, 1, older.PlanetId + 1, H(t0, 3), true, exportTax) };
    Check("tax: per planet — another planet's entry teaches this one nothing",
          PiTaxLearning.Learn(otherPlanet, moves, sd, _ => imported).Count == 0);

    // What the journal row carries: ref type, planet as the context, the amount negative.
    var row = new WalletJournalEntry { EsiId = 1, OwnerId = 7, OwnerType = "character", Date = t0,
        RefType = "planetary_export_tax", Amount = -1234.5m, ContextId = 40_000_002, ContextIdType = "planet_id" };
    var entry = PiTaxLearning.FromJournal(row);
    Check("journal: an export tax row is read", entry is { IsExport: true, PlanetId: 40_000_002, Amount: 1234.5m },
          $"{entry}");
    Check("journal: an import tax row is an import",
          PiTaxLearning.FromJournal(new WalletJournalEntry { RefType = "planetary_import_tax", ContextId = 40_000_002, ContextIdType = "planet_id" })
              is { IsExport: false });
    Check("journal: another ref type is ignored",
          PiTaxLearning.FromJournal(new WalletJournalEntry { RefType = "market_transaction", ContextId = 40_000_002 }) is null);
    Check("journal: a context that is not a planet is ignored",
          PiTaxLearning.FromJournal(new WalletJournalEntry { RefType = "planetary_export_tax", ContextId = 60_003_760, ContextIdType = "station_id" }) is null);
}

// ── 11. Worklist tasks ──────────────────────────────────────────────────────────────
//
// The PI generator's rules on constructed colonies: which colony gets which task, a character's
// colonies in one system hauled as one stop, input brought for the configured days, slots and
// upgrades from the colony list, the "open it in game" task only where nothing else will open the
// colony, and keys free of quantities and times.
{
    const long PilotOne = 1, PilotTwo = 2;
    const int Jita = 30_000_142, Perimeter = 30_000_144;
    var now = H(t0, 10);
    var t   = PiThresholds.Default;   // extractors 24 h, storage 24 h, inputs 48 h, stale 7 d, 7 days of input

    PiColonyLayout Extractor(long ch, int planet, int system, double stopsInHours, DateTimeOffset? snapshot = null) => new()
    {
        CharacterId = ch, PlanetId = planet, SolarSystemId = system, PlanetType = "barren", UpgradeLevel = 4,
        LastUpdate = snapshot ?? t0,
        Pins =
        [
            Pin(1, CcType),
            Pin(2, EcuType) with { InstallTime = H(now, stopsInHours - 24), ExpiryTime = H(now, stopsInHours),
                                   Extractor = new PiLayoutExtractor(A, 1800, 500, 4) },
            Pin(5, StorageType),
        ],
        Routes = [new PiLayoutRoute(1, 2, 5, A, 500)],
    };
    PiColonyLayout Factory(long ch, int planet, int system)
    {
        var f = FactoryPlanet();
        return new PiColonyLayout
        {
            CharacterId = ch, PlanetId = planet, SolarSystemId = system, PlanetType = "temperate", UpgradeLevel = 4,
            LastUpdate = f.LastUpdate, Pins = f.Pins, Routes = f.Routes,
        };
    }
    PiColonyLayout Lonely(long ch, int planet, int system, double daysOld) => new()
    {
        CharacterId = ch, PlanetId = planet, SolarSystemId = system, PlanetType = "ice", UpgradeLevel = 4,
        LastUpdate = now.AddDays(-daysOld), Pins = [Pin(1, CcType)],
    };
    PiColonyStatus Status(PiColonyLayout l, string name, string system)
    {
        var f = PiEngine.Forecast(l, sd, now, new PiChargeRate(0.10, PiChargeKind.CustomsOffice));
        return new PiColonyStatus(l.CharacterId, l.CharacterId == PilotOne ? "Pilot One" : "Pilot Two", l.PlanetId,
                                  name, l.SolarSystemId, system, 0.9, 4, f, PiEconomics.For(f, new Dictionary<int, double>()));
    }

    var colonies = new List<PiColonyStatus>
    {
        Status(Extractor(PilotOne, 101, Jita, -5), "Jita I", "Jita"),                              // stopped 5 h ago
        Status(Extractor(PilotOne, 102, Jita, 10), "Jita II", "Jita"),                             // stops in 10 h
        Status(Extractor(PilotOne, 103, Jita, 48), "Jita III", "Jita"),                            // stops in 2 days
        Status(Factory(PilotOne, 104, Jita), "Jita IV", "Jita"),                                   // fills in 10 h, Y out in 40 h
        Status(Factory(PilotOne, 105, Jita), "Jita V", "Jita"),                                    // the same, same system
        Status(Factory(PilotOne, 106, Perimeter), "Perimeter I", "Perimeter"),                     // the same, another system
        Status(Lonely(PilotTwo, 201, Perimeter, 10), "Perimeter II", "Perimeter"),                // 10 days old, nothing else
        Status(Extractor(PilotTwo, 202, Perimeter, -30, now.AddDays(-9)), "Perimeter III", "Perimeter"), // old AND stopped
        Status(Factory(PilotTwo, 203, Jita), "Jita VI", "Jita"),                                   // another character, same system
    };
    var characters = new List<PiCharacterStatus>
    {
        // Interplanetary Consolidation 5: six colonies allowed, six used — no slot free.
        new(PilotOne, "Pilot One", new PiSkills(PilotOne, 5, 4),
            colonies.Where(c => c.CharacterId == PilotOne)
                .Select(c => new PiColonySlot(c.PlanetId, "barren", c.SolarSystemId, c.PlanetId == 103 ? 2 : 4, 4, true)
                             { PlanetName = c.PlanetName, SystemName = c.SystemName })
                .ToList()),
        // Interplanetary Consolidation 3: four allowed, three used — one free.
        new(PilotTwo, "Pilot Two", new PiSkills(PilotTwo, 3, 4),
            colonies.Where(c => c.CharacterId == PilotTwo)
                .Select(c => new PiColonySlot(c.PlanetId, "ice", c.SolarSystemId, 4, 4, true)
                             { PlanetName = c.PlanetName, SystemName = c.SystemName })
                .ToList()),
    };

    var names = new Dictionary<int, string> { [A] = "Raw A", [X] = "Processed X", [Y] = "Processed Y", [Z] = "Refined Z" };
    var tasks = PiTaskPlanner.Plan(colonies, characters, t, sd, names);
    var keys  = tasks.Select(x => x.Key).ToHashSet();
    WorklistItem? Find(string key) => tasks.FirstOrDefault(x => x.Key == key);

    string[] expected =
    [
        "pi:extractors:1:101", "pi:extractors:1:102", "pi:extractors:2:202",
        "pi:haul_out:1:30000142", "pi:haul_out:1:30000144", "pi:haul_out:2:30000142",
        "pi:haul_in:1:104", "pi:haul_in:1:105", "pi:haul_in:1:106", "pi:haul_in:2:203",
        "pi:setup:2",
        "pi:upgrade:1:103",
        "pi:stale:2:201",
    ];
    Check("tasks: exactly the expected tasks", keys.SetEquals(expected),
          $"missing [{string.Join(", ", expected.Except(keys))}], extra [{string.Join(", ", keys.Except(expected))}]");
    Check("tasks: no duplicate keys", tasks.Count == keys.Count, $"{tasks.Count} tasks, {keys.Count} keys");
    Check("tasks: every task is a PI task from the PI source",
          tasks.All(x => x.Kind == WorklistKind.Pi && x.Source == PiGenerator.SourceId));

    // Restart extractors: stopped is ready, stopping is waiting, two days out is nothing yet.
    Check("restart: a stopped extractor is ready, at Missing",
          Find("pi:extractors:1:101") is { Readiness: WorklistReadiness.Ready, Priority: WorklistPriority.Missing });
    Check("restart: one stopping within the lead time waits",
          Find("pi:extractors:1:102") is { Readiness: WorklistReadiness.Waiting } w && w.BlockedBy.Length > 0);
    Check("restart: one stopping after the lead time raises nothing", !keys.Contains("pi:extractors:1:103"));
    Check("restart: names the character and the planet",
          Find("pi:extractors:1:101") is { CharacterId: PilotOne, CharacterName: "Pilot One", LocationId: 101 });

    // Take output off: Jita IV and Jita V are one stop; Perimeter I is another.
    var jita = Find("pi:haul_out:1:30000142");
    Check("haul out: one character's colonies in one system are one stop",
          jita is not null && jita.LocationId == Jita && jita.Lines.Count == 1
          && jita.Lines[0].TypeId == Z && jita.Lines[0].Quantity == 2 * 15_950,
          jita is null ? "none" : string.Join(", ", jita.Lines.Select(l => $"{l.TypeId}×{l.Quantity}")));
    Check("haul out: before full is HaulUnblocking", jita?.Priority == WorklistPriority.HaulUnblocking);
    Check("haul out: another character in the same system is another haul",
          Find("pi:haul_out:2:30000142") is { CharacterId: PilotTwo, LocationId: 203 } other && other.Lines.Single().Quantity == 15_950);
    var perimeter = Find("pi:haul_out:1:30000144");
    Check("haul out: a colony in another system is its own stop, at the planet",
          perimeter is not null && perimeter.LocationId == 106 && perimeter.Lines.Count == 1 && perimeter.Lines[0].Quantity == 15_950);
    Check("haul out: raw material used on the planet is not output",
          tasks.Where(x => x.Key.StartsWith("pi:haul_out")).SelectMany(x => x.Lines).All(l => l.TypeId == Z));

    // Bring input: seven days at 960 a day of each, less what is on hand after 11 starts.
    var bring = Find("pi:haul_in:1:104");
    Check("haul in: seven days of each input less what is there",
          bring is not null
          && bring.Lines.SingleOrDefault(l => l.TypeId == X)?.Quantity == 7 * 960 - (4000 - 11 * 40)
          && bring.Lines.SingleOrDefault(l => l.TypeId == Y)?.Quantity == 7 * 960 - (2000 - 11 * 40),
          bring is null ? "none" : string.Join(", ", bring.Lines.Select(l => $"{l.TypeId}×{l.Quantity}")));
    Check("haul in: to the planet, separate from the output haul",
          bring is { DestinationId: 104, LocationId: 0, Priority: WorklistPriority.Missing });
    var fewerDays = PiTaskPlanner.Plan(colonies, characters, t with { InputDays = 3 }, sd, names);
    // Three days: 2,880 of each, and 3,560 X is already there — only Y is wanted.
    var three = fewerDays.SingleOrDefault(x => x.Key == "pi:haul_in:1:104");
    Check("haul in: the days of input setting is used, and an input already covered is left out",
          three is not null && three.Lines.Count == 1 && three.Lines[0].TypeId == Y
          && three.Lines[0].Quantity == 3 * 960 - (2000 - 11 * 40),
          three is null ? "none" : string.Join(", ", three.Lines.Select(l => $"{l.TypeId}×{l.Quantity}")));
    var noLead = PiTaskPlanner.Plan(colonies, characters, t with { InputLead = TimeSpan.FromHours(24) }, sd, names);
    Check("haul in: not raised before the lead time", !noLead.Any(x => x.Key.StartsWith("pi:haul_in")));

    // Slots and command centers, from the colony list.
    Check("setup: only a character with a free slot", keys.Contains("pi:setup:2") && !keys.Contains("pi:setup:1")
          && Find("pi:setup:2")?.Priority == WorklistPriority.Housekeeping);
    Check("upgrade: only where the skill allows a higher level",
          tasks.Count(x => x.Key.StartsWith("pi:upgrade")) == 1 && Find("pi:upgrade:1:103")?.Priority == WorklistPriority.Housekeeping);

    // Old data: a task of its own only when no other task will open the colony.
    Check("stale: a lone old colony is to be opened in game", Find("pi:stale:2:201") is { Priority: WorklistPriority.Housekeeping });
    Check("stale: an old colony with another task gets no second one", !keys.Contains("pi:stale:2:202"));

    // Keys carry no quantity or time: half an hour later the same work has the same keys.
    now = H(t0, 10.5);
    var later = colonies.Select(c => Status(c.Forecast.Layout, c.PlanetName, c.SystemName)).ToList();
    var laterKeys = PiTaskPlanner.Plan(later, characters, t, sd, names).Select(x => x.Key).ToHashSet();
    Check("keys: stable as the estimates move", laterKeys.SetEquals(keys),
          $"changed: [{string.Join(", ", keys.Except(laterKeys).Concat(laterKeys.Except(keys)))}]");
    Check("keys: no quantity in any key", tasks.All(x => !x.Key.Contains("15950") && !x.Key.Contains("31900")));

    // The attention the tool's chip shows agrees with the tasks.
    var stoppedAttention = PiColonyAttention.For(colonies[0].Forecast, t);
    Check("attention: a stopped extractor is Action", stoppedAttention.State == PiColonyState.Action);
    Check("attention: two days out is OK", PiColonyAttention.For(colonies[2].Forecast, t).State == PiColonyState.Ok);
    Check("attention: old data is Attention", PiColonyAttention.For(colonies[6].Forecast, t).State == PiColonyState.Attention);
}

// ── 10. Through a database ──────────────────────────────────────────────────────────
//
// A throwaway SQLite file built from the model: two snapshots of one colony stored the way the
// poller stores them, an export tax entry between them, the rate learned and stored; and the PI
// switch read back.
var dbPath = Path.Combine(Path.GetTempPath(), $"eveconsole-picheck-{Guid.NewGuid():N}.db");
try
{
    var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").Options;
    await using var db = new AppDbContext(opts);
    db.Database.EnsureCreated();

    const long charId = 90_000_001;
    const int planet  = 40_000_001;
    var colony = new PlanetaryColony { CharacterId = charId, PlanetId = planet, PlanetType = "barren",
                                       SolarSystemId = 30_000_142, LastUpdate = t0, NumPins = 2 };
    db.EsiPlanetaryColonies.Add(colony);
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();

    // Storage-only colony: nothing is made, so the only change is what was taken off.
    EsiPlanetLayout Esi(long amount) => new(
        Links: [new EsiPlanetLink(11, 12, 0)],
        Pins:
        [
            new EsiPlanetPin(11, CcType, 0.1, 0.2, null, null, null, null, [], null, null),
            new EsiPlanetPin(12, StorageType, 0.3, 0.4, null, null, null, null,
                             amount > 0 ? [new EsiPlanetContent(X, amount)] : [], null, null),
        ],
        Routes: []);

    await PiLayoutStore.ReplaceAsync(db, colony, Esi(1000), sd, t0);
    var loaded = await PiLayoutStore.LoadAsync(db, [charId]);
    Check("db: a stored layout reads back", loaded.Count == 1 && loaded[0].Pins.Count == 2 && loaded[0].Links.Count == 1
          && loaded[0].Pins.Single(p => p.PinId == 12).Contents.GetValueOrDefault(X) == 1000);

    // Viewed again two hours later with the 1000 X gone; exported an hour in at 12 %.
    var t1 = H(t0, 2);
    await db.EsiPlanetaryColonies.Where(c => c.CharacterId == charId)
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUpdate, t1));
    colony.LastUpdate = t1;
    await PiLayoutStore.ReplaceAsync(db, colony, Esi(0), sd, t1);

    var stored = await db.PiColonyMovements.AsNoTracking().ToListAsync();
    Check("db: the export is recorded as a movement", stored.Count == 1 && stored[0].TypeId == X && stored[0].Removed == 1000,
          string.Join("; ", stored.Select(m => $"{m.TypeId} -{m.Removed} +{m.Added}")));
    Check("db: the old snapshot was replaced, not added to",
          await db.EsiPlanetaryPins.CountAsync() == 2 && await db.EsiPlanetaryLayouts.CountAsync() == 1);

    db.EsiWalletJournal.Add(new WalletJournalEntry
    {
        EsiId = 555, OwnerId = charId, OwnerType = "character", Date = H(t0, 1),
        RefType = "planetary_export_tax", Amount = -(1000 * 400 * 0.12m),
        ContextId = planet, ContextIdType = "planet_id",
    });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();

    var changed = await PiTaxLearning.LearnAsync(db, charId, sd);
    var rateRow = await db.PiPlanetTaxRates.AsNoTracking().SingleOrDefaultAsync(r => r.PlanetId == planet);
    Check("db: the planet's rate is learned and stored", changed == 1 && rateRow is not null && Near(rateRow.Rate, 0.12)
          && rateRow.JournalId == 555, $"changed {changed}, rate {rateRow?.Rate}");
    Check("db: learning again changes nothing", await PiTaxLearning.LearnAsync(db, charId, sd) == 0);

    await PiLayoutStore.DeleteAsync(db, charId, planet);
    Check("db: a given-up colony's layout is removed",
          await db.EsiPlanetaryPins.CountAsync() == 0 && await db.EsiPlanetaryLayouts.CountAsync() == 0
          && await db.PiPlanetTaxRates.CountAsync() == 1);

    // The PI switch: absence means on; only a row saying false takes a character out.
    db.Characters.AddRange(
        new Character { Id = 90_000_001, Name = "Pilot One",   RefreshToken = "t" },
        new Character { Id = 90_000_002, Name = "Pilot Two",   RefreshToken = "t" },
        new Character { Id = 90_000_003, Name = "Pilot Three", RefreshToken = "t" });
    db.WorklistIndyChars.AddRange(
        new WorklistIndyChar { CharacterId = 90_000_002, CharacterName = "Pilot Two",   PlanetaryIndustry = false },
        new WorklistIndyChar { CharacterId = 90_000_003, CharacterName = "Pilot Three", PlanetaryIndustry = true });
    await db.SaveChangesAsync();
    var pi = await PiCharacters.IdsAsync(db);
    Check("PI switch: unset and ticked characters do PI, a cleared one does not",
          pi.SetEquals([90_000_001L, 90_000_003L]), string.Join(",", pi));
    Check("PI switch: one character asked directly",
          !await PiCharacters.IsOnAsync(db, 90_000_002) && await PiCharacters.IsOnAsync(db, 90_000_001));

    // The worklist generator end to end: a stopped extractor on a character that does PI and on
    // one whose PI box is cleared. Only the first raises anything — no task, slot or upgrade for
    // the second.
    EsiPlanetLayout Stopped() => new(
        Links: [],
        Pins:
        [
            new EsiPlanetPin(21, CcType, 0, 0, null, null, null, null, [], null, null),
            new EsiPlanetPin(22, EcuType, 0, 0, null, t0.AddDays(-3), t0.AddDays(-2), null, [],
                             new EsiExtractorDetails(1800, 0.01, [], A, 500), null),
        ],
        Routes: []);
    var stamp = DateTimeOffset.UtcNow.AddHours(-1);
    foreach (var (ch, planetId) in new[] { (90_000_001L, 40_000_011), (90_000_002L, 40_000_012) })
    {
        var c = new PlanetaryColony { CharacterId = ch, PlanetId = planetId, PlanetType = "barren",
                                      SolarSystemId = 30_000_142, LastUpdate = stamp, NumPins = 2, UpgradeLevel = 0 };
        db.EsiPlanetaryColonies.Add(c);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await PiLayoutStore.ReplaceAsync(db, c, Stopped(), null, stamp);
    }
    db.EsiSkills.Add(new StoredSkill { CharacterId = 90_000_002, SkillId = PiSkills.CommandCenterUpgradesId,
                                       TrainedSkillLevel = 5, ActiveSkillLevel = 5 });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();

    var prefs     = new AppPreferencesService(null!);   // nothing set: every PI setting at its default
    var piService = new PiService(new CheckDbFactory(opts), new PiTaxService(prefs), new PiSettings(prefs));
    var generated = await new PiGenerator(piService).GenerateAsync();
    Check("generator: the PI character's stopped extractor is a task",
          generated.Any(x => x.Key == "pi:extractors:90000001:40000011"),
          string.Join(", ", generated.Select(x => x.Key)));
    Check("generator: a character with the PI box cleared raises nothing",
          generated.All(x => x.CharacterId != 90_000_002), string.Join(", ", generated.Select(x => x.Key)));
    Check("generator: a PI character with no colony is offered a slot", generated.Any(x => x.Key == "pi:setup:90000003"));
}
catch (Exception ex)
{
    failures.Add($"  FAIL  database section threw: {ex.GetType().Name}: {ex.Message}");
}
finally
{
    SqliteConnection.ClearAllPools();
    foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        try { File.Delete(f); } catch { /* a temp file left behind is not worth failing over */ }
}

Console.WriteLine($"PI engine check: {checks} check(s), {failures.Count} failure(s).");
foreach (var f in failures) Console.WriteLine(f);
return failures.Count == 0 ? 0 : 1;

/// <summary>A context per call over the throwaway database, as the app's factory gives them.</summary>
sealed class CheckDbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);
}
