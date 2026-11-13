using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>What the Pilots tab found: the rows, and the names of the corporations and alliances (and of
/// pilots whose row has none) they need.</summary>
public sealed record IntelPilotResults(IReadOnlyList<IntelLastSeen> Rows, IReadOnlyDictionary<long, string> Names)
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
/// <para>⚠️ Case is ignored the same way on both engines, by comparing lower-cased text: PostgreSQL's LIKE
/// minds case where SQLite's does not. The window's time is ISO text, compared as text on both.</para>
///
/// <para>⚠️ Ordered by SeenAt alone in the queries: both engines then walk the SeenAt index newest first and
/// stop at the cap. A second sort key makes them read every row in the window and sort it, which on sixty
/// days of a busy kill feed is what makes typing in the box lag.</para>
/// </summary>
public static class IntelPilotSearch
{
    public const int MaxRows = 500;

    /// <summary>The shortest text a "contains" and a corporation or alliance search run for: shorter, they
    /// match nearly everyone and say nothing.</summary>
    public const int ContainsFrom = 3;

    public static async Task<IntelPilotResults> SearchAsync(AppDbContext db, string? text, DateTimeOffset since, CancellationToken ct = default)
    {
        var sinceText = IntelLastSeenWriter.Format(since);
        var window = db.IntelLastSeens.AsNoTracking().Where(r => string.Compare(r.SeenAt, sinceText) >= 0);
        var term = (text ?? "").Trim();
        List<IntelLastSeen> rows;
        if (term.Length == 0)
        {
            rows = await window.OrderByDescending(r => r.SeenAt).Take(MaxRows).ToListAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var lower = term.ToLowerInvariant();
            rows = await window.Where(r => r.Name != null && r.Name.ToLower().StartsWith(lower))
                .OrderByDescending(r => r.SeenAt).Take(MaxRows).ToListAsync(ct).ConfigureAwait(false);
            if (term.Length >= ContainsFrom && rows.Count < MaxRows)
            {
                var have = rows.Select(r => r.CharacterId).ToHashSet();
                var more = await window.Where(r => r.Name != null && r.Name.ToLower().Contains(lower))
                    .OrderByDescending(r => r.SeenAt).Take(MaxRows).ToListAsync(ct).ConfigureAwait(false);

                // A corporation or an alliance by its name, through the name cache.
                var groups = await db.UniverseNames.AsNoTracking()
                    .Where(u => (u.Category == "corporation" || u.Category == "alliance") && u.Name.ToLower().StartsWith(lower))
                    .Select(u => u.EntityId).Take(200).ToListAsync(ct).ConfigureAwait(false);
                if (groups.Count > 0)
                    more.AddRange(await window
                        .Where(r => (r.CorporationId != null && groups.Contains(r.CorporationId.Value))
                                    || (r.AllianceId != null && groups.Contains(r.AllianceId.Value)))
                        .OrderByDescending(r => r.SeenAt).Take(MaxRows).ToListAsync(ct).ConfigureAwait(false));

                rows.AddRange(more.Where(r => have.Add(r.CharacterId))
                                  .OrderByDescending(r => r.SeenAt, StringComparer.Ordinal).ThenBy(r => r.CharacterId)
                                  .Take(MaxRows - rows.Count));
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
        return new IntelPilotResults(rows, names);
    }

    /// <summary>A pilot's row, for their page's "Last seen" line; null when they were never seen.</summary>
    public static Task<IntelLastSeen?> OneAsync(AppDbContext db, long characterId, CancellationToken ct = default) =>
        db.IntelLastSeens.AsNoTracking().FirstOrDefaultAsync(r => r.CharacterId == characterId, ct);
}
