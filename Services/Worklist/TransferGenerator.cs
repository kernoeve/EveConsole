using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>What each character level keeps with its character: the group's levels, at a station.</summary>
public static class CharacterLevelTargets
{
    /// <summary>
    /// Units of each type to keep with each character at each station, from the enabled character
    /// levels: the group's target times its multiplier, as a station level reads it. Two levels of
    /// one character and station that share a type add up.
    /// </summary>
    public static async Task<Dictionary<(long CharacterId, long Station, int TypeId), long>> LoadAsync(
        AppDbContext db, CancellationToken ct)
    {
        var targets = new Dictionary<(long, long, int), long>();
        var levels = await db.WorklistCharacterLevels.AsNoTracking().Where(l => l.Enabled).ToListAsync(ct);
        if (levels.Count == 0) return targets;

        var groupIds = levels.Select(l => l.GroupId).Distinct().ToList();
        var multipliers = await db.InvLevelGroups.AsNoTracking()
            .Where(g => groupIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => Math.Max(1, g.Multiplier), ct);
        var byGroup = (await db.InvLevelItems.AsNoTracking()
                .Where(i => groupIds.Contains(i.GroupId)).ToListAsync(ct))
            .GroupBy(i => i.GroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var level in levels)
        {
            if (!byGroup.TryGetValue(level.GroupId, out var items)) continue;
            var mult = multipliers.GetValueOrDefault(level.GroupId, 1);
            foreach (var i in items)
            {
                var key = (level.CharacterId, level.LocationId, i.TypeId);
                targets[key] = targets.GetValueOrDefault(key) + (long)i.TargetQuantity * mult;
            }
        }
        return targets;
    }
}

