using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Copies what ESI resolved into the app's own <see cref="Structure"/> table.
///
/// <para>One direction only. <c>EsiStructureNames</c> is the polling service's, rewritten on every
/// resolve; <c>Structures</c> is the user's, edited in the Structure Browser. Nothing here ever
/// writes back, so a hand-typed name can never travel into polled data and be mistaken for
/// something ESI said.</para>
///
/// <para>⚠️ An ESI refresh overwrites the fields ESI owns, including ones the user has edited, and
/// that is the agreed behaviour: for a structure we can read, ESI is right and the user's guess is
/// stale. What it must NOT do is discard the parts ESI cannot know — notes, fitted service modules
/// and rigs — so those are left alone here and live in their own tables.</para>
/// </summary>
public class StructureSyncService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
{
    /// <summary>Rows written on the last run, for the caller's status line.</summary>
    public int LastSynced { get; private set; }

    /// <summary>SDE category 65 — Structure. The only category a player structure can be.</summary>
    private const int StructureCategoryId = 65;

    /// <summary>
    /// Ids that our own assets prove are NOT player structures.
    ///
    /// <para>⚠️ Positive identification, not inference. <c>EsiStructureNames.TypeId</c> is written
    /// only by <c>/universe/structures/{id}</c>, which never succeeds for a ship — so it stays 0
    /// and the row looks merely "unresolved". The asset row for the same id has carried the real
    /// type all along; nothing ever joined the two. Measured on the live database, that identifies
    /// 342 rows outright: ~230 ships by hull group, 42 Asset Safety Wraps, 33 station services and
    /// 23 containers.</para>
    ///
    /// <para>Only acts where an asset row exists AND says something other than category 65, so a
    /// structure we merely cannot read is never touched — and our own structures, which DO appear
    /// as assets, identify as 65 and are kept. An earlier rule of "appears as an ItemId ⇒ not a
    /// structure" would have deleted all 49 of them.</para>
    /// </summary>
    private static async Task<HashSet<long>> IdentifyNonStructuresAsync(
        AppDbContext db, CancellationToken ct)
    {
        var rows = await (
            from a in db.EsiAssets.AsNoTracking()
            join t in db.SdeTypes.AsNoTracking()  on a.TypeId  equals t.TypeId
            join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
            where g.CategoryId != StructureCategoryId
            select a.ItemId).Distinct().ToListAsync(ct);

        return rows.ToHashSet();
    }

    /// <summary>
    /// Removes things from the structure tables that our assets prove are not structures, and
    /// reports how many went.
    ///
    /// <para>They arrived through the seeding heuristic — "a LocationId over 1T that is not an
    /// ItemId in THIS asset list" — which cannot see a ship whose own row landed on a different
    /// page or under a different character. An Asset Safety Wrap is the clearest case: the wrap
    /// and its contents routinely arrive separately.</para>
    /// </summary>
    public async Task<int> PurgeNonStructuresAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var notStructures = await IdentifyNonStructuresAsync(db, ct);
            if (notStructures.Count == 0) return 0;

            var doomed = (await db.EsiStructureNames.AsNoTracking()
                .Select(s => s.StructureId).ToListAsync(ct))
                .Where(notStructures.Contains)
                .ToList();

            if (doomed.Count == 0) return 0;

            await db.EsiStructureNames.Where(s => doomed.Contains(s.StructureId)).ExecuteDeleteAsync(ct);
            await db.EsiStructureNameFailures.Where(f => doomed.Contains(f.StructureId)).ExecuteDeleteAsync(ct);

            // The app's own table too, along with anything hung off it — a ship cannot have had
            // meaningful service modules or rigs recorded against it.
            await db.StructureServiceModules.Where(m => doomed.Contains(m.StructureId)).ExecuteDeleteAsync(ct);
            await db.StructureRigs.Where(r => doomed.Contains(r.StructureId)).ExecuteDeleteAsync(ct);
            await db.Structures.Where(s => doomed.Contains(s.StructureId)).ExecuteDeleteAsync(ct);

