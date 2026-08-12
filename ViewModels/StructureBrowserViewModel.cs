using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

public class StructureRow
{
    public long   StructureId  { get; init; }
    public string Name         { get; init; } = "";
    public string SystemName   { get; init; } = "";
    public string Constellation{ get; init; } = "";
    public string Region       { get; init; } = "";
    public string CorpName     { get; init; } = "";
    public string AllianceName { get; init; } = "";
    public string TypeName     { get; init; } = "";
    public string StatusText   { get; init; } = "";
    public string Coordinates  { get; init; } = "";
    public string NearestCelestial { get; init; } = "";
    public bool   IsKnown      { get; init; }   // false = no access / not found / pending

    // Filter keys (0 = unknown/none).
    public long CorpId          { get; init; }
    public long AllianceId      { get; init; }
    public long SystemId        { get; init; }
    public long ConstellationId { get; init; }
    public long RegionId        { get; init; }
    public long TypeId          { get; init; }
}

/// <summary>One fitted item in the viewer. Slot is the ESI LocationFlag it came from, or was
/// chosen as by hand.</summary>
public class FittingRow : ReactiveObject
{
    public string Slot     { get; init; } = "";
    public int    TypeId   { get; set; }
    public string TypeName { get; set; } = "";

    /// <summary>True when this came from our own assets rather than being typed. Shown so the
    /// user can tell what the app knows from what they have asserted.</summary>
    public bool FromAssets { get; init; }

    public string Source => FromAssets ? "assets" : "manual";
}

/// <summary>An asset sitting in the selected structure.</summary>
public class StructureAssetRow
{
    public string TypeName { get; init; } = "";
    public string Location { get; init; } = "";
    public long   Quantity { get; init; }
    public string Owner    { get; init; } = "";
}

/// <summary>An industry job run at the selected structure.</summary>
public class StructureJobRow
{
    public string Activity { get; init; } = "";
    public string Product  { get; init; } = "";
    public int    Runs     { get; init; }
    public string Status   { get; init; } = "";
    public string EndDate  { get; init; } = "";
}

