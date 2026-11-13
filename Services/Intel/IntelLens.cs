namespace EveConsole.Services.Intel;

/// <summary>One place distances are measured from: a character, in the system it is in.</summary>
public readonly record struct IntelOrigin(long CharacterId, int SystemId);

/// <summary>
/// Who is looking, and what they want to see. A panel beside a client is one origin; the Intel tool
/// and the alarms are every online character, and each row is measured from the nearest of them.
/// </summary>
public sealed record IntelViewer
{
    public IReadOnlyList<IntelOrigin> Origins { get; init; } = [];

    /// <summary>Rows farther than this are left out; null for any distance.</summary>
    public int? MaxJumps { get; init; }

    public IntelGraphMode Mode { get; init; } = IntelGraphMode.Threat;

    /// <summary>The tiers shown. Unnamed counts are Neutral. Default: everyone not blue and not ours.</summary>
    public IReadOnlySet<StandingTier> Tiers { get; init; } = StandingTiers.Hostile;

    /// <summary>The sources shown, by <see cref="IntelEvent.SourceKey"/>; null for every source.</summary>
    public IReadOnlySet<string>? Sources { get; init; }

    public bool IncludeClear      { get; init; } = true;
    public bool IncludeKills      { get; init; } = true;

    /// <summary>Feed lines past their lifetime, faded: the tool's Feed only.</summary>
    public bool IncludeExpired    { get; init; }

    /// <summary>Feed lines a later one restated or replaced: the tool's Feed only.</summary>
    public bool IncludeSuperseded { get; init; }

    /// <summary>How far back the feed goes; null for the whole snapshot window.</summary>
    public TimeSpan? FeedWindow   { get; init; }

    /// <summary>The most rows kept, AFTER every filter: a panel 50, the tool 500.</summary>
    public int SystemCap { get; init; } = 500;
    public int FeedCap   { get; init; } = 500;

    /// <summary>The news that lights a row up: this new, within <see cref="FreshJumps"/>.</summary>
    public const int FreshJumps = 5;
}

/// <summary>A pilot on a row, with the tier the row's viewing character gives them.</summary>
public sealed record IntelPilotRow(PilotIntel Pilot, StandingTier Tier);

/// <summary>A feed line's pilot, with its tier.</summary>
public sealed record IntelEventPilotRow(IntelEventPilot Pilot, StandingTier Tier);

/// <summary>One system as a viewer sees it.</summary>
/// <param name="NearestOrigin">The character the jumps are counted from, for "3 jumps from …".</param>
/// <param name="StandingsFrom">The character whose standings gave the colours; null for the rule
/// with no viewer.</param>
/// <param name="Tier">The worst tier among its named pilots and its camps' attackers; an unnamed count
/// is Neutral.</param>
/// <param name="Camps">The active camps with at least one attacker of a tier shown.</param>
/// <param name="IsFresh">News within the fresh time and within five jumps: what lights a panel row,
/// and what an alarm would wake for.</param>
public sealed record IntelSystemRow(
    int SystemId, int? Jumps, bool ViaWormhole, long? NearestOrigin, long? StandingsFrom,
    StandingTier Tier, IntelSystemState State, double Freshness, double Opacity, bool IsFresh,
    IReadOnlyList<IntelPilotRow> Pilots, int Unnamed, bool UnnamedNoVisual,
    IReadOnlyList<IntelFact> Facts, IReadOnlyList<GateCamp> Camps,
    string? Gate, int? HeadingSystemId, DateTimeOffset NewestAt, DateTimeOffset ExpiresAt, SystemIntel Intel);

/// <summary>One feed line as a viewer sees it, with its follow-ups nested under it.</summary>
public sealed record IntelFeedRow(
    IntelEvent Event, int? Jumps, bool ViaWormhole, long? NearestOrigin, long? StandingsFrom,
    StandingTier Tier, IReadOnlyList<IntelEventPilotRow> Pilots,
    double Freshness, double Opacity, bool Expired, bool IsFresh, IReadOnlyList<IntelFeedRow> FollowUps);

