using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>One generator's contribution to a run, kept separate so a failure is attributable.</summary>
public sealed record WorklistSection(string SourceId, string DisplayName,
                                     List<WorklistItem> Items, string? Error);

public sealed record WorklistRun(List<WorklistSection> Sections, DateTimeOffset GeneratedAt)
{
    public IEnumerable<WorklistItem> AllItems => Sections.SelectMany(s => s.Items);
}

/// <summary>
/// Builds the worklist by asking every generator what needs doing, then layering on the small
/// amount of state that cannot be recomputed.
///
/// Generators run in parallel and are individually guarded: one throwing costs its own section
/// and nothing else, because a worklist that vanishes whenever a single rule has a bad day is
/// worse than one with a gap in it.
/// </summary>
public class WorklistService(
    IDbContextFactory<AppDbContext> dbFactory,
    IEnumerable<IWorklistGenerator> generators,
    AppErrorLogger                  errorLogger)
{
    private readonly List<IWorklistGenerator> _generators = generators.ToList();

    public IReadOnlyList<IWorklistGenerator> Generators => _generators;

    public async Task<WorklistRun> BuildAsync(CancellationToken ct = default)
    {
        var tasks = _generators.Select(async g =>
        {
            try
            {
                var items = await g.GenerateAsync(ct);
                return new WorklistSection(g.Id, g.DisplayName, items, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errorLogger.Log("WorklistService", g.Id, ex);
                return new WorklistSection(g.Id, g.DisplayName, [], ex.Message);
            }
        });

        var sections = (await Task.WhenAll(tasks)).ToList();
        await ApplyStateAsync(sections, ct);

        return new WorklistRun(sections, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Stamps first-seen and snooze onto freshly generated items, and records first-seen for
    /// keys never encountered before.
    ///
    /// Rows for keys that no longer generate are left alone rather than swept. They cost a few
    /// bytes, and keeping them means an item that comes back — a standing order that lapses
    /// again next month — is not misreported as brand new, nor its snooze quietly forgotten.
    /// </summary>
    private async Task ApplyStateAsync(List<WorklistSection> sections, CancellationToken ct)
    {
        var keys = sections.SelectMany(s => s.Items).Select(i => i.Key).Distinct().ToList();
        if (keys.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var states = await db.WorklistItemStates.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, ct);

        var now      = DateTimeOffset.UtcNow;
        var newState = new List<WorklistItemState>();

        for (int si = 0; si < sections.Count; si++)
        {
            var section = sections[si];
            for (int i = 0; i < section.Items.Count; i++)
            {
                var item = section.Items[i];
                if (states.TryGetValue(item.Key, out var st))
                {
                    section.Items[i] = item with
                    {
                        FirstSeenAt  = st.FirstSeenAt,
                        SnoozedUntil = st.SnoozedUntil,
                    };
                }
                else
                {
                    section.Items[i] = item with { FirstSeenAt = now };
                    newState.Add(new WorklistItemState { Key = item.Key, FirstSeenAt = now });
                }
            }
        }

        if (newState.Count == 0) return;

        db.WorklistItemStates.AddRange(newState);
        try { await db.SaveChangesAsync(ct); }
        catch (Exception ex)
        {
            // Losing a first-seen timestamp costs an "age" column, not the list.
            errorLogger.Log("WorklistService", "ApplyState", ex);
        }
    }

    /// <summary>Hides an item until a chosen time. Pass null to un-snooze.</summary>
    public async Task SnoozeAsync(string key, DateTimeOffset? until, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var row = await db.WorklistItemStates.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new WorklistItemState { Key = key, FirstSeenAt = DateTimeOffset.UtcNow };
            db.WorklistItemStates.Add(row);
        }

        row.SnoozedUntil = until;
        await db.SaveChangesAsync(ct);
    }
}
