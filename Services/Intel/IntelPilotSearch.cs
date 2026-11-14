using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>What the Pilots tab found: the rows, and the names of the corporations and alliances (and of
/// pilots whose row has none) they need.</summary>
/// <param name="Hidden">Pilots read and left out by a standings chip switched off: what "N hidden by standings" says.
/// The user's own characters are left out too but not counted: no chip shows them, so none could bring them back.</param>
/// <param name="Capped">The rows stopped at <see cref="IntelPilotSearch.MaxRows"/> with more left in the window.</param>
public sealed record IntelPilotResults(IReadOnlyList<IntelLastSeen> Rows, IReadOnlyDictionary<long, string> Names,
                                       int Hidden = 0, bool Capped = false)
{
    public static readonly IntelPilotResults Empty = new([], new Dictionary<long, string>());
}

/// <summary>
/// The Intel tool's Pilots tab: where pilots were last seen, read straight from IntelLastSeen rather than
/// from the snapshot, because it goes back further (up to 60 days) than the live window. Always off the UI
/// thread, by whoever calls.
///
/// <para>The search: names starting with what was typed first; from three characters, names containing it
/// and pilots of a corporation or alliance whose name starts with it too. Newest first within each, at most
/// <see cref="MaxRows"/>.</para>
///
/// <para>With a standings filter (<c>judge</c>), the cap counts the pilots KEPT: the window is read newest first a
/// page at a time until <see cref="MaxRows"/> are kept or it runs out. A cap taken before the filter would let 500
/// blues seen lately hide every red behind them.</para>
///
/// <para>⚠️ Case is ignored the same way on both engines, by comparing lower-cased text: PostgreSQL's LIKE
/// minds case where SQLite's does not. The window's time is ISO text, compared as text on both.</para>
///
/// <para>⚠️ Ordered by SeenAt alone in the queries: both engines then walk the SeenAt index newest first and
/// stop at the cap. A second sort key makes them read every row in the window and sort it, which on sixty
/// days of a busy kill feed is what makes typing in the box lag. The pages keep to that: each starts below the
/// last one's oldest time, and the rows at that one time are read by themselves.</para>
/// </summary>
public static class IntelPilotSearch
{
    public const int MaxRows = 500;

    /// <summary>The shortest text a "contains" and a corporation or alliance search run for: shorter, they
    /// match nearly everyone and say nothing.</summary>
    public const int ContainsFrom = 3;

    /// <summary>Rows read per page when a filter leaves some out.</summary>
    public const int PageRows = 1000;

    /// <summary>The most rows one filtered search reads: past it, what was kept is shown and called capped. Sixty
    /// days of a busy feed with nearly everyone filtered out would otherwise read the whole table on every refresh.</summary>
    public const int MaxScanned = 40_000;

    public static async Task<IntelPilotResults> SearchAsync(AppDbContext db, string? text, DateTimeOffset since,
                                                            Func<IntelLastSeen, IntelPilotVerdict>? judge = null, CancellationToken ct = default)
    {
        var sinceText = IntelLastSeenWriter.Format(since);
        // The window's start is the reader's to add (see Reader): every query here is only what it looks for.
        var window = db.IntelLastSeens.AsNoTracking();
        var term = (text ?? "").Trim();
        var read = new Reader(judge, sinceText);
        List<IntelLastSeen> rows;
        if (term.Length == 0)
        {
            rows = await read.TakeAsync(window, MaxRows, ct).ConfigureAwait(false);
        }
        else
        {
            var lower = term.ToLowerInvariant();
            rows = await read.TakeAsync(window.Where(r => r.Name != null && r.Name.ToLower().StartsWith(lower)), MaxRows, ct).ConfigureAwait(false);
            if (term.Length >= ContainsFrom && rows.Count < MaxRows)
            {
                var have = rows.Select(r => r.CharacterId).ToHashSet();
                var want = MaxRows - rows.Count;
                var more = await read.TakeAsync(window.Where(r => r.Name != null && r.Name.ToLower().Contains(lower)), MaxRows, ct, have).ConfigureAwait(false);

                // A corporation or an alliance by its name, through the name cache.
                var groups = await db.UniverseNames.AsNoTracking()
                    .Where(u => (u.Category == "corporation" || u.Category == "alliance") && u.Name.ToLower().StartsWith(lower))
                    .Select(u => u.EntityId).Take(200).ToListAsync(ct).ConfigureAwait(false);
                if (groups.Count > 0)
                    more.AddRange(await read.TakeAsync(window
                        .Where(r => (r.CorporationId != null && groups.Contains(r.CorporationId.Value))
                                    || (r.AllianceId != null && groups.Contains(r.AllianceId.Value))), MaxRows, ct, have).ConfigureAwait(false));

                var merged = more.Where(r => have.Add(r.CharacterId))
                                 .OrderByDescending(r => r.SeenAt, StringComparer.Ordinal).ThenBy(r => r.CharacterId)
                                 .ToList();
                if (merged.Count > want) read.More = true;
                rows.AddRange(merged.Take(want));
            }
        }

        // The names a row shows and does not carry.
        var ids = rows.SelectMany(r => new[] { r.CorporationId ?? 0, r.AllianceId ?? 0, string.IsNullOrEmpty(r.Name) ? r.CharacterId : 0 })
            .Where(id => id > 0).Distinct().ToList();
        var names = new Dictionary<long, string>();
        foreach (var chunk in ids.Chunk(1000))
            foreach (var n in await db.UniverseNames.AsNoTracking().Where(u => chunk.Contains(u.EntityId))
                         .Select(u => new { u.EntityId, u.Name }).ToListAsync(ct).ConfigureAwait(false))
                names[n.EntityId] = n.Name;
        return new IntelPilotResults(rows, names, read.Hidden.Count, read.More || (judge is null && rows.Count >= MaxRows));
    }

    /// <summary>
    /// Reads a query newest first up to a number of rows kept. With no filter, one query with the cap, as it always
    /// was. With one, pages: each below the last page's oldest time, the rows at that time read by themselves (one
    /// second rarely holds many), each pilot once, until enough are kept, the window runs out or
    /// <see cref="MaxScanned"/> rows have been read.
    ///
    /// <para>⚠️ A page after the first is bounded by the last page's oldest time ALONE, never also by the window's
    /// start: the rows read past the start are dropped here, and they end the read. A text column bounded on both
    /// sides gets PostgreSQL's fixed guess of a few hundred rows whenever its statistics are missing or stale (a table
    /// just filled, before the next automatic ANALYZE), and with that guess it reads the whole range and sorts it
    /// for every page instead of walking the SeenAt index down to the page's end: 16 ms a page rather than 0.2, and
    /// a filtered search on a busy table took most of a second. Bounded on one side, the index walk is its plan
    /// whatever the statistics say.</para>
    /// </summary>
    private sealed class Reader(Func<IntelLastSeen, IntelPilotVerdict>? judge, string since)
    {
        /// <summary>Pilots read and left out by a chip switched off, each once whichever query read them. Own
        /// characters are left out without being counted.</summary>
        public HashSet<long> Hidden { get; } = [];

        /// <summary>A read stopped at its cap (or the scan's) with rows left in the window.</summary>
        public bool More { get; set; }

        private int _scanned;

        public async Task<List<IntelLastSeen>> TakeAsync(IQueryable<IntelLastSeen> query, int want, CancellationToken ct,
                                                         IReadOnlySet<long>? skip = null)
        {
            if (judge is null)
                return await query.Where(r => string.Compare(r.SeenAt, since) >= 0)
                    .OrderByDescending(r => r.SeenAt).Take(want).ToListAsync(ct).ConfigureAwait(false);

            var kept = new List<IntelLastSeen>();
            var seen = new HashSet<long>();
            string? below = null;
            while (true)
            {
                var page = below is null ? query.Where(r => string.Compare(r.SeenAt, since) >= 0)
                                         : query.Where(r => string.Compare(r.SeenAt, below) < 0);
                var rows = await page.OrderByDescending(r => r.SeenAt).Take(PageRows).Select(Judged).ToListAsync(ct).ConfigureAwait(false);
                var full = rows.Count == PageRows;

                // Past the window's start: read only to keep the page's query one-sided, and the end of the read.
                if (rows.RemoveAll(r => string.CompareOrdinal(r.SeenAt, since) < 0) > 0) full = false;

                if (full)
                {
                    // The page's oldest second may go on past it: that second whole, by itself, and the next page below it.
                    var last = rows[^1].SeenAt;
                    rows.RemoveAll(r => r.SeenAt == last);
                    rows.AddRange(await query.Where(r => r.SeenAt == last).Select(Judged).ToListAsync(ct).ConfigureAwait(false));
                    below = last;
                }

                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    if (!seen.Add(r.CharacterId) || skip?.Contains(r.CharacterId) == true) continue;
                    _scanned++;
                    var verdict = judge(r);
                    if (verdict == IntelPilotVerdict.Own) continue;
                    if (verdict == IntelPilotVerdict.Hidden) { Hidden.Add(r.CharacterId); continue; }
                    kept.Add(r);
                    if (kept.Count == want)
                    {
                        if (i < rows.Count - 1 || full) More = true;
                        return await WholeAsync(query, kept, ct).ConfigureAwait(false);
                    }
                }
                if (!full) return await WholeAsync(query, kept, ct).ConfigureAwait(false);
                if (_scanned >= MaxScanned) { More = true; return await WholeAsync(query, kept, ct).ConfigureAwait(false); }
            }
        }

        /// <summary>
        /// What the filter judges a row by, and the order: the pilot, the system, the corporation and alliance, and when.
        /// ⚠️ Only these while paging: a filtered read takes tens of thousands of rows to keep a few hundred, and reading
        /// each row whole cost more than finding it, most of all on PostgreSQL. The kept ones are read whole after.
        /// </summary>
        private static readonly System.Linq.Expressions.Expression<Func<IntelLastSeen, IntelLastSeen>> Judged = r => new IntelLastSeen
        {
            CharacterId = r.CharacterId, SeenAt = r.SeenAt, SystemId = r.SystemId, CorporationId = r.CorporationId, AllianceId = r.AllianceId,
        };

        /// <summary>The kept rows read whole, in the order kept. One that no longer matches (written over since) is left out.</summary>
        private static async Task<List<IntelLastSeen>> WholeAsync(IQueryable<IntelLastSeen> query, List<IntelLastSeen> kept, CancellationToken ct)
        {
            if (kept.Count == 0) return kept;
            var ids = kept.Select(r => r.CharacterId).ToList();
            var whole = (await query.Where(r => ids.Contains(r.CharacterId)).ToListAsync(ct).ConfigureAwait(false))
                .ToDictionary(r => r.CharacterId);
            return [.. kept.Where(r => whole.ContainsKey(r.CharacterId)).Select(r => whole[r.CharacterId])];
        }
    }

    /// <summary>A pilot's row, for their page's "Last seen" line; null when they were never seen.</summary>
    public static Task<IntelLastSeen?> OneAsync(AppDbContext db, long characterId, CancellationToken ct = default) =>
        db.IntelLastSeens.AsNoTracking().FirstOrDefaultAsync(r => r.CharacterId == characterId, ct);
}

