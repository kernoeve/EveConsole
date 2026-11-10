using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services.Worklist;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// How much of an item a Buy listing still wants: what the production plan needs of it, less what
/// is held, what running jobs will deliver and what sellers have already offered on the store.
/// </summary>
/// <param name="Wanted">What the plan needs of it in all: shelf levels, customer orders and what
/// the builds above it will eat.</param>
/// <param name="OnHand">Held in the plan's scope.</param>
/// <param name="InBuild">Coming out of running jobs.</param>
/// <param name="OnOrder">On this store's open offers, not yet delivered.</param>
public sealed record ItemNeed(long Wanted, long OnHand, long InBuild, long OnOrder)
{
    public long Needed => Math.Max(0, Wanted - OnHand - InBuild - OnOrder);

    /// <summary>Nothing wanted at all: the plan doesn't use this item.</summary>
    public static readonly ItemNeed None = new(0, 0, 0, 0);
}

/// <summary>
/// The need behind a Buy listing that tracks it (<see cref="StorePosting.TrackNeed"/>), for the
/// web site's catalogue, the mail price list, and the check when an offer is booked.
///
/// <para>The plan is the worklist's own: the same demand the job list builds from
/// (<see cref="IndustryDemandService.GatherAsync"/>, set up as <see cref="IndustryJobGenerator"/>
/// does), so the store asks for exactly what the Worklist says is missing. It is worked out
/// here rather than read from the Worklist screen because the store runs without one, on the
/// machine holding the worker lease.</para>
///
/// <para>⚠️ The plan is gathered at most once every <see cref="PlanAge"/>: it is the whole
/// demand walk, and the web sync and the mail pass both ask. The open offers are counted fresh
/// on every call, so an offer booked a moment ago is never asked for twice.</para>
/// </summary>
public sealed class StoreNeedService(
    IDbContextFactory<AppDbContext> dbFactory,
    IndustryAssignmentService assignment,
    IndustryDemandService demands,
    ProductionCalculatorService production,
    WorklistSettings settings)
{
    public static readonly TimeSpan PlanAge = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<int, DemandTotal>? _plan;
    private DateTime _planAt;

    /// <summary>The need for each of <paramref name="typeIds"/> on one store. Every id asked for is in the answer.</summary>
    public async Task<Dictionary<int, ItemNeed>> NeedsAsync(int storeId, IReadOnlyCollection<int> typeIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, ItemNeed>();
        if (typeIds.Count == 0) return result;

        var plan = await PlanAsync(ct).ConfigureAwait(false);
        var offered = await OfferedAsync(typeIds, ct).ConfigureAwait(false);
        foreach (var typeId in typeIds)
            result[typeId] = Of(plan.GetValueOrDefault(typeId), offered.GetValueOrDefault(typeId));
        return result;
    }

    /// <summary>
    /// For the Sale Postings screen: the need for a posting's items, with the open offers of every
    /// store that buys into it "only what's needed" — usually one. Null when no store does, and
    /// the posting has no need to show.
    /// </summary>
    public async Task<Dictionary<int, ItemNeed>?> ForPostingAsync(int postingId, IReadOnlyCollection<int> typeIds, CancellationToken ct = default)
    {
        List<int> stores;
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            stores = await db.StorePostings.AsNoTracking()
                .Where(p => p.PostingId == postingId && p.Side == StorePosting.Buy && p.TrackNeed)
                .Select(p => p.StoreId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        if (stores.Count == 0) return null;

        var plan = await PlanAsync(ct).ConfigureAwait(false);
        var offered = await OfferedAsync(typeIds, ct).ConfigureAwait(false);
        return typeIds.ToDictionary(t => t, t => Of(plan.GetValueOrDefault(t), offered.GetValueOrDefault(t)));
    }

    /// <summary>One item's need from its plan total and the units on open offers.</summary>
    /// <remarks>⚠️ The plan's "have" counts the open offers as on their way (the Worklist must not
    /// build what a seller brings). They are taken back out of On Hand here, because they are On
    /// Order, counted fresh — left in, they would be taken off twice.</remarks>
    public static ItemNeed Of(DemandTotal? plan, long onOrder) => plan is null
        ? ItemNeed.None with { OnOrder = onOrder }
        : new ItemNeed(plan.Wanted, Math.Max(0, plan.Have - plan.InBuild - plan.Incoming), plan.InBuild, onOrder);

    /// <summary>
    /// Units on open offers, by type: offers not completed or cancelled, less what has already
    /// been delivered (which is then held, and counted there). Every store's, not only the one
    /// asking: the Worklist counts them all as on their way, so the need has to as well, or an
    /// offer on another store would be neither held nor on order and asked for again.
    /// </summary>
    private async Task<Dictionary<int, long>> OfferedAsync(IReadOnlyCollection<int> typeIds, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = typeIds.ToList();
        var open = await db.TrackedOrders.AsNoTracking()
            .Where(o => o.Side == StorePosting.Buy && o.Status == "pending" && ids.Contains(o.TypeId))
            .ToListAsync(ct).ConfigureAwait(false);
        return Offered(open);
    }

    /// <summary>The same count over orders already in hand: the store's pending Buy orders, less what each has delivered.</summary>
    public static Dictionary<int, long> Offered(IEnumerable<TrackedOrder> orders, int storeId) =>
        Offered(orders.Where(o => o.StoreId == storeId));

    /// <summary>The same over every store: what the Worklist counts as on its way (<see cref="ScopeStock.Incoming"/>).</summary>
    public static Dictionary<int, long> Offered(IEnumerable<TrackedOrder> orders) =>
        orders.Where(o => o.Side == StorePosting.Buy && o.Status == "pending")
            .GroupBy(o => o.TypeId)
            .ToDictionary(g => g.Key, g => g.Sum(o => (long)Math.Max(0, o.Units - o.UnitsDelivered)));

    private async Task<Dictionary<int, DemandTotal>> PlanAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_plan is not null && DateTime.UtcNow - _planAt < PlanAge) return _plan;
            _plan = await GatherAsync(ct).ConfigureAwait(false);
            _planAt = DateTime.UtcNow;
            return _plan;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The job generator's demand, totalled per item. Empty with no industry park set up.</summary>
    private async Task<Dictionary<int, DemandTotal>> GatherAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var totals = new Dictionary<int, DemandTotal>();

        var parkId = await WorklistSettings.ResolveParkIdAsync(db, settings.IndustryParkId, ct).ConfigureAwait(false);
        if (parkId <= 0) return totals;

        var rules = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled && r.Action == "Build").ToListAsync(ct).ConfigureAwait(false);
        var ctx = await production.LoadContextAsync(parkId, ct).ConfigureAwait(false);
        var wrapped = await AssetExclusions.UnusableItemIdsAsync(db, ct).ConfigureAwait(false);
        var corps = await assignment.UsableCorporationsAsync(settings.IncludeNonPersonalCorps, ct).ConfigureAwait(false);

        // The scope as the job generator draws it: the configured one, the extra stations, and
        // the park's own facilities.
        var scope = await InvLevelService.ResolveScopeFilterAsync(db, settings.IndustryScope, settings.IndustryScopeId, ct).ConfigureAwait(false);
        if (scope is not null)
        {
            scope.UnionWith(await db.WorklistIndyScopeStations.AsNoTracking().Select(s => s.LocationId).ToListAsync(ct).ConfigureAwait(false));
            scope.UnionWith(await db.IndyStructures.AsNoTracking()
                .Where(s => s.ParkId == parkId && s.RealStructureId != null)
                .Select(s => s.RealStructureId!.Value).ToListAsync(ct).ConfigureAwait(false));
        }

        var inScope = await ScopeStock.LoadAsync(db, scope, wrapped, corps, ct).ConfigureAwait(false);
        var groupIds = rules.Select(r => r.GroupId).ToList();
        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct).ConfigureAwait(false);

        await demands.GatherAsync(db, ctx, rules, groups, scope, wrapped, corps, inScope, ct, totals: totals).ConfigureAwait(false);
        return totals;
    }
}
