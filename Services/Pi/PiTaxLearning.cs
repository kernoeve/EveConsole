using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Pi;

/// <summary>One planetary tax entry from a character's wallet journal.</summary>
/// <param name="Amount">What was paid, as a positive number.</param>
public sealed record PiTaxEntry(long JournalId, long CharacterId, int PlanetId, DateTimeOffset Date, bool IsExport, decimal Amount);

/// <summary>A planet's tax rate as learned from the journal.</summary>
public sealed record PiLearnedRate(int PlanetId, double Rate, DateTimeOffset LearnedAt, long JournalId,
                                   long CharacterId, string Source, long Units);

/// <summary>
/// Learns the tax rate each planet actually charges, from the wallet journal.
///
/// <para><b>What the journal carries.</b> ref_type <c>planetary_export_tax</c> (97) and
/// <c>planetary_import_tax</c> (96), with the amount paid (negative: it leaves the wallet), the
/// date, and <c>context_id</c> = the planet's id with <c>context_id_type</c> "planet_id" (CCP's
/// eve-glue maps both ref types' argument to planet_id). The description is the client's own
/// sentence and is not parsed. ⚠️ No quantity and no type: an entry says what was paid, never for
/// what.</para>
///
/// <para><b>The method.</b> rate = tax ÷ Σ (units × the tier's customs base cost) — half the base
/// for an import. The units come from the colony's own snapshots: each new layout is compared with
/// the old one simulated forward to the same moment, and the difference is what was taken off or
/// brought in (<see cref="PiLayoutStore.Movements"/>). Every tax entry for the planet that falls in
/// such a window — after the older snapshot, at or before the newer — is set against that window's
/// movements: exports against what was removed, imports against what was added, all of them in the
/// window together.</para>
///
/// <para><b>Its limits.</b></para>
/// <list type="bullet">
/// <item>Only as good as the forecast between the two snapshots. Imported inputs that were turned
/// into products before the newer snapshot are partly counted as consumed, not added, which reads
/// as a HIGHER rate; the products they became are kept out of the export side by counting only
/// imported types as imports and only the rest as exports.</item>
/// <item>Two exports to different places, or goods destroyed with a demolished pin, read the same
/// as one export. A window that gives a rate outside (0, 100 %] is thrown away.</item>
/// <item>A customs office can charge different rates by standing; this learns the rate charged to
/// whoever paid the newest entry, and applies it to the planet.</item>
/// <item>Sovereignty skyhooks may never write a journal entry at all, in which case a skyhook planet
/// keeps the Settings default.</item>
/// </list>
///
/// <para>⚠️ Built from the documentation and tested on constructed data (tools/PiEngineCheck):
/// at the time of writing no real planetary tax entry was available to check it against.</para>
/// </summary>
public static class PiTaxLearning
{
    public const string ExportRefType = "planetary_export_tax";
    public const string ImportRefType = "planetary_import_tax";

    /// <summary>The tax entry a journal row is, or null when it is not one for a planet.</summary>
    public static PiTaxEntry? FromJournal(WalletJournalEntry e)
    {
        var export = e.RefType == ExportRefType;
        if (!export && e.RefType != ImportRefType) return null;
        if (e.ContextId is not long planet || planet <= 0 || planet > int.MaxValue) return null;
        if (e.ContextIdType is { Length: > 0 } kind && kind != "planet_id") return null;
        return new PiTaxEntry(e.EsiId, e.OwnerId, (int)planet, e.Date, export, Math.Abs(e.Amount));
    }

    /// <summary>
    /// The rate one window of movements and the entries inside it give, or null when they give
    /// none worth keeping.
    /// </summary>
    /// <param name="importedTypes">Types the colony brings in (consumes, makes nowhere): what an
    /// import is counted in. Everything else is what an export is counted in.</param>
    public static PiLearnedRate? LearnWindow(IReadOnlyList<PiTaxEntry> entries,
                                             IReadOnlyList<PiColonyMovement> window,
                                             PiStaticData sd, IReadOnlySet<int> importedTypes)
    {
        if (entries.Count == 0 || window.Count == 0) return null;

        decimal exportTax = entries.Where(e => e.IsExport).Sum(e => e.Amount);
        decimal importTax = entries.Where(e => !e.IsExport).Sum(e => e.Amount);

        double exportBase = 0, importBase = 0;
        long   exportUnits = 0, importUnits = 0;
        foreach (var m in window)
        {
            if (sd.TierOf(m.TypeId) is not { } tier) continue;
            if (exportTax > 0 && !importedTypes.Contains(m.TypeId) && m.Removed > 0)
            {
                exportBase  += m.Removed * PiTiers.BaseCost(tier);
                exportUnits += m.Removed;
            }
            if (importTax > 0 && importedTypes.Contains(m.TypeId) && m.Added > 0)
            {
                importBase  += m.Added * PiTiers.BaseCost(tier) / 2;
                importUnits += m.Added;
            }
        }

        // The same rate applies both ways, so a window with both is one equation.
        var tax  = (double)(exportTax + importTax);
        var base_ = exportBase + importBase;
        if (tax <= 0 || base_ <= 0) return null;

        var rate = tax / base_;
        if (rate is <= 0 or > 1) return null;

        var newest = entries.MaxBy(e => e.Date)!;
        var source = exportTax > 0 && importTax > 0 ? "both" : exportTax > 0 ? "export" : "import";
        return new PiLearnedRate(newest.PlanetId, rate, newest.Date, newest.JournalId, newest.CharacterId,
                                 source, exportUnits + importUnits);
    }

