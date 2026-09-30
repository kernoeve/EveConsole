using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Localization;
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
    /// <summary>The module's English name — what the ring is handed; it names slots itself.</summary>
    public string  Name    { get; }
    /// <summary>The module's name in the interface language, for the list. Display only.</summary>
    public string  DisplayName => SdeNames.Type(TypeId, Name);
    public bool    IsEmpty => TypeId == 0;
    public string  EmptyText => Slot switch
    {
        FitSlot.High      => FittingText.EmptyRowHigh,
        FitSlot.Mid       => FittingText.EmptyRowMid,
        FitSlot.Low       => FittingText.EmptyRowLow,
        FitSlot.Rig       => FittingText.EmptyRowRig,
        FitSlot.Subsystem => FittingText.EmptyRowSubsystem,
        FitSlot.Service   => FittingText.EmptyRowService,
        _                 => "",
    };
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
        ModuleState.Offline    => FittingText.StateOff,
        ModuleState.Online     => FittingText.StateOn,
        ModuleState.Active     => FittingText.StateAct,
        ModuleState.Overheated => FittingText.StateHeat,
        _                      => "",
    };
    public string StateTip => State switch
    {
        ModuleState.Offline    => FittingText.TipStateOffline,
        ModuleState.Online     => CanActivate ? FittingText.TipStateOnlineActivate : FittingText.TipStateOnlineOffline,
        ModuleState.Active     => FittingText.TipStateActive,
        _                      => FittingText.TipStateOverheated,
    };
    public string HeatTip => IsHeated ? FittingText.TipHeatStop : FittingText.TipHeatStart;

    /// <summary>The state as the ring's tooltip names it.</summary>
    public string StateName => State switch
    {
        ModuleState.Offline    => FittingText.StateNameOffline,
        ModuleState.Active     => FittingText.StateNameActive,
        ModuleState.Overheated => FittingText.StateNameOverheated,
        _                      => FittingText.StateNameOnline,
    };

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

/// <summary>
/// A drone stack — how many are in the bay and how many launched — or a fighter squadron: its
/// size, whether it is in a launch tube, and which of its abilities are switched on.
/// </summary>
public sealed class FittingDroneRowVm : ReactiveObject
{
    public int    TypeId { get; }
    public string Name   { get; }
    /// <summary>The drone's or fighter's name in the interface language, for the list. Display only.</summary>
    public string DisplayName => SdeNames.Type(TypeId, Name);
    private int _count, _active;
    public int Count
    {
        get => _count;
        set
        {
            this.RaiseAndSetIfChanged(ref _count, Math.Clamp(value, 1, IsFighter ? MaxSquadron : int.MaxValue));
            this.RaisePropertyChanged(nameof(CountValue));
            // A squadron in a tube flies at its full size; a drone stack cannot launch more than it holds.
            if (IsFighter ? _active > 0 : _active > _count) Active = _count;
        }
    }
    public int Active
    {
        get => _active;
        set
        {
            this.RaiseAndSetIfChanged(ref _active, Math.Clamp(value, 0, _count));
            this.RaisePropertyChanged(nameof(ActiveValue));
            this.RaisePropertyChanged(nameof(Launched));
        }
    }
    /// <summary>For a squadron: in a launch tube.</summary>
    public bool Launched { get => _active > 0; set => Active = value ? _count : 0; }
    /// <summary>For a squadron: the most fighters it holds.</summary>
    public int MaxSquadron { get; init; } = int.MaxValue;
    /// <summary>For a squadron: its launch slot class, when the type names one.</summary>
    public FighterClass? Class { get; init; }
    /// <summary>For a squadron: its class as shown ("Light", "Support", "Heavy").</summary>
    public string ClassLabel => Class is { } c ? FighterAbilities.ClassName(c) : "";
    public string SquadronTip => Class switch
    {
        FighterClass.Light or FighterClass.StandupLight     => string.Format(FittingText.TipSquadronLight, MaxSquadron),
        FighterClass.Support or FighterClass.StandupSupport => string.Format(FittingText.TipSquadronSupport, MaxSquadron),
        FighterClass.Heavy or FighterClass.StandupHeavy     => string.Format(FittingText.TipSquadronHeavy, MaxSquadron),
        _                                                    => string.Format(FittingText.TipSquadron, MaxSquadron),
    };
    public ObservableCollection<FighterAbilityToggleVm> Abilities { get; } = [];
    private string _detail = "";
    /// <summary>What the stack or squadron does once launched — its DPS, per ability for a squadron.</summary>
    public string Detail { get => _detail; set => this.RaiseAndSetIfChanged(ref _detail, value); }
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
/// <summary>One of a squadron's abilities, switched on or off. Only damage abilities change the numbers.</summary>
public sealed class FighterAbilityToggleVm(FighterAbility ability, bool on) : ReactiveObject
{
    public FighterAbility Ability { get; } = ability;
    public string Label => Ability.Label;
    public bool DealsDamage => Ability.DealsDamage;
    public string Tip => Ability.DealsDamage
        ? string.Format(FittingText.TipAbilityCounted, Ability.Label)
        : string.Format(FittingText.TipAbilityNoEffect, Ability.Label);
    private bool _isOn = on;
    public bool IsOn { get => _isOn; set => this.RaiseAndSetIfChanged(ref _isOn, value); }
}

public sealed class FittingCargoRowVm : ReactiveObject
{
    public int    TypeId { get; }
    public string Name   { get; }
    /// <summary>The item's name in the interface language, for the list. Display only.</summary>
    public string DisplayName => SdeNames.Type(TypeId, Name);
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

/// <summary>A damage profile to judge the tank against: even, one type, or the user's own mix.
/// <paramref name="Key"/> is what the choice is remembered by, the same in every language;
/// <paramref name="Name"/> is shown.</summary>
public sealed record DamageProfileOption(string Key, string Name, DamageProfile? Profile)
{
    public bool IsCustom => Profile is null;
    public override string ToString() => Name;
}

public sealed class FittingImplantRowVm(int typeId, string name, bool booster)
{
    public int    TypeId    { get; } = typeId;
    public string Name      { get; } = name;
    /// <summary>The implant's or booster's name in the interface language, for the list. Display only.</summary>
    public string DisplayName => SdeNames.Type(TypeId, Name);
    public bool   IsBooster { get; } = booster;
    public string Kind      => IsBooster ? FittingText.KindBooster : FittingText.KindImplant;
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

/// <summary>Whose skills the fit is calculated with.</summary>
public sealed record SkillSourceOption(string Name, long? CharacterId, int AllLevel)
{
    public override string ToString() => Name;
}

/// <param name="Name">The fit's name, the user's own.</param>
/// <param name="ShipName">The hull's name as shown.</param>
public sealed record SavedFitOption(long Id, string Name, string ShipName)
{
    public override string ToString() => string.Format(FittingText.SavedFitLabel, Name, ShipName);
}

public sealed record TankLayerRow(string Layer, string Hp, string Em, string Thermal, string Kinetic, string Explosive, string Ehp);

/// <summary>Every number the stats panel shows, computed together off the UI thread.</summary>
public sealed class FitSnapshot
{
    public double Cpu, CpuOut, Power, PowerOut, Calib, CalibOut;
    public int Turrets, TurretsOut, Launchers, LaunchersOut;
    public double DroneBay, DroneBayOut, Bandwidth, BandwidthOut;
    public double FighterBay, FighterBayOut; public int Tubes, TubesOut;
    /// <summary>Launch slots per squadron class the hull has, or is using: (class, in tubes, slots).</summary>
    public List<(FighterClass Class, int Used, int Out)> FighterSlots = [];
    public Dictionary<FitSlot, int> Slots = new();
    public List<TankLayerRow> Tank = [];
    public double Ehp;
    public CapacitorResult? Cap;
    public DamageBreakdown WeaponDps = DamageBreakdown.Zero, DroneDps = DamageBreakdown.Zero, FighterDps = DamageBreakdown.Zero, Volley = DamageBreakdown.Zero;
    public Dictionary<int, string> DroneDetail = new();    // by drone/squadron index
    public double Speed, Align, Signature, Warp, Mass, Agility;
    public double Range, ScanRes, MaxTargets, Sensor;
    /// <summary>The strongest sensor's attribute, which says its type (radar, ladar…).</summary>
    public string SensorAttribute = "";
    public Dictionary<int, string> ModuleDetail = new();   // by module index
    public TankRates? Rates;
    public double ShieldRecharge;
    /// <summary>Per layer, the share of incoming damage (in the chosen profile) that is not resisted: HP/s ÷ this = EHP/s.</summary>
    public double ShieldTaken = 1, ArmorTaken = 1, HullTaken = 1;
    public double Cargo, CargoOut;
}

// ── The tool ─────────────────────────────────────────────────────────────────

/// <summary>
/// The fitting tool: the game data, the item finder, the saved fits, and a tab per fit. Picking a
/// hull, pasting a fit, opening one from the game or from the saved list each opens a new tab,
/// so whatever was being worked on stays where it was.
/// </summary>
public class FittingViewModel : ReactiveObject
{
    public IDbContextFactory<AppDbContext>     DbFactory    { get; }
    public FittingsService?                    Fittings     { get; }
    public EveConsole.Api.EsiClient?           Esi          { get; }
    public ObservableCollection<Character>?    Characters   { get; }
    public ObservableCollection<Corporation>?  Corporations { get; }

