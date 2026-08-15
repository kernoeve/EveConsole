using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// One blueprint the worklist could actually install a job from.
///
/// <para><b>Runs</b> is the licensed runs left on a copy; originals are unlimited and carry
/// <see cref="int.MaxValue"/> so callers can cap without branching on the kind first.</para>
/// </summary>
public sealed record BlueprintStock
{
    public required long   ItemId       { get; init; }
    public required int    TypeId       { get; init; }
    public required bool   IsOriginal   { get; init; }
    public required int    Runs         { get; init; }
    public required int    Me           { get; init; }
    public required int    Te           { get; init; }
    public required long   LocationId   { get; init; }
    public required string OwnerType    { get; init; }
    public required long   OwnerId      { get; init; }

    /// <summary>
    /// Installed in a live job, so unusable until it ends — but still owned. Kept rather than
    /// dropped because the two are different answers: a busy print comes back, and telling
    /// someone to buy one they already have would be wrong.
    /// </summary>
    public required bool   LockedInJob  { get; init; }

    public string Describe() => IsOriginal
        ? $"BPO ME{Me}/TE{Te}"
        : $"BPC ME{Me}/TE{Te}, {Runs:N0} run(s) left";
}

/// <summary>
/// Which blueprints are on hand, where, and which one a job should use.
///
/// <para>Job planning without this is planning against a blueprint that may not exist. A
/// shortfall is real, the materials may be sitting in the hangar, and the job still cannot be
/// installed because the print is in another structure or locked in a running job — which is
/// precisely the wasted trip the worklist is meant to prevent.</para>
///
/// <para><b>One blueprint, one job.</b> A print is locked for the duration of the job it is
/// installed in, original or copy. So the number of jobs that can run at once for an item is
/// bounded by how many prints are on hand, and splitting a long build into five shorter ones
/// needs five prints, not one. Copies additionally bound their own job: a 40-run copy cannot
/// carry a 100-run job.</para>
/// </summary>
public class IndustryBlueprintService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>
    /// Blueprints in asset safety are recoverable, not usable — a job cannot be installed from
    /// one. Everything else, including copies sitting in a container inside a hangar, can be.
    ///
    /// <para>The flag alone is not enough: a print inside a container inside a wrap carries an
    /// ordinary hangar flag, so the container chain has to be checked too.</para>
    /// </summary>
    private const string AssetSafetyFlag = "AssetSafety";

    /// <summary>
    /// Every usable print of the given blueprint types, keyed by type id.
    ///
    /// <para>Prints held in a container report the container's item id as their location, so the
    /// chain is walked to the station or structure the container is ultimately in — the same root
    /// the assets table stores, so blueprints and materials are compared at the same place.
    /// Nearly every print is in a container, so without this the search finds almost none.</para>
    ///
    /// <para>Prints installed in a live job are marked rather than removed. Whether one is
    /// available and whether one is owned are different questions with different answers, and
    /// only the second decides whether to go buy one.</para>
    /// </summary>
    public async Task<Dictionary<int, List<BlueprintStock>>> LoadAsync(
        IReadOnlyCollection<int> blueprintTypeIds, CancellationToken ct = default)
    {
        if (blueprintTypeIds.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var wrapped = await AssetExclusions.UnusableItemIdsAsync(db, ct);

        var rows = (await db.EsiBlueprints.AsNoTracking()
                .Where(b => blueprintTypeIds.Contains(b.TypeId) && b.LocationFlag != AssetSafetyFlag)
                .Select(b => new
                {
                    b.ItemId, b.TypeId, b.Runs, b.MaterialEfficiency, b.TimeEfficiency,
                    b.LocationId, b.OwnerType, b.OwnerId,
                })
                .ToListAsync(ct))
            .Where(b => !wrapped.Contains(b.ItemId) && !wrapped.Contains(b.LocationId))
            .ToList();
        if (rows.Count == 0) return [];

        // Container item id → the station or structure it is ultimately in. Only the locations
        // actually referenced are looked up, which is a handful of rows rather than the assets
        // table; a blueprint whose location matches no asset is already sitting in a station.
        var locIds = rows.Select(r => r.LocationId).Distinct().ToList();
        var containerRoots = await db.EsiAssets.AsNoTracking()
            .Where(a => locIds.Contains(a.ItemId))
            .Select(a => new { a.ItemId, a.RootLocationId })
            .ToDictionaryAsync(a => a.ItemId, a => a.RootLocationId, ct);

        // Prints locked in a job right now. The same live statuses used for slot counting:
        // "ready" holds its print as firmly as "active" does, since the job has not been
        // collected and the blueprint is still in it.
        var locked = (await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => j.Status == "active" || j.Status == "paused" || j.Status == "ready")
                .Select(j => j.BlueprintId)
                .ToListAsync(ct))
            .ToHashSet();

        return rows
            .Select(r => new BlueprintStock
            {
                ItemId      = r.ItemId,
                TypeId      = r.TypeId,
                IsOriginal  = r.Runs < 0,
                Runs        = r.Runs < 0 ? int.MaxValue : r.Runs,
                Me          = r.MaterialEfficiency,
                Te          = r.TimeEfficiency,
                LocationId  = containerRoots.GetValueOrDefault(r.LocationId, r.LocationId),
                OwnerType   = r.OwnerType,
                OwnerId     = r.OwnerId,
                LockedInJob = locked.Contains(r.ItemId),
            })
            .Where(b => b.Runs > 0)   // a spent copy is a row that has not caught up yet
            .GroupBy(b => b.TypeId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Whether a print of this type exists at all within reach, wherever it is and whatever it is
    /// doing — the question that decides between "move one" and "go buy one".
    ///
    /// <para>A print busy in a job counts, because it will come back. Originals always survive
    /// their job; a copy survives if it has runs left on it. Scope is applied because a print in
    /// another region is not one that will be installed here this week.</para>
    /// </summary>
    /// <summary>
    /// Every print in reach, whatever its type, for building an efficiency map across a whole
    /// production tree.
    /// </summary>
    public async Task<List<BlueprintStock>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.EsiBlueprints.AsNoTracking().Select(b => b.TypeId).Distinct().ToListAsync(ct);
        return (await LoadAsync(ids, ct)).SelectMany(kv => kv.Value).ToList();
    }

    /// <summary>
    /// The efficiency each product would actually be built at, from the best print on hand.
    ///
    /// <para>Keyed by product rather than blueprint, because that is what a plan asks about. The
    /// best print is the one the job generator would reach for — an original before a copy, then
    /// the highest ME — so the materials a purchase is sized against are the materials the job
    /// will really consume.</para>
    ///
    /// <para>Products with no print owned are absent, and the caller falls back to the shared
    /// default. Guessing an efficiency for a blueprint that has to be bought would be inventing
    /// a number about an object that does not exist yet.</para>
    /// </summary>
    public static Dictionary<int, int> BestMeByProduct(
        IReadOnlyList<BlueprintStock> all,
        IReadOnlyDictionary<int, SdeBlueprintProduct> blueprintByProduct,
        HashSet<long>? scope,
        IReadOnlyList<WorklistIndyCharReach> reaches)
    {
        var usable = all
            .Where(b => reaches.Any(r => r.CanUse(b)) && (scope is null || scope.Contains(b.LocationId)))
            .GroupBy(b => b.TypeId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(b => b.IsOriginal).ThenByDescending(b => b.Me)
                      .ThenByDescending(b => b.Te).ThenBy(b => b.ItemId).First());

        var byProduct = new Dictionary<int, int>();
        foreach (var (productTypeId, bp) in blueprintByProduct)
            if (usable.TryGetValue(bp.TypeId, out var best))
                byProduct[productTypeId] = best.Me;

        return byProduct;
    }

    public static bool OwnedWithin(
        IReadOnlyList<BlueprintStock> all, HashSet<long>? scope,
        IReadOnlyList<WorklistIndyCharReach> reaches) =>
        all.Any(b => reaches.Any(r => r.CanUse(b))
                     && (scope is null || scope.Contains(b.LocationId)));

    /// <summary>
    /// The prints of one type that a given character can install a job from at a given site, in
    /// the order they should be used.
    ///
    /// <para>Originals first, as asked: a copy is consumed by use and an original is not, so
    /// spending a copy while an original sits idle throws away a limited resource for nothing.
    /// Within each kind the best material efficiency comes first, because ME decides what the
    /// job costs and the difference compounds over a long run.</para>
    ///
    /// <para>Ties break on item id so the order is total. Assignments are regenerated on every
    /// refresh, and a list that reshuffled when nothing changed would be unreadable.</para>
    ///
    /// <para>Takes every candidate character's reach rather than one character's, because the
    /// order has to be fixed before jobs are handed out — a print usable by any of them is a
    /// print the work can be planned around, and which of them installs it is settled later,
    /// per job, by <see cref="WorklistIndyCharReach.CanUse"/>.</para>
    /// </summary>
    public static List<BlueprintStock> UsableAt(
        IReadOnlyList<BlueprintStock> all, long siteId,
        IReadOnlyList<WorklistIndyCharReach> reaches) =>
        all
            .Where(b => !b.LockedInJob && b.LocationId == siteId && reaches.Any(r => r.CanUse(b)))
            .OrderByDescending(b => b.IsOriginal)
            .ThenByDescending(b => b.Me)
            .ThenByDescending(b => b.Te)
            .ThenBy(b => b.ItemId)
            .ToList();
}

/// <summary>
/// Whose hangars a character's jobs may draw from. The same rule that governs materials governs
/// prints — a blueprint in a corp hangar is only usable by an alt whose corp assets count.
/// </summary>
public readonly record struct WorklistIndyCharReach(long CharacterId, bool Corp, bool Personal)
{
    public bool CanUse(BlueprintStock b) => b.OwnerType == "corporation"
        ? Corp
        : Personal && b.OwnerId == CharacterId;
}
