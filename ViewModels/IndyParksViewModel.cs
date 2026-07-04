using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using EveCortex.Data;
using EveCortex.Models;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

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

    public string DisplayHeader => string.IsNullOrWhiteSpace(DisplayName) ? StructureTypeLabel : DisplayName;

    public StructureVm(int id, int parkId, string displayName, string structureTypeKey,
                       string systemName, string securityClass, decimal facilityTax = 1m)
    {
        Id                = id;
        ParkId            = parkId;
        _displayName      = displayName;
        _structureTypeKey = structureTypeKey;
        _systemName       = systemName;
        _securityClass    = securityClass;
        _facilityTax      = facilityTax;
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
    public ReactiveCommand<ItemSearchResult, Unit> AddItemExceptionCommand     { get; }
    public ReactiveCommand<ItemExceptionVm, Unit>  RemoveItemExceptionCommand  { get; }

    // ── Private ───────────────────────────────────────────────────────────

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private bool _suppressSave;

    // ── Constructor ───────────────────────────────────────────────────────

    public IndyParksViewModel(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;

        LoadRigsFromSde();

        AddParkCommand            = ReactiveCommand.CreateFromTask(AddParkAsync);
        DeleteParkCommand         = ReactiveCommand.CreateFromTask(DeleteParkAsync);
        SetDefaultParkCommand     = ReactiveCommand.CreateFromTask(SetDefaultParkAsync);
        AddStructureCommand       = ReactiveCommand.CreateFromTask(AddStructureAsync);
        RemoveStructureCommand    = ReactiveCommand.CreateFromTask<StructureVm>(RemoveStructureAsync);
        SaveStructureCommand      = ReactiveCommand.CreateFromTask<StructureVm>(SaveStructureAsync);
        AddItemExceptionCommand   = ReactiveCommand.CreateFromTask<ItemSearchResult>(AddItemExceptionAsync);
        RemoveItemExceptionCommand = ReactiveCommand.CreateFromTask<ItemExceptionVm>(RemoveItemExceptionAsync);

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
    }

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
                WireStructureVm(vm);
                Structures.Add(vm);
            }

            RebuildAssignments(assignments);
            RebuildItemExceptions(exceptions);

            _suppressSave = false;
        });
    }

    private StructureVm BuildStructureVm(IndyStructure s)
        => new(s.Id, s.ParkId, s.DisplayName, s.StructureTypeKey, s.SystemName, s.SecurityClass, s.FacilityTax);

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

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Structures.Remove(vm);
            foreach (var a in Assignments)
                if (a.Selected?.Id == vm.Id) a.Selected = null;
            foreach (var e in ItemExceptions)
                if (e.Selected?.Id == vm.Id) e.Selected = null;
            RefreshAssignmentOptions();
        });
    }

    private async Task SaveStructureAsync(StructureVm vm)
    {
        await SaveStructureDbAsync(vm);
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
        entity.FacilityTax      = vm.FacilityTax;
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
        var results = await db.SdeTypes
            .Where(t => t.Published && t.Name.ToLower().Contains(lower))
            .OrderBy(t => t.Name)
            .Take(20)
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
