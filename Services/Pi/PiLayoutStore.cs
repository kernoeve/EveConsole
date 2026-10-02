using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Pi;

/// <summary>
/// Colony layouts between ESI, the database and the engine: stored one colony at a time, replaced
/// whole in one transaction, and read back as <see cref="PiColonyLayout"/>.
/// </summary>
public static class PiLayoutStore
{
    /// <summary>How many snapshot-to-snapshot windows of movements a colony keeps. Tax learning
    /// only ever needs the windows around recent journal entries.</summary>
    private const int MovementWindowsKept = 20;

    /// <summary>
    /// Every stored layout for these characters (all characters when null), with the colony
    /// list's planet type, system and upgrade level. A colony with no layout read yet is left out.
    /// </summary>
    public static async Task<List<PiColonyLayout>> LoadAsync(AppDbContext db, IReadOnlyCollection<long>? characterIds,
                                                             CancellationToken ct = default)
    {
        var ids = characterIds?.ToList();

        var colonies = await Of(db.EsiPlanetaryColonies, c => ids!.Contains(c.CharacterId)).ToListAsync(ct);
        var layouts  = await Of(db.EsiPlanetaryLayouts,  l => ids!.Contains(l.CharacterId)).ToListAsync(ct);
        var pins     = await Of(db.EsiPlanetaryPins,     p => ids!.Contains(p.CharacterId)).ToListAsync(ct);
        var contents = await Of(db.EsiPlanetaryPinContents, p => ids!.Contains(p.CharacterId)).ToListAsync(ct);
        var routes   = await Of(db.EsiPlanetaryRoutes,   r => ids!.Contains(r.CharacterId)).ToListAsync(ct);
        var links    = await Of(db.EsiPlanetaryLinks,    l => ids!.Contains(l.CharacterId)).ToListAsync(ct);

        // Everyone's rows when no list was given, otherwise only those characters'.
        IQueryable<T> Of<T>(DbSet<T> set, System.Linq.Expressions.Expression<Func<T, bool>> mine) where T : class
            => ids is null ? set.AsNoTracking() : set.AsNoTracking().Where(mine);

        var layoutOf = layouts.ToDictionary(l => (l.CharacterId, l.PlanetId));
        var pinsOf     = pins.ToLookup(p => (p.CharacterId, p.PlanetId));
        var contentsOf = contents.ToLookup(c => (c.CharacterId, c.PlanetId, c.PinId));
        var routesOf   = routes.ToLookup(r => (r.CharacterId, r.PlanetId));
        var linksOf    = links.ToLookup(l => (l.CharacterId, l.PlanetId));

        var result = new List<PiColonyLayout>();
        foreach (var colony in colonies)
        {
            var key = (colony.CharacterId, colony.PlanetId);
            if (!layoutOf.TryGetValue(key, out var layout)) continue;
            result.Add(Build(colony, layout, pinsOf[key], c => contentsOf[(key.CharacterId, key.PlanetId, c)],
                             routesOf[key], linksOf[key]));
        }
        return result;
    }

    /// <summary>One colony's stored layout, or null when none has been read.</summary>
    public static async Task<PiColonyLayout?> LoadOneAsync(AppDbContext db, long characterId, int planetId,
                                                           CancellationToken ct = default)
    {
        var colony = await db.EsiPlanetaryColonies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CharacterId == characterId && c.PlanetId == planetId, ct);
        var layout = await db.EsiPlanetaryLayouts.AsNoTracking()
            .FirstOrDefaultAsync(l => l.CharacterId == characterId && l.PlanetId == planetId, ct);
        if (colony is null || layout is null) return null;

