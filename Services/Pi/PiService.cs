using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Pi;

/// <summary>
/// A colony's money per day at its steady rate.
///
/// <para>The user's model, one formula for both kinds: output value − input cost − import charges
/// − export charges. On an extractor planet the raw material is its own, so nothing is imported
/// and that reduces to output value − export charges. Inputs are valued at market, the same as
/// output (<see cref="TypeValuation"/>).</para>
/// </summary>
public sealed record PiEconomics(double OutputValuePerDay, double InputCostPerDay,
                                 double ExportChargesPerDay, double ImportChargesPerDay)
{
    public double ProfitPerDay => OutputValuePerDay - InputCostPerDay - ExportChargesPerDay - ImportChargesPerDay;

    public static PiEconomics For(PiColonyForecast forecast, IReadOnlyDictionary<int, double> prices)
        => new(
            forecast.Flows.Sum(f => f.ExportedPerDay * prices.GetValueOrDefault(f.TypeId)),
            forecast.Flows.Sum(f => f.ImportedPerDay * prices.GetValueOrDefault(f.TypeId)),
            forecast.ExportChargesPerDay,
            forecast.ImportChargesPerDay);
}

/// <summary>Output thrown away, valued at market.</summary>
public sealed record PiValuedLoss(PiLoss Loss, double Value);

/// <summary>Output idle factories did not make, valued at market.</summary>
public sealed record PiValuedIdle(PiIdle Idle, double Value);

/// <summary>
/// A colony's money over its period (<see cref="PiPeriodForecast"/>): what it could make against
/// what it will, by the same rules and prices as <see cref="PiEconomics"/> — output value − input
/// cost − import charges − export charges.
///
/// <para><b>Potential</b> is the period's steady flows × its length. <b>Forecast</b> is the
/// simulation: what reaches storage of the types the colony exports, less the brought-in input
/// its factories used (at market plus the import charge) and the export charge on what reached
/// storage. Destroyed and idle are at market, gross: see <see cref="PiPeriodForecast"/> for how
/// they add up to the difference, roughly.</para>
/// </summary>
public sealed class PiPeriodEconomics
{
    public required PiPeriodForecast Period { get; init; }

    public long   PotentialUnits       { get; init; }
    public double PotentialOutputValue { get; init; }
    public double PotentialInputCost   { get; init; }
    public double PotentialCharges     { get; init; }
    public double PotentialProfit => PotentialOutputValue - PotentialInputCost - PotentialCharges;

    public long   ForecastUnits       { get; init; }
    public double ForecastOutputValue { get; init; }
    public double ForecastInputCost   { get; init; }
    public double ForecastCharges     { get; init; }
    public double ForecastProfit => ForecastOutputValue - ForecastInputCost - ForecastCharges;

    /// <summary>Potential less forecast.</summary>
    public double Shortfall => PotentialProfit - ForecastProfit;

    /// <summary>Forecast as a share of potential; null when there is no profit to measure against.</summary>
    public double? Efficiency => PotentialProfit > 0 ? ForecastProfit / PotentialProfit : null;

    /// <summary>Products thrown away, and raw material overflowing storage, apart.</summary>
    public IReadOnlyList<PiValuedLoss> Destroyed   { get; init; } = [];
    public IReadOnlyList<PiValuedLoss> RawOverflow { get; init; } = [];
    /// <summary>Schematics whose factories waited beyond what the potential counts on.</summary>
    public IReadOnlyList<PiValuedIdle> Idle        { get; init; } = [];

    public double DestroyedValue   => Destroyed.Sum(d => d.Value);
    public double RawOverflowValue => RawOverflow.Sum(d => d.Value);
    /// <summary>⚠️ Final products only: an idle lower tier also idles the tier above it, and
    /// counting both would count one loss twice.</summary>
    public double IdleValue => Idle.Where(i => i.Idle.IsFinal).Sum(i => i.Value);

    /// <summary>The period's units, per exported type: potential, then forecast.</summary>
    public IReadOnlyDictionary<int, (double Potential, long Forecast)> ByType { get; init; }
        = new Dictionary<int, (double, long)>();

