using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services.Fitting;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// How much a pilot's ship actually carries: the hull's cargo hold with what is fitted to it and
/// the pilot's own skills applied, through the fitting tool's engine.
///
/// <para>⚠️ The SDE's capacity is the bare hull's hold. A Charon is 465,000 m³ there and over a
/// million fitted with expanders and flown with Caldari Freighter trained — so a loop planned on
/// the bare figure made extra trips the ship did not need. The fit is read from the asset list,
/// which can be up to an hour behind; a fit changes far less often than a cargo does.</para>
///
/// <para>The engine's data loads once, the first time a hold is asked for, and is kept: it is the
/// game's static data.</para>
/// </summary>
public static class HaulHold
{
    private static Task<(DogmaData Data, FittingCatalog Catalog)>? _loading;
    private static readonly object Gate = new();

    /// <summary>
    /// The hold of <paramref name="hullTypeId"/> flown by <paramref name="characterId"/>, fitted
    /// as the ship <paramref name="shipItemId"/> is in the asset list — or with nothing fitted when
    /// that is null or the ship is not listed (a hull chosen for the lane, rather than one owned).
    /// Null when the engine cannot answer, and the caller falls back to the bare hull.
    /// </summary>
    public static async Task<double?> FittedAsync(
        IDbContextFactory<AppDbContext> dbFactory, long characterId, int hullTypeId, long? shipItemId,
        CancellationToken ct = default)
    {
        if (hullTypeId <= 0) return null;
        try
        {
            Task<(DogmaData, FittingCatalog)> loading;
            lock (Gate) loading = _loading ??= LoadAsync(dbFactory);
            var (data, catalog) = await loading;

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var fitted = new List<EsiFittingItem>();
            if (shipItemId is { } ship)
            {
                var contents = await db.EsiAssets.AsNoTracking()
                    .Where(a => a.LocationId == ship)
                    .Select(a => new { a.TypeId, a.LocationFlag, a.Quantity })
                    .ToListAsync(ct);
                await data.LoadTypesAsync(contents.Select(c => c.TypeId).Distinct(), ct);

                // The fit only — not the cargo, and not a module's loaded charge, which the fitting
                // tool would otherwise take for a module of its own.
                fitted.AddRange(contents
                    .Where(c => ExistingShips.IsFitted(c.LocationFlag)
                                && !(data.TryType(c.TypeId, out var t) && t.CategoryId == DogmaData.CategoryCharge))
                    .Select(c => new EsiFittingItem(c.TypeId, c.LocationFlag, Math.Max(1, c.Quantity))));
            }

            var fit = await EftFormat.FromEsiAsync(new EsiFittingData(0, "", "", hullTypeId, fitted), data, catalog, ct);

            var levels = characterId > 0
                ? await db.EsiSkills.AsNoTracking().Where(s => s.CharacterId == characterId)
                    .ToDictionaryAsync(s => s.SkillId, s => s.ActiveSkillLevel, ct)
                : [];

            var engine = await DogmaEngine.CreateAsync(data, fit, new SkillSet("", levels), ct: ct);
            var hold   = engine.Value(engine.Ship, DogmaData.AttrCapacity);
            return hold > 0 ? hold : null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // A ship the engine cannot assemble is planned on its bare hull, as before.
            return null;
        }
    }

    private static async Task<(DogmaData, FittingCatalog)> LoadAsync(IDbContextFactory<AppDbContext> dbFactory)
    {
        try
        {
            var data    = await DogmaData.LoadAsync(dbFactory);
            var catalog = await FittingCatalog.LoadAsync(data, dbFactory);
            return (data, catalog);
        }
        catch
        {
            // Not kept when it fails, so the next refresh tries again.
            lock (Gate) _loading = null;
            throw;
        }
    }
}
