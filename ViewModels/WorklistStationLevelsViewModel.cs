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

/// <summary>
/// One station level as shown in the grid, editable in place. Each field writes straight through
/// on change — the grid is the whole editor, so there is nowhere else to press Save.
/// </summary>
public sealed class StationLevelRow : ReactiveObject
{
    private readonly Func<StationLevelRow, Task> _save;

    /// <summary>Suppresses writes while the constructor fills the fields in.</summary>
    private readonly bool _loaded;

    public WorklistStationLevel Level { get; }
    public int                  Id    => Level.Id;

    /// <summary>Held on the row, not reached for with $parent — see InvRuleRow.GroupOptions.</summary>
    public IEnumerable<InvGroupOption> GroupOptions { get; }

    public StationLevelRow(WorklistStationLevel level, InvGroupOption? group,
                           IEnumerable<InvGroupOption> groupOptions,
                           Func<StationLevelRow, Task> save)
    {
        Level        = level;
        GroupOptions = groupOptions;
        _save        = save;

        _group          = group;
        _locationName   = level.LocationName;
        _acceptsSurplus = level.AcceptsSurplus;

        _loaded = true;
    }

    private InvGroupOption? _group;
    public InvGroupOption? Group
    {
        get => _group;
        set
        {
            // A null is the combo clearing itself, not the user unsetting the group.
            if (value is null) return;
            this.RaiseAndSetIfChanged(ref _group, value);
            Persist();
        }
    }

    /// <summary>Sorted on, so the grid keeps working when the cell shows a combo.</summary>
    public string GroupName => _group?.Name ?? $"Group {Level.GroupId}";

    private string _locationName;
    public string LocationName
    {
        get => _locationName;
        private set => this.RaiseAndSetIfChanged(ref _locationName, value);
    }

    /// <summary>
    /// Set from the picker rather than by typing: the id is what the rest of the tool matches on,
    /// and a free-typed name would leave the row pointing at the station it used to mean.
    /// </summary>
    public SdeStationResult? PickedLocation
    {
        get => null;
        set
        {
            if (value is null) return;
            Level.LocationId = value.StationId;
            LocationName     = value.Name;
            Persist();
        }
    }

    private bool _acceptsSurplus;
    public bool AcceptsSurplus
    {
        get => _acceptsSurplus;
        set { this.RaiseAndSetIfChanged(ref _acceptsSurplus, value); Persist(); }
    }

    private void Persist()
    {
        if (!_loaded) return;

        Level.GroupId        = _group?.Id ?? Level.GroupId;
        Level.LocationName   = _locationName;
        Level.AcceptsSurplus = _acceptsSurplus;

        this.RaisePropertyChanged(nameof(GroupName));
        _ = _save(this);
    }
}

