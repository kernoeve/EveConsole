using System.Text.Json;
using EveConsole.Alarms.Conditions;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>
/// The intel alarms' reach, for the Universe Map's alert zone outline: every system an enabled intel
/// alarm, inside its active hours, watches now.
///
/// <para>⚠️ Through <see cref="IntelCondition.WatchedSystemsAsync"/>, the function the alarm's own check
/// matches against, so the outline is exactly what would fire and cannot drift from it as the alarm
/// learns new reaches (priority systems, a route ahead).</para>
///
/// <para>Read off the UI thread, on the map's own slow cadence and when a character moves: it walks the
/// gate graph once per alarm.</para>
/// </summary>
public sealed class IntelZoneService(
    IDbContextFactory<AppDbContext> dbFactory, SystemGraph graph, AppErrorLogger? errors = null,
    IntelDistanceService? distances = null, IntelSnapshotService? snapshots = null)
{
    public const string IntelConditionType = "intel";

    /// <summary>The union of every enabled intel alarm's watched systems; empty when there is none.</summary>
    public async Task<IReadOnlySet<int>> ZoneAsync(CancellationToken ct = default)
    {
        var zone = new HashSet<int>();
        List<(long Id, string Json)> alarms;
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var rows = await db.Alarms.AsNoTracking()
                .Where(a => a.Enabled && a.ConditionType == IntelConditionType)
                .ToListAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.Now;
            alarms = [.. rows.Where(a => a.IsActiveAt(now)).Select(a => (a.Id, a.ConditionJson))];
        }

        // Where the characters are as the alarm sees them: the snapshot's own positions, once it has read.
        var snapshot = snapshots?.Current;
        var own = snapshot is { } s && s.At != DateTimeOffset.MinValue ? s.Own : null;

        foreach (var (id, json) in alarms)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var watched = await IntelCondition.WatchedSystemsAsync(doc.RootElement, graph, dbFactory, distances, own, ct)
                    .ConfigureAwait(false);
                zone.UnionWith(watched.Systems);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // One alarm whose settings cannot be read leaves the others' outline standing.
                errors?.Log(nameof(IntelZoneService), $"alarm {id}'s watched systems", ex);
            }
        }
        return zone;
    }
}
