using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Battle;
using Microsoft.EntityFrameworkCore;

// ─────────────────────────────────────────────────────────────────────────────
//  Battle report check
//
//  Are the sides, the sums and the live reading of a battle report right?
//
//  WHY. Nothing here fails loudly. A report with two alliances on the wrong side, a fleet's damage
//  to a citadel read as damage to ships, or a late-arriving mail that a live report never picks up
//  all look like a finished report. So this builds battles whose answers are known, on constructed
//  kills and on a throwaway database.
//
//  Ids below are invented, in the ranges EVE uses: alliances 99xxxxxxx, corporations 98xxxxxxx, NPC
//  corporations 1xxxxxx, pilots 90xxxxxxx.
// ─────────────────────────────────────────────────────────────────────────────

// ⚠️ SQLite, pinned, before anything reads the engine: the database section must never fall through
// to a developer machine's PostgreSQL config and touch a real server.
DbEngine.Pin(DbBackend.Sqlite);

var failures = 0;
void Check(string what, bool ok, string detail = "")
{
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}{(ok || detail.Length == 0 ? "" : "  -> " + detail)}");
}

const long AllianceA = 99_000_001, AllianceB = 99_000_002, AllianceC = 99_000_003, AllianceD = 99_000_004;
const long CorpA = 98_000_001, CorpB = 98_000_002, CorpC = 98_000_003, CorpD = 98_000_004, CorpLone = 98_000_005;
const long StarterCorp = 1_000_100;
const int Hull = 587, Pod = 670, Citadel = 35832;
const int System1 = 30_000_142;

var clock = new DateTimeOffset(2026, 1, 15, 18, 0, 0, TimeSpan.Zero);
var nextKill = 1000;

BattleAttacker Shooter(long pilot, long corp, long alliance, int damage = 100, bool final = false, int ship = Hull) =>
    new(pilot, corp, alliance, damage, final, ship);

BattleKill Kill(long pilot, long corp, long alliance, double value, BattleHullClass cls, int ship, params BattleAttacker[] attackers) =>
    new(nextKill++, clock = clock.AddSeconds(30), System1, pilot, corp, alliance, ship, cls, value, attackers);

BattleKill ShipKill(long pilot, long corp, long alliance, double value, params BattleAttacker[] attackers) =>
    Kill(pilot, corp, alliance, value, BattleHullClass.Ship, Hull, attackers);

BattleReportEngine NewEngine(long[]? ourChars = null, long[]? ourCorps = null) => new(ourChars ?? [], ourCorps ?? []);

int TeamOf(BattleSnapshot s, long side) => s.Sides.FirstOrDefault(r => r.SideId == side)?.Team ?? -1;

// A and C fight B and D, with A and C sharing targets, and B and D too.
List<BattleKill> TwoFleets() =>
[
    ShipKill(90_000_201, CorpB, AllianceB, 1000, Shooter(90_000_101, CorpA, AllianceA), Shooter(90_000_301, CorpC, AllianceC)),
    ShipKill(90_000_401, CorpD, AllianceD, 2000, Shooter(90_000_102, CorpA, AllianceA), Shooter(90_000_302, CorpC, AllianceC)),
    ShipKill(90_000_101, CorpA, AllianceA, 3000, Shooter(90_000_202, CorpB, AllianceB), Shooter(90_000_402, CorpD, AllianceD)),
    ShipKill(90_000_301, CorpC, AllianceC, 4000, Shooter(90_000_203, CorpB, AllianceB), Shooter(90_000_403, CorpD, AllianceD)),
];

// ── Who is on which side ─────────────────────────────────────────────────

{
    var engine = NewEngine();
    engine.Add(TwoFleets());
    var s = engine.Build();
    Check("allies on one attacker list share a team", TeamOf(s, AllianceA) == TeamOf(s, AllianceC) && TeamOf(s, AllianceB) == TeamOf(s, AllianceD));
    Check("the two fleets are on opposite teams", TeamOf(s, AllianceA) != TeamOf(s, AllianceB));
}

{
    var forward = NewEngine();
    forward.Add(TwoFleets());
    var backward = NewEngine();
    backward.Add(Enumerable.Reverse(TwoFleets()));
    var (f, b) = (forward.Build(), backward.Build());
    Check("the sides do not depend on the order kills arrive in",
        (TeamOf(f, AllianceA) == TeamOf(f, AllianceC)) == (TeamOf(b, AllianceA) == TeamOf(b, AllianceC))
        && TeamOf(b, AllianceA) != TeamOf(b, AllianceB));
}

{
    // Their own pilots lead team A, even when they are the smaller side.
    var engine = NewEngine(ourChars: [90_000_401]);
    engine.Add(TwoFleets());
    var s = engine.Build();
    Check("the user's own side is team A", TeamOf(s, AllianceD) == 0, $"team {TeamOf(s, AllianceD)}");
    Check("and is marked as theirs", s.Teams[0].IsOurs && !s.Teams[1].IsOurs);
}

{
    // A rat on the mail is on nobody's side; two starter-corporation pilots are not one side.
    var engine = NewEngine();
    engine.Add(
    [
        ShipKill(90_000_501, StarterCorp, 0, 100, Shooter(0, 1_000_125, 0), Shooter(90_000_502, StarterCorp, 0)),
    ]);
    var s = engine.Build();
    Check("a rat has no side", s.Sides.All(r => r.SideId != 1_000_125));
    Check("starter-corporation pilots stand alone",
        s.Sides.Count == 2 && s.Sides.All(r => r.Kind == BattleSideKind.Pilot) && TeamOf(s, 90_000_501) != TeamOf(s, 90_000_502));
}

{
    // A corporation with no alliance is its own side.
    var engine = NewEngine();
    engine.Add([ShipKill(90_000_101, CorpA, AllianceA, 100, Shooter(90_000_601, CorpLone, 0))]);
    var s = engine.Build();
    Check("a corporation with no alliance is a side", s.Sides.Any(r => r.SideId == CorpLone && r.Kind == BattleSideKind.Corporation));
}

