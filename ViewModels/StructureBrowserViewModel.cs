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

/// <summary>An asset sitting in the selected structure, at any depth.</summary>
public class StructureAssetRow
{
    public string TypeName  { get; init; } = "";
    public string Location  { get; init; } = "";
    /// <summary>The container it is inside, empty when it sits directly in the structure. Without
    /// this a hangar full of cans reads as one flat list and there is no telling what is where.</summary>
    public string Container { get; init; } = "";
    public long   Quantity  { get; init; }
    public string Owner     { get; init; } = "";
}

/// <summary>An industry job run at the selected structure.</summary>
public class StructureJobRow
{
    public string   Activity { get; init; } = "";
    public string   Product  { get; init; } = "";
    public int      Runs     { get; init; }
    public string   Status   { get; init; } = "";
    public string   EndDate  { get; init; } = "";

    /// <summary>The end date as a value, for filtering. EndDate above is already formatted for
    /// display and cannot be compared.</summary>
    public DateTime EndsAt   { get; init; }
}

public class StructureBrowserViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly EsiPollingService               _polling;
    private readonly Api.EsiClient                   _esi;
    private readonly FittingOptionService            _fittingOptions;

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

    public ObservableCollection<FittingRow>        Fitting { get; } = [];
    public ObservableCollection<StructureAssetRow> Assets  { get; } = [];
    public ObservableCollection<StructureAssetRow> Cargo    { get; } = [];
    public ObservableCollection<StructureAssetRow> Fuel     { get; } = [];
    public ObservableCollection<StructureAssetRow> Fighters { get; } = [];
    public ObservableCollection<StructureJobRow>   Jobs     { get; } = [];

    // ── Industry job filters ─────────────────────────────────────────────────
    // Held unfiltered so changing a filter re-filters in memory rather than re-querying.
    private List<StructureJobRow> _allJobs = [];

    /// <summary>Defaults to the last 90 days: a structure that has been running a while holds
    /// thousands of delivered jobs, and the recent ones are what anyone is looking for.</summary>
    private string _jobFrom = DateTime.Today.AddDays(-90).ToString("yyyy-MM-dd");
    public string JobFrom
    {
        get => _jobFrom;
        set { this.RaiseAndSetIfChanged(ref _jobFrom, value); ApplyJobFilters(); }
    }

    private string _jobThru = "";
    public string JobThru
    {
        get => _jobThru;
        set { this.RaiseAndSetIfChanged(ref _jobThru, value); ApplyJobFilters(); }
    }

    public ObservableCollection<string> JobStatuses { get; } = ["All"];

    private string _jobStatus = "All";
    public string JobStatus
    {
        get => _jobStatus;
        set { this.RaiseAndSetIfChanged(ref _jobStatus, value); ApplyJobFilters(); }
    }

    private string _jobCountText = "";
    public string JobCountText { get => _jobCountText; private set => this.RaiseAndSetIfChanged(ref _jobCountText, value); }

    /// <summary>
    /// Narrows the held jobs onto the grid. Dates are parsed leniently — a half-typed date leaves
    /// that bound open rather than emptying the grid while the user is still typing.
    /// </summary>
    private void ApplyJobFilters()
    {
        Jobs.Clear();

        DateTime? from = DateTime.TryParse(JobFrom, out var f) ? f.Date : null;
        DateTime? thru = DateTime.TryParse(JobThru, out var t) ? t.Date.AddDays(1) : null;

        foreach (var j in _allJobs)
        {
            if (from is { } lo && j.EndsAt < lo) continue;
            if (thru is { } hi && j.EndsAt >= hi) continue;
            if (JobStatus != "All" && !string.Equals(j.Status, JobStatus, StringComparison.OrdinalIgnoreCase))
                continue;

            Jobs.Add(j);
        }

        JobCountText = Jobs.Count == _allJobs.Count
            ? $"{Jobs.Count:N0} job(s)"
            : $"{Jobs.Count:N0} of {_allJobs.Count:N0} job(s)";
    }

    /// <summary>
    /// Clicking a slot. Reports what was clicked for now; this is the hook the module picker will
    /// hang off, and it is deliberately on the view model rather than the control so the control
    /// stays reusable for ships.
    /// </summary>
    public ReactiveCommand<EveConsole.Controls.FittingSlot, Unit> SlotClickedCommand { get; }

    private IReadOnlyList<EveConsole.Controls.FittingSlot>? _fittingSlots;
    /// <summary>Every slot the hull has, filled or empty, for the graphical fitting view.</summary>
    public IReadOnlyList<EveConsole.Controls.FittingSlot>? FittingSlots
    {
        get => _fittingSlots;
        private set
        {
            this.RaiseAndSetIfChanged(ref _fittingSlots, value);
            FittingReadOnly = value?.Any(s => s.FromAssets) == true;
        }
    }

    private bool _fittingReadOnly;
    /// <summary>
    /// True once ANY slot is known from assets, which locks the whole fitting rather than that
    /// one slot.
    ///
    /// <para>If the game tells us what is fitted, it is the authority; a hand-edited slot
    /// alongside asset-sourced ones would be a disagreement with no way to tell which side is
    /// right, and the next asset poll would silently overrule it anyway. Editing is for the
    /// structures we cannot see inside — which is exactly the set where no slot comes from
    /// assets.</para>
    /// </summary>
    public bool FittingReadOnly
    {
        get => _fittingReadOnly;
        private set
        {
            this.RaiseAndSetIfChanged(ref _fittingReadOnly, value);
            this.RaisePropertyChanged(nameof(FittingSourceText));
        }
    }

    public string FittingSourceText => FittingReadOnly
        ? "Fitting comes from assets — read only"
        : "No fitting in assets — click a slot to record one";

    // ── Module picker ────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> FitModuleCommand    { get; }
    public ReactiveCommand<Unit, Unit> ClearSlotCommand    { get; }
    public ReactiveCommand<Unit, Unit> CancelPickerCommand { get; }

    /// <summary>The slot being edited, or null when the picker is closed.</summary>
    private EveConsole.Controls.FittingSlot? _pickingSlot;
    public EveConsole.Controls.FittingSlot? PickingSlot
    {
        get => _pickingSlot;
        private set
        {
            this.RaiseAndSetIfChanged(ref _pickingSlot, value);
            this.RaisePropertyChanged(nameof(PickerTitle));
        }
    }

    public string PickerTitle => PickingSlot is { } s
        ? $"{s.Band} slot {s.Index + 1}"
        : "";

    private bool _pickerOpen;
    public bool PickerOpen { get => _pickerOpen; private set => this.RaiseAndSetIfChanged(ref _pickerOpen, value); }

    public ObservableCollection<FittingOption> ModuleOptions { get; } = [];

    private FittingOption? _selectedModule;
    public FittingOption? SelectedModule
    {
        get => _selectedModule;
        set => this.RaiseAndSetIfChanged(ref _selectedModule, value);
    }

    private string _moduleFilter = "";
    public string ModuleFilter
    {
        get => _moduleFilter;
        set { this.RaiseAndSetIfChanged(ref _moduleFilter, value); ApplyModuleFilter(); }
    }

    /// <summary>Everything fittable in the open slot, before the text filter narrows it.</summary>
    private List<FittingOption> _allModuleOptions = [];

    private void ApplyModuleFilter()
    {
        ModuleOptions.Clear();

        var needle = ModuleFilter.Trim();
        foreach (var o in _allModuleOptions)
            if (needle.Length == 0 ||
                o.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                o.GroupName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                ModuleOptions.Add(o);
    }

    /// <summary>
    /// Opens the picker for a slot, listing what the game says can go in it.
    ///
    /// <para>The canvas refuses clicks when the fitting comes from assets; this checks again, so a
    /// caller that forgets to bind IsReadOnly still cannot overwrite what the game reported.</para>
    /// </summary>
    private async Task OpenSlotPickerAsync(EveConsole.Controls.FittingSlot slot)
    {
        if (FittingReadOnly)
        {
            DetailStatus = "This fitting is known from assets and cannot be edited.";
            return;
        }

        if (Selected is not { } row) return;

        PickingSlot  = slot;
        ModuleFilter = "";
        SelectedModule = null;
        PickerOpen   = true;

        _allModuleOptions = await _fittingOptions.OptionsAsync(slot.Band, (int)row.TypeId);
        ApplyModuleFilter();

        DetailStatus = _allModuleOptions.Count == 0
            ? $"Nothing fits a {slot.Band} slot on this hull."
            : $"{_allModuleOptions.Count} module(s) fit this slot.";
    }

    private async Task FitSelectedModuleAsync()
    {
        if (PickingSlot is not { } slot || SelectedModule is not { } module) return;
        if (Selected is not { } row) return;

        await WriteSlotAsync(row.StructureId, slot, module.TypeId);
        DetailStatus = $"Fitted {module.Name}.";
    }

    private async Task ClearSlotAsync()
    {
        if (PickingSlot is not { } slot || Selected is not { } row) return;

        await WriteSlotAsync(row.StructureId, slot, 0);
        DetailStatus = "Slot cleared.";
    }

    /// <summary>
    /// Records one slot. A type id of 0 empties it.
    ///
    /// <para>Written straight through rather than batched behind a Save: a fitting is a set of
    /// small independent facts, and a half-applied one is not a state worth being able to reach.</para>
    /// </summary>
    private async Task WriteSlotAsync(
        long structureId, EveConsole.Controls.FittingSlot slot, int typeId)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var band     = slot.Band.ToString();
            var existing = await db.StructureFittings
                .FirstOrDefaultAsync(f => f.StructureId == structureId
                                       && f.Band == band
                                       && f.SlotIndex == slot.Index);

            if (typeId == 0)
            {
                if (existing is not null) db.StructureFittings.Remove(existing);
            }
            else if (existing is null)
            {
                db.StructureFittings.Add(new StructureFitting
                {
                    StructureId = structureId,
                    Band        = band,
                    SlotIndex   = slot.Index,
                    TypeId      = typeId,
                });
            }
            else
            {
                existing.TypeId = typeId;
            }

            await db.SaveChangesAsync();

            PickerOpen  = false;
            PickingSlot = null;

            // Rebuild so the ring shows the change, including its icon.
            await LoadDetailAsync(Selected);
        }
        catch (Exception ex) { DetailStatus = $"Could not record that module: {ex.Message}"; }
    }

    private Avalonia.Media.Imaging.Bitmap? _typeRender;
    /// <summary>Render of the structure's hull, refreshed whenever the selected type changes.</summary>
    public Avalonia.Media.Imaging.Bitmap? TypeRender
    {
        get => _typeRender;
        private set => this.RaiseAndSetIfChanged(ref _typeRender, value);
    }

    private string _detailStatus = "";
    public string DetailStatus { get => _detailStatus; private set => this.RaiseAndSetIfChanged(ref _detailStatus, value); }

    private string _status = "";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _busy;
    public bool Busy { get => _busy; set => this.RaiseAndSetIfChanged(ref _busy, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand   { get; }
    public ReactiveCommand<Unit, Unit> ResolveCommand   { get; }
    public ReactiveCommand<Unit, Unit> ClearFilters     { get; }

    public StructureBrowserViewModel(IDbContextFactory<AppDbContext> dbFactory, EsiPollingService polling,
                                     Api.EsiClient esi, FittingOptionService fittingOptions)
    {
        _dbFactory      = dbFactory;
        _polling        = polling;
        _esi            = esi;
        _fittingOptions = fittingOptions;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        ResolveCommand = ReactiveCommand.CreateFromTask(ResolveAsync);

        SlotClickedCommand = ReactiveCommand.CreateFromTask<EveConsole.Controls.FittingSlot>(OpenSlotPickerAsync);

        FitModuleCommand   = ReactiveCommand.CreateFromTask(FitSelectedModuleAsync);
        ClearSlotCommand   = ReactiveCommand.CreateFromTask(ClearSlotAsync);
        CancelPickerCommand = ReactiveCommand.Create(() =>
        {
            PickerOpen  = false;
            PickingSlot = null;
        });
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
        Cargo.Clear();
        Fuel.Clear();
        Fighters.Clear();
        Jobs.Clear();
        _allJobs = [];

        if (row is null) { DetailStatus = ""; Provenance = ""; TypeRender = null; return; }

        _ = LoadTypeRenderAsync(row.TypeId);

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

            await BuildFittingSlotsAsync(db, row, typeNames);

            // ── Assets stored here ───────────────────────────────────────────
            // ⚠️ RootLocationId, not LocationId. An item inside a container has the CONTAINER as
            // its LocationId, so matching on that alone shows only what sits loose in the
            // structure and silently hides everything packed away. RootLocationId is the
            // structure however deep the item is nested.
            var assets = await db.EsiAssets.AsNoTracking()
                .Where(a => (a.RootLocationId == row.StructureId || a.LocationId == row.StructureId)
                            && !a.LocationFlag.Contains("Slot"))
                .Select(a => new { a.ItemId, a.TypeId, a.Quantity, a.LocationId, a.LocationFlag,
                                   a.OwnerId, a.OwnerType })
                .Take(5000)
                .ToListAsync();

            // Containers are themselves assets, so their name comes from the same list.
            var containerTypes = assets.ToDictionary(a => a.ItemId, a => a.TypeId);

            // Owner names rather than "character"/"corporation", which said nothing useful.
            var charNames = await db.Characters.AsNoTracking()
                .ToDictionaryAsync(c => (long)c.Id, c => c.Name);
            var corpNames = await db.Corporations.AsNoTracking()
                .ToDictionaryAsync(c => (long)c.Id, c => c.Name);

            string OwnerName(long id, string type) => type == "corporation"
                ? corpNames.GetValueOrDefault(id, $"Corp {id}")
                : charNames.GetValueOrDefault(id, $"Character {id}");

            // Corp hangar divisions are named by the corp, and the name is the only thing that
            // says what a division is for — "CorpSAG3" tells you nothing, "Reactions" tells you
            // everything. Keyed by corp because two corps number their divisions differently.
            var divisions = (await db.EsiCorpDivisions.AsNoTracking()
                .Where(d => d.DivisionType == "hangar")
                .ToListAsync())
                .ToDictionary(d => (d.CorporationId, d.Division), d => d.Name);

            string WhereFrom(long ownerId, string ownerType, string flag)
            {
                // CorpSAG1..7 map to hangar divisions 1..7.
                if (ownerType == "corporation" && flag.StartsWith("CorpSAG") &&
                    int.TryParse(flag[7..], out var div) &&
                    divisions.TryGetValue((ownerId, div), out var name) &&
                    name.Length > 0)
                    return $"{name} (div {div})";

                return flag;
            }

            foreach (var a in assets.OrderBy(a => a.LocationFlag))
            {
                var vm = new StructureAssetRow
                {
                    TypeName  = typeNames.GetValueOrDefault(a.TypeId, $"Type {a.TypeId}"),
                    Location  = WhereFrom(a.OwnerId, a.OwnerType, a.LocationFlag),
                    Container = a.LocationId != row.StructureId
                                  && containerTypes.TryGetValue(a.LocationId, out var ct)
                                    ? typeNames.GetValueOrDefault(ct, $"Type {ct}")
                                    : "",
                    Quantity  = a.Quantity,
                    Owner     = OwnerName(a.OwnerId, a.OwnerType),
                };

                // ⚠️ The office itself is skipped, not excluded from the query. It is a container
                // other corps rent and store things in, so it has to stay in the lookup above for
                // their items to name it — but listing the empty folder as though it were stock
                // is noise.
                if (a.LocationFlag == "OfficeFolder") continue;

                // ⚠️ The dedicated tabs describe the STRUCTURE's own bays, so they take only items
                // sitting directly on it. A docked ship's cargo carries the flag "Cargo" too, and
                // its RootLocationId is this structure — so without this test every ship's hold
                // emptied itself onto the Cargo tab and the structure's own bay was lost in it.
                var onTheStructure = a.LocationId == row.StructureId;

                if (onTheStructure && a.LocationFlag == "StructureFuel")   Fuel.Add(vm);
                else if (onTheStructure && (a.LocationFlag == "Cargo"
                                         || a.LocationFlag == "QuantumCoreRoom"))
                    // The quantum core is one item and does not warrant a tab of its own, but it
                    // is part of what the structure is carrying, so it belongs with the cargo.
                    Cargo.Add(vm);
                else if (onTheStructure && (a.LocationFlag == "FighterBay"
                                         || a.LocationFlag.StartsWith("FighterTube")))
                    Fighters.Add(vm);
                else
                    Assets.Add(vm);
            }

            // ── Industry jobs run here ───────────────────────────────────────
            // ⚠️ Ordered in memory. EF Core's SQLite provider cannot translate a DateTimeOffset
            // into an ORDER BY and throws rather than degrading — the same trap that bites any
            // date comparison against these tables. Jobs at one structure are few enough that
            // fetching them all and sorting here costs nothing.
            // ⚠️ FacilityId, not StationId. StationId is 0 on every row we hold — measured across
            // 1,470 jobs, FacilityId matched a structure 1,320 times and StationId never once.
            var jobs = (await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => j.FacilityId == row.StructureId)
                .Select(j => new { j.ActivityId, j.ProductTypeId, j.Runs, j.Status, j.EndDate })
                .ToListAsync())
                .OrderByDescending(j => j.EndDate)
                .Take(500)
                .ToList();

            _allJobs = jobs.Select(j => new StructureJobRow
            {
                Activity = ActivityName(j.ActivityId),
                // Nullable: research and copying jobs produce no item type.
                Product  = j.ProductTypeId is { } pid
                             ? typeNames.GetValueOrDefault(pid, $"Type {pid}")
                             : "—",
                Runs     = j.Runs,
                Status   = j.Status,
                EndDate  = j.EndDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                EndsAt   = j.EndDate.ToLocalTime().DateTime,
            }).ToList();

            // Statuses actually present, so the dropdown never offers one that matches nothing.
            var statuses = _allJobs.Select(j => j.Status).Distinct()
                                   .OrderBy(x => x).ToList();
            JobStatuses.Clear();
            JobStatuses.Add("All");
            foreach (var st in statuses) JobStatuses.Add(st);
            if (!JobStatuses.Contains(JobStatus)) JobStatus = "All";

            ApplyJobFilters();

            DetailStatus =
                $"{Fitting.Count} fitted · {Assets.Count:N0} stored · {Cargo.Count:N0} cargo · " +
                $"{Fuel.Count:N0} fuel · {Fighters.Count:N0} fighters · {_allJobs.Count:N0} job(s)";
        }
        catch (Exception ex) { DetailStatus = $"Detail load failed: {ex.Message}"; }
    }

    // Dogma attributes that carry slot counts. Populated identically for hulls and structures,
    // which is what lets the fitting control serve both without knowing about either.
    private const int AttrLowSlots     = 12;
    private const int AttrMedSlots     = 13;
    private const int AttrHiSlots      = 14;
    private const int AttrRigSlots     = 1137;
    private const int AttrServiceSlots = 2056;

    /// <summary>
    /// Builds every slot the hull has — filled from what we know, empty where we do not.
    ///
    /// <para>Capacity comes from the type's dogma attributes rather than from what happens to be
    /// fitted, so a Fortizar shows five service slots with two filled rather than just the two.
    /// Empty slots are the useful half of a fitting view.</para>
    ///
    /// <para>Assets win where they exist, since they are what the game says is actually fitted.
    /// Hand-entered rigs and service modules fill slots assets did not cover, which is the whole
    /// arrangement for structures we do not own.</para>
    /// </summary>
    private async Task BuildFittingSlotsAsync(
        AppDbContext db, StructureRow row, Dictionary<int, string> typeNames)
    {
        if (row.TypeId <= 0) { FittingSlots = null; return; }

        var attrs = await db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => a.TypeId == row.TypeId)
            .ToDictionaryAsync(a => a.AttributeId, a => (int)a.Value);

        int Cap(int attr) => attrs.GetValueOrDefault(attr, 0);

        // Fitted, from assets: LocationFlag names the band and the index.
        var fitted = await db.EsiAssets.AsNoTracking()
            .Where(a => a.LocationId == row.StructureId && a.LocationFlag.Contains("Slot"))
            .Select(a => new { a.LocationFlag, a.TypeId })
            .ToListAsync();

        var byFlag = fitted
            .GroupBy(f => f.LocationFlag)
            .ToDictionary(g => g.Key, g => g.First().TypeId);

        // Hand-entered, for the structures assets never reach. Keyed by band and slot, so a typed
        // module lands back in the slot it was put in rather than the first free one.
        var hand = (await db.StructureFittings.AsNoTracking()
                .Where(f => f.StructureId == row.StructureId).ToListAsync())
            .ToDictionary(f => (f.Band, f.SlotIndex), f => f.TypeId);

        var slots = new List<EveConsole.Controls.FittingSlot>();
        var wanted = new List<int>();

        void Band(EveConsole.Controls.FittingBand band, string flagPrefix, int count,
                  Func<int, int> manual)
        {
            for (var i = 0; i < count; i++)
            {
                var flag       = $"{flagPrefix}{i}";
                var fromAssets = byFlag.TryGetValue(flag, out var assetType);
                var typeId     = fromAssets ? assetType : manual(i);

                if (typeId > 0) wanted.Add(typeId);

                slots.Add(new EveConsole.Controls.FittingSlot(
                    band, i, typeId,
                    typeId > 0 ? typeNames.GetValueOrDefault(typeId, $"Type {typeId}") : "",
                    Icon: null,
                    FromAssets: fromAssets));
            }
        }

        int Hand(EveConsole.Controls.FittingBand band, int slot) =>
            hand.GetValueOrDefault((band.ToString(), slot));

        Band(EveConsole.Controls.FittingBand.High, "HiSlot",  Cap(AttrHiSlots),
             i => Hand(EveConsole.Controls.FittingBand.High, i));
        Band(EveConsole.Controls.FittingBand.Mid,  "MedSlot", Cap(AttrMedSlots),
             i => Hand(EveConsole.Controls.FittingBand.Mid, i));
        Band(EveConsole.Controls.FittingBand.Low,  "LoSlot",  Cap(AttrLowSlots),
             i => Hand(EveConsole.Controls.FittingBand.Low, i));
        Band(EveConsole.Controls.FittingBand.Rig,  "RigSlot", Cap(AttrRigSlots),
             i => Hand(EveConsole.Controls.FittingBand.Rig, i));
        Band(EveConsole.Controls.FittingBand.Service, "ServiceSlot", Cap(AttrServiceSlots),
             i => Hand(EveConsole.Controls.FittingBand.Service, i));

        // Show the ring immediately, then fill the icons in. Waiting on a dozen HTTP fetches
        // before drawing anything would make selecting a structure feel broken.
        FittingSlots = slots;

        var icons = await LoadIconsAsync(wanted.Distinct().ToList());
        if (icons.Count == 0) return;

        // Records are immutable, so the list is rebuilt with the icons attached — which also
        // gives the control a new reference to notice.
        FittingSlots = slots
            .Select(s => s.TypeId > 0 && icons.TryGetValue(s.TypeId, out var bmp)
                           ? s with { Icon = bmp }
                           : s)
            .ToList();
    }

    /// <summary>Module icons, cached across selections — the same modules recur constantly.</summary>
    private static readonly Dictionary<int, Avalonia.Media.Imaging.Bitmap?> _iconCache = new();

    private static async Task<Dictionary<int, Avalonia.Media.Imaging.Bitmap>> LoadIconsAsync(
        IReadOnlyList<int> typeIds)
    {
        var result = new Dictionary<int, Avalonia.Media.Imaging.Bitmap>();

        foreach (var id in typeIds)
        {
            if (_iconCache.TryGetValue(id, out var cached))
            {
                if (cached is not null) result[id] = cached;
                continue;
            }

            try
            {
                var bytes = await _renderHttp.GetByteArrayAsync(
                    $"https://images.evetech.net/types/{id}/icon?size=64");

                using var ms = new MemoryStream(bytes);
                var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
                _iconCache[id] = bmp;
                result[id] = bmp;
            }
            catch
            {
                // A missing icon leaves an empty box, which is honest — better than a broken
                // image or an error for something purely decorative.
                _iconCache[id] = null;
            }
        }

        return result;
    }

    /// <summary>Renders of structure hulls, kept for the session — they are a few hundred KB each
    /// and the same handful of types recur constantly as the user moves down the list.</summary>
    private static readonly Dictionary<int, Avalonia.Media.Imaging.Bitmap?> _renderCache = new();
    private static readonly HttpClient _renderHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Fetches the hull render for a structure type. Follows the selected row rather than the
    /// stored type, so changing the type on the Details tab changes the picture with it.
    /// </summary>
    private async Task LoadTypeRenderAsync(long typeId)
    {
        if (typeId <= 0) { TypeRender = null; return; }

        var id = (int)typeId;
        if (_renderCache.TryGetValue(id, out var cached)) { TypeRender = cached; return; }

        try
        {
            var bytes = await _renderHttp.GetByteArrayAsync(
                $"https://images.evetech.net/types/{id}/render?size=256");

            using var ms = new MemoryStream(bytes);
            var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
            _renderCache[id] = bmp;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => TypeRender = bmp);
        }
        catch
        {
            // Not every type has a render, and a missing picture is not worth a visible error.
            _renderCache[id] = null;
            TypeRender = null;
        }
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
