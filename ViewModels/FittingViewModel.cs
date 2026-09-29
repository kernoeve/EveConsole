using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Fitting;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

// ── Small pieces the view binds to ───────────────────────────────────────────

/// <summary>Type icons from the image server, fetched once each.</summary>
public static class TypeIcons
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<int, Task<Bitmap?>> Cache = new();

    public static Task<Bitmap?> GetAsync(int typeId, int size = 32) => Fetch(typeId * 1000 + size, $"https://images.evetech.net/types/{typeId}/icon?size={size}");

    /// <summary>The hull's render — the picture the fitting ring is drawn around.</summary>
    public static Task<Bitmap?> RenderAsync(int typeId) => Fetch(-typeId, $"https://images.evetech.net/types/{typeId}/render?size=512");

    private static Task<Bitmap?> Fetch(int key, string url) => Cache.GetOrAdd(key, async _ =>
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch { return null; }
    });
}

public sealed class FittingModuleRowVm : ReactiveObject
{
    public FitSlot Slot    { get; }
    public int     TypeId  { get; }
    public string  Name    { get; }
    public bool    IsEmpty => TypeId == 0;
    public string  EmptyText => $"[Empty {FittingCatalog.SlotName(Slot)} slot]";
    public bool    CanActivate { get; init; }
    public bool    CanOverheat { get; init; }
    /// <summary>Rigs and subsystems are always on; there is nothing to switch.</summary>
    public bool    CanToggle => Slot is not (FitSlot.Rig or FitSlot.Subsystem);

    private ModuleState _state = ModuleState.Online;
    public ModuleState State
    {
        get => _state;
        set
        {
            this.RaiseAndSetIfChanged(ref _state, value);
            foreach (var p in new[] { nameof(StateLabel), nameof(StateTip), nameof(HeatTip), nameof(IsOff), nameof(IsActive), nameof(IsHeated) })
                this.RaisePropertyChanged(p);
        }
    }
    public bool IsOff    => _state == ModuleState.Offline;
    public bool IsActive => _state == ModuleState.Active;
    public bool IsHeated => _state == ModuleState.Overheated;
    public string StateLabel => State switch
    {
        ModuleState.Offline    => "OFF",
        ModuleState.Online     => "ON",
        ModuleState.Active     => "ACT",
        ModuleState.Overheated => "HEAT",
        _                      => "",
    };
    public string StateTip => State switch
    {
        ModuleState.Offline    => "Offline — click to put online",
        ModuleState.Online     => CanActivate ? "Online — click to activate" : "Online — click to take offline",
        ModuleState.Active     => "Active — click to take offline",
        _                      => "Overheated — click to take offline",
    };
    public string HeatTip => IsHeated ? "Overheated — click to stop" : "Click to overheat";

    private IReadOnlyList<CatalogEntry> _charges = [];
    public IReadOnlyList<CatalogEntry> Charges
    {
        get => _charges;
        set { this.RaiseAndSetIfChanged(ref _charges, value); this.RaisePropertyChanged(nameof(HasCharges)); }
    }
    public bool HasCharges => _charges.Count > 0;

    private CatalogEntry? _charge;
    public CatalogEntry? Charge { get => _charge; set => this.RaiseAndSetIfChanged(ref _charge, value); }

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; set => this.RaiseAndSetIfChanged(ref _icon, value); }

    private Bitmap? _chargeIcon;
    public Bitmap? ChargeIcon { get => _chargeIcon; set => this.RaiseAndSetIfChanged(ref _chargeIcon, value); }

    private string _detail = "";
    /// <summary>What the module costs and does, after the last calculation: "CPU 30 · PG 1".</summary>
    public string Detail { get => _detail; set => this.RaiseAndSetIfChanged(ref _detail, value); }

    private string _price = "";
    /// <summary>What the module is worth on the market, "" when there is no price.</summary>
    public string Price { get => _price; set => this.RaiseAndSetIfChanged(ref _price, value); }

    public ReactiveCommand<Unit, Unit>? CycleStateCommand { get; set; }
    public ReactiveCommand<Unit, Unit>? HeatCommand       { get; set; }
    public ReactiveCommand<Unit, Unit>? RemoveCommand     { get; set; }
    public ReactiveCommand<Unit, Unit>? ClearChargeCommand { get; set; }

    public FittingModuleRowVm(FitSlot slot, int typeId = 0, string name = "")
    {
        Slot = slot; TypeId = typeId; Name = name;
    }
}

public sealed class FittingSlotGroupVm(string title, IReadOnlyList<FittingModuleRowVm> rows, bool over)
{
    public string Title { get; } = title;
    public IReadOnlyList<FittingModuleRowVm> Rows { get; } = rows;
    /// <summary>More fitted than the hull has slots — after a hull change, say.</summary>
    public bool Over { get; } = over;
}

public sealed class FittingDroneRowVm : ReactiveObject
{
    public int    TypeId { get; }
    public string Name   { get; }
    private int _count, _active;
    public int Count
    {
        get => _count;
        set
        {
            this.RaiseAndSetIfChanged(ref _count, Math.Max(1, value));
            this.RaisePropertyChanged(nameof(CountValue));
            if (_active > _count) Active = _count;
        }
    }
    public int Active
    {
        get => _active;
        set { this.RaiseAndSetIfChanged(ref _active, Math.Clamp(value, 0, _count)); this.RaisePropertyChanged(nameof(ActiveValue)); }
    }
    // The spinners bind decimals.
    public decimal? CountValue  { get => _count;  set => Count  = (int)(value ?? 1); }
    public decimal? ActiveValue { get => _active; set => Active = (int)(value ?? 0); }
    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; set => this.RaiseAndSetIfChanged(ref _icon, value); }
    private string _price = "";
    public string Price { get => _price; set => this.RaiseAndSetIfChanged(ref _price, value); }
    public bool IsFighter { get; init; }
    public ReactiveCommand<Unit, Unit>? RemoveCommand { get; set; }
    public FittingDroneRowVm(int typeId, string name, int count, int active) { TypeId = typeId; Name = name; _count = count; _active = active; }
}

/// <summary>A stack in the cargo hold: anything at all, fuel and ammunition as much as modules.</summary>
public sealed class FittingCargoRowVm : ReactiveObject
{
    public int    TypeId { get; }
    public string Name   { get; }
    private int _quantity;
    public int Quantity
    {
        get => _quantity;
        set { this.RaiseAndSetIfChanged(ref _quantity, Math.Max(1, value)); this.RaisePropertyChanged(nameof(QuantityValue)); }
    }
    public decimal? QuantityValue { get => _quantity; set => Quantity = (int)(value ?? 1); }
    private string _price = "";
    public string Price { get => _price; set => this.RaiseAndSetIfChanged(ref _price, value); }
    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; set => this.RaiseAndSetIfChanged(ref _icon, value); }
    public ReactiveCommand<Unit, Unit>? RemoveCommand { get; set; }
    public FittingCargoRowVm(int typeId, string name, int quantity) { TypeId = typeId; Name = name; _quantity = Math.Max(1, quantity); }
}

/// <summary>A damage profile to judge the tank against: even, one type, or the user's own mix.</summary>
public sealed record DamageProfileOption(string Name, DamageProfile? Profile)
{
    public bool IsCustom => Profile is null;
    public override string ToString() => Name;
}

public sealed class FittingImplantRowVm(int typeId, string name, bool booster)
{
    public int    TypeId    { get; } = typeId;
    public string Name      { get; } = name;
    public bool   IsBooster { get; } = booster;
    public string Kind      => IsBooster ? "Booster" : "Implant";
    public ReactiveCommand<Unit, Unit>? RemoveCommand { get; set; }
}

public enum SaveKind { App, GameNew, GameUpdate }

/// <summary>One place a fit can be saved; disabled, with the reason, where it cannot.</summary>
public sealed record SaveTarget(SaveKind Kind, string Label, long? CharacterId = null, bool Enabled = true, string? Why = null)
{
    public string Display => Why is null ? Label : $"{Label} — {Why}";
    public override string ToString() => Display;
}

public sealed record SaveRequest(string Name, IReadOnlyList<SaveTarget> Targets, SaveTarget Suggested);
public sealed record SaveChoice(string Name, SaveTarget Target);
public enum UnsavedChoice { Save, Discard, Cancel }

/// <summary>Whose skills the fit is calculated with.</summary>
public sealed record SkillSourceOption(string Name, long? CharacterId, int AllLevel)
{
    public override string ToString() => Name;
}

public sealed record SavedFitOption(long Id, string Name, string ShipName)
{
    public override string ToString() => $"{Name}  ({ShipName})";
}

public sealed record TankLayerRow(string Layer, string Hp, string Em, string Thermal, string Kinetic, string Explosive, string Ehp);