{
    // A side that has shot at both fleets, as much at one as the other: who it fought beside decides.
    var engine = NewEngine(ourChars: [90_000_201]);
    var kills = new List<BattleKill>();
    for (var i = 0; i < 4; i++)
    {
        kills.Add(ShipKill(90_000_201, CorpB, AllianceB, 100, Shooter(90_000_101, CorpA, AllianceA), Shooter(90_000_301, CorpC, AllianceC)));
        kills.Add(ShipKill(90_000_102, CorpA, AllianceA, 100, Shooter(90_000_302, CorpC, AllianceC)));
    }
    engine.Add(kills);
    var s = engine.Build();
    Check("a side that shot both goes with those it fought beside", TeamOf(s, AllianceC) == TeamOf(s, AllianceA) && TeamOf(s, AllianceA) != TeamOf(s, AllianceB),
        $"A {TeamOf(s, AllianceA)}, B {TeamOf(s, AllianceB)}, C {TeamOf(s, AllianceC)}");
}

// ── Live: kills keep arriving ────────────────────────────────────────────

{
    // An early guess is put right by later kills. C appears alone, killed by nobody it can be
    // placed against, and only later shows whose side it is on.
    var engine = NewEngine();
    engine.Add([ShipKill(90_000_201, CorpB, AllianceB, 100, Shooter(90_000_101, CorpA, AllianceA))]);
    engine.Add([ShipKill(90_000_301, CorpC, AllianceC, 100, Shooter(90_000_701, 98_000_007, 0))]);
    for (var i = 0; i < 4; i++)
        engine.Add([ShipKill(90_000_201, CorpB, AllianceB, 100, Shooter(90_000_301, CorpC, AllianceC), Shooter(90_000_101, CorpA, AllianceA))]);
    var s = engine.Build();
    Check("later kills correct an early guess", TeamOf(s, AllianceC) == TeamOf(s, AllianceA) && TeamOf(s, AllianceC) != TeamOf(s, AllianceB));
}

{
    var engine = NewEngine();
    engine.Add(TwoFleets());
    var before = engine.Build();
    Check("a kill already held changes nothing", !engine.Add([TwoFleets()[0] with { KillMailId = before.Kills[0].KillMailId }])
        && engine.Build().Kills.Count == before.Kills.Count);
    engine.Add(TwoFleets());
    var after = engine.Build();
    Check("new kills add to the totals", after.Kills.Count == before.Kills.Count + 4 && after.TotalIsk == before.TotalIsk * 2);
    Check("and the teams stay where they were",
        TeamOf(after, AllianceA) == TeamOf(before, AllianceA) && TeamOf(after, AllianceB) == TeamOf(before, AllianceB));
    Check("snapshots are numbered, so an older one can be told apart", after.Version > before.Version);
}

// ── Moving a side by hand ────────────────────────────────────────────────

{
    var engine = NewEngine();
    engine.Add(TwoFleets());
    var start = engine.Build();
    var other = 1 - TeamOf(start, AllianceC);
    engine.Move(AllianceC, other);
    var moved = engine.Build();
    Check("a moved side is on the team it was moved to", TeamOf(moved, AllianceC) == other);
    Check("its pilots and their losses go with it",
        moved.Pilots.Where(p => p.AllianceId == AllianceC).All(p => p.Team == other)
        && moved.Teams[other].IskLost > start.Teams[other].IskLost);

    // More evidence against it does not undo the move, nor shuffle anyone else.
    for (var i = 0; i < 5; i++)
        engine.Add([ShipKill(90_000_201, CorpB, AllianceB, 100, Shooter(90_000_301, CorpC, AllianceC), Shooter(90_000_101, CorpA, AllianceA))]);
    var later = engine.Build();
    Check("a moved side stays moved as kills arrive", TeamOf(later, AllianceC) == other);
    Check("the other sides present stay too", TeamOf(later, AllianceA) == TeamOf(start, AllianceA) && TeamOf(later, AllianceD) == TeamOf(start, AllianceD));

    // A newcomer is still placed by what it did.
    engine.Add([ShipKill(90_000_101, CorpA, AllianceA, 100, Shooter(90_000_801, 98_000_008, 0))]);
    var joined = engine.Build();
    Check("a side arriving after a move is placed by what it did", TeamOf(joined, 98_000_008) != TeamOf(joined, AllianceA));

    Check("the moved side is kept for a saved report", engine.Moves[AllianceC] == other);
}

{
    // ⚠️ Read back, the whole arrangement must be as it was left, the user's side still leading. Move
    // a friend across: worked out afresh around it, the rest would follow it over.
    var engine = NewEngine(ourChars: [90_000_101]);
    engine.Add(TwoFleets());
    var before = engine.Build();
    Check("no moves, nothing to keep", engine.Moves.Count == 0);
    engine.Move(AllianceC, 1 - TeamOf(before, AllianceC));
    var left = engine.Build();

    var restored = NewEngine(ourChars: [90_000_101]);
    restored.Restore(engine.TeamCount, engine.Moves);
    restored.Add(TwoFleets());
    var back = restored.Build();
    Check("a saved arrangement comes back side for side",
        new[] { AllianceA, AllianceB, AllianceC, AllianceD }.All(s => TeamOf(back, s) == TeamOf(left, s)),
        string.Join(",", new[] { AllianceA, AllianceB, AllianceC, AllianceD }.Select(s => TeamOf(back, s))));
    Check("with the user's side where it was", TeamOf(back, AllianceA) == 0);
}

{
    var engine = NewEngine();
    engine.Add(TwoFleets());
    engine.Move(AllianceA, 1 - TeamOf(engine.Build(), AllianceA));
    var moves = engine.Moves.ToDictionary(kv => kv.Key, kv => kv.Value);

    var restored = NewEngine();
    restored.Restore(engine.TeamCount, moves);
    restored.Add(TwoFleets());
    Check("a saved move is put back before its kills are read", TeamOf(restored.Build(), AllianceA) == moves[AllianceA]);
}

{
    var engine = NewEngine();
    engine.Add(TwoFleets());
    Check("two teams to begin with", engine.Build().Teams.Count == 2);
    engine.AddTeam();
    var three = engine.Build();
    Check("a team added is empty, and nobody is put in it unasked", three.Teams.Count == 3 && three.Sides.All(r => r.Team < 2));

    // A side turning up with nothing to place it by (killed by a rat) still goes to a team in use.
    engine.Add([ShipKill(90_000_901, 98_000_009, 0, 100, Shooter(0, 1_000_125, 0))]);
    Check("nor a newcomer with nothing to place it by", TeamOf(engine.Build(), 98_000_009) is 0 or 1, $"team {TeamOf(engine.Build(), 98_000_009)}");

    engine.Move(AllianceC, 2);
    var s = engine.Build();
    Check("a side can be moved to it", TeamOf(s, AllianceC) == 2 && s.Teams[2].Pilots > 0);
    Check("a team with a side in it is not removed", !engine.CanRemoveTeam(2) && !engine.RemoveTeam(2) && engine.Build().Teams.Count == 3);
    engine.Move(AllianceC, 0);
    Check("an empty one is", engine.CanRemoveTeam(2) && engine.RemoveTeam(2) && engine.Build().Teams.Count == 2);
    Check("two teams always stay, even with one empty", !engine.RemoveTeam(0) && !engine.RemoveTeam(1));
    engine.Move(AllianceC, 7);
    Check("a team that does not exist is refused", TeamOf(engine.Build(), AllianceC) == 0);
}

