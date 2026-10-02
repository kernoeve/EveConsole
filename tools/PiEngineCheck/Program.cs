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
//  the PI box cleared left out — on constructed colonies and through the same database; then a
//  typical extractor planet hour by hour against a half-hour hand model, with a full launchpad
//  and overflowing storage, and the potential, forecast, destroyed and idle output over its
//  period and over a factory planet's next 30 days.
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

    // Imports and exports: the raw material is made and used up on the planet, so only the P1
    // goes off it — and nothing comes in.
    Check("extractor planet: exports are the P1 alone", f.Exports.Select(e => e.TypeId).SequenceEqual([X]),
          string.Join(",", f.Exports.Select(e => e.TypeId)));
    Check("extractor planet: no imports", !f.Imports.Any());

    // Waiting to be picked up: the P1 in storage, not the raw material sitting beside it for the
    // processor.
    var waiting = PiWaitingExport.For(f, sd, new Dictionary<int, double> { [X] = 4.0, [A] = 1.0 });
    var px      = storage.ContentsAt.GetValueOrDefault(X);
    Check("extractor planet: raw material in storage, the case the next check is about",
          storage.ContentsAt.GetValueOrDefault(A) > 0, $"{storage.ContentsAt.GetValueOrDefault(A)}");
    Check("extractor planet: waiting is the P1 in storage alone",
          waiting.Count == 1 && waiting[0].TypeId == X && waiting[0].Units == px && px > 0,
          string.Join(",", waiting.Select(w => $"{w.TypeId}:{w.Units}")));
    Check("extractor planet: waiting m³ and ISK", waiting.Count == 1
          && Near(waiting[0].Volume, px * sd.VolumeOf(X)) && Near(waiting[0].Value, px * 4.0),
          string.Join(",", waiting.Select(w => $"{w.Volume}/{w.Value}")));

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

    // Both P1 come in from off the planet, the P2 goes off it.
    Check("factory planet: imports are the two P1", f.Imports.Select(i => i.TypeId).Order().SequenceEqual(new[] { X, Y }.Order()),
          string.Join(",", f.Imports.Select(i => i.TypeId)));
    Check("factory planet: exports are the P2 alone", f.Exports.Select(e => e.TypeId).SequenceEqual([Z]),
          string.Join(",", f.Exports.Select(e => e.TypeId)));

    // Waiting: the Z in storage; the X and Y still on the launchpad are input, not waiting.
    var waiting = PiWaitingExport.For(f, sd, new Dictionary<int, double> { [X] = 1, [Y] = 1, [Z] = 10 });
    Check("factory planet: waiting is the Z in storage alone",
          waiting.Count == 1 && waiting[0].TypeId == Z && waiting[0].Units == 15_950,
          string.Join(",", waiting.Select(w => $"{w.TypeId}:{w.Units}")));
    Check("factory planet: waiting m³ and ISK", waiting.Count == 1
          && Near(waiting[0].Volume, 15_950 * sd.VolumeOf(Z)) && Near(waiting[0].Value, 159_500),
          string.Join(",", waiting.Select(w => $"{w.Volume}/{w.Value}")));

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

