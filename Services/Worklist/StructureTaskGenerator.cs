using System.Globalization;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Services.Pi;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>The kinds of structure task, each switched on and off on Config → Structures.</summary>
public enum StructureTaskKind { Fuel, Pickup, Ammo, Fit }

/// <summary>What a moon material bay is measured by to decide it is worth collecting.</summary>
public enum StructurePickupMeasure { MarketValue, Volume, PercentFull }

/// <summary>
/// Tasks from Corp Structures: fuel to bring before a structure runs dry, a Metenox moon bay to
/// empty, a fit to put right and ammo or fighters to top up — for the corporations chosen on
/// Config → Structures.
///
/// <para>⚠️ Fuel needs no assets: ESI's fuel expiry is the days left, for every corporation whose
/// structures are read. The burn is what Corp Structures works out — from the modules in the
/// assets, or with none read, from the fit set for the structure's type — and without one the
/// task is still raised, without a count of blocks. Everything else here reads what is IN the
/// structure, so it is raised only where the corporation's assets are read.</para>
/// </summary>
public class StructureTaskGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    WorklistSettings settings) : IWorklistGenerator
{
    public const string SourceId = "structures";

    public string Id          => SourceId;
    public string DisplayName => WorklistText.SourceStructures;

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        var fuelOn   = settings.RaisesStructureTask(StructureTaskKind.Fuel);
        var pickupOn = settings.RaisesStructureTask(StructureTaskKind.Pickup);
        var ammoOn   = settings.RaisesStructureTask(StructureTaskKind.Ammo);
        var fitOn    = settings.RaisesStructureTask(StructureTaskKind.Fit);
        if (!fuelOn && !pickupOn && !ammoOn && !fitOn) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var chosen = settings.StructureCorporations;
        var corps = (await db.Corporations.AsNoTracking()
                .Select(c => new { Id = (long)c.Id, c.Name }).ToListAsync(ct))
            .Where(c => chosen is null || chosen.Contains(c.Id))
            .ToList();
        if (corps.Count == 0) return [];