            return doomed.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(StructureSyncService), nameof(PurgeNonStructuresAsync), ex);
            return 0;
        }
    }

    /// <summary>
    /// Filters candidate ids before they are seeded, so identified non-structures never enter the
    /// table in the first place. The purge above cleans up what is already there; this is what
    /// stops it coming back on the next asset poll.
    /// </summary>
    public async Task<List<long>> RejectNonStructuresAsync(
        AppDbContext db, IEnumerable<long> candidateIds, CancellationToken ct = default)
    {
        var notStructures = await IdentifyNonStructuresAsync(db, ct);
        return candidateIds.Where(id => !notStructures.Contains(id)).ToList();
    }

    /// <summary>
    /// Brings <c>Structures</c> up to date with <c>EsiStructureNames</c>.
    ///
    /// <para>Insert and update are deliberately different rules:</para>
    /// <list type="bullet">
    /// <item>EVERY structure id we have seen gets a row here, resolved or not. A structure we
    /// cannot read is exactly the one worth describing by hand, so it has to be present to be
    /// edited — an empty row is an invitation, and its absence would mean the user has to add by
    /// id something the app already knows exists.</item>
    /// <item>An EXISTING row is only overwritten from a resolved lookup. Copying the blanks of a
    /// Pending or NoAccess row over a description someone typed is the one thing this must never
    /// do — and the case it would happen in, a structure we lost access to, is precisely when the
    /// hand-written version is all that is left.</item>
    /// </list>
    /// </summary>
    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var all = await db.EsiStructureNames.AsNoTracking().ToListAsync(ct);
            if (all.Count == 0) { LastSynced = 0; return 0; }

            var existing = await db.Structures.ToDictionaryAsync(s => s.StructureId, ct);
            var now      = DateTimeOffset.UtcNow;
            var written  = 0;

            foreach (var s in all)
            {
                var isResolved = s.Status == (int)StructureStatus.Resolved;

                if (!existing.TryGetValue(s.StructureId, out var row))
                {
                    // New to us. Seed it whatever its state — an unresolved row still carries the
                    // one fact that matters, that this structure exists and has this location id.
                    row = new Structure { StructureId = s.StructureId };
                    db.Structures.Add(row);
                }
                else if (!isResolved || Unchanged(row, s))
                {
                    // Either ESI has nothing to say, or nothing it owns has moved. Leaving the row
                    // alone keeps UpdatedBy/UpdatedAt honest — a sync that rewrote every row each
                    // cycle would erase the record of who last actually changed something.
                    continue;
                }

                row.Name               = s.Name;
                row.SolarSystemId      = s.SolarSystemId;
                row.TypeId             = s.TypeId;
                row.OwnerId            = s.OwnerId;
                row.AllianceId         = s.AllianceId;
                row.X                  = s.X;
                row.Y                  = s.Y;
                row.Z                  = s.Z;
                row.NearestCelestialId = s.NearestCelestialId;
                row.NearestCelestial   = s.NearestCelestial;
                row.Status             = s.Status;
                row.UpdatedBy          = StructureSource.Esi;
                row.UpdatedAt          = now;

                // Notes, service modules and rigs are untouched on purpose — ESI has nothing to
                // say about them, so there is nothing here that could legitimately overwrite them.
                written++;
            }

            if (written > 0) await db.SaveChangesAsync(ct);

            LastSynced = written;
            return written;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(StructureSyncService), nameof(SyncAsync), ex);
            return 0;
        }
    }

    /// <summary>True when every field ESI owns already matches, so the row needs no write.</summary>
    private static bool Unchanged(Structure row, StructureName s) =>
        row.Name               == s.Name &&
        row.SolarSystemId      == s.SolarSystemId &&
        row.TypeId             == s.TypeId &&
        row.OwnerId            == s.OwnerId &&
        row.AllianceId         == s.AllianceId &&
        row.X                  == s.X &&
        row.Y                  == s.Y &&
        row.Z                  == s.Z &&
        row.NearestCelestialId == s.NearestCelestialId &&
        row.NearestCelestial   == s.NearestCelestial &&
        row.Status             == s.Status;
}