    public static PiPeriodEconomics For(PiColonyForecast forecast, IReadOnlyDictionary<int, double> prices)
    {
        var p    = forecast.Period;
        var days = p.Days;
        var rate = forecast.Rate?.Rate;
        var tier = p.Flows.Where(f => f.Tier is not null).ToDictionary(f => f.TypeId, f => f.Tier!.Value);

        double Price(int type) => prices.GetValueOrDefault(type);
        double Export(int type) => rate is { } r && tier.TryGetValue(type, out var t) ? PiCharges.ExportPerUnit(t, r) : 0;
        double Import(int type) => rate is { } r && tier.TryGetValue(type, out var t) ? PiCharges.ImportPerUnit(t, r) : 0;

        var output = p.OutputUnits;
        var potentialByType = p.Flows.Where(f => p.ExportTypes.Contains(f.TypeId))
            .ToDictionary(f => f.TypeId, f => f.ExportedPerDay * days);

        return new PiPeriodEconomics
        {
            Period               = p,
            PotentialUnits       = (long)Math.Round(potentialByType.Values.Sum()),
            PotentialOutputValue = p.Flows.Sum(f => f.ExportedPerDay * days * Price(f.TypeId)),
            PotentialInputCost   = p.Flows.Sum(f => f.ImportedPerDay * days * Price(f.TypeId)),
            PotentialCharges     = p.Flows.Sum(f => f.ExportedPerDay * days * Export(f.TypeId)
                                                  + f.ImportedPerDay * days * Import(f.TypeId)),

            ForecastUnits       = output.Values.Sum(),
            ForecastOutputValue = output.Sum(kv => kv.Value * Price(kv.Key)),
            ForecastInputCost   = p.InputsConsumed.Sum(kv => kv.Value * Price(kv.Key)),
            ForecastCharges     = output.Sum(kv => kv.Value * Export(kv.Key))
                                + p.InputsConsumed.Sum(kv => kv.Value * Import(kv.Key)),

            Destroyed   = p.Losses.Where(l => l.Kind == PiLossKind.Product)
                .Select(l => new PiValuedLoss(l, l.Units * Price(l.TypeId))).ToList(),
            RawOverflow = p.Losses.Where(l => l.Kind == PiLossKind.Raw)
                .Select(l => new PiValuedLoss(l, l.Units * Price(l.TypeId))).ToList(),
            Idle        = p.Idle.Where(i => i.MissedUnits > 0)
                .Select(i => new PiValuedIdle(i, i.MissedUnits * Price(i.OutputTypeId))).ToList(),

            ByType = potentialByType.Keys.Union(output.Keys).Order()
                .ToDictionary(t => t, t => (potentialByType.GetValueOrDefault(t), output.GetValueOrDefault(t))),
        };
    }
}

/// <summary>One colony with everything the PI tool shows about it.</summary>
public sealed record PiColonyStatus(
    long CharacterId,
    string CharacterName,
    int PlanetId,
    // English, from the SDE; the screen names it in the interface language.
    string PlanetName,
    int SolarSystemId,
    string SystemName,
    double Security,
    // What the character's Command Center Upgrades allows.
    int MaxUpgradeLevel,
    PiColonyForecast Forecast,
    PiEconomics Economics)
{
    /// <summary>The planet's own SDE type ("Planet (Barren)"), for its localized type name; 0
    /// when the SDE has not been read.</summary>
    public int PlanetTypeId { get; init; }

    /// <summary>That type's English name, the fallback for <see cref="PiNames.PlanetType"/>.</summary>
    public string PlanetTypeName { get; init; } = "";

    /// <summary>The system's region and its English name, the fallback for the region shown.</summary>
    public int    RegionId   { get; init; }
    public string RegionName { get; init; } = "";

    /// <summary>Potential against forecast over the period ahead; null on a status built without
    /// prices.</summary>
    public PiPeriodEconomics? Period { get; init; }
}

/// <summary>A colony slot as the colony list has it, read or not.</summary>
public sealed record PiColonySlot(int PlanetId, string PlanetType, int SolarSystemId, int UpgradeLevel,
                                  int MaxUpgradeLevel, bool LayoutRead)
{
    /// <summary>English, from the SDE; the screen names it in the interface language
    /// (<see cref="PiNames.Planet"/>).</summary>
    public string PlanetName { get; init; } = "";
    public string SystemName { get; init; } = "";

    /// <summary>Command center levels the character could still add.</summary>
    public int UpgradeHeadroom => Math.Max(0, MaxUpgradeLevel - UpgradeLevel);
}

/// <summary>One PI character: skills, colonies used against colonies allowed, each colony's
/// command center against what the skill allows.</summary>
public sealed record PiCharacterStatus(long CharacterId, string Name, PiSkills Skills,
                                       IReadOnlyList<PiColonySlot> Colonies)
{
    public int ColoniesUsed    => Colonies.Count;
    public int ColoniesAllowed => Skills.ColoniesAllowed;
    /// <summary>Colonies the character could still set up.</summary>
    public int ColoniesFree    => Math.Max(0, ColoniesAllowed - ColoniesUsed);
}