{
    // A middle team emptied: later teams move down a letter, their sides and the moves with them.
    var engine = NewEngine();
    engine.Add(TwoFleets());
    engine.AddTeam();
    engine.AddTeam();
    engine.Move(AllianceC, 2);
    engine.Move(AllianceD, 3);
    Check("a middle team with a side on it cannot go", !engine.RemoveTeam(2));
    engine.Move(AllianceC, 0);
    Check("a middle team once empty can go", engine.RemoveTeam(2));
    var s = engine.Build();
    Check("later teams move down a letter, sides and all", s.Teams.Count == 3 && TeamOf(s, AllianceD) == 2 && s.Teams[2].Pilots > 0, $"{TeamOf(s, AllianceD)}");
    Check("and so do the moves kept", engine.Moves[AllianceD] == 2);
    Check("the sides on earlier teams stay", TeamOf(s, AllianceC) == 0);

    // A saved move naming a side no longer on any kill does not hold an empty team open.
    var restored = NewEngine();
    restored.Restore(3, new Dictionary<long, int> { [AllianceB] = 2 });
    restored.Add([ShipKill(90_000_101, CorpA, AllianceA, 100, Shooter(90_000_301, CorpC, AllianceC))]);
    Check("a team held only by a side not on any kill counts as empty", restored.CanRemoveTeam(2) && restored.RemoveTeam(2));
    Check("and that move goes with it", restored.Moves.Count == 0);
}

// ── The sums ─────────────────────────────────────────────────────────────

{
    var engine = NewEngine();
    engine.Add(
    [
        // A loses a ship and a pod to B; B loses a citadel to A; and A shoots its own.
        Kill(90_000_101, CorpA, AllianceA, 300, BattleHullClass.Ship, Hull,
            Shooter(90_000_201, CorpB, AllianceB, 500, final: true), Shooter(90_000_202, CorpB, AllianceB, 200)),
        Kill(90_000_101, CorpA, AllianceA, 100, BattleHullClass.Pod, Pod, Shooter(90_000_201, CorpB, AllianceB, 10, final: true)),
        Kill(0, CorpB, AllianceB, 600, BattleHullClass.Structure, Citadel,
            Shooter(90_000_102, CorpA, AllianceA, 90_000, final: true)),
        Kill(90_000_103, CorpA, AllianceA, 50, BattleHullClass.Ship, Hull, Shooter(90_000_102, CorpA, AllianceA, 40, final: true)),
    ]);
    var s = engine.Build();
    var teamA = s.Teams[TeamOf(s, AllianceA)];
    var teamB = s.Teams[TeamOf(s, AllianceB)];

    Check("ISK lost adds up per team", teamA.IskLost == 450 && teamB.IskLost == 600, $"{teamA.IskLost} / {teamB.IskLost}");
    Check("efficiency is the share of losses the others took", Math.Abs(teamA.Efficiency - 600.0 / 1050) < 1e-9 && Math.Abs(teamB.Efficiency - 450.0 / 1050) < 1e-9);
    Check("ships, pods and structures are counted apart",
        teamA.ShipsLost == 2 && teamA.PodsLost == 1 && teamA.StructuresLost == 0 && teamB.StructuresLost == 1 && teamB.ShipsLost == 0);
    Check("damage to a structure is not damage to ships", teamA.DamageDealt == 0 && teamA.StructureDamage == 90_000, $"{teamA.DamageDealt} / {teamA.StructureDamage}");
    Check("damage to ships is", teamB.DamageDealt == 710 && teamB.StructureDamage == 0, $"{teamB.DamageDealt}");
    Check("a team's damage to itself is not counted", s.Pilots.First(p => p.CharacterId == 90_000_102).Damage == 90_000);
    Check("pilots are counted once each", teamA.Pilots == 3 && teamB.Pilots == 2 && s.TotalPilots == 5, $"{teamA.Pilots} / {teamB.Pilots} / {s.TotalPilots}");

    var winner = s.Pilots.First(p => p.CharacterId == 90_000_201);
    Check("a pilot's kills and final blows", winner.Kills == 2 && winner.FinalBlows == 2 && winner.Damage == 510);
    Check("a pilot's losses, by hull", s.Pilots.First(p => p.CharacterId == 90_000_101 && p.ShipTypeId == Hull).IskLost == 300
        && s.Pilots.First(p => p.CharacterId == 90_000_101 && p.ShipTypeId == Pod).IskLost == 100);
    Check("a structure's row carries its owner", s.Pilots.Any(p => p.CharacterId == 0 && p.CorporationId == CorpB && p.ShipTypeId == Citadel && p.Lost == 1));
    Check("hulls add up per team", s.Ships.First(r => r.Team == teamB.Team && r.ShipTypeId == Hull).Pilots == 2
        && s.Ships.First(r => r.Team == teamA.Team && r.ShipTypeId == Hull).Lost == 2);
    Check("the timeline is newest first", s.Kills[0].Time >= s.Kills[^1].Time && s.LastKill == s.Kills[0].Time && s.FirstKill == s.Kills[^1].Time);
    Check("the total is every loss", s.TotalIsk == 1050);

    var hullKill = s.Kills.First(k => k.ShipTypeId == Hull && k.Value == 300);
    Check("a loss names who landed the last hit", hullKill.FinalBlowCharId == 90_000_201 && hullKill.FinalBlowCorpId == CorpB && hullKill.FinalBlowAllianceId == AllianceB);
    Check("and not whoever is first on the list", s.Kills.First(k => k.Value == 300).FinalBlowCharId != 90_000_202);
}