/// <summary>
/// The Pilots tab's rows measured and coloured as the Board measures and colours its pilots: the jumps from the
/// nearest origin the walks reach, and the tier from that origin's character (the forced one first, else every own
/// character's friendliest when the origin is a system or the row is not reached yet). Pure and immutable, so the
/// search can filter with it off the UI thread.
/// </summary>
public sealed class IntelPilotFilter(IntelStandings standings,
                                     IReadOnlyList<(IntelOrigin Origin, IReadOnlyDictionary<int, IntelHop> Hops)> walks,
                                     IReadOnlySet<StandingTier> tiers)
{
    public IntelStandings Standings { get; } = standings;
    public IReadOnlySet<StandingTier> Tiers { get; } = tiers;

    /// <summary>The jumps, whether through a hole, the character they count from (null for a system), and the tier.</summary>
    public (int? Jumps, bool ViaWormhole, long? Nearest, StandingTier Tier) Place(IntelLastSeen r)
    {
        int? best = null; var via = false; long? from = null;
        foreach (var (o, hops) in walks)
        {
            if (!hops.TryGetValue(r.SystemId, out var h)) continue;
            if (best is null || h.Jumps < best || (h.Jumps == best && via && !h.ViaWormhole))
                (best, via, from) = (h.Jumps, h.ViaWormhole, o.CharacterId > 0 ? o.CharacterId : null);
        }
        return (best, via, from, Standings.TierFor(from, r.CharacterId, r.CorporationId ?? 0, r.AllianceId ?? 0));
    }

    /// <summary>Whether the standings chips show this pilot. Own characters are never shown, as on the Board.</summary>
    public bool Keeps(IntelLastSeen r) => Judge(r) == IntelPilotVerdict.Shown;

    /// <summary>Shown, hidden by a chip switched off, or one of the user's own characters (never shown, and not
    /// counted as hidden: no chip brings them back).</summary>
    public IntelPilotVerdict Judge(IntelLastSeen r) => Judge(Place(r).Tier);

    public IntelPilotVerdict Judge(StandingTier tier) =>
        tier == StandingTier.Own ? IntelPilotVerdict.Own
        : Tiers.Contains(tier) ? IntelPilotVerdict.Shown
        : IntelPilotVerdict.Hidden;
}

/// <summary>What the Pilots tab's standings filter makes of a pilot.</summary>
public enum IntelPilotVerdict { Shown, Hidden, Own }
