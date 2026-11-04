using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Pi;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>How soon something runs out: drawn in the status colours.</summary>
public enum FuelUrgency { None, Fine, Soon, Critical, Out }

/// <summary>A label and its value in one of the detail panel's sections.</summary>
public sealed record CorpStructureField(string Label, string Value, FuelUrgency Urgency = FuelUrgency.None)
{
    public bool IsCritical => Urgency is FuelUrgency.Critical or FuelUrgency.Out;
    public bool IsSoon     => Urgency == FuelUrgency.Soon;
}

/// <summary>One fuel in the detail panel's fuel table.</summary>
public sealed class CorpFuelRowVm : ReactiveObject
{
    public CorpFuelRowVm(string name, string onHand, string perDay, string lasts, string runsOut,
                         string source, FuelUrgency urgency, int typeId)
    {
        Name = name; OnHand = onHand; PerDay = perDay; Lasts = lasts; RunsOut = runsOut;
        Source = source; Urgency = urgency;
        _ = ItemIcons.LoadAsync(typeId, b => Icon = b);
    }

    public string Name    { get; }
    public string OnHand  { get; }
    public string PerDay  { get; }
    public string Lasts   { get; }
    public string RunsOut { get; }
    public string Source  { get; }
    public FuelUrgency Urgency { get; }
    public bool IsCritical => Urgency is FuelUrgency.Critical or FuelUrgency.Out;
    public bool IsSoon     => Urgency == FuelUrgency.Soon;

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; private set => this.RaiseAndSetIfChanged(ref _icon, value); }
}

/// <summary>A band of the fit ("High slots") and what is in it.</summary>
public sealed record CorpFitGroupVm(string Title, IReadOnlyList<CorpFitItemVm> Items);

public sealed class CorpFitItemVm : ReactiveObject
{
    public CorpFitItemVm(string name, string quantity, bool byHand, int typeId)
    {
        Name = name; Quantity = quantity; ByHand = byHand;
        _ = ItemIcons.LoadAsync(typeId, b => Icon = b);
    }

    public string Name        { get; }
    public string Quantity    { get; }
    /// <summary>Entered by hand in the Structure Browser, where the assets did not reach.</summary>
    public bool   ByHand      { get; }
    public bool   HasQuantity => Quantity.Length > 0;

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; private set => this.RaiseAndSetIfChanged(ref _icon, value); }
}

/// <summary>One type in a Metenox's moon material bay, or the bay's total (no icon, bold).</summary>
public sealed class CorpBayRowVm : ReactiveObject
{
    public CorpBayRowVm(string name, string units, string volume, string value, int typeId, bool isTotal = false)
    {
        Name = name; Units = units; Volume = volume; Value = value; IsTotal = isTotal;
        if (typeId > 0) _ = ItemIcons.LoadAsync(typeId, b => Icon = b);
    }

    public string Name    { get; }
    public string Units   { get; }
    public string Volume  { get; }
    public string Value   { get; }
    public bool   IsTotal { get; }

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; private set => this.RaiseAndSetIfChanged(ref _icon, value); }
}

public sealed record CorpServiceVm(string Name, string StateText, bool IsOnline);

/// <summary>
/// One structure type on the Config tab: how many the corporation has anchored, and the fit they
/// should carry. Choosing a fit saves it at once.
/// </summary>
public sealed class CorpFitConfigRowVm : ReactiveObject
{
    private readonly Func<CorpFitConfigRowVm, CorpFitChoice, Task> _save;

    public CorpFitConfigRowVm(int typeId, string typeName, string status, IReadOnlyList<CorpFitChoice> choices,
                              CorpFitChoice selected, Func<CorpFitConfigRowVm, CorpFitChoice, Task> save)
    {
        TypeId = typeId; TypeName = typeName; Status = status; Choices = choices;
        _selected = selected; _save = save;
        _ = ItemIcons.LoadAsync(typeId, b => Icon = b);
    }

    public int    TypeId   { get; }
    public string TypeName { get; }
    public string Status   { get; }
    public IReadOnlyList<CorpFitChoice> Choices { get; }

    private CorpFitChoice _selected;
    /// <summary>⚠️ A null from the list is ignored: a ComboBox passes through null while its items
    /// are replaced, and taking that as a choice would clear the fit.</summary>
    public CorpFitChoice? Selected
    {
        get => _selected;
        set
        {
            if (value is null || value == _selected) return;
            var was = _selected;
            this.RaiseAndSetIfChanged(ref _selected, value);
            if (!value.IsCopy) _ = _save(this, value);
            else { _selected = was; this.RaisePropertyChanged(); }
        }
    }

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; private set => this.RaiseAndSetIfChanged(ref _icon, value); }
}

/// <summary>One structure or tower in the list.</summary>
public sealed class CorpStructureRowVm : ReactiveObject
{
    public CorpStructureInfo Info { get; }

    public CorpStructureRowVm(CorpStructureInfo info, DateTimeOffset now)
    {
        Info = info;
        _ = ItemIcons.LoadAsync(info.TypeId, b => Icon = b);
        var left = info.RunsOut is { } r ? r - now : (TimeSpan?)null;

        FuelLeft     = !info.IsPowered && info.Kind == CorpStructureKind.Tower ? "—"
                     : left is { } l ? (l > TimeSpan.Zero ? PiFormat.Duration(l) : CorpText.CsOutOfFuel)
                     : info.Kind == CorpStructureKind.Upwell && !info.IsPowered
                         ? (info.NeedsFuel ? CorpText.CsLowPower : CorpText.CsNoServices)
                         : "—";
        RunsOutText  = info.RunsOut is { } at ? PiFormat.When(at) : "";
        RunsOutSort  = info.RunsOut ?? DateTimeOffset.MaxValue;
        Urgency      = CorpStructuresViewModel.UrgencyOf(left, info);
        PerDay       = info.BlocksPerHour is { } h ? (h * 24).ToString("N0", CultureInfo.CurrentCulture) : "";
        PerDaySort   = info.BlocksPerHour ?? -1;
        IskPerDay    = info.FuelIskPerDay is { } isk ? CorpStructuresViewModel.Isk(isk) : "";
        IskPerDaySort = info.FuelIskPerDay ?? -1;
        if (info.MoonBay.Count > 0)
        {
            PickupValueSort  = info.MoonBay.Sum(m => m.Value ?? 0);
            PickupVolumeSort = info.MoonBay.Sum(m => m.Volume);
            PickupValue      = CorpStructuresViewModel.Isk(PickupValueSort);
            PickupVolume     = PickupVolumeSort.ToString("N0", CultureInfo.CurrentCulture);
        }
        if (info.FitCheck is { Known: true, HasProblems: true } fc)
            FitTip = CorpStructuresViewModel.FitCheckAgainst(fc) + Environment.NewLine + string.Join(Environment.NewLine,
                CorpStructuresViewModel.FitCheckFields(fc).Select(f => $"{f.Label}: {f.Value}"));
        var online   = info.Services.Count(s => s.IsOnline);
        ServicesText = info.Services.Count > 0 ? $"{online} / {info.Services.Count}" : "";
        StateText    = CorpStructuresViewModel.StateLabel(info.State);
        (FitText, FitUrgency) = CorpStructuresViewModel.FitSummary(info.FitCheck);
        Location     = info.Security is { } sec
            ? $"{info.SystemName} {SecurityColors.Text(sec)}"
            : info.SystemName;
        SecuritySort = info.Security ?? 0;
    }

