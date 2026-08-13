using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Keeps an Indy Parks structure and the real structure it is linked to describing the same
/// fitting.
///
/// <para>Direction is decided per structure by whether we can see the real one's fitting in our
/// own assets:</para>
/// <list type="bullet">
/// <item>Fitted modules visible in assets — the game is the authority, so the real structure feeds
/// the park and the park's rigs and services are read-only.</item>
/// <item>Nothing visible — the park is the only description anyone has, so it feeds the structure
/// table and either side may be edited, each pushing to the other.</item>
/// </list>
///
/// <para>⚠️ Applies only to park structures with a RealStructureId. An unlinked park entry
/// describes a structure that need not exist, and has nothing to agree with.</para>
///
/// <para>⚠️ Compares rigs as a SET, never slot by slot. A rig's effect does not depend on which
/// rig slot holds it, and the two sides genuinely disagree about slot order: measured across the
/// twelve linked structures here, every asset-fed one held exactly the same rigs as its park entry
/// but in a different order — 24 of 31 slots differed while not one rig did. Matching on slot index
/// would have reported every structure as conflicting and then "fixed" them by shuffling rigs
/// between slots on every sync, for ever.</para>
/// </summary>
public class IndyStructureLinkService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
{
    /// <summary>Park structures carry three rig slots, matching the Upwell hulls.</summary>
    private const int ParkRigSlots = 3;

    private const string BandRig     = "Rig";
    private const string BandService = "Service";

    /// <summary>A park structure and the real one it claims to describe.</summary>
    private sealed record Link(int ParkId, string ParkName, long RealId);

    /// <summary>Rows written on the last run, for the caller's status line.</summary>
    public int LastChanged { get; private set; }

    /// <summary>Brings every linked pair into agreement. Safe to run repeatedly — a pair that
    /// already agrees is not written.</summary>
    public async Task<int> SyncAllAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var links = await LinksAsync(db, ct);
            var changed = 0;
            foreach (var link in links)
                changed += await SyncOneAsync(db, link, ct);