    public DogmaData?      Data    { get; private set; }
    public FittingCatalog? Catalog { get; private set; }

    /// <summary>Whose skills a fit can be calculated with; each tab picks one.</summary>
    public ObservableCollection<SkillSourceOption> SkillSources { get; } = [];

    public FittingViewModel(IDbContextFactory<AppDbContext> dbFactory, FittingsService? fittings = null,
        ObservableCollection<Character>? characters = null, ObservableCollection<Corporation>? corporations = null,
        EveConsole.Api.EsiClient? esi = null)
    {
        DbFactory    = dbFactory;
        Fittings     = fittings;
        Characters   = characters;
        Corporations = corporations;
        Esi          = esi;
        LeftPane     = new FitPaneViewModel(this, false);
        RightPane    = new FitPaneViewModel(this, true);
        _activePane  = LeftPane;
        LeftPane.IsActive = true;
        try { _finderVisible = UiState.Get(FinderKey) != "0"; } catch { }

        ImportEftCommand   = Guarded(ReactiveCommand.CreateFromTask(ImportEftAsync));
        ImportEsiCommand   = Guarded(ReactiveCommand.CreateFromTask(ImportEsiAsync));
        DeleteSavedCommand = Guarded(ReactiveCommand.CreateFromTask(DeleteSavedAsync));
        AddSelectedCommand = Guarded(ReactiveCommand.CreateFromTask(() => SelectedResult is { } r ? AddAsync(r) : Task.CompletedTask));
        AddToCargoCommand  = Guarded(ReactiveCommand.Create(() =>
        {
            if (SelectedResult is not { } r) return;
            if (SelectedTab is not { HasShip: true } tab) { Status = FittingText.StatusStartFitFirst; return; }
            tab.AddToCargo(r, 1);
        }));

        this.WhenAnyValue(x => x.SearchText, x => x.KindFilter, x => x.FitsOnly)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(__ => _ = RunSearchAsync());
    }

    internal ReactiveCommand<TIn, TOut> Guarded<TIn, TOut>(ReactiveCommand<TIn, TOut> c)
    {
        c.ThrownExceptions.Subscribe(ex => Status = ex.Message);
        return c;
    }

    private string _status = "";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    // ── Side panel ──────────────────────────────────────────────────────────────

    private const string FinderKey = "fitting.finder_visible";
    private bool _finderVisible = true;
    /// <summary>The item list on the left; hidden to give the fits the whole width.</summary>
    public bool FinderVisible
    {
        get => _finderVisible;
        set
        {
            this.RaiseAndSetIfChanged(ref _finderVisible, value);
            try { UiState.Set(FinderKey, value ? "1" : "0"); } catch { }
        }
    }
    public ReactiveCommand<Unit, Unit> ToggleFinderCommand => _toggleFinder ??= ReactiveCommand.Create(() => { FinderVisible = !FinderVisible; });
    private ReactiveCommand<Unit, Unit>? _toggleFinder;

    // ── Tabs, on one side or two ────────────────────────────────────────────────

    /// <summary>The first side, which holds every tab until one is dragged to the right.</summary>
    public FitPaneViewModel LeftPane  { get; }
    /// <summary>The second side, shown only while it holds a tab.</summary>
    public FitPaneViewModel RightPane { get; }
    public IEnumerable<FitTabViewModel> AllTabs => LeftPane.Tabs.Concat(RightPane.Tabs);
    public bool IsSplit => RightPane.Tabs.Count > 0;
    public bool HasTab  => LeftPane.Tabs.Count > 0;

    private FitPaneViewModel _activePane;
    /// <summary>The side last clicked: new fits open there, and the finder adds to its fit.</summary>
    public FitPaneViewModel ActivePane
    {
        get => _activePane;
        set
        {
            if (value == _activePane) return;
            _activePane.IsActive = false;
            _activePane = value;
            value.IsActive = true;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(SelectedTab));
            _ = RunSearchAsync();   // "only what fits" follows the fit being worked on
        }
    }

    /// <summary>The fit being worked on: the one showing on the active side.</summary>
    public FitTabViewModel? SelectedTab
    {
        get => _activePane.SelectedTab;
        set
        {
            if (value is null) return;
            value.Pane.SelectedTab = value;
            if (value.Pane != _activePane) { ActivePane = value.Pane; return; }
            this.RaisePropertyChanged();
            _ = RunSearchAsync();
        }
    }

    public ReactiveCommand<FitTabViewModel, Unit> SelectTabCommand => _selectTab ??= ReactiveCommand.Create<FitTabViewModel>(t => SelectedTab = t);
    private ReactiveCommand<FitTabViewModel, Unit>? _selectTab;

    /// <summary>A new tab on the active side, calculated with the pilot of the fit it was opened from.</summary>
    private FitTabViewModel NewTab()
    {
        var tab = new FitTabViewModel(this, SelectedTab?.SelectedSkillSource ?? SkillSources.FirstOrDefault()) { Pane = _activePane };
        _activePane.Tabs.Add(tab);
        SelectedTab = tab;
        PanesChanged();
        return tab;
    }

    public void CloseTab(FitTabViewModel tab)
    {
        var pane = tab.Pane;
        if (!TakeOut(tab)) return;
        if (pane == _activePane) this.RaisePropertyChanged(nameof(SelectedTab));
        PanesChanged();
    }

    /// <summary>Puts <paramref name="tab"/> on <paramref name="to"/> before position
    /// <paramref name="index"/> (at the end when null): dragging a tab along its row, or across
    /// to the other side. Dragging one to the right while there is only one side splits the view.</summary>
    public void MoveTab(FitTabViewModel tab, FitPaneViewModel to, int? index = null)
    {
        var from = tab.Pane;
        var i    = from.Tabs.IndexOf(tab);
        if (i < 0) return;
        var at = Math.Clamp(index ?? to.Tabs.Count, 0, to.Tabs.Count);
        if (from == to)
        {
            if (at > i) at--;
            if (at != i) from.Tabs.Move(i, at);
        }
        else
        {
            TakeOut(tab);
            tab.Pane = to;
            to.Tabs.Insert(at, tab);
        }
        SelectedTab = tab;
        PanesChanged();
    }

    /// <summary>Removes a tab from its side, showing its neighbour in its place.</summary>
    private static bool TakeOut(FitTabViewModel tab)
    {
        var pane = tab.Pane;
        var i    = pane.Tabs.IndexOf(tab);
        if (i < 0) return false;
        pane.Tabs.RemoveAt(i);
        if (pane.SelectedTab == tab) pane.SelectedTab = pane.Tabs.Count == 0 ? null : pane.Tabs[Math.Min(i, pane.Tabs.Count - 1)];
        return true;
    }

    /// <summary>Two sides only while both hold a fit: when the last tab leaves either one, the
    /// view goes back to a single side.</summary>
    private void PanesChanged()
    {
        if (LeftPane.Tabs.Count == 0 && RightPane.Tabs.Count > 0)
        {
            var showing = RightPane.SelectedTab;
            var moving  = RightPane.Tabs.ToList();
            RightPane.Tabs.Clear();
            RightPane.SelectedTab = null;
            foreach (var t in moving) { t.Pane = LeftPane; LeftPane.Tabs.Add(t); }
            LeftPane.SelectedTab = showing;
        }
        if (RightPane.Tabs.Count == 0 && _activePane == RightPane) ActivePane = LeftPane;
        this.RaisePropertyChanged(nameof(IsSplit));
        this.RaisePropertyChanged(nameof(HasTab));
        this.RaisePropertyChanged(nameof(SelectedTab));
        this.RaisePropertyChanged(nameof(ShowSplitDropZone));
        _ = RunSearchAsync();
    }

    private bool _isDraggingTab;
    /// <summary>A tab is being dragged — the view shows where it can be dropped.</summary>
    public bool IsDraggingTab
    {
        get => _isDraggingTab;
        set { this.RaiseAndSetIfChanged(ref _isDraggingTab, value); this.RaisePropertyChanged(nameof(ShowSplitDropZone)); }
    }
    /// <summary>While dragging, with one side holding more than one tab: the right half takes a tab
    /// to show two fits side by side.</summary>
    public bool ShowSplitDropZone => _isDraggingTab && !IsSplit && LeftPane.Tabs.Count > 1;

    private int _searchedHull;

    /// <summary>A tab finished calculating. When it is the one showing and its hull has changed
    /// since the finder last looked, "only what fits" has something new to answer.</summary>
    internal void TabCalculated(FitTabViewModel tab)
    {
        if (tab == SelectedTab && FitsOnly && tab.ShipTypeId != _searchedHull) _ = RunSearchAsync();
    }

    // ── Loading ─────────────────────────────────────────────────────────────────

    private bool _loading, _loaded;
    public bool IsReady { get => _loaded; private set => this.RaiseAndSetIfChanged(ref _loaded, value); }