    public string Name         => Info.Name;
    public string TypeName     => Info.TypeName;
    public string Region       => Info.RegionName;
    public string Location     { get; }
    public double SecuritySort { get; }
    public string StateText    { get; }
    public string ServicesText { get; }
    public string FuelLeft     { get; }
    public string RunsOutText  { get; }
    public DateTimeOffset RunsOutSort { get; }
    public string PerDay       { get; }
    public double PerDaySort   { get; }
    /// <summary>The fuel it burns a day, in ISK at the asset valuation's prices.</summary>
    public string IskPerDay    { get; }
    public double IskPerDaySort { get; }
    /// <summary>What a Metenox holds to be picked up: its moon bay's worth and m³. Empty otherwise.</summary>
    public string PickupValue  { get; } = "";
    public double PickupValueSort { get; } = -1;
    public string PickupVolume { get; } = "";
    public double PickupVolumeSort { get; } = -1;
    /// <summary>The info icon's tooltip: what is wrong against the fit, line by line. Empty, and
    /// no icon, when nothing is.</summary>
    public string FitTip       { get; } = "";
    public bool   HasFitIssue  => FitTip.Length > 0;
    public FuelUrgency Urgency { get; }
    public bool IsCritical     => Urgency is FuelUrgency.Critical or FuelUrgency.Out;
    public bool IsSoon         => Urgency == FuelUrgency.Soon;
    public bool IsTower        => Info.Kind == CorpStructureKind.Tower;
    public bool IsSkyhook      => Info.Kind == CorpStructureKind.Skyhook;

    /// <summary>What is wrong against the structure type's fit, in a few words: "2 missing · ammo
    /// low"; "Matches" when nothing is; empty with no fit set for the type.</summary>
    public string FitText      { get; }
    public FuelUrgency FitUrgency { get; }
    public bool FitIsBad       => FitUrgency is FuelUrgency.Critical or FuelUrgency.Out;
    public bool FitIsLow       => FitUrgency == FuelUrgency.Soon;
    public bool FitIsGood      => FitUrgency == FuelUrgency.Fine;
    /// <summary>Problems first when the column is sorted.</summary>
    public int  FitSort        => FitUrgency switch { FuelUrgency.Critical => 0, FuelUrgency.Soon => 1, FuelUrgency.Fine => 3, _ => 2 };

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; private set => this.RaiseAndSetIfChanged(ref _icon, value); }
}

/// <summary>
/// Corp Structures: every Upwell structure and starbase the chosen corporation has anchored in
/// space, with its state, services, fit, fuel, burn and when it runs dry.
/// </summary>
public class CorpStructuresViewModel : ReactiveObject, IPeriodicRefresh
{
    /// <summary>Set the first time this tool is opened; until then its refresh timer is a
    /// no-op. See IPeriodicRefresh.</summary>
    public bool AutoRefreshEnabled
    {
        get => _opened;
        set
        {
            var first = value && !_opened;
            _opened = value;
            if (first) _ = LoadAsync();
        }
    }
    private bool _opened;

    /// <summary>Under this many days left, a structure's fuel shows as running low.</summary>
    public const double SoonDays     = 7;
    /// <summary>Under this many days left, as critical.</summary>
    public const double CriticalDays = 3;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger? _errorLogger;
    private List<CorpStructureRowVm> _all = [];
    private int _loadSeq;

    public ObservableCollection<Corporation> Corps { get; }

