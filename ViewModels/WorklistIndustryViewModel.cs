using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Worklist;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A station added to the asset scope on top of its region or system.</summary>
public sealed record ScopeStationRow(int Id, string LocationName);

/// <summary>One enabled character, with their slot picture alongside the switches.</summary>
/// <summary>
/// One industry character in the grid, with the three activity switches editable in place and
/// saved on change. Which slots may be used is the only thing this list decides, so it is the
/// only thing there is to edit.
/// </summary>
public sealed class IndyCharRow : ReactiveObject
{
    private readonly Func<IndyCharRow, Task> _save;
    private readonly bool _loaded;

    public WorklistIndyChar Config { get; }
    public int    Id            => Config.Id;
    public string CharacterName => Config.CharacterName;
    public string Slots         { get; }

    public IndyCharRow(WorklistIndyChar config, string slots, Func<IndyCharRow, Task> save)
    {
        Config = config;
        Slots  = slots;
        _save  = save;

        _manufacturing = config.Manufacturing;
        _reactions     = config.Reactions;
        _science       = config.Science;

        _loaded = true;
    }

    private bool _manufacturing;
    public bool Manufacturing
    {
        get => _manufacturing;
        set { this.RaiseAndSetIfChanged(ref _manufacturing, value); Persist(); }
    }

    private bool _reactions;
    public bool Reactions
    {
        get => _reactions;
        set { this.RaiseAndSetIfChanged(ref _reactions, value); Persist(); }
    }

    private bool _science;
    public bool Science
    {
        get => _science;
        set { this.RaiseAndSetIfChanged(ref _science, value); Persist(); }
    }

    /// <summary>Sorted on, and it still reads as a sentence when every box is clear.</summary>
    public string Activities
    {
        get
        {
            var parts = new List<string>(3);
            if (_manufacturing) parts.Add("Manufacturing");
            if (_reactions)     parts.Add("Reactions");
            if (_science)       parts.Add("Science");
            return parts.Count == 0 ? "— none —" : string.Join(", ", parts);
        }
    }

    private void Persist()
    {
        if (!_loaded) return;

        Config.Manufacturing = _manufacturing;
        Config.Reactions     = _reactions;
        Config.Science       = _science;

        this.RaisePropertyChanged(nameof(Activities));
        _ = _save(this);
    }
}