/// <summary>
/// The PI tools' one door to the data: who does PI, their skills and colonies, and every colony
/// forecast to a moment with its charges and money. Part of the app's services; the screens,
/// alerts and worklist tasks read from here and never assemble it themselves.
/// </summary>
public sealed class PiService(IDbContextFactory<AppDbContext> dbFactory, PiTaxService tax, PiSettings settings)
{
    private static readonly TimeSpan StaticDataLife = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _staticGate = new(1, 1);
    private PiStaticData? _static;
    private DateTimeOffset _staticAt;

    public PiTaxService Tax => tax;

    /// <summary>The lead times and limits the PI alerts and tasks judge colonies by.</summary>
    public PiSettings Settings => settings;

    /// <summary>Which characters do PI. See <see cref="PiCharacters"/>.</summary>
    public async Task<HashSet<long>> PiCharacterIdsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await PiCharacters.IdsAsync(db, ct).ConfigureAwait(false);
    }

    /// <summary>The SDE's PI data, read at most every ten minutes — it changes only with an SDE
    /// import.</summary>
    public async Task<PiStaticData> StaticDataAsync(CancellationToken ct = default)
    {
        await _staticGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_static is not null && DateTimeOffset.UtcNow - _staticAt < StaticDataLife) return _static;
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            _static   = await PiStaticDataLoader.LoadAsync(db, ct).ConfigureAwait(false);
            _staticAt = DateTimeOffset.UtcNow;
            return _static;
        }
        finally { _staticGate.Release(); }
    }

    /// <summary>Every PI character with skills and colony slots, by name.</summary>
    public async Task<IReadOnlyList<PiCharacterStatus>> CharactersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = await PiCharacters.IdsAsync(db, ct).ConfigureAwait(false);
        if (ids.Count == 0) return [];

        var idList   = ids.ToList();
        var names    = await NamesAsync(db, ids, ct).ConfigureAwait(false);
        var skills   = await PiSkills.LoadAsync(db, ids, ct).ConfigureAwait(false);
        var colonies = await db.EsiPlanetaryColonies.AsNoTracking()
            .Where(c => idList.Contains(c.CharacterId)).ToListAsync(ct).ConfigureAwait(false);
        var read     = (await db.EsiPlanetaryLayouts.AsNoTracking()
                .Where(l => idList.Contains(l.CharacterId))
                .Select(l => new { l.CharacterId, l.PlanetId })
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(l => (l.CharacterId, l.PlanetId)).ToHashSet();
        var (planetNames, systems) = await PlaceNamesAsync(db, colonies.Select(c => (c.PlanetId, c.SolarSystemId)), ct)
            .ConfigureAwait(false);

        return ids
            .Select(id => new PiCharacterStatus(id, names.GetValueOrDefault(id, id.ToString()), skills[id],
                colonies.Where(c => c.CharacterId == id).OrderBy(c => c.PlanetId)
                    .Select(c => new PiColonySlot(c.PlanetId, c.PlanetType, c.SolarSystemId, c.UpgradeLevel,
                                                  skills[id].MaxUpgradeLevel, read.Contains((id, c.PlanetId)))
                    {
                        PlanetName = planetNames.TryGetValue(c.PlanetId, out var p) ? p.Name : "",
                        SystemName = systems.TryGetValue(c.SolarSystemId, out var sys) ? sys.Name : "",
                    })
                    .ToList()))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every PI character's colonies with a layout read, forecast to <paramref name="at"/>, charged
    /// at each planet's rate, and priced.
    /// </summary>
    public async Task<IReadOnlyList<PiColonyStatus>> ColoniesAsync(DateTimeOffset at, CancellationToken ct = default)
    {
        var sd = await StaticDataAsync(ct).ConfigureAwait(false);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = await PiCharacters.IdsAsync(db, ct).ConfigureAwait(false);
        if (ids.Count == 0) return [];

        var layouts = await PiLayoutStore.LoadAsync(db, ids, ct).ConfigureAwait(false);
        if (layouts.Count == 0) return [];

        var names  = await NamesAsync(db, ids, ct).ConfigureAwait(false);
        var skills = await PiSkills.LoadAsync(db, ids, ct).ConfigureAwait(false);
        var rates  = await tax.RatesAsync(db, layouts.Select(l => (l.PlanetId, l.SolarSystemId)), ct).ConfigureAwait(false);

        var (planetNames, systems) = await PlaceNamesAsync(db, layouts.Select(l => (l.PlanetId, l.SolarSystemId)), ct)
            .ConfigureAwait(false);
        var planetTypeIds = planetNames.Values.Select(p => p.TypeId).Where(t => t > 0).Distinct().ToList();
        var planetTypeNames = await db.SdeTypes.AsNoTracking()
            .Where(t => planetTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct).ConfigureAwait(false);

        var forecasts = layouts
            .Select(l => PiEngine.Forecast(l, sd, at, rates.GetValueOrDefault(l.PlanetId)))
            .ToList();

        var typeIds = forecasts
            .SelectMany(f => f.Flows.Select(x => x.TypeId)
                .Concat(f.Period.Flows.Select(x => x.TypeId))
                .Concat(f.Period.Losses.Select(l => l.TypeId))
                .Concat(f.Period.Idle.Select(i => i.OutputTypeId)))
            .Distinct().ToList();
        var prices  = await TypeValuation.PricesAsync(db, typeIds, ct).ConfigureAwait(false);

        return forecasts
            .Select(f =>
            {
                var l = f.Layout;
                var (systemName, security, regionId, regionName) = systems.GetValueOrDefault(l.SolarSystemId, ("", 0, 0, ""));
                var (planetName, planetTypeId) = planetNames.GetValueOrDefault(l.PlanetId, ("", 0));
                return new PiColonyStatus(
                    l.CharacterId, names.GetValueOrDefault(l.CharacterId, l.CharacterId.ToString()),
                    l.PlanetId, planetName,
                    l.SolarSystemId, systemName, security,
                    skills.TryGetValue(l.CharacterId, out var s) ? s.MaxUpgradeLevel : 0,
                    f, PiEconomics.For(f, prices))
                {
                    PlanetTypeId   = planetTypeId,
                    PlanetTypeName = planetTypeNames.GetValueOrDefault(planetTypeId, ""),
                    RegionId       = regionId,
                    RegionName     = regionName,
                    Period         = PiPeriodEconomics.For(f, prices),
                };
            })
            .ToList();
    }

    /// <summary>English names of item types, for the screens and tasks to localize
    /// (<see cref="PiNames.Type"/>).</summary>
    public async Task<Dictionary<int, string>> TypeNamesAsync(IEnumerable<int> typeIds, CancellationToken ct = default)
    {
        var ids = typeIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.SdeTypes.AsNoTracking()
            .Where(t => ids.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct).ConfigureAwait(false);
    }

    /// <summary>Planets' English names and SDE types; systems' English names, security and region
    /// (with the region's English name — wormhole space has its own regions, "A-R00001" and the like).</summary>
    private static async Task<(Dictionary<int, (string Name, int TypeId)> Planets,
                               Dictionary<int, (string Name, double Security, int RegionId, string RegionName)> Systems)>
        PlaceNamesAsync(AppDbContext db, IEnumerable<(int PlanetId, int SolarSystemId)> places, CancellationToken ct)
    {
        var list      = places.ToList();
        var planetIds = list.Select(p => (long)p.PlanetId).Distinct().ToList();
        var systemIds = list.Select(p => p.SolarSystemId).Distinct().ToList();

        var planets = (await db.SdeCelestials.AsNoTracking()
                .Where(c => planetIds.Contains(c.ItemId))
                .Select(c => new { c.ItemId, c.Name, c.TypeId })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(c => (int)c.ItemId, c => (c.Name, c.TypeId));
        var rows = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => systemIds.Contains(s.SolarSystemId))
            .Select(s => new { s.SolarSystemId, s.Name, s.Security, s.RegionId })
            .ToListAsync(ct).ConfigureAwait(false);
        var regionIds = rows.Select(s => s.RegionId).Distinct().ToList();
        var regions = await db.SdeRegions.AsNoTracking()
            .Where(r => regionIds.Contains(r.RegionId))
            .ToDictionaryAsync(r => r.RegionId, r => r.Name, ct).ConfigureAwait(false);
        var systems = rows.ToDictionary(s => s.SolarSystemId,
            s => (s.Name, s.Security, s.RegionId, regions.GetValueOrDefault(s.RegionId, "")));
        return (planets, systems);
    }

    private static async Task<Dictionary<long, string>> NamesAsync(AppDbContext db, HashSet<long> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        return await db.Characters.AsNoTracking()
            .Where(c => list.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct).ConfigureAwait(false);
    }
}