// ── 12. A typical extractor planet ──────────────────────────────────────────────────
//
// Built the way an extractor planet usually is: two extractors on 2-hour cycles for a week (84
// cycles, four heads each), each into its own storage facility; each storage feeding two basic
// industry facilities (3,000 raw → 20 P1 every 30 minutes); all four sending their P1 to one
// launchpad. The snapshot is taken two minutes after the programs were set, with everything
// empty. Ids and types are invented.
//
// Every expected number is worked out here without the engine: the yields by the published
// formula written out again (and pinned to values computed apart from this program), the
// factories by a half-hour clock (ChainModel, below) rather than an event queue. Every event on
// this colony falls on one half-hour grid — the first extractor cycle ends on it, and both cycle
// times are whole multiples of it — so a clock sees exactly what the engine should.
{
    const int RawA = 110, RawB = 111, ProdA = 210, ProdB = 211;
    const int SchemA = 1011, SchemB = 1012;
    const int SmallStorageType = 7, SmallPadType = 8;
    const long Room = 2_400_000;          // 12,000 m³ of raw at 0.005 m³
    const long SmallRoom = 20_000;        // 100 m³

    var sdx = new PiStaticData
    {
        PinTypes = new Dictionary<int, PiPinType>
        {
            [CcType]           = new(CcType,           PiPinKind.CommandCenter,        500,    PiProcessorTier.None),
            [EcuType]          = new(EcuType,          PiPinKind.ExtractorControlUnit, 0,      PiProcessorTier.None),
            [BasicType]        = new(BasicType,        PiPinKind.Processor,            0,      PiProcessorTier.Basic),
            [StorageType]      = new(StorageType,      PiPinKind.Storage,              12_000, PiProcessorTier.None),
            [PadType]          = new(PadType,          PiPinKind.Launchpad,            10_000, PiProcessorTier.None),
            [SmallStorageType] = new(SmallStorageType, PiPinKind.Storage,              100,    PiProcessorTier.None),
            [SmallPadType]     = new(SmallPadType,     PiPinKind.Launchpad,            19.5,   PiProcessorTier.None),  // 102 P1
        },
        Commodities = new Dictionary<int, PiCommodity>
        {
            [RawA]  = new(RawA,  PiTier.P0, 0.005),
            [RawB]  = new(RawB,  PiTier.P0, 0.005),
            [ProdA] = new(ProdA, PiTier.P1, 0.19),
            [ProdB] = new(ProdB, PiTier.P1, 0.19),
        },
        Schematics = new Dictionary<int, PiSchematic>
        {
            [SchemA] = new(SchemA, "Basic A", 1800, [new PiSchematicInput(RawA, 3000)], ProdA, 20),
            [SchemB] = new(SchemB, "Basic B", 1800, [new PiSchematicInput(RawB, 3000)], ProdB, 20),
        },
    };

    var snap    = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
    var install = snap.AddMinutes(-2);
    var stop    = install.AddHours(168);
    DateTimeOffset Tick(int k) => install.AddHours(2).AddMinutes(30.0 * k);   // the grid: first cycle end, then every 30 min

    // Pins 1..10: command center; extractors A and B; storage 1 and 2; factories 1, 2 (B) and
    // 3, 4 (A); the launchpad.
    PiColonyLayout Typical(int storageType = StorageType, int padType = PadType, DateTimeOffset? stopB = null) => new()
    {
        CharacterId = 1, PlanetId = 4_000_001, PlanetType = "barren", LastUpdate = snap, UpgradeLevel = 4,
        Pins =
        [
            Pin(1, CcType),
            Pin(2, EcuType) with { InstallTime = install, ExpiryTime = stop, Extractor = new PiLayoutExtractor(RawA, 7200, 4417, 4) },
            Pin(3, EcuType) with { InstallTime = install, ExpiryTime = stopB ?? stop, Extractor = new PiLayoutExtractor(RawB, 7200, 4552, 4) },
            Pin(4, storageType),
            Pin(5, storageType),
            Pin(6, BasicType) with { SchematicId = SchemB },
            Pin(7, BasicType) with { SchematicId = SchemB },
            Pin(8, BasicType) with { SchematicId = SchemA },
            Pin(9, BasicType) with { SchematicId = SchemA },
            Pin(10, padType),
        ],
        Routes =
        [
            new PiLayoutRoute(1, 2, 4, RawA, 63_600),
            new PiLayoutRoute(2, 3, 5, RawB, 65_544),
            new PiLayoutRoute(3, 4, 8, RawA, 3000),
            new PiLayoutRoute(4, 4, 9, RawA, 3000),
            new PiLayoutRoute(5, 5, 6, RawB, 3000),
            new PiLayoutRoute(6, 5, 7, RawB, 3000),
            new PiLayoutRoute(7, 6, 10, ProdB, 20),
            new PiLayoutRoute(8, 7, 10, ProdB, 20),
            new PiLayoutRoute(9, 8, 10, ProdA, 20),
            new PiLayoutRoute(10, 9, 10, ProdA, 20),
        ],
    };

    // CCP's published extraction formula, written out again: the engine's own copy is what is
    // under test, so it is not called for the expected values.
    static long Published(int q, int cycleSeconds, int i)
    {
        var w     = cycleSeconds / 900.0;
        var t     = (i + 0.5) * w;
        var decay = q / (1 + t * 0.012);
        var phase = Math.Pow(q, 0.7);
        var noise = Math.Max((Math.Cos(phase + t / 12) + Math.Cos(phase / 2 + t * 0.2) + Math.Cos(t * 0.5)) / 3, 0);
        var v     = w * decay * (1 + 0.8 * noise);
        var whole = (long)Math.Floor(v);
        return Math.Max(0, v == whole ? whole - 1 : whole);
    }
    var outA = Enumerable.Range(0, 84).Select(i => Published(4417, 7200, i)).ToArray();
    var outB = Enumerable.Range(0, 84).Select(i => Published(4552, 7200, i)).ToArray();
    // Pinned: computed apart from this program, from the same published formula.
    Check("typical: the yields by the published formula",
          outA.Take(5).SequenceEqual([33_717L, 41_544, 36_738, 35_026, 26_177]) && outA[^1] == 3_919 && outA.Sum() == 924_747
          && outB.Take(5).SequenceEqual([47_848L, 44_340, 29_367, 31_627, 31_291]) && outB[^1] == 4_887 && outB.Sum() == 957_027,
          $"A {string.Join(",", outA.Take(5))} … {outA.Sum()}; B {string.Join(",", outB.Take(5))} … {outB.Sum()}");

    var chains = new[]
    {
        (Name: "A", Extractor: 2L, Out: outA, Storage: 4L, Factories: new[] { 8L, 9L }, Raw: RawA, Prod: ProdA),
        (Name: "B", Extractor: 3L, Out: outB, Storage: 5L, Factories: new[] { 6L, 7L }, Raw: RawB, Prod: ProdB),
    };

    // ── The colony hour by hour: +3 h and +30 h ──
    foreach (var hours in new[] { 3, 30 })
    {
        var at   = snap.AddHours(hours);
        var f    = PiEngine.Forecast(Typical(), sdx, at);
        var done = (int)((at - install).TotalSeconds / 7200);
        Check($"typical +{hours}h: whole cycles since install", done == (hours == 3 ? 1 : 15), $"{done}");

        foreach (var c in chains)
        {
            var label = $"typical +{hours}h, chain {c.Name}";
            var x = f.Extractors.Single(e => e.PinId == c.Extractor);
            Check($"{label}: extractor cycles done", x.CyclesDone == done && x.TotalCycles == 84, $"{x.CyclesDone}/{x.TotalCycles}");
            Check($"{label}: extractor output so far", x.OutputDone == c.Out.Take(done).Sum(), $"{x.OutputDone} vs {c.Out.Take(done).Sum()}");

            var m = ChainModel.Run(c.Out, install, snap, at, Room, snap, stop);
            var facs = c.Factories.Select(id => f.Factories.Single(p => p.PinId == id)).ToList();
            Check($"{label}: each factory's cycles", facs[0].CyclesCompleted == m.Cycles[0] && facs[1].CyclesCompleted == m.Cycles[1],
                  $"{facs[0].CyclesCompleted}, {facs[1].CyclesCompleted} vs {m.Cycles[0]}, {m.Cycles[1]}");
            var stored = f.Storage.Single(s => s.PinId == c.Storage).ContentsAt.GetValueOrDefault(c.Raw);
            Check($"{label}: raw left in storage", stored == m.Storage, $"{stored} vs {m.Storage}");

            // Conservation: everything extracted is in storage, in a factory's hopper, or was used
            // — 3,000 for every cycle started.
            var starts  = facs.Sum(p => p.CyclesCompleted + (p.StateAt == PiFactoryState.Running ? 1 : 0));
            var hoppers = facs.Sum(p => p.BufferAt.GetValueOrDefault(c.Raw));
            Check($"{label}: raw conserved", stored + hoppers + 3000L * starts == x.OutputDone && starts == m.Starts.Sum(),
                  $"{stored} + {hoppers} + 3000 × {starts} vs {x.OutputDone}");
            var onPad = f.Storage.Single(s => s.PinId == 10).ContentsAt.GetValueOrDefault(c.Prod);
            Check($"{label}: P1 on the launchpad is 20 per cycle of both factories",
                  onPad == 20L * (facs[0].CyclesCompleted + facs[1].CyclesCompleted) && onPad == m.Made, $"{onPad} vs {m.Made}");
        }
        Check($"typical +{hours}h: nothing destroyed", f.LostAt.Count == 0, string.Join(",", f.LostAt));
    }

    // By hand at +3 h: the first cycle lands at +1 h 58 m; both factories start then, again at
    // +2 h 28 m and +2 h 58 m — two cycles finished each, six sets of 3,000 drawn.
    {
        var f = PiEngine.Forecast(Typical(), sdx, snap.AddHours(3));
        Check("typical +3h by hand: two cycles per factory",
              f.Factories.All(p => p.CyclesCompleted == 2 && p.StateAt == PiFactoryState.Running));
        Check("typical +3h by hand: storage holds the first cycle less six sets",
              f.Storage.Single(s => s.PinId == 4).ContentsAt.GetValueOrDefault(RawA) == 33_717 - 6 * 3000
              && f.Storage.Single(s => s.PinId == 5).ContentsAt.GetValueOrDefault(RawB) == 47_848 - 6 * 3000);
        Check("typical +3h by hand: 80 of each P1 on the launchpad",
              f.Storage.Single(s => s.PinId == 10).ContentsAt is var pad && pad.GetValueOrDefault(ProdA) == 80 && pad.GetValueOrDefault(ProdB) == 80);
    }

    // ── The period: until the extractors stop ──
    var rate   = new PiChargeRate(0.10, PiChargeKind.CustomsOffice);    // P1 export 40 a unit
    var prices = new Dictionary<int, double> { [RawA] = 3, [RawB] = 4, [ProdA] = 450, [ProdB] = 520 };
    {
        var f   = PiEngine.Forecast(Typical(), sdx, snap, rate);
        var per = f.Period;
        var w   = (stop - snap).TotalSeconds;
        Check("typical period: from now until the extractors stop", per.UntilExtractorsStop && per.From == snap && per.To == stop,
              $"{per.From} – {per.To}, until stop {per.UntilExtractorsStop}");
        Check("typical period: the colony exports its P1 and nothing else",
              per.ExportTypes.Count == 2 && per.ExportTypes.Contains(ProdA) && per.ExportTypes.Contains(ProdB),
              string.Join(",", per.ExportTypes));
        Check("typical period: nothing destroyed, nothing brought in", per.Losses.Count == 0 && per.InputsConsumed.Count == 0);

        var money = PiPeriodEconomics.For(f, prices);
        var totalUnits = 0.0;
        double expectShort = 0, expectPotential = 0, expectForecast = 0;
        foreach (var c in chains)
        {
            var label = $"typical period, chain {c.Name}";
            var price = prices[c.Prod];
            var m = ChainModel.Run(c.Out, install, snap, stop, Room, snap, stop);

            // Potential: every cycle of the program ends in the period; 3,000 raw make 20 P1, and
            // the factories can take more than the extractor gives, so all of it becomes P1.
            var potential = c.Out.Sum() / 150.0;
            var forecast  = 20L * m.CyclesInPeriod.Sum();
            Check($"{label}: potential units", Near(money.ByType[c.Prod].Potential, potential),
                  $"{money.ByType[c.Prod].Potential} vs {potential}");
            Check($"{label}: forecast units are what reached the launchpad", per.OutputUnits[c.Prod] == forecast
                  && per.Delivered.GetValueOrDefault(c.Prod) == forecast, $"{per.OutputUnits[c.Prod]} vs {forecast}");
            Check($"{label}: all the raw that landed in storage", per.Delivered.GetValueOrDefault(c.Raw) == c.Out.Sum()
                  && per.TakenFromStorage.GetValueOrDefault(c.Raw) == c.Out.Sum() - m.Storage,
                  $"{per.Delivered.GetValueOrDefault(c.Raw)} in, {per.TakenFromStorage.GetValueOrDefault(c.Raw)} out");

            // Idle: the factories wait for the extractor, mostly by design — the potential runs
            // them at the share of full rate the extraction allows (a day's supply over 288,000).
            var share = c.Out.Sum() / (w / 86_400) / 288_000;
            var idle  = per.Idle.Single(i => i.OutputTypeId == c.Prod);
            var expected = 2 * (1 - share) * w;
            var missed   = (long)Math.Floor(Math.Max(0, m.IdleSeconds - expected) / 1800 * 20 + 0.5);
            Check($"{label}: idle time, both factories", idle.Factories == 2 && Near(idle.IdleSeconds, m.IdleSeconds),
                  $"{idle.IdleSeconds} vs {m.IdleSeconds}");
            Check($"{label}: idle the supply explains", Near(idle.ExpectedIdleSeconds, expected), $"{idle.ExpectedIdleSeconds} vs {expected}");
            Check($"{label}: output idle factories did not make", idle.MissedUnits == missed && idle.IsFinal, $"{idle.MissedUnits} vs {missed}");
            // How the numbers add up: nothing is destroyed, so the shortfall in units is the idle
            // output — here the raw of the last cycles, landing as the period closes.
            Check($"{label}: potential − forecast = idle output", Math.Abs(potential - forecast - idle.MissedUnits) <= 1,
                  $"{potential} − {forecast} vs {idle.MissedUnits}");

            totalUnits      += potential;
            expectPotential += potential * (price - 40);
            expectForecast  += forecast * (price - 40);
            expectShort     += missed * (price - 40);
        }
        Check("typical period: potential profit", Near(money.PotentialProfit, expectPotential), $"{money.PotentialProfit} vs {expectPotential}");
        Check("typical period: forecast profit", Near(money.ForecastProfit, expectForecast), $"{money.ForecastProfit} vs {expectForecast}");
        Check("typical period: efficiency", money.Efficiency is { } e && Near(e, expectForecast / expectPotential), $"{money.Efficiency}");
        Check("typical period: the shortfall is the idle output less its charges",
              Math.Abs(money.Shortfall - expectShort) <= 520, $"{money.Shortfall} vs {expectShort}");
        Check("typical period: nothing destroyed in the money either", money.Destroyed.Count == 0 && money.RawOverflow.Count == 0
              && money.PotentialUnits == (long)Math.Round(totalUnits));
        Check("typical period: a healthy colony is OK", PiColonyAttention.For(f, PiThresholds.Default) is { State: PiColonyState.Ok, OutputDestroyed: false, RawOverflow: false });

        // Later in the program the potential is what is still to come, not the program's average:
        // from +30 h, the cycles from the sixteenth on.
        var later = PiEngine.Forecast(Typical(), sdx, snap.AddHours(30), rate);
        Check("typical period from +30h: the potential is the cycles still to come",
              Near(PiPeriodEconomics.For(later, prices).ByType[ProdA].Potential, outA.Skip(15).Sum() / 150.0),
              $"{PiPeriodEconomics.For(later, prices).ByType[ProdA].Potential} vs {outA.Skip(15).Sum() / 150.0}");

        // Extractors stopping at different times: the colony makes something until the last stops.
        var staggered = PiEngine.Forecast(Typical(stopB: install.AddHours(120)), sdx, snap);
        Check("typical period: ends when the last extractor stops", staggered.Period.To == stop, $"{staggered.Period.To}");

        var stopped = PiEngine.Forecast(Typical(), sdx, stop.AddHours(1)).Period;
        Check("typical period: stopped extractors leave nothing to forecast", stopped.IsEmpty && stopped.UntilExtractorsStop
              && stopped.Losses.Count == 0 && stopped.Delivered.Count == 0);

        // The factories use up all the raw at their share of full rate: never an output, at any
        // moment of the program — however the doubles of "supply less what was used" round.
        var rawAsOutput = Enumerable.Range(0, 168)
            .Select(h => PiEngine.Forecast(Typical(), sdx, snap.AddHours(h).AddMinutes(17 * h % 60)))
            .Where(x => x.Period.ExportTypes.Contains(RawA) || x.Period.ExportTypes.Contains(RawB)
                     || PiHauls.OutputTypes(x).Contains(RawA) || PiHauls.OutputTypes(x).Contains(RawB))
            .Select(x => x.At)
            .ToList();
        Check("typical period: raw is never an output", rawAsOutput.Count == 0,
              $"{rawAsOutput.Count} moments, first {rawAsOutput.FirstOrDefault()}");
    }

    // ── A full launchpad: the factories keep running and taking input; P1 is destroyed ──
    {
        var at     = snap.AddHours(30);
        var normal = PiEngine.Forecast(Typical(), sdx, at);
        var small  = PiEngine.Forecast(Typical(padType: SmallPadType), sdx, at);
        Check("full launchpad: every factory runs exactly as with room",
              small.Factories.Zip(normal.Factories).All(p => p.First.CyclesCompleted == p.Second.CyclesCompleted
                                                         && p.First.StateAt == p.Second.StateAt)
              && small.Factories.Sum(p => p.CyclesCompleted) > 20,
              string.Join(",", small.Factories.Select(p => p.CyclesCompleted)));
        Check("full launchpad: the factories keep drawing raw",
              chains.All(c => small.Storage.Single(s => s.PinId == c.Storage).ContentsAt.GetValueOrDefault(c.Raw)
                           == normal.Storage.Single(s => s.PinId == c.Storage).ContentsAt.GetValueOrDefault(c.Raw)));
        var held = small.Storage.Single(s => s.PinId == 10).ContentsAt.Values.Sum();
        var made = 20L * small.Factories.Sum(p => p.CyclesCompleted);
        Check("full launchpad: holds what fits", held == 102, $"{held}");
        Check("full launchpad: P1 held + destroyed = P1 made",
              held + small.LostAt.GetValueOrDefault(ProdA) + small.LostAt.GetValueOrDefault(ProdB) == made,
              $"{held} + {small.LostAt.GetValueOrDefault(ProdA)} + {small.LostAt.GetValueOrDefault(ProdB)} vs {made}");

        // The period: room for 80 after the first round of cycles, 22 more in the second — the
        // first loss at the second round, for both products, charged to the launchpad.
        var f   = PiEngine.Forecast(Typical(padType: SmallPadType), sdx, snap, rate);
        var per = f.Period;
        var madeInPeriod = 20L * chains.Sum(c => ChainModel.Run(c.Out, install, snap, stop, Room, snap, stop).CyclesInPeriod.Sum());
        Check("full launchpad period: destroyed P1, at the launchpad, from the second round",
              per.Losses.Count == 2 && per.Losses.All(l => l is { Kind: PiLossKind.Product, PinId: 10, PinKind: PiPinKind.Launchpad, NoRoute: false }
                                                         && l.Since == Tick(2) && l.PinTypeId == SmallPadType),
              string.Join("; ", per.Losses.Select(l => $"{l.TypeId}@{l.PinId} {l.Units} from {l.Since}")));
        Check("full launchpad period: destroyed = made − what fits", per.Losses.Sum(l => l.Units) == madeInPeriod - 102,
              $"{per.Losses.Sum(l => l.Units)} vs {madeInPeriod - 102}");
        var money = PiPeriodEconomics.For(f, prices);
        Check("full launchpad period: destroyed valued at market",
              Near(money.DestroyedValue, per.Losses.Sum(l => l.Units * prices[l.TypeId])) && money.ForecastUnits == 102,
              $"{money.DestroyedValue}, forecast {money.ForecastUnits}");
        var a = PiColonyAttention.For(f, PiThresholds.Default);
        Check("full launchpad: the status says output will be destroyed, and from when",
              a.OutputDestroyedFrom == Tick(2) && !a.RawOverflow && a.State != PiColonyState.Ok, $"{a.OutputDestroyedFrom}");
    }

    // ── Raw overflow: storage too small for the first, richest cycles ──
    {
        var at = snap.AddHours(30);
        var f  = PiEngine.Forecast(Typical(storageType: SmallStorageType), sdx, at);
        foreach (var c in chains)
        {
            var label = $"raw overflow, chain {c.Name}";
            var m = ChainModel.Run(c.Out, install, snap, at, SmallRoom, snap, stop);
            Check($"{label}: lost when storage is full", f.LostAt.GetValueOrDefault(c.Raw) == m.LostRaw && m.LostRaw > 0,
                  $"{f.LostAt.GetValueOrDefault(c.Raw)} vs {m.LostRaw}");
            var facs   = c.Factories.Select(id => f.Factories.Single(p => p.PinId == id)).ToList();
            var stored = f.Storage.Single(s => s.PinId == c.Storage).ContentsAt.GetValueOrDefault(c.Raw);
            Check($"{label}: factories and storage as by hand", facs[0].CyclesCompleted == m.Cycles[0]
                  && facs[1].CyclesCompleted == m.Cycles[1] && stored == m.Storage,
                  $"{facs[0].CyclesCompleted}, {facs[1].CyclesCompleted}, {stored} vs {m.Cycles[0]}, {m.Cycles[1]}, {m.Storage}");
            var starts  = facs.Sum(p => p.CyclesCompleted + (p.StateAt == PiFactoryState.Running ? 1 : 0));
            var hoppers = facs.Sum(p => p.BufferAt.GetValueOrDefault(c.Raw));
            Check($"{label}: raw conserved, counting what was destroyed",
                  stored + hoppers + 3000L * starts + f.LostAt.GetValueOrDefault(c.Raw) == c.Out.Take(15).Sum());
        }

        var per = PiEngine.Forecast(Typical(storageType: SmallStorageType), sdx, snap, rate).Period;
        foreach (var c in chains)
        {
            var m    = ChainModel.Run(c.Out, install, snap, stop, SmallRoom, snap, stop);
            var loss = per.Losses.SingleOrDefault(l => l.TypeId == c.Raw);
            Check($"raw overflow period, chain {c.Name}: raw lost at its storage from the first cycle",
                  loss is { Kind: PiLossKind.Raw, PinKind: PiPinKind.Storage } && loss.PinId == c.Storage
                  && loss.Since == Tick(0) && loss.Units == m.LostRawInPeriod,
                  loss is null ? "none" : $"{loss.Units} vs {m.LostRawInPeriod} at {loss.PinId} from {loss.Since}");
        }
        Check("raw overflow period: no product lost", per.Losses.All(l => l.Kind == PiLossKind.Raw));
        var a = PiColonyAttention.For(PiEngine.Forecast(Typical(storageType: SmallStorageType), sdx, snap, rate), PiThresholds.Default);
        Check("raw overflow: the status says why", a.RawOverflowFrom == Tick(0) && !a.OutputDestroyed && a.State == PiColonyState.Attention,
              $"{a.RawOverflowFrom} {a.State}");
    }
}

