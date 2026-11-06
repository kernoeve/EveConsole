namespace EveConsole.Services.Battle;

/// <summary>What the victim of a kill was, for the counts a battle report keeps apart.</summary>
public enum BattleHullClass { Ship, Pod, Structure, Deployable }

/// <summary>What a side in a battle is: its alliance, its corporation with no alliance, or a lone
/// pilot in an NPC corporation, since everyone in a starter corporation is not on one side.</summary>
public enum BattleSideKind { Alliance, Corporation, Pilot }

/// <summary>One name on a kill's attacker list. Zero where the kill has no such id.</summary>
public sealed record BattleAttacker(
    long CharacterId, long CorporationId, long AllianceId, int DamageDone, bool FinalBlow, int ShipTypeId);

/// <summary>One kill, as a report reads it. Zero where the kill has no such id.</summary>
public sealed record BattleKill(
    int KillMailId, DateTimeOffset Time, int SystemId,
    long VictimCharId, long VictimCorpId, long VictimAllianceId,
    int VictimShipTypeId, BattleHullClass VictimClass, double Value,
    IReadOnlyList<BattleAttacker> Attackers);

/// <summary>One team's numbers. Damage leaves out what a team did to itself.</summary>
public sealed record BattleTeamStats(
    int Team, int Pilots, double IskLost, double Efficiency,
    int ShipsLost, int PodsLost, int StructuresLost, int DeployablesLost,
    long DamageDealt, long StructureDamage, bool IsOurs);

/// <summary>One alliance, corporation or lone pilot on a team; also one corporation within an
/// alliance, with the same numbers for it alone. Kills are the ships it was on the attacker list of:
/// pods, structures and deployables are not ships, and a kill with ten of its pilots on it is one.</summary>
public sealed record BattleSideRow(
    long SideId, BattleSideKind Kind, int Team, int Pilots, double IskLost, int ShipsLost, long Damage, int Kills, bool IsOurs);

/// <summary>One pilot in one hull. A structure on the field has no pilot: CharacterId is zero, and
/// the corporation owning it stands in.</summary>
public sealed record BattlePilotRow(
    int Team, long CharacterId, long CorporationId, long AllianceId, int ShipTypeId,
    long Damage, int Kills, int FinalBlows, int Lost, double IskLost, bool IsOurs);

/// <summary>One pilot on a team, once: their totals, and each hull behind them. Total's hull is the
/// one they did the most in. A structure, with no pilot, is a family of its own.</summary>
public sealed record BattlePilotFamily(BattlePilotRow Total, IReadOnlyList<BattlePilotRow> Hulls);

/// <summary>Who flew a hull type on a team: an alliance with its corporations under it, or a
/// corporation with no alliance. Its numbers are for that hull alone.</summary>
public sealed record BattleShipFlyer(
    long Id, BattleSideKind Kind, int Pilots, int Lost, double IskLost, long Damage, IReadOnlyList<BattleShipFlyer> Corps);

/// <summary>One hull type on a team.</summary>
public sealed record BattleShipRow(int Team, int ShipTypeId, int Pilots, int Lost, double IskLost, long Damage);

/// <summary>One loss, for the timeline. Team is -1 for a victim on no team.</summary>
public sealed record BattleKillRow(
    int KillMailId, DateTimeOffset Time, int Team, int SystemId, int ShipTypeId,
    long VictimCharId, long VictimCorpId, long VictimAllianceId, double Value, int Attackers,
    long FinalBlowCharId, long FinalBlowCorpId, long FinalBlowAllianceId);

/// <summary>A report at one moment: every number on the screen, built off the UI thread.</summary>
public sealed record BattleSnapshot(
    IReadOnlyList<BattleTeamStats> Teams,
    IReadOnlyList<BattleSideRow> Sides,
    IReadOnlyList<BattlePilotRow> Pilots,
    IReadOnlyList<BattleShipRow> Ships,
    IReadOnlyList<BattleKillRow> Kills,
    double TotalIsk, int TotalPilots, DateTimeOffset? FirstKill, DateTimeOffset? LastKill, long Version)
{
    /// <summary>Who flew each hull type on each team, most pilots first.</summary>
    public IReadOnlyDictionary<(int Team, int ShipTypeId), IReadOnlyList<BattleShipFlyer>> ShipFlyers { get; init; } =
        new Dictionary<(int, int), IReadOnlyList<BattleShipFlyer>>();

    /// <summary>Each pilot once, by total damage, then ISK lost.</summary>
    public IReadOnlyList<BattlePilotFamily> PilotFamilies { get; init; } = [];

    /// <summary>Each alliance's corporations, largest first.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<BattleSideRow>> SideCorps { get; init; } =
        new Dictionary<long, IReadOnlyList<BattleSideRow>>();
}

