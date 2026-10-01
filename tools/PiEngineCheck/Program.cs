using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Pi;
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
//  learned from constructed journal entries, in memory and through a throwaway SQLite database.
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
