using System.Text.Json;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services.Fitting;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>A saved fit that can be set as a structure type's fit: EVE Console's own, or a
/// character's fitting in the game.</summary>
public sealed record CorpFitChoice(string Source, long FitId, long OwnerId, string OwnerName, string Name, int ShipTypeId)
{
    public const string App  = "app";
    public const string Game = "game";

    /// <summary>The Config tab's "no fit" entry: the structure type is not checked.</summary>
    public static readonly CorpFitChoice None = new("", 0, 0, "", "", 0);

    /// <summary>The fit set for a type that can no longer be found where it was kept — listed so
    /// the Config tab can show it is still the one checked (against its copy).</summary>
    public bool IsCopy { get; init; }

    /// <summary>"Name — where it is kept", as the Config tab's fit list shows it.</summary>
    public string Display => Source.Length == 0 ? CorpText.CsFitNone
        : string.Format(IsCopy ? CorpText.CsFitChoiceCopy : CorpText.CsFitChoice,
            Name.Length > 0 ? Name : CorpText.CsFitNoName, Source == App ? CorpText.CsFitInApp : OwnerName);
}

/// <summary>The fit a structure type is checked against, as found when it was read.</summary>
/// <param name="FromCopy">The fit itself could not be found; Items is the copy kept when it last was.</param>
public sealed record CorpRequiredFit(
    int StructureTypeId, string Name, string Source, long FitId, long OwnerId, string OwnerName,
    IReadOnlyList<EsiFittingItem> Items, bool FromCopy, DateTimeOffset CopiedAt);

/// <summary>
/// The fits a corporation's structure types should carry (Corp Structures → Config): which saved
/// fits can be chosen, setting one, and reading them back as the items to check against.
/// </summary>
public static class CorpStructureFits
{
    private static readonly JsonSerializerOptions Json = new();
    private static Task<DogmaData>? _dogma;
    private static readonly Lock DogmaLock = new();

    /// <summary>The game data an EVE Console fit is read with, loaded once and only when one is
    /// needed — most fits are the game's own, which need none.</summary>
    private static Task<DogmaData> DogmaAsync(IDbContextFactory<AppDbContext> dbFactory)
    {
        lock (DogmaLock)
        {
            if (_dogma is null || _dogma.IsFaulted) _dogma = DogmaData.LoadAsync(dbFactory);
            return _dogma;
        }
    }

    /// <summary>Every saved fit for any of <paramref name="structureTypeIds"/>: EVE Console's,
    /// then each character's in the game.</summary>
    public static async Task<List<CorpFitChoice>> ChoicesAsync(
        IDbContextFactory<AppDbContext> dbFactory, IReadOnlyCollection<int> structureTypeIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = structureTypeIds.ToList();
        var app = await db.SavedFits.AsNoTracking()
            .Where(f => ids.Contains(f.ShipTypeId))
            .Select(f => new { f.Id, f.Name, f.ShipTypeId })
            .ToListAsync(ct);
        var game = await db.EsiFittings.AsNoTracking()
            .Where(f => ids.Contains(f.ShipTypeId))
            .Select(f => new { f.FittingId, f.CharacterId, f.Name, f.ShipTypeId })
            .ToListAsync(ct);
        var names = await db.Characters.AsNoTracking()
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        return
        [
            .. app.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                  .Select(f => new CorpFitChoice(CorpFitChoice.App, f.Id, 0, "", f.Name, f.ShipTypeId)),
            .. game.OrderBy(f => names.GetValueOrDefault(f.CharacterId) ?? "", StringComparer.CurrentCultureIgnoreCase)
                   .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                   .Select(f => new CorpFitChoice(CorpFitChoice.Game, f.FittingId, f.CharacterId,
                       names.GetValueOrDefault(f.CharacterId) ?? f.CharacterId.ToString(), f.Name, f.ShipTypeId)),
        ];
    }

    /// <summary>Sets <paramref name="choice"/> as the fit of <paramref name="corpId"/>'s structures
    /// of <paramref name="structureTypeId"/>, or clears it when null.</summary>
    public static async Task SetAsync(IDbContextFactory<AppDbContext> dbFactory, long corpId, int structureTypeId,
        CorpFitChoice? choice, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.CorpStructureFits.FirstOrDefaultAsync(
            f => f.CorporationId == corpId && f.StructureTypeId == structureTypeId, ct);
        if (choice is null)
        {
            if (row is not null) { db.CorpStructureFits.Remove(row); await db.SaveChangesAsync(ct); }
            CorpStructureAlerts.Forget();
            return;
        }

        var items = choice.Source == CorpFitChoice.App
            ? await AppItemsAsync(dbFactory, db, choice.FitId, ct)
            : await GameItemsAsync(db, choice.FitId, ct);
        if (row is null)
        {
            row = new CorpStructureFit { CorporationId = corpId, StructureTypeId = structureTypeId };
            db.CorpStructureFits.Add(row);
        }
        row.FitSource  = choice.Source;
        row.FitId      = choice.FitId;
        row.FitOwnerId = choice.OwnerId;
        row.FitName    = choice.Name;
        row.Items      = JsonSerializer.Serialize(items ?? [], Json);
        row.ItemsAt    = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        CorpStructureAlerts.Forget();   // after the save: the next check reads the new fit
    }

