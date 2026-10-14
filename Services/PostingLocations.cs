using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// What a posting's places hold: a station or structure, narrowed to an owner, a hangar or
/// division, and a container at any depth — see <see cref="SalePostingLocation"/>.
///
/// <para>Only what can be handed over counts. Containers themselves are not for sale (what is in
/// them is), nor is anything inside a ship — a fitted module, a hold's cargo — nor an office, nor
/// whatever sits in asset safety or impound. Only our own characters and the corporations we poll
/// are looked at, and an item two owners' snapshots both hold is counted once, from the owner
/// polled last (see <see cref="HaulLag.AssetClocksAsync"/>).</para>
/// </summary>
public static class PostingLocations
{
    /// <summary>A corporation's office: corp hangar divisions hang off it.</summary>
    private const int OfficeTypeId   = 27;
    private const int ShipCategoryId = 6;

    /// <summary>The hangars things for sale sit in at a station. Asset safety, impound and the
    /// like are left out: nothing in them can be contracted.</summary>
    private static readonly HashSet<string> Hangars =
    [
        "Hangar", "Deliveries", "CorpDeliveries",
        "CorpSAG1", "CorpSAG2", "CorpSAG3", "CorpSAG4", "CorpSAG5", "CorpSAG6", "CorpSAG7",
    ];

    private sealed record Asset(long ItemId, long LocationId, string Flag, long Root, string RootType,
                                long OwnerId, string OwnerType, int TypeId, long Qty, bool Singleton);

    /// <summary>Where an asset sits: its hangar at the station, the containers it is inside (nearest
    /// first), and whether any of them is a ship.</summary>
    private sealed record Place(string Hangar, List<long> Containers, bool InShip);

    // ── What the places hold ─────────────────────────────────────────────────

    /// <summary>
    /// Units of each type at each place, by the place's id. An item two places both take in is
    /// counted in the first of them only, so overlapping places do not list it twice.
    /// </summary>
    /// <param name="onlyPackaged">The posting's "only packaged": assembled items are left out.</param>
    public static async Task<Dictionary<int, Dictionary<int, long>>> ContentsAsync(
        AppDbContext db, IReadOnlyList<SalePostingLocation> places, bool onlyPackaged, CancellationToken ct = default)
    {
        var result = places.ToDictionary(p => p.Id, _ => new Dictionary<int, long>());
        if (places.Count == 0) return result;

        var assets = await LoadAsync(db, places.Select(p => p.StationId).Distinct().ToList(), ct);
        var (placed, holders, ships) = await PlaceAllAsync(db, assets, ct);

        foreach (var a in assets)
        {
            if (!ForSale(a, placed[a.ItemId], holders, ships) || (onlyPackaged && a.Singleton)) continue;
            var place = placed[a.ItemId];
            var first = places.FirstOrDefault(p => Takes(p, a, place));
            if (first is null) continue;
            var units = result[first.Id];
            units[a.TypeId] = units.GetValueOrDefault(a.TypeId) + a.Qty;
        }
        return result;
    }

    private static bool Takes(SalePostingLocation p, Asset a, Place place) =>
        a.Root == p.StationId
        && (p.OwnerType.Length == 0 || (a.OwnerType == p.OwnerType && (p.OwnerId == 0 || a.OwnerId == p.OwnerId)))
        && (p.Flag.Length == 0 || place.Hangar == p.Flag)
        && (p.ContainerId == 0 || place.Containers.Contains(p.ContainerId));

    /// <summary>Something a buyer could be handed: in a hangar that counts, not inside a ship, and
    /// not itself a container or an office.</summary>
    private static bool ForSale(Asset a, Place place, HashSet<long> holders, HashSet<int> ships) =>
        Hangars.Contains(place.Hangar)
        && !place.InShip
        && a.TypeId != OfficeTypeId
        && !(holders.Contains(a.ItemId) && !ships.Contains(a.TypeId));

    // ── The places to pick from ───────────────────────────────────────────────

    /// <summary>One step of the picker's tree: a station, an owner there, a hangar, a container.</summary>
    public sealed record Node(string Kind, string Label, SalePostingLocation Place, List<Node> Children)
    {
        /// <summary>A container: its item, and who owns it, for asking ESI the name it was given.</summary>
        public long ItemId  { get; init; }
        public long OwnerId { get; init; }
        public bool OwnerIsCorporation { get; init; }
    }

    /// <summary>
    /// Every station and structure something of ours is in, with its owners, their hangars and
    /// divisions, and the containers in them, nested as they are. Ships are not offered as places,
    /// though things sit in them: what is in a ship is not for sale.
    /// </summary>
    public static async Task<List<Node>> TreeAsync(AppDbContext db, CancellationToken ct = default)
    {
        var assets = await LoadAsync(db, null, ct);
        var (placed, holders, ships) = await PlaceAllAsync(db, assets, ct);

        var stationNames = await StationNamesAsync(db, assets.Select(a => a.Root).Distinct().ToList(), ct);
        var ownerNames   = await OwnerNamesAsync(db, ct);
        var divisions    = (await db.EsiCorpDivisions.AsNoTracking()
                .Where(d => d.DivisionType == "hangar" && d.Name != "")
                .ToListAsync(ct))
            .GroupBy(d => ((long)d.CorporationId, d.Division)).ToDictionary(g => g.Key, g => g.First().Name);
        var typeIds   = assets.Where(a => holders.Contains(a.ItemId)).Select(a => a.TypeId).Distinct().ToList();
        // Container types named as the screen names them, the English where the SDE has nothing else.
        var typeNames = await db.SdeTypes.AsNoTracking().Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => SdeNames.Type(t.TypeId, t.Name), ct);

