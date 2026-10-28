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

public sealed record CorpServiceVm(string Name, string StateText, bool IsOnline);

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
        var online   = info.Services.Count(s => s.IsOnline);
        ServicesText = info.Services.Count > 0 ? $"{online} / {info.Services.Count}" : "";
        StateText    = CorpStructuresViewModel.StateLabel(info.State);
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
    public FuelUrgency Urgency { get; }
    public bool IsCritical     => Urgency is FuelUrgency.Critical or FuelUrgency.Out;
    public bool IsSoon         => Urgency == FuelUrgency.Soon;
    public bool IsTower        => Info.Kind == CorpStructureKind.Tower;

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
    public ObservableCollection<CorpFitItemVm>      MoonBay        { get; } = [];
    public ObservableCollection<CorpStructureField> TowerFields    { get; } = [];

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

            var upwell = _all.Count(r => !r.IsTower);
            var towers = _all.Count(r => r.IsTower);
            var low    = _all.Count(r => r.Urgency is FuelUrgency.Soon or FuelUrgency.Critical);
            var outs   = _all.Count(r => r.Urgency == FuelUrgency.Out);
            Summary = string.Format(CorpText.CsSummary, upwell, towers, low, outs);
            Status  = string.Format(CorpText.CsLoadedAt, DateTime.Now.ToString("t", CultureInfo.CurrentCulture));

            var keep = Selected?.Info.Id;
            ApplyFilter();
            Selected = Rows.FirstOrDefault(r => r.Info.Id == keep) ?? Rows.FirstOrDefault();
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

    private void ApplyFilter()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _all.Where(r =>
                (r.IsTower ? ShowTowers : ShowUpwell)
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

    private void BuildDetail()
    {
        StatusFields.Clear(); LocationFields.Clear(); FuelRows.Clear(); Services.Clear();
        FitGroups.Clear(); MoonFields.Clear(); MoonBay.Clear(); TowerFields.Clear();

        if (Selected is not { Info: var i })
        {
            HasDetail = false;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        DetailTitle    = i.Name;
        DetailSubtitle = i.GroupName.Length > 0 ? $"{i.TypeName} · {i.GroupName}" : i.TypeName;
        DetailRender   = null;
        _ = LoadRenderAsync(i.TypeId);

        // Location.
        LocationFields.Add(new(CorpText.CsSystem, i.Security is { } sec ? $"{i.SystemName} ({SecurityColors.Text(sec)})" : i.SystemName));
        if (i.RegionName.Length > 0) LocationFields.Add(new(CorpText.CsRegion, i.RegionName));
        if (i.Place.Length > 0)
            LocationFields.Add(new(i.Kind == CorpStructureKind.Tower ? CorpText.CsMoon : CorpText.CsNearest, i.Place));

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
        foreach (var m in i.MoonBay)
            MoonBay.Add(new CorpFitItemVm(m.Name, m.Quantity.ToString("N0", CultureInfo.CurrentCulture), false, m.TypeId));
        HasMoon = MoonFields.Count > 0 || MoonBay.Count > 0;

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
        _                 => band,   // a tower's modules come grouped by their group's name
    };

    private static string YesNo(bool b) => b ? CorpText.CsYes : CorpText.CsNo;
}
