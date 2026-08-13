using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

// ── SDE rig option (loaded once from DB) ──────────────────────────────────────

public record SdeRigOption(int TypeId, string Name)
{
    public override string ToString() => Name;
}

// ── Rig slot (one of three per structure) ─────────────────────────────────────

public class RigSlotVm : ReactiveObject
{
    public int SlotIndex { get; }

    private IReadOnlyList<SdeRigOption> _availableRigs = [];
    public IReadOnlyList<SdeRigOption> AvailableRigs
    {
        get => _availableRigs;
        set => this.RaiseAndSetIfChanged(ref _availableRigs, value);
    }

    private SdeRigOption? _selected;
    public SdeRigOption? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    public RigSlotVm(int slotIndex, IReadOnlyList<SdeRigOption> rigs, SdeRigOption? selected = null)
    {
        SlotIndex = slotIndex;
        _availableRigs = rigs;
        _selected = selected;
    }
}

// ── Service module (zero or more per structure, no slot) ──────────────────────

/// <summary>One service module on a park structure. Unlike a rig it carries no slot index: the
/// game gives a structure several service slots but which one holds what changes nothing.</summary>
public class ServiceModuleVm(StructureVm owner, int typeId, string name) : ReactiveObject
{
    /// <summary>The structure it sits on. Carried here so the remove button can pass one object
    /// and still say which structure to take it off.</summary>
    public StructureVm Owner  { get; } = owner;
    public int         TypeId { get; } = typeId;
    public string      Name   { get; } = name;
}

// ── Structure VM ──────────────────────────────────────────────────────────────

public class StructureVm : ReactiveObject
{
    public int Id { get; }
    public int ParkId { get; }

    private string _displayName;
    public string DisplayName
    {
        get => _displayName;
        set => this.RaiseAndSetIfChanged(ref _displayName, value);
    }

    private string _structureTypeKey;
    public string StructureTypeKey
    {
        get => _structureTypeKey;
        set
        {
            this.RaiseAndSetIfChanged(ref _structureTypeKey, value);
            this.RaisePropertyChanged(nameof(StructureTypeLabel));
            this.RaisePropertyChanged(nameof(DisplayHeader));
        }
    }

    // ComboBox binds to this; setting it propagates back to StructureTypeKey
    public string StructureTypeLabel
    {
        get
        {
            var idx = Array.IndexOf(IndyParksViewModel.StructureTypeKeys, _structureTypeKey);
            return idx >= 0 ? IndyParksViewModel.StructureTypeLabels[idx] : _structureTypeKey;
        }
        set
        {
            var idx = Array.IndexOf(IndyParksViewModel.StructureTypeLabels, value);
            StructureTypeKey = idx >= 0 ? IndyParksViewModel.StructureTypeKeys[idx] : value;
        }
    }

    private string _systemName;
    public string SystemName
    {
        get => _systemName;
        set => this.RaiseAndSetIfChanged(ref _systemName, value);
    }

    private string _securityClass;
    public string SecurityClass
    {
        get => _securityClass;
        set
        {
            this.RaiseAndSetIfChanged(ref _securityClass, value);
            this.RaisePropertyChanged(nameof(SecurityLabel));
        }
    }

    public string SecurityLabel
    {
        get
        {
            var idx = Array.IndexOf(IndyParksViewModel.SecurityClasses, _securityClass);
            return idx >= 0 ? IndyParksViewModel.SecurityLabels[idx] : _securityClass;
        }
        set
        {
            var idx = Array.IndexOf(IndyParksViewModel.SecurityLabels, value);
            SecurityClass = idx >= 0 ? IndyParksViewModel.SecurityClasses[idx] : value;
        }
    }

    private decimal _facilityTax = 1m;
    public decimal FacilityTax
    {
        get => _facilityTax;
        set => this.RaiseAndSetIfChanged(ref _facilityTax, value);
    }

    public ObservableCollection<RigSlotVm> RigSlots { get; } = new();

    /// <summary>Service modules on this structure. No slots: a park entry cares which services
    /// exist, not which hole each occupies.</summary>
    public ObservableCollection<ServiceModuleVm> Services { get; } = new();

    /// <summary>Candidates for the "add a service" picker, for this structure's hull.</summary>
    private IReadOnlyList<SdeRigOption> _availableServices = [];
    public IReadOnlyList<SdeRigOption> AvailableServices
    {
        get => _availableServices;
        set => this.RaiseAndSetIfChanged(ref _availableServices, value);
    }

    private SdeRigOption? _serviceToAdd;
    public SdeRigOption? ServiceToAdd
    {
        get => _serviceToAdd;
        set => this.RaiseAndSetIfChanged(ref _serviceToAdd, value);
    }

    /// <summary>
    /// True when the linked real structure's fitting is visible in our own assets.
    ///
    /// <para>⚠️ Rigs and services are then read-only here. The game is the authority for a
    /// structure we can see inside, so an edit made on this side would be reverted by the next
    /// sweep — offering it would be offering a change that silently undoes itself.</para>
    /// </summary>
    private bool _fittingFromAssets;
    public bool FittingFromAssets
    {
        get => _fittingFromAssets;
        set
        {
            this.RaiseAndSetIfChanged(ref _fittingFromAssets, value);
            this.RaisePropertyChanged(nameof(FittingEditable));
            this.RaisePropertyChanged(nameof(FittingSourceText));
        }
    }

    public bool FittingEditable => !_fittingFromAssets;

    public string FittingSourceText => _fittingFromAssets
        ? "From assets — the game reports this structure's fitting, so it cannot be edited here."
        : RealStructureId is null
            ? ""
            : "Entered by hand — this fitting is also written to the linked structure.";

    public string DisplayHeader => string.IsNullOrWhiteSpace(DisplayName) ? StructureTypeLabel : DisplayName;

    // ── Link to a real in-game facility ──────────────────────────────────────
    // Set by hand: the user says which actual structure this park entry describes.
    // Nothing is inferred or name-matched. Without a link, industry jobs running at
    // that facility are reported as unknown rather than unrigged.