/// <param name="Nearest">The nearest live threat: the panel's single line.</param>
/// <param name="MissingOrigins">Origins whose distances were not ready: the caller asks for them off
/// the UI thread and projects again. Rows meanwhile have no jumps ("…").</param>
public sealed record IntelView(
    IReadOnlyList<IntelSystemRow> Systems, IReadOnlyList<IntelFeedRow> Feed, IntelSystemRow? Nearest,
    IReadOnlyList<IntelOrigin> MissingOrigins)
{
    public bool Pending => MissingOrigins.Count > 0;
    public static readonly IntelView Empty = new([], [], null, []);
}

/// <summary>
/// The per-viewer projection of the shared snapshot: standings, distance, filters, fade and order.
/// Pure and cheap, with no Avalonia types: the panel, the tool, the map and the alarms all use it, and
/// tools/IntelCheck runs it on snapshots built by hand.
///
/// <para>⚠️ Filter first, cap after. A cap before the distance and source filters lets a busy kill
/// feed from the far side of New Eden push out every report next door.</para>
///
/// <para>Fade: full strength for the first third of a lifetime, then down to 40% at its end; a line
/// past its lifetime (the tool's Feed only) at 30%.</para>
/// </summary>
public static class IntelLens
{
    public const double ExpiredOpacity = 0.3;
    public const double EndOpacity     = 0.4;

    /// <summary>1 when new, 0 at the end of its lifetime.</summary>
    public static double Freshness(DateTimeOffset at, TimeSpan lifetime, DateTimeOffset now) =>
        lifetime <= TimeSpan.Zero ? 0 : Math.Clamp(1 - (now - at) / lifetime, 0, 1);

    /// <summary>How strongly a line is drawn at its age.</summary>
    public static double Opacity(DateTimeOffset at, TimeSpan lifetime, DateTimeOffset now)
    {
        var age = now - at;
        if (age >= lifetime) return ExpiredOpacity;
        var third = lifetime / 3;
        if (age <= third) return 1.0;
        return 1.0 - (1.0 - EndOpacity) * ((age - third) / (lifetime - third));
    }

    /// <summary>The projection with the distances the service has cached; never waits.</summary>
    public static IntelView Project(IntelSnapshot snapshot, IntelViewer viewer, IntelDistanceService? distances, DateTimeOffset now) =>
        Project(snapshot, viewer, now, origin => distances?.TryCached(origin.SystemId, viewer.Mode,
            snapshot.Standings.Own(origin.CharacterId)?.AllianceId is long a and > 0 ? a : null));