        var byId = assets.ToDictionary(a => a.ItemId);
        // What each container holds for sale, at any depth: told apart by it when two of a kind
        // sit side by side and neither was ever named.
        var holds = new Dictionary<long, int>();
        foreach (var a in assets)
            if (ForSale(a, placed[a.ItemId], holders, ships))
                foreach (var box in placed[a.ItemId].Containers) holds[box] = holds.GetValueOrDefault(box) + 1;
        // Containers: holders that are not ships, offices or anything already inside a ship.
        var boxes = assets
            .Where(a => holders.Contains(a.ItemId) && !ships.Contains(a.TypeId) && a.TypeId != OfficeTypeId
                     && Hangars.Contains(placed[a.ItemId].Hangar) && !placed[a.ItemId].InShip)
            .ToList();

        var tree = new List<Node>();
        foreach (var station in assets.Where(a => Hangars.Contains(placed[a.ItemId].Hangar) && !placed[a.ItemId].InShip)
                                      .GroupBy(a => a.Root)
                                      .OrderBy(g => stationNames.GetValueOrDefault(g.Key, ""), StringComparer.CurrentCultureIgnoreCase))
        {
            var stationName = stationNames.GetValueOrDefault(station.Key) ?? string.Format(SalesText.LocationNumbered, station.Key);
            var at = new SalePostingLocation { StationId = station.Key };
            var stationNode = new Node("station", stationName, at, []);

            foreach (var owner in station.GroupBy(a => (a.OwnerType, a.OwnerId))
                                         .OrderBy(g => g.Key.OwnerType == "corporation")
                                         .ThenBy(g => ownerNames.GetValueOrDefault(g.Key.OwnerId, "")))
            {
                var corp      = owner.Key.OwnerType == "corporation";
                var ownerName = ownerNames.GetValueOrDefault(owner.Key.OwnerId) ?? owner.Key.OwnerId.ToString();
                var ownerAt   = new SalePostingLocation { StationId = station.Key, OwnerType = owner.Key.OwnerType, OwnerId = owner.Key.OwnerId };
                var ownerNode = new Node("owner", string.Format(corp ? SalesText.LocationCorpOwner : SalesText.LocationPersonalOwner, ownerName), ownerAt, []);

                foreach (var hangar in owner.Select(a => placed[a.ItemId].Hangar).Distinct().OrderBy(HangarOrder))
                {
                    var hangarAt   = new SalePostingLocation { StationId = station.Key, OwnerType = owner.Key.OwnerType, OwnerId = owner.Key.OwnerId, Flag = hangar };
                    var hangarNode = new Node("division", HangarLabel(hangar, owner.Key.OwnerId, divisions), hangarAt, []);

                    // The containers straight in this hangar, each with those inside it.
                    foreach (var box in boxes.Where(b => b.OwnerId == owner.Key.OwnerId && b.Root == station.Key
                                                      && placed[b.ItemId].Hangar == hangar && placed[b.ItemId].Containers.Count == 0))
                        hangarNode.Children.Add(BoxNode(box, hangarAt));
                    ownerNode.Children.Add(hangarNode);
                }
                stationNode.Children.Add(ownerNode);
            }
            tree.Add(stationNode);
        }
        return tree;