    /// <summary>Called when the tool is first shown: reads the SDE's dogma data and the item list.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded || _loading) return;
        _loading = true;
        Status = FittingText.StatusLoadingGameData;
        try
        {
            Data    = await Task.Run(() => DogmaData.LoadAsync(DbFactory));
            Catalog = await Task.Run(() => FittingCatalog.LoadAsync(Data, DbFactory));
            if (Data.Effects.Count == 0 || Catalog.Entries.Count == 0)
            {
                Status = FittingText.StatusNoGameData;
                return;
            }
            // An SDE imported by a build before this tool has the effects but not their rules,
            // and every number would be a hull's bare base value. The update that adds them runs
            // by itself after upgrading; say so rather than show numbers that look plausible.
            if (Data.Effects.Values.All(e => e.Modifiers.Count == 0))
            {
                Status = FittingText.StatusGameDataUpdating;
                Data = null;
                return;
            }
            SkillSources.Clear();
            SkillSources.Add(new SkillSourceOption(FittingText.PilotAllV, null, 5));
            SkillSources.Add(new SkillSourceOption(FittingText.PilotAll0, null, 0));
            await using (var db = await DbFactory.CreateDbContextAsync())
                foreach (var c in (await db.Characters.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync()).OrderBy(c => c.Name))
                    SkillSources.Add(new SkillSourceOption(c.Name, c.Id, 0));
            await LoadSavedListAsync();
            Hulls.Clear();
            foreach (var h in Catalog.Entries.Where(e => e.Kind == CatalogKind.Hull).OrderBy(e => e.DisplayName, StringComparer.CurrentCulture))
                Hulls.Add(h);

            IsReady = true;
            Status  = FittingText.StatusStart;
            await RunSearchAsync();
        }
        catch (Exception ex) { Status = string.Format(FittingText.StatusLoadFailed, ex.Message); }
        finally { _loading = false; }
    }

    // ── Finder ──────────────────────────────────────────────────────────────────

    /// <summary>Every hull, for the new-fit picker, in the order of the names it shows.</summary>
    public ObservableCollection<CatalogEntry> Hulls { get; } = [];

    /// <summary>The new-fit picker matches every word against the hull's name or its class, as
    /// shown or in English.</summary>
    public Avalonia.Controls.AutoCompleteFilterPredicate<object?> HullFilter { get; } = (text, item) =>
        item is CatalogEntry e && (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).All(e.Matches);

    /// <summary>A new fit on <paramref name="hull"/>, in a tab of its own.</summary>
    public async Task NewFitAsync(CatalogEntry hull)
    {
        if (Data is null || hull.Kind != CatalogKind.Hull) return;
        await Data.LoadTypesAsync([hull.TypeId]);
        await NewTab().StartAsync(hull);
        Status = string.Format(FittingText.StatusNewFit, hull.DisplayName);
    }

    /// <summary>The finder's categories. <see cref="KindFilter"/> holds the key, which
    /// <see cref="RunSearchAsync"/> and <see cref="PointFinderAt"/> go by; only the label is shown.</summary>
    public static IReadOnlyList<Choice<string>> KindFilters { get; } =
    [
        new("All",         FittingText.FilterAll),
        new("Modules",     FittingText.FilterModules),
        new("Rigs",        FittingText.FilterRigs),
        new("Subsystems",  FittingText.FilterSubsystems),
        new("Charges",     FittingText.FilterCharges),
        new("Drones",      FittingText.FilterDrones),
        new("Implants",    FittingText.FilterImplants),
        new("Boosters",    FittingText.FilterBoosters),
        new("Other items", FittingText.FilterOtherItems),
    ];

    private string _searchText = "";
    public string SearchText { get => _searchText; set => this.RaiseAndSetIfChanged(ref _searchText, value); }

    private string _kindFilter = "All";
    /// <summary>The finder's category, by key: a value of <see cref="KindFilters"/>.</summary>
    public string KindFilter
    {
        get => _kindFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _kindFilter, value);
            this.RaisePropertyChanged(nameof(SelectedKindFilter));
        }
    }

    /// <summary>The category pick list's choice.</summary>
    public Choice<string> SelectedKindFilter
    {
        get => KindFilters.FirstOrDefault(o => o.Value == _kindFilter) ?? KindFilters[0];
        set
        {
            // A detaching ComboBox sets null; that is not a choice.
            if (value is null) { this.RaisePropertyChanged(); return; }
            KindFilter = value.Value;
        }
    }

    private bool _fitsOnly = true;
    /// <summary>Only modules, rigs and subsystems the current hull could take.</summary>
    public bool FitsOnly { get => _fitsOnly; set => this.RaiseAndSetIfChanged(ref _fitsOnly, value); }

    public ObservableCollection<CatalogEntry> SearchResults { get; } = [];

    private CatalogEntry? _selectedResult;
    public CatalogEntry? SelectedResult { get => _selectedResult; set => this.RaiseAndSetIfChanged(ref _selectedResult, value); }

    internal async Task RunSearchAsync()
    {
        if (Catalog is null || Data is null) return;
        HashSet<CatalogKind>? kinds = KindFilter switch
        {
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

        var found = Catalog.Search(SearchText, kinds)
            .Where(f => f.Kind != CatalogKind.Hull)
            .Where(f => FinderSlot is not { } only || f.Slot == only)
            .Take(400).ToList();
        if (FitsOnly && SelectedTab?.LastEngine is { } engine)
        {
            // Slot and rig-size rules only; a full slot does not hide what could go in it.
            await Data.LoadTypesAsync(found.Where(f => f.Kind is CatalogKind.Rig or CatalogKind.Subsystem or CatalogKind.Module).Select(f => f.TypeId));
            var shipSlots = new FitStats(engine);
            found = found.Where(f => f.Kind switch
            {
                CatalogKind.Module or CatalogKind.Rig => shipSlots.Slots(f.Slot) > 0 && RigSizeFits(engine, f.TypeId) && HullAllows(engine, f.TypeId),
                CatalogKind.Subsystem => FitsHull(engine, f.TypeId),
                _ => true,
            }).ToList();
        }
        _searchedHull = SelectedTab?.ShipTypeId ?? 0;
        SearchResults.Clear();
        foreach (var f in found.Take(200)) SearchResults.Add(f);
    }

    private bool RigSizeFits(DogmaEngine e, int typeId)
    {
        if (Data!.Attribute("rigSize")?.Id is not { } rs) return true;
        var t = Data.Type(typeId);
        return t.Attr(rs) is not { } size || e.Value(e.Ship, rs) is var hull && (hull <= 0 || hull == size);
    }

    private bool HullAllows(DogmaEngine e, int typeId)
    {
        var t = Data!.Type(typeId);
        var groups = Data.AttributesByName.Values.Where(a => a.Name.StartsWith("canFitShipGroup")).Select(a => t.Attr(a.Id)).OfType<double>().ToList();
        var hulls  = Data.AttributesByName.Values.Where(a => a.Name.StartsWith("canFitShipType")).Select(a => t.Attr(a.Id)).OfType<double>().ToList();
        return (groups.Count == 0 && hulls.Count == 0) || groups.Contains(e.Ship.Type.GroupId) || hulls.Contains(e.Ship.Type.Id);
    }

    private bool FitsHull(DogmaEngine e, int typeId) =>
        Data!.Attribute("fitsToShipType")?.Id is not { } f || Data.Type(typeId).Attr(f) is not { } hull || (int)hull == e.Ship.Type.Id;

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
    public string FinderSlotText => _finderSlot switch
    {
        FitSlot.High      => FittingText.FinderOnlyHigh,
        FitSlot.Mid       => FittingText.FinderOnlyMid,
        FitSlot.Low       => FittingText.FinderOnlyLow,
        FitSlot.Rig       => FittingText.FinderOnlyRig,
        FitSlot.Subsystem => FittingText.FinderOnlySubsystem,
        FitSlot.Service   => FittingText.FinderOnlyService,
        _                 => "",
    };
    public ReactiveCommand<Unit, Unit> ClearFinderSlotCommand => _clearFinderSlot ??= ReactiveCommand.Create(() => { FinderSlot = null; });
    private ReactiveCommand<Unit, Unit>? _clearFinderSlot;

    /// <summary>An empty slot was clicked on the ring: list what could go in it.</summary>
    internal void PointFinderAt(FitSlot slot)
    {
        _finderSlot = slot;
        this.RaisePropertyChanged(nameof(FinderSlot));
        this.RaisePropertyChanged(nameof(FinderSlotText));
        this.RaisePropertyChanged(nameof(HasFinderSlot));
        KindFilter = slot switch { FitSlot.Rig => "Rigs", FitSlot.Subsystem => "Subsystems", _ => "Modules" };
        FitsOnly   = true;
        FinderVisible = true;
        _ = RunSearchAsync();
    }

    // ── Adding from the finder ──────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> AddSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToCargoCommand  { get; }

    /// <summary>Puts <paramref name="entry"/> on the fit being worked on (a hull starts a new one).</summary>
    public async Task AddAsync(CatalogEntry entry)
    {
        if (Data is null || Catalog is null) return;
        if (entry.Kind == CatalogKind.Hull) { await NewFitAsync(entry); return; }
        if (SelectedTab is not { HasShip: true } tab) { Status = FittingText.StatusPickHullFirst; return; }
        await tab.AddAsync(entry);
    }

    // ── Import, export, saving ──────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ImportEftCommand   { get; }
    public ReactiveCommand<Unit, Unit> ImportEsiCommand   { get; }

    /// <summary>Asks the view for EFT text to import; null when cancelled.</summary>
    public Interaction<Unit, string?> AskEft { get; } = new();
    /// <summary>Hands the view EFT text to put on the clipboard.</summary>
    public Interaction<string, Unit> CopyText { get; } = new();
    /// <summary>Asks the view to show the in-game fittings picker.</summary>
    public Interaction<FitSelectorViewModel, EsiFittingData?> PickEsiFit { get; } = new();
    /// <summary>Asks the view where to save; null when cancelled.</summary>
    public Interaction<SaveRequest, SaveChoice?> AskSave { get; } = new();

    private async Task ImportEftAsync()
    {
        if (Data is null) return;
        var text = await AskEft.Handle(Unit.Default);
        if (string.IsNullOrWhiteSpace(text)) return;
        var parsed = await EftFormat.ParseAsync(text, Data);
        await NewTab().LoadFitAsync(parsed.Fit);
        Status = parsed.Unknown.Count == 0
            ? string.Format(FittingText.StatusImported, parsed.Fit.Name)
            : string.Format(FittingText.StatusImportedUnrecognised, string.Join(CommonText.ListSeparator, parsed.Unknown));
    }

    private async Task ImportEsiAsync()
    {
        if (Data is null || Catalog is null) return;
        if (Fittings is null || Characters is null || Corporations is null) { Status = FittingText.StatusNoCharacters; return; }
        var picker = new FitSelectorViewModel(Fittings, DbFactory, Characters, Corporations, [], 0) { ChooseGroup = false };
        var esi = await PickEsiFit.Handle(picker);
        if (esi is null) return;
        var tab = NewTab();
        await tab.LoadFitAsync(await EftFormat.FromEsiAsync(esi, Data, Catalog));

        // A character's own fitting can be updated in place; a corporation's cannot be written at all.
        if (picker.SelectedNode?.Entry is { Source: FitSource.Personal } entry
            && Characters.FirstOrDefault(c => c.Name == entry.OwnerName) is { } owner)
        {
            tab.CurrentGameSource = new FitTabViewModel.GameSource(owner.Id, owner.Name, esi.FittingId, esi.Name);
            // Calculated with the owner's skills unless a character pilot was already chosen.
            if (tab.SelectedSkillSource?.CharacterId is null && SkillSources.FirstOrDefault(s => s.CharacterId == owner.Id) is { } pilot)
                tab.SelectedSkillSource = pilot;
        }
        Status = string.Format(FittingText.StatusImportedFromGame, esi.Name);
    }

    // ── Saved fits ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> DeleteSavedCommand { get; }
    public ObservableCollection<SavedFitOption> SavedFits { get; } = [];

    private SavedFitOption? _selectedSaved;
    /// <summary>Choosing a saved fit opens it in a new tab.</summary>
    public SavedFitOption? SelectedSaved
    {
        get => _selectedSaved;
        set
        {
            var previous = _selectedSaved;
            this.RaiseAndSetIfChanged(ref _selectedSaved, value);
            if (value is not null && value != previous) _ = OpenSavedAsync(value);
        }
    }

    internal async Task LoadSavedListAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var rows = await db.SavedFits.AsNoTracking().Select(f => new { f.Id, f.Name, f.ShipTypeId }).ToListAsync();
        SavedFits.Clear();
        foreach (var r in rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            SavedFits.Add(new SavedFitOption(r.Id, r.Name, Catalog?.Find(r.ShipTypeId)?.DisplayName ?? ""));
    }

    private async Task OpenSavedAsync(SavedFitOption option)
    {
        if (Data is null) return;
        await using var db = await DbFactory.CreateDbContextAsync();
        var row = await db.SavedFits.AsNoTracking().FirstOrDefaultAsync(f => f.Id == option.Id);
        if (row is null) return;
        var parsed = await EftFormat.ParseAsync(row.Eft, Data);
        var tab = NewTab();
        await tab.LoadFitAsync(parsed.Fit);
        tab.FitName = row.Name;
        tab.MarkSavedInApp(row.Id);
        Status = string.Format(FittingText.StatusOpened, row.Name);
    }

    private async Task DeleteSavedAsync()
    {
        if (SelectedSaved is not { } sel) { Status = FittingText.StatusChooseSavedToDelete; return; }
        await using var db = await DbFactory.CreateDbContextAsync();
        await db.SavedFits.Where(f => f.Id == sel.Id).ExecuteDeleteAsync();
        _selectedSaved = null; this.RaisePropertyChanged(nameof(SelectedSaved));
        await LoadSavedListAsync();
        Status = string.Format(FittingText.StatusDeleted, sel.Name);
    }
}