// ── 13. A factory planet's period: next 30 days ─────────────────────────────────────
//
// The factory planet of section 3 from 10 hours on: its storage has room for 50 more Z (full at
// 20 hours), and its Y runs out at 50 hours. Destroyed and idle account for the whole shortfall.
{
    var at  = H(t0, 10);
    var f   = PiEngine.Forecast(FactoryPlanet(), sd, at, new PiChargeRate(0.10, PiChargeKind.CustomsOffice));
    var per = f.Period;
    Check("factory period: the next 30 days", !per.UntilExtractorsStop && per.From == at && per.To == at.AddDays(30),
          $"{per.From} – {per.To}");
    var loss = per.Losses.SingleOrDefault();
    // Completions at 21..50 hours: thirty cycles of 5 Z with nowhere to go.
    Check("factory period: Z destroyed at the full storage from 21 hours",
          loss is { TypeId: Z, PinId: 5, Kind: PiLossKind.Product, Units: 150 } && loss.Since == H(t0, 21),
          loss is null ? "none" : $"{loss.TypeId}@{loss.PinId} {loss.Units} from {loss.Since}");
    var idle = per.Idle.Single();
    // Waiting from 50 hours to the end, 730 hours: 680 hours, at 5 Z an hour.
    Check("factory period: idle once Y is gone", Near(idle.IdleSeconds, 680 * 3600.0) && idle.ExpectedIdleSeconds == 0
          && idle.MissedUnits == 3400, $"{idle.IdleSeconds / 3600} h, expected {idle.ExpectedIdleSeconds}, missed {idle.MissedUnits}");
    // Cycles started after the period opens: 11..49 hours.
    Check("factory period: brought-in input used", per.InputsConsumed.GetValueOrDefault(X) == 39 * 40
          && per.InputsConsumed.GetValueOrDefault(Y) == 39 * 40, string.Join(",", per.InputsConsumed));
    Check("factory period: Z kept is what storage had room for", per.OutputUnits.GetValueOrDefault(Z) == 50);

    var money = PiPeriodEconomics.For(f, new Dictionary<int, double> { [X] = 100, [Y] = 150, [Z] = 20_000 });
    Check("factory period: potential is 30 days of the steady day",
          Near(money.PotentialProfit, 30 * (2_400_000 - 240_000 - 86_400 - 38_400.0)), $"{money.PotentialProfit}");
    // 50 Z at 20,000 less 720 export; 1,560 each of X (100) and Y (150), each plus a 20 import charge.
    Check("factory period: forecast", Near(money.ForecastProfit, 50 * (20_000 - 720) - 1560 * (100 + 20) - 1560 * (150 + 20.0)),
          $"{money.ForecastProfit}");
    Check("factory period: potential − forecast in units = destroyed + idle",
          Near(money.ByType[Z].Potential - money.ByType[Z].Forecast, (loss?.Units ?? 0) + idle.MissedUnits),
          $"{money.ByType[Z].Potential} − {money.ByType[Z].Forecast} vs {loss?.Units} + {idle.MissedUnits}");
    Check("factory period: the status says output will be destroyed", PiColonyAttention.For(f, PiThresholds.Default).OutputDestroyedFrom == H(t0, 21));
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

/// <summary>
/// One raw → P1 chain of the typical extractor planet, by hand: an extractor's cycles into one
/// storage, two factories drawing 3,000 from it in route order and making 20 P1 in 30 minutes.
/// A half-hour clock, not an event queue, so it checks the engine rather than repeating it.
/// Within a tick: the extractor's cycle lands, then cycles finish, then idle factories draw and
/// start — the order the engine documents.
/// </summary>
sealed class ChainModel
{
    public long Extracted, Storage, LostRaw, LostRawInPeriod, Made;
    public readonly long[] Buffer = new long[2];
    public readonly int[]  Cycles = new int[2], Starts = new int[2], CyclesInPeriod = new int[2];
    public readonly bool[] Running = new bool[2];
    /// <summary>Both factories' waiting for input within the period, in seconds.</summary>
    public double IdleSeconds;

    /// <param name="room">Units of raw the storage holds.</param>
    /// <param name="until">The last moment run; the period's idle is only complete when this is
    /// the period's end or later.</param>
    public static ChainModel Run(long[] outputs, DateTimeOffset install, DateTimeOffset snapshot, DateTimeOffset until,
                                 long room, DateTimeOffset periodFrom, DateTimeOffset periodTo)
    {
        var m = new ChainModel();
        DateTimeOffset?[] idleSince = [snapshot, snapshot];   // empty at the snapshot: waiting from then

        bool InPeriod(DateTimeOffset t) => t > periodFrom && t <= periodTo;
        void Idle(DateTimeOffset a, DateTimeOffset b)
        {
            var from = a > periodFrom ? a : periodFrom;
            var to   = b < periodTo ? b : periodTo;
            if (to > from) m.IdleSeconds += (to - from).TotalSeconds;
        }

        for (var k = 0; ; k++)
        {
            var t = install.AddHours(2).AddMinutes(30.0 * k);
            if (t > until) break;

            if (k % 4 == 0 && k / 4 < outputs.Length)
            {
                var o    = outputs[k / 4];
                var take = Math.Min(o, room - m.Storage);
                m.Extracted += o;
                m.Storage   += take;
                m.LostRaw   += o - take;
                if (InPeriod(t)) m.LostRawInPeriod += o - take;
            }

            for (var i = 0; i < 2; i++)
                if (m.Running[i])
                {
                    m.Running[i] = false;
                    m.Cycles[i]++;
                    m.Made += 20;
                    if (InPeriod(t)) m.CyclesInPeriod[i]++;
                }

            for (var i = 0; i < 2; i++)
            {
                var take = Math.Min(3000 - m.Buffer[i], m.Storage);
                m.Storage   -= take;
                m.Buffer[i] += take;
                if (m.Buffer[i] < 3000) { idleSince[i] ??= t; continue; }
                m.Buffer[i]  = 0;
                m.Running[i] = true;
                m.Starts[i]++;
                if (idleSince[i] is { } since) Idle(since, t);
                idleSince[i] = null;
            }
        }

        for (var i = 0; i < 2; i++)
            if (idleSince[i] is { } since) Idle(since, periodTo);
        return m;
    }
}

/// <summary>A context per call over the throwaway database, as the app's factory gives them.</summary>
sealed class CheckDbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);
}
