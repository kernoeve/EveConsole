using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Fitting;

/// <summary>
/// One assembled ship from the asset lists: what it is, whose, where, what is fitted to it, and
/// what that is worth.
/// </summary>
/// <param name="Name">The name its owner gave it, from ESI; null when it was never named or the
/// name could not be asked for.</param>
/// <param name="HullName">The hull's English name; the screen shows it in the interface language.</param>
/// <param name="Fitted">What the fit is made of, in the game's own fitting layout: modules, rigs and
/// subsystems by slot, loaded charges, and the drone and fighter bays. The cargo hold and the
/// other holds are not part of it.</param>
/// <param name="Charges">The charge loaded in each slot, by its flag ("HiSlot0").</param>
public sealed record ExistingShip(
    long ItemId, int HullTypeId, string HullName, string? Name,
    long OwnerId, bool OwnedByCorporation, string OwnerName,
    int? SolarSystemId, string SystemName,
    IReadOnlyList<EsiFittingItem> Fitted, IReadOnlyDictionary<string, int> Charges,
    double HullValue, double FitValue)
{
    public double TotalValue => HullValue + FitValue;
}

/// <summary>The ships, and the price basis their values are on (as <see cref="FitPrices.Basis"/>).</summary>
public sealed record ExistingShipList(IReadOnlyList<ExistingShip> Ships, string PriceBasis, bool NamesMissing);

/// <summary>
/// Every assembled ship the characters and personal corporations own, read from the stored asset
/// lists — the fitting tool's Existing ships picker.
///
/// <para>⚠️ The fit is what the last asset poll saw, not what is fitted this minute: assets are
/// polled hourly. Ship names are not in the asset list at all; they are asked of ESI when the list
/// is opened, and kept for the rest of the session.</para>
/// </summary>
public static class ExistingShips
{
    private const int GroupCapsule = 29;

    /// <summary>The flags a fit is made of. Everything else in a ship — its cargo, its holds, a
    /// carrier's hangars — is what it carries, not how it is fitted.</summary>
    internal static bool IsFitted(string flag) =>
        flag.StartsWith("HiSlot") || flag.StartsWith("MedSlot") || flag.StartsWith("LoSlot")
        || flag.StartsWith("RigSlot") || flag.StartsWith("SubSystemSlot")
        || flag is "DroneBay" or "FighterBay" || flag.StartsWith("FighterTube");

    // Names asked of ESI this session, by item id: a ship is rarely renamed, and asking every time
    // the list opens would cost a call per owner for nothing. An empty string is "never named".
    private static readonly Dictionary<long, string> NameCache = [];
    private static readonly object NameLock = new();

    public static async Task<ExistingShipList> LoadAsync(IDbContextFactory<AppDbContext> dbFactory, EsiClient? esi,
                                                         CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var characters = await db.Characters.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var corps      = await db.Corporations.AsNoTracking().Where(c => c.IsPersonal)
                                  .Select(c => new { Id = (long)c.Id, c.Name }).ToListAsync(ct);
        var charIds = characters.Select(c => c.Id).ToList();
        var corpIds = corps.Select(c => c.Id).ToList();

        var shipTypes = await (from t in db.SdeTypes.AsNoTracking()
                               join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                               where g.CategoryId == DogmaData.CategoryShip && g.GroupId != GroupCapsule
                               select new { t.TypeId, t.Name }).ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);
        var shipTypeIds = shipTypes.Keys.ToList();

        // Assembled hulls only: a packaged ship is not a singleton and has nothing fitted.
        var ships = await db.EsiAssets.AsNoTracking()
            .Where(a => a.IsSingleton && shipTypeIds.Contains(a.TypeId)
                        && ((a.OwnerType == "character" && charIds.Contains(a.OwnerId))
                            || (a.OwnerType == "corporation" && corpIds.Contains(a.OwnerId))))
            .ToListAsync(ct);
        if (ships.Count == 0) return new ExistingShipList([], "", false);

        var shipIds  = ships.Select(s => s.ItemId).ToList();
        var contents = await db.EsiAssets.AsNoTracking().Where(a => shipIds.Contains(a.LocationId)).ToListAsync(ct);

        var systemIds = ships.Where(s => s.SolarSystemId is not null).Select(s => s.SolarSystemId!.Value).Distinct().ToList();
        var systems   = await db.SdeSolarSystems.AsNoTracking().Where(s => systemIds.Contains(s.SolarSystemId))
                                .ToDictionaryAsync(s => s.SolarSystemId, s => s.Name, ct);

        var contentTypeIds = contents.Select(c => c.TypeId).Distinct().ToList();
        var categories = await (from t in db.SdeTypes.AsNoTracking()
                                join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                                where contentTypeIds.Contains(t.TypeId)
                                select new { t.TypeId, g.CategoryId }).ToDictionaryAsync(t => t.TypeId, t => t.CategoryId, ct);

        var names = await NamesAsync(esi, ships.Select(s => (s.OwnerId, s.OwnerType == "corporation", s.ItemId)).ToList(), ct);

        var prices = await FitPricing.LoadAsync(dbFactory,
            ships.Select(s => s.TypeId).Concat(contents.Select(c => c.TypeId)), ct);
        double Price(int typeId) => prices.Of(typeId) ?? 0;

