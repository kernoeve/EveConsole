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

public sealed record InvGroupOption(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One rule as shown in the grid, with the group resolved to its name.</summary>
public sealed record InvRuleRow(int Id, string GroupName, string ThresholdText,
                                string FillText, string LocationName, string AltText,
                                WorklistInvRule Rule);

/// <summary>
/// Rules that turn an inventory level group into buy orders.
///
/// "Below 100% of the group target, have an order at UALX. Below 75%, also have one at Jita."
/// Several rules on one group stack rather than override, so falling further adds a hub order
/// without cancelling the local one.
/// </summary>
public class WorklistInvRulesViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly CorpActivityService             _stations;
    private readonly WorklistMarketAltService             _marketAlts;

    public ObservableCollection<InvRuleRow>     Rules  { get; } = [];
    public ObservableCollection<InvGroupOption> Groups { get; } = [];

    public ReactiveCommand<Unit, Unit>      AddCommand    { get; }
    public ReactiveCommand<InvRuleRow, Unit> DeleteCommand { get; }

    public WorklistInvRulesViewModel(IDbContextFactory<AppDbContext> dbFactory,
                                     CorpActivityService stations,
                                     WorklistMarketAltService marketAlts)
    {
        _dbFactory = dbFactory;
        _stations  = stations;
        _marketAlts     = marketAlts;

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask<InvRuleRow>(async r =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.WorklistInvRules.Where(x => x.Id == r.Id).ExecuteDeleteAsync();
            await LoadAsync();
            if (RulesChanged is not null) await RulesChanged();
        });

        _ = LoadAsync();
    }

    /// <summary>Raised after a rule changes so the worklist rebuilds without a manual refresh.</summary>
    public Func<Task>? RulesChanged { get; set; }

    public Func<string?, CancellationToken, Task<IEnumerable<object>>> LocationPopulator =>
        async (text, ct) =>
        {
            var hits = await _stations.SearchSdeStationsAsync(text ?? "", ct);
            return hits.Cast<object>().ToList();
        };

    private object? _selectedLocation;
    public object? SelectedLocation
    {
        get => _selectedLocation;
        set => this.RaiseAndSetIfChanged(ref _selectedLocation, value);
    }

    private string _locationText = "";
    public string LocationText { get => _locationText; set => this.RaiseAndSetIfChanged(ref _locationText, value); }

    private InvGroupOption? _selectedGroup;
    public InvGroupOption? SelectedGroup { get => _selectedGroup; set => this.RaiseAndSetIfChanged(ref _selectedGroup, value); }

    private string _threshold = "100";
    public string Threshold { get => _threshold; set => this.RaiseAndSetIfChanged(ref _threshold, value); }

    private string _fill = "100";
    public string Fill { get => _fill; set => this.RaiseAndSetIfChanged(ref _fill, value); }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var rules  = await db.WorklistInvRules.AsNoTracking().ToListAsync();
        var groups = await db.InvLevelGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        var altMap = await _marketAlts.GetByLocationAsync();

        var groupNames = groups.ToDictionary(g => g.Id, g => g.Name);

        var rows = rules
            .OrderBy(r => groupNames.GetValueOrDefault(r.GroupId, ""))
            .ThenByDescending(r => r.ThresholdPercent)
            .Select(r => new InvRuleRow(
                r.Id,
                groupNames.GetValueOrDefault(r.GroupId, $"Group {r.GroupId}"),
                $"below {r.ThresholdPercent:0.#}%",
                $"to {r.FillTargetPercent:0.#}%",
                r.LocationName,
                // Surfaced per rule because a rule pointing at a station with no market alt produces
                // blocked items, and this is where that is fixable.
                altMap.TryGetValue(r.LocationId, out var d) ? d.CharacterName : "— unassigned —",
                r))
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Rules.Clear();
            foreach (var r in rows) Rules.Add(r);

            Groups.Clear();
            foreach (var g in groups) Groups.Add(new InvGroupOption(g.Id, g.Name));

            Status = groups.Count == 0
                ? "No inventory level groups exist yet — create one in Inventory Levels first."
                : rows.Count == 0
                    ? "No rules yet."
                    : $"{rows.Count:N0} rule(s)";
        });
    }

    private async Task AddAsync()
    {
        if (SelectedGroup is null)             { Status = "Pick an inventory level group."; return; }
        if (SelectedLocation is not SdeStationResult loc) { Status = "Pick a station or structure."; return; }
        if (!double.TryParse(Threshold, out var threshold) || threshold <= 0)
                                               { Status = "Threshold must be a number above zero."; return; }
        if (!double.TryParse(Fill, out var fill) || fill <= 0)
                                               { Status = "Fill target must be a number above zero."; return; }

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorklistInvRules.Add(new WorklistInvRule
        {
            GroupId           = SelectedGroup.Id,
            ThresholdPercent  = threshold,
            FillTargetPercent = fill,
            LocationId        = loc.StationId,
            LocationName      = loc.Name,
            Enabled           = true,
        });
        await db.SaveChangesAsync();

        SelectedLocation = null;
        LocationText     = "";

        await LoadAsync();
        if (RulesChanged is not null) await RulesChanged();
    }
}