            if (changed > 0) await db.SaveChangesAsync(ct);
            LastChanged = changed;
            return changed;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(IndyStructureLinkService), nameof(SyncAllAsync), ex);
            return 0;
        }
    }

    /// <summary>Pushes one park structure's fitting to its linked real structure. No-op when the
    /// real one is asset-fed, since the game outranks a typed answer.</summary>
    public Task<int> PushFromParkAsync(int parkStructureId, CancellationToken ct = default) =>
        SyncWhereAsync(l => l.ParkId == parkStructureId, ct);

    /// <summary>Pushes a real structure's fitting to any park entry linked to it.</summary>
    public Task<int> PushFromRealAsync(long realStructureId, CancellationToken ct = default) =>
        SyncWhereAsync(l => l.RealId == realStructureId, ct);

    private async Task<int> SyncWhereAsync(Func<Link, bool> match, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var changed = 0;
            foreach (var link in (await LinksAsync(db, ct)).Where(match))
                changed += await SyncOneAsync(db, link, ct);

            if (changed > 0) await db.SaveChangesAsync(ct);
            return changed;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(IndyStructureLinkService), nameof(SyncWhereAsync), ex);
            return 0;
        }
    }

    private static async Task<List<Link>> LinksAsync(AppDbContext db, CancellationToken ct) =>
        await db.IndyStructures.AsNoTracking()
            .Where(s => s.RealStructureId != null)
            .Select(s => new Link(s.Id, s.DisplayName, s.RealStructureId!.Value))
            .ToListAsync(ct);

    /// <summary>
    /// Whether the game is telling us what is fitted to this structure.
    ///
    /// <para>⚠️ ANY slot-flagged asset counts, not "any rig". A structure whose fitting we can read
    /// and which simply has no rigs is telling us something — that it has none — and the park should
    /// follow. Testing per band would instead read that as "nothing known" and let the park
    /// overwrite the truth with a guess. Same test the Structure Browser uses to decide whether a
    /// fitting is editable, so the two cannot disagree about who is in charge.</para>
    /// </summary>
    private static Task<bool> IsAssetFedAsync(AppDbContext db, long realId, CancellationToken ct) =>
        db.EsiAssets.AsNoTracking()
            .AnyAsync(a => a.LocationId == realId && a.LocationFlag.Contains("Slot"), ct);

    private static async Task<int> SyncOneAsync(AppDbContext db, Link link, CancellationToken ct)
    {
        var assetFed = await IsAssetFedAsync(db, link.RealId, ct);

        return assetFed
            ? await RealToParkAsync(db, link, ct)
            : await ParkToRealAsync(db, link, ct);
    }

    // ── Real → park ──────────────────────────────────────────────────────────

    private static async Task<int> RealToParkAsync(AppDbContext db, Link link, CancellationToken ct)
    {
        var fitted = await db.EsiAssets.AsNoTracking()
            .Where(a => a.LocationId == link.RealId && a.LocationFlag.Contains("Slot"))
            .Select(a => new { a.LocationFlag, a.TypeId })
            .ToListAsync(ct);

        var rigs = fitted.Where(f => f.LocationFlag.StartsWith("RigSlot"))
                         .Select(f => f.TypeId).ToList();
        var services = fitted.Where(f => f.LocationFlag.StartsWith("ServiceSlot"))
                             .Select(f => f.TypeId).ToList();

        return await WriteParkRigsAsync(db, link.ParkId, rigs, ct)
             + await WriteParkServicesAsync(db, link.ParkId, services, ct);
    }

    private static async Task<int> WriteParkRigsAsync(
        AppDbContext db, int parkId, List<int> wanted, CancellationToken ct)
    {
        var rows = await db.IndyStructureRigs
            .Where(r => r.StructureId == parkId)
            .OrderBy(r => r.SlotIndex)
            .ToListAsync(ct);

        if (SameSet(rows.Select(r => r.RigTypeId), wanted)) return 0;

        // Slots are filled in order and the remainder blanked. Which slot holds which rig has no
        // effect in game, so there is nothing to preserve by trying to match the old arrangement.
        var ordered = wanted.OrderBy(t => t).Take(ParkRigSlots).ToList();

        for (var slot = 0; slot < ParkRigSlots; slot++)
        {
            var typeId = slot < ordered.Count ? ordered[slot] : 0;

            var row = rows.FirstOrDefault(r => r.SlotIndex == slot);
            if (row is null)
            {
                if (typeId == 0) continue;
                db.IndyStructureRigs.Add(new IndyStructureRig
                {
                    StructureId = parkId, SlotIndex = slot, RigTypeId = typeId,
                });
            }
            else row.RigTypeId = typeId;
        }

        return 1;
    }

    private static async Task<int> WriteParkServicesAsync(
        AppDbContext db, int parkId, List<int> wanted, CancellationToken ct)
    {
        var rows = await db.IndyStructureServices
            .Where(s => s.StructureId == parkId).ToListAsync(ct);

        if (SameSet(rows.Select(s => s.TypeId), wanted)) return 0;

        // A service module has no slot index that means anything to us, so the list is replaced
        // rather than reconciled row by row.
        db.IndyStructureServices.RemoveRange(rows);
        foreach (var typeId in wanted.Where(t => t > 0).Distinct().OrderBy(t => t))
            db.IndyStructureServices.Add(new IndyStructureService
            {
                StructureId = parkId, TypeId = typeId,
            });

        return 1;
    }

    // ── Park → real ──────────────────────────────────────────────────────────

    private static async Task<int> ParkToRealAsync(AppDbContext db, Link link, CancellationToken ct)
    {
        var rigs = await db.IndyStructureRigs.AsNoTracking()
            .Where(r => r.StructureId == link.ParkId && r.RigTypeId > 0)
            .Select(r => r.RigTypeId).ToListAsync(ct);

        var services = await db.IndyStructureServices.AsNoTracking()
            .Where(s => s.StructureId == link.ParkId && s.TypeId > 0)
            .Select(s => s.TypeId).ToListAsync(ct);

        // ⚠️ Only creates the structure row if one is already there. This service describes a
        // structure the user has linked to; inventing a Structures row for an id we have never
        // resolved would put a record in the browser that nothing else knows anything about.
        if (!await db.Structures.AnyAsync(s => s.StructureId == link.RealId, ct)) return 0;

        return await WriteRealBandAsync(db, link.RealId, BandRig, rigs, ct)
             + await WriteRealBandAsync(db, link.RealId, BandService, services, ct);
    }

    private static async Task<int> WriteRealBandAsync(
        AppDbContext db, long realId, string band, List<int> wanted, CancellationToken ct)
    {
        var rows = await db.StructureFittings
            .Where(f => f.StructureId == realId && f.Band == band).ToListAsync(ct);

        if (SameSet(rows.Select(f => f.TypeId), wanted)) return 0;

        db.StructureFittings.RemoveRange(rows);

        var slot = 0;
        foreach (var typeId in wanted.Where(t => t > 0).Distinct().OrderBy(t => t))
            db.StructureFittings.Add(new StructureFitting
            {
                StructureId = realId, Band = band, SlotIndex = slot++, TypeId = typeId,
            });

        return 1;
    }

    /// <summary>Set comparison, ignoring order and blanks — the only sense in which two fittings
    /// are the same. Duplicates collapse: two identical rigs cannot be fitted anyway.</summary>
    private static bool SameSet(IEnumerable<int> a, IEnumerable<int> b) =>
        a.Where(x => x > 0).ToHashSet().SetEquals(b.Where(x => x > 0));
}