{
    var engine = NewEngine();
    engine.Add([ShipKill(90_000_101, CorpA, AllianceA, 100, Shooter(90_000_201, CorpB, AllianceB, 500), Shooter(90_000_202, 98_000_022, 99_000_022, 10, final: true))]);
    var row = engine.Build().Kills[0];
    Check("the last hit is the one marked, wherever it is on the list", row.FinalBlowCharId == 90_000_202 && row.FinalBlowCorpId == 98_000_022 && row.FinalBlowAllianceId == 99_000_022);
    engine.Add([ShipKill(90_000_101, CorpA, AllianceA, 100, Shooter(90_000_201, CorpB, AllianceB, 500))]);
    Check("a mail with none marked names nobody", engine.Build().Kills[0].FinalBlowCharId == 0);
}

{
    // ⚠️ Found on a real fight: dozens of mobile tractor units read as structures lost, burying the
    // one structure that really was. They count apart, and their damage is not structure damage.
    var engine = NewEngine();
    engine.Add(
    [
        Kill(0, CorpB, AllianceB, 40, BattleHullClass.Deployable, Hull, Shooter(90_000_101, CorpA, AllianceA, 500, final: true)),
        Kill(0, CorpB, AllianceB, 60, BattleHullClass.Deployable, Hull, Shooter(90_000_101, CorpA, AllianceA, 300, final: true)),
        Kill(0, CorpB, AllianceB, 900, BattleHullClass.Structure, Citadel, Shooter(90_000_102, CorpA, AllianceA, 90_000, final: true)),
    ]);
    var s = engine.Build();
    var (a, b) = (s.Teams[TeamOf(s, AllianceA)], s.Teams[TeamOf(s, AllianceB)]);
    Check("deployables lost count apart from structures", b.DeployablesLost == 2 && b.StructuresLost == 1 && b.ShipsLost == 0, $"{b.DeployablesLost} / {b.StructuresLost} / {b.ShipsLost}");
    Check("their value is still lost ISK", b.IskLost == 1000);
    Check("damage to them is not damage to structures", a.StructureDamage == 90_000 && a.DamageDealt == 800, $"{a.StructureDamage} / {a.DamageDealt}");
}

{
    // ⚠️ A side's kills are ships it was on, not attacker rows: three of its pilots on one mail is one.
    const long CorpA2 = 98_000_012;
    var engine = NewEngine();
    engine.Add(
    [
        Kill(90_000_201, CorpB, AllianceB, 100, BattleHullClass.Ship, Hull,
            Shooter(90_000_101, CorpA, AllianceA, 100), Shooter(90_000_102, CorpA, AllianceA, 100), Shooter(90_000_103, CorpA2, AllianceA, 50)),
        Kill(90_000_202, CorpB, AllianceB, 200, BattleHullClass.Ship, Hull, Shooter(90_000_101, CorpA, AllianceA, 10)),
        Kill(90_000_202, CorpB, AllianceB, 30, BattleHullClass.Pod, Pod, Shooter(90_000_101, CorpA, AllianceA, 10)),
        Kill(0, CorpB, AllianceB, 900, BattleHullClass.Structure, Citadel, Shooter(90_000_103, CorpA2, AllianceA, 7000)),
        Kill(0, CorpB, AllianceB, 40, BattleHullClass.Deployable, Hull, Shooter(90_000_103, CorpA2, AllianceA, 5)),
        Kill(90_000_104, CorpA2, AllianceA, 500, BattleHullClass.Ship, Hull, Shooter(90_000_203, CorpB, AllianceB, 400)),
    ]);
    var s = engine.Build();
    var side = s.Sides.First(r => r.SideId == AllianceA);
    Check("a side's kills are the ships it was on, once each", side.Kills == 2, side.Kills.ToString());
    Check("pods, structures and deployables are not ships", s.Sides.First(r => r.SideId == AllianceB).Kills == 1);

    var corps = s.SideCorps[AllianceA];
    Check("an alliance breaks out by corporation", corps.Select(c => c.SideId).Order().SequenceEqual([CorpA, CorpA2]), string.Join(",", corps.Select(c => c.SideId)));
    var (one, two) = (corps.First(c => c.SideId == CorpA), corps.First(c => c.SideId == CorpA2));
    Check("each corporation's kills are its own", one.Kills == 2 && two.Kills == 1, $"{one.Kills} / {two.Kills}");
    Check("and its pilots", one.Pilots == 2 && two.Pilots == 2, $"{one.Pilots} / {two.Pilots}");
    Check("and its losses", one.ShipsLost == 0 && two.ShipsLost == 1 && two.IskLost == 500);
    Check("and its damage", one.Damage == 220 && two.Damage == 7055, $"{one.Damage} / {two.Damage}");
    Check("the corporations add up to the alliance's pilots, losses and damage",
        corps.Sum(c => c.Pilots) == side.Pilots && corps.Sum(c => c.ShipsLost) == side.ShipsLost && corps.Sum(c => c.Damage) == side.Damage && corps.Sum(c => c.IskLost) == side.IskLost);
    Check("the corporation is on its alliance's team", corps.All(c => c.Team == side.Team));
    Check("a corporation with no alliance has nothing to break out", !s.SideCorps.ContainsKey(CorpLone));

    engine.Move(AllianceA, 1 - side.Team);
    Check("moved with its alliance", engine.Build().SideCorps[AllianceA].All(c => c.Team == 1 - side.Team));
}