/// <summary>One side of the fitting tool: a row of tabs and the fit showing. The tool has two,
/// and shows the second only while it holds a tab.</summary>
public sealed class FitPaneViewModel(FittingViewModel tool, bool isRight) : ReactiveObject
{
    public FittingViewModel Tool { get; } = tool;
    public bool IsRight { get; } = isRight;
    public ObservableCollection<FitTabViewModel> Tabs { get; } = [];

    private FitTabViewModel? _selectedTab;
    public FitTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab is not null) _selectedTab.IsSelected = false;
            this.RaiseAndSetIfChanged(ref _selectedTab, value);
            if (value is not null) value.IsSelected = true;
            this.RaisePropertyChanged(nameof(HasTab));
        }
    }
    public bool HasTab => _selectedTab is not null;

    private bool _isActive;
    /// <summary>The side last clicked; marked when there are two.</summary>
    public bool IsActive { get => _isActive; set => this.RaiseAndSetIfChanged(ref _isActive, value); }
}

/// <summary>
/// One fit, in its own tab of the fitting tool: its hull, modules, drones, cargo and implants, and
/// everything calculated from them. The tool opens a tab for every hull picked and every fit
/// loaded, so a fit being worked on is never replaced by the next one.
/// </summary>
public class FitTabViewModel : ReactiveObject
{
    public FittingViewModel Tool { get; }
    private IDbContextFactory<AppDbContext> _dbFactory => Tool.DbFactory;
    private EveConsole.Api.EsiClient? _esi => Tool.Esi;
    private DogmaData? _data => Tool.Data;
    private FittingCatalog? _catalog => Tool.Catalog;

    private DogmaEngine? _lastEngine;
    /// <summary>The last calculation — what the finder's "only what fits" reads.</summary>
    public DogmaEngine? LastEngine => _lastEngine;
    private readonly Dictionary<long, SkillSet> _skillCache = new();

    // The fit itself: the hull, its modules in the order they were added, drones, implants, cargo.
    private int _shipTypeId;
    public int ShipTypeId => _shipTypeId;
    private readonly List<FittingModuleRowVm> _modules = [];
    private long? _loadedSavedId;

    public ObservableCollection<FittingSlotGroupVm> SlotGroups { get; } = [];
    public ObservableCollection<FittingDroneRowVm>  Drones     { get; } = [];
    public ObservableCollection<FittingImplantRowVm> Implants  { get; } = [];
    public ObservableCollection<FittingCargoRowVm>  Cargo      { get; } = [];

    public FitTabViewModel(FittingViewModel tool, SkillSourceOption? pilot)
    {
        Tool = tool;
        LoadDamageProfile();
        _selectedSkillSource = pilot ?? SkillSources.FirstOrDefault();
        this.WhenAnyValue(x => x.SelectedSkillSource).Skip(1)
            .Subscribe(_ => ScheduleRecalc());
        CloseCommand   = ReactiveCommand.Create(() => Tool.CloseTab(this));
        SelectCommand  = ReactiveCommand.Create(() => { Tool.SelectedTab = this; });
        CopyEftCommand = Tool.Guarded(ReactiveCommand.CreateFromTask(CopyEftAsync));
        SaveCommand    = Tool.Guarded(ReactiveCommand.CreateFromTask(async () => { await SaveInteractiveAsync(); }));
    }

    /// <summary>The side of the tool this tab is on.</summary>
    public FitPaneViewModel Pane { get; internal set; } = null!;
    public ReactiveCommand<Unit, Unit> CopyEftCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand    { get; }