/// <summary>
/// Where each inventory group's stock should live.
///
/// <para>Distribution, not demand. The inventory rules already say how much of a group should
/// exist; this says which station it belongs at, so it raises hauling and never buying or
/// building — naming a station for a group must not double its target.</para>
/// </summary>
public class WorklistStationLevelsViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly CorpActivityService             _stations;
    private readonly WorklistSettings                _settings;

    public ObservableCollection<StationLevelRow> Levels { get; } = [];
    public ObservableCollection<InvGroupOption>  Groups { get; } = [];

    public ReactiveCommand<Unit, Unit>            AddCommand    { get; }
    public ReactiveCommand<StationLevelRow, Unit> DeleteCommand { get; }

    public WorklistStationLevelsViewModel(IDbContextFactory<AppDbContext> dbFactory,
                                          CorpActivityService stations,
                                          WorklistSettings settings)
    {
        _dbFactory = dbFactory;
        _stations  = stations;
        _settings  = settings;

        _restockBand = settings.RestockBandPercent;
        _surplusBand = settings.SurplusBandPercent;
        _bandsLoaded = true;

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask<StationLevelRow>(async r =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.WorklistStationLevels.Where(x => x.Id == r.Id).ExecuteDeleteAsync();
            await LoadAsync();
            if (LevelsChanged is not null) await LevelsChanged();
        });

        _ = LoadAsync();
    }

    public Func<Task>? LevelsChanged { get; set; }

    public Func<string?, CancellationToken, Task<IEnumerable<object>>> LocationPopulator =>
        async (text, ct) =>
            (await _stations.SearchSdeStationsAsync(text ?? "", ct)).Cast<object>().ToList();

    private InvGroupOption? _selectedGroup;
    public InvGroupOption? SelectedGroup { get => _selectedGroup; set => this.RaiseAndSetIfChanged(ref _selectedGroup, value); }

    private object? _selectedLocation;
    public object? SelectedLocation { get => _selectedLocation; set => this.RaiseAndSetIfChanged(ref _selectedLocation, value); }

    private string _locationText = "";
    public string LocationText { get => _locationText; set => this.RaiseAndSetIfChanged(ref _locationText, value); }

    private bool _acceptsSurplus;
    public bool AcceptsSurplus { get => _acceptsSurplus; set => this.RaiseAndSetIfChanged(ref _acceptsSurplus, value); }

    // ── Deadbands ─────────────────────────────────────────────────────────────
    //
    // How far a station may drift from its level before anything is moved. Held to the unit, a
    // level raises a haul for every handful consumed, and a task that appears, is ignored and
    // reappears teaches the reader to ignore the list. Both save on change like everything else
    // on these tabs.

    private double _restockBand;
    public double RestockBand
    {
        get => _restockBand;
        set { this.RaiseAndSetIfChanged(ref _restockBand, value); SaveBand(WorklistSettings.RestockBandKey, value); }
    }

    private double _surplusBand;
    public double SurplusBand
    {
        get => _surplusBand;
        set { this.RaiseAndSetIfChanged(ref _surplusBand, value); SaveBand(WorklistSettings.SurplusBandKey, value); }
    }

    private bool _bandsLoaded;

    private void SaveBand(string key, double percent)
    {
        if (!_bandsLoaded) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _settings.SetBandAsync(key, percent);
                if (LevelsChanged is not null) await LevelsChanged();
            }
            catch (Exception ex) { Status = $"Could not save that change: {ex.Message}"; }
        });
    }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    /// <summary>
    /// Writes one edited row back.
    ///
    /// <para>Group and station together are what a level means, and <see cref="AddAsync"/> already
    /// treats that pair as unique — so an edit that collides with another row is refused here
    /// rather than quietly creating a second level for the same group at the same station.</para>
    ///
    /// <para>Deliberately does not reload the grid: that would rebuild every row while the user
    /// still has a cell open and take the focus with it. Saves are coalesced per row so a burst
    /// of changes rebuilds the worklist behind this tab once rather than on every one.</para>
    /// </summary>
    private readonly Dictionary<int, CancellationTokenSource> _pendingSaves = [];

    private async Task SaveAsync(StationLevelRow row)
    {
        if (_pendingSaves.Remove(row.Id, out var inFlight)) inFlight.Cancel();
        var cts = _pendingSaves[row.Id] = new CancellationTokenSource();

        try { await Task.Delay(500, cts.Token); }
        catch (OperationCanceledException) { return; }
        finally { if (ReferenceEquals(_pendingSaves.GetValueOrDefault(row.Id), cts)) _pendingSaves.Remove(row.Id); }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var clash = await db.WorklistStationLevels.AsNoTracking().AnyAsync(x =>
                x.Id != row.Id && x.GroupId == row.Level.GroupId && x.LocationId == row.Level.LocationId);
            if (clash)
            {
                Status = $"{row.GroupName} already has a level at {row.LocationName} — that change was not saved.";
                return;
            }

            await db.WorklistStationLevels
                .Where(x => x.Id == row.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.GroupId,        row.Level.GroupId)
                    .SetProperty(x => x.LocationId,     row.Level.LocationId)
                    .SetProperty(x => x.LocationName,   row.Level.LocationName)
                    .SetProperty(x => x.AcceptsSurplus, row.Level.AcceptsSurplus));

            Status = "Saved.";
            if (LevelsChanged is not null) await LevelsChanged();
        }
        catch (Exception ex)
        {
            Status = $"Could not save that change: {ex.Message}";
        }
    }

    private async Task AddAsync()
    {
        if (SelectedGroup is null) { Status = "Pick an inventory group."; return; }
        if (SelectedLocation is not SdeStationResult loc)
        {
            Status = "Pick a station or structure.";
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        var existing = await db.WorklistStationLevels
            .FirstOrDefaultAsync(x => x.GroupId == SelectedGroup.Id && x.LocationId == loc.StationId);

        if (existing is not null)
        {
            existing.AcceptsSurplus = AcceptsSurplus;
            existing.LocationName   = loc.Name;
        }
        else
        {
            db.WorklistStationLevels.Add(new WorklistStationLevel
            {
                GroupId        = SelectedGroup.Id,
                LocationId     = loc.StationId,
                LocationName   = loc.Name,
                AcceptsSurplus = AcceptsSurplus,
            });
        }

        await db.SaveChangesAsync();

        SelectedLocation = null;
        LocationText     = "";
        await LoadAsync();
        if (LevelsChanged is not null) await LevelsChanged();
    }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var groups = await db.InvLevelGroups.AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name })
            .ToListAsync();
        var options = groups.Select(g => new InvGroupOption(g.Id, g.Name)).ToList();

        var rows = (await db.WorklistStationLevels.AsNoTracking().ToListAsync())
            .Select(l => new StationLevelRow(
                l,
                options.FirstOrDefault(o => o.Id == l.GroupId),
                Groups,
                SaveAsync))
            .OrderBy(r => r.GroupName).ThenBy(r => r.LocationName)
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Groups first: the rows' combo boxes bind their ItemsSource to this collection, and
            // a row realised against an empty one draws blank and pushes null back.
            Groups.Clear();
            foreach (var g in options) Groups.Add(g);

            Levels.Clear();
            foreach (var r in rows) Levels.Add(r);

            Status = rows.Count == 0
                ? "No station levels yet. Without one, material is only moved to where a job is waiting for it."
                : $"{rows.Count:N0} level(s)";
        });
    }
}
