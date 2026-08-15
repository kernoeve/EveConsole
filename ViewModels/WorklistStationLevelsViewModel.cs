using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One station level as shown in the grid, with the group resolved to its name.</summary>
public sealed record StationLevelRow(
    int Id, string GroupName, string LocationName, string SurplusText, WorklistStationLevel Level);

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

    public ObservableCollection<StationLevelRow> Levels { get; } = [];
    public ObservableCollection<InvGroupOption>  Groups { get; } = [];

    public ReactiveCommand<Unit, Unit>            AddCommand    { get; }
    public ReactiveCommand<StationLevelRow, Unit> DeleteCommand { get; }

    public WorklistStationLevelsViewModel(IDbContextFactory<AppDbContext> dbFactory,
                                          CorpActivityService stations)
    {
        _dbFactory = dbFactory;
        _stations  = stations;

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

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

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
        var groupNames = groups.ToDictionary(g => g.Id, g => g.Name);

        var rows = (await db.WorklistStationLevels.AsNoTracking().ToListAsync())
            .Select(l => new StationLevelRow(
                l.Id,
                groupNames.GetValueOrDefault(l.GroupId, $"Group {l.GroupId}"),
                l.LocationName,
                l.AcceptsSurplus ? "Yes" : "",
                l))
            .OrderBy(r => r.GroupName).ThenBy(r => r.LocationName)
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Levels.Clear();
            foreach (var r in rows) Levels.Add(r);

            Groups.Clear();
            foreach (var g in groups) Groups.Add(new InvGroupOption(g.Id, g.Name));

            Status = rows.Count == 0
                ? "No station levels yet. Without one, material is only moved to where a job is waiting for it."
                : $"{rows.Count:N0} level(s)";
        });
    }
}
