using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using Avalonia.Media.Imaging;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.Services.Pi;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One den in the list.</summary>
public sealed class MercenaryDenRowVm : ReactiveObject
{
    public MercenaryDenInfo Info { get; }

    public MercenaryDenRowVm(MercenaryDenInfo info, int anarchyAlertLevel, DateTimeOffset now)
    {
        Info = info;
        if (info.TypeId > 0) _ = ItemIcons.LoadAsync(info.TypeId, b => Icon = b);
        Location      = info.Security is { } sec ? $"{info.SystemName} {SecurityColors.Text(sec)}" : info.SystemName;
        StateText     = CorpStructuresViewModel.DenStateLabel(info.State);
        Infomorphs    = info.ReadAt is null ? "" : info.Infomorphs.ToString("N0", CultureInfo.CurrentCulture);
        PerHour       = info.InfomorphsPerHour is { } h ? h.ToString(h < 10 ? "N1" : "N0", CultureInfo.CurrentCulture) : "";
        Development   = info.ReadAt is null ? "" : LevelText(info.DevelopmentLevel, info.DevelopmentAmount);
        Anarchy       = info.ReadAt is null ? "" : LevelText(info.AnarchyLevel, info.AnarchyAmount);
        AnarchyHigh   = info.AnarchyLevel >= anarchyAlertLevel;
        Reinforced    = info.ReinforcedUntil is { } r && r > now ? $"{PiFormat.When(r)} ({PiFormat.Relative(r, now)})" : "";
        var open      = info.Operations.Count(o => o.State is "Available" or "Started");
        Operations    = open > 0 ? open.ToString(CultureInfo.CurrentCulture) : "";
    }

    /// <summary>"3 · 72" — the level, then the amount out of 100.</summary>
    internal static string LevelText(int level, long amount) =>
        string.Format(CorpText.DenLevelValue, CorpStructuresViewModel.Level(level), amount);

    public string Owner        => Info.Owner;
    public string Planet       => Info.Planet;
    public string Location     { get; }
    public string Region       => Info.RegionName;
    public string SkyhookCorp  => Info.SkyhookCorporation;
    public string StateText    { get; }
    public string Infomorphs   { get; }
    public double InfomorphsSort => Info.Infomorphs;
    public string PerHour      { get; }
    public double PerHourSort  => Info.InfomorphsPerHour ?? -1;
    public string Development  { get; }
    public long   DevelopmentSort => Info.DevelopmentLevel * 1000L + Info.DevelopmentAmount;
    public string Anarchy      { get; }
    public long   AnarchySort  => Info.AnarchyLevel * 1000L + Info.AnarchyAmount;
    /// <summary>At or above the anarchy level the alert is raised at (Settings → Alerts).</summary>
    public bool   AnarchyHigh  { get; }
    public string Reinforced   { get; }
    public string Operations   { get; }

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; private set => this.RaiseAndSetIfChanged(ref _icon, value); }
}

/// <summary>An operation at the chosen den: its state and when it expires.</summary>
public sealed record MercenaryOperationRowVm(string State, string Expires, string Seen, bool IsOpen);

/// <summary>
/// Mercenary Dens: every den the user's characters own, with where it is, its state, infomorphs
/// and how fast they build, development, anarchy, and the tactical operations it offers.
/// </summary>
public class MercenaryDensViewModel : ReactiveObject, IPeriodicRefresh
{
    /// <summary>Set the first time this tool is opened; until then nothing is read.</summary>
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

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger? _errorLogger;
    private int _loadSeq;
    private long? _showDen;

    public ObservableCollection<MercenaryDenRowVm> Rows { get; } = [];
    public ObservableCollection<CorpStructureField> DetailFields { get; } = [];
    public ObservableCollection<MercenaryOperationRowVm> OperationRows { get; } = [];

    private MercenaryDenRowVm? _selected;
    public MercenaryDenRowVm? Selected
    {
        get => _selected;
        set { this.RaiseAndSetIfChanged(ref _selected, value); BuildDetail(); }
    }

    private string _summary = "";
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _hasRows;
    public bool HasRows { get => _hasRows; private set => this.RaiseAndSetIfChanged(ref _hasRows, value); }

    private bool _hasDetail;
    public bool HasDetail { get => _hasDetail; private set => this.RaiseAndSetIfChanged(ref _hasDetail, value); }

    private bool _hasOperations;
    public bool HasOperations { get => _hasOperations; private set => this.RaiseAndSetIfChanged(ref _hasOperations, value); }

    private string _detailTitle = "", _detailSubtitle = "";
    public string DetailTitle    { get => _detailTitle;    private set => this.RaiseAndSetIfChanged(ref _detailTitle, value); }
    public string DetailSubtitle { get => _detailSubtitle; private set => this.RaiseAndSetIfChanged(ref _detailSubtitle, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public MercenaryDensViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger? errorLogger = null)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        RefreshCommand.ThrownExceptions.Subscribe(_ => { });
    }