/// <summary>
/// A battle report's sums and sides, with nothing of the database or the screen in it, so kills can
/// keep arriving while a report is open.
///
/// <para>Who is on which side comes from the kills themselves: a side that shot another is against
/// it, and sides on the same attacker list lean together, more weakly, since a shot is proof and
/// shared targets are only a hint. Every new kill can correct an early guess. Once the user moves a
/// side by hand, every side present stays where it is: a screen that rearranges what was just
/// arranged is worse than a guess left standing. Later arrivals are still placed by what they did.</para>
///
/// <para>Not thread-safe. The caller holds one lock around every call.</para>
/// </summary>
public sealed class BattleReportEngine
{
    /// <summary>Teams worked out from the kills. More exist only when the user adds them.</summary>
    public const int AutomaticTeams = 2;

    private const double Friendly = 0.5;   // sides on one attacker list, against one shot at the other
    private const double MoveMargin = 0.25; // what a side must gain to change teams on its own

    private readonly Dictionary<int, BattleKill> _kills = [];
    private readonly Dictionary<long, Dictionary<long, double>> _affinity = [];
    private readonly Dictionary<long, double> _activity = [];
    private readonly Dictionary<long, BattleSideKind> _kinds = [];
    private readonly Dictionary<long, int> _team = [];
    private readonly HashSet<long> _pinned = [];
    private readonly Dictionary<long, int> _moves = [];
    private readonly HashSet<long> _ourCharacters, _ourCorporations;
    private readonly HashSet<long> _ourSides = [];

    public BattleReportEngine(IEnumerable<long> ourCharacters, IEnumerable<long> ourCorporations)
    {
        _ourCharacters   = [.. ourCharacters];
        _ourCorporations = [.. ourCorporations];
    }

    public int TeamCount { get; private set; } = AutomaticTeams;

    public int KillCount => _kills.Count;

    public IReadOnlyCollection<int> KnownKillIds => _kills.Keys;

    /// <summary>What a saved report keeps of the arrangement: nothing while the sides are all as the
    /// kills place them, and every side once the user has moved one. ⚠️ Every side, not just the one
    /// moved: on their own, the others would be worked out afresh around it, and a side moved away
    /// from its friends pulls them after it, so the arrangement read back is not the one saved.</summary>
    public IReadOnlyDictionary<long, int> Moves =>
        _moves.Count == 0 ? _moves : _pinned.Where(_team.ContainsKey).ToDictionary(s => s, s => _team[s]);

    /// <summary>The side a name fights for, or zero for a rat.</summary>
    public static (long Side, BattleSideKind Kind) SideOf(long characterId, long corporationId, long allianceId)
    {
        if (allianceId > 0) return (allianceId, BattleSideKind.Alliance);
        if (corporationId > 0 && !IsNpcCorporation(corporationId)) return (corporationId, BattleSideKind.Corporation);
        return characterId > 0 && !IsNpcCharacter(characterId) ? (characterId, BattleSideKind.Pilot) : (0, BattleSideKind.Pilot);
    }

    private static bool IsNpcCorporation(long id) => id is >= 1_000_000 and < 2_000_000;
    private static bool IsNpcCharacter(long id)   => id is >= 3_000_000 and < 4_000_000;

    /// <summary>Puts back a report's saved arrangement, before its kills are added.</summary>
    public void Restore(int teamCount, IReadOnlyDictionary<long, int> moves)
    {
        TeamCount = Math.Max(AutomaticTeams, teamCount);
        foreach (var (side, team) in moves)
        {
            if (team < 0 || team >= TeamCount) continue;
            _team[side] = team;
            _moves[side] = team;
            _pinned.Add(side);
        }
    }

