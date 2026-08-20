using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Shared "stage a full killmail into the DB" step used by both
/// ZkillboardFirehoseService (R2Z2, already has full data) and ZkillboardBackfillService
/// (daily dumps, already has full data) — unlike ZkillboardPollingService, which only
/// gets id+hash from zKillboard's filtered API and relies on the existing
/// KillMailService.FetchMissingAsync to hydrate details via ESI.
///
/// Additive-only, by design: every call checks whether the killmail (or a given owner's
/// ref to it) already exists and skips it if so. Nothing here ever updates or deletes an
/// existing KillMailDetails/KillMailAttackers/KillMailItems/EsiKillMailRefs row —
/// killmails are treated as immutable once stored.
///
/// Does NOT call SaveChangesAsync — callers control batching (one item at a time for the
/// firehose/poller, larger batches with periodic ChangeTracker.Clear() for backfill).
/// </summary>
public class ZkillboardKillImportService
{
    /// <summary>Currently tracked (authorized-token) character/corp ids, shared by the
    /// firehose and backfill services so both apply the same ownership rules as
    /// ZkillboardPollingService and the existing ESI polling.</summary>
    public static async Task<(HashSet<long> CharacterIds, HashSet<long> CorpIds)> GetTrackedIdsAsync(
        AppDbContext db, CancellationToken ct = default)
    {
        var characterIds = await db.Characters.Where(c => c.RefreshToken != "")
            .Select(c => c.Id).ToListAsync(ct);
        var corpIds = await db.Corporations.Where(c => c.RefreshToken != "")
            .Select(c => (long)c.Id).ToListAsync(ct);
        return (characterIds.ToHashSet(), corpIds.ToHashSet());
    }

    /// <summary>
    /// Existing-row lookup, preloaded once and kept in memory instead of one
    /// AnyAsync round trip per killmail — the difference between a handful of queries
    /// and tens of thousands for a multi-day backfill. Mutated in place as rows are
    /// staged, so later items (and later days, if the caller reuses one instance across
    /// a whole backfill run) see earlier ones without re-querying the DB.
    ///
    /// Not used by the firehose/poller — those touch the DB once every ~100ms-15s at
    /// most, where a full-table preload would cost more than it saves as the tables grow
    /// over months of running. Backfill/gap-fill's per-day-or-per-run volume is the
    /// opposite case, where the preload easily pays for itself.
    /// </summary>
    public sealed class KnownIds
    {
        public HashSet<int> DetailIds { get; }
        public HashSet<(long OwnerId, string OwnerType, int KillMailId)> RefKeys { get; }
        public HashSet<int> FlagIds { get; }

        private KnownIds(HashSet<int> detailIds, HashSet<(long, string, int)> refKeys, HashSet<int> flagIds)
        {
            DetailIds = detailIds;
            RefKeys   = refKeys;
            FlagIds   = flagIds;
        }

        public static async Task<KnownIds> LoadAsync(AppDbContext db, CancellationToken ct = default)
        {
            var detailIds = await db.KillMailDetails.Select(d => d.KillMailId).ToListAsync(ct);
            var refKeys = await db.EsiKillMailRefs
                .Select(r => new { r.OwnerId, r.OwnerType, r.KillMailId })
                .ToListAsync(ct);
            var flagIds = await db.ZkbKillFlags.Select(f => f.KillMailId).ToListAsync(ct);

            return new KnownIds(
                detailIds.ToHashSet(),
                refKeys.Select(r => (r.OwnerId, r.OwnerType, r.KillMailId)).ToHashSet(),
                flagIds.ToHashSet());
        }
    }