    /// <summary>The projection, with distances from <paramref name="hopsFrom"/> (null for a walk not
    /// ready yet).</summary>
    public static IntelView Project(IntelSnapshot snapshot, IntelViewer viewer, DateTimeOffset now,
                                    Func<IntelOrigin, IReadOnlyDictionary<int, IntelHop>?> hopsFrom)
    {
        var standings = snapshot.Standings;
        var walks = new List<(IntelOrigin Origin, IReadOnlyDictionary<int, IntelHop> Hops)>();
        var missing = new List<IntelOrigin>();
        foreach (var o in viewer.Origins.Distinct())
        {
            if (hopsFrom(o) is { } hops) walks.Add((o, hops));
            else missing.Add(o);
        }

        // Jumps from the nearest origin; at the same distance, a way through gates over one through a hole.
        (int? Jumps, bool Via, long? Origin, bool Drop) Distance(int systemId)
        {
            int? best = null; var via = false; long? from = null;
            foreach (var (o, hops) in walks)
            {
                if (!hops.TryGetValue(systemId, out var h)) continue;
                if (best is null || h.Jumps < best || (h.Jumps == best && via && !h.ViaWormhole))
                    (best, via, from) = (h.Jumps, h.ViaWormhole, o.CharacterId);
            }
            var drop = viewer.MaxJumps is int max && viewer.Origins.Count > 0
                && (best is int j ? j > max : missing.Count == 0);
            return (best, via, from, drop);
        }

        StandingTier Tier(long? viewerId, long pilot, long corp, long alliance) =>
            standings.TierFor(viewerId, pilot, corp, alliance);

        bool Fresh(DateTimeOffset newest, int? jumps) =>
            jumps is int j && j <= IntelViewer.FreshJumps && now - newest <= TimeSpan.FromSeconds(snapshot.Settings.FreshSeconds);

        // ── Systems ──────────────────────────────────────────────────────────
        var systems = new List<IntelSystemRow>();
        foreach (var sys in snapshot.Systems.Values)
        {
            var (jumps, via, origin, drop) = Distance(sys.SystemId);
            if (drop) continue;
            var viewerId = standings.EffectiveViewer(origin);

            var pilots = sys.PilotIds
                .Select(id => snapshot.Pilots.GetValueOrDefault(id))
                .Where(p => p is not null && p.SystemId == sys.SystemId && p.ExpiresAt > now)
                .Select(p => new IntelPilotRow(p!, Tier(origin, p!.CharacterId, p.CorporationId, p.AllianceId)))
                .Where(p => viewer.Tiers.Contains(p.Tier))
                .OrderBy(p => p.Tier).ThenByDescending(p => p.Pilot.At)
                .ToList();
            var unnamed = sys.Unnamed is { } u && u.ExpiresAt > now && viewer.Tiers.Contains(StandingTier.Neutral) ? u : null;
            var facts = sys.Facts.Where(f => f.ExpiresAt > now).ToList();
            // A camp is judged by its attackers, as its feed line is: kept when any of them is a tier
            // shown, at the worst of those. A camp of blues killing hostiles on a gate is no threat.
            var campTiers = new List<StandingTier>();
            var camps = new List<GateCamp>();
            foreach (var camp in sys.Camps.Where(c => c.IsActive(now)))
            {
                var shownTiers = camp.Attackers.Count == 0
                    ? [StandingTier.Neutral]
                    : camp.Attackers.Select(a => Tier(origin, a.CharacterId, a.CorporationId, a.AllianceId)).ToList();
                shownTiers.RemoveAll(t => !viewer.Tiers.Contains(t));
                if (shownTiers.Count == 0) continue;
                camps.Add(camp);
                campTiers.Add(shownTiers.Min());
            }

            // (time, lifetime) of everything the row shows, for its age and fade.
            var items = new List<(DateTimeOffset At, TimeSpan Life)>();
            items.AddRange(pilots.Select(p => (p.Pilot.At, p.Pilot.Lifetime)));
            if (unnamed is not null) items.Add((unnamed.At, unnamed.Lifetime));
            items.AddRange(facts.Select(f => (f.At, f.Lifetime)));
            items.AddRange(camps.Select(c => (c.LastKill, c.ExpiresAt - c.LastKill)));

            IntelSystemState state;
            if (pilots.Count > 0 || unnamed is not null)
                state = pilots.All(p => p.Pilot.NoVisual) && (unnamed?.NoVisual ?? true) ? IntelSystemState.NoVisual
                                                                                         : IntelSystemState.Hostile;
            else if (facts.Count > 0 || camps.Count > 0)
                state = IntelSystemState.FlagsOnly;
            else if (viewer.IncludeClear && sys.Clear is { } c && c.ExpiresAt > now)
            {
                state = IntelSystemState.Clear;
                items.Add((c.At, c.Lifetime));
            }
            else continue;

            var newest  = items.Max(i => i.At);
            var expires = items.Max(i => i.At + i.Life);
            var fresh   = items.Max(i => Freshness(i.At, i.Life, now));
            var opacity = items.Max(i => Opacity(i.At, i.Life, now));
            var tiers = pilots.Select(p => p.Tier).Concat(campTiers).ToList();
            var tier = tiers.Count > 0 ? tiers.Min() : StandingTier.Neutral;
            if (unnamed is not null && tier > StandingTier.Neutral) tier = StandingTier.Neutral;

            systems.Add(new IntelSystemRow(
                sys.SystemId, jumps, via, origin, viewerId, tier, state, fresh, opacity,
                state != IntelSystemState.Clear && Fresh(newest, jumps),
                pilots, unnamed?.Count ?? 0, unnamed?.NoVisual ?? false, facts, camps,
                facts.Count > 0 || pilots.Count > 0 || unnamed is not null ? sys.Gate : null,
                sys.HeadingSystemId, newest, expires, sys));
        }

        systems = [.. systems
            .OrderBy(r => r.Jumps is null ? 1 : 0).ThenBy(r => r.Jumps ?? 0)
            .ThenByDescending(r => r.NewestAt).ThenBy(r => r.SystemId)
            .Take(viewer.SystemCap)];

        var nearest = systems
            .Where(r => r.State is IntelSystemState.Hostile or IntelSystemState.NoVisual)
            .OrderBy(r => r.Jumps is null ? 1 : 0).ThenBy(r => r.Jumps ?? 0).ThenByDescending(r => r.NewestAt)
            .FirstOrDefault();

        // ── Feed ─────────────────────────────────────────────────────────────
        var rows = new List<IntelFeedRow>();
        var since = viewer.FeedWindow is { } window ? now - window : DateTimeOffset.MinValue;
        foreach (var e in snapshot.Feed)
        {
            if (e.At < since) continue;
            if (viewer.Sources is { } sources && !sources.Contains(e.SourceKey)) continue;
            if (!viewer.IncludeKills && e.Kind is IntelEventKind.Kill or IntelEventKind.CampFormed) continue;
            if (!viewer.IncludeClear && e.Kind == IntelEventKind.Clear) continue;
            var expired = now >= e.ExpiresAt;
            if (expired && !viewer.IncludeExpired) continue;
            if (e.Superseded && !viewer.IncludeSuperseded) continue;

            var (jumps, via, origin, drop) = Distance(e.SystemId);
            if (drop) continue;

            var pilots = e.Pilots.Select(p => new IntelEventPilotRow(p, Tier(origin, p.CharacterId, p.CorporationId, p.AllianceId))).ToList();
            var shown = pilots.Where(p => viewer.Tiers.Contains(p.Tier)).ToList();
            var visible = e.Kind == IntelEventKind.Clear
                || shown.Count > 0
                || (e.Count > 0 && viewer.Tiers.Contains(StandingTier.Neutral))
                || (pilots.Count == 0 && e.Kind is not IntelEventKind.Kill and not IntelEventKind.CampFormed);
            if (!visible) continue;

            var tier = shown.Count > 0 ? shown.Min(p => p.Tier) : StandingTier.Neutral;
            if (e.Count > 0 && tier > StandingTier.Neutral) tier = StandingTier.Neutral;
            rows.Add(new IntelFeedRow(
                e, jumps, via, origin, standings.EffectiveViewer(origin), tier, shown,
                Freshness(e.At, e.Lifetime, now), expired ? ExpiredOpacity : Opacity(e.At, e.Lifetime, now), expired,
                !expired && e.Kind != IntelEventKind.Clear && Fresh(e.At, jumps), []));
        }

        // Follow-ups under the first report of their thread, when it is shown too: one level.
        var byKey = rows.Where(r => r.Event.ParentReportId is null && r.Event.ReportId is not null)
                        .ToDictionary(r => r.Event.ReportId!.Value);
        var children = new Dictionary<int, List<IntelFeedRow>>();
        var top = new List<IntelFeedRow>();
        foreach (var r in rows)
        {
            if (r.Event.ParentReportId is int head && byKey.ContainsKey(head))
            {
                if (!children.TryGetValue(head, out var list)) children[head] = list = [];
                list.Add(r);
            }
            else top.Add(r);
        }
        var feed = top
            .OrderByDescending(r => r.Event.At).ThenByDescending(r => r.Event.Key, StringComparer.Ordinal)
            .Take(viewer.FeedCap)
            .Select(r => r.Event.ReportId is int id && children.TryGetValue(id, out var kids)
                ? r with { FollowUps = [.. kids.OrderByDescending(k => k.Event.At)] }
                : r)
            .ToList();

        return new IntelView(systems, feed, nearest, missing);
    }
}