    /// <summary>Takes in kills it has not seen and places any new sides; true if there were any.</summary>
    public bool Add(IEnumerable<BattleKill> kills)
    {
        var added = false;
        foreach (var kill in kills)
        {
            if (!_kills.TryAdd(kill.KillMailId, kill)) continue;
            added = true;
            Learn(kill);
        }
        if (added) Assign();
        return added;
    }

    /// <summary>Moves a side to a team by hand, and holds every side present where it stands.</summary>
    public void Move(long side, int team)
    {
        if (team < 0 || team >= TeamCount || !_team.ContainsKey(side)) return;
        _team[side] = team;
        _moves[side] = team;
        _pinned.UnionWith(_team.Keys);
    }

    /// <summary>An empty team for the user to move sides to.</summary>
    public int AddTeam() => ++TeamCount;

    /// <summary>Can this team go: it has no side on any kill held, and two would still be left.</summary>
    public bool CanRemoveTeam(int team) =>
        TeamCount > AutomaticTeams && team >= 0 && team < TeamCount
        && !_team.Any(kv => kv.Value == team && _activity.ContainsKey(kv.Key));

    /// <summary>Removes a team with nobody on it. Later teams move down a letter, so the letters
    /// stay unbroken. A saved move to it naming a side no longer on any kill goes with it.</summary>
    public bool RemoveTeam(int team)
    {
        if (!CanRemoveTeam(team)) return false;

        foreach (var side in _team.Where(kv => kv.Value == team).Select(kv => kv.Key).ToList())
        {
            _team.Remove(side);
            _moves.Remove(side);
            _pinned.Remove(side);
        }
        foreach (var side in _team.Keys.ToList())
            if (_team[side] > team) _team[side]--;
        foreach (var side in _moves.Keys.ToList())
            if (_moves[side] > team) _moves[side]--;

        TeamCount--;
        return true;
    }

    private void Learn(BattleKill kill)
    {
        var (victim, victimKind) = SideOf(kill.VictimCharId, kill.VictimCorpId, kill.VictimAllianceId);
        Note(victim, victimKind, kill.VictimCharId, kill.VictimCorpId);

        var attackers = new HashSet<long>();
        foreach (var a in kill.Attackers)
        {
            var (side, kind) = SideOf(a.CharacterId, a.CorporationId, a.AllianceId);
            if (side == 0) continue;
            Note(side, kind, a.CharacterId, a.CorporationId);
            attackers.Add(side);
        }

        if (victim != 0)
            foreach (var a in attackers)
                if (a != victim) Link(a, victim, -1);

        // Shared out across the list, so a blob of forty corporations on one mail does not outweigh
        // the direct evidence of that same mail.
        if (attackers.Count > 1)
        {
            var weight = Friendly / (attackers.Count - 1);
            var list = attackers.ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                    Link(list[i], list[j], weight);
        }
    }

    private void Note(long side, BattleSideKind kind, long characterId, long corporationId)
    {
        if (side == 0) return;
        _kinds[side] = kind;
        _activity.TryAdd(side, 0);
        if (_ourCharacters.Contains(characterId) || _ourCorporations.Contains(corporationId))
            _ourSides.Add(side);
    }

    private void Link(long a, long b, double weight)
    {
        Half(a, b);
        Half(b, a);
        _activity[a] += Math.Abs(weight);
        _activity[b] += Math.Abs(weight);

        void Half(long from, long to)
        {
            if (!_affinity.TryGetValue(from, out var row)) _affinity[from] = row = [];
            row[to] = row.GetValueOrDefault(to) + weight;
        }
    }

    private double Gain(long side, int team)
    {
        if (!_affinity.TryGetValue(side, out var row)) return 0;
        var gain = 0.0;
        foreach (var (other, weight) in row)
            if (_team.TryGetValue(other, out var t) && t == team) gain += weight;
        return gain;
    }