        var corpIds = corps.Select(c => c.Id).ToList();
        var withAny = (await db.EsiCorpStructures.AsNoTracking().Where(x => corpIds.Contains(x.CorporationId))
                .Select(x => x.CorporationId).Distinct().ToListAsync(ct))
            .Concat(await db.EsiCorpStarbases.AsNoTracking().Where(x => corpIds.Contains(x.CorporationId))
                .Select(x => x.CorporationId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var now   = DateTimeOffset.UtcNow;
        var rules = new StructureTaskRules(settings.StructureFuelMinDays, settings.StructureFuelFillDays,
                                           settings.StructurePickupBy, settings.StructurePickupAt);
        var items = new List<WorklistItem>();

        foreach (var corp in corps.Where(c => withAny.Contains(c.Id)))
        {
            var infos = await CorpStructures.LoadAsync(dbFactory, corp.Id, ct);

            // A manifest line keeps the item's English name, as every worklist line does: it is
            // what a copied list is pasted into the game as.
            var lineTypeIds = infos.SelectMany(i => i.Fuel.Select(f => f.TypeId)
                    .Concat(i.MoonBay.Select(b => b.TypeId))
                    .Concat(i.FitCheck is { } c
                        ? c.Missing.Concat(c.Wrong).Select(x => x.TypeId)
                            .Concat(c.AmmoLow.Concat(c.FightersLow).Select(x => x.TypeId))
                        : []))
                .Distinct().ToList();
            var english = await db.SdeTypes.AsNoTracking()
                .Where(t => lineTypeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);
            string Eng(int typeId, string shown) => english.GetValueOrDefault(typeId, shown);

            // ⚠️ A list no longer read is a snapshot: its fuel expiries are from the day it stopped,
            // and a task off one would send someone to fuel what was fuelled long since. One task
            // instead, for the list itself.
            var structuresRead = await CorpStructures.ListReadAsync(dbFactory, corp.Id, CorpStructures.StructuresEndpoint, ct);
            var towersRead     = await CorpStructures.ListReadAsync(dbFactory, corp.Id, CorpStructures.StarbasesEndpoint, ct);
            if (fuelOn)
            {
                if (Stale(corp.Id, corp.Name, structuresRead, infos.Any(i => i.Kind == CorpStructureKind.Upwell),
                          CorpStructures.StructuresEndpoint, WorklistText.StructureListStaleTitle) is { } s) items.Add(s);
                if (Stale(corp.Id, corp.Name, towersRead, infos.Any(i => i.Kind == CorpStructureKind.Tower),
                          CorpStructures.StarbasesEndpoint, WorklistText.StructureTowerListStaleTitle) is { } t) items.Add(t);
            }
            bool Current(CorpStructureInfo i) => i.Kind switch
            {
                CorpStructureKind.Upwell => !structuresRead.Stale,
                CorpStructureKind.Tower  => !towersRead.Stale,
                _                        => true,
            };

            foreach (var i in infos)
            {
                if (fuelOn   && Current(i) && Fuel(i, corp.Name, rules, now, Eng) is { } f) items.Add(f);
                if (pickupOn && Pickup(i, corp.Name, rules, Eng)      is { } p) items.Add(p);
                if (fitOn    && Fit(i, corp.Name, Eng)                is { } x) items.Add(x);
                if (ammoOn   && Ammo(i, corp.Name, Eng)               is { } a) items.Add(a);
            }
        }

        return items;
    }

    private static WorklistItem Base(CorpStructureInfo i, string kind) => new()
    {
        Key          = $"{SourceId}:{kind}:{i.Id}",
        Source       = SourceId,
        Kind         = WorklistKind.Structure,
        Title        = "",
        Readiness    = WorklistReadiness.Ready,
        LocationId   = i.Id,
        LocationName = i.Name,
        IconUrl      = WorklistIcons.Render(i.TypeId),
    };

    // ── A list no longer read ────────────────────────────────────────────────

    private static WorklistItem? Stale(long corpId, string corp, CorpListRead read, bool any, string endpoint, string titleFormat)
    {
        if (!any || !read.Stale) return null;
        var when = read.ReadAt is { } at ? PiFormat.When(at) : WorklistText.StructureListNeverRead;
        return new WorklistItem
        {
            Key       = $"{SourceId}:stale:{corpId}:{endpoint}",
            Source    = SourceId,
            Kind      = WorklistKind.Structure,
            Title     = string.Format(titleFormat, corp, when),
            Detail    = read.Denied ? WorklistText.StructureListDeniedDetail : WorklistText.StructureListNotReadDetail,
            Readiness = WorklistReadiness.Ready,
            Priority  = WorklistPriority.StructureFuel,
            IconUrl   = WorklistIcons.CorporationLogo(corpId),
        };
    }

    // ── Fuel ─────────────────────────────────────────────────────────────────

    private static WorklistItem? Fuel(CorpStructureInfo i, string corp, StructureTaskRules rules,
                                      DateTimeOffset now, Func<int, string, string> eng)
    {
        if (i.Kind == CorpStructureKind.Skyhook) return null;

        // Out of fuel already: an Upwell structure sitting in low power with services to run.
        var outOfFuel = i.Kind == CorpStructureKind.Upwell && !i.IsPowered && i.NeedsFuel && i.RunsOut is null;
        double daysLeft;
        if (i.RunsOut is { } runsOut && (i.IsPowered || i.NeedsFuel)) daysLeft = Math.Max(0, (runsOut - now).TotalDays);
        else if (outOfFuel) daysLeft = 0;
        else return null;

        if (!StructureTasks.FuelDue(daysLeft, rules.FuelMinDays)) return null;

        // What to bring of each fuel that burns: enough for it to last the fill-to days.
        var lines = new List<WorklistLine>();
        foreach (var f in i.Fuel.Where(f => f.PerHour > 0))
        {
            var left = f.RunsOut is { } r ? Math.Max(0, (r - now).TotalDays) : daysLeft;
            var need = StructureTasks.FuelToBring(left, rules.FuelFillDays, f.PerHour!.Value);
            if (need > 0) lines.Add(new WorklistLine(f.TypeId, eng(f.TypeId, f.Name), need));
        }

        // No bay read: the blocks counted from the burn alone, of no kind in particular.
        var hasBlockLine = i.Fuel.Any(f => f.PerHour > 0 && f.EsiRunsOut is not null)
                        || (i.Kind == CorpStructureKind.Tower && lines.Count > 0);
        long looseBlocks = 0;
        if (!hasBlockLine && i.BlocksPerHour is > 0 and var bph)
            looseBlocks = StructureTasks.FuelToBring(daysLeft, rules.FuelFillDays, bph);

        var detail = string.Format(WorklistText.StructureFuelDetail, i.TypeName, corp,
                         i.RunsOut is { } at ? PiFormat.When(at) : WorklistText.StructureFuelNow,
                         rules.FuelFillDays.ToString("0.#", CultureInfo.CurrentCulture));
        if (looseBlocks > 0)
            detail += " " + string.Format(WorklistText.StructureFuelBlocks, looseBlocks,
                                          i.BlocksPerHour!.Value.ToString("0.#", CultureInfo.CurrentCulture));
        else if (lines.Count == 0 && i.BlocksPerHour is null)
            detail += " " + string.Format(WorklistText.StructureFuelNoBurn, i.TypeName);
        if (i.BurnFromTypeFit && i.BlocksPerHour is > 0)
            detail += " " + WorklistText.StructureFuelTypeFit;

        return Base(i, "fuel") with
        {
            Title    = outOfFuel || daysLeft <= 0
                ? string.Format(WorklistText.StructureFuelOutTitle, i.Name)
                : string.Format(WorklistText.StructureFuelTitle, i.Name, PiFormat.Duration(TimeSpan.FromDays(daysLeft))),
            Detail   = detail,
            Lines    = lines,
            Quantity = lines.Count == 0 ? looseBlocks : 0,
            Priority = WorklistPriority.StructureFuel,
        };
    }

    // ── Moon material pickup ─────────────────────────────────────────────────

    private static WorklistItem? Pickup(CorpStructureInfo i, string corp, StructureTaskRules rules,
                                        Func<int, string, string> eng)
    {
        // The bay is read from the assets: none read, nothing known to be in it.
        if (i.MoonBayCapacity is not { } capacity || !i.FitKnown || i.MoonBay.Count == 0) return null;

        var value  = i.MoonBay.Sum(b => b.Value ?? 0);
        var volume = i.MoonBay.Sum(b => b.Volume);
        if (!StructureTasks.PickupDue(rules.PickupBy, rules.PickupAt, value, volume, capacity)) return null;

        return Base(i, "pickup") with
        {
            Title    = string.Format(WorklistText.StructurePickupTitle, i.Name,
                                     StructureTasks.PercentFull(volume, capacity).ToString("0.#", CultureInfo.CurrentCulture)),
            Detail   = string.Format(WorklistText.StructurePickupDetail, i.TypeName, corp,
                                     volume.ToString("N0", CultureInfo.CurrentCulture),
                                     IndustryOppsReport.Isk(value)),
            Lines    = [.. i.MoonBay.Select(b => new WorklistLine(b.TypeId, eng(b.TypeId, b.Name), b.Quantity))],
            Priority = WorklistPriority.StructurePickup,
        };
    }

    // ── Fit ──────────────────────────────────────────────────────────────────

    private static WorklistItem? Fit(CorpStructureInfo i, string corp, Func<int, string, string> eng)
    {
        if (i.FitCheck is not { Known: true } c || c.Missing.Count + c.Wrong.Count == 0) return null;

        var parts = new List<string>();
        if (c.Missing.Count > 0) parts.Add(string.Format(WorklistText.StructureFitMissing, Modules(c.Missing)));
        if (c.Wrong.Count > 0)   parts.Add(string.Format(WorklistText.StructureFitWrong, Modules(c.Instead), Modules(c.Wrong)));

        return Base(i, "fit") with
        {
            Title    = string.Format(WorklistText.StructureFitTitle, i.Name),
            Detail   = string.Join(" ", parts) + " "
                     + string.Format(WorklistText.StructureFitAgainst, c.FitName, c.FitSource, i.TypeName, corp),
            Lines    = [.. c.Missing.Concat(c.Wrong)
                           .GroupBy(m => m.TypeId)
                           .Select(g => new WorklistLine(g.Key, eng(g.Key, g.First().Name), g.Sum(m => (long)m.Count)))],
            Priority = WorklistPriority.Missing,
        };
    }

    // ── Ammo and fighters ────────────────────────────────────────────────────

    private static WorklistItem? Ammo(CorpStructureInfo i, string corp, Func<int, string, string> eng)
    {
        if (i.FitCheck is not { Known: true } c || c.AmmoLow.Count + c.FightersLow.Count == 0) return null;

        var low = c.AmmoLow.Concat(c.FightersLow).ToList();
        return Base(i, "ammo") with
        {
            Title    = string.Format(WorklistText.StructureAmmoTitle, i.Name),
            Detail   = string.Format(WorklistText.StructureAmmoDetail,
                           string.Join(", ", low.Select(s => string.Format(WorklistText.StructureAmmoHaveWant, s.Name, s.Have, s.Want))),
                           c.FitName, i.TypeName, corp),
            Lines    = [.. low.Select(s => new WorklistLine(s.TypeId, eng(s.TypeId, s.Name), s.Want - s.Have))],
            Priority = WorklistPriority.StructureAmmo,
        };
    }

    private static string Modules(IEnumerable<CorpFitShortfall> list) =>
        string.Join(", ", list.Select(m => m.Count > 1 ? $"{m.Count} × {m.Name}" : m.Name));
}

/// <summary>The structure task thresholds from Config → Structures.</summary>
public sealed record StructureTaskRules(double FuelMinDays, double FuelFillDays,
                                        StructurePickupMeasure PickupBy, double PickupAt);

/// <summary>The sums behind the structure tasks, apart from the database so they can be checked.</summary>
public static class StructureTasks
{
    /// <summary>A structure with less than the minimum days of fuel left is due. Out of fuel is
    /// always due, whatever the minimum.</summary>
    public static bool FuelDue(double daysLeft, double minDays) => daysLeft <= 0 || daysLeft < minDays;

    /// <summary>
    /// Units to bring for a fuel to last <paramref name="fillDays"/> from now: the days it is short,
    /// at its burn, rounded up — a block short is an hour of services off. Nothing when it already
    /// lasts that long.
    /// </summary>
    public static long FuelToBring(double daysLeft, double fillDays, double perHour) =>
        perHour <= 0 || daysLeft >= fillDays ? 0
            : (long)Math.Ceiling((fillDays - Math.Max(0, daysLeft)) * 24 * perHour - 1e-9);

    public static double PercentFull(double volume, double capacity) =>
        capacity > 0 ? volume / capacity * 100 : 0;

    /// <summary>Whether a moon bay holds enough, by the chosen measure, to be worth collecting.
    /// An empty bay never is, even at a threshold of nothing.</summary>
    public static bool PickupDue(StructurePickupMeasure by, double at, double value, double volume, double capacity)
    {
        if (volume <= 0) return false;
        return by switch
        {
            StructurePickupMeasure.MarketValue => value  >= at,
            StructurePickupMeasure.Volume      => volume >= at,
            _                                  => PercentFull(volume, capacity) >= at,
        };
    }
}