public class StructureBrowserViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly EsiPollingService               _polling;
    private readonly Api.EsiClient                   _esi;

    private List<StructureRow> _all = [];

    public ObservableCollection<StructureRow> Rows { get; } = [];

    // Autocomplete suggestion lists (distinct values present in the data).
    public ObservableCollection<string> RegionSuggestions        { get; } = [];
    public ObservableCollection<string> ConstellationSuggestions { get; } = [];
    public ObservableCollection<string> SystemSuggestions        { get; } = [];
    public ObservableCollection<string> TypeSuggestions          { get; } = [];
    public ObservableCollection<string> CorpSuggestions          { get; } = [];
    public ObservableCollection<string> AllianceSuggestions      { get; } = [];

    private string _regionText = "", _constellationText = "", _systemText = "",
                   _typeText = "", _corpText = "", _allianceText = "";
    public string RegionText        { get => _regionText;        set { this.RaiseAndSetIfChanged(ref _regionText, value); ApplyFilters(); } }
    public string ConstellationText { get => _constellationText; set { this.RaiseAndSetIfChanged(ref _constellationText, value); ApplyFilters(); } }
    public string SystemText        { get => _systemText;        set { this.RaiseAndSetIfChanged(ref _systemText, value); ApplyFilters(); } }
    public string TypeText          { get => _typeText;          set { this.RaiseAndSetIfChanged(ref _typeText, value); ApplyFilters(); } }
    public string CorpText          { get => _corpText;          set { this.RaiseAndSetIfChanged(ref _corpText, value); ApplyFilters(); } }
    public string AllianceText      { get => _allianceText;      set { this.RaiseAndSetIfChanged(ref _allianceText, value); ApplyFilters(); } }

    /// <summary>
    /// Defaults on. Unresolved rows used to be hidden as noise, but they are now the ones worth
    /// looking at — a structure ESI will not describe is exactly the one to fill in by hand, and
    /// hiding it would mean the user cannot reach the record they most need to edit.
    /// </summary>
    private bool _showUnknown = true;
    public bool ShowUnknown { get => _showUnknown; set { this.RaiseAndSetIfChanged(ref _showUnknown, value); ApplyFilters(); } }

    // ── Selected structure (the viewer below the list) ───────────────────────

    private StructureRow? _selected;
    public StructureRow? Selected
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            _ = LoadDetailAsync(value);
        }
    }

    public bool HasSelection => _selected is not null;

    // Editable copies. Held apart from the row so cancelling is just a reload and a half-typed
    // edit never leaks into the list.
    private string _editName = "";
    public string EditName { get => _editName; set => this.RaiseAndSetIfChanged(ref _editName, value); }

    private string _editSystem = "";
    public string EditSystem { get => _editSystem; set => this.RaiseAndSetIfChanged(ref _editSystem, value); }

    private string _editType = "";
    public string EditType { get => _editType; set => this.RaiseAndSetIfChanged(ref _editType, value); }

    private string _editNotes = "";
    public string EditNotes { get => _editNotes; set => this.RaiseAndSetIfChanged(ref _editNotes, value); }

    private string _provenance = "";
    /// <summary>Who last wrote this row and when — the reason UpdatedBy exists.</summary>
    public string Provenance { get => _provenance; private set => this.RaiseAndSetIfChanged(ref _provenance, value); }

    public ObservableCollection<FittingRow>       Fitting { get; } = [];
    public ObservableCollection<StructureAssetRow> Assets  { get; } = [];
    public ObservableCollection<StructureJobRow>   Jobs    { get; } = [];

    private string _detailStatus = "";
    public string DetailStatus { get => _detailStatus; private set => this.RaiseAndSetIfChanged(ref _detailStatus, value); }

    private string _status = "";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _busy;
    public bool Busy { get => _busy; set => this.RaiseAndSetIfChanged(ref _busy, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand   { get; }
    public ReactiveCommand<Unit, Unit> ResolveCommand   { get; }
    public ReactiveCommand<Unit, Unit> ClearFilters     { get; }

    public StructureBrowserViewModel(IDbContextFactory<AppDbContext> dbFactory, EsiPollingService polling, Api.EsiClient esi)
    {
        _dbFactory = dbFactory;
        _polling   = polling;
        _esi       = esi;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        ResolveCommand = ReactiveCommand.CreateFromTask(ResolveAsync);
        ClearFilters   = ReactiveCommand.Create(() =>
        {
            RegionText = ConstellationText = SystemText = TypeText = CorpText = AllianceText = "";
        });

        _ = LoadAsync();
    }

    private static string StatusLabel(int status) => (StructureStatus)status switch
    {
        StructureStatus.Resolved => "OK",
        StructureStatus.NoAccess => "No access",         // 403 — private / no docking rights
        StructureStatus.NotFound => "Unanchored (gone)", // 404 — structure no longer exists
        _                        => "Pending",
    };

    private async Task LoadAsync()
    {
        Busy = true;
        try
        {
            // Compute nearest celestials for anything pending (local-only, fast) so the column fills
            // after an SDE import without a full ESI resolve.
            try { await _polling.RefreshNearestCelestialsAsync(); } catch { }

            await using var db = await _dbFactory.CreateDbContextAsync();

            // ⚠️ Structures, not EsiStructureNames. That table belongs to the polling service and
            // is rewritten on every resolve; this one is ours, and is what the editing below
            // writes to. Reading the ESI table here would show values the user cannot change and
            // hide the ones they have.
            var structs = await db.Structures.AsNoTracking().ToListAsync();

            var sys   = await db.SdeSolarSystems.AsNoTracking()
                .ToDictionaryAsync(s => s.SolarSystemId, s => new { s.Name, s.ConstellationId, s.RegionId });
            var cons  = await db.SdeConstellations.AsNoTracking().ToDictionaryAsync(c => c.ConstellationId, c => c.Name);
            var regs  = await db.SdeRegions.AsNoTracking().ToDictionaryAsync(r => r.RegionId, r => r.Name);
            // Resolve the nearest-celestial name live from the celestial table (by stored id) so
            // updated names (e.g. "Stargate to C-FD0D" after a re-import) show without recomputing.
            var nearIds = structs.Where(s => s.NearestCelestialId != 0)
                                 .Select(s => s.NearestCelestialId).Distinct().ToList();
            var nearNames = await db.SdeCelestials.AsNoTracking()
                .Where(c => nearIds.Contains(c.ItemId))
                .ToDictionaryAsync(c => c.ItemId, c => c.Name);

            var allTypeIds = structs.Where(s => s.TypeId > 0).Select(s => s.TypeId).Distinct().ToList();
            var typeNames = await db.SdeTypes.AsNoTracking()
                .Where(t => allTypeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);

            var nameIds = structs.SelectMany(s => new[] { s.OwnerId, s.AllianceId })
                                 .Where(id => id > 0).Distinct().ToList();
            var names = await db.UniverseNames.AsNoTracking()
                .Where(u => nameIds.Contains(u.EntityId))
                .ToDictionaryAsync(u => u.EntityId, u => u.Name);

            // Resolve any corp/alliance names we don't have yet (immutable — cache them).
            var missing = nameIds.Where(id => !names.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                try
                {
                    var resolved = await _esi.GetNamesAsync(missing);
                    var toAdd = resolved
                        .Where(r => !names.ContainsKey(r.Id))
                        .GroupBy(r => r.Id).Select(g => g.First())
                        .Select(r => new UniverseName { EntityId = r.Id, Name = r.Name, Category = r.Category })
                        .ToList();
                    if (toAdd.Count > 0)
                    {
                        db.UniverseNames.AddRange(toAdd);
                        await db.SaveChangesAsync();
                        foreach (var u in toAdd) names[u.EntityId] = u.Name;
                    }
                }
                catch { /* names are best-effort; fall back to "Corp {id}" labels */ }
            }

            string CorpN(long id)  => id > 0 ? names.GetValueOrDefault(id, $"Corp {id}") : "";
            string AllyN(long id)  => id > 0 ? names.GetValueOrDefault(id, $"Alliance {id}") : "";

            _all = structs.Select(s =>
            {
                sys.TryGetValue(s.SolarSystemId, out var sinfo);
                long constId = sinfo?.ConstellationId ?? 0;
                long regId   = sinfo?.RegionId ?? 0;
                return new StructureRow
                {
                    StructureId     = s.StructureId,
                    Name            = string.IsNullOrEmpty(s.Name) ? $"Structure {s.StructureId}" : s.Name,
                    SystemName      = sinfo?.Name ?? "",
                    Constellation   = constId > 0 ? cons.GetValueOrDefault((int)constId, "") : "",
                    Region          = regId > 0 ? regs.GetValueOrDefault((int)regId, "") : "",
                    CorpName        = CorpN(s.OwnerId),
                    AllianceName    = AllyN(s.AllianceId),
                    TypeName        = s.TypeId > 0 ? typeNames.GetValueOrDefault(s.TypeId, $"Type {s.TypeId}") : "",
                    StatusText      = StatusLabel(s.Status),
                    IsKnown         = s.Status == (int)StructureStatus.Resolved,
                    Coordinates     = (s.X == 0 && s.Y == 0 && s.Z == 0)
                                        ? "" : $"{s.X:N0}, {s.Y:N0}, {s.Z:N0}",
                    NearestCelestial= s.NearestCelestialId != 0
                                        ? nearNames.GetValueOrDefault(s.NearestCelestialId, s.NearestCelestial)
                                        : s.NearestCelestial,
                    CorpId          = s.OwnerId,
                    AllianceId      = s.AllianceId,
                    SystemId        = s.SolarSystemId,
                    ConstellationId = constId,
                    RegionId        = regId,
                    TypeId          = s.TypeId,
                };
            }).ToList();

            BuildFilters();
            ApplyFilters();
            Status = $"{_all.Count} structure(s).";
        }
        catch (Exception ex) { Status = $"Load failed: {ex.Message}"; }
        finally { Busy = false; }
    }

    /// <summary>
    /// Fills the viewer for one structure: its editable fields, what is fitted, what is stored
    /// there and what has been built there.
    /// </summary>
    private async Task LoadDetailAsync(StructureRow? row)
    {
        Fitting.Clear();
        Assets.Clear();
        Jobs.Clear();

        if (row is null) { DetailStatus = ""; Provenance = ""; return; }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var s = await db.Structures.AsNoTracking()
                .FirstOrDefaultAsync(x => x.StructureId == row.StructureId);

            EditName   = s?.Name  ?? "";
            EditNotes  = s?.Notes ?? "";
            EditSystem = row.SystemName;
            EditType   = row.TypeName;

            Provenance = s is null
                ? ""
                : s.UpdatedAt == default
                    ? $"Never written · {s.UpdatedBy}"
                    : $"Last written by {s.UpdatedBy} at {s.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";

            var typeNames = await db.SdeTypes.AsNoTracking()
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);

            // ── Fitting ──────────────────────────────────────────────────────
            // From assets where we have them: LocationId is the structure, LocationFlag is the
            // slot. Coverage is "structures we own", which is why the hand-entered rows below
            // are merged in rather than replaced.
            var fitted = await db.EsiAssets.AsNoTracking()
                .Where(a => a.LocationId == row.StructureId && a.LocationFlag.Contains("Slot"))
                .Select(a => new { a.LocationFlag, a.TypeId })
                .ToListAsync();

            foreach (var f in fitted.OrderBy(f => f.LocationFlag))
                Fitting.Add(new FittingRow
                {
                    Slot       = f.LocationFlag,
                    TypeId     = f.TypeId,
                    TypeName   = typeNames.GetValueOrDefault(f.TypeId, $"Type {f.TypeId}"),
                    FromAssets = true,
                });

            // Hand-entered rigs and service modules, for the slots assets did not cover. Never
            // cleared by an empty asset list — that is the whole point of keeping them separately.
            var seen = Fitting.Select(f => f.Slot).ToHashSet();

            foreach (var r in await db.StructureRigs.AsNoTracking()
                         .Where(r => r.StructureId == row.StructureId).ToListAsync())
            {
                var slot = $"RigSlot{r.SlotIndex}";
                if (seen.Contains(slot)) continue;
                Fitting.Add(new FittingRow
                {
                    Slot     = slot,
                    TypeId   = r.RigTypeId,
                    TypeName = typeNames.GetValueOrDefault(r.RigTypeId, $"Type {r.RigTypeId}"),
                });
            }

            foreach (var m in await db.StructureServiceModules.AsNoTracking()
                         .Where(m => m.StructureId == row.StructureId).ToListAsync())
            {
                Fitting.Add(new FittingRow
                {
                    Slot     = "Service",
                    TypeId   = m.TypeId,
                    TypeName = typeNames.GetValueOrDefault(m.TypeId, $"Type {m.TypeId}"),
                });
            }

            // ── Assets stored here ───────────────────────────────────────────
            var assets = await db.EsiAssets.AsNoTracking()
                .Where(a => a.LocationId == row.StructureId)
                .Select(a => new { a.TypeId, a.Quantity, a.LocationFlag, a.OwnerType })
                .Take(2000)
                .ToListAsync();

            foreach (var a in assets.OrderBy(a => a.LocationFlag))
                Assets.Add(new StructureAssetRow
                {
                    TypeName = typeNames.GetValueOrDefault(a.TypeId, $"Type {a.TypeId}"),
                    Location = a.LocationFlag,
                    Quantity = a.Quantity,
                    Owner    = a.OwnerType,
                });

            // ── Industry jobs run here ───────────────────────────────────────
            var jobs = await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => j.StationId == row.StructureId)
                .OrderByDescending(j => j.EndDate)
                .Take(500)
                .Select(j => new { j.ActivityId, j.ProductTypeId, j.Runs, j.Status, j.EndDate })
                .ToListAsync();

            foreach (var j in jobs)
                Jobs.Add(new StructureJobRow
                {
                    Activity = ActivityName(j.ActivityId),
                    // Nullable: research and copying jobs produce no item type.
                    Product  = j.ProductTypeId is { } pid
                                 ? typeNames.GetValueOrDefault(pid, $"Type {pid}")
                                 : "—",
                    Runs     = j.Runs,
                    Status   = j.Status,
                    EndDate  = j.EndDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                });

            DetailStatus = $"{Fitting.Count} fitted · {Assets.Count:N0} asset(s) · {Jobs.Count:N0} job(s)";
        }
        catch (Exception ex) { DetailStatus = $"Detail load failed: {ex.Message}"; }
    }

    private static string ActivityName(int activityId) => activityId switch
    {
        1 => "Manufacturing",
        3 => "TE Research",
        4 => "ME Research",
        5 => "Copying",
        8 => "Invention",
        9 => "Reactions",
        11 => "Reactions",
        _ => $"Activity {activityId}",
    };

    /// <summary>
    /// Writes the edited fields back. Only the app's own table is touched — nothing here can
    /// reach EsiStructureNames, so a hand-typed name cannot be mistaken for something ESI said.
    /// </summary>
    public async Task SaveDetailAsync()
    {
        if (Selected is not { } row) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var s = await db.Structures.FindAsync(row.StructureId);
            if (s is null)
            {
                DetailStatus = "That structure is no longer in the table.";
                return;
            }

            s.Name      = EditName.Trim();
            s.Notes     = EditNotes;
            s.UpdatedBy = StructureSource.User;
            s.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();

            DetailStatus = "Saved.";
            Provenance   = $"Last written by {StructureSource.User} at {DateTimeOffset.Now:yyyy-MM-dd HH:mm}";
            await LoadAsync();
        }
        catch (Exception ex) { DetailStatus = $"Save failed: {ex.Message}"; }
    }

    private void BuildFilters()
    {
        void Fill(ObservableCollection<string> col, Func<StructureRow, string> sel)
        {
            col.Clear();
            foreach (var v in _all.Select(sel).Where(s => !string.IsNullOrEmpty(s))
                                  .Distinct().OrderBy(s => s))
                col.Add(v);
        }
        Fill(RegionSuggestions,        r => r.Region);
        Fill(ConstellationSuggestions, r => r.Constellation);
        Fill(SystemSuggestions,        r => r.SystemName);
        Fill(TypeSuggestions,          r => r.TypeName);
        Fill(CorpSuggestions,          r => r.CorpName);
        Fill(AllianceSuggestions,      r => r.AllianceName);
    }

    private void ApplyFilters()
    {
        static bool Has(string value, string filter) =>
            string.IsNullOrWhiteSpace(filter) ||
            value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

        bool Match(StructureRow r) =>
            (ShowUnknown || r.IsKnown) &&
            Has(r.Region, RegionText) && Has(r.Constellation, ConstellationText) &&
            Has(r.SystemName, SystemText) && Has(r.TypeName, TypeText) &&
            Has(r.CorpName, CorpText) && Has(r.AllianceName, AllianceText);

        Rows.Clear();
        foreach (var r in _all.Where(Match).OrderBy(r => r.Region).ThenBy(r => r.SystemName).ThenBy(r => r.Name))
            Rows.Add(r);
        int hidden = _all.Count(r => !r.IsKnown);
        Status = ShowUnknown || hidden == 0
            ? $"{Rows.Count} of {_all.Count} structure(s)."
            : $"{Rows.Count} shown · {hidden} unknown hidden.";
    }

    private async Task ResolveAsync()
    {
        Busy = true;
        Status = "Resolving structures via ESI…";
        try
        {
            await _polling.ForceResolveStructureNamesAsync();
            await LoadAsync();
        }
        catch (Exception ex) { Status = $"Resolve failed: {ex.Message}"; }
        finally { Busy = false; }
    }
}