    /// <summary>Places every side not yet on a team, most tied to the placed ones first, then lets
    /// sides not held in place correct themselves. Same kills, same answer.</summary>
    private void Assign()
    {
        var waiting = _activity.Keys.Where(s => !_team.ContainsKey(s)).ToHashSet();
        if (waiting.Count > 0)
        {
            // How strongly each waiting side is tied to the sides already placed.
            var ties = waiting.ToDictionary(s => s, _ => 0.0);
            foreach (var s in waiting)
                if (_affinity.TryGetValue(s, out var row))
                    foreach (var (other, weight) in row)
                        if (_team.ContainsKey(other)) ties[s] += Math.Abs(weight);

            while (waiting.Count > 0)
            {
                var next = PickNext(waiting, ties);
                waiting.Remove(next);
                ties.Remove(next);
                _team[next] = BestTeam(next);
                if (_affinity.TryGetValue(next, out var row))
                    foreach (var (other, weight) in row)
                        if (waiting.Contains(other)) ties[other] += Math.Abs(weight);
            }
        }

        var loose = _team.Keys.Where(s => !_pinned.Contains(s))
            .OrderByDescending(s => _activity.GetValueOrDefault(s)).ThenBy(s => s).ToList();
        for (var pass = 0; pass < 10; pass++)
        {
            var moved = false;
            foreach (var side in loose)
            {
                var current = _team[side];
                var best = current;
                var bestGain = Gain(side, current) + MoveMargin;
                for (var t = 0; t < AutomaticTeams; t++)
                {
                    if (t == current) continue;
                    var gain = Gain(side, t);
                    if (gain > bestGain) (best, bestGain) = (t, gain);
                }
                if (best == current) continue;
                _team[side] = best;
                moved = true;
            }
            if (!moved) break;
        }
    }

    /// <summary>The first side of all is the user's own largest where they are in the fight, so
    /// their side reads first; after that, whoever is most tied to those already placed.</summary>
    private long PickNext(HashSet<long> waiting, Dictionary<long, double> ties)
    {
        IEnumerable<long> from = waiting;
        if (_team.Count == 0 && waiting.Any(_ourSides.Contains)) from = waiting.Where(_ourSides.Contains);

        long best = 0;
        var (bestTie, bestActivity) = (-1.0, -1.0);
        foreach (var s in from)
        {
            var (tie, activity) = (ties[s], _activity.GetValueOrDefault(s));
            var better = tie > bestTie
                || (tie == bestTie && activity > bestActivity)
                || (tie == bestTie && activity == bestActivity && s < best);
            if (better) (best, bestTie, bestActivity) = (s, tie, activity);
        }
        return best;
    }

    private int BestTeam(long side)
    {
        var members = new int[TeamCount];
        foreach (var t in _team.Values) members[t]++;

        var best = 0;
        var bestGain = double.NegativeInfinity;
        for (var t = 0; t < TeamCount; t++)
        {
            // A team the user added and has not used yet is theirs to fill.
            if (t >= AutomaticTeams && members[t] == 0) continue;
            var gain = Gain(side, t);
            if (gain > bestGain || (gain == bestGain && members[t] < members[best]))
                (best, bestGain) = (t, gain);
        }
        return best;
    }

