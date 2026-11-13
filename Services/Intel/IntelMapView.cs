namespace EveConsole.Services.Intel;

/// <summary>A report's direction drawn on the map: a solid arrow for where a pilot came from (From → the
/// system they were seen in), a dashed one for where they were said to be going (the system → Heading).</summary>
/// <param name="Freshness">1 when new, 0 at the end of the report's lifetime: what the arrow fades by.</param>
/// <param name="Tier">The worst tier the report named, as the map judges standings.</param>
public sealed record IntelMapArrow(
    int FromSystemId, int ToSystemId, bool Heading, DateTimeOffset At, double Freshness, StandingTier Tier, IntelEvent Event);

/// <summary>An active gate camp drawn on the map, on the line of the gate it is on.</summary>
/// <param name="Tier">The worst tier among its attackers that the map shows.</param>
public sealed record IntelMapCamp(GateCamp Camp, double Freshness, StandingTier Tier)
{
    public int SystemId     => Camp.SystemId;
    public int GateSystemId => Camp.GateSystemId;
}

/// <summary>What intel holds on one system of a planned route, as the route's character sees it.</summary>
/// <param name="Hostiles">Pilots placed there and counted, not blue and not ours.</param>
/// <param name="Camp">An active gate camp in the system.</param>
public sealed record IntelRouteMark(
    int SystemId, int Hostiles, StandingTier Tier, IntelSystemState State, bool Camp, bool NoVisual,
    DateTimeOffset NewestAt, IntelSystemRow Row);

/// <summary>
/// The Universe Map's and the route planner's reading of the shared snapshot, through the same lens as
/// every intel view: tier colours, fade, the clear and no-visual states, camps on their gates, and the
/// direction reports gave. Pure, with no Avalonia types: tools/IntelCheck runs it on snapshots made by hand.
/// </summary>
public static class IntelMapView
{
    /// <summary>The most arrows drawn at once, newest first: more is a tangle, not a picture.</summary>
    public const int MaxArrows = 30;

    /// <summary>
    /// What the map sees: every system, no distance (the map has no single origin), the clear state on,
    /// and every live report line, for its arrows. Kills are left out of the feed: the map draws a kill
    /// as the pilots it placed and the camp it made, not as a line.
    /// </summary>
    public static IntelViewer Viewer(long? standingsCharacterId = null) => new()
    {
        IncludeClear = true, IncludeKills = false, SystemCap = int.MaxValue, FeedCap = int.MaxValue,
        StandingsCharacterId = standingsCharacterId,
    };

    /// <summary>The map's projection of a snapshot at <paramref name="now"/>.</summary>
    public static IntelView Project(IntelSnapshot snapshot, DateTimeOffset now, long? standingsCharacterId = null) =>
        IntelLens.Project(snapshot, Viewer(standingsCharacterId), now, _ => null);

    /// <summary>
    /// The arrows of the live reports: a solid one from where a pilot came from to where they were seen, a
    /// dashed one from there to where they were said to be heading. Only while the system the report placed
    /// them in still shows something (a clear, or the pilots gone, takes the arrow with them), never for a
    /// line a later one restated, one per pair and kind, newest first, at most <see cref="MaxArrows"/>.
    /// </summary>
    public static IReadOnlyList<IntelMapArrow> Arrows(IntelView view)
    {
        var live = view.Systems.Where(r => r.State != IntelSystemState.Clear).Select(r => r.SystemId).ToHashSet();
        var arrows = new List<IntelMapArrow>();
        foreach (var row in Flatten(view.Feed))
        {
            var e = row.Event;
            if (row.Expired || e.Superseded) continue;
            if (e.Kind is IntelEventKind.Clear or IntelEventKind.Kill or IntelEventKind.CampFormed) continue;
            if (!live.Contains(e.SystemId)) continue;
            if (e.FromSystemId is int from && from > 0 && from != e.SystemId)
                arrows.Add(new IntelMapArrow(from, e.SystemId, false, e.At, row.Freshness, row.Tier, e));
            if (e.HeadingSystemId is int to && to > 0 && to != e.SystemId)
                arrows.Add(new IntelMapArrow(e.SystemId, to, true, e.At, row.Freshness, row.Tier, e));
        }
        return [.. arrows
            .GroupBy(a => (a.FromSystemId, a.ToSystemId, a.Heading))
            .Select(g => g.OrderByDescending(a => a.At).ThenByDescending(a => a.Event.Key, StringComparer.Ordinal).First())
            .OrderByDescending(a => a.At).ThenBy(a => a.FromSystemId).ThenBy(a => a.ToSystemId)
            .Take(MaxArrows)];
    }

    /// <summary>
    /// The active camps the map shows, each at the worst tier among its attackers that the viewer shows: the
    /// lens has already dropped a camp of blues, and an ended one.
    /// </summary>
    public static IReadOnlyList<IntelMapCamp> Camps(IntelSnapshot snapshot, IntelView view, IntelViewer viewer, DateTimeOffset now)
    {
        var camps = new List<IntelMapCamp>();
        foreach (var row in view.Systems)
            foreach (var camp in row.Camps)
            {
                List<StandingTier> tiers = camp.Attackers.Count == 0
                    ? [StandingTier.Neutral]
                    : camp.Attackers.Select(a => snapshot.Standings.TierFor(row.StandingsFrom ?? viewer.StandingsCharacterId,
                                                                             a.CharacterId, a.CorporationId, a.AllianceId))
                                    .Where(viewer.Tiers.Contains).ToList();
                camps.Add(new IntelMapCamp(camp, IntelLens.Freshness(camp.LastKill, camp.ExpiresAt - camp.LastKill, now),
                                           tiers.Count > 0 ? tiers.Min() : StandingTier.Neutral));
            }
        return [.. camps.OrderByDescending(c => c.Camp.LastKill).ThenBy(c => c.Camp.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The hostiles column of the route planner: for each system of the route that intel holds anything live
    /// on, how many pilots are there, at what tier, and whether a gate camp is on. Judged by the route's own
    /// character's standings (the forced one when Settings → Intel sets one, the friendliest of the user's
    /// characters when none is chosen). A system only called clear is not listed.
    /// </summary>
    public static IReadOnlyDictionary<int, IntelRouteMark> Route(
        IntelSnapshot snapshot, IEnumerable<int> systems, long? characterId, DateTimeOffset now)
    {
        var wanted = systems.ToHashSet();
        var view = IntelLens.Project(snapshot,
            new IntelViewer { IncludeClear = false, FeedCap = 0, SystemCap = int.MaxValue, StandingsCharacterId = characterId },
            now, _ => null);
        var marks = new Dictionary<int, IntelRouteMark>();
        foreach (var row in view.Systems)
        {
            if (!wanted.Contains(row.SystemId)) continue;
            marks[row.SystemId] = new IntelRouteMark(
                row.SystemId, row.Pilots.Count + row.Unnamed, row.Tier, row.State, row.Camps.Count > 0,
                row.State == IntelSystemState.NoVisual, row.NewestAt, row);
        }
        return marks;
    }

    private static IEnumerable<IntelFeedRow> Flatten(IEnumerable<IntelFeedRow> rows)
    {
        foreach (var r in rows)
        {
            yield return r;
            foreach (var f in r.FollowUps) yield return f;
        }
    }
}