/// <summary>Every number the stats panel shows, computed together off the UI thread.</summary>
public sealed class FitSnapshot
{
    public double Cpu, CpuOut, Power, PowerOut, Calib, CalibOut;
    public int Turrets, TurretsOut, Launchers, LaunchersOut;
    public double DroneBay, DroneBayOut, Bandwidth, BandwidthOut;
    public Dictionary<FitSlot, int> Slots = new();
    public List<TankLayerRow> Tank = [];
    public double Ehp;
    public CapacitorResult? Cap;
    public DamageBreakdown WeaponDps = DamageBreakdown.Zero, DroneDps = DamageBreakdown.Zero, Volley = DamageBreakdown.Zero;
    public double Speed, Align, Signature, Warp, Mass, Agility;
    public double Range, ScanRes, MaxTargets, Sensor; public string SensorType = "";
    public Dictionary<int, string> ModuleDetail = new();   // by module index
    public TankRates? Rates;
    public double ShieldRecharge;
    /// <summary>Per layer, the share of incoming damage (in the chosen profile) that is not resisted: HP/s ÷ this = EHP/s.</summary>
    public double ShieldTaken = 1, ArmorTaken = 1, HullTaken = 1;
    public double Cargo, CargoOut;
}

// ── The tool ─────────────────────────────────────────────────────────────────

