using System.Globalization;
using System.Reactive;
using Avalonia.Media.Imaging;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.Services.Battle;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One fight found. Opening it starts a battle with its systems and times filled in.</summary>
public sealed class BattleFightVm : ReactiveObject
{
    private readonly FoundFight _fight;
    private readonly IReadOnlyDictionary<int, string> _systems;
    private readonly BattleNames _names;

    public BattleFightVm(FoundFight fight, IReadOnlyDictionary<int, string> systems, BattleNames names, Action<FoundFight> open)
    {
        _fight   = fight;
        _systems = systems;
        _names   = names;
        OpenCommand = ReactiveCommand.Create(() => open(fight));
    }

    public FoundFight Fight => _fight;
    public DateTimeOffset StartRaw => _fight.First;
    public string Started => _fight.First.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public TimeSpan DurationRaw => _fight.Last - _fight.First;
    public string Duration => _fight.StillGoing
        ? BattleText.StillGoing
        : $"{(int)DurationRaw.TotalHours}:{DurationRaw.Minutes:00}";

    public int SystemCount => _fight.Windows.Select(w => w.SystemId).Distinct().Count();

    /// <summary>Its systems, busiest first; a long list stops at three and says how many more.</summary>
    public string Systems
    {
        get
        {
            var ordered = _fight.Runs.GroupBy(r => r.SystemId)
                .OrderByDescending(g => g.Sum(r => r.KillIds.Count))
                .Select(g => _systems.GetValueOrDefault(g.Key, g.Key.ToString(CultureInfo.InvariantCulture))).ToList();
            return string.Join(", ", ordered.Take(3)) + (ordered.Count > 3 ? " +" + (ordered.Count - 3) : "");
        }
    }

    public int    Kills   => _fight.KillIds.Count;
    public int    Pilots  => _fight.Pilots;
    public double IskLost => _fight.IskLost;
    public string IskText => BattleFormat.Isk(_fight.IskLost);
    public bool   IsOurs  => _fight.IsOurs;
    public string Who     => string.Join(", ", _fight.LargestSides.Select(_names.Of));

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    public void NamesChanged() => this.RaisePropertyChanged(nameof(Who));
}

/// <summary>
/// Looks through a period for large fights in this database, to start a battle from. What it finds
/// is a suggestion: opening one fills in the systems and times, for the user to adjust.
/// </summary>
public sealed class BattleFinderViewModel : ReactiveObject
{
    private const int MaxDays = 31;
    private const int DefaultMinPilots = 50;

    private readonly BattleReportService _service;
    private readonly Func<IReadOnlyList<long>, Task<Dictionary<long, string>>> _nameLookup;
    private readonly ZkillboardSettings _zkb;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly Action<FoundFight> _open;
    private readonly BattleNames _names = new();
    private CancellationTokenSource? _cts;

    public BattleFinderViewModel(BattleReportService service, Func<IReadOnlyList<long>, Task<Dictionary<long, string>>> nameLookup,
                                 ZkillboardSettings zkb, IDbContextFactory<AppDbContext> dbFactory, Action<FoundFight> open)
    {
        _service    = service;
        _nameLookup = nameLookup;
        _zkb        = zkb;
        _dbFactory  = dbFactory;
        _open       = open;

        FindCommand  = ReactiveCommand.CreateFromTask(FindAsync);
        FindCommand.ThrownExceptions.Subscribe(ex => { IsBusy = false; SetError(string.Format(BattleText.ReadFailed, ex.Message)); });
        Last24Command = ReactiveCommand.Create(() => SetPeriod(TimeSpan.FromHours(24)));
        Last3DaysCommand = ReactiveCommand.Create(() => SetPeriod(TimeSpan.FromDays(3)));
        Last7DaysCommand = ReactiveCommand.Create(() => SetPeriod(TimeSpan.FromDays(7)));
        _fromText = Format(DateTimeOffset.UtcNow.AddHours(-24));
    }

