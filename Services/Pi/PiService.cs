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
    PiEconomics Economics);

/// <summary>A colony slot as the colony list has it, read or not.</summary>
public sealed record PiColonySlot(int PlanetId, string PlanetType, int SolarSystemId, int UpgradeLevel,
                                  int MaxUpgradeLevel, bool LayoutRead)
{
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
public sealed class PiService(IDbContextFactory<AppDbContext> dbFactory, PiTaxService tax)
{
    private static readonly TimeSpan StaticDataLife = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _staticGate = new(1, 1);
    private PiStaticData? _static;
    private DateTimeOffset _staticAt;

    public PiTaxService Tax => tax;

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

        return ids
            .Select(id => new PiCharacterStatus(id, names.GetValueOrDefault(id, id.ToString()), skills[id],
                colonies.Where(c => c.CharacterId == id).OrderBy(c => c.PlanetId)
                    .Select(c => new PiColonySlot(c.PlanetId, c.PlanetType, c.SolarSystemId, c.UpgradeLevel,
                                                  skills[id].MaxUpgradeLevel, read.Contains((id, c.PlanetId))))
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

        var planetIds = layouts.Select(l => (long)l.PlanetId).Distinct().ToList();
        var planetNames = await db.SdeCelestials.AsNoTracking()
            .Where(c => planetIds.Contains(c.ItemId))
            .ToDictionaryAsync(c => c.ItemId, c => c.Name, ct).ConfigureAwait(false);
        var systemIds = layouts.Select(l => l.SolarSystemId).Distinct().ToList();
        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => systemIds.Contains(s.SolarSystemId))
            .ToDictionaryAsync(s => s.SolarSystemId, s => (s.Name, s.Security), ct).ConfigureAwait(false);

        var forecasts = layouts
            .Select(l => PiEngine.Forecast(l, sd, at, rates.GetValueOrDefault(l.PlanetId)))
            .ToList();

        var typeIds = forecasts.SelectMany(f => f.Flows.Select(x => x.TypeId)).Distinct().ToList();
        var prices  = await TypeValuation.PricesAsync(db, typeIds, ct).ConfigureAwait(false);

        return forecasts
            .Select(f =>
            {
                var l = f.Layout;
                var (systemName, security) = systems.GetValueOrDefault(l.SolarSystemId, ("", 0));
                return new PiColonyStatus(
                    l.CharacterId, names.GetValueOrDefault(l.CharacterId, l.CharacterId.ToString()),
                    l.PlanetId, planetNames.GetValueOrDefault(l.PlanetId, ""),
                    l.SolarSystemId, systemName, security,
                    skills.TryGetValue(l.CharacterId, out var s) ? s.MaxUpgradeLevel : 0,
                    f, PiEconomics.For(f, prices));
            })
            .ToList();
    }

    private static async Task<Dictionary<long, string>> NamesAsync(AppDbContext db, HashSet<long> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        return await db.Characters.AsNoTracking()
            .Where(c => list.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct).ConfigureAwait(false);
    }
}