    private long? _realStructureId;
    public long? RealStructureId
    {
        get => _realStructureId;
        set { this.RaiseAndSetIfChanged(ref _realStructureId, value); this.RaisePropertyChanged(nameof(FacilityLinkText)); }
    }

    private string _realStructureName = "";
    public string RealStructureName
    {
        get => _realStructureName;
        set { this.RaiseAndSetIfChanged(ref _realStructureName, value); this.RaisePropertyChanged(nameof(FacilityLinkText)); }
    }

    public string FacilityLinkText => RealStructureId is null
        ? "Not linked — jobs here won't be rig-checked"
        : RealStructureName;

    /// <summary>Search results while picking; not persisted.</summary>
    public ObservableCollection<SdeStationResult> FacilityResults { get; } = [];

    private string _facilitySearch = "";
    public string FacilitySearch
    {
        get => _facilitySearch;
        set => this.RaiseAndSetIfChanged(ref _facilitySearch, value);
    }

    /// <summary>
    /// This facility is the park's catch-all — where items no category assignment covers
    /// get planned, with no rig bonus. Exactly one facility per park carries it; checking
    /// one clears the rest. Stored as a single id on the park, so two cannot both be set.
    /// </summary>
    private bool _isDefaultFacility;
    public bool IsDefaultFacility
    {
        get => _isDefaultFacility;
        set => this.RaiseAndSetIfChanged(ref _isDefaultFacility, value);
    }

    public StructureVm(int id, int parkId, string displayName, string structureTypeKey,
                       string systemName, string securityClass, decimal facilityTax = 1m,
                       long? realStructureId = null, string realStructureName = "")
    {
        Id                 = id;
        ParkId             = parkId;
        _displayName       = displayName;
        _structureTypeKey  = structureTypeKey;
        _systemName        = systemName;
        _securityClass     = securityClass;
        _facilityTax       = facilityTax;
        _realStructureId   = realStructureId;
        _realStructureName = realStructureName;
    }
}

// ── Category assignment row ───────────────────────────────────────────────────

public class CategoryAssignmentVm : ReactiveObject
{
    public string CategoryKey   { get; }
    public string CategoryLabel { get; }

    private IReadOnlyList<StructureVm?> _structureOptions = [];
    public IReadOnlyList<StructureVm?> StructureOptions
    {
        get => _structureOptions;
        set => this.RaiseAndSetIfChanged(ref _structureOptions, value);
    }

    private StructureVm? _selected;
    public StructureVm? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    public CategoryAssignmentVm(string key, string label)
    {
        CategoryKey   = key;
        CategoryLabel = label;
    }
}

// ── Item exception row ────────────────────────────────────────────────────────

public class ItemExceptionVm : ReactiveObject
{
    public int    Id       { get; }
    public int    TypeId   { get; }
    public string TypeName { get; }

    private IReadOnlyList<StructureVm?> _structureOptions = [];
    public IReadOnlyList<StructureVm?> StructureOptions
    {
        get => _structureOptions;
        set => this.RaiseAndSetIfChanged(ref _structureOptions, value);
    }

    private StructureVm? _selected;
    public StructureVm? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    public ItemExceptionVm(int id, int typeId, string typeName)
    {
        Id       = id;
        TypeId   = typeId;
        TypeName = typeName;
    }
}

// ── Item search result ────────────────────────────────────────────────────────

public record ItemSearchResult(int TypeId, string Name)
{
    public override string ToString() => Name;
}

// ── Park list entry ───────────────────────────────────────────────────────────

public class IndyParkListItem : ReactiveObject
{
    public int Id { get; }

    private string _name;
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    private bool _isDefault;
    public bool IsDefault
    {
        get => _isDefault;
        set => this.RaiseAndSetIfChanged(ref _isDefault, value);
    }

    public IndyParkListItem(int id, string name, bool isDefault = false)
    {
        Id = id; _name = name; _isDefault = isDefault;
    }
    public override string ToString() => Name;
}

// ── Main ViewModel ────────────────────────────────────────────────────────────

public class IndyParksViewModel : ReactiveObject
{
    // ── Predefined data ───────────────────────────────────────────────────

    public static readonly string[] StructureTypeKeys   = ["raitaru", "azbel", "sotiyo", "athanor", "tatara", "npc_station"];
    public static readonly string[] StructureTypeLabels = ["Raitaru", "Azbel", "Sotiyo", "Athanor", "Tatara", "NPC Station"];
    public static readonly string[] SecurityClasses     = ["highsec", "lowsec", "nullsec", "wormhole"];
    public static readonly string[] SecurityLabels      = ["High Sec", "Low Sec", "Null Sec", "Wormhole"];

    public static readonly (string Key, string Label)[] ProductionCategories =
    [
        // Manufacturing
        ("large_ships",        "Large Ships"),
        ("medium_ships",       "Medium Ships"),
        ("small_ships",        "Small Ships"),
        ("capital_ships",      "Capital Ships"),
        ("adv_large_ships",    "Advanced Large Ships"),
        ("adv_medium_ships",   "Advanced Medium Ships"),
        ("adv_small_ships",    "Advanced Small Ships"),
        ("capital_components", "Capital Components"),
        ("adv_components",     "Advanced Components"),
        ("cap_adv_components", "Capital Advanced Components"),
        ("drones_fighters",    "Drones and Fighters"),
        ("ammo_charges",       "Ammo and Charges"),
        ("modules_equipment",  "Modules and Equipment"),
        ("structure_ammo",     "Structure and Ammo"),
        // Reactions
        ("react_composite",    "Composite Reactions"),
        ("react_biochemical",  "Hybrid Reactions"),
        ("react_bio_gas",      "Bio and Gas Phase Reactions"),
        ("react_structure",    "Structures and Fuel Blocks"),
        // Reprocessing
        ("reprocessing",       "Reprocessing"),
    ];

    // ── Pre-loaded rig options per structure type ─────────────────────────