public class FittingViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly FittingsService?                _fittings;
    private readonly EveConsole.Api.EsiClient?       _esi;
    private readonly ObservableCollection<Character>?   _characters;
    private readonly ObservableCollection<Corporation>? _corporations;

    private DogmaData?      _data;
    private FittingCatalog? _catalog;
    private DogmaEngine?    _lastEngine;
    private readonly Dictionary<long, SkillSet> _skillCache = new();

    // The fit itself: the hull, its modules in the order they were added, drones, implants, cargo.
    private int _shipTypeId;
    private readonly List<FittingModuleRowVm> _modules = [];

    public ObservableCollection<FittingSlotGroupVm> SlotGroups { get; } = [];
    public ObservableCollection<FittingDroneRowVm>  Drones     { get; } = [];
    public ObservableCollection<FittingImplantRowVm> Implants  { get; } = [];
    public ObservableCollection<FittingCargoRowVm>  Cargo      { get; } = [];

    public FittingViewModel(IDbContextFactory<AppDbContext> dbFactory, FittingsService? fittings = null,
        ObservableCollection<Character>? characters = null, ObservableCollection<Corporation>? corporations = null,
        EveConsole.Api.EsiClient? esi = null)
    {
        _dbFactory    = dbFactory;
        _fittings     = fittings;
        _characters   = characters;
        _corporations = corporations;
        _esi          = esi;
        LoadDamageProfile();

        NewFitCommand      = ReactiveCommand.CreateFromTask(async () => { if (await ConfirmLeaveAsync()) NewFit(announce: true); });
        ImportEftCommand   = ReactiveCommand.CreateFromTask(ImportEftAsync);
        CopyEftCommand     = ReactiveCommand.CreateFromTask(CopyEftAsync);
        ImportEsiCommand   = ReactiveCommand.CreateFromTask(ImportEsiAsync);
        DeleteSavedCommand = ReactiveCommand.CreateFromTask(DeleteSavedAsync);
        AddSelectedCommand = ReactiveCommand.CreateFromTask(() => SelectedResult is { } r ? AddAsync(r) : Task.CompletedTask);
        foreach (var c in new ReactiveCommandBase<Unit, Unit>[] { NewFitCommand, ImportEftCommand, CopyEftCommand, ImportEsiCommand, DeleteSavedCommand, AddSelectedCommand })
            c.ThrownExceptions.Subscribe(ex => Status = ex.Message);

        this.WhenAnyValue(x => x.SearchText, x => x.KindFilter, x => x.FitsOnly)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(__ => _ = RunSearchAsync());
        this.WhenAnyValue(x => x.SelectedSkillSource).Skip(1)
            .Subscribe(_ => ScheduleRecalc());
    }

    // ── Loading ─────────────────────────────────────────────────────────────────

    private bool _loading, _loaded;
    public bool IsReady { get => _loaded; private set => this.RaiseAndSetIfChanged(ref _loaded, value); }

    /// <summary>Called when the tool is first shown: reads the SDE's dogma data and the item list.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded || _loading) return;
        _loading = true;
        Status = "Loading game data…";
        try
        {
            _data    = await Task.Run(() => DogmaData.LoadAsync(_dbFactory));
            _catalog = await Task.Run(() => FittingCatalog.LoadAsync(_data, _dbFactory));
            if (_data.Effects.Count == 0 || _catalog.Entries.Count == 0)
            {
                Status = "No game data yet — import the SDE in Settings → SDE, then reopen this tool.";
                return;
            }
            // An SDE imported by a build before this tool has the effects but not their rules,
            // and every number would be a hull's bare base value. The update that adds them runs
            // by itself after upgrading; say so rather than show numbers that look plausible.
            if (_data.Effects.Values.All(e => e.Modifiers.Count == 0))
            {
                Status = "The game data is being updated for fitting (Settings → SDE shows the progress). Reopen this tool when it finishes.";
                _data = null;
                return;
            }
            SkillSources.Clear();
            SkillSources.Add(new SkillSourceOption("All V", null, 5));
            SkillSources.Add(new SkillSourceOption("All 0", null, 0));
            await using (var db = await _dbFactory.CreateDbContextAsync())
                foreach (var c in (await db.Characters.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync()).OrderBy(c => c.Name))
                    SkillSources.Add(new SkillSourceOption(c.Name, c.Id, 0));
            _selectedSkillSource = SkillSources[0];
            this.RaisePropertyChanged(nameof(SelectedSkillSource));
            await LoadSavedListAsync();

            IsReady = true;
            Status  = "Pick a hull from the list on the left, or import a fit.";
            await RunSearchAsync();
        }
        catch (Exception ex) { Status = $"Could not load game data: {ex.Message}"; }
        finally { _loading = false; }
    }

    // ── Finder ──────────────────────────────────────────────────────────────────

    public static IReadOnlyList<string> KindFilters { get; } =
        ["All", "Hulls", "Modules", "Rigs", "Subsystems", "Charges", "Drones", "Implants", "Boosters", "Other items"];

    private string _searchText = "";
    public string SearchText { get => _searchText; set => this.RaiseAndSetIfChanged(ref _searchText, value); }

    private string _kindFilter = "All";
    public string KindFilter { get => _kindFilter; set => this.RaiseAndSetIfChanged(ref _kindFilter, value); }

    private bool _fitsOnly = true;
    /// <summary>Only modules, rigs and subsystems the current hull could take.</summary>
    public bool FitsOnly { get => _fitsOnly; set => this.RaiseAndSetIfChanged(ref _fitsOnly, value); }

    public ObservableCollection<CatalogEntry> SearchResults { get; } = [];

    private CatalogEntry? _selectedResult;
    public CatalogEntry? SelectedResult { get => _selectedResult; set => this.RaiseAndSetIfChanged(ref _selectedResult, value); }

    private async Task RunSearchAsync()
    {
        if (_catalog is null || _data is null) return;
        HashSet<CatalogKind>? kinds = KindFilter switch
        {
            "Hulls"      => [CatalogKind.Hull],
            "Modules"    => [CatalogKind.Module],
            "Rigs"       => [CatalogKind.Rig],
            "Subsystems" => [CatalogKind.Subsystem],
            "Charges"    => [CatalogKind.Charge],
            "Drones"     => [CatalogKind.Drone, CatalogKind.Fighter],
            "Implants"   => [CatalogKind.Implant],
            "Boosters"   => [CatalogKind.Booster],
            "Other items" => [CatalogKind.Item],
            _            => null,
        };
        // An empty search with no category would list seven thousand items; ask for a word first,
        // unless a category narrows it.
        if (SearchText.Trim().Length < 2 && kinds is null) { SearchResults.Clear(); return; }
        if (kinds is not null && SearchText.Trim().Length == 0 && FinderSlot is null && kinds.Contains(CatalogKind.Module)) { SearchResults.Clear(); return; }

        var found = _catalog.Search(SearchText, kinds)
            .Where(f => FinderSlot is not { } only || f.Slot == only)
            .Take(400).ToList();
        if (FitsOnly && _lastEngine is { } engine)
        {
            // Slot and rig-size rules only; a full slot does not hide what could go in it.
            await _data.LoadTypesAsync(found.Where(f => f.Kind is CatalogKind.Rig or CatalogKind.Subsystem or CatalogKind.Module).Select(f => f.TypeId));
            var shipSlots = new FitStats(engine);
            found = found.Where(f => f.Kind switch
            {
                CatalogKind.Module or CatalogKind.Rig => shipSlots.Slots(f.Slot) > 0 && RigSizeFits(engine, f.TypeId) && HullAllows(engine, f.TypeId),
                CatalogKind.Subsystem => FitsHull(engine, f.TypeId),
                _ => true,
            }).ToList();
        }
        SearchResults.Clear();
        foreach (var f in found.Take(200)) SearchResults.Add(f);
    }

    private bool RigSizeFits(DogmaEngine e, int typeId)
    {
        if (_data!.Attribute("rigSize")?.Id is not { } rs) return true;
        var t = _data.Type(typeId);
        return t.Attr(rs) is not { } size || e.Value(e.Ship, rs) is var hull && (hull <= 0 || hull == size);
    }

    private bool HullAllows(DogmaEngine e, int typeId)
    {
        var t = _data!.Type(typeId);
        var groups = _data.AttributesByName.Values.Where(a => a.Name.StartsWith("canFitShipGroup")).Select(a => t.Attr(a.Id)).OfType<double>().ToList();
        var hulls  = _data.AttributesByName.Values.Where(a => a.Name.StartsWith("canFitShipType")).Select(a => t.Attr(a.Id)).OfType<double>().ToList();
        return (groups.Count == 0 && hulls.Count == 0) || groups.Contains(e.Ship.Type.GroupId) || hulls.Contains(e.Ship.Type.Id);
    }

    private bool FitsHull(DogmaEngine e, int typeId) =>
        _data!.Attribute("fitsToShipType")?.Id is not { } f || _data.Type(typeId).Attr(f) is not { } hull || (int)hull == e.Ship.Type.Id;

    // ── Header: hull, name, pilot ───────────────────────────────────────────────

    private string _shipName = "";
    public string ShipName { get => _shipName; private set => this.RaiseAndSetIfChanged(ref _shipName, value); }
    public bool HasShip => _shipTypeId != 0;

    private Bitmap? _shipIcon;
    public Bitmap? ShipIcon { get => _shipIcon; private set => this.RaiseAndSetIfChanged(ref _shipIcon, value); }

    private string _fitName = "";
    public string FitName { get => _fitName; set => this.RaiseAndSetIfChanged(ref _fitName, value); }

    public ObservableCollection<SkillSourceOption> SkillSources { get; } = [];
    private SkillSourceOption? _selectedSkillSource;
    public SkillSourceOption? SelectedSkillSource { get => _selectedSkillSource; set => this.RaiseAndSetIfChanged(ref _selectedSkillSource, value); }

    private string _status = "";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    // ── Editing ─────────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> AddSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> NewFitCommand      { get; }

    /// <summary>The module row the user last clicked — where a charge picked in the finder goes.</summary>
    private FittingModuleRowVm? _selectedModule;
    public FittingModuleRowVm? SelectedModule { get => _selectedModule; set => this.RaiseAndSetIfChanged(ref _selectedModule, value); }

    public async Task AddAsync(CatalogEntry entry)
    {
        if (_data is null || _catalog is null) return;
        await _data.LoadTypesAsync([entry.TypeId]);
        var type = _data.Type(entry.TypeId);

        switch (entry.Kind)
        {
            case CatalogKind.Hull:
                // A different hull starts a new fit: what is fitted belongs to the old one.
                if (_shipTypeId != 0 && _shipTypeId != entry.TypeId)
                {
                    if (!await ConfirmLeaveAsync()) return;
                    NewFit(announce: false);
                }
                SetShip(entry.TypeId);
                if (FitName.Length == 0) FitName = $"New {entry.Name}";
                MarkClean();
                Status = $"{entry.Name}. Now add modules from the list.";
                break;

            case CatalogKind.Module or CatalogKind.Rig or CatalogKind.Subsystem:
                if (_shipTypeId == 0) { Status = "Pick a hull first."; return; }
                // Checked against the fit as it is now, not the last debounced calculation, which
                // may not include a module added a moment ago.
                _recalcCts?.Cancel();
                await RecalculateAsync(CancellationToken.None);
                if (_lastEngine is { } engine && await _catalog.WhyNotAsync(engine, entry.TypeId) is { } why)
                {
                    Status = why;
                    return;
                }
                await AddModuleAsync(entry.TypeId, EftFormat.DefaultState(_data, type), null);
                Status = $"Added {entry.Name}.";
                break;

            case CatalogKind.Charge:
                await LoadChargeAsync(entry);
                break;

            case CatalogKind.Drone or CatalogKind.Fighter:
                if (Drones.FirstOrDefault(d => d.TypeId == entry.TypeId) is { } existing) existing.Count++;
                else AddDrone(entry.TypeId, entry.Name, 1, 1);
                Status = $"Added {entry.Name}.";
                break;

            case CatalogKind.Implant or CatalogKind.Booster:
                if (Implants.Any(i => i.TypeId == entry.TypeId)) { Status = $"{entry.Name} is already plugged in."; return; }
                AddImplant(entry.TypeId, entry.Name, entry.Kind == CatalogKind.Booster);
                Status = $"Added {entry.Name}.";
                break;

            case CatalogKind.Item:
                if (_shipTypeId == 0) { Status = "Pick a hull first."; return; }
                AddToCargo(entry, 1);
                break;
        }
        RebuildSlots();
        ScheduleRecalc();
    }

    private void SetShip(int typeId)
    {
        _shipTypeId = typeId;
        ShipName = _catalog?.Find(typeId)?.Name ?? _data?.Type(typeId).Name ?? "";
        this.RaisePropertyChanged(nameof(HasShip));
        _ = LoadShipIconAsync(typeId);
    }

    private async Task LoadShipIconAsync(int typeId)
    {
        var bmp = await TypeIcons.GetAsync(typeId, 64);
        if (typeId == _shipTypeId) ShipIcon = bmp;
        var render = await TypeIcons.RenderAsync(typeId);
        if (typeId == _shipTypeId) ShipRender = render;
    }

    private Bitmap? _shipRender;
    public Bitmap? ShipRender { get => _shipRender; private set => this.RaiseAndSetIfChanged(ref _shipRender, value); }

    private async Task AddModuleAsync(int typeId, ModuleState state, int? chargeTypeId)
    {
        var type = _data!.Type(typeId);
        var slot = DogmaEngine.SlotOf(_data, type);
        var row = new FittingModuleRowVm(slot, typeId, type.Name)
        {
            CanActivate = EftFormat.CanActivate(_data, type),
            CanOverheat = type.EffectIds.Any(id => _data.Effects.TryGetValue(id, out var e) && e.Category == 5),
        };
        row.State = state;
        row.CycleStateCommand  = ReactiveCommand.Create(() => SetState(row, NextState(row)));
        // Overheat is a switch of its own, as it is in the game: heating a module that is not on
        // turns it on as well, and cooling it leaves it running.
        row.HeatCommand        = ReactiveCommand.Create(() =>
            SetState(row, row.State == ModuleState.Overheated ? ModuleState.Active : ModuleState.Overheated));
        row.RemoveCommand      = ReactiveCommand.Create(() => Remove(row));
        row.ClearChargeCommand = ReactiveCommand.Create(() => { row.Charge = null; });
        row.WhenAnyValue(r => r.Charge).Skip(1).Subscribe(_ => ScheduleRecalc());
        row.WhenAnyValue(r => r.Charge).Subscribe(c => _ = LoadChargeIconAsync(row, c));
        row.WhenAnyValue(r => r.Icon).Skip(1).Subscribe(_ => RebuildRing());
        _modules.Add(row);
        _ = LoadRowIconAsync(row);

        row.Charges = await _catalog!.ChargesForAsync(typeId);
        if (chargeTypeId is { } c) row.Charge = row.Charges.FirstOrDefault(x => x.TypeId == c) ?? _catalog.Find(c);
    }

    private static async Task LoadRowIconAsync(FittingModuleRowVm row) => row.Icon = await TypeIcons.GetAsync(row.TypeId);

    private async Task LoadChargeIconAsync(FittingModuleRowVm row, CatalogEntry? charge)
    {
        row.ChargeIcon = charge is null ? null : await TypeIcons.GetAsync(charge.TypeId);
        RebuildRing();
    }

    private async Task LoadChargeAsync(CatalogEntry charge)
    {
        var targets = SelectedModule is { IsEmpty: false } sel && sel.Charges.Any(c => c.TypeId == charge.TypeId)
            ? [sel]
            : _modules.Where(m => m.Charges.Any(c => c.TypeId == charge.TypeId)).ToList();
        if (targets.Count == 0) { Status = $"Nothing fitted can load {charge.Name}."; return; }
        foreach (var t in targets) t.Charge = t.Charges.First(c => c.TypeId == charge.TypeId);
        Status = targets.Count == 1 ? $"Loaded {charge.Name}." : $"Loaded {charge.Name} in {targets.Count} modules.";
        await Task.CompletedTask;
    }

    /// <summary>Puts <paramref name="quantity"/> of anything in the hold, on an existing stack if there is one.</summary>
    public void AddToCargo(CatalogEntry entry, int quantity)
    {
        if (Cargo.FirstOrDefault(c => c.TypeId == entry.TypeId) is { } stack) stack.Quantity += quantity;
        else AddCargo(entry.TypeId, entry.Name, quantity);
        Status = $"{entry.Name} in the cargo hold.";
        ScheduleRecalc();
    }

    private void AddCargo(int typeId, string name, int quantity)
    {
        var row = new FittingCargoRowVm(typeId, name, quantity);
        row.RemoveCommand = ReactiveCommand.Create(() => { Cargo.Remove(row); ScheduleRecalc(); });
        row.WhenAnyValue(r => r.Quantity).Skip(1).Subscribe(_ => ScheduleRecalc());
        Cargo.Add(row);
        _ = Task.Run(async () => { var b = await TypeIcons.GetAsync(typeId); Dispatcher.UIThread.Post(() => row.Icon = b); });
    }

    public ReactiveCommand<Unit, Unit> AddToCargoCommand => _addToCargo ??= ReactiveCommand.Create(() =>
    {
        if (SelectedResult is not { } r) return;
        if (_shipTypeId == 0) { Status = "Pick a hull first."; return; }
        AddToCargo(r, 1);
    });
    private ReactiveCommand<Unit, Unit>? _addToCargo;

    private void AddDrone(int typeId, string name, int count, int active)
    {
        var row = new FittingDroneRowVm(typeId, name, count, active)
        {
            IsFighter = _data is not null && _data.TryType(typeId, out var t) && t.CategoryId == DogmaData.CategoryFighter,
        };
        row.RemoveCommand = ReactiveCommand.Create(() => { Drones.Remove(row); ScheduleRecalc(); });
        row.WhenAnyValue(r => r.Count, r => r.Active).Skip(1).Subscribe(_ => ScheduleRecalc());
        Drones.Add(row);
        _ = Task.Run(async () => { var b = await TypeIcons.GetAsync(typeId); Dispatcher.UIThread.Post(() => row.Icon = b); });
    }

    private void AddImplant(int typeId, string name, bool booster)
    {
        var row = new FittingImplantRowVm(typeId, name, booster);
        row.RemoveCommand = ReactiveCommand.Create(() => { Implants.Remove(row); ScheduleRecalc(); });
        Implants.Add(row);
    }

    private void NewFit(bool announce)
    {
        _shipTypeId = 0; ShipName = ""; ShipIcon = null; ShipRender = null; SelectedModule = null; FitName = "";
        _modules.Clear(); Drones.Clear(); Implants.Clear(); Cargo.Clear(); _gameSource = null;
        _lastEngine = null; _loadedSavedId = null;
        this.RaisePropertyChanged(nameof(HasShip));
        RebuildSlots();
        Stats = null;
        if (announce) Status = "New fit. Pick a hull.";
    }

    // ── The ring ────────────────────────────────────────────────────────────────

    private IReadOnlyList<Controls.FittingSlot> _ringSlots = [];
    /// <summary>The slots as the fitting ring draws them, in the game's arrangement.</summary>
    public IReadOnlyList<Controls.FittingSlot> RingSlots { get => _ringSlots; private set => this.RaiseAndSetIfChanged(ref _ringSlots, value); }

    private void RebuildRing()
    {
        var slots = new List<Controls.FittingSlot>();
        foreach (var group in SlotGroups)
            for (var i = 0; i < group.Rows.Count; i++)
            {
                var r = group.Rows[i];
                var band = r.Slot switch
                {
                    FitSlot.High      => Controls.FittingBand.High,
                    FitSlot.Mid       => Controls.FittingBand.Mid,
                    FitSlot.Low       => Controls.FittingBand.Low,
                    FitSlot.Rig       => Controls.FittingBand.Rig,
                    FitSlot.Subsystem => Controls.FittingBand.Subsystem,
                    _                 => Controls.FittingBand.Service,
                };
                var activity = r.IsEmpty || !r.CanToggle ? Controls.SlotActivity.None : r.State switch
                {
                    ModuleState.Offline    => Controls.SlotActivity.Offline,
                    ModuleState.Active     => Controls.SlotActivity.Active,
                    ModuleState.Overheated => Controls.SlotActivity.Overheated,
                    _                      => Controls.SlotActivity.Online,
                };
                var detail = r.IsEmpty ? "Click to choose a module for this slot"
                    : string.Join("  ·  ", new[] { r.CanToggle ? r.State.ToString() : null, r.Charge?.Name, r.Detail }
                        .Where(x => !string.IsNullOrEmpty(x)));
                slots.Add(new Controls.FittingSlot(band, i, r.TypeId, r.Name, r.Icon, false, activity, r.ChargeIcon, detail, r));
            }
        RingSlots = slots;
    }

    /// <summary>Left-click on the ring: a filled slot is switched the way the game switches it
    /// (on, active, off); an empty one points the finder at modules for that slot.</summary>
    public ReactiveCommand<Controls.FittingSlot, Unit> RingSlotClickedCommand => _ringClicked ??= ReactiveCommand.Create<Controls.FittingSlot>(RingSlotClicked);
    private ReactiveCommand<Controls.FittingSlot, Unit>? _ringClicked;

    public void RingSlotClicked(Controls.FittingSlot slot)
    {
        if (slot.Tag is FittingModuleRowVm { IsEmpty: false } row)
        {
            SelectedModule = row;
            if (row.CanToggle) SetState(row, NextState(row));
            return;
        }
        if (slot.Tag is FittingModuleRowVm empty)
        {
            FinderSlot = empty.Slot;
            KindFilter = empty.Slot switch { FitSlot.Rig => "Rigs", FitSlot.Subsystem => "Subsystems", _ => "Modules" };
            FitsOnly   = true;
            Status     = $"Choose a {FittingCatalog.SlotName(empty.Slot)} slot module from Items.";
        }
    }

    public void SetState(FittingModuleRowVm row, ModuleState state)
    {
        row.State = state;
        RebuildRing();
        ScheduleRecalc();
    }

    public void Remove(FittingModuleRowVm row)
    {
        _modules.Remove(row);
        if (SelectedModule == row) SelectedModule = null;
        RebuildSlots();
        ScheduleRecalc();
    }

    private static ModuleState NextState(FittingModuleRowVm row) => row.State switch
    {
        ModuleState.Offline                     => ModuleState.Online,
        ModuleState.Online when row.CanActivate => ModuleState.Active,
        _                                       => ModuleState.Offline,
    };

    private FitSlot? _finderSlot;
    /// <summary>When set, the finder lists only modules for this slot — set by clicking an empty
    /// slot on the ring, cleared by the ✕ beside the note it shows.</summary>
    public FitSlot? FinderSlot
    {
        get => _finderSlot;
        set
        {
            this.RaiseAndSetIfChanged(ref _finderSlot, value);
            this.RaisePropertyChanged(nameof(FinderSlotText));
            this.RaisePropertyChanged(nameof(HasFinderSlot));
            _ = RunSearchAsync();
        }
    }
    public bool   HasFinderSlot  => _finderSlot is not null;
    public string FinderSlotText => _finderSlot is { } s ? $"{char.ToUpper(FittingCatalog.SlotName(s)[0])}{FittingCatalog.SlotName(s)[1..]} slot modules only" : "";
    public ReactiveCommand<Unit, Unit> ClearFinderSlotCommand => _clearFinderSlot ??= ReactiveCommand.Create(() => { FinderSlot = null; });
    private ReactiveCommand<Unit, Unit>? _clearFinderSlot;

    /// <summary>Slot sections for the hull: what is fitted, then an empty row per free slot.</summary>
    private void RebuildSlots()
    {
        SlotGroups.Clear();
        if (_shipTypeId == 0) { RebuildRing(); return; }
        var counts = Stats?.Slots ?? new Dictionary<FitSlot, int>();
        foreach (var slot in new[] { FitSlot.High, FitSlot.Mid, FitSlot.Low, FitSlot.Rig, FitSlot.Subsystem, FitSlot.Service })
        {
            var fitted = _modules.Where(m => m.Slot == slot).ToList();
            var total  = counts.GetValueOrDefault(slot);
            if (total == 0 && fitted.Count == 0) continue;
            var rows = fitted.Concat(Enumerable.Range(0, Math.Max(0, total - fitted.Count)).Select(_ => new FittingModuleRowVm(slot))).ToList();
            var name = slot == FitSlot.Mid ? "Mid" : char.ToUpper(FittingCatalog.SlotName(slot)[0]) + FittingCatalog.SlotName(slot)[1..];
            SlotGroups.Add(new FittingSlotGroupVm($"{name} slots  {fitted.Count} / {total}", rows, fitted.Count > total));
        }
        RebuildRing();
    }

    // ── Writing to the game ─────────────────────────────────────────────────────

    /// <summary>The in-game fitting this fit was opened from, when it was one of a character's own.</summary>
    public sealed record GameSource(long CharacterId, string CharacterName, int FittingId, string Name);

    private GameSource? _gameSource;
    private GameSource? CurrentGameSource
    {
        get => _gameSource;
        set
        {
            _gameSource = value;
            this.RaisePropertyChanged(nameof(HasGameSource));
            this.RaisePropertyChanged(nameof(GameSourceText));
        }
    }
    public bool   HasGameSource  => _gameSource is not null;
    public string GameSourceText => _gameSource is { } g ? $"From {g.CharacterName}'s fittings in the game: {g.Name}" : "";

    // ── Saving: where, and whether there is anything unsaved ────────────────────

    /// <summary>Asks the view where to save; null when cancelled.</summary>
    public Interaction<SaveRequest, SaveChoice?> AskSave { get; } = new();
    /// <summary>Asks the view what to do with unsaved changes before they are replaced.</summary>
    public Interaction<string, UnsavedChoice> AskUnsaved { get; } = new();

    private ReactiveCommand<Unit, Unit> Guarded(ReactiveCommand<Unit, Unit> c)
    {
        c.ThrownExceptions.Subscribe(ex => Status = ex.Message);
        return c;
    }

    /// <summary>The fit as last loaded or saved, in EFT — what "changed" is measured against.</summary>
    private string _baseline = "";

    private void MarkClean() => _baseline = _data is null || _shipTypeId == 0 ? "" : EftFormat.Write(CurrentFit(), _data);

    /// <summary>
    /// Whether the fit has changed since it was loaded or saved. A bare hull with nothing on it is
    /// not worth asking about, and module states (on, active, overheated) are not part of any fit
    /// format, so switching modules on and off does not count.
    /// </summary>
    public bool IsDirty
    {
        get
        {
            if (_data is null || _shipTypeId == 0) return false;
            var fit = CurrentFit();
            if (fit.Modules.Count + fit.Drones.Count + fit.Cargo.Count + fit.Implants.Count + fit.Boosters.Count == 0) return false;
            return EftFormat.Write(fit, _data) != _baseline;
        }
    }

    /// <summary>
    /// Before anything replaces the fit: offers to save unsaved changes. True to go ahead —
    /// saved, or the user chose not to — false to stay where they are.
    /// </summary>
    public async Task<bool> ConfirmLeaveAsync()
    {
        if (!IsDirty) return true;
        var name = FitName.Trim().Length > 0 ? FitName.Trim() : ShipName;
        return await AskUnsaved.Handle($"\"{name}\" has changes that have not been saved.") switch
        {
            UnsavedChoice.Save    => await SaveInteractiveAsync(),
            UnsavedChoice.Discard => true,
            _                     => false,
        };
    }

    /// <summary>
    /// Where a fit can be saved: in EVE Console; as a new fitting on any character that has
    /// granted the fittings write scope; and, for a fit opened from a character's fittings, over
    /// that fitting. Corporation fittings are not offered — ESI has no way to write them.
    /// </summary>
    private async Task<List<SaveTarget>> SaveTargetsAsync()
    {
        var targets = new List<SaveTarget> { new(SaveKind.App, "EVE Console (this app)") };
        if (_gameSource is { } src)
            targets.Add(new(SaveKind.GameUpdate, $"In game: replace \"{src.Name}\" in {src.CharacterName}'s fittings", src.CharacterId));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var chars = await db.Characters.AsNoTracking().Select(c => new { c.Id, c.Name, c.GrantedScopes, c.RefreshToken }).ToListAsync();
        foreach (var c in chars.OrderBy(c => c.Name))
        {
            var why = c.RefreshToken.Length == 0 ? "token expired — re-authorise in Settings → ESI Tokens"
                    : !c.GrantedScopes.Split(' ').Contains(GameFittings.WriteScope) ? "fittings write scope not granted — update the character in Settings → ESI Tokens"
                    : null;
            targets.Add(new(SaveKind.GameNew, $"In game: new fitting on {c.Name}", c.Id, why is null, why));
        }
        return targets;
    }

    public ReactiveCommand<Unit, Unit> SaveCommand => _save ??= Guarded(ReactiveCommand.CreateFromTask(async () => { await SaveInteractiveAsync(); }));
    private ReactiveCommand<Unit, Unit>? _save;

    /// <summary>Asks where to save, and saves there. True when it was saved.</summary>
    public async Task<bool> SaveInteractiveAsync()
    {
        if (_data is null || _shipTypeId == 0) { Status = "Nothing to save yet."; return false; }
        var targets = await SaveTargetsAsync();
        var suggested = _gameSource is not null ? targets.First(t => t.Kind == SaveKind.GameUpdate)
            : SelectedSkillSource?.CharacterId is { } pilot && _lastSavedToApp is null
                ? targets.FirstOrDefault(t => t.Kind == SaveKind.GameNew && t.CharacterId == pilot && t.Enabled) ?? targets[0]
                : targets[0];
        var name = FitName.Trim().Length > 0 ? FitName.Trim() : $"{ShipName} fit";
        var choice = await AskSave.Handle(new SaveRequest(name, targets, suggested));
        if (choice is null) return false;
        FitName = choice.Name;

        return choice.Target.Kind switch
        {
            SaveKind.App        => await SaveToAppAsync(),
            SaveKind.GameNew    => await SaveToGameAsync(choice.Target.CharacterId!.Value),
            _                   => await UpdateInGameAsync(),
        };
    }

    private long? _lastSavedToApp;

    /// <summary>Saves as a new fitting on <paramref name="characterId"/>.</summary>
    private async Task<bool> SaveToGameAsync(long characterId)
    {
        if (_data is null || _esi is null) { Status = "The game cannot be reached from here."; return false; }
        if (await WritableCharacterAsync(characterId) is not { } ch) return false;

        var fit = CurrentFit();
        GameFittings.ToItems(fit, _data, out var skipped);
        Status = $"Saving to {ch.Name}'s fittings…";
        var (fittingId, error) = await GameFittings.CreateAsync(_esi, ch.Id, fit, _data, "Saved from EVE Console");
        if (error is not null) { Status = error; return false; }
        CurrentGameSource = new GameSource(ch.Id, ch.Name, fittingId!.Value, fit.Name);
        MarkClean();
        Status = $"Saved to {ch.Name}'s fittings in the game." + Skipped(skipped);
        return true;
    }

    /// <summary>
    /// Replaces the in-game fitting this fit came from. ESI cannot edit a fitting, so the new one
    /// is created first and the old one deleted only once that has worked.
    /// </summary>
    private async Task<bool> UpdateInGameAsync()
    {
        if (_data is null || _esi is null || CurrentGameSource is not { } src) { Status = "This fit was not opened from the game."; return false; }
        if (await WritableCharacterAsync(src.CharacterId) is not { } ch) return false;

        var fit = CurrentFit();
        GameFittings.ToItems(fit, _data, out var skipped);
        Status = $"Updating {ch.Name}'s fitting…";
        var (fittingId, error) = await GameFittings.CreateAsync(_esi, ch.Id, fit, _data, "Saved from EVE Console");
        if (error is not null) { Status = error; return false; }
        var deleteError = await GameFittings.DeleteAsync(_esi, ch.Id, src.FittingId);
        CurrentGameSource = new GameSource(ch.Id, ch.Name, fittingId!.Value, fit.Name);
        MarkClean();
        Status = deleteError is null
            ? $"Updated {fit.Name} in {ch.Name}'s fittings." + Skipped(skipped)
            : $"Saved the new fitting, but {deleteError[0..1].ToLower()}{deleteError[1..]} Delete the old one in the game.";
        return true;
    }

    /// <summary>A character that can have fittings written to it, or why not.</summary>
    private async Task<(long Id, string Name)?> WritableCharacterAsync(long characterId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ch = await db.Characters.AsNoTracking().Where(c => c.Id == characterId)
            .Select(c => new { c.Id, c.Name, c.GrantedScopes, c.RefreshToken }).FirstOrDefaultAsync();
        if (ch is null) { Status = "That character is not in EVE Console."; return null; }
        if (ch.RefreshToken.Length == 0) { Status = $"{ch.Name}'s token has expired — re-authorise in Settings → ESI Tokens."; return null; }
        if (!ch.GrantedScopes.Split(' ').Contains(GameFittings.WriteScope))
        {
            Status = $"{ch.Name} has not granted the fittings write scope — update the character in Settings → ESI Tokens.";
            return null;
        }
        return (ch.Id, ch.Name);
    }

    private static string Skipped(IReadOnlyList<string> skipped) =>
        skipped.Count == 0 ? "" : $" Not saved (a fitting has no place for them): {string.Join(", ", skipped)}.";

    // ── Value ───────────────────────────────────────────────────────────────────

    private string _valueText = "", _valueBreakdown = "", _valueBasis = "";
    public string ValueText      { get => _valueText;      private set => this.RaiseAndSetIfChanged(ref _valueText, value); }
    public string ValueBreakdown { get => _valueBreakdown; private set => this.RaiseAndSetIfChanged(ref _valueBreakdown, value); }
    /// <summary>Which prices these are, and what could not be priced.</summary>
    public string ValueBasis     { get => _valueBasis;     private set => this.RaiseAndSetIfChanged(ref _valueBasis, value); }

    /// <summary>
    /// Prices every row and totals the fit as the game stores a fitting — hull, fitted modules,
    /// drone and fighter bays, and cargo, including a load of each charge fitted but not carried.
    /// Implants are the pilot's, so they are totalled apart.
    /// </summary>
    private async Task ApplyPricesAsync(FitDefinition fit)
    {
        if (_data is null) return;
        var items = GameFittings.ToItems(fit, _data, out _);
        var ids = items.Select(i => i.TypeId).Concat(fit.Implants).Concat(fit.Boosters).Append(fit.ShipTypeId)
            .Concat(fit.Modules.Where(m => m.ChargeTypeId is not null).Select(m => m.ChargeTypeId!.Value));
        var prices = await FitPricing.LoadAsync(_dbFactory, ids);

        string Isk(double? v) => v is { } x ? IskText.Compact(x) : "";
        foreach (var m in _modules) m.Price = Isk(prices.Of(m.TypeId));
        foreach (var d in Drones)   d.Price = prices.Of(d.TypeId) is { } p ? Isk(p * d.Count) : "";
        foreach (var c in Cargo)    c.Price = prices.Of(c.TypeId) is { } p ? Isk(p * c.Quantity) : "";

        double Sum(IEnumerable<(int TypeId, int Qty)> xs) => xs.Sum(x => (prices.Of(x.TypeId) ?? 0) * x.Qty);
        var hull    = prices.Of(fit.ShipTypeId) ?? 0;
        var fitted  = Sum(items.Where(i => i.Flag.Contains("Slot")).Select(i => (i.TypeId, i.Quantity)));
        var bays    = Sum(items.Where(i => i.Flag is "DroneBay" or "FighterBay").Select(i => (i.TypeId, i.Quantity)));
        var cargo   = Sum(items.Where(i => i.Flag == "Cargo").Select(i => (i.TypeId, i.Quantity)));
        var implant = Sum(fit.Implants.Concat(fit.Boosters).Select(id => (id, 1)));
        var unpriced = items.Select(i => i.TypeId).Append(fit.ShipTypeId).Distinct().Count(id => prices.Of(id) is null);

        if (prices.ByType.Count == 0)
        {
            ValueText = ""; ValueBreakdown = "";
            ValueBasis = "No market prices yet — choose an asset value market in Settings → Market.";
            return;
        }
        ValueText      = $"{IskText.Compact(hull + fitted + bays + cargo)} ISK";
        ValueBreakdown = $"Hull {IskText.Compact(hull)}    Modules {IskText.Compact(fitted)}"
                       + (bays  > 0 ? $"    Drones {IskText.Compact(bays)}" : "")
                       + (cargo > 0 ? $"    Cargo {IskText.Compact(cargo)}" : "")
                       + (implant > 0 ? $"\nImplants and boosters {IskText.Compact(implant)} (the pilot's, not in the total)" : "");
        ValueBasis     = $"Prices: {prices.Basis}" + (unpriced > 0 ? $" — {unpriced} item(s) have no price" : "");
    }

    // ── Damage profile ──────────────────────────────────────────────────────────

    public IReadOnlyList<DamageProfileOption> DamageProfiles { get; } =
    [
        new("Even (25% each)", DamageProfile.Uniform),
        new("EM",        new DamageProfile(100, 0, 0, 0)),
        new("Thermal",   new DamageProfile(0, 100, 0, 0)),
        new("Kinetic",   new DamageProfile(0, 0, 100, 0)),
        new("Explosive", new DamageProfile(0, 0, 0, 100)),
        new("Custom",    null),
    ];

    private const string ProfileKey = "fitting.damage_profile";

    private DamageProfileOption? _selectedProfile;
    /// <summary>The damage the tank is judged against: EHP, and repairs and regeneration as EHP/s.</summary>
    public DamageProfileOption? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedProfile, value);
            this.RaisePropertyChanged(nameof(IsCustomProfile));
            ProfileChanged();
        }
    }
    public bool IsCustomProfile => _selectedProfile?.IsCustom == true;

    private decimal _customEm = 25, _customThermal = 25, _customKinetic = 25, _customExplosive = 25;
    public decimal CustomEm        { get => _customEm;        set { this.RaiseAndSetIfChanged(ref _customEm,        Math.Max(0, value)); ProfileChanged(); } }
    public decimal CustomThermal   { get => _customThermal;   set { this.RaiseAndSetIfChanged(ref _customThermal,   Math.Max(0, value)); ProfileChanged(); } }
    public decimal CustomKinetic   { get => _customKinetic;   set { this.RaiseAndSetIfChanged(ref _customKinetic,   Math.Max(0, value)); ProfileChanged(); } }
    public decimal CustomExplosive { get => _customExplosive; set { this.RaiseAndSetIfChanged(ref _customExplosive, Math.Max(0, value)); ProfileChanged(); } }

    public DamageProfile CurrentProfile => _selectedProfile?.Profile
        ?? (CustomEm + CustomThermal + CustomKinetic + CustomExplosive > 0
            ? new DamageProfile((double)CustomEm, (double)CustomThermal, (double)CustomKinetic, (double)CustomExplosive)
            : DamageProfile.Uniform);

    private bool _restoringProfile;

    private void ProfileChanged()
    {
        if (_restoringProfile) return;
        // Remembered per machine, as the other tools remember their choices: "name|em|th|kin|exp".
        try { UiState.Set(ProfileKey, FormattableString.Invariant($"{_selectedProfile?.Name}|{CustomEm}|{CustomThermal}|{CustomKinetic}|{CustomExplosive}")); } catch { }
        ScheduleRecalc();
    }

    private void LoadDamageProfile()
    {
        _restoringProfile = true;
        try
        {
            var parts = (UiState.Get(ProfileKey) ?? "").Split('|');
            if (parts.Length == 5)
            {
                decimal Num(string s) => decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 25;
                CustomEm = Num(parts[1]); CustomThermal = Num(parts[2]); CustomKinetic = Num(parts[3]); CustomExplosive = Num(parts[4]);
            }
            SelectedProfile = DamageProfiles.FirstOrDefault(p => p.Name == parts[0]) ?? DamageProfiles[0];
        }
        catch { SelectedProfile = DamageProfiles[0]; }
        finally { _restoringProfile = false; }
    }

    // ── Calculation ─────────────────────────────────────────────────────────────

    private FitSnapshot? _stats;
    public FitSnapshot? Stats
    {
        get => _stats;
        private set { this.RaiseAndSetIfChanged(ref _stats, value); this.RaisePropertyChanged(nameof(HasStats)); RaiseStatText(); }
    }
    public bool HasStats => _stats is not null;

    private CancellationTokenSource? _recalcCts;

    private void ScheduleRecalc()
    {
        _recalcCts?.Cancel();
        var cts = _recalcCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(60, cts.Token);
                await Dispatcher.UIThread.InvokeAsync(() => RecalculateAsync(cts.Token));
            }
            catch (OperationCanceledException) { }
        });
    }

    public FitDefinition CurrentFit()
    {
        var fit = new FitDefinition { ShipTypeId = _shipTypeId, Name = FitName };
        fit.Modules.AddRange(_modules.Select(m => new FitModule(m.TypeId, m.State, m.Charge?.TypeId)));
        fit.Drones.AddRange(Drones.Select(d => new FitDrone(d.TypeId, d.Count, d.Active)));
        fit.Implants.AddRange(Implants.Where(i => !i.IsBooster).Select(i => i.TypeId));
        fit.Boosters.AddRange(Implants.Where(i => i.IsBooster).Select(i => i.TypeId));
        fit.Cargo.AddRange(Cargo.Select(c => (c.TypeId, c.Quantity)));
        return fit;
    }

    private async Task<SkillSet> SkillsAsync()
    {
        var src = SelectedSkillSource ?? new SkillSourceOption("All V", null, 5);
        if (src.CharacterId is not { } id) return SkillSet.AllAt(_data!, src.AllLevel);
        if (_skillCache.TryGetValue(id, out var cached)) return cached;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var levels = await db.EsiSkills.AsNoTracking().Where(s => s.CharacterId == id)
            .ToDictionaryAsync(s => s.SkillId, s => s.ActiveSkillLevel);
        return _skillCache[id] = new SkillSet(src.Name, levels);
    }

    private async Task RecalculateAsync(CancellationToken ct)
    {
        if (_data is null || _shipTypeId == 0) return;
        var fit     = CurrentFit();
        var skills  = await SkillsAsync();
        var profile = CurrentProfile;
        try
        {
            var (engine, snap) = await Task.Run(async () =>
            {
                var e = await DogmaEngine.CreateAsync(_data, fit, skills, profile, ct);
                return (e, Snapshot(e, profile));
            }, ct);
            if (ct.IsCancellationRequested) return;
            _lastEngine = engine;
            Stats = snap;
            for (var i = 0; i < _modules.Count; i++)
                _modules[i].Detail = snap.ModuleDetail.GetValueOrDefault(i, "");
            // Slot counts can change with the fit (subsystems add slots), so the sections are
            // laid out again each time; the module rows themselves are the same objects.
            RebuildSlots();
            await ApplyPricesAsync(fit);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = $"Calculation failed: {ex.Message}"; }
    }

    private static FitSnapshot Snapshot(DogmaEngine e, DamageProfile profile)
    {
        var s = new FitStats(e);
        var snap = new FitSnapshot
        {
            Cpu = s.CpuUsed, CpuOut = s.CpuOutput, Power = s.PowerUsed, PowerOut = s.PowerOutput,
            Calib = s.CalibrationUsed, CalibOut = s.Calibration,
            TurretsOut = s.TurretHardpoints, LaunchersOut = s.LauncherHardpoints,
            DroneBay = s.DroneBayUsed, DroneBayOut = s.DroneBay, Bandwidth = s.DroneBandwidthUsed, BandwidthOut = s.DroneBandwidth,
            Speed = s.MaxVelocity, Align = s.AlignTime, Signature = s.Signature, Warp = s.WarpSpeed, Mass = s.Mass, Agility = s.Agility,
            Range = s.TargetRange, ScanRes = s.ScanResolution, MaxTargets = s.MaxLockedTargets,
        };
        foreach (var slot in new[] { FitSlot.High, FitSlot.Mid, FitSlot.Low, FitSlot.Rig, FitSlot.Subsystem, FitSlot.Service })
            snap.Slots[slot] = s.Slots(slot);
        snap.Turrets   = e.Modules.Count(m => m.Type.EffectIds.Any(id => e.Data.Effects.TryGetValue(id, out var fx) && fx.Name == "turretFitted"));
        snap.Launchers = e.Modules.Count(m => m.Type.EffectIds.Any(id => e.Data.Effects.TryGetValue(id, out var fx) && fx.Name == "launcherFitted"));

        static string Pct(double resonance) => $"{(1 - resonance) * 100:0.0}%";
        foreach (var (name, layer) in new[] { ("Shield", s.Shield), ("Armor", s.Armor), ("Hull", s.Hull) })
            snap.Tank.Add(new TankLayerRow(name, $"{layer.Hp:N0}", Pct(layer.EmResonance), Pct(layer.ThermalResonance),
                Pct(layer.KineticResonance), Pct(layer.ExplosiveResonance), $"{layer.Ehp(profile):N0}"));
        snap.Ehp = s.Ehp(profile);
        snap.Cargo = s.CargoUsed; snap.CargoOut = s.CargoCapacity;
        var repairs = s.Repairs();
        snap.Rates = s.Tank(repairs);
        snap.ShieldRecharge = s.ShieldRechargeSeconds;
        double Taken(LayerStats l) => (profile.Em * l.EmResonance + profile.Thermal * l.ThermalResonance
                                     + profile.Kinetic * l.KineticResonance + profile.Explosive * l.ExplosiveResonance) / profile.Total;
        snap.ShieldTaken = Taken(s.Shield); snap.ArmorTaken = Taken(s.Armor); snap.HullTaken = Taken(s.Hull);
        snap.Cap = s.Capacitor();
        var weapons = s.Weapons();
        snap.WeaponDps = s.WeaponDps(weapons); snap.DroneDps = s.DroneDps(weapons); snap.Volley = s.Volley(weapons);

        var sensors = new[] { ("Radar", "scanRadarStrength"), ("Ladar", "scanLadarStrength"), ("Magnetometric", "scanMagnetometricStrength"), ("Gravimetric", "scanGravimetricStrength") }
            .Select(x => (x.Item1, Value: e.Value(e.Ship, x.Item2))).OrderByDescending(x => x.Value).First();
        snap.Sensor = sensors.Value; snap.SensorType = sensors.Item1;

        var byModule = weapons.Where(w => w.Kind != WeaponKind.Drone).ToDictionary(w => w.Item, w => w);
        for (var i = 0; i < e.Modules.Count; i++)
        {
            var m = e.Modules[i];
            var parts = new List<string>();
            if (m.Kind == DogmaItemKind.Rig) parts.Add($"Calibration {e.Value(m, "upgradeCost"):0}");
            else
            {
                if (e.Value(m, "cpu") is var cpu and > 0) parts.Add($"CPU {cpu:0.##}");
                if (e.Value(m, "power") is var pg and > 0) parts.Add($"PG {pg:0.##}");
            }
            if (byModule.TryGetValue(m, out var w)) parts.Add($"{w.Dps.Total:N1} DPS");
            if (repairs.FirstOrDefault(r => r.Item == m) is { } rep) parts.Add($"{rep.PerSecond:N1} {rep.Layer.ToString().ToLower()} HP/s");
            snap.ModuleDetail[i] = string.Join(" · ", parts);
        }
        return snap;
    }

    // Stats as the panel shows them.
    public string CpuText         => Stats is { } s ? $"{s.Cpu:0.##} / {s.CpuOut:0.##}" : "—";
    public double CpuFraction     => Stats is { CpuOut: > 0 } s ? Math.Min(1, s.Cpu / s.CpuOut) : 0;
    public bool   CpuOver         => Stats is { } s && s.Cpu > s.CpuOut + 1e-9;
    public string PowerText       => Stats is { } s ? $"{s.Power:0.##} / {s.PowerOut:0.##}" : "—";
    public double PowerFraction   => Stats is { PowerOut: > 0 } s ? Math.Min(1, s.Power / s.PowerOut) : 0;
    public bool   PowerOver       => Stats is { } s && s.Power > s.PowerOut + 1e-9;
    public string CalibText       => Stats is { } s ? $"{s.Calib:0} / {s.CalibOut:0}" : "—";
    public double CalibFraction   => Stats is { CalibOut: > 0 } s ? Math.Min(1, s.Calib / s.CalibOut) : 0;
    public bool   CalibOver       => Stats is { } s && s.Calib > s.CalibOut + 1e-9;
    public string HardpointsText  => Stats is { } s ? $"Turrets {s.Turrets} / {s.TurretsOut}    Launchers {s.Launchers} / {s.LaunchersOut}" : "";
    public bool   HardpointsOver  => Stats is { } s && (s.Turrets > s.TurretsOut || s.Launchers > s.LaunchersOut);
    public string CargoText       => Stats is { } s ? $"Cargo {s.Cargo:N1} / {s.CargoOut:N0} m³" : "";
    public bool   CargoOver       => Stats is { } s && s.Cargo > s.CargoOut + 1e-9;
    public string DroneText       => Stats is { } s ? $"Drone bay {s.DroneBay:0} / {s.DroneBayOut:0} m³    Bandwidth {s.Bandwidth:0} / {s.BandwidthOut:0} Mbit/s" : "";
    public bool   DroneOver       => Stats is { } s && (s.DroneBay > s.DroneBayOut + 1e-9 || s.Bandwidth > s.BandwidthOut + 1e-9);
    public IReadOnlyList<TankLayerRow> TankRows => Stats?.Tank ?? [];
    public string EhpText         => Stats is { } s ? $"{s.Ehp:N0} EHP" : "";
    /// <summary>Peak shield regeneration and recharge time — what a passive shield tank lives on.</summary>
    public string RegenText       => Stats is { Rates: { } r } s && r.PassiveShield > 0
        ? $"Shield regen {r.PassiveShield:N1} HP/s at peak ({r.PassiveShield / s.ShieldTaken:N0} EHP/s)    Recharge {FormatDuration(s.ShieldRecharge)}" : "";
    /// <summary>What active modules repair, per layer, raw and effective against even damage.</summary>
    public string RepairText      => Stats is { Rates: { } r } s ? string.Join(Environment.NewLine, new[]
        {
            r.ShieldBoost > 0 ? $"Shield boost {r.ShieldBoost:N1} HP/s ({r.ShieldBoost / s.ShieldTaken:N0} EHP/s)" : null,
            r.ArmorRepair > 0 ? $"Armor repair {r.ArmorRepair:N1} HP/s ({r.ArmorRepair / s.ArmorTaken:N0} EHP/s)" : null,
            r.HullRepair  > 0 ? $"Hull repair {r.HullRepair:N1} HP/s ({r.HullRepair / s.HullTaken:N0} EHP/s)" : null,
            (r.ShieldBoost + r.ArmorRepair + r.HullRepair) > 0 && s.Cap is { Stable: false } c
                ? $"Repairs that use capacitor stop when it runs out ({FormatDuration(c.LastsSeconds)})" : null,
        }.OfType<string>()) : "";
    public bool HasRepairs        => Stats is { Rates: { } r } && r.ShieldBoost + r.ArmorRepair + r.HullRepair > 0;
    public string CapText         => Stats?.Cap is { } c ? $"{c.Capacity:N0} GJ" : "";
    public string CapStateText    => Stats?.Cap is { } c
        ? c.Stable ? $"Stable at {c.StableFraction * 100:0.0}%" : $"Lasts {FormatDuration(c.LastsSeconds)}" : "";
    public bool   CapStable       => Stats?.Cap?.Stable ?? true;
    public string CapFlowText     => Stats?.Cap is { } c
        ? $"Use {c.Drain:0.0} GJ/s    Peak recharge {c.PeakRecharge:0.0} GJ/s" + (c.Injection > 0 ? $"    Boosters +{c.Injection:0.0} GJ/s" : "") : "";
    public string DpsText         => Stats is { } s ? $"{(s.WeaponDps.Total + s.DroneDps.Total):N0} DPS" : "";
    public string DpsSplitText    => Stats is { } s ? $"Weapons {s.WeaponDps.Total:N1}    Drones {s.DroneDps.Total:N1}    Volley {s.Volley.Total:N0}" : "";
    public string DamageTypesText => Stats is { } s && s.WeaponDps.Total + s.DroneDps.Total > 0
        ? DamageMix(s.WeaponDps + s.DroneDps) : "";
    public string SpeedText       => Stats is { } s ? $"{s.Speed:N0} m/s" : "";
    public string NavText         => Stats is { } s ? $"Align {s.Align:0.00} s    Signature {s.Signature:N0} m    Warp {s.Warp:0.##} AU/s" : "";
    public string MassText        => Stats is { } s ? $"Mass {s.Mass:N0} kg    Inertia {s.Agility:0.###}" : "";
    public string TargetingText   => Stats is { } s ? $"Range {s.Range / 1000:0.#} km    Scan res {s.ScanRes:N0} mm    Targets {s.MaxTargets:0}" : "";
    public string SensorText      => Stats is { } s ? $"{s.SensorType} {s.Sensor:0.#}" : "";

    private static string DamageMix(DamageBreakdown d) =>
        d.Total <= 0 ? "" : $"EM {d.Em / d.Total:P0} · Th {d.Thermal / d.Total:P0} · Kin {d.Kinetic / d.Total:P0} · Exp {d.Explosive / d.Total:P0}";

    private static string FormatDuration(double seconds) =>
        seconds >= 3600 ? $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60)} m"
        : seconds >= 60 ? $"{(int)(seconds / 60)} m {(int)(seconds % 60)} s"
        : $"{seconds:0.0} s";

    private void RaiseStatText()
    {
        foreach (var p in new[] { nameof(CpuText), nameof(CpuFraction), nameof(CpuOver), nameof(PowerText), nameof(PowerFraction), nameof(PowerOver),
                     nameof(CalibText), nameof(CalibFraction), nameof(CalibOver), nameof(HardpointsText), nameof(HardpointsOver), nameof(DroneText), nameof(DroneOver), nameof(CargoText), nameof(CargoOver),
                     nameof(TankRows), nameof(EhpText), nameof(RegenText), nameof(RepairText), nameof(HasRepairs), nameof(CapText), nameof(CapStateText), nameof(CapStable), nameof(CapFlowText),
                     nameof(DpsText), nameof(DpsSplitText), nameof(DamageTypesText), nameof(SpeedText), nameof(NavText), nameof(MassText),
                     nameof(TargetingText), nameof(SensorText) })
            this.RaisePropertyChanged(p);
    }

    // ── Import / export ─────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ImportEftCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyEftCommand   { get; }
    public ReactiveCommand<Unit, Unit> ImportEsiCommand { get; }

    /// <summary>Asks the view for EFT text to import; null when cancelled.</summary>
    public Interaction<Unit, string?> AskEft { get; } = new();
    /// <summary>Hands the view EFT text to put on the clipboard.</summary>
    public Interaction<string, Unit> CopyText { get; } = new();
    /// <summary>Asks the view to show the in-game fittings picker.</summary>
    public Interaction<FitSelectorViewModel, EsiFittingData?> PickEsiFit { get; } = new();

    private async Task ImportEftAsync()
    {
        if (_data is null) return;
        if (!await ConfirmLeaveAsync()) return;
        var text = await AskEft.Handle(Unit.Default);
        if (string.IsNullOrWhiteSpace(text)) return;
        var parsed = await EftFormat.ParseAsync(text, _data);
        await LoadFitAsync(parsed.Fit);
        Status = parsed.Unknown.Count == 0 ? $"Imported {parsed.Fit.Name}." : $"Imported, but not recognised: {string.Join(", ", parsed.Unknown)}";
    }

    private async Task CopyEftAsync()
    {
        if (_data is null || _shipTypeId == 0) { Status = "Nothing to copy yet."; return; }
        await CopyText.Handle(EftFormat.Write(CurrentFit(), _data));
        Status = "Fit copied to the clipboard as EFT text.";
    }

    private async Task ImportEsiAsync()
    {
        if (_data is null || _catalog is null) return;
        if (!await ConfirmLeaveAsync()) return;
        if (_fittings is null || _characters is null || _corporations is null) { Status = "No characters to read fittings from."; return; }
        var picker = new FitSelectorViewModel(_fittings, _dbFactory, _characters, _corporations, [], 0) { ChooseGroup = false };
        var esi = await PickEsiFit.Handle(picker);
        if (esi is null) return;
        await LoadFitAsync(await EftFormat.FromEsiAsync(esi, _data, _catalog));

        // A character's own fitting can be updated in place; a corporation's cannot be written at all.
        if (picker.SelectedNode?.Entry is { Source: FitSource.Personal } entry
            && _characters.FirstOrDefault(c => c.Name == entry.OwnerName) is { } owner)
        {
            CurrentGameSource = new GameSource(owner.Id, owner.Name, esi.FittingId, esi.Name);
            // Calculated with the owner's skills unless a pilot was already chosen deliberately.
            if (SelectedSkillSource?.CharacterId is null && SkillSources.FirstOrDefault(s => s.CharacterId == owner.Id) is { } pilot)
                SelectedSkillSource = pilot;
        }
        Status = $"Imported {esi.Name} from the game.";
    }

    /// <summary>Replaces the fit being edited with <paramref name="fit"/>.</summary>
    public async Task LoadFitAsync(FitDefinition fit)
    {
        if (_data is null) return;
        await _data.LoadTypesAsync(fit.AllTypeIds());
        NewFit(announce: false);
        SetShip(fit.ShipTypeId);
        FitName = fit.Name;
        foreach (var m in fit.Modules) await AddModuleAsync(m.TypeId, m.State, m.ChargeTypeId);
        foreach (var d in fit.Drones) AddDrone(d.TypeId, _data.Type(d.TypeId).Name, d.Count, Math.Min(d.Count, d.Active > 0 ? d.Active : 5));
        foreach (var i in fit.Implants) AddImplant(i, _data.Type(i).Name, false);
        foreach (var b in fit.Boosters) AddImplant(b, _data.Type(b).Name, true);
        foreach (var (id, qty) in fit.Cargo) AddCargo(id, _data.Type(id).Name, qty);
        RebuildSlots();
        await RecalculateAsync(CancellationToken.None);
        MarkClean();
    }

    // ── Saved fits ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> DeleteSavedCommand { get; }
    public ObservableCollection<SavedFitOption> SavedFits { get; } = [];
    private long? _loadedSavedId;

    private SavedFitOption? _selectedSaved;
    public SavedFitOption? SelectedSaved
    {
        get => _selectedSaved;
        set
        {
            var previous = _selectedSaved;
            this.RaiseAndSetIfChanged(ref _selectedSaved, value);
            if (value is not null && value != previous) _ = OpenSavedGuardedAsync(value, previous);
        }
    }

    private async Task LoadSavedListAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.SavedFits.AsNoTracking().Select(f => new { f.Id, f.Name, f.ShipTypeId }).ToListAsync();
        SavedFits.Clear();
        foreach (var r in rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            SavedFits.Add(new SavedFitOption(r.Id, r.Name, _catalog?.Find(r.ShipTypeId)?.Name ?? ""));
    }

    /// <summary>Opens a saved fit, after offering to save the one it replaces; a cancel puts the
    /// list back to what it showed.</summary>
    private async Task OpenSavedGuardedAsync(SavedFitOption option, SavedFitOption? previous)
    {
        if (await ConfirmLeaveAsync()) { await OpenSavedAsync(option); return; }
        _selectedSaved = previous;
        this.RaisePropertyChanged(nameof(SelectedSaved));
    }

    private async Task OpenSavedAsync(SavedFitOption option)
    {
        if (_data is null) return;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.SavedFits.AsNoTracking().FirstOrDefaultAsync(f => f.Id == option.Id);
        if (row is null) return;
        var parsed = await EftFormat.ParseAsync(row.Eft, _data);
        await LoadFitAsync(parsed.Fit);
        FitName = row.Name;
        _loadedSavedId = row.Id;
        _lastSavedToApp = row.Id;
        MarkClean();
        Status = $"Opened {row.Name}.";
    }

    /// <summary>Saves in EVE Console, under the fit's name: the same name on the same hull replaces it.</summary>
    private async Task<bool> SaveToAppAsync()
    {
        if (_data is null || _shipTypeId == 0) { Status = "Nothing to save yet."; return false; }
        var name = FitName.Trim().Length > 0 ? FitName.Trim() : $"{ShipName} fit";
        await using var db = await _dbFactory.CreateDbContextAsync();
        // Saving under the name of a fit already saved for this hull replaces it; a new name adds one.
        var row = await db.SavedFits.FirstOrDefaultAsync(f => f.Name == name && f.ShipTypeId == _shipTypeId);
        if (row is null) { row = new SavedFit { Name = name, ShipTypeId = _shipTypeId }; db.SavedFits.Add(row); }
        row.Eft       = EftFormat.Write(CurrentFit(), _data);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        _loadedSavedId = row.Id;
        _lastSavedToApp = row.Id;
        await LoadSavedListAsync();
        MarkClean();
        Status = $"Saved {name} in EVE Console.";
        return true;
    }

    private async Task DeleteSavedAsync()
    {
        if (SelectedSaved is not { } sel) { Status = "Choose a saved fit to delete."; return; }
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.SavedFits.Where(f => f.Id == sel.Id).ExecuteDeleteAsync();
        _selectedSaved = null; this.RaisePropertyChanged(nameof(SelectedSaved));
        await LoadSavedListAsync();
        Status = $"Deleted {sel.Name}.";
    }
}

/// <summary>Greys out a save destination that cannot be used.</summary>
public static class SaveFitDialogConverters
{
    public static readonly Avalonia.Data.Converters.IValueConverter EnabledOpacity =
        new Avalonia.Data.Converters.FuncValueConverter<bool, double>(enabled => enabled ? 1.0 : 0.45);
}