        var byShip = contents.Where(c => IsFitted(c.LocationFlag)).ToLookup(c => c.LocationId);
        var list = new List<ExistingShip>(ships.Count);
        foreach (var s in ships)
        {
            var fitted  = new List<EsiFittingItem>();
            var charges = new Dictionary<string, int>();
            double value = 0;
            foreach (var item in byShip[s.ItemId])
            {
                var qty = Math.Max(1, item.Quantity);
                value += Price(item.TypeId) * qty;
                // A charge in a slot is the module's ammunition, not a module of its own.
                if (categories.GetValueOrDefault(item.TypeId) == DogmaData.CategoryCharge
                    && !item.LocationFlag.StartsWith("Drone") && !item.LocationFlag.StartsWith("Fighter"))
                    charges[item.LocationFlag] = item.TypeId;
                else
                    fitted.Add(new EsiFittingItem(item.TypeId,
                        item.LocationFlag.StartsWith("FighterTube") ? "FighterBay" : item.LocationFlag, qty));
            }

            var corp = s.OwnerType == "corporation";
            var owner = corp ? corps.FirstOrDefault(c => c.Id == s.OwnerId)?.Name : characters.FirstOrDefault(c => c.Id == s.OwnerId)?.Name;
            list.Add(new ExistingShip(
                s.ItemId, s.TypeId, shipTypes.GetValueOrDefault(s.TypeId, ""), names.GetValueOrDefault(s.ItemId),
                s.OwnerId, corp, owner ?? s.OwnerId.ToString(),
                s.SolarSystemId, s.SolarSystemId is { } sys ? systems.GetValueOrDefault(sys, "") : "",
                fitted, charges, Price(s.TypeId), value));
        }
        // A ship ESI was not asked about, or did not answer for, has no name this time.
        return new ExistingShipList(list, prices.Basis, ships.Any(s => !NameKnown(s.ItemId)));
    }

    private static bool NameKnown(long itemId) { lock (NameLock) return NameCache.ContainsKey(itemId); }

    /// <summary>The names owners gave their ships, asked of ESI once a session per ship: by each
    /// owner, a thousand at a time. A ship whose name could not be asked has none here.</summary>
    private static async Task<Dictionary<long, string>> NamesAsync(EsiClient? esi,
        List<(long OwnerId, bool Corporation, long ItemId)> ships, CancellationToken ct)
    {
        var found = new Dictionary<long, string>();
        var ask   = new List<(long OwnerId, bool Corporation, long ItemId)>();
        lock (NameLock)
            foreach (var s in ships)
                if (NameCache.TryGetValue(s.ItemId, out var n)) { if (n.Length > 0) found[s.ItemId] = n; }
                else ask.Add(s);

        if (esi is null) return found;
        foreach (var owner in ask.GroupBy(s => (s.OwnerId, s.Corporation)))
            foreach (var chunk in owner.Select(s => s.ItemId).Chunk(1000))
            {
                var (status, data) = owner.Key.Corporation
                    ? await esi.PostCorpAuthAsync<List<EsiAssetName>>(owner.Key.OwnerId, $"corporations/{owner.Key.OwnerId}/assets/names/", chunk, ct)
                    : await esi.PostAuthAsync<List<EsiAssetName>>(owner.Key.OwnerId, $"characters/{owner.Key.OwnerId}/assets/names/", chunk, ct);
                if (status is < 200 or >= 300 || data is null) continue;   // left unnamed; asked again next time

                lock (NameLock)
                {
                    // Every id asked is answered; one the game has no name for comes back as "None".
                    foreach (var id in chunk) NameCache[id] = "";
                    foreach (var n in data)
                    {
                        var name = n.Name is null or "None" ? "" : n.Name.Trim();
                        NameCache[n.ItemId] = name;
                        if (name.Length > 0) found[n.ItemId] = name;
                    }
                }
            }
        return found;
    }

    /// <summary>
    /// The ship's fit, ready for a tab: built the way a fitting from the game is
    /// (<see cref="EftFormat.FromEsiAsync"/>), then each slot's loaded charge put back in its
    /// module. Named after the ship — or its hull, if never named.
    /// </summary>
    public static async Task<FitDefinition> ToFitAsync(ExistingShip ship, string fallbackName, DogmaData data,
                                                       FittingCatalog catalog, CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(ship.Name) ? fallbackName : ship.Name!;
        var fit  = await EftFormat.FromEsiAsync(new EsiFittingData(0, name, "", ship.HullTypeId, [.. ship.Fitted]), data, catalog, ct);
        await data.LoadTypesAsync(ship.Charges.Values, ct);

        // The modules in the order FromEsiAsync added them: by rack, then slot, skipping a type
        // the game data does not know.
        var order = ship.Fitted
            .Where(i => !(i.Flag is "DroneBay" or "FighterBay") && data.TryType(i.TypeId, out var t) && t.CategoryId != DogmaData.CategoryImplant)
            .OrderBy(i => Rack(i.Flag)).ThenBy(i => SlotIndex(i.Flag))
            .SelectMany(i => Enumerable.Repeat(i.Flag, Math.Max(1, i.Quantity)))
            .ToList();
        for (var i = 0; i < fit.Modules.Count && i < order.Count; i++)
            if (ship.Charges.TryGetValue(order[i], out var charge) && data.TryType(charge, out _))
                fit.Modules[i] = fit.Modules[i] with { ChargeTypeId = charge };
        return fit;
    }

    private static int SlotIndex(string flag) =>
        int.TryParse(new string(flag.SkipWhile(c => !char.IsDigit(c)).ToArray()), out var n) ? n : 0;

    private static int Rack(string flag) => flag switch
    {
        _ when flag.StartsWith("HiSlot")        => 0,
        _ when flag.StartsWith("MedSlot")       => 1,
        _ when flag.StartsWith("LoSlot")        => 2,
        _ when flag.StartsWith("RigSlot")       => 3,
        _ when flag.StartsWith("SubSystemSlot") => 4,
        _ when flag.StartsWith("ServiceSlot")   => 5,
        _                                       => 6,
    };
}