    /// <summary>Every number on the report, from every kill held.</summary>
    public BattleSnapshot Build()
    {
        var pilots = new Dictionary<(int Team, long Char, long Owner, int Ship), PilotSum>();
        var sidePilots = new Dictionary<long, HashSet<long>>();
        var sides = new Dictionary<long, SideSum>();
        var corpSums = new Dictionary<(long Side, long Corp), SideSum>();
        var corpPilots = new Dictionary<(long Side, long Corp), HashSet<long>>();
        var teams = Enumerable.Range(0, TeamCount).Select(_ => new TeamSum()).ToArray();
        var everyone = new HashSet<long>();
        var killRows = new List<BattleKillRow>(_kills.Count);

        foreach (var kill in _kills.Values)
        {
            var (victim, _) = SideOf(kill.VictimCharId, kill.VictimCorpId, kill.VictimAllianceId);
            var victimTeam = victim != 0 && _team.TryGetValue(victim, out var vt) ? vt : -1;
            var finalBlow = kill.Attackers.FirstOrDefault(a => a.FinalBlow);
            killRows.Add(new BattleKillRow(kill.KillMailId, kill.Time, victimTeam, kill.SystemId, kill.VictimShipTypeId,
                kill.VictimCharId, kill.VictimCorpId, kill.VictimAllianceId, kill.Value, kill.Attackers.Count,
                finalBlow?.CharacterId ?? 0, finalBlow?.CorporationId ?? 0, finalBlow?.AllianceId ?? 0));

            if (victimTeam >= 0)
            {
                var team = teams[victimTeam];
                team.IskLost += kill.Value;
                switch (kill.VictimClass)
                {
                    case BattleHullClass.Pod:       team.PodsLost++; break;
                    case BattleHullClass.Structure: team.StructuresLost++; break;
                    case BattleHullClass.Deployable: team.DeployablesLost++; break;
                    default:                        team.ShipsLost++; break;
                }

                var side = Side(sides, victim);
                side.IskLost += kill.Value;
                if (kill.VictimClass == BattleHullClass.Ship) side.ShipsLost++;

                var victimCorp = CorpOf(corpSums, victim, kill.VictimCorpId);
                victimCorp.IskLost += kill.Value;
                if (kill.VictimClass == BattleHullClass.Ship) victimCorp.ShipsLost++;
                CountCorp(victim, kill.VictimCorpId, kill.VictimCharId);

                var row = Pilot(pilots, victimTeam, kill.VictimCharId, kill.VictimCorpId, kill.VictimAllianceId, kill.VictimShipTypeId);
                row.Lost++;
                row.IskLost += kill.Value;
                Count(victim, victimTeam, kill.VictimCharId);
            }

            foreach (var a in kill.Attackers)
            {
                var (side, _) = SideOf(a.CharacterId, a.CorporationId, a.AllianceId);
                if (side == 0 || !_team.TryGetValue(side, out var team)) continue;

                var row = Pilot(pilots, team, a.CharacterId, a.CorporationId, a.AllianceId, a.ShipTypeId);
                Count(side, team, a.CharacterId);
                CountCorp(side, a.CorporationId, a.CharacterId);

                // A team's own losses are not what it did to the other side.
                if (team == victimTeam) continue;

                row.Damage += a.DamageDone;
                row.Kills++;
                if (a.FinalBlow) row.FinalBlows++;

                var sum = Side(sides, side);
                sum.Damage += a.DamageDone;
                sum.CountKill(kill);

                var corpSum = CorpOf(corpSums, side, a.CorporationId);
                corpSum.Damage += a.DamageDone;
                corpSum.CountKill(kill);

                if (kill.VictimClass == BattleHullClass.Structure) teams[team].StructureDamage += a.DamageDone;
                else teams[team].DamageDealt += a.DamageDone;
            }
        }

        void CountCorp(long side, long corporation, long character)
        {
            if (corporation <= 0) return;
            CorpOf(corpSums, side, corporation);
            if (character <= 0) return;
            if (!corpPilots.TryGetValue((side, corporation), out var set)) corpPilots[(side, corporation)] = set = [];
            set.Add(character);
        }

        void Count(long side, int team, long character)
        {
            if (character <= 0) return;
            everyone.Add(character);
            teams[team].Pilots.Add(character);
            if (!sidePilots.TryGetValue(side, out var set)) sidePilots[side] = set = [];
            set.Add(character);
        }

        var totalLost = teams.Sum(t => t.IskLost);
        var teamRows = teams.Select((t, i) => new BattleTeamStats(
            i, t.Pilots.Count, t.IskLost, totalLost > 0 ? (totalLost - t.IskLost) / totalLost : 0,
            t.ShipsLost, t.PodsLost, t.StructuresLost, t.DeployablesLost, t.DamageDealt, t.StructureDamage,
            _team.Any(kv => kv.Value == i && _ourSides.Contains(kv.Key)))).ToList();

        // Only sides on a kill held: a saved move can name one no longer in the database.
        var sideRows = _team
            .Where(kv => _activity.ContainsKey(kv.Key))
            .Select(kv =>
            {
                sides.TryGetValue(kv.Key, out var s);
                return new BattleSideRow(kv.Key, _kinds.GetValueOrDefault(kv.Key), kv.Value,
                    sidePilots.TryGetValue(kv.Key, out var set) ? set.Count : 0,
                    s?.IskLost ?? 0, s?.ShipsLost ?? 0, s?.Damage ?? 0, s?.Kills ?? 0, _ourSides.Contains(kv.Key));
            })
            .OrderByDescending(s => s.Pilots).ThenByDescending(s => s.Damage).ThenBy(s => s.SideId)
            .ToList();

        var pilotRows = pilots
            .Select(kv => new BattlePilotRow(kv.Key.Team, kv.Key.Char, kv.Value.CorporationId, kv.Value.AllianceId, kv.Key.Ship,
                kv.Value.Damage, kv.Value.Kills, kv.Value.FinalBlows, kv.Value.Lost, kv.Value.IskLost,
                _ourCharacters.Contains(kv.Key.Char) || _ourCorporations.Contains(kv.Value.CorporationId)))
            .OrderByDescending(p => p.Damage).ThenByDescending(p => p.IskLost)
            .ThenBy(p => p.CharacterId).ThenBy(p => p.CorporationId).ThenBy(p => p.ShipTypeId)
            .ToList();

        var shipRows = pilotRows
            .Where(p => p.ShipTypeId > 0)
            .GroupBy(p => (p.Team, p.ShipTypeId))
            .Select(g => new BattleShipRow(g.Key.Team, g.Key.ShipTypeId, g.Count(),
                g.Sum(p => p.Lost), g.Sum(p => p.IskLost), g.Sum(p => p.Damage)))
            .OrderByDescending(s => s.Pilots).ThenByDescending(s => s.IskLost).ThenBy(s => s.ShipTypeId)
            .ToList();

        killRows.Sort((a, b) => b.Time != a.Time ? b.Time.CompareTo(a.Time) : b.KillMailId.CompareTo(a.KillMailId));

        return new BattleSnapshot(teamRows, sideRows, pilotRows, shipRows, killRows,
            killRows.Sum(k => k.Value), everyone.Count,
            killRows.Count > 0 ? killRows[^1].Time : null,
            killRows.Count > 0 ? killRows[0].Time : null,
            ++_version)
        {
            PilotFamilies = FamiliesOf(pilotRows),
            ShipFlyers = FlyersOf(pilotRows),
            SideCorps = corpSums
                .Where(kv => _kinds.GetValueOrDefault(kv.Key.Side) == BattleSideKind.Alliance && _team.ContainsKey(kv.Key.Side))
                .GroupBy(kv => kv.Key.Side)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<BattleSideRow>)[.. g
                    .Select(kv => new BattleSideRow(kv.Key.Corp, BattleSideKind.Corporation, _team[kv.Key.Side],
                        corpPilots.TryGetValue(kv.Key, out var set) ? set.Count : 0,
                        kv.Value.IskLost, kv.Value.ShipsLost, kv.Value.Damage, kv.Value.Kills,
                        _ourCorporations.Contains(kv.Key.Corp)))
                    .OrderByDescending(r => r.Pilots).ThenByDescending(r => r.Damage).ThenBy(r => r.SideId)]),
        };
    }

    /// <summary>Each hull type's flyers. Sums of the pilots' lines in that hull, so a level always
    /// adds up to the one above it.</summary>
    public static Dictionary<(int Team, int ShipTypeId), IReadOnlyList<BattleShipFlyer>> FlyersOf(IEnumerable<BattlePilotRow> rows)
    {
        static BattleShipFlyer Sum(long id, BattleSideKind kind, IReadOnlyCollection<BattlePilotRow> lines, IReadOnlyList<BattleShipFlyer> corps) =>
            new(id, kind, lines.Count, lines.Sum(l => l.Lost), lines.Sum(l => l.IskLost), lines.Sum(l => l.Damage), corps);

        static List<BattleShipFlyer> Largest(IEnumerable<BattleShipFlyer> flyers) =>
            [.. flyers.OrderByDescending(f => f.Pilots).ThenByDescending(f => f.Damage).ThenBy(f => f.Id)];

        return rows.Where(r => r.ShipTypeId > 0).GroupBy(r => (r.Team, r.ShipTypeId)).ToDictionary(
            hull => hull.Key,
            hull => (IReadOnlyList<BattleShipFlyer>)Largest(hull
                .GroupBy(r => r.AllianceId > 0 ? (Id: r.AllianceId, Kind: BattleSideKind.Alliance) : (Id: r.CorporationId, Kind: BattleSideKind.Corporation))
                .Select(g => Sum(g.Key.Id, g.Key.Kind, [.. g], g.Key.Kind != BattleSideKind.Alliance ? [] :
                    Largest(g.GroupBy(r => r.CorporationId).Select(c => Sum(c.Key, BattleSideKind.Corporation, [.. c], [])))))));
    }

    private const int Capsule = 670, GenolutionCapsule = 33328;

    /// <summary>Pilots' lines gathered into one family a pilot. What a pilot flew in the fight is the
    /// hull they did the most in, or failing that, lost the most in: not a pod, and not a line where
    /// the mail did not say, unless that is all there is.</summary>
    public static List<BattlePilotFamily> FamiliesOf(IEnumerable<BattlePilotRow> rows) =>
        [.. rows.GroupBy(r => (r.Team, r.CharacterId, Corp: r.CharacterId > 0 ? 0 : r.CorporationId, Ship: r.CharacterId > 0 ? 0 : r.ShipTypeId))
            .Select(g =>
            {
                var hulls = g.OrderByDescending(r => r.Damage).ThenByDescending(r => r.IskLost).ThenBy(r => r.ShipTypeId).ToList();
                var main = hulls
                    .OrderByDescending(r => r.ShipTypeId is > 0 and not Capsule and not GenolutionCapsule)
                    .ThenByDescending(r => r.Damage).ThenByDescending(r => r.IskLost).ThenBy(r => r.ShipTypeId).First();
                return new BattlePilotFamily(main with
                {
                    Damage     = hulls.Sum(r => r.Damage),
                    Kills      = hulls.Sum(r => r.Kills),
                    FinalBlows = hulls.Sum(r => r.FinalBlows),
                    Lost       = hulls.Sum(r => r.Lost),
                    IskLost    = hulls.Sum(r => r.IskLost),
                    IsOurs     = hulls.Any(r => r.IsOurs),
                }, hulls);
            })
            .OrderByDescending(f => f.Total.Damage).ThenByDescending(f => f.Total.IskLost)
            .ThenBy(f => f.Total.CharacterId).ThenBy(f => f.Total.CorporationId).ThenBy(f => f.Total.ShipTypeId)];

    private static SideSum CorpOf(Dictionary<(long, long), SideSum> corps, long side, long corporation)
    {
        if (!corps.TryGetValue((side, corporation), out var sum)) corps[(side, corporation)] = sum = new SideSum();
        return sum;
    }

    private long _version;

    private static SideSum Side(Dictionary<long, SideSum> sides, long id)
    {
        if (!sides.TryGetValue(id, out var sum)) sides[id] = sum = new SideSum();
        return sum;
    }

    /// <summary>A pilot's row in a hull; a structure, with no pilot, its owner's in that type.</summary>
    private static PilotSum Pilot(Dictionary<(int, long, long, int), PilotSum> pilots,
        int team, long character, long corporation, long alliance, int ship)
    {
        var key = (team, Math.Max(character, 0), character > 0 ? 0 : corporation, ship);
        if (!pilots.TryGetValue(key, out var sum)) pilots[key] = sum = new PilotSum();
        sum.CorporationId = corporation;
        sum.AllianceId    = alliance;
        return sum;
    }

    private sealed class TeamSum
    {
        public readonly HashSet<long> Pilots = [];
        public double IskLost;
        public int ShipsLost, PodsLost, StructuresLost, DeployablesLost;
        public long DamageDealt, StructureDamage;
    }

    private sealed class SideSum
    {
        public double IskLost;
        public int ShipsLost, Kills;
        public long Damage;
        private int _lastKill;

        /// <summary>One for a ship it was on, however many of its pilots were.</summary>
        public void CountKill(BattleKill kill)
        {
            if (kill.VictimClass != BattleHullClass.Ship || _lastKill == kill.KillMailId) return;
            _lastKill = kill.KillMailId;
            Kills++;
        }
    }

    private sealed class PilotSum
    {
        public long CorporationId, AllianceId, Damage;
        public int Kills, FinalBlows, Lost;
        public double IskLost;
    }
}