/// <summary>
/// Which characters run industry, on what, and where their materials come from.
///
/// Opt-in rather than "all my alts": slots belonging to characters in corps that never run
/// industry are not available for work, and counting them would turn the free-slot number into
/// fiction.
/// </summary>
public class WorklistIndustryViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IndustryAssignmentService       _assignment;
    private readonly WorklistSettings                _settings;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly CorpActivityService             _corpActivity;
    private readonly WorklistMarketAltService        _marketAlts;

    public ObservableCollection<IndyCharRow>     Chars      { get; } = [];
    public ObservableCollection<ParkOption>      Parks      { get; } = [];

    public ReactiveCommand<Unit, Unit>            AddScopeStationCommand    { get; }
    public ReactiveCommand<ScopeStationRow, Unit> DeleteScopeStationCommand { get; }

    public WorklistIndustryViewModel(IDbContextFactory<AppDbContext> dbFactory,
                                     IndustryAssignmentService assignment,
                                     WorklistSettings settings,
                                     AppErrorLogger errorLogger,
                                     CorpActivityService corpActivity,
                                     WorklistMarketAltService marketAlts)
    {
        _dbFactory    = dbFactory;
        _assignment   = assignment;
        _settings     = settings;
        _errorLogger  = errorLogger;
        _corpActivity = corpActivity;
        _marketAlts   = marketAlts;

        // Neither add nor delete: every authorised character is enrolled automatically, so an
        // added row would be a duplicate and a removed one would return on the next pass. Clearing
        // all three activity boxes is how a character is taken out of consideration, and that
        // survives, because enrolment never touches a row that already exists.
        AddScopeStationCommand    = ReactiveCommand.CreateFromTask(AddScopeStationAsync);
        DeleteScopeStationCommand = ReactiveCommand.CreateFromTask<ScopeStationRow>(async r =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.WorklistIndyScopeStations.Where(x => x.Id == r.Id).ExecuteDeleteAsync();
            await LoadAsync();
            if (IndustryChanged is not null) await IndustryChanged();
        });

        _ = LoadAsync();
    }

    public Func<Task>? IndustryChanged { get; set; }

    // ── Park ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The "follow the starred park" choice, stored as park id 0.
    ///
    /// <para>Resolved when work is planned rather than when it is picked, so moving the star in
    /// Indy Parks moves the planning with it. Pinning the id at the moment of choosing would make
    /// the two silently disagree the first time the default changed.</para>
    /// </summary>
    public const string DefaultParkLabel = "<Default>";

    private ParkOption? _selectedPark;
    public ParkOption? SelectedPark
    {
        get => _selectedPark;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPark, value);
            if (value is not null && !_loading) _ = ApplyParkAsync(value.Id);
        }
    }

    private async Task ApplyParkAsync(int parkId)
    {
        await _settings.SetIndustryParkAsync(parkId);
        await LoadAsync();
        if (IndustryChanged is not null) await IndustryChanged();
    }

    /// <summary>
    /// Structures in the chosen park with no real location behind them.
    ///
    /// Worth its own warning rather than a silent gap: an unlinked structure models rigs but
    /// points nowhere, so materials sitting in the real facility cannot be counted. Jobs there
    /// would read as blocked for want of materials that are actually present.
    /// </summary>
    private string _parkWarning = "";
    public string ParkWarning { get => _parkWarning; private set => this.RaiseAndSetIfChanged(ref _parkWarning, value); }
    public bool HasParkWarning => ParkWarning.Length > 0;

    /// <summary>Which park <see cref="DefaultParkLabel"/> resolves to, blank when a park is named
    /// outright. The dropdown reads "&lt;Default&gt;" and nothing else would say what that is.</summary>
    private string _parkResolvedText = "";
    public string ParkResolvedText { get => _parkResolvedText; private set => this.RaiseAndSetIfChanged(ref _parkResolvedText, value); }

    // ── Where industry buys ───────────────────────────────────────────────────

    public Func<string?, CancellationToken, Task<IEnumerable<object>>> LocationPopulator =>
        async (text, ct) =>
            (await _corpActivity.SearchSdeStationsAsync(text ?? "", ct)).Cast<object>().ToList();

    private object? _selectedBuyLocation;
    public object? SelectedBuyLocation
    {
        get => _selectedBuyLocation;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedBuyLocation, value);
            if (_loading || value is not SdeStationResult s) return;
            _ = Fire(async () =>
            {
                await _settings.SetIndustryBuyLocationAsync(s.StationId, s.Name);
                await LoadAsync();
                if (IndustryChanged is not null) await IndustryChanged();
            }, "SetBuyLocation");
        }
    }

    private string _buyLocationText = "";
    public string BuyLocationText { get => _buyLocationText; set => this.RaiseAndSetIfChanged(ref _buyLocationText, value); }

    /// <summary>
    /// Says when buying cannot be routed. Shortfalls are still found and still reported on the
    /// jobs they block; what is missing is somewhere to send the purchase, so the buy tasks
    /// would have no station and no character.
    /// </summary>
    private string _buyWarning = "";
    public string BuyWarning { get => _buyWarning; private set => this.RaiseAndSetIfChanged(ref _buyWarning, value); }
    public bool HasBuyWarning => BuyWarning.Length > 0;

    // ── How far to look for materials ─────────────────────────────────────────

    private bool _includeNonPersonalCorps;
    public bool IncludeNonPersonalCorps
    {
        get => _includeNonPersonalCorps;
        set
        {
            this.RaiseAndSetIfChanged(ref _includeNonPersonalCorps, value);
            if (_loading) return;
            _ = Fire(async () =>
            {
                await _settings.SetIncludeNonPersonalCorpsAsync(value);
                if (IndustryChanged is not null) await IndustryChanged();
            }, "SetIncludeNonPersonalCorps");
        }
    }

    public string[] Scopes { get; } = ["Everywhere", "Region", "System"];

    private string _selectedScope = "Everywhere";
    public string SelectedScope
    {
        get => _selectedScope;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedScope, value);
            this.RaisePropertyChanged(nameof(NeedsScopePlace));
            if (_loading) return;

            // Everywhere needs no place, so it can save immediately. The other two are only
            // half-specified until one is picked, and saving a region scope with no region would
            // silently mean "nowhere" — every material would read as unowned and every job would
            // raise a purchase.
            if (value == "Everywhere")
                _ = Fire(async () =>
                {
                    await _settings.SetIndustryScopeAsync("Everywhere", null, "");
                    await LoadAsync();
                    if (IndustryChanged is not null) await IndustryChanged();
                }, "SetScope");
        }
    }

    public bool NeedsScopePlace => SelectedScope != "Everywhere";

    public Func<string?, CancellationToken, Task<IEnumerable<object>>> ScopePlacePopulator =>
        async (text, ct) => SelectedScope == "System"
            ? (await _corpActivity.SearchSdeSystemsAsync(text ?? "", ct)).Cast<object>().ToList()
            : (await _corpActivity.SearchSdeRegionsAsync(text ?? "", ct)).Cast<object>().ToList();

    private object? _selectedScopePlace;
    public object? SelectedScopePlace
    {
        get => _selectedScopePlace;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedScopePlace, value);
            if (_loading) return;

            (long Id, string Name)? place = value switch
            {
                SdeSystemResult s => (s.SystemId, s.Name),
                SdeRegionResult r => (r.RegionId, r.Name),
                _                 => null,
            };
            if (place is not { } p) return;

            _ = Fire(async () =>
            {
                await _settings.SetIndustryScopeAsync(SelectedScope, p.Id, p.Name);
                await LoadAsync();
                if (IndustryChanged is not null) await IndustryChanged();
            }, "SetScope");
        }
    }

    private string _scopePlaceText = "";
    public string ScopePlaceText { get => _scopePlaceText; set => this.RaiseAndSetIfChanged(ref _scopePlaceText, value); }

    // ── Extra stations in scope ───────────────────────────────────────────────

    public ObservableCollection<ScopeStationRow> ScopeStations { get; } = [];

    private object? _selectedExtraStation;
    public object? SelectedExtraStation { get => _selectedExtraStation; set => this.RaiseAndSetIfChanged(ref _selectedExtraStation, value); }

    private string _extraStationText = "";
    public string ExtraStationText { get => _extraStationText; set => this.RaiseAndSetIfChanged(ref _extraStationText, value); }

    private async Task AddScopeStationAsync()
    {
        if (SelectedExtraStation is not SdeStationResult s)
        {
            Status = "Pick a station to add to the scope.";
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        if (!await db.WorklistIndyScopeStations.AnyAsync(x => x.LocationId == s.StationId))
        {
            db.WorklistIndyScopeStations.Add(new WorklistIndyScopeStation
            {
                LocationId = s.StationId, LocationName = s.Name,
            });
            await db.SaveChangesAsync();
        }

        SelectedExtraStation = null;
        ExtraStationText     = "";
        await LoadAsync();
        if (IndustryChanged is not null) await IndustryChanged();
    }

    // ── Job length ────────────────────────────────────────────────────────────
    //
    // Held as text rather than a number so a half-typed value does not momentarily read as zero
    // and turn the cap off. Nothing is saved until the field loses focus or the value parses,
    // and blank means "no limit" as plainly as it looks.

    private string _maxJobDaysMfg = "";
    public string MaxJobDaysMfg
    {
        get => _maxJobDaysMfg;
        set { this.RaiseAndSetIfChanged(ref _maxJobDaysMfg, value); Save(WorklistSettings.MaxJobDaysMfgKey, value); }
    }

    private string _maxJobDaysRxn = "";
    public string MaxJobDaysRxn
    {
        get => _maxJobDaysRxn;
        set { this.RaiseAndSetIfChanged(ref _maxJobDaysRxn, value); Save(WorklistSettings.MaxJobDaysRxnKey, value); }
    }

    /// <summary>
    /// Copying and invention.
    ///
    /// <para>Apart from the other two because an invention batch behaves differently from a build:
    /// a long build still delivers its first fifth on day two if it is split, while an invention
    /// job yields nothing at all until it ends. A single attempt on a capital blueprint runs about
    /// eight days before rig bonuses, so a limit inherited from manufacturing would force one
    /// attempt per job and spend a science slot on each.</para>
    /// </summary>
    private string _maxJobDaysSci = "";
    public string MaxJobDaysSci
    {
        get => _maxJobDaysSci;
        set { this.RaiseAndSetIfChanged(ref _maxJobDaysSci, value); Save(WorklistSettings.MaxJobDaysSciKey, value); }
    }

    private void Save(string key, string raw)
    {
        if (_loading) return;
        var text = raw.Trim();
        var days = text.Length == 0 ? 0.0
                 : double.TryParse(text, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0
                     ? d : -1.0;
        if (days < 0) return;   // mid-edit garbage: leave the stored value alone

        _ = Fire(async () =>
        {
            await _settings.SetMaxJobDaysAsync(key, days);
            if (IndustryChanged is not null) await IndustryChanged();
        }, "SetMaxJobDays");
    }

    /// <summary>
    /// Runs work started from a property setter. Nothing awaits these, so an escaping exception
    /// would land unhandled on the thread pool and take the client down — a poor trade for a
    /// save that lost a race with another writer.
    /// </summary>
    private Task Fire(Func<Task> work, string context) => Task.Run(async () =>
    {
        try { await work(); }
        catch (Exception ex) { _errorLogger.Log(nameof(WorklistIndustryViewModel), context, ex); }
    });

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _loading;

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            // Before anything is read, so a character authorised since the last build is in the
            // grid rather than missing until the next worklist refresh.
            await _assignment.EnrolMissingAsync();

            await using var db = await _dbFactory.CreateDbContextAsync();

            var parks = await db.IndyParks.AsNoTracking().OrderBy(p => p.Name).ToListAsync();

            // Two ids, deliberately. The dropdown shows what was *chosen* — <Default> stays
            // selected as <Default> — while every warning below describes the park actually
            // planned against.
            var chosenParkId = _settings.IndustryParkId;
            var parkId       = await WorklistSettings.ResolveParkIdAsync(db, chosenParkId);
            var defaultName  = chosenParkId > 0
                ? ""
                : parks.FirstOrDefault(p => p.Id == parkId)?.Name ?? "";

            var unlinked = parkId > 0
                ? await db.IndyStructures.AsNoTracking()
                    .Where(s => s.ParkId == parkId && s.RealStructureId == null)
                    .Select(s => s.DisplayName)
                    .ToListAsync()
                : [];

            var linkedCount = parkId > 0
                ? await db.IndyStructures.CountAsync(s => s.ParkId == parkId && s.RealStructureId != null)
                : 0;

            var buyLocId = _settings.IndustryBuyLocationId;
            var buyAlt   = buyLocId > 0
                ? (await _marketAlts.GetByLocationAsync()).GetValueOrDefault(buyLocId)
                : null;

            var scopeStations = (await db.WorklistIndyScopeStations.AsNoTracking()
                    .OrderBy(s => s.LocationName)
                    .ToListAsync())
                .Select(s => new ScopeStationRow(s.Id, s.LocationName))
                .ToList();

            // Slot figures come from the assignment service so the tab and the generator can
            // never disagree about how many slots a character has free.
            var candidates = await _assignment.LoadCandidatesAsync();

            var rows = candidates
                .OrderBy(c => c.Config.CharacterName)
                .Select(c => new IndyCharRow(
                    c.Config,
                    $"M {c.FreeSlots[IndustryPool.Manufacturing]}/{c.Capacity[IndustryPool.Manufacturing]}  ·  "
                    + $"R {c.FreeSlots[IndustryPool.Reaction]}/{c.Capacity[IndustryPool.Reaction]}  ·  "
                    + $"S {c.FreeSlots[IndustryPool.Science]}/{c.Capacity[IndustryPool.Science]}",
                    SaveCharAsync))
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Chars.Clear();
                foreach (var r in rows) Chars.Add(r);

                Parks.Clear();
                // Id 0 is the sentinel: follow whichever park carries the star, resolved each
                // time work is planned rather than pinned here.
                Parks.Add(new ParkOption { Id = 0, Name = DefaultParkLabel });
                foreach (var p in parks) Parks.Add(new ParkOption { Id = p.Id, Name = p.Name });
                _selectedPark = Parks.FirstOrDefault(p => p.Id == chosenParkId) ?? Parks[0];
                this.RaisePropertyChanged(nameof(SelectedPark));

                // Blank rather than "0" for no limit: an empty box reads as unset, where a zero
                // reads as a cap of nothing.
                _maxJobDaysMfg = Text(_settings.MaxJobDaysManufacturing);
                _maxJobDaysRxn = Text(_settings.MaxJobDaysReaction);
                _maxJobDaysSci = Text(_settings.MaxJobDaysScience);
                this.RaisePropertyChanged(nameof(MaxJobDaysMfg));
                this.RaisePropertyChanged(nameof(MaxJobDaysRxn));
                this.RaisePropertyChanged(nameof(MaxJobDaysSci));

                _buyLocationText = _settings.IndustryBuyLocationName;
                this.RaisePropertyChanged(nameof(BuyLocationText));

                _includeNonPersonalCorps = _settings.IncludeNonPersonalCorps;
                this.RaisePropertyChanged(nameof(IncludeNonPersonalCorps));

                _selectedScope  = _settings.IndustryScope;
                _scopePlaceText = _settings.IndustryScopeName;
                this.RaisePropertyChanged(nameof(SelectedScope));
                this.RaisePropertyChanged(nameof(NeedsScopePlace));
                this.RaisePropertyChanged(nameof(ScopePlaceText));

                ScopeStations.Clear();
                foreach (var s in scopeStations) ScopeStations.Add(s);

                BuyWarning = buyLocId <= 0
                    ? "No buy location set. Shortfalls will still be reported on the jobs they block, but the purchases have nowhere to be raised."
                    : buyAlt is null
                        ? $"No market alt is assigned to {_settings.IndustryBuyLocationName} on the Market Alts tab, so buy tasks there will have no character."
                        : "";
                this.RaisePropertyChanged(nameof(HasBuyWarning));

                ParkWarning = parkId <= 0
                    ? $"{DefaultParkLabel} is selected and no park is marked default in Indy Parks. "
                      + "Industry jobs stay silent until a park is starred there or picked here, because the park decides facilities and rigs."
                    : unlinked.Count > 0
                        ? $"{unlinked.Count} structure(s) in this park are not linked to a real location "
                          + $"({string.Join(", ", unlinked.Take(3))}"
                          + (unlinked.Count > 3 ? ", …" : "") + "). "
                          + "Materials there cannot be counted, so jobs may read as blocked when the inputs are actually present."
                        : linkedCount == 0
                            ? "This park has no structures. Nothing can be checked for materials."
                            : "";
                this.RaisePropertyChanged(nameof(HasParkWarning));

                // Which park <Default> currently points at. Shown because the dropdown says
                // "<Default>" and nothing else on screen would say what that resolved to.
                ParkResolvedText = defaultName.Length > 0 ? $"→ {defaultName}" : "";
            });
        }
        catch (Exception ex)
        {
            // ⚠️ Every field on this tab is assigned at the end of the try above, so anything that
            // throws part-way leaves the whole tab blank — park, job lengths, asset scope and the
            // character grid all at once, with no clue why. That happened. Say so instead.
            _errorLogger.Log(nameof(WorklistIndustryViewModel), nameof(LoadAsync), ex);
            Status = $"Could not load the industry settings: {ex.Message}";
        }
        finally { _loading = false; }
    }

    private static string Text(double days) =>
        days > 0 ? days.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// Writes one character's activity switches back.
    ///
    /// <para>No reload afterwards. Rebuilding the grid would re-query every character's slots and
    /// replace the row under the cursor mid-click; the worklist behind the tab is refreshed
    /// instead, which is the part that actually changes.</para>
    /// </summary>
    private async Task SaveCharAsync(IndyCharRow row)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.WorklistIndyChars
                .Where(x => x.Id == row.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Manufacturing, row.Config.Manufacturing)
                    .SetProperty(x => x.Reactions,     row.Config.Reactions)
                    .SetProperty(x => x.Science,       row.Config.Science));

            if (IndustryChanged is not null) await IndustryChanged();
        }
        catch (Exception ex)
        {
            // The grid has no status line of its own — the box the user just clicked is the
            // feedback. A failed save is a real fault, so it goes to the error log rather than
            // being swallowed to keep the panel quiet.
            _errorLogger.Log(nameof(WorklistIndustryViewModel), nameof(SaveCharAsync), ex);
        }
    }

}
