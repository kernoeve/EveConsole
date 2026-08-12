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