    /// <summary>Opens on <paramref name="denId"/>, for an Overview alert.</summary>
    public void Show(long? denId)
    {
        _showDen = denId;
        if (_opened) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var seq = ++_loadSeq;
        Status = CorpText.CsLoading;
        try
        {
            var (dens, level) = await Task.Run(async () =>
            {
                var list = await MercenaryDens.LoadAsync(_dbFactory);
                await using var db = await _dbFactory.CreateDbContextAsync();
                var lv = await db.AlertSettings.AsNoTracking().Where(a => a.Id == 1)
                    .Select(a => (int?)a.DenAnarchyLevel).FirstOrDefaultAsync() ?? 3;
                return (list, lv);
            });
            if (seq != _loadSeq) return;

            var now  = DateTimeOffset.UtcNow;
            var keep = _showDen ?? Selected?.Info.DenId;
            _showDen = null;
            Rows.Clear();
            foreach (var d in dens.OrderByDescending(d => d.AnarchyLevel).ThenByDescending(d => d.AnarchyAmount)
                                  .ThenBy(d => d.Owner, StringComparer.CurrentCultureIgnoreCase))
                Rows.Add(new MercenaryDenRowVm(d, level, now));
            HasRows = Rows.Count > 0;

            var high = Rows.Count(r => r.AnarchyHigh);
            var ops  = dens.Sum(d => d.Operations.Count(o => o.State is "Available" or "Started"));
            Summary  = Plurals.Format(CorpText.ResourceManager, nameof(CorpText.DenSummaryOther), Rows.Count)
                     + (high > 0 ? " · " + string.Format(CorpText.DenSummaryHigh, high, level) : "")
                     + (ops > 0 ? " · " + Plurals.Format(CorpText.ResourceManager, nameof(CorpText.DenSummaryOpsOther), ops) : "");
            Status   = string.Format(CorpText.CsLoadedAt, DateTime.Now.ToString("t", CultureInfo.CurrentCulture));
            Selected = Rows.FirstOrDefault(r => r.Info.DenId == keep) ?? Rows.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = string.Format(CorpText.CsLoadFailed, ex.Message);
            _errorLogger?.Log("MercenaryDensViewModel", "LoadAsync", ex);
        }
    }

    private void BuildDetail()
    {
        DetailFields.Clear(); OperationRows.Clear();
        if (Selected is not { Info: var d })
        {
            HasDetail = false; HasOperations = false;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        DetailTitle    = d.Planet;
        DetailSubtitle = d.TypeName.Length > 0 ? $"{d.TypeName} · {d.Owner}" : d.Owner;

        DetailFields.Add(new(CorpText.CsState, Selected.StateText,
            d.State is "Paused" or "Disabled" ? FuelUrgency.Soon : FuelUrgency.None));
        if (d.ReadAt is not null)
        {
            DetailFields.Add(new(CorpText.DenInfomorphs, d.InfomorphsPerHour is { } h
                ? string.Format(CorpText.DenInfomorphsRate, d.Infomorphs.ToString("N0", CultureInfo.CurrentCulture),
                    h.ToString(h < 10 ? "N1" : "N0", CultureInfo.CurrentCulture))
                : d.Infomorphs.ToString("N0", CultureInfo.CurrentCulture)));
            DetailFields.Add(new(CorpText.DenDevelopment, Selected.Development));
            DetailFields.Add(new(CorpText.DenAnarchy, Selected.Anarchy, Selected.AnarchyHigh ? FuelUrgency.Critical : FuelUrgency.None));
        }
        if (d.ReinforcedUntil is { } r && r > now)
            DetailFields.Add(new(CorpText.CsReinforcedUntil, Selected.Reinforced, FuelUrgency.Critical));
        DetailFields.Add(new(CorpText.CsSystem, Selected.Location));
        if (d.RegionName.Length > 0) DetailFields.Add(new(CorpText.CsRegion, d.RegionName));
        if (d.SkyhookCorporation.Length > 0) DetailFields.Add(new(CorpText.DenSkyhookOwner, d.SkyhookCorporation));
        DetailFields.Add(new(CorpText.CsSkyRead, d.ReadAt is { } at ? PiFormat.When(at) : CorpText.CsSkyNotRead));

        foreach (var o in d.Operations)
            OperationRows.Add(new MercenaryOperationRowVm(MtoStateLabel(o.State),
                o.Expires > now ? $"{PiFormat.When(o.Expires)} ({PiFormat.Relative(o.Expires, now)})" : PiFormat.When(o.Expires),
                PiFormat.When(o.SeenAt), o.State is "Available" or "Started"));
        HasOperations = OperationRows.Count > 0;
        HasDetail     = true;
    }

    internal static string MtoStateLabel(string state) => state switch
    {
        "Available" => CorpText.MtoAvailable,
        "Started"   => CorpText.MtoStarted,
        "Completed" => CorpText.MtoCompleted,
        "Expired"   => CorpText.MtoExpired,
        "Removed"   => CorpText.MtoRemoved,
        _           => state,
    };
}
