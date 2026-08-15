using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>Why a station wants something, which is also how urgent moving it is.</summary>
public enum HaulReason { Unblocking, Restock, Refine, Surplus }

/// <summary>
/// Moving material to where it is needed.
///
/// <para>The other generators end at "this job cannot start, its inputs are elsewhere". That is a
/// diagnosis, not work. This turns it into the trip that fixes it.</para>
///
/// <para><b>One task per pair of stations.</b> A hauler flying from Jita to ZD1-Z2 carries
/// everything ZD1-Z2 needs from Jita, so the task lists items rather than being one task each —
/// twenty rows for one round trip would be twenty times the reading for the same flying. Volume
/// is deliberately ignored: how many trips it takes is the hauler's problem, and splitting by
/// capacity would guess at ships and rigs the tool knows nothing about.</para>
///
/// <para><b>A task is worth its best cargo.</b> If any part of a run unblocks a job, the whole
/// run carries that urgency even when the rest is routine restocking. The trip happens once.</para>
///
/// <para>Sources are ranked by distance — same system first, then gates — and a station is never
/// drawn below what it needs itself, so filling one structure cannot empty another.</para>
/// </summary>
public class LogisticsGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    IndustryBlueprintService        blueprints,
    IndustryAssignmentService       assignment,
    InvLevelService                 invLevels,
    JumpDistanceService             jumps,
    ProductionCalculatorService     production,
    WorklistSettings                settings,
    AppErrorLogger                  errorLogger) : IWorklistGenerator
{
    public string Id          => "logistics";
    public string DisplayName => "Logistics";

    private sealed record Want(long Qty, HaulReason Reason);

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var parkId = settings.IndustryParkId;
        if (parkId <= 0) return [];

        try
        {
            return await BuildAsync(db, parkId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(LogisticsGenerator), $"Park {parkId}", ex);
            return [];
        }
    }

    private async Task<List<WorklistItem>> BuildAsync(
        AppDbContext db, int parkId, CancellationToken ct)
    {
        var ctx = await production.LoadContextAsync(parkId, ct);

        var candidates = await assignment.LoadCandidatesAsync(ct);
        if (candidates.Count == 0) return [];
        var reaches = candidates
            .Select(c => new WorklistIndyCharReach(
                c.Config.CharacterId, c.Config.IncludeCorpAssets, c.Config.IncludePersonalAssets))
            .ToList();

        var scope   = await ScopeAsync(db, ct);
        var wrapped = await AssetExclusions.UnusableItemIdsAsync(db, ct);

        // Everything reachable, by where it is. The same rule the shortfall checks use, so the
        // two agree about what exists.
        var stock = (await (scope is null
                    ? db.EsiAssets.AsNoTracking()
                    : db.EsiAssets.AsNoTracking().Where(a => scope.Contains(a.RootLocationId)))
                .Select(a => new { a.ItemId, a.RootLocationId, a.TypeId, a.OwnerType, a.OwnerId, a.Quantity })
                .ToListAsync(ct))
            .Where(a => !wrapped.Contains(a.ItemId))
            .Where(a => a.OwnerType == "corporation"
                          ? candidates.Any(c => c.Config.IncludeCorpAssets)
                          : candidates.Any(c => c.Config.IncludePersonalAssets
                                                && c.Config.CharacterId == a.OwnerId))
            .GroupBy(a => (Station: a.RootLocationId, a.TypeId))
            .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity));

        var want = new Dictionary<(long Station, int TypeId), Want>();

        void Need(long station, int typeId, long qty, HaulReason why)
        {
            if (station <= 0 || qty <= 0) return;
            var key = (station, typeId);
            var had = want.GetValueOrDefault(key);
            want[key] = new Want(
                had is null ? qty : had.Qty + qty,
                had is null ? why : (HaulReason)Math.Min((int)had.Reason, (int)why));
        }

        var meMap = IndustryBlueprintService.BestMeByProduct(
            await blueprints.LoadAllAsync(ct), ctx.BlueprintByProduct, scope, reaches);

        await AddJobDemandAsync(db, ctx, meMap, Need, ct);
        await AddStationLevelDemandAsync(db, stock, Need, ct);

        var refineMoves = await RefiningMovesAsync(db, ctx, parkId, stock, ct);

        var systems = await SystemsAsync(db, ct);
        var places  = await PlaceNamesAsync(db, ct);

        var moves = new List<Move>(refineMoves);
        moves.AddRange(await AllocateAsync(want, stock, systems, ct));
        moves.AddRange(SurplusMoves(await SurplusHomesAsync(db, ct), want, stock));

        // Named from the moves themselves, not from the demand that produced most of them.
        // Surplus exists precisely where nothing is wanted, so its types are never in `want` —
        // and a run mixing restocking with surplus takes the restock label while listing the
        // surplus items as bare type ids.
        var names = await NamesAsync(db, moves.Select(m => m.TypeId).Distinct().ToList(), ct);

        return Tasks(moves, names, places);
    }

    /// <summary>One item moving between two stations.</summary>
    private sealed record Move(long From, long To, int TypeId, long Qty, HaulReason Reason);

    // ── Demand ────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the jobs planned at each park facility will consume there.
    ///
    /// <para>The whole shortfall's worth, not only the part that could start now. A job split
    /// five ways still wants all five jobs' material eventually, and hauling it in one trip beats
    /// five.</para>
    /// </summary>
    private async Task AddJobDemandAsync(
        AppDbContext db, ProductionContext ctx, Dictionary<int, int> meMap,
        Action<long, int, long, HaulReason> need, CancellationToken ct)
    {
        var rules = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled && r.Action == "Build")
            .ToListAsync(ct);
        if (rules.Count == 0) return;

        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => rules.Select(r => r.GroupId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        foreach (var rule in rules.OrderBy(r => r.Id))
        {
            if (!groups.TryGetValue(rule.GroupId, out var group)) continue;

            var groupItems = await db.InvLevelItems.AsNoTracking()
                .Where(i => i.GroupId == group.Id).ToListAsync(ct);
            if (groupItems.Count == 0) continue;

            var typeIds = groupItems.Select(i => i.TypeId).Distinct().ToList();
            var avail   = await invLevels.LoadAvailableAsync(group, typeIds, ct);

            foreach (var gi in groupItems.OrderBy(i => i.TypeId))
            {
                avail.TryGetValue(gi.TypeId, out var av);
                var shortfall = InvRuleShortfall.For(rule, group, gi, av);
                if (shortfall is null || shortfall.Shortfall <= 0) continue;
                if (!ctx.BlueprintByProduct.ContainsKey(gi.TypeId)) continue;

                var entry = new ProductionQueueEntry
                {
                    TypeId   = gi.TypeId,
                    Quantity = (int)Math.Clamp(shortfall.Shortfall, 1, int.MaxValue),
                    MeLevel  = meMap.TryGetValue(gi.TypeId, out var me) ? me : 10,
                };

                var root = production.Calculate([entry], ctx, meOverrides: meMap)
                                     .AllJobs.FirstOrDefault(j => j.OutputTypeId == gi.TypeId);
                if (root?.StationId is not { } site) continue;

                foreach (var m in root.Materials)
                    need(site, m.MaterialTypeId, m.TotalQty, HaulReason.Unblocking);
            }
        }
    }

    /// <summary>
    /// Station levels: keep this group's stock at this station.
    ///
    /// <para>The station is the scope, whatever scope the group itself carries — the row exists
    /// to say "here", so counting stock elsewhere against it would defeat the point.</para>
    /// </summary>
    private static async Task AddStationLevelDemandAsync(
        AppDbContext db, Dictionary<(long Station, int TypeId), long> stock,
        Action<long, int, long, HaulReason> need, CancellationToken ct)
    {
        var levels = await db.WorklistStationLevels.AsNoTracking()
            .Where(l => l.Enabled).ToListAsync(ct);
        if (levels.Count == 0) return;

        foreach (var level in levels)
        {
            var items = await db.InvLevelItems.AsNoTracking()
                .Where(i => i.GroupId == level.GroupId).ToListAsync(ct);

            foreach (var i in items)
            {
                var here = stock.GetValueOrDefault((level.LocationId, i.TypeId));
                var gap  = i.TargetQuantity - here;
                if (gap > 0) need(level.LocationId, i.TypeId, gap, HaulReason.Restock);
            }
        }
    }

    /// <summary>
    /// Ore, ice and gas sitting anywhere but the facility that processes it.
    ///
    /// <para>Modelled as moves rather than as a need, because the quantity is whatever exists —
    /// a refinery does not want "200,000 Veldspar", it wants all of it. Moon ore is separated
    /// from asteroid ore by group, since the rigs are separate and so is the park assignment.</para>
    /// </summary>
    private static async Task<List<Move>> RefiningMovesAsync(
        AppDbContext db, ProductionContext ctx, int parkId,
        Dictionary<(long Station, int TypeId), long> stock, CancellationToken ct)
    {
        var facilities = await db.IndyStructures.AsNoTracking()
            .Where(s => s.ParkId == parkId && s.RealStructureId != null)
            .ToListAsync(ct);
        var assignments = await db.IndyCategoryAssignments.AsNoTracking()
            .Where(a => a.ParkId == parkId && a.StructureId != null)
            .ToListAsync(ct);

        long? Facility(string key)
        {
            var a = assignments.FirstOrDefault(x => x.CategoryKey == key)
                 ?? assignments.FirstOrDefault(x => x.CategoryKey == "reprocessing");
            if (a is null) return null;
            return facilities.FirstOrDefault(f => f.Id == a.StructureId)?.RealStructureId;
        }

        var target = new Dictionary<string, long?>
        {
            ["refine_ore"]      = Facility("refine_ore"),
            ["refine_moon_ore"] = Facility("refine_moon_ore"),
            ["refine_ice"]      = Facility("refine_ice"),
            ["decompress_gas"]  = Facility("decompress_gas"),
        };
        if (target.Values.All(v => v is null)) return [];

        // Classify by SDE group so ice and moon ore separate from ordinary ore.
        var held = stock.Keys.Select(k => k.TypeId).Distinct().ToList();
        var kinds = await db.SdeTypes.AsNoTracking()
            .Where(t => held.Contains(t.TypeId))
            .Join(db.SdeGroups, t => t.GroupId, g => g.GroupId,
                  (t, g) => new { t.TypeId, g.CategoryId, g.GroupId })
            .ToListAsync(ct);

        var routeOf = kinds.ToDictionary(k => k.TypeId, k => Route(k.CategoryId, k.GroupId));

        var moves = new List<Move>();
        foreach (var ((station, typeId), qty) in stock)
        {
            if (qty <= 0) continue;
            if (!routeOf.TryGetValue(typeId, out var key) || key is null) continue;
            if (target.GetValueOrDefault(key) is not { } to) continue;
            if (station == to) continue;   // already where it is processed

            moves.Add(new Move(station, to, typeId, qty, HaulReason.Refine));
        }

        return moves;
    }

    // Gas lives in the Celestial category alongside cargo containers, wrecks and planetary
    // clouds, so the category is far too broad to route on — matching it sent every secure
    // container in Jita to the refinery. These are the two groups that are actually gas.
    private const int HarvestableCloud = 711;
    private const int CompressedGas    = 4168;

    private const int Ice              = 465;
    private const int AncientCompressedIce = 903;

    /// <summary>Moon asteroid groups: Ubiquitous, Common, Uncommon, Rare, Exceptional.</summary>
    private static readonly HashSet<int> MoonOre = [1884, 1920, 1921, 1922, 1923];

    private const int AsteroidCategory = 25;

    /// <summary>Which processing a type needs, or null when it is not raw material at all.</summary>
    private static string? Route(int categoryId, int groupId) => groupId switch
    {
        HarvestableCloud or CompressedGas          => "decompress_gas",
        Ice or AncientCompressedIce                => "refine_ice",
        _ when MoonOre.Contains(groupId)           => "refine_moon_ore",
        _ when categoryId == AsteroidCategory      => "refine_ore",
        _                                          => null,
    };

    // ── Matching ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills each station's wants from the nearest station that can spare the material.
    ///
    /// <para>A source is only offered what it holds beyond its own need, so satisfying one
    /// structure never starves another. Same system first, then gate distance, then station id so
    /// two equally good sources always resolve the same way.</para>
    /// </summary>
    private async Task<List<Move>> AllocateAsync(
        Dictionary<(long Station, int TypeId), Want> want,
        Dictionary<(long Station, int TypeId), long> stock,
        Dictionary<long, int> systems, CancellationToken ct)
    {
        var moves = new List<Move>();
        if (want.Count == 0) return moves;

        // Spare stock: what a station holds beyond whatever it wants itself.
        var spare = new Dictionary<(long Station, int TypeId), long>();
        foreach (var (key, qty) in stock)
        {
            var mine = want.GetValueOrDefault(key)?.Qty ?? 0;
            var free = qty - mine;
            if (free > 0) spare[key] = free;
        }

        var jumpCache = new Dictionary<int, Dictionary<int, int>>();

        // Destinations in a fixed order so the same inputs always produce the same plan.
        foreach (var ((dest, typeId), w) in want.OrderBy(k => k.Key.Station).ThenBy(k => k.Key.TypeId))
        {
            var have = stock.GetValueOrDefault((dest, typeId));
            var gap  = w.Qty - have;
            if (gap <= 0) continue;

            var destSystem = systems.GetValueOrDefault(dest);
            if (destSystem != 0 && !jumpCache.ContainsKey(destSystem))
                jumpCache[destSystem] = await jumps.JumpsFromAsync(destSystem, ct: ct);
            var distances = jumpCache.GetValueOrDefault(destSystem) ?? [];

            // Distance first, then indifference. Between two equally close sources, take from the
            // one that has no use for the item at all rather than from one holding it to a level
            // — the second is only spare until its own consumption catches up, and emptying it to
            // the line means the next refresh asks for it back.
            var sources = spare
                .Where(s => s.Key.TypeId == typeId && s.Key.Station != dest && s.Value > 0)
                .OrderBy(s => Distance(distances, systems, s.Key.Station))
                .ThenBy(s => want.ContainsKey((s.Key.Station, typeId)) ? 1 : 0)
                .ThenBy(s => s.Key.Station)
                .ToList();

            foreach (var s in sources)
            {
                if (gap <= 0) break;
                var take = Math.Min(gap, s.Value);
                moves.Add(new Move(s.Key.Station, dest, typeId, take, w.Reason));
                spare[s.Key] -= take;
                gap -= take;
            }
        }

        return moves;
    }

    /// <summary>Gate distance, with unreachable sources sorted last rather than dropped — a long
    /// haul is still an answer, and pretending the material is not there is not.</summary>
    private static int Distance(
        Dictionary<int, int> distances, Dictionary<long, int> systems, long station)
    {
        var sys = systems.GetValueOrDefault(station);
        if (sys == 0) return int.MaxValue - 1;
        return distances.TryGetValue(sys, out var d) ? d : int.MaxValue - 1;
    }

    /// <summary>
    /// Spare stock with nowhere to be, sent to the station that collects its group.
    ///
    /// <para>Only what no station wants. Capital parts belong at the capital shipyard once every
    /// waiting job is served, not instead of serving them.</para>
    /// </summary>
    private static List<Move> SurplusMoves(
        Dictionary<int, (long Station, string Group)> homes,
        Dictionary<(long Station, int TypeId), Want> want,
        Dictionary<(long Station, int TypeId), long> stock)
    {
        var moves = new List<Move>();
        if (homes.Count == 0) return moves;

        // Total demand for each type anywhere, so "nothing needs it" is a real check rather than
        // a per-station one.
        var demand = want.GroupBy(w => w.Key.TypeId)
                         .ToDictionary(g => g.Key, g => g.Sum(x => x.Value.Qty));

        foreach (var ((station, typeId), qty) in stock.OrderBy(s => s.Key.Station).ThenBy(s => s.Key.TypeId))
        {
            if (!homes.TryGetValue(typeId, out var home)) continue;
            if (station == home.Station) continue;
            if (demand.GetValueOrDefault(typeId) > 0) continue;
            if (qty <= 0) continue;

            moves.Add(new Move(station, home.Station, typeId, qty, HaulReason.Surplus));
        }

        return moves;
    }

    private static async Task<Dictionary<int, (long Station, string Group)>> SurplusHomesAsync(
        AppDbContext db, CancellationToken ct)
    {
        var levels = await db.WorklistStationLevels.AsNoTracking()
            .Where(l => l.Enabled && l.AcceptsSurplus).ToListAsync(ct);
        if (levels.Count == 0) return [];

        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => levels.Select(l => l.GroupId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name, ct);

        var homes = new Dictionary<int, (long, string)>();

        // Lowest row id wins if two stations claim the same group's surplus, so the answer is
        // stable rather than whichever row was read first.
        foreach (var level in levels.OrderBy(l => l.Id))
        {
            var items = await db.InvLevelItems.AsNoTracking()
                .Where(i => i.GroupId == level.GroupId).Select(i => i.TypeId).ToListAsync(ct);
            foreach (var typeId in items)
                homes.TryAdd(typeId, (level.LocationId, groups.GetValueOrDefault(level.GroupId, "")));
        }

        return homes;
    }

    // ── Output ────────────────────────────────────────────────────────────────

    private List<WorklistItem> Tasks(
        List<Move> moves, Dictionary<int, string> names, Dictionary<long, string> places)
    {
        var items = new List<WorklistItem>();

        foreach (var run in moves
                     .Where(m => m.From != m.To && m.Qty > 0)
                     .GroupBy(m => (m.From, m.To))
                     .OrderBy(g => g.Key.From).ThenBy(g => g.Key.To))
        {
            var cargo  = run.GroupBy(m => m.TypeId)
                            .Select(g => (TypeId: g.Key, Qty: g.Sum(x => x.Qty)))
                            .OrderByDescending(x => x.Qty)
                            .ToList();
            var reason = run.Min(m => m.Reason);   // the best cargo sets the worth of the run

            var from = places.GetValueOrDefault(run.Key.From, $"Location {run.Key.From}");
            var to   = places.GetValueOrDefault(run.Key.To,   $"Location {run.Key.To}");

            items.Add(new WorklistItem
            {
                // Keyed on the pair, not the cargo: it is one trip, and a key that changed as
                // items were added or removed would reset its age every refresh.
                Key          = $"haul:{run.Key.From}:{run.Key.To}",
                Source       = Id,
                Kind         = WorklistKind.Haul,
                Title        = $"{cargo.Count} item(s)",
                // The manifest lives on the row's own lines now. Repeating four of them here and
                // hiding the rest behind "and 9 more" was a worse answer to the same question.
                Detail       = Because(reason),
                Readiness    = WorklistReadiness.Ready,
                LocationId      = run.Key.From,
                LocationName    = from,
                DestinationId   = run.Key.To,
                DestinationName = to,
                Lines        = cargo
                    .Select(c => new WorklistLine(
                        c.TypeId, names.GetValueOrDefault(c.TypeId, $"Type {c.TypeId}"), c.Qty))
                    .ToList(),
                TypeId       = cargo[0].TypeId,
                TypeName     = names.GetValueOrDefault(cargo[0].TypeId, ""),
                Priority     = reason switch
                {
                    HaulReason.Unblocking => WorklistPriority.HaulUnblocking,
                    HaulReason.Restock    => WorklistPriority.HaulRestock,
                    HaulReason.Refine     => WorklistPriority.HaulToRefine,
                    _                     => WorklistPriority.HaulSurplus,
                },
            });
        }

        return items;
    }

    private static string Because(HaulReason r) => r switch
    {
        HaulReason.Unblocking => "Jobs are waiting on this.",
        HaulReason.Restock    => "Tops the station up to its level.",
        HaulReason.Refine     => "For refining or decompression.",
        _                     => "Spare stock going to where its group lives.",
    };

    /// <summary>Structure names carry their system already; the title only needs the tail.</summary>
    private static string Short(string place)
    {
        var dash = place.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? place[(dash + 3)..] : place;
    }

    // ── Lookups ───────────────────────────────────────────────────────────────

    private async Task<HashSet<long>?> ScopeAsync(AppDbContext db, CancellationToken ct)
    {
        var scope = await InvLevelService.ResolveScopeFilterAsync(
            db, settings.IndustryScope, settings.IndustryScopeId, ct);
        if (scope is not null)
            scope.UnionWith(await db.WorklistIndyScopeStations.AsNoTracking()
                .Select(s => s.LocationId).ToListAsync(ct));
        return scope;
    }

    private static async Task<Dictionary<int, string>> NamesAsync(
        AppDbContext db, List<int> typeIds, CancellationToken ct) =>
        await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

    /// <summary>Station or structure id to the system it sits in, for ranking sources.</summary>
    private static async Task<Dictionary<long, int>> SystemsAsync(AppDbContext db, CancellationToken ct)
    {
        var map = (await db.SdeStations.AsNoTracking()
                .Select(s => new { Id = (long)s.StationId, s.SolarSystemId })
                .ToListAsync(ct))
            .ToDictionary(s => s.Id, s => s.SolarSystemId);

        foreach (var s in await db.EsiStructureNames.AsNoTracking()
                     .Where(s => s.SolarSystemId != 0)
                     .Select(s => new { s.StructureId, s.SolarSystemId }).ToListAsync(ct))
            map[s.StructureId] = s.SolarSystemId;

        foreach (var s in await db.EsiCorpStructures.AsNoTracking()
                     .Select(s => new { s.StructureId, s.SystemId }).ToListAsync(ct))
            map[s.StructureId] = s.SystemId;

        // Assets in space report the solar system itself as their root, so a system is its own
        // location. Without this they rank as unreachable and sort behind every real station.
        foreach (var id in await db.SdeSolarSystems.AsNoTracking()
                     .Select(s => s.SolarSystemId).ToListAsync(ct))
            map.TryAdd(id, id);

        return map;
    }

    private static async Task<Dictionary<long, string>> PlaceNamesAsync(
        AppDbContext db, CancellationToken ct)
    {
        var map = (await db.SdeStations.AsNoTracking()
                .Select(s => new { Id = (long)s.StationId, s.Name }).ToListAsync(ct))
            .ToDictionary(s => s.Id, s => s.Name);

        foreach (var s in await db.EsiStructureNames.AsNoTracking()
                     .Where(s => s.Name != "")
                     .Select(s => new { s.StructureId, s.Name }).ToListAsync(ct))
            map[s.StructureId] = s.Name;

        // Anything in space — an anchored container, a ship left on grid — roots to the system
        // rather than a station. Saying "in space" beats printing a bare id, and it tells the
        // reader why the pickup has no station name.
        foreach (var s in await db.SdeSolarSystems.AsNoTracking()
                     .Select(s => new { s.SolarSystemId, s.Name }).ToListAsync(ct))
            map.TryAdd(s.SolarSystemId, $"{s.Name} (in space)");

        return map;
    }
}