    /// <summary>The tab's label: the fit's name, else the hull's.</summary>
    public string TabTitle => FitName.Trim().Length > 0 ? FitName.Trim() : ShipName.Length > 0 ? ShipName : FittingText.TabNewFit;
    /// <summary>Changed since it was loaded or saved — the dot on the tab.</summary>
    public bool TabDirty => IsDirty;
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectCommand { get; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

    /// <summary>A new fit on <paramref name="hull"/>. The name it starts with is the user's to
    /// change, so it is worded in the interface language like the rest of the screen.</summary>
    public async Task StartAsync(CatalogEntry hull)
    {
        SetShip(hull.TypeId);
        FitName = string.Format(FittingText.NewFitName, hull.DisplayName);
        RebuildSlots();
        await RecalculateAsync(CancellationToken.None);
        MarkClean();
    }

    /// <summary>Recorded as saved in EVE Console under <paramref name="id"/> — a fit opened from the saved list.</summary>
    internal void MarkSavedInApp(long id)
    {
        _loadedSavedId  = id;
        _lastSavedToApp = id;
        MarkClean();
    }

    // ── Header: hull, name, pilot ───────────────────────────────────────────────

    private string _shipName = "";
    /// <summary>The hull's name in the interface language. Display only: the fit holds the type id.</summary>
    public string ShipName { get => _shipName; private set => this.RaiseAndSetIfChanged(ref _shipName, value); }
    public bool HasShip => _shipTypeId != 0;

    private Bitmap? _shipIcon;
    public Bitmap? ShipIcon { get => _shipIcon; private set => this.RaiseAndSetIfChanged(ref _shipIcon, value); }

    private string _fitName = "";
    public string FitName { get => _fitName; set { this.RaiseAndSetIfChanged(ref _fitName, value); this.RaisePropertyChanged(nameof(TabTitle)); } }

    public ObservableCollection<SkillSourceOption> SkillSources => Tool.SkillSources;
    private SkillSourceOption? _selectedSkillSource;
    public SkillSourceOption? SelectedSkillSource { get => _selectedSkillSource; set => this.RaiseAndSetIfChanged(ref _selectedSkillSource, value); }

    /// <summary>Messages go to the tool's status line, whichever tab they come from.</summary>
    public string Status { get => Tool.Status; set => Tool.Status = value; }

    // ── Editing ─────────────────────────────────────────────────────────────────

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
            case CatalogKind.Module or CatalogKind.Rig or CatalogKind.Subsystem:
                if (_shipTypeId == 0) { Status = FittingText.StatusPickHullFirst; return; }
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
                Status = string.Format(FittingText.StatusAdded, entry.DisplayName);
                break;

            case CatalogKind.Charge:
                await LoadChargeAsync(entry);
                break;

            case CatalogKind.Drone:
                if (Drones.FirstOrDefault(d => d.TypeId == entry.TypeId) is { } existing) existing.Count++;
                else AddDrone(entry.TypeId, entry.Name, 1, 1);
                Status = string.Format(FittingText.StatusAdded, entry.DisplayName);
                break;

            // A full squadron each time, into a tube if one is free.
            case CatalogKind.Fighter:
                var size = FighterAbilities.MaxSquadron(_data, _data.Type(entry.TypeId));
                _droneEdited = AddDrone(entry.TypeId, entry.Name, size, size);
                Status = string.Format(FittingText.StatusAddedSquadron, size, entry.DisplayName);
                break;

            case CatalogKind.Implant or CatalogKind.Booster:
                if (Implants.Any(i => i.TypeId == entry.TypeId)) { Status = string.Format(FittingText.StatusAlreadyPluggedIn, entry.DisplayName); return; }
                AddImplant(entry.TypeId, entry.Name, entry.Kind == CatalogKind.Booster);
                Status = string.Format(FittingText.StatusAdded, entry.DisplayName);
                break;

            case CatalogKind.Item:
                if (_shipTypeId == 0) { Status = FittingText.StatusPickHullFirst; return; }
                AddToCargo(entry, 1);
                break;
        }
        RebuildSlots();
        ScheduleRecalc();
    }

    private void SetShip(int typeId)
    {
        _shipTypeId = typeId;
        ShipName = SdeNames.Type(typeId, _catalog?.Find(typeId)?.Name ?? _data?.Type(typeId).Name ?? "");
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
        if (targets.Count == 0) { Status = string.Format(FittingText.StatusNothingCanLoad, charge.DisplayName); return; }
        foreach (var t in targets) t.Charge = t.Charges.First(c => c.TypeId == charge.TypeId);
        Status = targets.Count == 1
            ? string.Format(FittingText.StatusLoadedCharge, charge.DisplayName)
            : Plurals.Format(FittingText.ResourceManager, nameof(FittingText.StatusLoadedChargeInModulesOther), targets.Count, charge.DisplayName);
        await Task.CompletedTask;
    }

    /// <summary>Puts <paramref name="quantity"/> of anything in the hold, on an existing stack if there is one.</summary>
    public void AddToCargo(CatalogEntry entry, int quantity)
    {
        if (Cargo.FirstOrDefault(c => c.TypeId == entry.TypeId) is { } stack) stack.Quantity += quantity;
        else AddCargo(entry.TypeId, entry.Name, quantity);
        Status = string.Format(FittingText.StatusInCargo, entry.DisplayName);
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

    private FittingDroneRowVm AddDrone(int typeId, string name, int count, int active, IReadOnlyList<int>? abilities = null)
    {
        var fighter = _data is not null && _data.TryType(typeId, out var t) && t.CategoryId == DogmaData.CategoryFighter;
        var type = fighter ? _data!.Type(typeId) : null;
        var row = new FittingDroneRowVm(typeId, name, count, active)
        {
            IsFighter   = fighter,
            MaxSquadron = type is null ? int.MaxValue : FighterAbilities.MaxSquadron(_data!, type),
            Class       = type is null ? null : FighterAbilities.ClassOf(_data!, type),
        };
        if (type is not null)
            foreach (var a in FighterAbilities.Of(_data!, type))
            {
                var toggle = new FighterAbilityToggleVm(a, abilities?.Contains(a.EffectId) ?? a.OnByDefault);
                toggle.WhenAnyValue(x => x.IsOn).Skip(1).Subscribe(_ => ScheduleRecalc());
                row.Abilities.Add(toggle);
            }
        row.RemoveCommand = ReactiveCommand.Create(() => { Drones.Remove(row); ScheduleRecalc(); });
        row.WhenAnyValue(r => r.Count, r => r.Active).Skip(1).Subscribe(_ =>
        {
            if (_trimmingDrones) return;
            _droneEdited = row;
            ScheduleRecalc();
        });
        Drones.Add(row);
        _ = Task.Run(async () => { var b = await TypeIcons.GetAsync(typeId); Dispatcher.UIThread.Post(() => row.Icon = b); });
        return row;
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
        if (announce) Status = FittingText.StatusNewFitPickHull;
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
                var detail = r.IsEmpty ? FittingText.TipChooseModule
                    : string.Join("  ·  ", new[] { r.CanToggle ? r.StateName : null, r.Charge?.DisplayName, r.Detail }
                        .Where(x => !string.IsNullOrEmpty(x)));
                // The English name: the ring names the module in the interface language itself.
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
            Tool.PointFinderAt(empty.Slot);
            Status     = empty.Slot switch
            {
                FitSlot.High      => FittingText.StatusChooseHigh,
                FitSlot.Mid       => FittingText.StatusChooseMid,
                FitSlot.Low       => FittingText.StatusChooseLow,
                FitSlot.Rig       => FittingText.StatusChooseRig,
                FitSlot.Subsystem => FittingText.StatusChooseSubsystem,
                _                 => FittingText.StatusChooseService,
            };
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
            SlotGroups.Add(new FittingSlotGroupVm(SlotGroupTitle(slot, fitted.Count, total), rows, fitted.Count > total));
        }
        RebuildRing();
    }

    /// <summary>A slot section's heading: "High slots  2 / 8". One sentence per slot, as another
    /// language cannot always make "high slots" out of "high" and "slots".</summary>
    private static string SlotGroupTitle(FitSlot slot, int fitted, int total) => slot switch
    {
        FitSlot.High      => string.Format(FittingText.SlotGroupHigh, fitted, total),
        FitSlot.Mid       => string.Format(FittingText.SlotGroupMid, fitted, total),
        FitSlot.Low       => string.Format(FittingText.SlotGroupLow, fitted, total),
        FitSlot.Rig       => string.Format(FittingText.SlotGroupRig, fitted, total),
        FitSlot.Subsystem => string.Format(FittingText.SlotGroupSubsystem, fitted, total),
        _                 => string.Format(FittingText.SlotGroupService, fitted, total),
    };

    // ── Writing to the game ─────────────────────────────────────────────────────

    /// <summary>The in-game fitting this fit was opened from, when it was one of a character's own.</summary>
    public sealed record GameSource(long CharacterId, string CharacterName, int FittingId, string Name);

    private GameSource? _gameSource;
    internal GameSource? CurrentGameSource
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
    public string GameSourceText => _gameSource is { } g ? string.Format(FittingText.GameSource, g.CharacterName, g.Name) : "";

    // ── Saving: where, and whether there is anything unsaved ────────────────────

    private ReactiveCommand<Unit, Unit> Guarded(ReactiveCommand<Unit, Unit> c)
    {
        c.ThrownExceptions.Subscribe(ex => Status = ex.Message);
        return c;
    }

    /// <summary>The fit as last loaded or saved, in EFT — what "changed" is measured against.</summary>
    private string _baseline = "";

    private void MarkClean()
    {
        _baseline = _data is null || _shipTypeId == 0 ? "" : EftFormat.Write(CurrentFit(), _data);
        this.RaisePropertyChanged(nameof(TabDirty));
    }

    /// <summary>
    /// Whether the fit has changed since it was loaded or saved. A bare hull with nothing on it is
    /// not worth marking, and module states (on, active, overheated) are not part of any fit
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
    /// Where a fit can be saved: in EVE Console; as a new fitting on any character that has
    /// granted the fittings write scope; and, for a fit opened from a character's fittings, over
    /// that fitting. Corporation fittings are not offered — ESI has no way to write them.
    /// </summary>
    private async Task<List<SaveTarget>> SaveTargetsAsync()
    {
        var targets = new List<SaveTarget> { new(SaveKind.App, FittingText.SaveTargetApp) };
        if (_gameSource is { } src)
            targets.Add(new(SaveKind.GameUpdate, string.Format(FittingText.SaveTargetReplace, src.Name, src.CharacterName), src.CharacterId));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var chars = await db.Characters.AsNoTracking().Select(c => new { c.Id, c.Name, c.GrantedScopes, c.RefreshToken }).ToListAsync();
        foreach (var c in chars.OrderBy(c => c.Name))
        {
            var why = c.RefreshToken.Length == 0 ? FittingText.SaveWhyTokenExpired
                    : !c.GrantedScopes.Split(' ').Contains(GameFittings.WriteScope) ? FittingText.SaveWhyNoScope
                    : null;
            targets.Add(new(SaveKind.GameNew, string.Format(FittingText.SaveTargetNew, c.Name), c.Id, why is null, why));
        }
        return targets;
    }

    /// <summary>Asks where to save, and saves there. True when it was saved.</summary>
    public async Task<bool> SaveInteractiveAsync()
    {
        if (_data is null || _shipTypeId == 0) { Status = FittingText.StatusNothingToSave; return false; }
        var targets = await SaveTargetsAsync();
        var suggested = _gameSource is not null ? targets.First(t => t.Kind == SaveKind.GameUpdate)
            : SelectedSkillSource?.CharacterId is { } pilot && _lastSavedToApp is null
                ? targets.FirstOrDefault(t => t.Kind == SaveKind.GameNew && t.CharacterId == pilot && t.Enabled) ?? targets[0]
                : targets[0];
        var name = FitName.Trim().Length > 0 ? FitName.Trim() : string.Format(FittingText.DefaultFitName, ShipName);
        var choice = await Tool.AskSave.Handle(new SaveRequest(name, targets, suggested));
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

    // The description a fitting saved from here carries in the game, where the player reads it:
    // in the interface language, like the fit's name.
    private static string GameDescription => FittingText.GameFitDescription;

    /// <summary>Saves as a new fitting on <paramref name="characterId"/>.</summary>
    private async Task<bool> SaveToGameAsync(long characterId)
    {
        if (_data is null || _esi is null) { Status = FittingText.StatusGameUnreachable; return false; }
        if (await WritableCharacterAsync(characterId) is not { } ch) return false;

        var fit = CurrentFit();
        GameFittings.ToItems(fit, _data, out var skipped);
        Status = string.Format(FittingText.StatusSavingToGame, ch.Name);
        var (fittingId, error) = await GameFittings.CreateAsync(_esi, ch.Id, fit, _data, GameDescription);
        if (error is not null) { Status = error; return false; }
        CurrentGameSource = new GameSource(ch.Id, ch.Name, fittingId!.Value, fit.Name);
        MarkClean();
        Status = Sentences.Join(string.Format(FittingText.StatusSavedToGame, ch.Name), Skipped(skipped));
        return true;
    }

    /// <summary>
    /// Replaces the in-game fitting this fit came from. ESI cannot edit a fitting, so the new one
    /// is created first and the old one deleted only once that has worked.
    /// </summary>
    private async Task<bool> UpdateInGameAsync()
    {
        if (_data is null || _esi is null || CurrentGameSource is not { } src) { Status = FittingText.StatusNotFromGame; return false; }
        if (await WritableCharacterAsync(src.CharacterId) is not { } ch) return false;

        var fit = CurrentFit();
        GameFittings.ToItems(fit, _data, out var skipped);
        Status = string.Format(FittingText.StatusUpdatingInGame, ch.Name);
        var (fittingId, error) = await GameFittings.CreateAsync(_esi, ch.Id, fit, _data, GameDescription);
        if (error is not null) { Status = error; return false; }
        var deleteRefused = await GameFittings.DeleteAsync(_esi, ch.Id, src.FittingId);
        CurrentGameSource = new GameSource(ch.Id, ch.Name, fittingId!.Value, fit.Name);
        MarkClean();
        Status = deleteRefused is { } refused
            ? string.Format(FittingText.StatusSavedButOldNotRemoved, refused)
            : Sentences.Join(string.Format(FittingText.StatusUpdatedInGame, fit.Name, ch.Name), Skipped(skipped));
        return true;
    }

    /// <summary>A character that can have fittings written to it, or why not.</summary>
    private async Task<(long Id, string Name)?> WritableCharacterAsync(long characterId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ch = await db.Characters.AsNoTracking().Where(c => c.Id == characterId)
            .Select(c => new { c.Id, c.Name, c.GrantedScopes, c.RefreshToken }).FirstOrDefaultAsync();
        if (ch is null) { Status = FittingText.StatusCharacterNotInApp; return null; }
        if (ch.RefreshToken.Length == 0) { Status = string.Format(FittingText.StatusTokenExpired, ch.Name); return null; }
        if (!ch.GrantedScopes.Split(' ').Contains(GameFittings.WriteScope))
        {
            Status = string.Format(FittingText.StatusNoWriteScope, ch.Name);
            return null;
        }
        return (ch.Id, ch.Name);
    }

    /// <summary>What a fitting had no place for, named in a sentence to follow the status; "" when nothing.</summary>
    private string Skipped(IReadOnlyList<int> skipped) =>
        skipped.Count == 0 ? "" : string.Format(FittingText.NotSavedNoPlace, string.Join(CommonText.ListSeparator,
            skipped.Select(id => _data is not null && _data.TryType(id, out var t)
                ? SdeNames.Type(id, t.Name)
                : string.Format(FittingText.SkippedUnknownType, id))));

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
            ValueBasis = FittingText.ValueNoPrices;
            return;
        }
        ValueText      = $"{IskText.Compact(hull + fitted + bays + cargo)} ISK";
        ValueBreakdown = string.Format(FittingText.ValueHull, IskText.Compact(hull))
                       + "    " + string.Format(FittingText.ValueModules, IskText.Compact(fitted))
                       + (bays  > 0 ? "    " + string.Format(FittingText.ValueDrones, IskText.Compact(bays)) : "")
                       + (cargo > 0 ? "    " + string.Format(FittingText.ValueCargo, IskText.Compact(cargo)) : "")
                       + (implant > 0 ? "\n" + string.Format(FittingText.ValueImplants, IskText.Compact(implant)) : "");
        ValueBasis     = unpriced > 0
            ? Plurals.Format(FittingText.ResourceManager, nameof(FittingText.ValuePricesUnpricedOther), unpriced, prices.Basis)
            : string.Format(FittingText.ValuePrices, prices.Basis);
    }

    // ── Damage profile ──────────────────────────────────────────────────────────

    /// <summary>The profiles to pick from. The key — the English name, as the choice has always
    /// been remembered — is what is saved and looked up; the name is only shown.</summary>
    public IReadOnlyList<DamageProfileOption> DamageProfiles { get; } =
    [
        new("Even (25% each)", FittingText.ProfileEven,     DamageProfile.Uniform),
        new("EM",              FittingText.DamageEm,        new DamageProfile(100, 0, 0, 0)),
        new("Thermal",         FittingText.DamageThermal,   new DamageProfile(0, 100, 0, 0)),
        new("Kinetic",         FittingText.DamageKinetic,   new DamageProfile(0, 0, 100, 0)),
        new("Explosive",       FittingText.DamageExplosive, new DamageProfile(0, 0, 0, 100)),
        new("Custom",          FittingText.ProfileCustom,   null),
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
        // Remembered per machine, as the other tools remember their choices: "key|em|th|kin|exp".
        try { UiState.Set(ProfileKey, FormattableString.Invariant($"{_selectedProfile?.Key}|{CustomEm}|{CustomThermal}|{CustomKinetic}|{CustomExplosive}")); } catch { }
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
            SelectedProfile = DamageProfiles.FirstOrDefault(p => p.Key == parts[0]) ?? DamageProfiles[0];
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
        fit.Drones.AddRange(Drones.Select(d => new FitDrone(d.TypeId, d.Count, d.Active,
            d.IsFighter ? d.Abilities.Where(a => a.IsOn).Select(a => a.Ability.EffectId).ToList() : null)));
        fit.Implants.AddRange(Implants.Where(i => !i.IsBooster).Select(i => i.TypeId));
        fit.Boosters.AddRange(Implants.Where(i => i.IsBooster).Select(i => i.TypeId));
        fit.Cargo.AddRange(Cargo.Select(c => (c.TypeId, c.Quantity)));
        return fit;
    }

    private async Task<SkillSet> SkillsAsync()
    {
        var src = SelectedSkillSource ?? new SkillSourceOption(FittingText.PilotAllV, null, 5);
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
            if (TrimLaunchedDrones(engine)) { await RecalculateAsync(ct); return; }
            Stats = snap;
            for (var i = 0; i < _modules.Count; i++)
                _modules[i].Detail = snap.ModuleDetail.GetValueOrDefault(i, "");
            for (var i = 0; i < Drones.Count; i++)
                Drones[i].Detail = snap.DroneDetail.GetValueOrDefault(i, "");
            // Slot counts can change with the fit (subsystems add slots), so the sections are
            // laid out again each time; the module rows themselves are the same objects.
            RebuildSlots();
            await ApplyPricesAsync(fit);
            this.RaisePropertyChanged(nameof(TabDirty));
            Tool.TabCalculated(this);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = string.Format(FittingText.StatusCalculationFailed, ex.Message); }
    }

    private bool _trimmingDrones;
    /// <summary>The drone stack last changed by hand — the one to cut back when there is too much out.</summary>
    private FittingDroneRowVm? _droneEdited;

    /// <summary>
    /// Keeps the launched drones within what the pilot can control (their skills) and what the
    /// hull's bandwidth carries. A stack just raised by hand gives way first; otherwise the stacks
    /// fill in order, as when a fit is loaded — EFT and in-game fittings do not say which drones
    /// are out, only what is in the bay. Returns whether anything changed.
    ///
    /// <para>Fighters have limits of their own (tubes and fighter slots) and are left alone here.</para>
    /// </summary>
    private bool TrimLaunchedDrones(DogmaEngine e)
    {
        var edited = _droneEdited;
        _droneEdited = null;
        var changed = TrimDrones(e, edited) | TrimFighters(e, edited);
        if (changed) Status = FittingText.StatusDronesCutBack;
        return changed;
    }

    /// <summary>Rows in fit order, but the one changed by hand last — it is the one to give way.</summary>
    private List<FittingDroneRowVm> InOrder(bool fighters, FittingDroneRowVm? edited)
    {
        var rows = Drones.Where(d => d.IsFighter == fighters).ToList();
        if (edited is not null && rows.Remove(edited)) rows.Add(edited);
        return rows;
    }

    /// <summary>
    /// Squadrons in tubes: no more than the hull has tubes, and no more of each class (light,
    /// support, heavy — or their structure versions) than it has launch slots for it.
    /// </summary>
    private bool TrimFighters(DogmaEngine e, FittingDroneRowVm? edited)
    {
        var rows = InOrder(true, edited);
        if (rows.Count == 0) return false;
        var s = new FitStats(e);
        var tubes = s.FighterTubes;
        var slots = Enum.GetValues<FighterClass>().ToDictionary(c => c, s.FighterSlots);
        var changed = false;
        _trimmingDrones = true;
        try
        {
            foreach (var row in rows.Where(r => r.Launched))
            {
                var cls = FighterAbilities.ClassOf(_data!, _data!.Type(row.TypeId));
                if (tubes > 0 && cls is { } c && slots[c] > 0) { tubes--; slots[c]--; continue; }
                row.Launched = false;
                changed = true;
            }
        }
        finally { _trimmingDrones = false; }
        return changed;
    }

    private bool TrimDrones(DogmaEngine e, FittingDroneRowVm? edited)
    {
        var rows = InOrder(false, edited);
        if (rows.Count == 0) return false;

        var roomCount = (int)Math.Round(e.Value(e.Character, "maxActiveDrones"));
        var roomBw    = new FitStats(e).DroneBandwidth;
        double BandwidthOf(int typeId) =>
            e.DroneStacks.FirstOrDefault(d => d.Type.Id == typeId) is { } d ? e.Value(d, "droneBandwidthUsed")
            : _data!.Attribute("droneBandwidthUsed")?.Id is { } a ? _data.Type(typeId).Attr(a) ?? 0 : 0;

        var changed = false;
        _trimmingDrones = true;
        try
        {
            foreach (var row in rows)
            {
                var each    = BandwidthOf(row.TypeId);
                var allowed = Math.Min(row.Active, Math.Max(0, roomCount));
                if (each > 0) allowed = Math.Min(allowed, Math.Max(0, (int)Math.Floor(roomBw / each + 1e-9)));
                if (allowed != row.Active) { row.Active = allowed; changed = true; }
                roomCount -= allowed;
                roomBw    -= allowed * each;
            }
        }
        finally { _trimmingDrones = false; }
        return changed;
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
        foreach (var (name, layer) in new[] { (FittingText.LayerShield, s.Shield), (FittingText.LayerArmor, s.Armor), (FittingText.LayerHull, s.Hull) })
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
        snap.WeaponDps = s.WeaponDps(weapons); snap.DroneDps = s.DroneDps(weapons); snap.FighterDps = s.FighterDps(weapons);
        snap.Volley = s.Volley(weapons);

        snap.FighterBay = s.FighterBayUsed; snap.FighterBayOut = s.FighterBay;
        snap.Tubes = s.FighterTubesUsed; snap.TubesOut = s.FighterTubes;
        foreach (var c in Enum.GetValues<FighterClass>())
            if (s.FighterSlots(c) is var n && s.FighterSlotsUsed(c) is var used && (n > 0 || used > 0))
                snap.FighterSlots.Add((c, used, n));
        for (var i = 0; i < e.Drones.Count; i++)
        {
            var d = e.Drones[i];
            var mine = weapons.Where(w => w.Item == d).ToList();
            snap.DroneDetail[i] = d.Kind == DogmaItemKind.Fighter
                ? string.Join(" · ", mine.Select(w => w.CycleSeconds > 0
                    ? string.Format(FittingText.AbilityDps, w.Label, w.Dps.Total)
                    : string.Format(FittingText.AbilityAlpha, w.Label, w.Volley.Total)))
                : mine.Sum(w => w.Dps.Total) is var dps and > 0 ? string.Format(FittingText.Dps1, dps) : "";
        }

        var sensors = new[] { "scanRadarStrength", "scanLadarStrength", "scanMagnetometricStrength", "scanGravimetricStrength" }
            .Select(a => (Attribute: a, Value: e.Value(e.Ship, a))).OrderByDescending(x => x.Value).First();
        snap.Sensor = sensors.Value; snap.SensorAttribute = sensors.Attribute;

        var byModule = weapons.Where(w => w.Kind is not (WeaponKind.Drone or WeaponKind.Fighter)).ToDictionary(w => w.Item, w => w);
        for (var i = 0; i < e.Modules.Count; i++)
        {
            var m = e.Modules[i];
            var parts = new List<string>();
            if (m.Kind == DogmaItemKind.Rig) parts.Add(string.Format(FittingText.DetailCalibration, e.Value(m, "upgradeCost")));
            else
            {
                if (e.Value(m, "cpu") is var cpu and > 0) parts.Add(string.Format(FittingText.DetailCpu, cpu));
                if (e.Value(m, "power") is var pg and > 0) parts.Add(string.Format(FittingText.DetailPg, pg));
            }
            if (byModule.TryGetValue(m, out var w)) parts.Add(string.Format(FittingText.Dps1, w.Dps.Total));
            if (repairs.FirstOrDefault(r => r.Item == m) is { } rep) parts.Add(string.Format(rep.Layer switch
            {
                TankLayer.Shield => FittingText.DetailRepairShield,
                TankLayer.Armor  => FittingText.DetailRepairArmor,
                _                => FittingText.DetailRepairHull,
            }, rep.PerSecond));
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
    public string HardpointsText  => Stats is { } s ? string.Format(FittingText.Hardpoints, s.Turrets, s.TurretsOut, s.Launchers, s.LaunchersOut) : "";
    public bool   HardpointsOver  => Stats is { } s && (s.Turrets > s.TurretsOut || s.Launchers > s.LaunchersOut);
    public string CargoText       => Stats is { } s ? string.Format(FittingText.CargoLine, s.Cargo, s.CargoOut) : "";
    public bool   CargoOver       => Stats is { } s && s.Cargo > s.CargoOut + 1e-9;
    public string DroneText       => Stats is { } s ? string.Format(FittingText.DroneLine, s.DroneBay, s.DroneBayOut, s.Bandwidth, s.BandwidthOut) : "";
    public bool   DroneOver       => Stats is { } s && (s.DroneBay > s.DroneBayOut + 1e-9 || s.Bandwidth > s.BandwidthOut + 1e-9);
    /// <summary>A carrier has no drone bay; the drone line shows only where there is one, or drones in it.</summary>
    public bool   HasDroneBay     => Stats is { } s && (s.DroneBayOut > 0 || s.BandwidthOut > 0 || s.DroneBay > 0);
    public bool   HasFighterBay   => Stats is { } s && (s.FighterBayOut > 0 || s.TubesOut > 0 || s.FighterBay > 0);
    public string FighterText     => Stats is { } s
        ? string.Format(FittingText.FighterLine, s.FighterBay, s.FighterBayOut, s.Tubes, s.TubesOut)
          + string.Concat(s.FighterSlots.Select(c => "    " + FighterSlotsText(c.Class, c.Used, c.Out))) : "";
    public bool   FighterOver     => Stats is { } s && (s.FighterBay > s.FighterBayOut + 1e-9 || s.Tubes > s.TubesOut
                                        || s.FighterSlots.Any(c => c.Used > c.Out));
    public IReadOnlyList<TankLayerRow> TankRows => Stats?.Tank ?? [];
    public string EhpText         => Stats is { } s ? string.Format(FittingText.EhpLine, s.Ehp) : "";
    /// <summary>Peak shield regeneration and recharge time — what a passive shield tank lives on.</summary>
    public string RegenText       => Stats is { Rates: { } r } s && r.PassiveShield > 0
        ? string.Format(FittingText.RegenLine, r.PassiveShield, r.PassiveShield / s.ShieldTaken, FormatDuration(s.ShieldRecharge)) : "";
    /// <summary>What active modules repair, per layer, raw and effective against even damage.</summary>
    public string RepairText      => Stats is { Rates: { } r } s ? string.Join(Environment.NewLine, new[]
        {
            r.ShieldBoost > 0 ? string.Format(FittingText.RepairShield, r.ShieldBoost, r.ShieldBoost / s.ShieldTaken) : null,
            r.ArmorRepair > 0 ? string.Format(FittingText.RepairArmor, r.ArmorRepair, r.ArmorRepair / s.ArmorTaken) : null,
            r.HullRepair  > 0 ? string.Format(FittingText.RepairHull, r.HullRepair, r.HullRepair / s.HullTaken) : null,
            (r.ShieldBoost + r.ArmorRepair + r.HullRepair) > 0 && s.Cap is { Stable: false } c
                ? string.Format(FittingText.RepairCapRunsOut, FormatDuration(c.LastsSeconds)) : null,
        }.OfType<string>()) : "";
    public bool HasRepairs        => Stats is { Rates: { } r } && r.ShieldBoost + r.ArmorRepair + r.HullRepair > 0;
    public string CapText         => Stats?.Cap is { } c ? $"{c.Capacity:N0} GJ" : "";
    public string CapStateText    => Stats?.Cap is { } c
        ? c.Stable ? string.Format(FittingText.CapStable, c.StableFraction * 100) : string.Format(FittingText.CapLasts, FormatDuration(c.LastsSeconds)) : "";
    public bool   CapStable       => Stats?.Cap?.Stable ?? true;
    public string CapFlowText     => Stats?.Cap is { } c
        ? string.Format(FittingText.CapFlow, c.Drain, c.PeakRecharge)
          + (c.Injection > 0 ? "    " + string.Format(FittingText.CapBoosters, c.Injection) : "") : "";
    public string DpsText         => Stats is { } s ? string.Format(FittingText.Dps0, s.WeaponDps.Total + s.DroneDps.Total + s.FighterDps.Total) : "";
    public string DpsSplitText    => Stats is { } s
        ? string.Format(FittingText.DpsWeapons, s.WeaponDps.Total) + "    " + string.Format(FittingText.DpsDrones, s.DroneDps.Total)
          + (s.FighterDps.Total > 0 || s.TubesOut > 0 ? "    " + string.Format(FittingText.DpsFighters, s.FighterDps.Total) : "")
          + "    " + string.Format(FittingText.DpsVolley, s.Volley.Total) : "";
    public string DamageTypesText => Stats is { } s && s.WeaponDps.Total + s.DroneDps.Total + s.FighterDps.Total > 0
        ? DamageMix(s.WeaponDps + s.DroneDps + s.FighterDps) : "";
    public string SpeedText       => Stats is { } s ? $"{s.Speed:N0} m/s" : "";
    public string NavText         => Stats is { } s ? string.Format(FittingText.NavLine, s.Align, s.Signature, s.Warp) : "";
    public string MassText        => Stats is { } s ? string.Format(FittingText.MassLine, s.Mass, s.Agility) : "";
    public string TargetingText   => Stats is { } s ? string.Format(FittingText.TargetingLine, s.Range / 1000, s.ScanRes, s.MaxTargets) : "";
    /// <summary>The strongest sensor, by its type.</summary>
    public string SensorText      => Stats is { } s ? string.Format(s.SensorAttribute switch
        {
            "scanLadarStrength"         => FittingText.SensorLadar,
            "scanMagnetometricStrength" => FittingText.SensorMagnetometric,
            "scanGravimetricStrength"   => FittingText.SensorGravimetric,
            _                           => FittingText.SensorRadar,
        }, s.Sensor) : "";

    /// <summary>A class's launch slots, in use and on the hull: "Light 1 / 2".</summary>
    private static string FighterSlotsText(FighterClass c, int used, int slots) => c switch
    {
        FighterClass.Light or FighterClass.StandupLight     => string.Format(FittingText.FighterSlotsLight, used, slots),
        FighterClass.Support or FighterClass.StandupSupport => string.Format(FittingText.FighterSlotsSupport, used, slots),
        _                                                    => string.Format(FittingText.FighterSlotsHeavy, used, slots),
    };

    private static string DamageMix(DamageBreakdown d) =>
        d.Total <= 0 ? "" : string.Format(FittingText.DamageMix, d.Em / d.Total, d.Thermal / d.Total, d.Kinetic / d.Total, d.Explosive / d.Total);

    private static string FormatDuration(double seconds) =>
        seconds >= 3600 ? string.Format(FittingText.DurationHours, (int)(seconds / 3600), (int)(seconds % 3600 / 60))
        : seconds >= 60 ? string.Format(FittingText.DurationMinutes, (int)(seconds / 60), (int)(seconds % 60))
        : string.Format(FittingText.DurationSeconds, seconds);

    private void RaiseStatText()
    {
        foreach (var p in new[] { nameof(CpuText), nameof(CpuFraction), nameof(CpuOver), nameof(PowerText), nameof(PowerFraction), nameof(PowerOver),
                     nameof(CalibText), nameof(CalibFraction), nameof(CalibOver), nameof(HardpointsText), nameof(HardpointsOver), nameof(DroneText), nameof(DroneOver), nameof(HasDroneBay), nameof(HasFighterBay), nameof(FighterText), nameof(FighterOver), nameof(CargoText), nameof(CargoOver),
                     nameof(TankRows), nameof(EhpText), nameof(RegenText), nameof(RepairText), nameof(HasRepairs), nameof(CapText), nameof(CapStateText), nameof(CapStable), nameof(CapFlowText),
                     nameof(DpsText), nameof(DpsSplitText), nameof(DamageTypesText), nameof(SpeedText), nameof(NavText), nameof(MassText),
                     nameof(TargetingText), nameof(SensorText) })
            this.RaisePropertyChanged(p);
    }

    // ── Export and load ─────────────────────────────────────────────────────────

    public async Task CopyEftAsync()
    {
        if (_data is null || _shipTypeId == 0) { Status = FittingText.StatusNothingToCopy; return; }
        // English, as the game and other tools read EFT.
        await Tool.CopyText.Handle(EftFormat.Write(CurrentFit(), _data));
        Status = FittingText.StatusCopiedEft;
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
        // Launched counts are a starting point; the first calculation trims them to what the pilot
        // and hull allow. Squadrons start in their tubes.
        foreach (var d in fit.Drones)
            AddDrone(d.TypeId, _data.Type(d.TypeId).Name, d.Count,
                _data.Type(d.TypeId).CategoryId == DogmaData.CategoryFighter ? d.Count : Math.Min(d.Count, d.Active > 0 ? d.Active : 5),
                d.Abilities);
        foreach (var i in fit.Implants) AddImplant(i, _data.Type(i).Name, false);
        foreach (var b in fit.Boosters) AddImplant(b, _data.Type(b).Name, true);
        foreach (var (id, qty) in fit.Cargo) AddCargo(id, _data.Type(id).Name, qty);
        RebuildSlots();
        await RecalculateAsync(CancellationToken.None);
        MarkClean();
    }

    /// <summary>Saves in EVE Console, under the fit's name: the same name on the same hull replaces it.</summary>
    private async Task<bool> SaveToAppAsync()
    {
        if (_data is null || _shipTypeId == 0) { Status = FittingText.StatusNothingToSave; return false; }
        var name = FitName.Trim().Length > 0 ? FitName.Trim() : string.Format(FittingText.DefaultFitName, ShipName);
        await using var db = await _dbFactory.CreateDbContextAsync();
        // Saving under the name of a fit already saved for this hull replaces it; a new name adds one.
        var row = await db.SavedFits.FirstOrDefaultAsync(f => f.Name == name && f.ShipTypeId == _shipTypeId);
        if (row is null) { row = new SavedFit { Name = name, ShipTypeId = _shipTypeId }; db.SavedFits.Add(row); }
        row.Eft       = EftFormat.Write(CurrentFit(), _data);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        _loadedSavedId = row.Id;
        _lastSavedToApp = row.Id;
        await Tool.LoadSavedListAsync();
        MarkClean();
        Status = string.Format(FittingText.StatusSavedInApp, name);
        return true;
    }
}

/// <summary>Greys out a save destination that cannot be used.</summary>
public static class SaveFitDialogConverters
{
    public static readonly Avalonia.Data.Converters.IValueConverter EnabledOpacity =
        new Avalonia.Data.Converters.FuncValueConverter<bool, double>(enabled => enabled ? 1.0 : 0.45);
}