    private static string Format(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private void SetPeriod(TimeSpan length)
    {
        FromText = Format(DateTimeOffset.UtcNow - length);
        ToText = "";
    }

    private string _fromText, _toText = "", _minPilotsText = DefaultMinPilots.ToString(CultureInfo.CurrentCulture);
    public string FromText { get => _fromText; set => this.RaiseAndSetIfChanged(ref _fromText, value); }

    /// <summary>Blank is up to now.</summary>
    public string ToText { get => _toText; set => this.RaiseAndSetIfChanged(ref _toText, value); }

    public string MinPilotsText { get => _minPilotsText; set => this.RaiseAndSetIfChanged(ref _minPilotsText, value); }

    private bool _onlyOurs;
    public bool OnlyOurs { get => _onlyOurs; set => this.RaiseAndSetIfChanged(ref _onlyOurs, value); }

    private bool _isBusy, _isError, _hasResults;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }
    public bool IsError { get => _isError; private set => this.RaiseAndSetIfChanged(ref _isError, value); }
    public bool HasResults { get => _hasResults; private set => this.RaiseAndSetIfChanged(ref _hasResults, value); }

    private string _statusText = BattleText.FinderNote;
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    /// <summary>Where the database's kills come from, when that limits what can be found.</summary>
    public string CoverageNote => !_zkb.Enabled || _zkb.Scope == ZkbScope.MineAndCorp ? BattleText.CoverageFinder : "";
    public bool HasCoverageNote => CoverageNote.Length > 0;

    public BulkObservableCollection<BattleFightVm> Fights { get; } = [];

    public ReactiveCommand<Unit, Unit> FindCommand { get; }
    public ReactiveCommand<Unit, Unit> Last24Command { get; }
    public ReactiveCommand<Unit, Unit> Last3DaysCommand { get; }
    public ReactiveCommand<Unit, Unit> Last7DaysCommand { get; }

    private void SetError(string text)
    {
        IsError = true;
        StatusText = text;
    }

    private async Task FindAsync()
    {
        if (BattleSessionViewModel.ParseTime(FromText) is not { } from) { SetError(string.Format(BattleText.BadFromTime, BattleText.FindFights)); return; }
        var now = DateTimeOffset.UtcNow;
        var to = now;
        if (!string.IsNullOrWhiteSpace(ToText))
        {
            if (BattleSessionViewModel.ParseTime(ToText) is not { } end) { SetError(string.Format(BattleText.BadToTime, BattleText.FindFights)); return; }
            to = end;
        }
        if (to <= from) { SetError(string.Format(BattleText.EndBeforeStart, BattleText.FindFights)); return; }
        if (to - from > TimeSpan.FromDays(MaxDays)) { SetError(string.Format(BattleText.PeriodTooLong, MaxDays)); return; }
        if (!NumberText.TryParse(MinPilotsText, out long minValue) || minValue < 1) { SetError(BattleText.BadMinPilots); return; }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        IsError = false;
        HasResults = false;
        StatusText = BattleText.Finding;
        Fights.ResetTo([]);

        var minPilots = (int)minValue;
        var onlyOurs = OnlyOurs;
        var fights = await Task.Run(() => _service.FindFightsAsync(from, to, minPilots, onlyOurs, now, cts.Token));
        if (cts.IsCancellationRequested) return;

        var systemIds = fights.SelectMany(f => f.Windows.Select(w => w.SystemId)).Distinct().ToList();
        var systems = (await Task.Run(() => SystemIndexReport.LookUpAsync(_dbFactory, systemIds))).ToDictionary(s => s.SystemId, s => s.Name);
        if (cts.IsCancellationRequested) return;

        Fights.ResetTo([.. fights.Select(f => new BattleFightVm(f, systems, _names, _open))]);
        HasResults = Fights.Count > 0;
        StatusText = Fights.Count == 0 ? BattleText.NoFights : string.Format(BattleText.FoundCount, Fights.Count);
        IsBusy = false;

        await ResolveNamesAsync(fights);
    }

    /// <summary>The sides' names, after the list is up: the fights do not wait for them.</summary>
    private async Task ResolveNamesAsync(List<FoundFight> fights)
    {
        var ids = fights.SelectMany(f => f.LargestSides).Where(id => !_names.Has(id)).Distinct().ToList();
        if (ids.Count == 0) return;
        try
        {
            _names.Add(await Task.Run(() => _nameLookup(ids)));
            foreach (var fight in Fights) fight.NamesChanged();
        }
        catch (Exception ex) { StatusText += "  ·  " + string.Format(BattleText.NamesFailed, ex.Message); }
    }
}