    /// <summary>
    /// The newest rate each planet's entries and movements give. Entries with no window around
    /// them — paid before the first snapshot was read, or after the latest — give nothing yet.
    /// </summary>
    public static List<PiLearnedRate> Learn(IEnumerable<PiTaxEntry> entries, IEnumerable<PiColonyMovement> movements,
                                            PiStaticData sd, Func<int, IReadOnlySet<int>> importedTypesOf)
    {
        var byPlanet = entries.GroupBy(e => e.PlanetId).ToDictionary(g => g.Key, g => g.ToList());
        var result   = new List<PiLearnedRate>();

        foreach (var planetWindows in movements.Where(m => byPlanet.ContainsKey(m.PlanetId))
                                               .GroupBy(m => m.PlanetId))
        {
            var planetEntries = byPlanet[planetWindows.Key];
            var imported      = importedTypesOf(planetWindows.Key);

            PiLearnedRate? best = null;
            foreach (var window in planetWindows.GroupBy(m => (m.FromUpdate, m.ToUpdate)))
            {
                var (from, to) = window.Key;
                var inside = planetEntries.Where(e => e.Date > from && e.Date <= to).ToList();
                if (LearnWindow(inside, window.ToList(), sd, imported) is { } learned
                    && (best is null || learned.LearnedAt > best.LearnedAt))
                    best = learned;
            }
            if (best is not null) result.Add(best);
        }
        return result;
    }

    /// <summary>
    /// Learns what it can from one character's journal and colony movements, and stores each
    /// planet's rate where it is newer than the one held. Returns how many planets changed.
    /// </summary>
    public static async Task<int> LearnAsync(AppDbContext db, long characterId, PiStaticData sd,
                                             CancellationToken ct = default)
    {
        // Two ref types for one owner: few rows, and filtered on the date in memory —
        // ⚠️ DateTimeOffset comparisons do not translate on SQLite.
        var rows = await db.EsiWalletJournal.AsNoTracking()
            .Where(e => e.OwnerId == characterId && e.OwnerType == "character"
                     && (e.RefType == ExportRefType || e.RefType == ImportRefType))
            .ToListAsync(ct);
        var entries = rows.Select(FromJournal).OfType<PiTaxEntry>().ToList();
        if (entries.Count == 0) return 0;

        var planets   = entries.Select(e => e.PlanetId).Distinct().ToList();
        var movements = await db.PiColonyMovements.AsNoTracking()
            .Where(m => m.CharacterId == characterId && planets.Contains(m.PlanetId))
            .ToListAsync(ct);
        if (movements.Count == 0) return 0;

        var layouts = (await PiLayoutStore.LoadAsync(db, [characterId], ct))
            .ToDictionary(l => l.PlanetId);
        IReadOnlySet<int> ImportedTypes(int planetId) =>
            layouts.TryGetValue(planetId, out var layout)
                ? PiEngine.Flows(layout, sd).Where(f => f.ImportedPerDay > 0).Select(f => f.TypeId).ToHashSet()
                : new HashSet<int>();

        var learned = Learn(entries, movements, sd, ImportedTypes);
        if (learned.Count == 0) return 0;

        var held = await db.PiPlanetTaxRates
            .Where(r => planets.Contains(r.PlanetId))
            .ToDictionaryAsync(r => r.PlanetId, ct);

        var changed = 0;
        foreach (var l in learned)
        {
            if (held.TryGetValue(l.PlanetId, out var row))
            {
                if (row.LearnedAt >= l.LearnedAt) continue;
                row.Rate = l.Rate; row.LearnedAt = l.LearnedAt; row.JournalId = l.JournalId;
                row.CharacterId = l.CharacterId; row.Source = l.Source; row.Units = l.Units;
            }
            else
            {
                db.PiPlanetTaxRates.Add(new PiPlanetTaxRate
                {
                    PlanetId = l.PlanetId, Rate = l.Rate, LearnedAt = l.LearnedAt, JournalId = l.JournalId,
                    CharacterId = l.CharacterId, Source = l.Source, Units = l.Units,
                });
            }
            changed++;
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return changed;
    }
}