        var pins = await db.EsiPlanetaryPins.AsNoTracking()
            .Where(p => p.CharacterId == characterId && p.PlanetId == planetId).ToListAsync(ct);
        var contents = (await db.EsiPlanetaryPinContents.AsNoTracking()
            .Where(p => p.CharacterId == characterId && p.PlanetId == planetId).ToListAsync(ct))
            .ToLookup(c => c.PinId);
        var routes = await db.EsiPlanetaryRoutes.AsNoTracking()
            .Where(r => r.CharacterId == characterId && r.PlanetId == planetId).ToListAsync(ct);
        var links = await db.EsiPlanetaryLinks.AsNoTracking()
            .Where(l => l.CharacterId == characterId && l.PlanetId == planetId).ToListAsync(ct);

        return Build(colony, layout, pins, pinId => contents[pinId], routes, links);
    }

    private static PiColonyLayout Build(PlanetaryColony colony, PlanetaryLayout layout,
                                        IEnumerable<PlanetaryPin> pins,
                                        Func<long, IEnumerable<PlanetaryPinContent>> contentsOf,
                                        IEnumerable<PlanetaryRoute> routes, IEnumerable<PlanetaryLink> links)
        => new()
        {
            CharacterId   = colony.CharacterId,
            PlanetId      = colony.PlanetId,
            PlanetType    = colony.PlanetType,
            SolarSystemId = colony.SolarSystemId,
            UpgradeLevel  = colony.UpgradeLevel,
            // ⚠️ The layout's own moment, not the colony list's: the list may already be newer
            // than the layout stored, until the next read catches up.
            LastUpdate    = layout.LastUpdate,
            FetchedAt     = layout.FetchedAt,
            Pins = pins.OrderBy(p => p.PinId).Select(p => new PiLayoutPin
            {
                PinId          = p.PinId,
                TypeId         = p.TypeId,
                SchematicId    = p.SchematicId,
                InstallTime    = p.InstallTime,
                ExpiryTime     = p.ExpiryTime,
                LastCycleStart = p.LastCycleStart,
                Contents       = contentsOf(p.PinId).ToDictionary(c => c.TypeId, c => c.Amount),
                Extractor      = p.ExtractorProductTypeId is null && p.ExtractorCycleTime is null
                                 && p.ExtractorQtyPerCycle is null && p.ExtractorHeadCount == 0
                    ? null
                    : new PiLayoutExtractor(p.ExtractorProductTypeId, p.ExtractorCycleTime,
                                            p.ExtractorQtyPerCycle, p.ExtractorHeadCount),
            }).ToList(),
            Routes = routes.OrderBy(r => r.RouteId)
                .Select(r => new PiLayoutRoute(r.RouteId, r.SourcePinId, r.DestinationPinId, r.ContentTypeId, r.Quantity))
                .ToList(),
            Links = links.Select(l => new PiLayoutLink(l.SourcePinId, l.DestinationPinId, l.LinkLevel)).ToList(),
        };

    /// <summary>The rows an ESI layout is stored as.</summary>
    public static (List<PlanetaryPin> Pins, List<PlanetaryPinContent> Contents,
                   List<PlanetaryRoute> Routes, List<PlanetaryLink> Links)
        RowsFrom(long characterId, int planetId, EsiPlanetLayout data)
    {
        var pins = (data.Pins ?? []).DistinctBy(p => p.PinId).Select(p => new PlanetaryPin
        {
            CharacterId            = characterId,
            PlanetId               = planetId,
            PinId                  = p.PinId,
            TypeId                 = p.TypeId,
            // factory_details is where a processor's schematic normally is; the pin's own field
            // where ESI gives that instead.
            SchematicId            = p.FactoryDetails?.SchematicId ?? p.SchematicId,
            InstallTime            = p.InstallTime,
            ExpiryTime             = p.ExpiryTime,
            LastCycleStart         = p.LastCycleStart,
            Latitude               = p.Latitude,
            Longitude              = p.Longitude,
            ExtractorProductTypeId = p.ExtractorDetails?.ProductTypeId,
            ExtractorCycleTime     = p.ExtractorDetails?.CycleTime,
            ExtractorQtyPerCycle   = p.ExtractorDetails?.QtyPerCycle,
            ExtractorHeadRadius    = p.ExtractorDetails?.HeadRadius,
            ExtractorHeadCount     = p.ExtractorDetails?.Heads?.Count ?? 0,
        }).ToList();

        var contents = (data.Pins ?? []).DistinctBy(p => p.PinId)
            .SelectMany(p => (p.Contents ?? [])
                .GroupBy(c => c.TypeId)
                .Select(g => new PlanetaryPinContent
                {
                    CharacterId = characterId,
                    PlanetId    = planetId,
                    PinId       = p.PinId,
                    TypeId      = g.Key,
                    Amount      = g.Sum(c => c.Amount),
                }))
            .ToList();

        var routes = (data.Routes ?? []).DistinctBy(r => r.RouteId).Select(r => new PlanetaryRoute
        {
            CharacterId      = characterId,
            PlanetId         = planetId,
            RouteId          = r.RouteId,
            SourcePinId      = r.SourcePinId,
            DestinationPinId = r.DestinationPinId,
            ContentTypeId    = r.ContentTypeId,
            Quantity         = r.Quantity,
            Waypoints        = string.Join(',', r.Waypoints ?? []),
        }).ToList();

        var links = (data.Links ?? []).DistinctBy(l => (l.SourcePinId, l.DestinationPinId)).Select(l => new PlanetaryLink
        {
            CharacterId      = characterId,
            PlanetId         = planetId,
            SourcePinId      = l.SourcePinId,
            DestinationPinId = l.DestinationPinId,
            LinkLevel        = l.LinkLevel,
        }).ToList();

        return (pins, contents, routes, links);
    }

    /// <summary>
    /// Replaces one colony's stored layout with what ESI just returned, and records what moved on
    /// or off it since the layout it replaces (see <see cref="Movements"/>).
    ///
    /// <para>⚠️ One transaction. The old rows are deleted and the new ones written inside it, so a
    /// reader sees the old colony or the new one and never a colony half gone — deleting and then
    /// inserting outside one leaves a window with no layout at all.</para>
    /// </summary>
    /// <param name="sd">The static data movements are simulated with; null skips movements.</param>
    public static async Task ReplaceAsync(AppDbContext db, PlanetaryColony colony, EsiPlanetLayout data,
                                          PiStaticData? sd, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        var charId   = colony.CharacterId;
        var planetId = colony.PlanetId;

        var older = sd is null ? null : await LoadOneAsync(db, charId, planetId, ct);
        var (pins, contents, routes, links) = RowsFrom(charId, planetId, data);

        var movements = new List<PiColonyMovement>();
        if (older is not null && sd is not null && colony.LastUpdate > older.LastUpdate)
        {
            var newer = Build(colony,
                new PlanetaryLayout { CharacterId = charId, PlanetId = planetId, LastUpdate = colony.LastUpdate, FetchedAt = fetchedAt },
                pins, pinId => contents.Where(c => c.PinId == pinId), routes, links);
            movements = Movements(older, newer, sd);
        }

        // What movement windows to drop, read before the transaction: only ids are needed.
        var keptWindows = await db.PiColonyMovements.AsNoTracking()
            .Where(m => m.CharacterId == charId && m.PlanetId == planetId)
            .Select(m => new { m.Id, m.ToUpdate })
            .ToListAsync(ct);
        var keep = keptWindows.Select(m => m.ToUpdate).Distinct().OrderByDescending(t => t)
            .Take(MovementWindowsKept - (movements.Count > 0 ? 1 : 0))
            .ToHashSet();
        var dropIds = keptWindows.Where(m => !keep.Contains(m.ToUpdate)).Select(m => m.Id).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await DeleteRowsAsync(db, charId, planetId, ct);
        if (dropIds.Count > 0)
            await db.PiColonyMovements.Where(m => dropIds.Contains(m.Id)).ExecuteDeleteAsync(ct);

        db.EsiPlanetaryLayouts.Add(new PlanetaryLayout
        {
            CharacterId = charId,
            PlanetId    = planetId,
            LastUpdate  = colony.LastUpdate,
            FetchedAt   = fetchedAt,
        });
        db.EsiPlanetaryPins.AddRange(pins);
        db.EsiPlanetaryPinContents.AddRange(contents);
        db.EsiPlanetaryRoutes.AddRange(routes);
        db.EsiPlanetaryLinks.AddRange(links);
        db.PiColonyMovements.AddRange(movements);
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>Removes a colony's stored layout — one given up in the game — in one transaction.
    /// Its movements and learned tax rate stay: the planet's rate is still worth knowing.</summary>
    public static async Task DeleteAsync(AppDbContext db, long characterId, int planetId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await DeleteRowsAsync(db, characterId, planetId, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task DeleteRowsAsync(AppDbContext db, long charId, int planetId, CancellationToken ct)
    {
        await db.EsiPlanetaryLayouts.Where(x => x.CharacterId == charId && x.PlanetId == planetId).ExecuteDeleteAsync(ct);
        await db.EsiPlanetaryPins.Where(x => x.CharacterId == charId && x.PlanetId == planetId).ExecuteDeleteAsync(ct);
        await db.EsiPlanetaryPinContents.Where(x => x.CharacterId == charId && x.PlanetId == planetId).ExecuteDeleteAsync(ct);
        await db.EsiPlanetaryRoutes.Where(x => x.CharacterId == charId && x.PlanetId == planetId).ExecuteDeleteAsync(ct);
        await db.EsiPlanetaryLinks.Where(x => x.CharacterId == charId && x.PlanetId == planetId).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// What was taken off a colony and what was brought onto it between two of its snapshots.
    ///
    /// <para>The older snapshot is simulated forward to the newer one's moment, which is what the
    /// colony would hold had nobody touched it; whatever the newer snapshot holds less of was
    /// taken off (Removed), and whatever it holds more of was brought in (Added). Totals across the
    /// whole colony — storage, launchpads, command center and processor buffers.</para>
    ///
    /// <para>⚠️ An estimate, and only as good as the simulation: production that ran differently
    /// from the forecast, a layout rebuilt between the two snapshots, or goods destroyed with a
    /// demolished pin all read as movement. Tax learning guards against the worst of it.</para>
    /// </summary>
    public static List<PiColonyMovement> Movements(PiColonyLayout older, PiColonyLayout newer, PiStaticData sd)
    {
        var forecast = PiEngine.Forecast(older, sd, newer.LastUpdate, horizon: TimeSpan.Zero);

        var expected = new Dictionary<int, long>();
        foreach (var s in forecast.Storage)
            foreach (var (type, amount) in s.ContentsAt) expected[type] = expected.GetValueOrDefault(type) + amount;
        foreach (var f in forecast.Factories)
            foreach (var (type, amount) in f.BufferAt) expected[type] = expected.GetValueOrDefault(type) + amount;

        var actual = new Dictionary<int, long>();
        foreach (var p in newer.Pins)
            foreach (var (type, amount) in p.Contents) actual[type] = actual.GetValueOrDefault(type) + amount;

        return expected.Keys.Union(actual.Keys).Order()
            .Select(type =>
            {
                var diff = actual.GetValueOrDefault(type) - expected.GetValueOrDefault(type);
                return new PiColonyMovement
                {
                    CharacterId = newer.CharacterId,
                    PlanetId    = newer.PlanetId,
                    FromUpdate  = older.LastUpdate,
                    ToUpdate    = newer.LastUpdate,
                    TypeId      = type,
                    Removed     = diff < 0 ? -diff : 0,
                    Added       = diff > 0 ? diff : 0,
                };
            })
            .Where(m => m.Removed > 0 || m.Added > 0)
            .ToList();
    }
}