{
    // ⚠️ A pilot once, whatever they flew, lost, or were read as flying.
    BattlePilotRow P(long pilot, int ship, long damage, int kills = 0, int lost = 0, double isk = 0, int team = 0, long corp = CorpA) =>
        new(team, pilot, corp, AllianceA, ship, damage, kills, 0, lost, isk, false);
    const int Dromi = 37604, Apoc = 642;
    var families = BattleReportEngine.FamiliesOf(
    [
        P(90_000_101, Apoc, 100, kills: 3, lost: 1, isk: 500),
        P(90_000_101, Pod, 0, lost: 1, isk: 50),
        P(90_000_101, Dromi, 400, kills: 9),
        P(90_000_101, 0, 900, kills: 2),            // a line where the mail did not say what they flew
        P(90_000_102, Pod, 0, lost: 1, isk: 20),    // nothing but a pod
        P(90_000_103, 0, 5, kills: 1),              // nothing but a line with no hull
        P(90_000_104, Apoc, 700, kills: 2),
        P(90_000_104, Apoc, 50, team: 1),           // the same pilot on the other team: two lines
        P(0, Citadel, 0, lost: 1, isk: 900, corp: CorpB),
        P(0, Hull, 0, lost: 1, isk: 9, corp: CorpB), // two structures of one owner: neither one's child
    ]);
    var byPilot = families.Where(f => f.Total.CharacterId > 0).GroupBy(f => (f.Total.Team, f.Total.CharacterId));
    Check("a pilot is listed once on a team", byPilot.All(g => g.Count() == 1));
    Check("on each team they were on", families.Count(f => f.Total.CharacterId == 90_000_104) == 2);
    var many = families.First(f => f.Total.CharacterId == 90_000_101);
    Check("their hulls are behind that one line", many.Hulls.Count == 4);
    Check("the line carries the totals", many.Total.Damage == 1400 && many.Total.Kills == 14 && many.Total.Lost == 2 && many.Total.IskLost == 550, $"{many.Total.Damage} {many.Total.Kills} {many.Total.Lost} {many.Total.IskLost}");
    Check("each hull keeps its own numbers", many.Hulls.First(h => h.ShipTypeId == Apoc) is { Damage: 100, Kills: 3, Lost: 1, IskLost: 500 });
    Check("the hull named is the one they did the most in, not a blank line", many.Total.ShipTypeId == Dromi, many.Total.ShipTypeId.ToString());
    Check("a pod only if that is all there was", families.First(f => f.Total.CharacterId == 90_000_102).Total.ShipTypeId == Pod);
    Check("a pilot with one hull has just that", families.First(f => f.Total.CharacterId == 90_000_103).Hulls.Count == 1);
    Check("two structures of one owner stay two lines", families.Count(f => f.Total.CharacterId == 0) == 2 && families.Where(f => f.Total.CharacterId == 0).All(f => f.Hulls.Count == 1));
    Check("largest total first", families[0].Total.CharacterId == 90_000_101, families[0].Total.CharacterId.ToString());
    Check("a pod's loss is not the hull named over one flown", many.Total.ShipTypeId != Pod);

    var engine = NewEngine();
    engine.Add(TwoFleets());
    var snap = engine.Build();
    Check("the report's families hold every line", snap.PilotFamilies.Sum(f => f.Hulls.Count) == snap.Pilots.Count);
}

{
    // Who flew each hull: alliances, their corporations, and corporations with no alliance.
    BattlePilotRow L(long pilot, long corp, long alliance, int ship, long damage = 0, int lost = 0, double isk = 0, int team = 0) =>
        new(team, pilot, corp, alliance, ship, damage, 0, 0, lost, isk, false);
    const int Other = 11978;
    var flyers = BattleReportEngine.FlyersOf(
    [
        L(1, CorpA, AllianceA, Hull, damage: 100),
        L(2, CorpA, AllianceA, Hull, damage: 50, lost: 1, isk: 500),
        L(3, 98_000_012, AllianceA, Hull, damage: 30),
        L(4, CorpLone, 0, Hull, damage: 900, lost: 2, isk: 70),
        L(5, CorpB, AllianceB, Hull, team: 1, damage: 7),
        L(6, CorpB, AllianceB, Other, team: 1, damage: 8),
        L(7, CorpB, AllianceB, 0, team: 1, damage: 9),     // no hull on the mail: under no hull
    ]);
    var hull = flyers[(0, Hull)];
    Check("a hull lists who flew it, most pilots first", hull.Select(f => f.Id).SequenceEqual([AllianceA, CorpLone]), string.Join(",", hull.Select(f => f.Id)));
    var alliance = hull[0];
    Check("an alliance has its own numbers for that hull", alliance is { Kind: BattleSideKind.Alliance, Pilots: 3, Lost: 1, IskLost: 500, Damage: 180 });
    Check("and its corporations theirs", alliance.Corps.Select(c => (c.Id, c.Pilots, c.Damage)).SequenceEqual([(CorpA, 2, 150L), (98_000_012, 1, 30L)]));
    Check("the corporations add up to the alliance", alliance.Corps.Sum(c => c.Pilots) == alliance.Pilots && alliance.Corps.Sum(c => c.Lost) == alliance.Lost && alliance.Corps.Sum(c => c.IskLost) == alliance.IskLost && alliance.Corps.Sum(c => c.Damage) == alliance.Damage);
    Check("a corporation with no alliance stands on its own, with nothing under it", hull[1] is { Kind: BattleSideKind.Corporation, Pilots: 1, Lost: 2, IskLost: 70, Damage: 900 } && hull[1].Corps.Count == 0);
    Check("each team's flyers are its own", flyers[(1, Hull)].Single().Id == AllianceB && flyers[(1, Hull)].Single().Damage == 7);
    Check("and each hull's", flyers[(1, Other)].Single().Damage == 8);
    Check("a line with no hull is under none", !flyers.Keys.Any(k => k.ShipTypeId == 0));

    var engine = NewEngine();
    engine.Add(TwoFleets());
    var snap = engine.Build();
    Check("every hull on the report has its flyers, and they add up to it",
        snap.Ships.All(r => snap.ShipFlyers[(r.Team, r.ShipTypeId)] is var f && f.Sum(x => x.Pilots) == r.Pilots && f.Sum(x => x.Lost) == r.Lost && f.Sum(x => x.Damage) == r.Damage));
}

// ── What a victim was ────────────────────────────────────────────────────

Check("a capsule is a pod", BattleReportService.ClassOf(29, 6) == BattleHullClass.Pod);
Check("a frigate is a ship", BattleReportService.ClassOf(25, 6) == BattleHullClass.Ship);
Check("a citadel is a structure", BattleReportService.ClassOf(1657, 65) == BattleHullClass.Structure);
Check("a control tower is a structure", BattleReportService.ClassOf(365, 23) == BattleHullClass.Structure);
Check("a mobile depot is a deployable, not a structure", BattleReportService.ClassOf(1246, 22) == BattleHullClass.Deployable);

// ── Finding fights ───────────────────────────────────────────────────────