    private Corporation? _selectedCorp;
    public Corporation? SelectedCorp
    {
        get => _selectedCorp;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedCorp, value);
            // Remembered on this machine — a choice, not a pick made for the user while the list
            // fills, nor the null a refilled list passes through.
            _autoPicked = _picking;
            if (value is not null && !_picking) UiState.SetLong(UiState.CorpStructuresCorp, value.Id);
        }
    }

    private bool _picking, _autoPicked;

    public ObservableCollection<CorpStructureRowVm> Rows { get; } = [];

    private CorpStructureRowVm? _selected;
    public CorpStructureRowVm? Selected
    {
        get => _selected;
        set { this.RaiseAndSetIfChanged(ref _selected, value); BuildDetail(); }
    }

    private string _search = "";
    public string Search
    {
        get => _search;
        set { this.RaiseAndSetIfChanged(ref _search, value); ApplyFilter(); }
    }

    private bool _showUpwell = true;
    public bool ShowUpwell
    {
        get => _showUpwell;
        set { this.RaiseAndSetIfChanged(ref _showUpwell, value); ApplyFilter(); }
    }

    private bool _showTowers = true;
    public bool ShowTowers
    {
        get => _showTowers;
        set { this.RaiseAndSetIfChanged(ref _showTowers, value); ApplyFilter(); }
    }

    private bool _showSkyhooks = true;
    public bool ShowSkyhooks
    {
        get => _showSkyhooks;
        set { this.RaiseAndSetIfChanged(ref _showSkyhooks, value); ApplyFilter(); }
    }

    private string _status = CorpText.StatusSelectCorp;
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private string _summary = "";
    /// <summary>"212 structures · 3 low on fuel · 1 out" — above the list.</summary>
    public string Summary
    {
        get => _summary;
        private set => this.RaiseAndSetIfChanged(ref _summary, value);
    }

    private bool _hasRows;
    public bool HasRows
    {
        get => _hasRows;
        private set => this.RaiseAndSetIfChanged(ref _hasRows, value);
    }

    private string _emptyText = "";
    public string EmptyText
    {
        get => _emptyText;
        private set => this.RaiseAndSetIfChanged(ref _emptyText, value);
    }

    // ── Detail ───────────────────────────────────────────────────────────────

    private bool _hasDetail;
    public bool HasDetail
    {
        get => _hasDetail;
        private set => this.RaiseAndSetIfChanged(ref _hasDetail, value);
    }

    private string _detailTitle = "";
    public string DetailTitle { get => _detailTitle; private set => this.RaiseAndSetIfChanged(ref _detailTitle, value); }

    private string _detailSubtitle = "";
    public string DetailSubtitle { get => _detailSubtitle; private set => this.RaiseAndSetIfChanged(ref _detailSubtitle, value); }

    private Bitmap? _detailRender;
    /// <summary>The structure's picture, from the image server.</summary>
    public Bitmap? DetailRender { get => _detailRender; private set => this.RaiseAndSetIfChanged(ref _detailRender, value); }

    public ObservableCollection<CorpStructureField> StatusFields   { get; } = [];
    public ObservableCollection<CorpStructureField> LocationFields { get; } = [];
    public ObservableCollection<CorpFuelRowVm>      FuelRows       { get; } = [];
    public ObservableCollection<CorpServiceVm>      Services       { get; } = [];
    public ObservableCollection<CorpFitGroupVm>     FitGroups      { get; } = [];
    public ObservableCollection<CorpStructureField> MoonFields     { get; } = [];
    public ObservableCollection<CorpBayRowVm>       MoonBay        { get; } = [];
    public ObservableCollection<CorpStructureField> TowerFields    { get; } = [];

    private bool _hasMoonBay;   public bool HasMoonBay   { get => _hasMoonBay;   private set => this.RaiseAndSetIfChanged(ref _hasMoonBay, value); }

    public ObservableCollection<CorpStructureField> SkyhookFields { get; } = [];
    /// <summary>A skyhook's reagents: name, secured, unsecured, last cycle (in the bay row's columns).</summary>
    public ObservableCollection<CorpBayRowVm>       Reagents      { get; } = [];
    /// <summary>The user's characters' dens on the skyhook: owner, then the den in a line.</summary>
    public ObservableCollection<CorpStructureField> SkyhookDens   { get; } = [];
    private bool _isSkyhookDetail; public bool IsSkyhookDetail { get => _isSkyhookDetail; private set => this.RaiseAndSetIfChanged(ref _isSkyhookDetail, value); }
    private bool _hasReagents;     public bool HasReagents     { get => _hasReagents;     private set => this.RaiseAndSetIfChanged(ref _hasReagents, value); }
    private string _moonBayFill = "";
    /// <summary>"23,828 of 500,000 m³ (4.8% full)"; empty where the bay's size is not known.</summary>
    public string MoonBayFill { get => _moonBayFill; private set => this.RaiseAndSetIfChanged(ref _moonBayFill, value); }
    private double _moonBayPct;
    public double MoonBayPct { get => _moonBayPct; private set => this.RaiseAndSetIfChanged(ref _moonBayPct, value); }
    public bool HasMoonBayFill => MoonBayFill.Length > 0;

    private bool _hasFuel;      public bool HasFuel      { get => _hasFuel;      private set => this.RaiseAndSetIfChanged(ref _hasFuel, value); }
    private bool _hasServices;  public bool HasServices  { get => _hasServices;  private set => this.RaiseAndSetIfChanged(ref _hasServices, value); }
    private bool _hasFit;       public bool HasFit       { get => _hasFit;       private set => this.RaiseAndSetIfChanged(ref _hasFit, value); }
    private bool _hasMoon;      public bool HasMoon      { get => _hasMoon;      private set => this.RaiseAndSetIfChanged(ref _hasMoon, value); }
    private bool _hasTower;     public bool HasTower     { get => _hasTower;     private set => this.RaiseAndSetIfChanged(ref _hasTower, value); }
    private bool _hasTowerSettings; public bool HasTowerSettings { get => _hasTowerSettings; private set => this.RaiseAndSetIfChanged(ref _hasTowerSettings, value); }

    private string _fuelNote = "";
    /// <summary>How the burn figures were arrived at — shown under the fuel table.</summary>
    public string FuelNote { get => _fuelNote; private set => this.RaiseAndSetIfChanged(ref _fuelNote, value); }

    private string _fitNote = "";
    public string FitNote { get => _fitNote; private set => this.RaiseAndSetIfChanged(ref _fitNote, value); }
    public bool HasFitNote => FitNote.Length > 0;

    /// <summary>The structure against its type's fit: what is missing, wrong or low, line by line.</summary>
    public ObservableCollection<CorpStructureField> FitCheckLines { get; } = [];
    private bool _hasFitCheck; public bool HasFitCheck { get => _hasFitCheck; private set => this.RaiseAndSetIfChanged(ref _hasFitCheck, value); }
    private string _fitCheckNote = "";
    /// <summary>Which fit it was checked against, and where that fit is kept.</summary>
    public string FitCheckNote { get => _fitCheckNote; private set => this.RaiseAndSetIfChanged(ref _fitCheckNote, value); }

    // ── Config tab ───────────────────────────────────────────────────────────

    private int _selectedTabIndex;
    /// <summary>0 the structures, 1 the Config tab.</summary>
    public int SelectedTabIndex { get => _selectedTabIndex; set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value); }

    /// <summary>Each Upwell structure type the corporation has anchored, or has a fit set for.</summary>
    public ObservableCollection<CorpFitConfigRowVm> ConfigRows { get; } = [];
    private bool _hasConfigRows; public bool HasConfigRows { get => _hasConfigRows; private set => this.RaiseAndSetIfChanged(ref _hasConfigRows, value); }

    /// <summary>A structure to choose once the next load is in — set by <see cref="Show"/>.</summary>
    private long? _showStructure;

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public CorpStructuresViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        ObservableCollection<Corporation> corps,
        AppErrorLogger? errorLogger = null)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        Corps        = corps;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        RefreshCommand.ThrownExceptions.Subscribe(_ => { });

        PickRemembered();
        Corps.CollectionChanged += (_, _) => PickRemembered();

        // Read once the tool has been opened, not at startup for a screen nobody has looked at.
        this.WhenAnyValue(x => x.SelectedCorp)
            .Where(c => c is not null && _opened)
            .Subscribe(t => { _ = LoadAsync(); });

        // The fuel left counts down between polls; a reload every five minutes keeps it honest
        // and picks up whatever the pollers have brought in.
        Observable.Interval(TimeSpan.FromMinutes(5))
            .Where(_ => AutoRefreshEnabled)
            .ObserveOnUi("CorpStructures.AutoRefresh")
            .Where(_ => SelectedCorp is not null && !IsLoading)
            .Subscribe(t => { _ = LoadAsync(); });
    }

    /// <summary>
    /// Selects the corporation chosen last time on this machine, else the first — when nothing is
    /// selected, or when the selection was this method's own and the remembered one has since
    /// been added. The corporations arrive one at a time at startup, and the first in is rarely
    /// the one that was chosen.
    /// </summary>
    private void PickRemembered()
    {
        if (Corps.Count == 0) return;
        var id   = UiState.GetLong(UiState.CorpStructuresCorp, 0);
        var want = Corps.FirstOrDefault(c => c.Id == id);
        if (SelectedCorp is not null && Corps.Contains(SelectedCorp) && !(_autoPicked && want is not null)) return;

        _picking = true;
        try { SelectedCorp = want ?? Corps[0]; }
        finally { _picking = false; }
    }

    /// <summary>
    /// Opens on <paramref name="corpId"/> with <paramref name="structureId"/> chosen, for an
    /// Overview alert. Not remembered as the user's choice of corporation: the alert chose it.
    /// </summary>
    public void Show(long corpId, long? structureId)
    {
        SelectedTabIndex = 0;
        if (structureId is not null) { Search = ""; ShowUpwell = true; ShowTowers = true; ShowSkyhooks = true; }
        _showStructure = structureId;
        if (Corps.FirstOrDefault(c => c.Id == corpId) is { } corp && corp != SelectedCorp)
        {
            _picking = true;
            try { SelectedCorp = corp; }   // loads, once opened
            finally { _picking = false; }
        }
        else if (_opened) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (SelectedCorp is not { } corp) return;
        var seq = ++_loadSeq;
        IsLoading = true;
        Status    = CorpText.CsLoading;
        try
        {
            var infos = await Task.Run(() => CorpStructures.LoadAsync(_dbFactory, corp.Id));
            if (seq != _loadSeq) return;

            var now = DateTimeOffset.UtcNow;
            _all = [.. infos
                .Select(i => new CorpStructureRowVm(i, now))
                .OrderBy(r => r.Info.IsPowered ? 0 : r.Info.Kind == CorpStructureKind.Upwell ? 1 : 2)
                .ThenBy(r => r.RunsOutSort)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];

            var upwell = _all.Count(r => r.Info.Kind == CorpStructureKind.Upwell);
            var towers = _all.Count(r => r.IsTower);
            var hooks  = _all.Count(r => r.IsSkyhook);
            var low    = _all.Count(r => r.Urgency is FuelUrgency.Soon or FuelUrgency.Critical);
            var outs   = _all.Count(r => r.Urgency == FuelUrgency.Out);
            var unfit  = _all.Count(r => r.Info.FitCheck is { Known: true, HasProblems: true });
            var iskDay = _all.Sum(r => r.Info.FuelIskPerDay ?? 0);
            Summary = string.Format(CorpText.CsSummary, upwell, towers, low, outs)
                    + (hooks > 0 ? " · " + Plurals.Format(CorpText.ResourceManager, nameof(CorpText.CsSummarySkyhooksOther), hooks) : "")
                    + (unfit > 0 ? " · " + string.Format(CorpText.CsSummaryFit, unfit) : "")
                    + (iskDay > 0 ? " · " + string.Format(CorpText.CsSummaryIskPerDay, Isk(iskDay)) : "");
            Status  = string.Format(CorpText.CsLoadedAt, DateTime.Now.ToString("t", CultureInfo.CurrentCulture));

            var keep = _showStructure ?? Selected?.Info.Id;
            _showStructure = null;
            ApplyFilter();
            Selected = Rows.FirstOrDefault(r => r.Info.Id == keep) ?? Rows.FirstOrDefault();

            await LoadConfigAsync(corp.Id, infos, seq);
        }
        catch (Exception ex)
        {
            Status = string.Format(CorpText.CsLoadFailed, ex.Message);
            _errorLogger?.Log("CorpStructuresViewModel", "LoadAsync", ex);
        }
        finally
        {
            if (seq == _loadSeq) IsLoading = false;
        }
    }

    /// <summary>The Config tab: each Upwell structure type anchored, or with a fit set, and the
    /// saved fits for it to choose from.</summary>
    private async Task LoadConfigAsync(long corpId, List<CorpStructureInfo> infos, int seq)
    {
        var upwell   = infos.Where(i => i.Kind == CorpStructureKind.Upwell).ToList();
        var required = await Task.Run(() => CorpStructureFits.RequiredAsync(_dbFactory, corpId));
        var typeIds  = upwell.Select(i => i.TypeId).Concat(required.Keys).Distinct().ToList();
        var choices  = await Task.Run(() => CorpStructureFits.ChoicesAsync(_dbFactory, typeIds));
        var unnamed  = typeIds.Where(id => upwell.All(i => i.TypeId != id)).ToList();
        Dictionary<int, string> names = unnamed.Count == 0 ? [] : await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.SdeTypes.AsNoTracking().Where(t => unnamed.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => SdeNames.Type(t.TypeId, t.Name));
        });
        if (seq != _loadSeq) return;

        var rows = new List<CorpFitConfigRowVm>();
        foreach (var typeId in typeIds)
        {
            var mine = upwell.Where(i => i.TypeId == typeId).ToList();
            var list = new List<CorpFitChoice> { CorpFitChoice.None };
            list.AddRange(choices.Where(c => c.ShipTypeId == typeId));
            var selected = CorpFitChoice.None;
            if (required.TryGetValue(typeId, out var req))
            {
                selected = list.FirstOrDefault(c => c.Source == req.Source && c.FitId == req.FitId)
                        ?? new CorpFitChoice(req.Source, req.FitId, req.OwnerId, req.OwnerName, req.Name, typeId) { IsCopy = true };
                if (!list.Contains(selected)) list.Add(selected);
            }

            var known = mine.Count(i => i.FitCheck is { Known: true });
            var bad   = mine.Count(i => i.FitCheck is { Known: true, HasProblems: true });
            var status = req is null              ? string.Format(CorpText.CsConfigStatusNoFit, mine.Count)
                       : mine.Count > 0 && known == 0 ? string.Format(CorpText.CsConfigStatusUnknown, mine.Count)
                       : bad > 0                  ? string.Format(CorpText.CsConfigStatus, mine.Count, bad)
                       :                            string.Format(CorpText.CsConfigStatusAllMatch, mine.Count);
            var name = mine.FirstOrDefault()?.TypeName ?? names.GetValueOrDefault(typeId) ?? $"#{typeId}";
            rows.Add(new CorpFitConfigRowVm(typeId, name, status, list, selected, SaveFitAsync));
        }

        ConfigRows.Clear();
        foreach (var r in rows.OrderBy(r => r.TypeName, StringComparer.CurrentCultureIgnoreCase)) ConfigRows.Add(r);
        HasConfigRows = ConfigRows.Count > 0;
    }

    /// <summary>Sets a structure type's fit, then reads everything again so the checks follow.</summary>
    private async Task SaveFitAsync(CorpFitConfigRowVm row, CorpFitChoice choice)
    {
        if (SelectedCorp is not { } corp) return;
        try
        {
            await Task.Run(() => CorpStructureFits.SetAsync(_dbFactory, corp.Id, row.TypeId,
                choice == CorpFitChoice.None ? null : choice));
            await LoadAsync();
            Status = string.Format(CorpText.CsFitSaved, row.TypeName);
        }
        catch (Exception ex)
        {
            Status = string.Format(CorpText.CsFitSaveFailed, ex.Message);
            _errorLogger?.Log("CorpStructuresViewModel", "SaveFitAsync", ex);
        }
    }

    private void ApplyFilter()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _all.Where(r =>
                (r.IsTower ? ShowTowers : r.IsSkyhook ? ShowSkyhooks : ShowUpwell)
                && words.All(w =>
                       r.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                    || r.TypeName.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                    || r.Location.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                    || r.Region.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();

        var keep = Selected;
        Rows.Clear();
        foreach (var r in shown) Rows.Add(r);
        HasRows   = Rows.Count > 0;
        EmptyText = _all.Count == 0 ? CorpText.CsNoneAnchored : CorpText.CsNoneMatch;
        if (keep is not null && Rows.Contains(keep)) Selected = keep;
    }

    // ── Detail panel ─────────────────────────────────────────────────────────

    internal static string FitCheckAgainst(CorpFitCheck fc) => fc.FromCopy
        ? string.Format(CorpText.CsCheckAgainstCopy, fc.FitName, fc.FitSource, PiFormat.When(fc.CopiedAt))
        : string.Format(CorpText.CsCheckAgainst, fc.FitName, fc.FitSource);

    /// <summary>A structure against its type's fit, line by line — the detail panel's Fit check
    /// section, and the list's info icon tooltip.</summary>
    internal static List<CorpStructureField> FitCheckFields(CorpFitCheck fc)
    {
        string Line(CorpFitShortfall m) =>
            $"{(m.Count > 1 ? string.Format(CorpText.CsCountOf, m.Count, m.Name) : m.Name)} ({BandTitle(m.Band)})";
        string Stock(CorpStockShortfall x) => string.Format(CorpText.CsCheckStock, x.Name,
            x.Have.ToString("N0", CultureInfo.CurrentCulture), x.Want.ToString("N0", CultureInfo.CurrentCulture),
            x.Want > 0 ? (100 * x.Have / x.Want).ToString(CultureInfo.CurrentCulture) : "0");

        var lines = new List<CorpStructureField>();
        if (!fc.Known) lines.Add(new(CorpText.CsCheckLabel, CorpText.CsCheckUnknownLong));
        else if (!fc.HasProblems) lines.Add(new(CorpText.CsCheckLabel, CorpText.CsCheckMatches));
        foreach (var m in fc.Missing)     lines.Add(new(CorpText.CsCheckMissingLabel, Line(m), FuelUrgency.Critical));
        foreach (var m in fc.Wrong)       lines.Add(new(CorpText.CsCheckWrongLabel,   Line(m), FuelUrgency.Critical));
        foreach (var m in fc.Instead)     lines.Add(new(CorpText.CsCheckInsteadLabel, Line(m)));
        foreach (var low in fc.AmmoLow)     lines.Add(new(CorpText.CsCheckAmmoLabel,     Stock(low), FuelUrgency.Soon));
        foreach (var low in fc.FightersLow) lines.Add(new(CorpText.CsCheckFightersLabel, Stock(low), FuelUrgency.Soon));
        return lines;
    }

    /// <summary>ISK as the other tools abbreviate it: 11.2M, 1.27B, 840k.</summary>
    internal static string Isk(double v) => v switch
    {
        >= 1_000_000_000_000 => $"{v / 1_000_000_000_000:N2}T",
        >= 1_000_000_000     => $"{v / 1_000_000_000:N2}B",
        >= 1_000_000         => $"{v / 1_000_000:N1}M",
        >= 1_000             => $"{v / 1_000:N0}k",
        _                    => v.ToString("N0"),
    };

    private void BuildDetail()
    {
        StatusFields.Clear(); LocationFields.Clear(); FuelRows.Clear(); Services.Clear();
        FitGroups.Clear(); MoonFields.Clear(); MoonBay.Clear(); TowerFields.Clear(); FitCheckLines.Clear();

        if (Selected is not { Info: var i })
        {
            HasDetail = false;
            return;
        }

        // The fit check: what the structure lacks against its type's fit, or has in its place.
        if (i.FitCheck is { } fc)
        {
            FitCheckNote = FitCheckAgainst(fc);
            foreach (var line in FitCheckFields(fc)) FitCheckLines.Add(line);
        }
        HasFitCheck = i.FitCheck is not null;

        var now = DateTimeOffset.UtcNow;
        DetailTitle    = i.Name;
        DetailSubtitle = i.GroupName.Length > 0 ? $"{i.TypeName} · {i.GroupName}" : i.TypeName;
        DetailRender   = null;
        _ = LoadRenderAsync(i.TypeId);

        // Location.
        LocationFields.Add(new(CorpText.CsSystem, i.Security is { } sec ? $"{i.SystemName} ({SecurityColors.Text(sec)})" : i.SystemName));
        if (i.RegionName.Length > 0) LocationFields.Add(new(CorpText.CsRegion, i.RegionName));
        if (i.Place.Length > 0)
            LocationFields.Add(new(i.Kind switch
            {
                CorpStructureKind.Tower   => CorpText.CsMoon,
                CorpStructureKind.Skyhook => CorpText.DenColPlanet,
                _                         => CorpText.CsNearest,
            }, i.Place));

        // Status.
        StatusFields.Add(new(CorpText.CsState, StateLabel(i.State)));
        var left = i.RunsOut is { } ro ? ro - now : (TimeSpan?)null;
        var urgency = UrgencyOf(left, i);
        if (i.RunsOut is { } runsOut)
            StatusFields.Add(new(CorpText.CsFuelRunsOut,
                left > TimeSpan.Zero
                    ? string.Format(CorpText.CsWhenIn, PiFormat.When(runsOut), PiFormat.Duration(left!.Value))
                    : PiFormat.When(runsOut),
                urgency));
        else if (i.Kind == CorpStructureKind.Upwell && !i.IsPowered && i.NeedsFuel)
            StatusFields.Add(new(CorpText.CsFuelRunsOut, CorpText.CsLowPowerLong, FuelUrgency.Out));
        if (i.BlocksPerHour is { } bph)
            StatusFields.Add(new(CorpText.CsBlocksPerDay,
                string.Format(CorpText.CsPerDayPerHour, (bph * 24).ToString("N0"), bph.ToString("0.#"))));
        if (i.FuelIskPerDay is { } isk)
            StatusFields.Add(new(CorpText.CsFuelIskPerDay,
                string.Format(CorpText.CsFuelIskValue, Isk(isk))));
        if (i.StateTimerEnd is { } te)
            StatusFields.Add(new(CorpText.CsStateTimer,
                i.StateTimerStart is { } ts
                    ? $"{PiFormat.When(ts)} → {PiFormat.When(te)} ({PiFormat.Relative(te, now)})"
                    : $"{PiFormat.When(te)} ({PiFormat.Relative(te, now)})"));
        if (i.ReinforceHour is { } rh)
            StatusFields.Add(new(CorpText.CsReinforceHour, string.Format(CorpText.CsHourUtc, rh)));
        if (i.NextReinforceHour is { } nrh && i.NextReinforceApply is { } apply)
            StatusFields.Add(new(CorpText.CsNextReinforce,
                string.Format(CorpText.CsNextReinforceValue, nrh, PiFormat.When(apply))));
        if (i.UnanchorsAt is { } ua)
            StatusFields.Add(new(CorpText.CsUnanchors, $"{PiFormat.When(ua)} ({PiFormat.Relative(ua, now)})", FuelUrgency.Soon));
        if (i.OnlinedSince is { } os)
            StatusFields.Add(new(CorpText.CsOnlineSince, PiFormat.When(os)));
        if (i.ReinforcedUntil is { } ru)
            StatusFields.Add(new(CorpText.CsReinforcedUntil, $"{PiFormat.When(ru)} ({PiFormat.Relative(ru, now)})", FuelUrgency.Critical));
        if (i.StrontiumHours is { } sh)
            StatusFields.Add(new(CorpText.CsStrontium, string.Format(CorpText.CsStrontiumHours, sh.ToString("0.#"))));
        if (i.ProfileId is { } pid)
            StatusFields.Add(new(CorpText.CsProfile, pid.ToString(CultureInfo.CurrentCulture)));

        // Fuel.
        foreach (var f in i.Fuel)
        {
            var perHour = f.PerHour;
            var fuelLeft = f.RunsOut is { } fro ? fro - now : (TimeSpan?)null;
            string source;
            if (f.ExpectedPerHour is not null && f.MeasuredPerHour is { } m)
            {
                source = string.Format(CorpText.CsSourceBoth, (m * 24).ToString("N0"), PiFormat.Duration(f.MeasuredOver));
            }
            else if (f.ExpectedPerHour is not null) source = CorpText.CsSourceExpected;
            else if (f.MeasuredPerHour is not null)
            {
                source = string.Format(CorpText.CsSourceMeasured, PiFormat.Duration(f.MeasuredOver));
            }
            else source = CorpText.CsSourceMeasuring;

            // Expected and the game's fuel expiry disagreeing by more than an hour's rounding:
            // say so, rather than show a burn the run-out date contradicts.
            if (f.ExpectedPerHour is { } ex && f.ImpliedPerHour is { } im && Math.Abs(ex - im) > Math.Max(0.6, ex * 0.05))
                source = string.Format(CorpText.CsSourceImplied, source, (im * 24).ToString("N0"));

            FuelRows.Add(new CorpFuelRowVm(
                f.Name,
                f.OnHandNow(now).ToString("N0", CultureInfo.CurrentCulture),
                perHour is { } p ? (p * 24).ToString("N0", CultureInfo.CurrentCulture) : "—",
                fuelLeft is { } fl ? (fl > TimeSpan.Zero ? PiFormat.Duration(fl) : CorpText.CsOutOfFuel)
                                   : perHour is 0 ? CorpText.CsNotBurning : "—",
                f.RunsOut is { } at ? PiFormat.When(at) : "",
                source,
                perHour is > 0 ? UrgencyOf(fuelLeft, i) : FuelUrgency.None,
                f.TypeId));
        }
        HasFuel  = FuelRows.Count > 0;
        FuelNote = !HasFuel
            ? (i.Kind == CorpStructureKind.Tower ? CorpText.CsNoFuelTower : CorpText.CsNoFuelUpwell)
            : i.Kind == CorpStructureKind.Tower ? CorpText.CsFuelNoteTower : CorpText.CsFuelNoteUpwell;

        // Services.
        foreach (var s in i.Services)
            Services.Add(new CorpServiceVm(s.Name, ServiceStateLabel(s.State), s.IsOnline));
        HasServices = Services.Count > 0;

        // Fit.
        foreach (var g in i.Fit.GroupBy(f => f.Band))
            FitGroups.Add(new CorpFitGroupVm(BandTitle(g.Key),
                [.. g.Select(f => new CorpFitItemVm(f.Name,
                    f.Quantity > 1 ? f.Quantity.ToString("N0", CultureInfo.CurrentCulture) : "",
                    f.ByHand, f.TypeId))]));
        HasFit  = FitGroups.Count > 0;
        FitNote = HasFit
            ? (i.Fit.Any(f => f.ByHand) ? CorpText.CsFitByHandNote : "")
            : i.Kind == CorpStructureKind.Tower
                ? (i.FitKnown ? CorpText.CsTowerNoModules : CorpText.CsTowerModulesUnknown)
                : (i.FitKnown ? CorpText.CsNothingFitted : CorpText.CsFitUnknown);
        this.RaisePropertyChanged(nameof(HasFitNote));

        // Moon drilling.
        if (i.Extraction is { } x)
        {
            MoonFields.Add(new(CorpText.CsExtractionStarted, PiFormat.When(x.ExtractionStartTime)));
            MoonFields.Add(new(CorpText.CsChunkArrives, $"{PiFormat.When(x.ChunkArrivalTime)} ({PiFormat.Relative(x.ChunkArrivalTime, now)})"));
            MoonFields.Add(new(CorpText.CsNaturalDecay, $"{PiFormat.When(x.NaturalDecayTime)} ({PiFormat.Relative(x.NaturalDecayTime, now)})"));
        }
        var c = CultureInfo.CurrentCulture;
        static string M3(double v) => v.ToString(v < 100 ? "N1" : "N0", CultureInfo.CurrentCulture);
        foreach (var m in i.MoonBay)
            MoonBay.Add(new CorpBayRowVm(m.Name, m.Quantity.ToString("N0", c), M3(m.Volume),
                m.Value is { } v ? Isk(v) : "—", m.TypeId));
        var bayVolume = i.MoonBay.Sum(m => m.Volume);
        if (i.MoonBay.Count > 1)
            MoonBay.Add(new CorpBayRowVm(CorpText.CsBayTotal, "", M3(bayVolume),
                Isk(i.MoonBay.Sum(m => m.Value ?? 0)), 0, isTotal: true));
        HasMoonBay = MoonBay.Count > 0;
        // Only where the corporation's assets are read: otherwise an empty list is not an empty bay.
        if (i.MoonBayCapacity is > 0 and var cap && i.FitKnown)
        {
            MoonBayPct  = Math.Min(100, 100 * bayVolume / cap);
            MoonBayFill = string.Format(CorpText.CsBayFill, M3(bayVolume), cap.ToString("N0", c), MoonBayPct.ToString("0.#", c));
        }
        else MoonBayFill = "";
        this.RaisePropertyChanged(nameof(HasMoonBayFill));
        HasMoon = MoonFields.Count > 0 || MoonBay.Count > 0 || HasMoonBayFill;

        // Skyhook: producing or not, workforce, theft window, reagents, and the user's own dens on it.
        SkyhookFields.Clear(); Reagents.Clear(); SkyhookDens.Clear();
        if (i.Kind == CorpStructureKind.Skyhook)
        {
            SkyhookFields.Add(new(CorpText.CsSkyActive, i.ReadAt is null ? CorpText.CsSkyNotRead : YesNo(i.SkyhookActive),
                i.ReadAt is not null && !i.SkyhookActive ? FuelUrgency.Soon : FuelUrgency.None));
            if (i.Workforce is { } wf) SkyhookFields.Add(new(CorpText.CsSkyWorkforce, wf.ToString("N0", c)));
            if (i.TheftStart is { } ts && i.TheftEnd is { } tend)
            {
                var open = ts <= now && now < tend;
                SkyhookFields.Add(new(CorpText.CsSkyTheft,
                    open ? string.Format(CorpText.CsSkyTheftOpen, PiFormat.When(tend), PiFormat.Relative(tend, now))
                         : string.Format(CorpText.CsSkyTheftWindow, PiFormat.When(ts), PiFormat.When(tend), PiFormat.Relative(ts, now)),
                    open ? FuelUrgency.Critical : FuelUrgency.None));
            }
            if (i.ReadAt is { } ra) SkyhookFields.Add(new(CorpText.CsSkyRead, PiFormat.When(ra)));
            foreach (var r in i.Reagents)
                Reagents.Add(new CorpBayRowVm(r.Name, r.Secured.ToString("N0", c), r.Unsecured.ToString("N0", c),
                    PiFormat.When(r.LastCycle), r.TypeId));
            foreach (var den in i.Dens)
                SkyhookDens.Add(new(den.Owner, DenLine(den.State, den.AnarchyLevel, den.AnarchyAmount,
                        den.DevelopmentLevel, den.DevelopmentAmount, den.Infomorphs),
                    den.AnarchyLevel >= i.AnarchyAlertLevel ? FuelUrgency.Critical : FuelUrgency.None));
        }
        IsSkyhookDetail = i.Kind == CorpStructureKind.Skyhook;
        HasReagents     = Reagents.Count > 0;

        // Tower settings.
        if (i.Settings is { } d)
        {
            TowerFields.Add(new(CorpText.CsTowerCorpAccess,     YesNo(d.AllowCorporationMembers)));
            TowerFields.Add(new(CorpText.CsTowerAllianceAccess, YesNo(d.AllowAllianceMembers)));
            TowerFields.Add(new(CorpText.CsTowerUseAllianceStandings, YesNo(d.UseAllianceStandings)));
            if (d.AttackStandingThreshold is { } st)
                TowerFields.Add(new(CorpText.CsTowerAttackStanding, st.ToString("0.0#", CultureInfo.CurrentCulture)));
            if (d.AttackSecurityThreshold is { } ss)
                TowerFields.Add(new(CorpText.CsTowerAttackSecurity, ss.ToString("0.0#", CultureInfo.CurrentCulture)));
            TowerFields.Add(new(CorpText.CsTowerAttackAtWar,     YesNo(d.AttackIfAtWar)));
            TowerFields.Add(new(CorpText.CsTowerAttackDropping,  YesNo(d.AttackIfSecurityDropping)));
            TowerFields.Add(new(CorpText.CsTowerRoleAnchor,      RoleLabel(d.Anchor)));
            TowerFields.Add(new(CorpText.CsTowerRoleUnanchor,    RoleLabel(d.Unanchor)));
            TowerFields.Add(new(CorpText.CsTowerRoleOnline,      RoleLabel(d.Online)));
            TowerFields.Add(new(CorpText.CsTowerRoleOffline,     RoleLabel(d.Offline)));
            TowerFields.Add(new(CorpText.CsTowerRoleFuelView,    RoleLabel(d.FuelBayView)));
            TowerFields.Add(new(CorpText.CsTowerRoleFuelTake,    RoleLabel(d.FuelBayTake)));
            TowerFields.Add(new(CorpText.CsTowerSettingsRead,    PiFormat.When(d.PulledAt)));
        }
        HasTower  = i.Kind == CorpStructureKind.Tower;
        HasTowerSettings = TowerFields.Count > 0;
        HasDetail = true;
    }

    private async Task LoadRenderAsync(int typeId)
    {
        var bmp = await EveImageCache.GetAsync($"https://images.evetech.net/types/{typeId}/render?size=128");
        Dispatcher.UIThread.Post(() => { if (Selected?.Info.TypeId == typeId) DetailRender = bmp; });
    }

    // ── Labels ───────────────────────────────────────────────────────────────

    /// <summary>The list's fit column: what is wrong in a few words, and how bad. A module
    /// missing or wrong is a fault; ammo or fighters below the share is running low.</summary>
    public static (string Text, FuelUrgency Urgency) FitSummary(CorpFitCheck? c)
    {
        if (c is null)   return ("", FuelUrgency.None);
        if (!c.Known)    return (CorpText.CsCheckUnknown, FuelUrgency.None);
        if (!c.HasProblems) return (CorpText.CsCheckOk, FuelUrgency.Fine);
        var parts = new List<string>();
        if (c.Missing.Count > 0) parts.Add(string.Format(CorpText.CsCheckMissing, c.Missing.Sum(m => m.Count)));
        if (c.Wrong.Count > 0)   parts.Add(string.Format(CorpText.CsCheckWrong, c.Wrong.Sum(m => m.Count)));
        if (c.AmmoLow.Count > 0)     parts.Add(CorpText.CsCheckAmmo);
        if (c.FightersLow.Count > 0) parts.Add(CorpText.CsCheckFighters);
        return (string.Join(" · ", parts),
                c.Missing.Count + c.Wrong.Count > 0 ? FuelUrgency.Critical : FuelUrgency.Soon);
    }

    internal static FuelUrgency UrgencyOf(TimeSpan? left, CorpStructureInfo i)
    {
        if (left is null)
            return i.Kind == CorpStructureKind.Upwell && !i.IsPowered && i.NeedsFuel ? FuelUrgency.Out : FuelUrgency.None;
        if (left <= TimeSpan.Zero)                       return FuelUrgency.Out;
        if (left.Value.TotalDays < CriticalDays)         return FuelUrgency.Critical;
        if (left.Value.TotalDays < SoonDays)             return FuelUrgency.Soon;
        return FuelUrgency.Fine;
    }

    /// <summary>ESI's state words, for Upwell structures and towers alike.</summary>
    internal static string StateLabel(string state) => state switch
    {
        "shield_vulnerable"    => CorpText.CsStateShieldVulnerable,
        "armor_vulnerable"     => CorpText.CsStateArmorVulnerable,
        "hull_vulnerable"      => CorpText.CsStateHullVulnerable,
        "armor_reinforce"      => CorpText.CsStateArmorReinforce,
        "hull_reinforce"       => CorpText.CsStateHullReinforce,
        "anchoring"            => CorpText.CsStateAnchoring,
        "anchor_vulnerable"    => CorpText.CsStateAnchorVulnerable,
        "deploy_vulnerable"    => CorpText.CsStateDeployVulnerable,
        "fitting_invulnerable" => CorpText.CsStateFitting,
        "onlining_vulnerable"  => CorpText.CsStateOnlining,
        "online_deprecated"    => CorpText.CsStateOnline,
        "unanchored"           => CorpText.CsStateUnanchored,
        "online"               => CorpText.CsStateOnline,
        "offline"              => CorpText.CsStateOffline,
        "onlining"             => CorpText.CsStateOnlining,
        "reinforced"           => CorpText.CsStateReinforced,
        "unanchoring"          => CorpText.CsStateUnanchoring,
        _                      => state,
    };

    private static string ServiceStateLabel(string state) => state switch
    {
        "online"  => CorpText.CsServiceOnline,
        "offline" => CorpText.CsServiceOffline,
        "cleanup" => CorpText.CsServiceCleanup,
        _         => state,
    };

    private static string RoleLabel(string role) => role switch
    {
        "alliance_member"                  => CorpText.CsRoleAllianceMember,
        "corporation_member"               => CorpText.CsRoleCorporationMember,
        "config_starbase_equipment_role"   => CorpText.CsRoleStarbaseConfig,
        "starbase_fuel_technician_role"    => CorpText.CsRoleFuelTechnician,
        _                                  => role,
    };

    private static string BandTitle(string band) => band switch
    {
        "HiSlot"          => CorpText.CsBandHigh,
        "MedSlot"         => CorpText.CsBandMid,
        "LoSlot"          => CorpText.CsBandLow,
        "RigSlot"         => CorpText.CsBandRig,
        "ServiceSlot"     => CorpText.CsBandService,
        "QuantumCoreRoom" => CorpText.CsBandCore,
        "FighterTube"     => CorpText.CsBandFighterTubes,
        "FighterBay"      => CorpText.CsBandFighterBay,
        "Cargo"           => CorpText.CsBandAmmoHold,
        _                => band,   // a tower's modules come grouped by their group's name
    };

    private static string YesNo(bool b) => b ? CorpText.CsYes : CorpText.CsNo;

    /// <summary>"Running · anarchy 3 (72) · development 2 (40) · 1,250 infomorphs" — a den in a line,
    /// here and on Mercenary Dens.</summary>
    internal static string DenLine(string state, int anarchyLevel, long anarchy, int devLevel, long dev, long infomorphs) =>
        string.Format(CorpText.CsDenLine, DenStateLabel(state), Level(anarchyLevel), anarchy, Level(devLevel), dev,
            infomorphs.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>A den's anarchy or development level, 0–4; "?" when ESI gave none.</summary>
    internal static string Level(int level) => level >= 0 ? level.ToString(CultureInfo.CurrentCulture) : "?";

    internal static string DenStateLabel(string state) => state switch
    {
        "Running"  => CorpText.CsDenRunning,
        "Paused"   => CorpText.CsDenPaused,
        "Disabled" => CorpText.CsDenDisabled,
        ""         => CorpText.CsSkyNotRead,
        _          => state,
    };
}
