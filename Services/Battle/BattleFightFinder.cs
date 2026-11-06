namespace EveConsole.Services.Battle;

/// <summary>A kill's place and time, all a first pass over a period needs.</summary>
public readonly record struct FightKillTime(int KillMailId, DateTimeOffset Time, int SystemId);

/// <summary>A stretch of kills in one system with no long quiet in it.</summary>
public sealed record FightRun(int SystemId, DateTimeOffset First, DateTimeOffset Last, IReadOnlyList<int> KillIds);

/// <summary>Someone on a kill: a pilot, and the side they were on.</summary>
public readonly record struct FightParticipant(long Pilot, long Side);

/// <summary>A fight found in the kills: one or more stretches, in one or more systems, sharing pilots.</summary>
public sealed record FoundFight(
    IReadOnlyList<FightRun> Runs, IReadOnlyList<BattleWindow> Windows,
    IReadOnlyList<int> KillIds, int Pilots, IReadOnlyList<long> LargestSides,
    DateTimeOffset First, DateTimeOffset Last, bool StillGoing, bool IsOurs)
{
    public double IskLost { get; init; }
}

/// <summary>
/// Finds large fights in a stretch of kills, with nothing of the database or the screen in it.
///
/// <para>A fight is where kills cluster in a system. Stretches in different systems are one fight
/// when they run at the same time and many of the same pilots are in both: a sov fight moves across
/// a constellation or several, and two battles at once on opposite sides of New Eden share nobody.
/// Distance is not asked about, since a fleet can bridge anywhere.</para>
///
/// <para>Sized by pilots, not kills: a few pilots farming a thousand cheap kills is no fight.</para>
/// </summary>
public static class BattleFightFinder
{
    /// <summary>A quiet this long in a system ends a stretch.</summary>
    public static readonly TimeSpan QuietGap = TimeSpan.FromMinutes(30);

    /// <summary>What a suggested window leaves either side of its first and last kills, so that it
    /// reads as a time someone would type, and does not cut a mail off by a few seconds.</summary>
    public static readonly TimeSpan Padding = TimeSpan.FromMinutes(5);

    /// <summary>Fewer kills than this in a stretch is not looked at further.</summary>
    public const int MinKillsInRun = 5;

    // ⚠️ Set against a month of real kills. At 5 pilots and 15% with no limit on length, one link at a
    // time joined a whole day's unrelated fights: 82 systems, 24 hours, called one fight.
    private const int MinSharedPilots = 10;
    private const double MinSharedShare = 0.25;

    /// <summary>How far apart in time two stretches can be and still be joined.</summary>
    private static readonly TimeSpan JoinGap = TimeSpan.FromMinutes(15);

    /// <summary>The longest joining will make a fight. Longer may well be real, and then shows as
    /// two suggestions, for the user to put together; one that swallowed a day could not be read.</summary>
    public static readonly TimeSpan MaxJoinedLength = TimeSpan.FromHours(12);

    /// <summary>Every kill's system and time, into stretches of at least <see cref="MinKillsInRun"/>.</summary>
    public static List<FightRun> Runs(IEnumerable<FightKillTime> kills)
    {
        var runs = new List<FightRun>();
        foreach (var system in kills.GroupBy(k => k.SystemId))
        {
            var ids = new List<int>();
            DateTimeOffset first = default, last = default;
            foreach (var kill in system.OrderBy(k => k.Time).ThenBy(k => k.KillMailId))
            {
                if (ids.Count > 0 && kill.Time - last > QuietGap)
                {
                    Close();
                    ids = [];
                }
                if (ids.Count == 0) first = kill.Time;
                ids.Add(kill.KillMailId);
                last = kill.Time;
            }
            Close();

            void Close()
            {
                if (ids.Count >= MinKillsInRun) runs.Add(new FightRun(system.Key, first, last, ids));
            }
        }
        return runs;
    }

    /// <summary>The stretches as fights of at least <paramref name="minPilots"/>. A fight whose last
    /// kill is recent has no end yet, so its windows are left open, ready to watch.</summary>
    public static List<FoundFight> Fights(
        IReadOnlyList<FightRun> runs, IReadOnlyDictionary<int, IReadOnlyList<FightParticipant>> participants,
        int minPilots, DateTimeOffset now, IReadOnlySet<long> ourPilots, IReadOnlySet<long> ourSides)
    {
        var ordered = runs.OrderBy(r => r.First).ThenBy(r => r.SystemId).ToList();
        var pilots = ordered.Select(r => r.KillIds
            .SelectMany(id => participants.TryGetValue(id, out var list) ? list : [])
            .Select(p => p.Pilot).ToHashSet()).ToList();

        // Who has joined whom, and the time each group spans so far.
        var parent = Enumerable.Range(0, ordered.Count).ToArray();
        var spans = ordered.Select(r => (r.First, r.Last)).ToArray();
        int Find(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                // In order of starting: from the first too late for this one, all are.
                if (ordered[j].First - ordered[i].Last > JoinGap) break;

                var (a, b) = (Find(i), Find(j));
                if (a == b || !ShareEnough(pilots[i], pilots[j])) continue;

                var start = spans[a].First < spans[b].First ? spans[a].First : spans[b].First;
                var end   = spans[a].Last > spans[b].Last ? spans[a].Last : spans[b].Last;
                if (end - start > MaxJoinedLength) continue;

                parent[b] = a;
                spans[a] = (start, end);
            }
        }

        var fights = new List<FoundFight>();
        foreach (var group in Enumerable.Range(0, ordered.Count).GroupBy(Find))
        {
            var members = group.Select(i => ordered[i]).ToList();
            var killIds = members.SelectMany(r => r.KillIds).ToList();
            var everyone = killIds.SelectMany(id => participants.TryGetValue(id, out var list) ? list : []).ToList();
            var distinct = everyone.Select(p => p.Pilot).ToHashSet();
            if (distinct.Count < minPilots) continue;

            var first = members.Min(r => r.First);
            var last  = members.Max(r => r.Last);
            var sides = everyone.Where(p => p.Side != 0).Distinct()
                .GroupBy(p => p.Side)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => g.Key).ToList();

            var windows = members.OrderBy(r => r.First).ThenBy(r => r.SystemId).Select(r =>
                new BattleWindow(r.SystemId, FloorMinute(r.First - Padding),
                    now - r.Last <= QuietGap ? null : CeilingMinute(r.Last + Padding))).ToList();

            fights.Add(new FoundFight(members, windows, killIds, distinct.Count, [.. sides.Take(3)], first, last,
                now - last <= QuietGap,
                distinct.Overlaps(ourPilots) || sides.Any(ourSides.Contains)));
        }
        return fights;
    }

    private static bool ShareEnough(HashSet<long> a, HashSet<long> b)
    {
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        if (small.Count == 0) return false;
        var shared = small.Count(large.Contains);
        return shared >= MinSharedPilots && shared >= small.Count * MinSharedShare;
    }

    private static DateTimeOffset FloorMinute(DateTimeOffset t)
    {
        var utc = t.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset CeilingMinute(DateTimeOffset t)
    {
        var floor = FloorMinute(t);
        return floor == t.ToUniversalTime() ? floor : floor.AddMinutes(1);
    }
}
