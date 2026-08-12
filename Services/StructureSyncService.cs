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
    /// <para>Only rows ESI has actually resolved are copied: a Pending or NoAccess row carries no
    /// information the user does not already have, and copying its blanks over a description
    /// someone typed by hand would be the one thing this must never do.</para>
    /// </summary>
    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var resolved = await db.EsiStructureNames.AsNoTracking()
                .Where(s => s.Status == (int)StructureStatus.Resolved)
                .ToListAsync(ct);

            if (resolved.Count == 0) { LastSynced = 0; return 0; }

            var existing = await db.Structures.ToDictionaryAsync(s => s.StructureId, ct);
            var now      = DateTimeOffset.UtcNow;
            var written  = 0;

            foreach (var s in resolved)
            {
                if (!existing.TryGetValue(s.StructureId, out var row))
                {
                    row = new Structure { StructureId = s.StructureId };
                    db.Structures.Add(row);
                }
                else if (Unchanged(row, s))
                {
                    // Nothing ESI owns has moved. Leaving the row alone keeps UpdatedBy/UpdatedAt
                    // honest — a sync that rewrote every row each cycle would erase the record of
                    // who last actually changed something.
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