{
    var at = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
    var far = at.AddDays(30); // long after, so nothing here reads as still going
    var ids = 5000;
    var parts = new Dictionary<int, IReadOnlyList<FightParticipant>>();
    FightKillTime K(int system, double minutes, params (long Pilot, long Side)[] who)
    {
        var id = ids++;
        parts[id] = [.. who.Select(w => new FightParticipant(w.Pilot, w.Side))];
        return new FightKillTime(id, at.AddMinutes(minutes), system);
    }
    (long, long)[] Crowd(long start, int count, long side) => [.. Enumerable.Range(0, count).Select(i => (start + i, side))];
    IEnumerable<FightKillTime> Many(int system, double from, int n, (long, long)[] who, double step = 1) =>
        Enumerable.Range(0, n).Select(i => K(system, from + i * step, who));
    var none = new HashSet<long>();

    var quiet = BattleFightFinder.Runs(Many(1, 0, 6, []).Concat(Many(1, 200, 6, [])));
    Check("a long quiet in a system ends a stretch", quiet.Count == 2 && quiet.All(r => r.KillIds.Count == 6), quiet.Count.ToString());
    Check("a handful of kills is not looked at", BattleFightFinder.Runs(Many(1, 0, 4, [])).Count == 0);
    Check("a gap just under the limit does not", BattleFightFinder.Runs(Many(1, 0, 6, [], step: 29)).Count == 1);

    var fleetA = Crowd(90_100_000, 40, AllianceA);
    var fleetB = Crowd(90_200_000, 40, AllianceB);
    var strangers = Crowd(90_300_000, 60, AllianceC);
    var moving = Many(100, 0, 10, [.. fleetA, .. fleetB])   // one fight, moving between systems
        .Concat(Many(101, 15, 10, [.. fleetA.Take(30), .. fleetB.Take(30)]))
        .Concat(Many(200, 5, 10, strangers))                // at the same time, nobody in common
        .Concat(Many(100, 300, 10, [.. fleetA, .. fleetB])) // the same pilots, hours later
        .ToList();
    var found = BattleFightFinder.Fights(BattleFightFinder.Runs(moving), parts, 30, far, none, none);
    var mover = found.FirstOrDefault(f => f.Runs.Count == 2 && f.Windows.Select(w => w.SystemId).Distinct().Count() == 2);
    Check("a fight moving between systems is one fight", mover is not null && mover.Pilots == 80, string.Join(" | ", found.Select(f => $"{f.Runs.Count} runs {f.Pilots}p")));
    Check("a fight at the same time with nobody in common is another", found.Any(f => f.Runs.Count == 1 && f.Runs[0].SystemId == 200));
    Check("the same pilots hours later are another", found.Count == 3 && found.Any(f => f.Runs[0].First == at.AddMinutes(300)));
    Check("largest sides first", mover?.LargestSides.Take(2).Order().SequenceEqual([AllianceA, AllianceB]) == true && found.First(f => f.Runs[0].SystemId == 200).LargestSides[0] == AllianceC);

    var w = mover!.Windows.First(x => x.SystemId == 100);
    Check("each system gets its own times, padded to the minute", w.From == at.AddMinutes(-5) && w.To == at.AddMinutes(14));
    Check("the next system its own", mover.Windows.First(x => x.SystemId == 101) is { } w2 && w2.From == at.AddMinutes(10) && w2.To == at.AddMinutes(29));

    // ⚠️ Found on a month of real kills: fights an hour apart, each sharing pilots with the next, read
    // as one fight of 82 systems and 24 hours.
    var chain = Enumerable.Range(0, 30).SelectMany(n => Many(1000 + n, n * 60, 10, fleetA)).ToList();
    var chained = BattleFightFinder.Fights(BattleFightFinder.Runs(chain), parts, 30, far.AddDays(5), none, none);
    Check("a long chain of fights is not one fight", chained.Count > 1 && chained.All(f => f.Last - f.First <= BattleFightFinder.MaxJoinedLength), string.Join(",", chained.Select(f => f.Runs.Count)));

    var overlapChain = Enumerable.Range(0, 30).SelectMany(n => Many(2000 + n, n * 50, 60, fleetA)).ToList();
    var joined = BattleFightFinder.Fights(BattleFightFinder.Runs(overlapChain), parts, 30, far.AddDays(5), none, none);
    Check("nor one where each overlaps the next", joined.Count > 1 && joined.All(f => f.Last - f.First <= BattleFightFinder.MaxJoinedLength), string.Join(",", joined.Select(f => f.Runs.Count)));

    // A few pilots in common is not the same fight: a scout, a neutral watching.
    var fewShared = Many(3000, 0, 10, [.. fleetA, .. fleetB.Take(5)]).Concat(Many(3001, 0, 10, [.. strangers, .. fleetB.Take(5)])).ToList();
    Check("a few pilots in common does not join two fights", BattleFightFinder.Fights(BattleFightFinder.Runs(fewShared), parts, 30, far, none, none).Count == 2);

    Check("small fights fall under the minimum", BattleFightFinder.Fights(BattleFightFinder.Runs(moving), parts, 70, far, none, none).Count == 2);

    // A few pilots farming a thousand kills is no fight.
    var farm = Many(300, 0, 500, Crowd(90_400_000, 6, AllianceD), step: 0.1).ToList();
    Check("many kills by few pilots is not a large fight", BattleFightFinder.Fights(BattleFightFinder.Runs(farm), parts, 30, far, none, none).Count == 0);

    // A fight whose last kill is recent has no end yet.
    var justNow = at.AddMinutes(9 + 20);
    var going = BattleFightFinder.Fights(BattleFightFinder.Runs(Many(100, 0, 10, fleetA)), parts, 30, justNow, none, none).Single();
    Check("a fight still going is left open, ready to watch", going.StillGoing && going.Windows.All(x => x.To is null) && going.Windows[0].IsOpenAt(justNow));
    var over = BattleFightFinder.Fights(BattleFightFinder.Runs(Many(100, 0, 10, fleetA)), parts, 30, at.AddMinutes(9 + 31), none, none).Single();
    Check("one gone quiet has an end", !over.StillGoing && over.Windows.All(x => x.To is not null));

    Check("a fight with the user's pilots is marked", BattleFightFinder.Fights(BattleFightFinder.Runs(Many(100, 0, 10, fleetA)), parts, 30, far, new HashSet<long> { fleetA[0].Item1 }, none).Single().IsOurs);
    Check("and by their corporation", BattleFightFinder.Fights(BattleFightFinder.Runs(Many(100, 0, 10, fleetA)), parts, 30, far, none, new HashSet<long> { AllianceA }).Single().IsOurs);
    Check("others are not", !BattleFightFinder.Fights(BattleFightFinder.Runs(Many(100, 0, 10, fleetA)), parts, 30, far, none, none).Single().IsOurs);
}

// ── Reading a kill link ──────────────────────────────────────────────────

Check("a kill link gives its id", BattleReportService.ParseKillId("https://zkillboard.com/kill/123456789/") == 123456789);
Check("a bare number is an id", BattleReportService.ParseKillId(" 123456789 ") == 123456789);
Check("a link to something else is not", BattleReportService.ParseKillId("https://zkillboard.com/character/123456789/") is null);
Check("nor is text", BattleReportService.ParseKillId("abc") is null && BattleReportService.ParseKillId("") is null);

