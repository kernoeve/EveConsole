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

/// <summary>
/// One choice in the system or type picker. <see cref="Label"/> is both what is shown and what
/// the box matches typing against; <see cref="Id"/> is what gets saved, so picking "Jita — The
/// Forge" stores 30000142 rather than a string the next reader would have to resolve again.
/// </summary>
public sealed record PickOption(int Id, string Label)
{
    public override string ToString() => Label;
}

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

    /// <summary>Grouped for reading — a fuel bay holds hundreds of thousands of blocks, and
    /// bare digits at that length take a moment to size up. The column sorts on
    /// <see cref="Quantity"/> so the display format cannot turn the ordering alphabetical.</summary>
    public string QuantityText => Quantity.ToString("N0");
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
    private readonly AppPreferencesService           _prefs;
    private readonly IndyStructureLinkService        _indyLink;

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
    /// Off on a first run: the unresolved rows outnumber the readable ones by roughly two to one,
    /// so showing them by default buries the structures the user came to look at. They stay one
    /// click away because a structure ESI will not describe is exactly the one worth filling in
    /// by hand — which is also why the choice is remembered: someone doing that work should not
    /// have to re-tick the box every time the app starts.
    /// </summary>
    private bool _showUnknown;
    public bool ShowUnknown
    {
        get => _showUnknown;
        set
        {
            this.RaiseAndSetIfChanged(ref _showUnknown, value);
            ApplyFilters();
            _ = _prefs.SetBoolAsync(ShowUnknownKey, value);
        }
    }

    private const string ShowUnknownKey = "structures.show_unknown";

    // ── Selected structure (the viewer below the list) ───────────────────────

    private StructureRow? _selected;
    public StructureRow? Selected
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(CanEditIdentity));
            this.RaisePropertyChanged(nameof(EditLockReason));
            _ = LoadDetailAsync(value);
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>
    /// Whether identity — name, system, type — may be hand-edited.
    ///
    /// <para>⚠️ False as soon as ESI can describe the structure. Those three fields are exactly
    /// the ones the next resolve will overwrite, so allowing an edit here would offer a change
    /// that silently reverts within the hour. Editing is for the structures ESI refuses us: a 403
    /// or a 404 means nothing will ever come back to contradict what the user types.</para>
    ///
    /// <para>Notes is deliberately outside this gate — ESI has no opinion about it, so there is
    /// nothing for a hand-written note to fight with.</para>
    /// </summary>
    public bool CanEditIdentity => _selected is not null && !_selected.IsKnown;

    /// <summary>Explains the locked fields rather than leaving them mysteriously greyed.</summary>
    public string EditLockReason => _selected is null
        ? ""
        : _selected.IsKnown
            ? "Name, system and type come from ESI and cannot be edited — the next resolve would overwrite them."
            : "";

    // Editable copies. Held apart from the row so cancelling is just a reload and a half-typed
    // edit never leaks into the list.
    private string _editName = "";
    public string EditName { get => _editName; set => this.RaiseAndSetIfChanged(ref _editName, value); }

    private string _editNotes = "";
    public string EditNotes { get => _editNotes; set => this.RaiseAndSetIfChanged(ref _editNotes, value); }

    // ── System / type pickers ────────────────────────────────────────────────
    // Both are chosen from the SDE rather than typed: the row stores ids, and a free-text field
    // would have to guess which system "Jita" meant on every save.

    /// <summary>
    /// Every system, held in the view model and never handed to a control wholesale.
    ///
    /// <para>⚠️ New Eden has ~8,500 systems. The first version filled a bound ObservableCollection
    /// one item at a time — 8,500 notifications, each of which the AutoCompleteBox reacted to, all
    /// on the UI thread — and then gave the control all 8,500 to lay out. That froze the app on
    /// the first selection and eventually took it down. The box is fed through
    /// <see cref="SystemPopulator"/> instead, which never returns more than a screenful.</para>
    /// </summary>
    private List<PickOption> _systemOptions = [];
    public List<PickOption> SystemOptions
    {
        get => _systemOptions;
        private set => this.RaiseAndSetIfChanged(ref _systemOptions, value);
    }

    /// <summary>How many matches the system box will show at once. Anyone who wants a particular
    /// system types more of its name; nobody scrolls a thousand rows to find one.</summary>
    private const int SystemMatchLimit = 50;

    /// <summary>
    /// Feeds the system box only what matches what has been typed.
    ///
    /// <para>Prefix matches first: typing "Jit" wants Jita, not the first alphabetical system that
    /// happens to contain those letters somewhere.</para>
    /// </summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> SystemPopulator => (text, _) =>
    {
        var needle = (text ?? "").Trim();
        if (needle.Length == 0)
            return Task.FromResult<IEnumerable<object>>([]);

        var matches = SystemOptions
            .Where(o => o.Label.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Label.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(o => o.Label, StringComparer.OrdinalIgnoreCase)
            .Take(SystemMatchLimit)
            .Cast<object>()
            .ToList();

        return Task.FromResult<IEnumerable<object>>(matches);
    };

    private List<PickOption> _typeOptions = [];
    public List<PickOption> TypeOptions
    {
        get => _typeOptions;
        private set => this.RaiseAndSetIfChanged(ref _typeOptions, value);
    }

    /// <summary>Started once at construction, awaited by the first detail load. Held as a task so
    /// two quick selections cannot both decide to load them.</summary>
    private Task? _optionsTask;

    private PickOption? _editSystem;
    public PickOption? EditSystem { get => _editSystem; set => this.RaiseAndSetIfChanged(ref _editSystem, value); }

    /// <summary>
    /// The literal text in the system box, which the selection alone cannot express.
    ///
    /// <para>An AutoCompleteBox drops its selection the moment you type, so a null
    /// <see cref="EditSystem"/> means either "cleared" or "half-way through typing Jita" — and
    /// those want opposite outcomes on save. The text tells them apart: empty is a deliberate
    /// clear, non-empty with no selection is an unfinished edit worth refusing.</para>
    /// </summary>
    private string _editSystemText = "";
    public string EditSystemText
    {
        get => _editSystemText;
        set => this.RaiseAndSetIfChanged(ref _editSystemText, value);
    }

    private PickOption? _editType;
    public PickOption? EditType
    {
        get => _editType;
        set
        {
            this.RaiseAndSetIfChanged(ref _editType, value);
            // The render follows the picker, so a mis-picked type is obvious before saving —
            // and clearing the type empties the frame rather than leaving the old hull sitting
            // there next to an empty box.
            _ = LoadTypeRenderAsync(value?.Id ?? 0);
        }
    }

    private string _provenance = "";
    /// <summary>Who last wrote this row and when — the reason UpdatedBy exists.</summary>
    public string Provenance { get => _provenance; private set => this.RaiseAndSetIfChanged(ref _provenance, value); }

    private string _eveRefNote = "";
    /// <summary>
    /// Says when a field came from EVE Ref rather than ESI.
    ///
    /// <para>⚠️ Shown because these two are not the same kind of fact. EVE Ref is a third party's
    /// observation, assembled by people whose characters can dock where ours cannot — it can be
    /// months stale, and the structure may have been renamed or destroyed since, possibly by us.
    /// Presenting it in the same grey as a value ESI returned would be claiming a confidence we
    /// do not have.</para>
    /// </summary>
    public string EveRefNote { get => _eveRefNote; private set => this.RaiseAndSetIfChanged(ref _eveRefNote, value); }

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

            // Carry it through to any Indy Parks entry linked to this structure. Only rigs and
            // service modules exist on that side, and the link service ignores the push entirely
            // when assets describe this structure — in which case this edit could not have
            // happened, since the fitting is read-only then.
            await _indyLink.PushFromRealAsync(structureId);

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

    // ── Add by location ID ───────────────────────────────────────────────────
    // Two steps, look then commit. A structure normally arrives by being seen in assets, jobs,
    // contracts or kills, so every id in the table has already been proven to exist; a typed one
    // has not, and there is no way to remove a row once it is in. Showing what the id turns out
    // to be before accepting it is what stops a mistyped digit becoming permanent.

    private bool _addOpen;
    public bool AddOpen { get => _addOpen; set => this.RaiseAndSetIfChanged(ref _addOpen, value); }

    private string _addIdText = "";
    public string AddIdText
    {
        get => _addIdText;
        set
        {
            this.RaiseAndSetIfChanged(ref _addIdText, value);
            // Editing the id invalidates a preview of the previous one — otherwise Add could
            // commit an id the panel is no longer describing.
            ClearAddPreview();
        }
    }

    private string _addStatus = "";
    public string AddStatus { get => _addStatus; private set => this.RaiseAndSetIfChanged(ref _addStatus, value); }

    private string _addPreview = "";
    /// <summary>What ESI said about the looked-up id, or why it said nothing.</summary>
    public string AddPreview { get => _addPreview; private set => this.RaiseAndSetIfChanged(ref _addPreview, value); }

    private long _addLookedUpId;
    private bool _addCanCommit;
    /// <summary>Only true once a look-up has run for the id currently typed.</summary>
    public bool AddCanCommit { get => _addCanCommit; private set => this.RaiseAndSetIfChanged(ref _addCanCommit, value); }

    private void ClearAddPreview()
    {
        AddPreview      = "";
        AddCanCommit    = false;
        _addLookedUpId  = 0;
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand    { get; }
    public ReactiveCommand<Unit, Unit> ResolveCommand    { get; }
    public ReactiveCommand<Unit, Unit> ClearFilters      { get; }
    public ReactiveCommand<Unit, Unit> PullFromEsiCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenAddCommand   { get; }
    public ReactiveCommand<Unit, Unit> LookupAddCommand { get; }
    public ReactiveCommand<Unit, Unit> CommitAddCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAddCommand { get; }

    public StructureBrowserViewModel(IDbContextFactory<AppDbContext> dbFactory, EsiPollingService polling,
                                     Api.EsiClient esi, FittingOptionService fittingOptions,
                                     AppPreferencesService prefs, IndyStructureLinkService indyLink)
    {
        _dbFactory      = dbFactory;
        _polling        = polling;
        _esi            = esi;
        _fittingOptions = fittingOptions;
        _prefs          = prefs;
        _indyLink       = indyLink;

        // Straight to the backing field: the setter persists, and going through it here would
        // write the stored value back over itself on every startup.
        _showUnknown = _prefs.GetBool(ShowUnknownKey, false);

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        ResolveCommand = ReactiveCommand.CreateFromTask(ResolveAsync);

        PullFromEsiCommand = ReactiveCommand.CreateFromTask(PullFromEsiAsync);

        OpenAddCommand   = ReactiveCommand.Create(() =>
        {
            AddIdText = "";
            ClearAddPreview();
            AddStatus = "";
            AddOpen   = true;
        });
        LookupAddCommand = ReactiveCommand.CreateFromTask(LookupAddAsync);
        CommitAddCommand = ReactiveCommand.CreateFromTask(CommitAddAsync);
        CancelAddCommand = ReactiveCommand.Create(() => { AddOpen = false; });

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

        // Started now rather than on the first selection. It is the same work either way, but at
        // construction nothing is waiting on it, whereas on a click the user is.
        _ = LoadPickOptionsAsync();
    }

    /// <summary>
    /// Bumped on every selection. Work that outlives the click it belongs to — icon downloads —
    /// compares against it before publishing, so a slow fetch cannot decorate the ring of a
    /// structure the user has since moved off.
    /// </summary>
    private int _detailSeq;

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
        _detailSeq++;

        Fitting.Clear();
        Assets.Clear();
        Cargo.Clear();
        Fuel.Clear();
        Fighters.Clear();
        Jobs.Clear();
        _allJobs = [];

        if (row is null) { DetailStatus = ""; Provenance = ""; TypeRender = null; return; }

        _ = LoadTypeRenderAsync(row.TypeId);
        await LoadPickOptionsAsync();

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var s = await db.Structures.AsNoTracking()
                .FirstOrDefaultAsync(x => x.StructureId == row.StructureId);

            EditName   = s?.Name  ?? "";
            EditNotes  = s?.Notes ?? "";

            // Assigned through the backing fields: the EditType setter reloads the render, which
            // is wasted work when the load above is already fetching the same image.
            //
            // A stored id missing from its list gets an entry made for it. Otherwise the box would
            // show blank and saving would write that blank back — an unpublished hull, or a system
            // predating an SDE import, would be quietly erased by opening the tab and pressing Save.
            _editSystem = row.SystemId == 0
                ? null
                : Ensure(SystemOptions, v => SystemOptions = v, (int)row.SystemId, row.SystemName);
            _editType = row.TypeId == 0
                ? NoType
                : Ensure(TypeOptions, v => TypeOptions = v, (int)row.TypeId, row.TypeName);

            _editSystemText = _editSystem?.Label ?? "";
            this.RaisePropertyChanged(nameof(EditSystem));
            this.RaisePropertyChanged(nameof(EditSystemText));
            this.RaisePropertyChanged(nameof(EditType));

            Provenance = s is null
                ? ""
                : s.UpdatedAt == default
                    ? $"Never written · {s.UpdatedBy}"
                    : $"Last written by {s.UpdatedBy} at {s.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";

            EveRefNote = await BuildEveRefNoteAsync(db, row, s);

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

            // Hand-entered fittings, for the slots assets did not cover. Named with the same
            // LocationFlag the game uses so the list reads identically whichever side a row came
            // from — the Source column is what distinguishes them, not the slot label.
            var seen = Fitting.Select(f => f.Slot).ToHashSet();

            foreach (var f in await db.StructureFittings.AsNoTracking()
                         .Where(f => f.StructureId == row.StructureId).ToListAsync())
            {
                var slot = $"{BandFlagPrefix(f.Band)}{f.SlotIndex}";
                if (f.TypeId <= 0 || !seen.Add(slot)) continue;

                Fitting.Add(new FittingRow
                {
                    Slot     = slot,
                    TypeId   = f.TypeId,
                    TypeName = typeNames.GetValueOrDefault(f.TypeId, $"Type {f.TypeId}"),
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

            // Containers are themselves assets, so both their names and their place in the chain
            // come from this same list.
            var byItem = assets.ToDictionary(a => a.ItemId);

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

            // The division a CorpSAG flag names, or null when the flag is not one.
            string? DivisionName(long ownerId, string ownerType, string flag)
            {
                // CorpSAG1..7 map to hangar divisions 1..7. CorporationGoalDeliveries also lives
                // under the office and is not one of them, so the digit has to parse.
                if (ownerType != "corporation" || !flag.StartsWith("CorpSAG") ||
                    !int.TryParse(flag[7..], out var div))
                    return null;

                return divisions.TryGetValue((ownerId, div), out var name) && name.Length > 0
                    ? name
                    : $"Division {div}";
            }

            // The flag column keeps the division number alongside the name: this column is about
            // where in the hangar something sits, and the number is how the game labels the tab.
            string WhereFrom(long ownerId, string ownerType, string flag)
            {
                if (ownerType != "corporation" || !flag.StartsWith("CorpSAG") ||
                    !int.TryParse(flag[7..], out var div))
                    return flag;

                return divisions.TryGetValue((ownerId, div), out var name) && name.Length > 0
                    ? $"{name} (div {div})"
                    : flag;
            }

            // The full chain from the structure down to whatever holds this item, matching the
            // Assets tool rather than naming only the immediate container — an item can be several
            // layers deep, and "Station Container" alone does not say which of them.
            //
            // ⚠️ A corp hangar division is not an item and owns no hop of its own: it is the
            // LocationFlag of whatever sits directly in the office. So the division is emitted when
            // the flag being carried up is a CorpSAG one, which is exactly when the parent just
            // added is the office — that is what puts it between the two.
            string ContainerPath(long locationId, string flag, long ownerId, string ownerType)
            {
                var parts = new List<string>();
                var id    = locationId;

                // Bounded rather than while(true): a cycle in the asset graph would otherwise hang
                // the load, and nothing legitimately nests anywhere near this deep.
                for (int hop = 0; hop < 8 && id != row.StructureId; hop++)
                {
                    if (!byItem.TryGetValue(id, out var parent)) break;

                    parts.Insert(0, typeNames.GetValueOrDefault(parent.TypeId, $"Type {parent.TypeId}"));
                    if (DivisionName(ownerId, ownerType, flag) is { } division)
                        parts.Insert(1, division);

                    flag = parent.LocationFlag;
                    id   = parent.LocationId;
                }

                return string.Join(" > ", parts);
            }

            foreach (var a in assets.OrderBy(a => a.LocationFlag))
            {
                var vm = new StructureAssetRow
                {
                    TypeName  = typeNames.GetValueOrDefault(a.TypeId, $"Type {a.TypeId}"),
                    Location  = WhereFrom(a.OwnerId, a.OwnerType, a.LocationFlag),
                    Container = ContainerPath(a.LocationId, a.LocationFlag, a.OwnerId, a.OwnerType),
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
    /// <summary>
    /// The LocationFlag prefix the game uses for each band. Shared by the ring and the list so the
    /// two cannot disagree about what a slot is called — they are read side by side, and a hand
    /// entry labelled differently from an asset one would read as a different kind of thing.
    /// </summary>
    private static string BandFlagPrefix(EveConsole.Controls.FittingBand band) => band switch
    {
        EveConsole.Controls.FittingBand.High    => "HiSlot",
        EveConsole.Controls.FittingBand.Mid     => "MedSlot",
        EveConsole.Controls.FittingBand.Low     => "LoSlot",
        EveConsole.Controls.FittingBand.Rig     => "RigSlot",
        EveConsole.Controls.FittingBand.Service => "ServiceSlot",
        _                                       => band.ToString(),
    };

    /// <summary>As above, from the stored band name. Falls back to the raw text if it no longer
    /// parses, so a row written by an older build still shows rather than vanishing.</summary>
    private static string BandFlagPrefix(string band) =>
        Enum.TryParse<EveConsole.Controls.FittingBand>(band, out var b) ? BandFlagPrefix(b) : band;

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

        void Band(EveConsole.Controls.FittingBand band, int count, Func<int, int> manual)
        {
            for (var i = 0; i < count; i++)
            {
                var flag       = $"{BandFlagPrefix(band)}{i}";
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

        Band(EveConsole.Controls.FittingBand.High,    Cap(AttrHiSlots),
             i => Hand(EveConsole.Controls.FittingBand.High, i));
        Band(EveConsole.Controls.FittingBand.Mid,     Cap(AttrMedSlots),
             i => Hand(EveConsole.Controls.FittingBand.Mid, i));
        Band(EveConsole.Controls.FittingBand.Low,     Cap(AttrLowSlots),
             i => Hand(EveConsole.Controls.FittingBand.Low, i));
        Band(EveConsole.Controls.FittingBand.Rig,     Cap(AttrRigSlots),
             i => Hand(EveConsole.Controls.FittingBand.Rig, i));
        Band(EveConsole.Controls.FittingBand.Service, Cap(AttrServiceSlots),
             i => Hand(EveConsole.Controls.FittingBand.Service, i));

        // Show the ring immediately, then fill the icons in.
        FittingSlots = slots;

        // ⚠️ Deliberately not awaited, which is what the line above was always meant to mean.
        // Awaiting it made the caller wait too, so the assets, cargo and job tabs sat empty until
        // every module icon had come down — the first selection of a session pays for all of them
        // at once, which is exactly when the viewer looked hung.
        _ = FillFittingIconsAsync(slots, wanted.Distinct().ToList(), _detailSeq);
    }

    /// <summary>
    /// Attaches module icons to the ring once they arrive.
    ///
    /// <para>⚠️ Checks the sequence it was started with before publishing. Without it, clicking
    /// down the list faster than the images download would let an earlier structure's icons land
    /// on a later structure's ring.</para>
    /// </summary>
    private async Task FillFittingIconsAsync(
        List<EveConsole.Controls.FittingSlot> slots, List<int> wanted, int seq)
    {
        var icons = await LoadIconsAsync(wanted);
        if (icons.Count == 0 || seq != _detailSeq) return;

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

    /// <summary>
    /// Fetches whatever icons are not already cached.
    ///
    /// <para>⚠️ In parallel, not one after another. A full rack is a dozen or more modules, and
    /// serially every one of them could spend the whole HTTP timeout before the next even started
    /// — which is how a single unreachable image server turned one click into minutes rather than
    /// seconds. Concurrently the worst case is one timeout, whatever the slot count.</para>
    /// </summary>
    private static async Task<Dictionary<int, Avalonia.Media.Imaging.Bitmap>> LoadIconsAsync(
        IReadOnlyList<int> typeIds)
    {
        var result  = new Dictionary<int, Avalonia.Media.Imaging.Bitmap>();
        var missing = new List<int>();

        foreach (var id in typeIds)
        {
            if (_iconCache.TryGetValue(id, out var cached))
            {
                if (cached is not null) result[id] = cached;
            }
            else missing.Add(id);
        }

        if (missing.Count == 0) return result;

        var fetched = await Task.WhenAll(missing.Distinct().Select(async id =>
        {
            try
            {
                var bytes = await _renderHttp.GetByteArrayAsync(
                    $"https://images.evetech.net/types/{id}/icon?size=64");

                using var ms = new MemoryStream(bytes);
                return (id, bmp: (Avalonia.Media.Imaging.Bitmap?)new Avalonia.Media.Imaging.Bitmap(ms));
            }
            catch
            {
                // A missing icon leaves an empty box, which is honest — better than a broken
                // image or an error for something purely decorative.
                return (id, bmp: (Avalonia.Media.Imaging.Bitmap?)null);
            }
        }));

        // Written here rather than inside the tasks: the cache is a plain dictionary, and this
        // continuation is the one place they are all back on a single thread.
        foreach (var (id, bmp) in fetched)
        {
            _iconCache[id] = bmp;
            if (bmp is not null) result[id] = bmp;
        }

        return result;
    }

    /// <summary>Renders of structure hulls, kept for the session — they are a few hundred KB each
    /// and the same handful of types recur constantly as the user moves down the list.</summary>
    private static readonly Dictionary<int, Avalonia.Media.Imaging.Bitmap?> _renderCache = new();
    /// <summary>
    /// ⚠️ A short timeout on purpose. These are decorative 64px icons and 256px renders from a
    /// CDN: if one has not arrived in five seconds it is not coming, and waiting longer buys
    /// nothing but a frozen-looking viewer. Failures are cached as null, so an unreachable image
    /// server costs one wait per type per session rather than one per click.
    /// </summary>
    private static readonly HttpClient _renderHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

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
    /// Names the fields on this row that match EVE Ref's snapshot rather than anything ESI said.
    ///
    /// <para>⚠️ Only for structures ESI will not describe. On a resolved structure ESI's own
    /// answer filled these fields and EVE Ref merely agrees, so saying so would be noise on the
    /// 1,500 rows where it does not matter.</para>
    ///
    /// <para>Compares values rather than tracking who wrote what: the import only ever fills
    /// blanks and never stamps UpdatedBy, so a match against a field ESI left empty is exactly the
    /// evidence that EVE Ref is where it came from.</para>
    /// </summary>
    private static async Task<string> BuildEveRefNoteAsync(
        AppDbContext db, StructureRow row, Structure? s)
    {
        if (s is null || row.IsKnown) return "";

        var t = await db.EveRefStructures.AsNoTracking()
            .FirstOrDefaultAsync(x => x.StructureId == row.StructureId);
        if (t is null) return "";

        var fields = new List<string>();
        if (t.Name.Length > 0   && s.Name == t.Name)                   fields.Add("name");
        if (t.SolarSystemId > 0 && s.SolarSystemId == t.SolarSystemId) fields.Add("system");
        if (t.TypeId > 0        && s.TypeId == t.TypeId)               fields.Add("type");
        if (t.OwnerId > 0       && s.OwnerId == t.OwnerId)             fields.Add("owner");

        if (fields.Count == 0) return "";

        var seen = t.FetchedAt == default ? "" : $", read {t.FetchedAt.ToLocalTime():yyyy-MM-dd}";
        return $"The {string.Join(", ", fields)} came from EVE Ref{seen} — a third party's "
             + "observation, not something ESI confirmed. It can be out of date.";
    }

    /// <summary>
    /// Fills the system and type pickers once per session. Systems carry their region, because
    /// New Eden has several same-named-looking systems and the region is how anyone tells the
    /// intended one apart; types are the structure category only, so the list is short enough to
    /// scroll rather than search.
    /// </summary>
    private Task LoadPickOptionsAsync() => _optionsTask ??= BuildPickOptionsAsync();

    private async Task BuildPickOptionsAsync()
    {
        try
        {
            // ⚠️ Off the UI thread. Microsoft.Data.Sqlite has no true async I/O, so every
            // "…Async" here runs synchronously on whichever thread calls it — awaiting them from
            // the selection handler blocks the UI for the whole load rather than yielding.
            var (systems, types) = await Task.Run(async () =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync();

                var regs = await db.SdeRegions.AsNoTracking()
                    .ToDictionaryAsync(r => r.RegionId, r => r.Name);

                var sys = (await db.SdeSolarSystems.AsNoTracking()
                        .Select(s => new { s.SolarSystemId, s.Name, s.RegionId })
                        .ToListAsync())
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(s =>
                    {
                        var region = regs.GetValueOrDefault(s.RegionId, "");
                        return new PickOption(
                            s.SolarSystemId, region.Length > 0 ? $"{s.Name} — {region}" : s.Name);
                    })
                    .ToList();

                // Category 65 is what makes a type a structure, the same test the non-structure
                // purge uses. Unpublished types are test/removed hulls that cannot be anchored.
                var tps = (await db.SdeTypes.AsNoTracking()
                        .Join(db.SdeGroups.AsNoTracking().Where(g => g.CategoryId == StructureCategory),
                              t => t.GroupId, g => g.GroupId, (t, g) => new { t.TypeId, t.Name, t.Published })
                        .Where(t => t.Published)
                        .ToListAsync())
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(t => new PickOption(t.TypeId, t.Name))
                    .ToList();

                // A dropdown has no equivalent of clearing the text, so "unknown" has to be an
                // entry in the list or the field becomes write-once: pick a type by mistake and
                // there is no way back to not knowing.
                tps.Insert(0, NoType);

                return (sys, tps);
            });

            SystemOptions = systems;
            TypeOptions   = types;
        }
        catch (Exception ex) { DetailStatus = $"Could not load pickers: {ex.Message}"; }
    }

    private const int StructureCategory = 65;

    /// <summary>The "no type recorded" row, so the dropdown can express what an empty text box can.</summary>
    private static readonly PickOption NoType = new(0, "— none —");

    /// <summary>
    /// Finds the option for an id, inventing one from the row's own label if the SDE list does not
    /// carry it. The invented entry is what lets an unrecognised id survive a save.
    /// </summary>
    private static PickOption Ensure(List<PickOption> options, Action<List<PickOption>> replace,
                                     int id, string label)
    {
        var found = options.FirstOrDefault(o => o.Id == id);
        if (found is not null) return found;

        var made = new PickOption(id, label.Length > 0 ? label : $"Type {id}");
        // A replaced list, not a mutated one — the bound controls only notice a new reference.
        replace([.. options, made]);
        return made;
    }

    /// <summary>
    /// Writes the edited fields back. Only the app's own table is touched — nothing here can
    /// reach EsiStructureNames, so a hand-typed name cannot be mistaken for something ESI said.
    ///
    /// <para>Identity is written only when <see cref="CanEditIdentity"/> allows it. The check is
    /// repeated here rather than trusted to the disabled controls: a stale selection or a binding
    /// that failed to update would otherwise write a hand-typed system over one ESI gave us.</para>
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

            s.Notes = EditNotes;

            if (CanEditIdentity)
            {
                // Cleared means cleared. The earlier "only write what is set" guard could not tell
                // an emptied box from an untouched one, so clearing a system silently did nothing.
                if (string.IsNullOrWhiteSpace(EditSystemText))
                {
                    s.SolarSystemId = 0;
                }
                else if (EditSystem is not null)
                {
                    s.SolarSystemId = EditSystem.Id;
                }
                else
                {
                    // Text with no selection is a half-finished edit. Refusing is better than
                    // guessing: writing 0 would erase a system the user was mid-way through
                    // retyping, and keeping the old one would ignore what they typed.
                    DetailStatus = "Pick a system from the list, or clear the box to leave it unset.";
                    return;
                }

                s.Name   = EditName.Trim();
                s.TypeId = EditType?.Id ?? 0;
            }

            s.UpdatedBy = StructureSource.User;
            s.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();

            DetailStatus = CanEditIdentity ? "Saved." : "Notes saved (identity comes from ESI).";
            Provenance   = $"Last written by {StructureSource.User} at {DateTimeOffset.Now:yyyy-MM-dd HH:mm}";
            await LoadAsync();
        }
        catch (Exception ex) { DetailStatus = $"Save failed: {ex.Message}"; }
    }

    /// <summary>
    /// Asks ESI about this one structure now, ignoring the thirty-day backoff a 403 normally
    /// earns. That backoff is right for the sweep and wrong here: the user pressing this is
    /// usually saying they just got docking rights, which is the one thing the sweep's timer
    /// cannot know.
    /// </summary>
    private async Task PullFromEsiAsync()
    {
        if (Selected is not { } row) return;

        var id = row.StructureId;
        DetailStatus = "Asking ESI…";
        Busy = true;
        try
        {
            var status = await _polling.RetryStructureAsync(id);

            DetailStatus = status switch
            {
                StructureStatus.Resolved => "Resolved from ESI.",
                StructureStatus.NoAccess => "No access (403) — no docking rights with this structure.",
                StructureStatus.NotFound => "Not found (404) — the structure has been unanchored or destroyed.",
                _                        => "ESI did not answer. See the error log.",
            };

            await LoadAsync();
            // Reselect from the displayed rows, not the unfiltered list: LoadAsync rebuilds every
            // row object, so the old selection now points at an instance the grid has never seen.
            SelectById(id);
        }
        catch (Exception ex) { DetailStatus = $"Pull failed: {ex.Message}"; }
        finally { Busy = false; }
    }

    /// <summary>
    /// Asks ESI what a typed id is, without adding anything yet.
    /// </summary>
    private async Task LookupAddAsync()
    {
        ClearAddPreview();

        // Ids get pasted out of chat and spreadsheets, which add separators and spaces.
        var text = new string(AddIdText.Where(char.IsDigit).ToArray());
        if (!long.TryParse(text, out var id) || id <= 0)
        {
            AddStatus = "Enter a numeric location ID.";
            return;
        }

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            if (await db.Structures.AnyAsync(s => s.StructureId == id))
            {
                AddStatus = "Already in the table.";
                AddOpen   = false;
                SelectById(id);
                return;
            }
        }

        // A warning rather than a refusal. Station ids and the like are far below this, and the
        // lookup will simply 404 — but refusing outright would also block whatever id range CCP
        // decides to use next, and being wrong in that direction is worse.
        var caveat = id < EveIds.PlayerStructureThreshold
            ? " (that is below the player-structure ID range, so a 404 is likely)"
            : "";

        AddStatus = $"Asking ESI about {id}{caveat}…";
        Busy = true;
        try
        {
            var row = await _polling.LookupStructureAsync(id);

            _addLookedUpId = id;
            AddCanCommit   = true;

            if (row is null)
            {
                AddPreview = "ESI did not answer — see the error log. It can still be added and "
                           + "filled in by hand.";
                AddStatus  = "No answer.";
                return;
            }

            AddPreview = (StructureStatus)row.Status switch
            {
                StructureStatus.NoAccess => "No access (403) — no docking rights with this "
                                          + "structure. It can still be added and filled in by hand.",
                StructureStatus.NotFound => "Not found (404) — no such structure, or it has been "
                                          + "unanchored. Check the ID before adding.",
                StructureStatus.Resolved => await DescribeAsync(row),
                _                        => "ESI returned nothing usable for this ID.",
            };

            AddStatus = ((StructureStatus)row.Status) == StructureStatus.Resolved
                ? "Resolved."
                : StatusLabel(row.Status) + ".";
        }
        catch (Exception ex)
        {
            AddStatus  = $"Look-up failed: {ex.Message}";
            AddPreview = "";
        }
        finally { Busy = false; }
    }

    /// <summary>Turns a resolved row into the line shown before committing — names, not ids, since
    /// the point is to let someone recognise the place.</summary>
    private async Task<string> DescribeAsync(StructureName row)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var system = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => s.SolarSystemId == row.SolarSystemId)
            .Select(s => s.Name).FirstOrDefaultAsync() ?? $"System {row.SolarSystemId}";

        var type = row.TypeId > 0
            ? await db.SdeTypes.AsNoTracking().Where(t => t.TypeId == row.TypeId)
                  .Select(t => t.Name).FirstOrDefaultAsync() ?? $"Type {row.TypeId}"
            : "unknown type";

        var owner = row.OwnerId > 0
            ? await db.UniverseNames.AsNoTracking().Where(u => u.EntityId == row.OwnerId)
                  .Select(u => u.Name).FirstOrDefaultAsync() ?? $"Corp {row.OwnerId}"
            : "unknown owner";

        return $"{row.Name}\n{type} in {system}\nOwned by {owner}";
    }

    /// <summary>
    /// Commits the looked-up id to the app's own table.
    /// </summary>
    private async Task CommitAddAsync()
    {
        var id = _addLookedUpId;
        if (id <= 0) return;

        Busy = true;
        try
        {
            // The sync owns the mapping from ESI's row onto ours, and inserts a row for every id
            // it has seen — so for anything the look-up reached, resolved or not, this is enough.
            await _polling.SyncStructuresAsync();

            await using (var db = await _dbFactory.CreateDbContextAsync())
            {
                if (!await db.Structures.AnyAsync(s => s.StructureId == id))
                {
                    // ESI never answered at all, so there is no row for the sync to have copied.
                    // Record it anyway: a structure we cannot read is precisely the one worth
                    // keeping by hand, which is the whole reason this dialog exists.
                    db.Structures.Add(new Structure
                    {
                        StructureId = id,
                        UpdatedBy   = StructureSource.User,
                        UpdatedAt   = DateTimeOffset.UtcNow,
                    });
                    await db.SaveChangesAsync();
                }
            }

            AddOpen = false;
            await LoadAsync();
            SelectById(id);
        }
        catch (Exception ex) { AddStatus = $"Add failed: {ex.Message}"; }
        finally { Busy = false; }
    }

    /// <summary>
    /// Shows one structure, for a caller arriving from somewhere else.
    ///
    /// <para>Loads first when the list is empty: the tool builds its rows on construction, but a
    /// link can be followed before that has finished, and selecting into an empty list would land
    /// on nothing and look like a broken link.</para>
    /// </summary>
    public void Open(long structureId) => _ = OpenAsync(structureId);

    private async Task OpenAsync(long structureId)
    {
        if (_all.Count == 0) await LoadAsync();
        SelectById(structureId);
    }

    /// <summary>
    /// Selects a structure by id, revealing it if the current filters hide it.
    ///
    /// <para>⚠️ Unhides rather than failing quietly. An id that was just added or just pulled is
    /// usually an unresolved one, and Show unknown now defaults off — so the row the user asked
    /// for would land outside the list and the command would look like it did nothing.</para>
    /// </summary>
    private void SelectById(long id)
    {
        if (Rows.All(r => r.StructureId != id) && _all.Any(r => r.StructureId == id))
        {
            ShowUnknown = true;
            RegionText  = ConstellationText = SystemText = TypeText = CorpText = AllianceText = "";
        }

        Selected = Rows.FirstOrDefault(r => r.StructureId == id);
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