/// <summary>
/// Items at the right station held by the wrong owner.
///
/// <para>A character builds only with what is in its own hangar or its corporation's. So material
/// is kept in one corporation, the default one (<see cref="WorklistSettings.DefaultCorporationId"/>),
/// where every alt in it can use it. Two kinds of task come out of that:</para>
/// <list type="bullet">
/// <item><b>To the corporation.</b> A Worklist item (of an order or an inventory level) that a
/// character holds — in its hangar, or a container there — goes to the
/// default corporation, at a station where it has a hangar. What the character's levels keep with
/// it stays. With <see cref="WorklistSettings.DefaultCorpFromOtherCorps"/>, the player's other
/// corporations give theirs too.</item>
/// <item><b>To a character.</b> A character level short at its station is given what it lacks
/// from the default corporation's hangar there. It's how an alt outside that corporation gets
/// material to build with; hauling brings it to the station first (the level counts there as a
/// station level would).</item>
/// </list>
///
/// <para>⚠️ Never touched: blueprints (what an alt outside the corporation holds is what it can
/// build), assembled ships, anything in a ship at all — fitted, in a bay, or in its cargo hold,
/// where a cyno ship's Liquid Ozone is what lights its cyno (the user's rule, 2026-11-08) — and
/// anything in asset safety. An item two owners' snapshots both list counts where the newest
/// one has it, or a hold just unloaded into the corporation's hangar would be "given" twice.</para>
///
/// <para>Done when the next asset polls show it moved: like every task, it simply stops appearing.</para>
/// </summary>
public class TransferGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    IndustryAssignmentService       assignment,
    WorklistSettings                settings) : IWorklistGenerator
{
    public const string SourceId = "transfers";
    public string Id          => SourceId;
    public string DisplayName => WorklistText.SourceTransfers;

    private const int ShipCategory      = 6;
    private const int BlueprintCategory = 9;

    private sealed record Row(long ItemId, long OwnerId, bool IsCorp, int TypeId, long Station, long Quantity);

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var home    = settings.DefaultCorporationId;
        var targets = await CharacterLevelTargets.LoadAsync(db, ct);
        if (home == 0 && targets.Count == 0) return [];

        var types = await WorklistTypesAsync(db, ct);
        if (types.Count == 0) return [];

        var characters = await db.Characters.AsNoTracking()
            .Select(c => new { c.Id, c.Name, Corp = (long)c.CorporationId }).ToListAsync(ct);
        var corpOf  = characters.ToDictionary(c => c.Id, c => c.Corp);
        var corps   = await assignment.UsableCorporationsAsync(settings.IncludeNonPersonalCorps, ct);
        var givers  = settings.DefaultCorpFromOtherCorps;
        var restockBand = settings.CharacterRestockBandPercent / 100.0;
        var surplusBand = settings.CharacterSurplusBandPercent / 100.0;

        // The default corporation's hangars: wherever it holds anything at all.
        var homeStations = home == 0 ? [] : (await db.EsiAssets.AsNoTracking()
                .Where(a => a.OwnerType == "corporation" && a.OwnerId == home)
                .Select(a => a.RootLocationId).Distinct().ToListAsync(ct))
            .ToHashSet();
        // ⚠️ Only inside the Worklist's asset scope — its region or other choice, and the extra
        // stations — as every other task. Held anywhere else, an item is not the Worklist's to move.
        var scope = await InvLevelService.ResolveScopeFilterAsync(db, settings.IndustryScope, settings.IndustryScopeId, ct);
        if (scope is not null)
        {
            scope.UnionWith(await db.WorklistIndyScopeStations.AsNoTracking().Select(s => s.LocationId).ToListAsync(ct));
            homeStations.IntersectWith(scope);
        }
        var stations = homeStations.Concat(targets.Keys.Select(k => k.Station))
            .Where(s => scope is null || scope.Contains(s)).ToHashSet();
        if (stations.Count == 0) return [];

        var rows = await RowsAsync(db, types, stations, home, givers, corps, ct);

        var held = rows.GroupBy(r => (r.OwnerId, r.Station, r.TypeId))
                       .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));

        var names     = await NamesAsync(db, ct);
        var typeNames = await db.SdeTypes.AsNoTracking()
            .Where(t => types.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => SdeNames.Type(t.TypeId, t.Name), ct);
        var clocks = await HaulLag.AssetClocksAsync(db, ct);

        string NameOf(long id, bool corp) => names.GetValueOrDefault(id,
            string.Format(corp ? WorklistText.CorpWithId : WorklistText.CharacterWithId, id));
        string PlaceOf(long id) => names.GetValueOrDefault(id, string.Format(WorklistText.LocationWithId, id));
        string TypeOf(int id) => typeNames.GetValueOrDefault(id, string.Format(WorklistText.TypeWithId, id));

        // Who does a corporation's part: a character of ours in it, the first by name.
        long MemberOf(long corp) => characters.Where(c => c.Corp == corp).OrderBy(c => c.Name, StringComparer.CurrentCulture)
                                              .Select(c => c.Id).FirstOrDefault();

        var items = new List<WorklistItem>();

        WorklistItem Task(long from, bool fromCorp, long to, bool toCorp, long station, long doer,
                          List<WorklistLine> lines, string detail, int priority)
        {
            var phrase = lines.Count == 1
                ? string.Format(WorklistText.TransferOneLine, lines[0].Quantity, lines[0].TypeName)
                : Plurals.Format(WorklistText.ResourceManager, nameof(WorklistText.TransferTypesOther), lines.Count);
            var asOf = new[] { from, to }.Select(o => clocks.GetValueOrDefault(o)).Where(t => t != default).DefaultIfEmpty().Min();
            return new WorklistItem
            {
                Key           = $"transfer:{from}:{to}:{station}",
                Source        = Id,
                Kind          = WorklistKind.Transfer,
                Title         = string.Format(WorklistText.TransferTitle, phrase, NameOf(to, toCorp)),
                Detail        = detail,
                CharacterId   = doer,
                CharacterName = doer == 0 ? "" : NameOf(doer, false),
                LocationId    = station,
                LocationName  = PlaceOf(station),
                TypeId        = lines.Count == 1 ? lines[0].TypeId : 0,
                TypeName      = lines.Count == 1 ? lines[0].TypeName : "",
                Quantity      = lines.Count == 1 ? lines[0].Quantity : 0,
                Lines         = lines.Count == 1 ? [] : lines,
                Priority      = priority,
                IconUrl       = lines.Count == 1 ? null : toCorp ? WorklistIcons.CorporationLogo(to) : WorklistIcons.Portrait(to),
                DataAsOf      = asOf == default ? null : asOf,
            };
        }

        // ── To the default corporation ────────────────────────────────────────
        if (home != 0)
        {
            foreach (var group in held.Where(h => h.Key.OwnerId != home && homeStations.Contains(h.Key.Station))
                                      .GroupBy(h => (h.Key.OwnerId, h.Key.Station)))
            {
                var (owner, station) = group.Key;
                var isCorp = !corpOf.ContainsKey(owner);
                // What a character's level keeps stays, and the rest goes back once it is more
                // than the surplus band over that level — then all of it, down to the level.
                // Nothing kept is nothing over: all of it goes.
                var lines = group
                    .Select(h =>
                    {
                        var keep = isCorp ? 0 : targets.GetValueOrDefault((owner, station, h.Key.TypeId));
                        var over = h.Value > keep * (1 + surplusBand);
                        return (h.Key.TypeId, Give: over ? h.Value - keep : 0);
                    })
                    .Where(x => x.Give > 0)
                    .Select(x => new WorklistLine(x.TypeId, TypeOf(x.TypeId), x.Give))
                    .OrderBy(l => l.TypeName, StringComparer.CurrentCulture)
                    .ToList();
                if (lines.Count == 0) continue;

                var place = PlaceOf(station);
                var corp  = NameOf(home, true);
                long doer;
                string detail;
                if (isCorp)
                {
                    doer   = MemberOf(owner);
                    detail = string.Format(WorklistText.TransferCorpToCorp,
                        doer == 0 ? NameOf(owner, true) : NameOf(doer, false), NameOf(owner, true), corp, place);
                }
                else if (corpOf.GetValueOrDefault(owner) == home)
                {
                    doer   = owner;
                    detail = string.Format(WorklistText.TransferToCorpInCorp, NameOf(owner, false), corp, place);
                }
                else
                {
                    doer   = owner;
                    detail = string.Format(WorklistText.TransferToCorpContract, NameOf(owner, false), corp, place);
                }

                items.Add(Task(owner, isCorp, home, true, station, doer, lines, detail, WorklistPriority.TransferToCorp));
            }
        }

        // ── To a character, for its character level ───────────────────────────
        if (home != 0)
        {
            var corpLeft = held.Where(h => h.Key.OwnerId == home)
                               .ToDictionary(h => (h.Key.Station, h.Key.TypeId), h => h.Value);

            foreach (var group in targets.GroupBy(t => (t.Key.CharacterId, t.Key.Station)).OrderBy(g => g.Key.CharacterId))
            {
                var (who, station) = group.Key;
                if (!corpOf.ContainsKey(who)) continue;   // a character no longer here

                var lines = new List<WorklistLine>();
                foreach (var (key, target) in group.OrderBy(g => g.Key.TypeId))
                {
                    // Left alone until it is more than the restock band under its level, then
                    // given enough to fill it all the way back up.
                    var has  = held.GetValueOrDefault((who, station, key.TypeId));
                    if (has >= target * (1 - restockBand)) continue;
                    var lack = target - has;
                    var there = corpLeft.GetValueOrDefault((station, key.TypeId));
                    var give  = Math.Min(lack, there);
                    if (give <= 0) continue;
                    corpLeft[(station, key.TypeId)] = there - give;
                    lines.Add(new WorklistLine(key.TypeId, TypeOf(key.TypeId), give));
                }
                if (lines.Count == 0) continue;
                lines = [.. lines.OrderBy(l => l.TypeName, StringComparer.CurrentCulture)];

                var place = PlaceOf(station);
                var corp  = NameOf(home, true);
                var inHome = corpOf[who] == home;
                var doer   = inHome ? who : MemberOf(home);
                var detail = inHome
                    ? string.Format(WorklistText.TransferFromCorpSelf, NameOf(who, false), corp, place)
                    : string.Format(WorklistText.TransferFromCorpContract,
                          doer == 0 ? corp : NameOf(doer, false), corp, NameOf(who, false), place);

                items.Add(Task(home, true, who, false, station, doer, lines, detail, WorklistPriority.TransferToCharacter));
            }
        }

        return items;
    }

    /// <summary>
    /// The Worklist's items: every item in the groups its inventory rules, station levels and
    /// character levels use, and every item of a customer order still to be supplied.
    /// </summary>
    private static async Task<HashSet<int>> WorklistTypesAsync(AppDbContext db, CancellationToken ct)
    {
        var groupIds = (await db.WorklistInvRules.AsNoTracking().Where(r => r.Enabled).Select(r => r.GroupId).ToListAsync(ct))
            .Concat(await db.WorklistStationLevels.AsNoTracking().Where(l => l.Enabled).Select(l => l.GroupId).ToListAsync(ct))
            .Concat(await db.WorklistCharacterLevels.AsNoTracking().Where(l => l.Enabled).Select(l => l.GroupId).ToListAsync(ct))
            .Distinct().ToList();

        var types = (await db.InvLevelItems.AsNoTracking()
                .Where(i => groupIds.Contains(i.GroupId)).Select(i => i.TypeId).ToListAsync(ct))
            .ToHashSet();

        foreach (var o in (await db.TrackedOrders.AsNoTracking()
                     .Where(o => o.Status == "pending" && o.Side != StorePosting.Buy).ToListAsync(ct))
                 .Where(o => OrderContractLinks.StillToSupply(o) > 0))
            types.Add(o.TypeId);

        return types;
    }

    /// <summary>
    /// The holdings that can be transferred, at the stations asked about: characters', the default
    /// corporation's, and the player's other corporations' when they give too. Each placed at its
    /// station; nothing in a ship.
    /// </summary>
    private static async Task<List<Row>> RowsAsync(
        AppDbContext db, HashSet<int> types, HashSet<long> stations, long home, bool givers,
        HashSet<long>? corps, CancellationToken ct)
    {
        var typeList    = types.ToList();
        var stationList = stations.ToList();
        var raw = await db.EsiAssets.AsNoTracking()
            .Where(a => typeList.Contains(a.TypeId) && stationList.Contains(a.RootLocationId))
            .Select(a => new { a.ItemId, a.OwnerId, a.OwnerType, a.TypeId, a.LocationId, a.LocationType,
                               a.LocationFlag, a.Quantity, a.IsSingleton, a.RootLocationId })
            .ToListAsync(ct);

        raw = [.. raw.Where(a => a.OwnerType != "corporation"
                              || a.OwnerId == home
                              || (givers && (corps is null || corps.Contains(a.OwnerId))))];
        if (raw.Count == 0) return [];

        // What each item sits in, up its chain, to see a ship's fit or asset safety above it.
        var parents = new Dictionary<long, (long LocationId, string LocationType, string Flag, int TypeId)>();
        var frontier = raw.Where(a => a.LocationType == "item").Select(a => a.LocationId).Distinct().ToList();
        for (var depth = 0; depth < 8 && frontier.Count > 0; depth++)
        {
            var found = await db.EsiAssets.AsNoTracking()
                .Where(a => frontier.Contains(a.ItemId))
                .Select(a => new { a.ItemId, a.LocationId, a.LocationType, a.LocationFlag, a.TypeId })
                .ToListAsync(ct);
            foreach (var f in found) parents.TryAdd(f.ItemId, (f.LocationId, f.LocationType, f.LocationFlag, f.TypeId));
            frontier = [.. found.Where(f => f.LocationType == "item" && !parents.ContainsKey(f.LocationId))
                                .Select(f => f.LocationId).Distinct()];
        }

        var lookUp = typeList.Concat(parents.Values.Select(p => p.TypeId)).Distinct().ToList();
        var categoryOf = await db.SdeTypes.AsNoTracking()
            .Where(t => lookUp.Contains(t.TypeId))
            .Join(db.SdeGroups.AsNoTracking(), t => t.GroupId, g => g.GroupId, (t, g) => new { t.TypeId, g.CategoryId })
            .ToDictionaryAsync(x => x.TypeId, x => x.CategoryId, ct);

        // An item two snapshots list is where the newest has it.
        var clocks = await HaulLag.AssetClocksAsync(db, ct);
        var newest = raw.GroupBy(a => a.ItemId).Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.MaxBy(a => clocks.GetValueOrDefault(a.OwnerId))!.OwnerId);

        var rows = new List<Row>(raw.Count);
        foreach (var a in raw)
        {
            if (newest.TryGetValue(a.ItemId, out var keeper) && keeper != a.OwnerId) continue;
            var category = categoryOf.GetValueOrDefault(a.TypeId);
            if (category == BlueprintCategory) continue;
            if (category == ShipCategory && a.IsSingleton) continue;
            if (a.LocationFlag == "AssetSafety") continue;

            // Up the chain: in a ship, or in asset safety, is never moved. ⚠️ Not even the cargo
            // hold: what a ship carries is packed for its work — a cyno ship's Liquid Ozone lights
            // its cyno, and given to the corporation it could not.
            var keep = true;
            var (loc, locType) = (a.LocationId, a.LocationType);
            for (var depth = 0; depth < 8 && locType == "item" && parents.TryGetValue(loc, out var p); depth++)
            {
                if (categoryOf.GetValueOrDefault(p.TypeId) == ShipCategory || p.Flag == "AssetSafety") { keep = false; break; }
                (loc, locType) = (p.LocationId, p.LocationType);
            }
            if (!keep) continue;

            rows.Add(new Row(a.ItemId, a.OwnerId, a.OwnerType == "corporation", a.TypeId, a.RootLocationId, a.Quantity));
        }
        return rows;
    }

    private static async Task<Dictionary<long, string>> NamesAsync(AppDbContext db, CancellationToken ct)
    {
        var map = (await db.SdeStations.AsNoTracking()
                .Select(s => new { Id = (long)s.StationId, s.Name }).ToListAsync(ct))
            .ToDictionary(s => s.Id, s => SdeNames.Station(s.Id, s.Name));
        foreach (var s in await db.EsiStructureNames.AsNoTracking().Where(s => s.Name != "")
                     .Select(s => new { s.StructureId, s.Name }).ToListAsync(ct))
            map[s.StructureId] = s.Name;
        foreach (var c in await db.Characters.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            map[c.Id] = c.Name;
        foreach (var c in await db.Corporations.AsNoTracking().Select(c => new { Id = (long)c.Id, c.Name }).ToListAsync(ct))
            map[c.Id] = c.Name;
        return map;
    }
}