// ── The database: what a live report reads ───────────────────────────────

var dbPath = Path.Combine(Path.GetTempPath(), $"battlereportcheck-{Guid.NewGuid():N}.db");
try
{
    var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath};Pooling=False").Options;
    var factory = new OneFactory(options);
    await using (var db = factory.CreateDbContext())
    {
        db.Database.EnsureCreated();
        db.SdeCategories.Add(new SdeCategory { CategoryId = 6, Name = "Ship" });
        db.SdeCategories.Add(new SdeCategory { CategoryId = 65, Name = "Structure" });
        db.SdeGroups.Add(new SdeGroup { GroupId = 25, CategoryId = 6, Name = "Frigate" });
        db.SdeGroups.Add(new SdeGroup { GroupId = 29, CategoryId = 6, Name = "Capsule" });
        db.SdeGroups.Add(new SdeGroup { GroupId = 1657, CategoryId = 65, Name = "Citadel" });
        db.SdeTypes.Add(new SdeType { TypeId = Hull, GroupId = 25, Name = "Test Frigate" });
        db.SdeTypes.Add(new SdeType { TypeId = Pod, GroupId = 29, Name = "Test Capsule" });
        db.SdeTypes.Add(new SdeType { TypeId = Citadel, GroupId = 1657, Name = "Test Citadel" });
        db.MarketDefaultSettings.Add(new MarketDefaultSettings { Id = 1, AssetValueConfigId = 1, AssetValuePriceType = MarketPriceType.Sell });
        db.MarketItemPrices.Add(new MarketItemPrice { ConfigId = 1, TypeId = Hull, SellPrice = 1_000_000 });
        await db.SaveChangesAsync();
    }

    var service = new BattleReportService(factory);
    var start = new DateTimeOffset(2026, 1, 15, 18, 0, 0, TimeSpan.Zero);

    async Task Store(int id, DateTimeOffset time, int system = System1, int ship = Hull)
    {
        await using var db = factory.CreateDbContext();
        db.KillMailDetails.Add(new KillMailDetail
        {
            KillMailId = id, KillMailHash = "x", KillMailTime = time, SolarSystemId = system,
            VictimCharId = 90_000_201, VictimCorpId = CorpB, VictimAllianceId = AllianceB, VictimShipTypeId = ship,
        });
        db.KillMailAttackers.Add(new KillMailAttacker { KillMailId = id, CharacterId = 90_000_101, CorporationId = CorpA, AllianceId = AllianceA, DamageDone = 77, FinalBlow = true, ShipTypeId = Hull });
        await db.SaveChangesAsync();
    }

    await Store(1, start.AddMinutes(1));
    await Store(2, start.AddMinutes(2));
    await Store(3, start.AddMinutes(90));                // after a fixed window's end
    await Store(4, start.AddMinutes(3), system: 30_000_144); // another system
    await Store(5, start.AddMinutes(-30));               // before the start
    await Store(6, start.AddMinutes(4), ship: Citadel);

    var (first, tooMany) = await service.LoadNewAsync([new BattleWindow(System1, start, start.AddMinutes(60))], new HashSet<int>());
    Check("a window reads its own system and time, nothing else", first.Select(k => k.KillMailId).Order().SequenceEqual([1, 2, 6]) && !tooMany,
        string.Join(",", first.Select(k => k.KillMailId)));

    var kill1 = first.First(k => k.KillMailId == 1);
    Check("a kill is priced by our own valuation", kill1.Value == 1_000_000, $"{kill1.Value}");
    Check("its attackers come with it", kill1.Attackers.Count == 1 && kill1.Attackers[0].DamageDone == 77 && kill1.Attackers[0].FinalBlow);
    Check("a citadel is a structure, a frigate a ship", first.First(k => k.KillMailId == 6).VictimClass == BattleHullClass.Structure && kill1.VictimClass == BattleHullClass.Ship);

    BattleWindow[] openWindow = [new BattleWindow(System1, start, null)];
    var open = await service.LoadNewAsync(openWindow, new HashSet<int>());
    Check("an open window has no end", open.Kills.Select(k => k.KillMailId).Order().SequenceEqual([1, 2, 3, 6]));

    var held = open.Kills.Select(k => k.KillMailId).ToHashSet();
    var none = await service.LoadNewAsync(openWindow, held);
    Check("nothing new reads as nothing", none.Kills.Count == 0);

    // ⚠️ What a "newer than the last kill" check would lose: this mail records a kill from before
    // the newest one held, and reaches the database only now.
    await Store(7, start.AddMinutes(5));
    var late = await service.LoadNewAsync(openWindow, held);
    Check("a late mail for an earlier moment is still picked up", late.Kills.Select(k => k.KillMailId).SequenceEqual([7]));

    var engine = NewEngine();
    engine.Add(open.Kills);
    var totalBefore = engine.Build().Kills.Count;
    engine.Add(late.Kills);
    Check("and reaches the report", engine.Build().Kills.Count == totalBefore + 1);

    // A battle across systems, each over its own time, is one fight.
    const int System2 = 30_000_144, System3 = 30_000_145;
    await Store(20, start.AddMinutes(100), system: System3);  // outside its window
    await Store(21, start.AddMinutes(50),  system: System3);
    var across = await service.LoadNewAsync(
    [
        new BattleWindow(System1, start, start.AddMinutes(10)),
        new BattleWindow(System2, start.AddMinutes(2), start.AddMinutes(4)),
        new BattleWindow(System3, start.AddMinutes(40), start.AddMinutes(60)),
    ], new HashSet<int>());
    Check("each system is read over its own time", across.Kills.Select(k => k.KillMailId).Order().SequenceEqual([1, 2, 4, 6, 7, 21]),
        string.Join(",", across.Kills.Select(k => k.KillMailId).Order()));

    var overlap = await service.LoadNewAsync(
    [
        new BattleWindow(System1, start, start.AddMinutes(3)),
        new BattleWindow(System1, start.AddMinutes(1), start.AddMinutes(5)),
    ], new HashSet<int>());
    Check("windows overlapping on one system read each kill once", overlap.Kills.Select(k => k.KillMailId).Order().SequenceEqual([1, 2, 6, 7]),
        string.Join(",", overlap.Kills.Select(k => k.KillMailId).Order()));

    var at = start.AddMinutes(30);
    Check("no end, or an end to come, can still gain kills",
        new BattleWindow(System1, start, null).IsOpenAt(at) && new BattleWindow(System1, start, at.AddSeconds(1)).IsOpenAt(at));
    Check("an end passed cannot", !new BattleWindow(System1, start, at).IsOpenAt(at));

    // Constellations: picking one brings its systems.
    await using (var db = factory.CreateDbContext())
    {
        db.SdeRegions.Add(new SdeRegion { RegionId = 10000002, Name = "The Forge" });
        db.SdeConstellations.Add(new SdeConstellation { ConstellationId = 20000020, RegionId = 10000002, Name = "Kimotoro" });
        db.SdeConstellations.Add(new SdeConstellation { ConstellationId = 20000021, RegionId = 10000002, Name = "Otanuomi" });
        db.SdeSolarSystems.Add(new SdeSolarSystem { SolarSystemId = System1, Name = "Jita", ConstellationId = 20000020, RegionId = 10000002 });
        db.SdeSolarSystems.Add(new SdeSolarSystem { SolarSystemId = System2, Name = "Perimeter", ConstellationId = 20000020, RegionId = 10000002 });
        db.SdeSolarSystems.Add(new SdeSolarSystem { SolarSystemId = System3, Name = "Otitoh", ConstellationId = 20000021, RegionId = 10000002 });
        await db.SaveChangesAsync();
    }
    var found = await service.SearchConstellationsAsync("Kimo");
    Check("a constellation is found with its systems, and no others",
        found.Count == 1 && found[0].Region == "The Forge"
        && found[0].Systems.Select(s => s.SystemId).Order().SequenceEqual([System1, System2]),
        found.Count == 0 ? "none" : string.Join(",", found[0].Systems.Select(s => s.Name)));

    // Saved battles.
    Check("nothing is saved to begin with", (await service.ListSavedAsync()).Count == 0);
    var definition = new BattleDefinition
    {
        Windows = [new BattleWindow(System1, start, null), new BattleWindow(System3, start.AddMinutes(40), start.AddMinutes(60))],
        TeamCount = 3, Moves = new() { [AllianceA] = 2 }, WatchLive = false,
    };
    var savedId = await service.SaveBattleAsync(null, "First", definition);
    var otherId = await service.SaveBattleAsync(null, "Second", new BattleDefinition());
    var list = await service.ListSavedAsync();
    Check("battles are kept, as many as wanted", list.Count == 2 && savedId != otherId);
    var back = list.First(b => b.Id == savedId);
    Check("a battle comes back as it was saved",
        back.Name == "First" && back.Definition.Windows.SequenceEqual(definition.Windows)
        && back.Definition.TeamCount == 3 && back.Definition.Moves[AllianceA] == 2 && !back.Definition.WatchLive);

    await service.SaveBattleAsync(savedId, "Renamed", definition);
    list = await service.ListSavedAsync();
    Check("saving again changes that one, and is listed first", list.Count == 2 && list[0].Id == savedId && list[0].Name == "Renamed");

    await service.DeleteBattleAsync(savedId);
    Check("a deleted one is gone, the other stays", (await service.ListSavedAsync()).Select(b => b.Id).SequenceEqual([otherId]));
    var again = await service.SaveBattleAsync(savedId, "Back", definition);
    Check("one deleted from another machine is kept anew, not lost", (await service.ListSavedAsync()).Any(b => b.Id == again && b.Name == "Back"));

    // A damaged row is still listed, so it can be deleted, and does not cost the list.
    await using (var db = factory.CreateDbContext())
    {
        db.SavedBattles.Add(new SavedBattle { Name = "Damaged", Definition = "{not json", UpdatedAt = start });
        await db.SaveChangesAsync();
    }
    Check("a damaged saved battle does not cost the list", (await service.ListSavedAsync()).Any(b => b.Name == "Damaged" && b.Definition.Windows.Count == 0));

    // Finding fights, from the database.
    var fightStart = start.AddDays(5);
    await using (var db = factory.CreateDbContext())
    {
        for (var k = 0; k < 8; k++)
        {
            var id = 700 + k;
            db.KillMailDetails.Add(new KillMailDetail
            {
                KillMailId = id, KillMailHash = "x", KillMailTime = fightStart.AddMinutes(k), SolarSystemId = System2,
                VictimCharId = 90_500_000 + k, VictimCorpId = CorpB, VictimAllianceId = AllianceB, VictimShipTypeId = Hull,
            });
            db.KillMailAttackers.Add(new KillMailAttacker { KillMailId = id, CharacterId = 90_600_000 + k, CorporationId = CorpA, AllianceId = AllianceA, DamageDone = 5, ShipTypeId = Hull });
            db.KillMailAttackers.Add(new KillMailAttacker { KillMailId = id, CorporationId = 1_000_125, DamageDone = 5 }); // a rat
        }
        await db.SaveChangesAsync();
    }
    var fights = await service.FindFightsAsync(fightStart.AddHours(-1), fightStart.AddHours(1), 10, false, fightStart.AddDays(1));
    Check("a fight is found in the database, its pilots counted, rats not", fights.Count == 1 && fights[0].Pilots == 16 && fights[0].KillIds.Count == 8, $"{fights.Count} fights");
    Check("valued by our own valuation", fights[0].IskLost == 8_000_000, fights[0].IskLost.ToString());
    Check("outside the period there is none", (await service.FindFightsAsync(fightStart.AddHours(1), fightStart.AddHours(2), 10, false, fightStart.AddDays(1))).Count == 0);
    Check("only our fights: not this one", (await service.FindFightsAsync(fightStart.AddHours(-1), fightStart.AddHours(1), 10, true, fightStart.AddDays(1))).Count == 0);

    var anchor = await service.KillAnchorAsync(1);
    Check("a kill gives the system and time to start from", anchor is { } a && a.SystemId == System1 && a.Time == start.AddMinutes(1));
    Check("a kill not held gives nothing", await service.KillAnchorAsync(999) is null);

    await using (var db = factory.CreateDbContext())
    {
        db.Characters.Add(new Character { Id = 90_000_101, Name = "x" });
        await db.SaveChangesAsync();
    }
    var ours = await service.OursAsync();
    Check("the user's own pilots are read, token or not", ours.Characters.Contains(90_000_101));
}
finally
{
    try { File.Delete(dbPath); } catch { /* a temp file; the next run uses another name */ }
}

Console.WriteLine(failures == 0 ? "Battle report: all checks pass" : $"Battle report: {failures} FAILED");
return failures;

sealed class OneFactory(DbContextOptions<AppDbContext> o) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(o);
}