    /// <summary>Records that zKillboard is known to have this kill — the marker
    /// ZkillboardPostService uses to leave it alone. Called for every kill that arrives
    /// from a zKillboard source, including ones whose detail row we already had (an
    /// ESI-sourced kill that zKillboard also has is exactly the case worth knowing about).
    ///
    /// With <paramref name="known"/> (backfill), an existing flag row is left untouched
    /// rather than loaded and inspected — the only such row that could still be missing
    /// its seen timestamp is one from a failed submission, which self-corrects on the
    /// next retry (zKillboard answers "duplicate" and the flag is completed then).</summary>
    public static async Task MarkSeenOnZkbAsync(
        AppDbContext db, int killMailId, CancellationToken ct = default, KnownIds? known = null)
    {
        if (known is not null)
        {
            if (known.FlagIds.Add(killMailId))
                db.ZkbKillFlags.Add(new ZkbKillFlag { KillMailId = killMailId, SeenOnZkbAt = DateTimeOffset.UtcNow });
            return;
        }

        var existing = await db.ZkbKillFlags.FirstOrDefaultAsync(f => f.KillMailId == killMailId, ct);
        if (existing is null)
            db.ZkbKillFlags.Add(new ZkbKillFlag { KillMailId = killMailId, SeenOnZkbAt = DateTimeOffset.UtcNow });
        else if (existing.SeenOnZkbAt is null)
            existing.SeenOnZkbAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Stages a killmail (and any matching tracked-owner refs) into
    /// <paramref name="db"/>. Returns true if the killmail's detail row was newly
    /// added, false if it already existed (owner refs may still have been added even
    /// when the detail row was already present, e.g. it arrived earlier via ESI's own
    /// final-blow pull without a ref for this particular corp).
    ///
    /// When <paramref name="known"/> is supplied, existence checks use it instead of
    /// per-call AnyAsync queries — see KnownIds remarks for when that trade-off is
    /// worth it.</summary>
    public async Task<bool> ImportAsync(
        AppDbContext db,
        EsiKillMailFull full,
        string hash,
        IReadOnlySet<long> trackedCharacterIds,
        IReadOnlySet<long> trackedCorpIds,
        CancellationToken ct = default,
        KnownIds? known = null)
    {
        var isNew = known is not null
            ? known.DetailIds.Add(full.KillMailId)
            : !await db.KillMailDetails.AnyAsync(d => d.KillMailId == full.KillMailId, ct);
        if (isNew)
        {
            var victim = full.Victim;
            db.KillMailDetails.Add(new KillMailDetail
            {
                KillMailId        = full.KillMailId,
                KillMailHash      = hash,
                KillMailTime      = full.KillMailTime,
                SolarSystemId     = full.SolarSystemId,
                MoonId            = full.MoonId,
                WarId             = full.WarId,
                VictimCharId      = victim?.CharacterId    ?? 0L,
                VictimCorpId      = victim?.CorporationId  ?? 0L,
                VictimAllianceId  = victim?.AllianceId,
                VictimFactionId   = victim?.FactionId,
                VictimShipTypeId  = victim?.ShipTypeId     ?? 0,
                VictimDamageTaken = victim?.DamageTaken    ?? 0,
                VictimPosX        = victim?.Position?.X,
                VictimPosY        = victim?.Position?.Y,
                VictimPosZ        = victim?.Position?.Z,
            });

            if (full.Attackers != null)
                foreach (var a in full.Attackers)
                    db.KillMailAttackers.Add(new KillMailAttacker
                    {
                        KillMailId     = full.KillMailId,
                        CharacterId    = a.CharacterId,
                        CorporationId  = a.CorporationId,
                        AllianceId     = a.AllianceId,
                        FactionId      = a.FactionId,
                        DamageDone     = a.DamageDone,
                        FinalBlow      = a.FinalBlow,
                        SecurityStatus = a.SecurityStatus,
                        ShipTypeId     = a.ShipTypeId,
                        WeaponTypeId   = a.WeaponTypeId,
                    });

            if (victim?.Items != null)
                foreach (var it in victim.Items)
                    db.KillMailItems.Add(new KillMailItem
                    {
                        KillMailId        = full.KillMailId,
                        Flag              = it.Flag,
                        ItemTypeId        = it.ItemTypeId,
                        QuantityDestroyed = it.QuantityDestroyed,
                        QuantityDropped   = it.QuantityDropped,
                        Singleton         = it.Singleton,
                    });
        }

        // Outside the isNew branch on purpose: a kill we already had from ESI that also
        // turns up here is precisely the one we need to know zKillboard has.
        await MarkSeenOnZkbAsync(db, full.KillMailId, ct, known);

        foreach (var (ownerId, ownerType) in MatchedOwners(full, trackedCharacterIds, trackedCorpIds))
        {
            var isNewRef = known is not null
                ? known.RefKeys.Add((ownerId, ownerType, full.KillMailId))
                : !await db.EsiKillMailRefs.AnyAsync(r =>
                    r.OwnerId == ownerId && r.OwnerType == ownerType && r.KillMailId == full.KillMailId, ct);
            if (isNewRef)
                db.EsiKillMailRefs.Add(new KillMailRef
                {
                    OwnerId      = ownerId,
                    OwnerType    = ownerType,
                    KillMailId   = full.KillMailId,
                    KillMailHash = hash,
                });
        }

        return isNew;
    }

    /// <summary>Which tracked characters/corps this killmail actually involves (victim
    /// or any attacker) — drives which EsiKillMailRefs rows get created so the existing
    /// per-corp Kills viewer picks these up, the same way an ESI-sourced kill would.</summary>
    private static HashSet<(long OwnerId, string OwnerType)> MatchedOwners(
        EsiKillMailFull full, IReadOnlySet<long> trackedCharacterIds, IReadOnlySet<long> trackedCorpIds)
    {
        var owners = new HashSet<(long, string)>();

        void Consider(long? charId, long? corpId)
        {
            if (charId is long c && trackedCharacterIds.Contains(c)) owners.Add((c, "character"));
            if (corpId  is long p && trackedCorpIds.Contains(p))     owners.Add((p, "corporation"));
        }

        Consider(full.Victim?.CharacterId, full.Victim?.CorporationId);
        if (full.Attackers != null)
            foreach (var a in full.Attackers)
                Consider(a.CharacterId, a.CorporationId);

        return owners;
    }
}