    private readonly Dictionary<string, IReadOnlyList<SdeRigOption>> _rigsByType = new();

    // ── Parks list ────────────────────────────────────────────────────────

    public ObservableCollection<IndyParkListItem> Parks { get; } = new();

    private IndyParkListItem? _selectedPark;
    public IndyParkListItem? SelectedPark
    {
        get => _selectedPark;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPark, value);
            _ = LoadParkDetailAsync(value?.Id);
        }
    }

    // ── Park details ──────────────────────────────────────────────────────

    private string _parkName = "";
    public string ParkName
    {
        get => _parkName;
        set => this.RaiseAndSetIfChanged(ref _parkName, value);
    }

    public ObservableCollection<StructureVm>          Structures       { get; } = new();
    public ObservableCollection<CategoryAssignmentVm> Assignments      { get; } = new();
    public ObservableCollection<ItemExceptionVm>      ItemExceptions   { get; } = new();
    public ObservableCollection<ItemSearchResult>     ItemSearchResults { get; } = new();

    private string _itemSearchText = "";
    public string ItemSearchText
    {
        get => _itemSearchText;
        set => this.RaiseAndSetIfChanged(ref _itemSearchText, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit>             AddParkCommand             { get; }
    public ReactiveCommand<Unit, Unit>             DeleteParkCommand           { get; }
    public ReactiveCommand<Unit, Unit>             SetDefaultParkCommand       { get; }
    public ReactiveCommand<Unit, Unit>             AddStructureCommand         { get; }
    public ReactiveCommand<StructureVm, Unit>      RemoveStructureCommand      { get; }
    public ReactiveCommand<StructureVm, Unit>      SaveStructureCommand        { get; }
    public ReactiveCommand<StructureVm, Unit>      SearchFacilityCommand       { get; }
    public ReactiveCommand<SdeStationResult, Unit> LinkFacilityCommand         { get; }
    public ReactiveCommand<StructureVm, Unit>      UnlinkFacilityCommand       { get; }
    public ReactiveCommand<ItemSearchResult, Unit> AddItemExceptionCommand     { get; }
    public ReactiveCommand<ItemExceptionVm, Unit>  RemoveItemExceptionCommand  { get; }
    public ReactiveCommand<StructureVm, Unit>      AddServiceCommand           { get; }
    public ReactiveCommand<ServiceModuleVm, Unit>  RemoveServiceCommand        { get; }

    // ── Private ───────────────────────────────────────────────────────────

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private bool _suppressSave;

    // ── Constructor ───────────────────────────────────────────────────────

    /// <summary>Used only to search real stations and structures for the facility link.
    /// Optional so the designer and any test construction still work.</summary>
    private readonly CorpActivityService? _corpActivity;
    private readonly AppErrorLogger?      _errorLogger;

    /// <summary>Pushes hand-entered fittings through to the linked structure. Optional for the
    /// same reason as the two above — without it, edits simply stay on this side.</summary>
    private readonly IndyStructureLinkService? _indyLink;

    public IndyParksViewModel(IDbContextFactory<AppDbContext> dbFactory,
                              CorpActivityService? corpActivity = null,
                              AppErrorLogger? errorLogger = null,
                              IndyStructureLinkService? indyLink = null)
    {
        _dbFactory    = dbFactory;
        _corpActivity = corpActivity;
        _errorLogger  = errorLogger;
        _indyLink     = indyLink;

        LoadRigsFromSde();

        AddParkCommand            = ReactiveCommand.CreateFromTask(AddParkAsync);
        DeleteParkCommand         = ReactiveCommand.CreateFromTask(DeleteParkAsync);
        SetDefaultParkCommand     = ReactiveCommand.CreateFromTask(SetDefaultParkAsync);
        AddStructureCommand       = ReactiveCommand.CreateFromTask(AddStructureAsync);
        RemoveStructureCommand    = ReactiveCommand.CreateFromTask<StructureVm>(RemoveStructureAsync);
        SaveStructureCommand      = ReactiveCommand.CreateFromTask<StructureVm>(SaveStructureAsync);
        SearchFacilityCommand     = ReactiveCommand.CreateFromTask<StructureVm>(SearchFacilityAsync);
        LinkFacilityCommand       = ReactiveCommand.CreateFromTask<SdeStationResult>(LinkFacilityAsync);
        UnlinkFacilityCommand     = ReactiveCommand.CreateFromTask<StructureVm>(UnlinkFacilityAsync);
        AddItemExceptionCommand   = ReactiveCommand.CreateFromTask<ItemSearchResult>(AddItemExceptionAsync);
        RemoveItemExceptionCommand = ReactiveCommand.CreateFromTask<ItemExceptionVm>(RemoveItemExceptionAsync);
        AddServiceCommand         = ReactiveCommand.CreateFromTask<StructureVm>(AddServiceAsync);
        RemoveServiceCommand      = ReactiveCommand.CreateFromTask<ServiceModuleVm>(
                                        s => RemoveServiceAsync(s.Owner, s));

        this.WhenAnyValue(x => x.ParkName)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Subscribe(async _ => await SaveParkNameAsync());

        this.WhenAnyValue(x => x.ItemSearchText)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .Subscribe(async text => await SearchItemsAsync(text));

        _ = LoadParksAsync();
    }

    // ── Rig loading ───────────────────────────────────────────────────────

    private void LoadRigsFromSde()
    {
        const int attrRigSize   = 1547;
        const int attrMfgME     = 2594;
        const int attrMfgTE     = 2593;
        const int attrMfgCost   = 2595;
        const int attrRxnME     = 2714;
        const int attrReprYield = 717;    // refiningYieldMultiplier — reprocessing rigs

        using var db = _dbFactory.CreateDbContext();
        _rigsByType["raitaru"]     = LoadRigs(db, attrRigSize, 2, [attrMfgME, attrMfgTE, attrMfgCost]);
        _rigsByType["azbel"]       = LoadRigs(db, attrRigSize, 3, [attrMfgME, attrMfgTE, attrMfgCost]);
        _rigsByType["sotiyo"]      = LoadRigs(db, attrRigSize, 4, [attrMfgME, attrMfgTE, attrMfgCost]);
        _rigsByType["athanor"]     = LoadRigs(db, attrRigSize, 2, [attrRxnME, attrReprYield]);
        _rigsByType["tatara"]      = LoadRigs(db, attrRigSize, 3, [attrRxnME, attrReprYield]);
        _rigsByType["npc_station"] = [];

        LoadServiceOptions(db);
    }

    /// <summary>
    /// Service modules, shared by every Upwell hull.
    ///
    /// <para>Unlike rigs there is no size restriction to apply — a service module declares the
    /// serviceSlot effect and that is the whole test, which is also how the Structure Browser's
    /// picker decides. NPC stations get none: their services are not something anyone fits.</para>
    /// </summary>
    private void LoadServiceOptions(AppDbContext db)
    {
        const int effServiceSlot = 6306;   // dogma effect, verified against the SDE
        const int structureModuleCategory = 66;

        var services = (from te in db.SdeTypeDogmaEffects
                        join t in db.SdeTypes  on te.TypeId  equals t.TypeId
                        join g in db.SdeGroups on t.GroupId equals g.GroupId
                        where te.EffectId == effServiceSlot
                              && t.Published
                              && g.CategoryId == structureModuleCategory
                        select new { t.TypeId, t.Name })
                       .ToList()
                       .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                       .Select(t => new SdeRigOption(t.TypeId, t.Name))
                       .ToList();

        foreach (var key in StructureTypeKeys)
            _servicesByType[key] = key == "npc_station" ? [] : services;
    }

    private readonly Dictionary<string, IReadOnlyList<SdeRigOption>> _servicesByType = new();

    public IReadOnlyList<SdeRigOption> GetServicesForType(string structureTypeKey)
        => _servicesByType.TryGetValue(structureTypeKey, out var s) ? s : [];

    private static IReadOnlyList<SdeRigOption> LoadRigs(AppDbContext db, int sizeAttrId, double sizeValue, int[] bonusAttrIds)
    {
        return db.SdeTypes
            .Where(t => t.Published)
            .Where(t => !t.Name.EndsWith("Output Rig") && !t.Name.Contains("Outpost Rig"))
            .Where(t => db.SdeTypeDogmaAttributes.Any(a =>
                a.TypeId == t.TypeId && a.AttributeId == sizeAttrId && a.Value == sizeValue))
            .Where(t => db.SdeTypeDogmaAttributes.Any(a =>
                a.TypeId == t.TypeId && bonusAttrIds.Contains(a.AttributeId)))
            .OrderBy(t => t.Name)
            .Select(t => new SdeRigOption(t.TypeId, t.Name))
            .ToList();
    }

    public IReadOnlyList<SdeRigOption> GetRigsForType(string structureTypeKey)
        => _rigsByType.TryGetValue(structureTypeKey, out var rigs) ? rigs : [];

    // ── Park list ─────────────────────────────────────────────────────────

    private async Task LoadParksAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parks = await db.IndyParks.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Parks.Clear();
            foreach (var p in parks)
                Parks.Add(new IndyParkListItem(p.Id, p.Name, p.IsDefault));
            if (Parks.Count > 0)
                SelectedPark = Parks[0];
        });
    }

    private async Task AddParkAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var park = new IndyPark { Name = "New Park" };
        db.IndyParks.Add(park);
        await db.SaveChangesAsync();

        foreach (var (key, _) in ProductionCategories)
            db.IndyCategoryAssignments.Add(new IndyCategoryAssignment { ParkId = park.Id, CategoryKey = key });
        await db.SaveChangesAsync();

        var item = new IndyParkListItem(park.Id, park.Name);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Parks.Add(item);
            SelectedPark = item;
        });
    }

    private async Task DeleteParkAsync()
    {
        if (_selectedPark is null) return;
        var id = _selectedPark.Id;

        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.IndyCategoryAssignments.Where(a => a.ParkId == id).ExecuteDeleteAsync();
        await db.IndyItemExceptions.Where(e => e.ParkId == id).ExecuteDeleteAsync();
        var structIds = await db.IndyStructures.Where(s => s.ParkId == id)
            .Select(s => s.Id).ToListAsync();
        foreach (var sid in structIds)
            await db.IndyStructureRigs.Where(r => r.StructureId == sid).ExecuteDeleteAsync();
        await db.IndyStructures.Where(s => s.ParkId == id).ExecuteDeleteAsync();
        await db.IndyParks.Where(p => p.Id == id).ExecuteDeleteAsync();

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            var item = Parks.FirstOrDefault(p => p.Id == id);
            if (item is not null) Parks.Remove(item);
            SelectedPark = Parks.FirstOrDefault();
        });
    }

    private async Task SetDefaultParkAsync()
    {
        if (_selectedPark is null) return;
        var id = _selectedPark.Id;

        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.IndyParks.Where(p => p.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false));
        var park = await db.IndyParks.FindAsync(id);
        if (park is null) return;
        park.IsDefault = true;
        await db.SaveChangesAsync();

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var item in Parks)
                item.IsDefault = item.Id == id;
        });
    }

    // ── Park detail ───────────────────────────────────────────────────────

    private async Task LoadParkDetailAsync(int? parkId)
    {
        if (parkId is null)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _suppressSave = true;
                ParkName = "";
                Structures.Clear();
                Assignments.Clear();
                _suppressSave = false;
            });
            return;
        }

        var id = parkId.Value;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var park = await db.IndyParks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (park is null) return;

        var structures = await db.IndyStructures.AsNoTracking()
            .Where(s => s.ParkId == id).OrderBy(s => s.Id).ToListAsync();

        var structIds = structures.Select(s => s.Id).ToList();
        var rigs = await db.IndyStructureRigs.AsNoTracking()
            .Where(r => structIds.Contains(r.StructureId)).ToListAsync();

        var services = await db.IndyStructureServices.AsNoTracking()
            .Where(s => structIds.Contains(s.StructureId)).ToListAsync();

        var serviceNames = await db.SdeTypes.AsNoTracking()
            .Where(t => services.Select(s => s.TypeId).Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name);

        // Which linked structures the game is describing for us. Read once for the whole park
        // rather than per structure, and by the same test the sync uses to pick a direction.
        var linkedIds = structures.Where(s => s.RealStructureId != null)
                                  .Select(s => s.RealStructureId!.Value).ToList();
        var assetFed = linkedIds.Count == 0
            ? []
            : (await db.EsiAssets.AsNoTracking()
                .Where(a => linkedIds.Contains(a.LocationId) && a.LocationFlag.Contains("Slot"))
                .Select(a => a.LocationId).Distinct().ToListAsync()).ToHashSet();

        var assignments = await db.IndyCategoryAssignments.AsNoTracking()
            .Where(a => a.ParkId == id).ToListAsync();

        var exceptions = await db.IndyItemExceptions.AsNoTracking()
            .Where(e => e.ParkId == id).OrderBy(e => e.TypeName).ToListAsync();

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            _suppressSave = true;

            ParkName = park.Name;
            Structures.Clear();

            foreach (var s in structures)
            {
                var vm = BuildStructureVm(s);
                var structureRigs = rigs.Where(r => r.StructureId == s.Id).ToList();
                var availableRigs = GetRigsForType(s.StructureTypeKey);
                for (int slot = 0; slot < 3; slot++)
                {
                    var saved = structureRigs.FirstOrDefault(r => r.SlotIndex == slot);
                    var selectedRig = saved is null ? null
                        : availableRigs.FirstOrDefault(r => r.TypeId == saved.RigTypeId);
                    vm.RigSlots.Add(new RigSlotVm(slot, availableRigs, selectedRig));
                }

                vm.AvailableServices = GetServicesForType(s.StructureTypeKey);
                foreach (var svc in services.Where(x => x.StructureId == s.Id)
                                            .OrderBy(x => serviceNames.GetValueOrDefault(x.TypeId, "")))
                    vm.Services.Add(new ServiceModuleVm(
                        vm, svc.TypeId, serviceNames.GetValueOrDefault(svc.TypeId, $"Type {svc.TypeId}")));

                vm.FittingFromAssets =
                    s.RealStructureId is { } realId && assetFed.Contains(realId);

                WireStructureVm(vm);
                Structures.Add(vm);
            }

            // Exactly one facility carries the catch-all. Fall back to the first when the
            // park has none recorded — parks predating this setting, and every park whose
            // default facility was since deleted, would otherwise plan unclassified items
            // with no structure at all.
            var marked = Structures.FirstOrDefault(v => v.Id == park.DefaultStructureId)
                      ?? Structures.FirstOrDefault();
            foreach (var v in Structures) v.IsDefaultFacility = ReferenceEquals(v, marked);
            if (marked is not null && park.DefaultStructureId != marked.Id)
                _ = PersistDefaultStructureAsync(id, marked.Id);

            RebuildAssignments(assignments);
            RebuildItemExceptions(exceptions);

            _suppressSave = false;
        });
    }

    private StructureVm BuildStructureVm(IndyStructure s)
        => new(s.Id, s.ParkId, s.DisplayName, s.StructureTypeKey, s.SystemName, s.SecurityClass,
               s.FacilityTax, s.RealStructureId, s.RealStructureName);

    /// <summary>Which structure the visible search results belong to. The results list
    /// renders SdeStationResult rows, so the pick alone can't say what it links to.</summary>
    private StructureVm? _facilitySearchTarget;

    /// <summary>Search real stations and structures for the facility link. Reuses the
    /// same helper the standing-project and standing-buy-order dialogs use, so NPC
    /// stations, player structures and corp structures are all reachable.</summary>
    public async Task SearchFacilityAsync(StructureVm vm)
    {
        // Only one result list is meaningful at a time; clear any other structure's.
        if (_facilitySearchTarget is not null && !ReferenceEquals(_facilitySearchTarget, vm))
            _facilitySearchTarget.FacilityResults.Clear();
        _facilitySearchTarget = vm;

        var text = vm.FacilitySearch?.Trim() ?? "";
        vm.FacilityResults.Clear();
        if (text.Length < 2 || _corpActivity is null) return;

        try
        {
            foreach (var r in await _corpActivity.SearchSdeStationsAsync(text))
                vm.FacilityResults.Add(r);
        }
        catch (Exception ex) { _errorLogger?.Log(nameof(IndyParksViewModel), "SearchFacility", ex); }
    }

    public async Task LinkFacilityAsync(SdeStationResult pick)
    {
        var vm = _facilitySearchTarget;
        if (vm is null) return;

        vm.RealStructureId   = pick.StationId;
        vm.RealStructureName = pick.Name;
        vm.FacilityResults.Clear();
        vm.FacilitySearch = "";
        _facilitySearchTarget = null;
        await SaveStructureDbAsync(vm);
    }

    public async Task UnlinkFacilityAsync(StructureVm vm)
    {
        vm.RealStructureId   = null;
        vm.RealStructureName = "";
        await SaveStructureDbAsync(vm);
    }

    private void WireStructureVm(StructureVm vm)
    {
        // Reload rig list when structure type changes
        vm.WhenAnyValue(x => x.StructureTypeKey).Skip(1).Subscribe(async key =>
        {
            var rigs = GetRigsForType(key);
            foreach (var slot in vm.RigSlots)
            {
                slot.AvailableRigs = rigs;
                slot.Selected = null;
            }
            await SaveStructureDbAsync(vm);
            await SaveAllRigSlotsAsync(vm);
        });

        vm.WhenAnyValue(x => x.DisplayName, x => x.SystemName, x => x.SecurityClass, x => x.FacilityTax)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(400))
            .Subscribe(async _ => await SaveStructureDbAsync(vm));

        // Radio-group behaviour from a checkbox. Only a tick does anything; unticking the
        // current catch-all puts it straight back, since a park must always have one.
        vm.WhenAnyValue(x => x.IsDefaultFacility).Skip(1).Subscribe(async isDefault =>
        {
            if (_suppressSave) return;
            if (isDefault) await SetDefaultStructureAsync(vm);
            else if (!Structures.Any(s => s.IsDefaultFacility)) vm.IsDefaultFacility = true;
        });

        foreach (var slot in vm.RigSlots)
            WireRigSlot(vm, slot);
    }

    private void WireRigSlot(StructureVm structure, RigSlotVm slot)
    {
        slot.WhenAnyValue(x => x.Selected).Skip(1)
            .Subscribe(async _ => await SaveRigSlotAsync(structure, slot));
    }

    private void RebuildAssignments(List<IndyCategoryAssignment> saved)
    {
        Assignments.Clear();

        // Null-entry for "None" in dropdowns
        IReadOnlyList<StructureVm?> options = [null, .. Structures.Cast<StructureVm?>()];

        foreach (var (key, label) in ProductionCategories)
        {
            var vm = new CategoryAssignmentVm(key, label) { StructureOptions = options };
            var existing = saved.FirstOrDefault(a => a.CategoryKey == key);
            if (existing?.StructureId is int sid)
                vm.Selected = Structures.FirstOrDefault(s => s.Id == sid);

            vm.WhenAnyValue(x => x.Selected).Skip(1)
                .Subscribe(async _ => await SaveAssignmentAsync(vm));

            Assignments.Add(vm);
        }
    }

    private void RefreshAssignmentOptions()
    {
        IReadOnlyList<StructureVm?> options = [null, .. Structures.Cast<StructureVm?>()];
        foreach (var a in Assignments)
        {
            var current = a.Selected;
            a.StructureOptions = options;
            a.Selected = current is null ? null : Structures.FirstOrDefault(s => s.Id == current.Id);
        }
        foreach (var e in ItemExceptions)
        {
            var current = e.Selected;
            e.StructureOptions = options;
            e.Selected = current is null ? null : Structures.FirstOrDefault(s => s.Id == current.Id);
        }
    }

    // ── Park name save ────────────────────────────────────────────────────

    private async Task SaveParkNameAsync()
    {
        if (_suppressSave || _selectedPark is null) return;
        var id = _selectedPark.Id;
        var name = ParkName;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var park = await db.IndyParks.FindAsync(id);
        if (park is null) return;
        park.Name = name;
        await db.SaveChangesAsync();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_selectedPark?.Id == id)
                _selectedPark.Name = name;
        });
    }

    // ── Structure CRUD ────────────────────────────────────────────────────

    private async Task AddStructureAsync()
    {
        if (_selectedPark is null) return;
        var parkId = _selectedPark.Id;

        var s = new IndyStructure
        {
            ParkId           = parkId,
            DisplayName      = "New Structure",
            StructureTypeKey = "raitaru",
            SecurityClass    = "nullsec",
        };
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.IndyStructures.Add(s);
        await db.SaveChangesAsync();

        for (int slot = 0; slot < 3; slot++)
            db.IndyStructureRigs.Add(new IndyStructureRig { StructureId = s.Id, SlotIndex = slot, RigTypeId = 0 });
        await db.SaveChangesAsync();

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            var vm = BuildStructureVm(s);
            var rigs = GetRigsForType(s.StructureTypeKey);
            for (int slot = 0; slot < 3; slot++)
                vm.RigSlots.Add(new RigSlotVm(slot, rigs, null));
            WireStructureVm(vm);
            Structures.Add(vm);
            // The first facility in a park becomes its catch-all.
            if (Structures.Count == 1) vm.IsDefaultFacility = true;
            RefreshAssignmentOptions();
        });
    }

    private async Task RemoveStructureAsync(StructureVm vm)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var asgn = await db.IndyCategoryAssignments
            .Where(a => a.StructureId == vm.Id).ToListAsync();
        foreach (var a in asgn) a.StructureId = null;
        await db.IndyStructureRigs.Where(r => r.StructureId == vm.Id).ExecuteDeleteAsync();
        await db.IndyStructures.Where(s => s.Id == vm.Id).ExecuteDeleteAsync();
        await db.SaveChangesAsync();

        bool wasDefault = vm.IsDefaultFacility;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Structures.Remove(vm);
            foreach (var a in Assignments)
                if (a.Selected?.Id == vm.Id) a.Selected = null;
            foreach (var e in ItemExceptions)
                if (e.Selected?.Id == vm.Id) e.Selected = null;
            RefreshAssignmentOptions();
        });

        // Deleting the catch-all would leave the park without one, and every unclassified
        // item would then plan with no structure. Hand it to whatever is left.
        if (wasDefault)
        {
            var successor = Structures.FirstOrDefault();
            if (successor is not null)
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => successor.IsDefaultFacility = true);
            else
                await PersistDefaultStructureAsync(vm.ParkId, null);
        }
    }

    private async Task SaveStructureAsync(StructureVm vm)
    {
        await SaveStructureDbAsync(vm);
    }

    // ── Catch-all facility ────────────────────────────────────────────────────

    /// <summary>
    /// Makes one facility the park's catch-all and clears the rest. Bound to a checkbox per
    /// facility rather than a dropdown, but it behaves as a radio group — unchecking the
    /// current one re-checks it, because a park with no catch-all silently loses the
    /// structure for every unclassified item.
    /// </summary>
    private async Task SetDefaultStructureAsync(StructureVm vm)
    {
        if (_suppressSave || _selectedPark is null) return;

        _suppressSave = true;
        foreach (var other in Structures)
            other.IsDefaultFacility = ReferenceEquals(other, vm);
        _suppressSave = false;

        await PersistDefaultStructureAsync(_selectedPark.Id, vm.Id);
    }

    private async Task PersistDefaultStructureAsync(int parkId, int? structureId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var park = await db.IndyParks.FindAsync(parkId);
        if (park is null) return;
        park.DefaultStructureId = structureId;
        await db.SaveChangesAsync();
    }

    private async Task SaveStructureDbAsync(StructureVm vm)
    {
        if (_suppressSave) return;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.IndyStructures.FindAsync(vm.Id);
        if (entity is null) return;
        entity.DisplayName      = vm.DisplayName;
        entity.StructureTypeKey = vm.StructureTypeKey;
        entity.SystemName       = vm.SystemName;
        entity.SecurityClass    = vm.SecurityClass;
        entity.FacilityTax        = vm.FacilityTax;
        entity.RealStructureId    = vm.RealStructureId;
        entity.RealStructureName  = vm.RealStructureName;
        await db.SaveChangesAsync();
    }

    private async Task SaveRigSlotAsync(StructureVm structure, RigSlotVm slot)
    {
        if (_suppressSave) return;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.IndyStructureRigs.FirstOrDefaultAsync(r =>
            r.StructureId == structure.Id && r.SlotIndex == slot.SlotIndex);
        if (entity is null) return;
        entity.RigTypeId = slot.Selected?.TypeId ?? 0;
        await db.SaveChangesAsync();

        await PushFittingAsync(structure);
    }

    /// <summary>
    /// Carries a hand-entered fitting through to the linked structure.
    ///
    /// <para>The link service decides whether anything actually moves: if the real structure's
    /// fitting is visible in assets it pushes the other way instead, so this cannot overwrite what
    /// the game reported even if the UI let an edit through.</para>
    /// </summary>
    private async Task PushFittingAsync(StructureVm structure)
    {
        if (_suppressSave || structure.RealStructureId is null || _indyLink is null) return;
        await _indyLink.PushFromParkAsync(structure.Id);
    }

    // ── Service modules ───────────────────────────────────────────────────

    private async Task AddServiceAsync(StructureVm structure)
    {
        if (structure.ServiceToAdd is not { } pick) return;
        if (structure.Services.Any(s => s.TypeId == pick.TypeId))
        {
            // The same service twice does nothing in game, and two identical rows would give the
            // set comparison in the link service something it would have to collapse anyway.
            structure.ServiceToAdd = null;
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.IndyStructureServices.Add(new IndyStructureService
        {
            StructureId = structure.Id, TypeId = pick.TypeId,
        });
        await db.SaveChangesAsync();

        structure.Services.Add(new ServiceModuleVm(structure, pick.TypeId, pick.Name));
        structure.ServiceToAdd = null;

        await PushFittingAsync(structure);
    }

    private async Task RemoveServiceAsync(StructureVm structure, ServiceModuleVm service)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.IndyStructureServices
            .Where(s => s.StructureId == structure.Id && s.TypeId == service.TypeId)
            .ExecuteDeleteAsync();

        structure.Services.Remove(service);

        await PushFittingAsync(structure);
    }

    private async Task SaveAllRigSlotsAsync(StructureVm vm)
    {
        foreach (var slot in vm.RigSlots)
            await SaveRigSlotAsync(vm, slot);
    }

    // ── Category assignment save ──────────────────────────────────────────

    private async Task SaveAssignmentAsync(CategoryAssignmentVm vm)
    {
        if (_suppressSave || _selectedPark is null) return;
        var parkId = _selectedPark.Id;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.IndyCategoryAssignments.FirstOrDefaultAsync(a =>
            a.ParkId == parkId && a.CategoryKey == vm.CategoryKey);
        if (entity is null)
        {
            entity = new IndyCategoryAssignment { ParkId = parkId, CategoryKey = vm.CategoryKey };
            db.IndyCategoryAssignments.Add(entity);
        }
        entity.StructureId = vm.Selected?.Id;
        await db.SaveChangesAsync();
    }

    // ── Item exceptions ───────────────────────────────────────────────────

    private void RebuildItemExceptions(List<IndyItemException> saved)
    {
        ItemExceptions.Clear();
        IReadOnlyList<StructureVm?> options = [null, .. Structures.Cast<StructureVm?>()];
        foreach (var exc in saved)
        {
            var vm = new ItemExceptionVm(exc.Id, exc.TypeId, exc.TypeName) { StructureOptions = options };
            if (exc.StructureId is int sid)
                vm.Selected = Structures.FirstOrDefault(s => s.Id == sid);
            vm.WhenAnyValue(x => x.Selected).Skip(1)
                .Subscribe(async _ => await SaveItemExceptionAsync(vm));
            ItemExceptions.Add(vm);
        }
    }

    private async Task SearchItemsAsync(string text)
    {
        if (text.Length < 2)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ItemSearchResults.Clear());
            return;
        }
        await using var db = await _dbFactory.CreateDbContextAsync();
        var lower = text.ToLower();
        // Ranked, not just alphabetical. A plain A-Z ordering buries the thing being
        // searched for: "Hel" matches Shield, Helium, Sheltered and hundreds more, and
        // the ship itself sorts past the cut. Exact match first, then names starting
        // with the term, then the rest; shorter names win ties, so "Hel" beats
        // "Hel Blueprint".
        var results = await db.SdeTypes
            .Where(t => t.Published && t.Name.ToLower().Contains(lower))
            .OrderBy(t => t.Name.ToLower() == lower            ? 0
                        : t.Name.ToLower().StartsWith(lower)   ? 1
                        : 2)
            .ThenBy(t => t.Name.Length)
            .ThenBy(t => t.Name)
            .Take(200)
            .Select(t => new ItemSearchResult(t.TypeId, t.Name))
            .ToListAsync();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            ItemSearchResults.Clear();
            foreach (var r in results) ItemSearchResults.Add(r);
        });
    }

    private async Task AddItemExceptionAsync(ItemSearchResult item)
    {
        if (_selectedPark is null) return;
        if (ItemExceptions.Any(e => e.TypeId == item.TypeId)) return;

        var parkId = _selectedPark.Id;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = new IndyItemException { ParkId = parkId, TypeId = item.TypeId, TypeName = item.Name };
        db.IndyItemExceptions.Add(entity);
        await db.SaveChangesAsync();

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            IReadOnlyList<StructureVm?> options = [null, .. Structures.Cast<StructureVm?>()];
            var vm = new ItemExceptionVm(entity.Id, entity.TypeId, entity.TypeName) { StructureOptions = options };
            vm.WhenAnyValue(x => x.Selected).Skip(1)
                .Subscribe(async _ => await SaveItemExceptionAsync(vm));
            ItemExceptions.Add(vm);
            ItemSearchText = "";
            ItemSearchResults.Clear();
        });
    }

    private async Task RemoveItemExceptionAsync(ItemExceptionVm vm)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.IndyItemExceptions.Where(e => e.Id == vm.Id).ExecuteDeleteAsync();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ItemExceptions.Remove(vm));
    }

    private async Task SaveItemExceptionAsync(ItemExceptionVm vm)
    {
        if (_suppressSave) return;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.IndyItemExceptions.FindAsync(vm.Id);
        if (entity is null) return;
        entity.StructureId = vm.Selected?.Id;
        await db.SaveChangesAsync();
    }

    // ── Export / Import ───────────────────────────────────────────────────

    public async Task ExportCurrentParkAsync(Stream stream)
    {
        if (_selectedPark is null) return;
        var parkId = _selectedPark.Id;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var park       = await db.IndyParks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parkId);
        if (park is null) return;
        var structures = await db.IndyStructures.AsNoTracking().Where(s => s.ParkId == parkId).OrderBy(s => s.Id).ToListAsync();
        var structIds  = structures.Select(s => s.Id).ToList();
        var rigs       = await db.IndyStructureRigs.AsNoTracking().Where(r => structIds.Contains(r.StructureId)).ToListAsync();
        var catAsgn    = await db.IndyCategoryAssignments.AsNoTracking().Where(a => a.ParkId == parkId).ToListAsync();
        var itemExc    = await db.IndyItemExceptions.AsNoTracking().Where(e => e.ParkId == parkId).ToListAsync();

        var dto = new ParkExportDto(
            park.Name,
            structures.Select((s, i) => new StructureExportDto(
                s.DisplayName, s.StructureTypeKey, s.SystemName, s.SecurityClass,
                Enumerable.Range(0, 3).Select(slot =>
                    rigs.FirstOrDefault(r => r.StructureId == s.Id && r.SlotIndex == slot)?.RigTypeId ?? 0
                ).ToList()
            )).ToList(),
            catAsgn.Select(a => new CategoryAssignmentExportDto(
                a.CategoryKey,
                a.StructureId is int sid ? structures.FindIndex(s => s.Id == sid) : (int?)-1
            )).ToList(),
            itemExc.Select(e => new ItemExceptionExportDto(
                e.TypeId, e.TypeName,
                e.StructureId is int sid ? structures.FindIndex(s => s.Id == sid) : (int?)-1
            )).ToList()
        );

        await JsonSerializer.SerializeAsync(stream, dto, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task ImportParkAsync(Stream stream)
    {
        ParkExportDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<ParkExportDto>(stream); }
        catch { return; }
        if (dto is null) return;

        await using var db = await _dbFactory.CreateDbContextAsync();

        var park = new IndyPark { Name = dto.Name };
        db.IndyParks.Add(park);
        await db.SaveChangesAsync();

        var structureIds = new List<int>();
        foreach (var s in dto.Structures)
        {
            var structure = new IndyStructure
            {
                ParkId           = park.Id,
                DisplayName      = s.DisplayName,
                StructureTypeKey = s.StructureTypeKey,
                SystemName       = s.SystemName,
                SecurityClass    = s.SecurityClass,
            };
            db.IndyStructures.Add(structure);
            await db.SaveChangesAsync();
            structureIds.Add(structure.Id);

            for (int slot = 0; slot < 3; slot++)
            {
                db.IndyStructureRigs.Add(new IndyStructureRig
                {
                    StructureId = structure.Id,
                    SlotIndex   = slot,
                    RigTypeId   = slot < s.RigTypeIds.Count ? s.RigTypeIds[slot] : 0,
                });
            }
            await db.SaveChangesAsync();
        }

        foreach (var a in dto.Assignments)
        {
            db.IndyCategoryAssignments.Add(new IndyCategoryAssignment
            {
                ParkId      = park.Id,
                CategoryKey = a.CategoryKey,
                StructureId = a.StructureIndex is >= 0 and int idx && idx < structureIds.Count
                              ? structureIds[idx] : null,
            });
        }
        // Seed any missing category assignments
        foreach (var (key, _) in ProductionCategories)
        {
            if (!dto.Assignments.Any(a => a.CategoryKey == key))
                db.IndyCategoryAssignments.Add(new IndyCategoryAssignment { ParkId = park.Id, CategoryKey = key });
        }
        await db.SaveChangesAsync();

        foreach (var e in dto.ItemExceptions)
        {
            db.IndyItemExceptions.Add(new IndyItemException
            {
                ParkId      = park.Id,
                TypeId      = e.TypeId,
                TypeName    = e.TypeName,
                StructureId = e.StructureIndex is >= 0 and int idx && idx < structureIds.Count
                              ? structureIds[idx] : null,
            });
        }
        await db.SaveChangesAsync();

        var item = new IndyParkListItem(park.Id, park.Name);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Parks.Add(item);
            SelectedPark = item;
        });
    }
}

// ── Export DTOs (file format) ─────────────────────────────────────────────────

file record ParkExportDto(
    string Name,
    List<StructureExportDto> Structures,
    List<CategoryAssignmentExportDto> Assignments,
    List<ItemExceptionExportDto> ItemExceptions
);

file record StructureExportDto(
    string DisplayName,
    string StructureTypeKey,
    string SystemName,
    string SecurityClass,
    List<int> RigTypeIds
);

file record CategoryAssignmentExportDto(string CategoryKey, int? StructureIndex);
file record ItemExceptionExportDto(int TypeId, string TypeName, int? StructureIndex);