        Node BoxNode(Asset box, SalePostingLocation within)
        {
            var at = new SalePostingLocation
            {
                StationId = within.StationId, OwnerType = within.OwnerType, OwnerId = within.OwnerId,
                Flag = within.Flag, ContainerId = box.ItemId,
            };
            var label = string.Format(SalesText.PlacesContainerHolds, typeNames.GetValueOrDefault(box.TypeId, box.TypeId.ToString()),
                Plurals.Format(SalesText.ResourceManager, nameof(SalesText.PlacesItemsOther), holds.GetValueOrDefault(box.ItemId)));
            var node = new Node("container", label, at, [])
            {
                ItemId = box.ItemId, OwnerId = box.OwnerId, OwnerIsCorporation = box.OwnerType == "corporation",
            };
            foreach (var inner in boxes.Where(b => b.LocationId == box.ItemId))
                node.Children.Add(BoxNode(inner, within));
            return node;
        }
    }

    private static int HangarOrder(string flag) => flag switch
    {
        "Hangar"         => 0,
        "Deliveries"     => 1,
        "CorpDeliveries" => 9,
        _                => flag.StartsWith("CorpSAG", StringComparison.Ordinal) && int.TryParse(flag[7..], out var n) ? 1 + n : 50,
    };

    /// <summary>A hangar as the game names it: a corporation division by the name the corporation
    /// gave it, where the app has it.</summary>
    private static string HangarLabel(string flag, long corpId, Dictionary<(long, int), string> divisions)
    {
        if (flag.StartsWith("CorpSAG", StringComparison.Ordinal) && int.TryParse(flag[7..], out var n))
            return divisions.TryGetValue((corpId, n), out var name)
                ? string.Format(SalesText.LocationDivisionNamed, n, name)
                : string.Format(SalesText.LocationDivision, n);
        return flag switch
        {
            "Hangar"         => SalesText.LocationHangar,
            "Deliveries"     => SalesText.LocationDeliveries,
            "CorpDeliveries" => SalesText.LocationDeliveries,
            _                => flag,
        };
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    /// <summary>Our owners' assets at stations and structures — at these, or at all — one copy per item.</summary>
    private static async Task<List<Asset>> LoadAsync(AppDbContext db, IReadOnlyCollection<long>? roots, CancellationToken ct)
    {
        var chars = (await db.Characters.AsNoTracking().Where(c => c.RefreshToken != "").Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        var corps = (await db.Corporations.AsNoTracking().Where(c => c.RefreshToken != "").Select(c => (long)c.Id).ToListAsync(ct)).ToHashSet();

        var query = db.EsiAssets.AsNoTracking()
            .Where(a => a.RootLocationType == "station" || a.RootLocationType == "structure");
        if (roots is not null) query = query.Where(a => roots.Contains(a.RootLocationId));

        var rows = (await query
                .Select(a => new Asset(a.ItemId, a.LocationId, a.LocationFlag, a.RootLocationId, a.RootLocationType,
                                       a.OwnerId, a.OwnerType, a.TypeId, a.Quantity, a.IsSingleton))
                .ToListAsync(ct))
            .Where(a => a.OwnerType == "corporation" ? corps.Contains(a.OwnerId) : chars.Contains(a.OwnerId))
            .ToList();

        // ⚠️ One item, one copy: an item can be in two owners' snapshots at once.
        var clocks = await HaulLag.AssetClocksAsync(db, ct);
        return rows.GroupBy(a => a.ItemId)
                   .Select(g => g.Count() == 1 ? g.First() : g.OrderByDescending(a => clocks.GetValueOrDefault(a.OwnerId)).First())
                   .ToList();
    }

    /// <summary>Where every asset sits, which items hold others, and which types are ships.</summary>
    private static async Task<(Dictionary<long, Place> Placed, HashSet<long> Holders, HashSet<int> Ships)> PlaceAllAsync(
        AppDbContext db, List<Asset> assets, CancellationToken ct)
    {
        var byId    = assets.ToDictionary(a => a.ItemId);
        var holders = assets.Select(a => a.LocationId).Where(byId.ContainsKey).ToHashSet();

        var typeIds = assets.Select(a => a.TypeId).Distinct().ToList();
        var ships = (await (from t in db.SdeTypes.AsNoTracking()
                            join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                            where typeIds.Contains(t.TypeId) && g.CategoryId == ShipCategoryId
                            select t.TypeId).ToListAsync(ct)).ToHashSet();

        var placed = new Dictionary<long, Place>(assets.Count);
        foreach (var a in assets)
        {
            // Up to the hangar: the item whose parent is the station itself, or a corporation's
            // office (whose divisions are the flags of what sits in it).
            var chain = new List<long>();
            var cur   = a;
            var depth = 0;
            while (byId.TryGetValue(cur.LocationId, out var parent) && parent.TypeId != OfficeTypeId && depth++ < 32)
            {
                chain.Add(parent.ItemId);
                cur = parent;
            }
            placed[a.ItemId] = new Place(cur.Flag, chain, chain.Any(id => ships.Contains(byId[id].TypeId)));
        }
        return (placed, holders, ships);
    }

    private static async Task<Dictionary<long, string>> StationNamesAsync(AppDbContext db, List<long> ids, CancellationToken ct)
    {
        var small = ids.Where(id => id <= int.MaxValue).Select(id => (int)id).ToList();
        var map = (await db.SdeStations.AsNoTracking()
                .Where(s => small.Contains(s.StationId))
                .Select(s => new { s.StationId, s.Name }).ToListAsync(ct))
            .ToDictionary(s => (long)s.StationId, s => SdeNames.Station(s.StationId, s.Name));
        foreach (var s in await db.EsiStructureNames.AsNoTracking()
                     .Where(s => ids.Contains(s.StructureId) && s.Name != "")
                     .Select(s => new { s.StructureId, s.Name }).ToListAsync(ct))
            map[s.StructureId] = s.Name;
        return map;
    }

    private static async Task<Dictionary<long, string>> OwnerNamesAsync(AppDbContext db, CancellationToken ct)
    {
        var map = (await db.Characters.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => c.Name);
        foreach (var c in await db.Corporations.AsNoTracking().Select(c => new { Id = (long)c.Id, c.Name }).ToListAsync(ct))
            map[c.Id] = c.Name;
        return map;
    }
}