    /// <summary>
    /// The fit each of <paramref name="corpId"/>'s structure types is checked against, by type.
    /// Each is read from where it is kept; one no longer found there falls back to its copy, and
    /// a copy that has gone stale against the fit is brought up to date on the way.
    /// </summary>
    public static async Task<Dictionary<int, CorpRequiredFit>> RequiredAsync(
        IDbContextFactory<AppDbContext> dbFactory, long corpId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.CorpStructureFits.Where(f => f.CorporationId == corpId).ToListAsync(ct);
        var result = new Dictionary<int, CorpRequiredFit>();
        if (rows.Count == 0) return result;

        var names = await db.Characters.AsNoTracking()
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var changed = false;

        foreach (var row in rows)
        {
            List<EsiFittingItem>? items = null;
            var name = row.FitName;

            if (row.FitSource == CorpFitChoice.Game)
            {
                // By id, else the same owner's fitting of that name and hull — saving a fitting in
                // the game again gives it a new id. The newest when there are several.
                var fitting = await db.EsiFittings.AsNoTracking()
                                  .FirstOrDefaultAsync(f => f.FittingId == row.FitId, ct)
                              ?? (await db.EsiFittings.AsNoTracking()
                                  .Where(f => f.CharacterId == row.FitOwnerId && f.ShipTypeId == row.StructureTypeId && f.Name == row.FitName)
                                  .ToListAsync(ct)).MaxBy(f => f.FittingId);
                if (fitting is not null)
                {
                    items = await GameItemsAsync(db, fitting.FittingId, ct);
                    name  = fitting.Name;
                    if (fitting.FittingId != row.FitId) { row.FitId = fitting.FittingId; changed = true; }
                }
            }
            else
            {
                var saved = await db.SavedFits.AsNoTracking()
                    .Where(f => f.Id == row.FitId)
                    .Select(f => new { f.Name, f.UpdatedAt }).FirstOrDefaultAsync(ct);
                if (saved is not null)
                {
                    name = saved.Name;
                    // Read again only when the fit has been saved since its copy was taken: the
                    // EFT text needs the game data to read, which is not loaded otherwise.
                    items = saved.UpdatedAt > row.ItemsAt ? await AppItemsAsync(dbFactory, db, row.FitId, ct) : Copy(row);
                }
            }

            var fromCopy = items is null;
            items ??= Copy(row);
            var json = JsonSerializer.Serialize(items, Json);
            if (!fromCopy && (json != row.Items || name != row.FitName))
            {
                row.Items = json; row.FitName = name; row.ItemsAt = DateTimeOffset.UtcNow;
                changed = true;
            }

            result[row.StructureTypeId] = new CorpRequiredFit(row.StructureTypeId, name, row.FitSource, row.FitId, row.FitOwnerId,
                row.FitSource == CorpFitChoice.Game ? names.GetValueOrDefault(row.FitOwnerId) ?? "" : "",
                items, fromCopy, row.ItemsAt);
        }

        if (changed) await db.SaveChangesAsync(ct);
        return result;
    }

    private static List<EsiFittingItem> Copy(CorpStructureFit row)
    {
        if (row.Items.Length == 0) return [];
        try { return JsonSerializer.Deserialize<List<EsiFittingItem>>(row.Items, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static async Task<List<EsiFittingItem>?> GameItemsAsync(AppDbContext db, long fittingId, CancellationToken ct)
    {
        var id = (int)fittingId;
        var items = await db.EsiFittingItems.AsNoTracking()
            .Where(i => i.FittingId == id)
            .Select(i => new { i.TypeId, i.Flag, i.Quantity })
            .ToListAsync(ct);
        return items.Count == 0 ? null : [.. items.Select(i => new EsiFittingItem(i.TypeId, i.Flag, i.Quantity))];
    }

    /// <summary>An EVE Console fit in the game's fitting item form, as it would be saved to the
    /// game: each module in a numbered slot, fighters in the bay and charges in the cargo.</summary>
    private static async Task<List<EsiFittingItem>?> AppItemsAsync(
        IDbContextFactory<AppDbContext> dbFactory, AppDbContext db, long savedFitId, CancellationToken ct)
    {
        var eft = await db.SavedFits.AsNoTracking().Where(f => f.Id == savedFitId).Select(f => f.Eft).FirstOrDefaultAsync(ct);
        if (eft is null) return null;
        var data = await DogmaAsync(dbFactory);
        try
        {
            var fit = (await EftFormat.ParseAsync(eft, data, ct)).Fit;
            return GameFittings.ToItems(fit, data, out _);
        }
        catch (FormatException) { return null; }
    }
}
