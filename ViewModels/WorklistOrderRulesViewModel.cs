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

public sealed record OrderRuleRow(int Id, string ParkName, string LocationName, string AltText,
                                  WorklistOrderRule Rule);

/// <summary>
/// Rules that turn pending customer orders into buy orders.
///
/// The park decides how the orders are planned — facilities, rigs and therefore how much of each
/// material a build actually needs — and the station decides where the buying happens.
/// </summary>
public class WorklistOrderRulesViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly CorpActivityService             _stations;
    private readonly WorklistMarketAltService        _marketAlts;

    public ObservableCollection<OrderRuleRow> Rules { get; } = [];
    public ObservableCollection<ParkOption>   Parks { get; } = [];

    public ReactiveCommand<Unit, Unit>         AddCommand    { get; }
    public ReactiveCommand<OrderRuleRow, Unit> DeleteCommand { get; }

    public WorklistOrderRulesViewModel(IDbContextFactory<AppDbContext> dbFactory,
                                       CorpActivityService stations,
                                       WorklistMarketAltService marketAlts)
    {
        _dbFactory  = dbFactory;
        _stations   = stations;
        _marketAlts = marketAlts;

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask<OrderRuleRow>(async r =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.WorklistOrderRules.Where(x => x.Id == r.Id).ExecuteDeleteAsync();
            await LoadAsync();
            if (RulesChanged is not null) await RulesChanged();
        });

        _ = LoadAsync();
    }

    public Func<Task>? RulesChanged { get; set; }

    public Func<string?, CancellationToken, Task<IEnumerable<object>>> LocationPopulator =>
        async (text, ct) =>
        {
            var hits = await _stations.SearchSdeStationsAsync(text ?? "", ct);
            return hits.Cast<object>().ToList();
        };

    private object? _selectedLocation;
    public object? SelectedLocation { get => _selectedLocation; set => this.RaiseAndSetIfChanged(ref _selectedLocation, value); }

    private string _locationText = "";
    public string LocationText { get => _locationText; set => this.RaiseAndSetIfChanged(ref _locationText, value); }

    private ParkOption? _selectedPark;
    public ParkOption? SelectedPark { get => _selectedPark; set => this.RaiseAndSetIfChanged(ref _selectedPark, value); }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var rules = await db.WorklistOrderRules.AsNoTracking().ToListAsync();
        var parks = await db.IndyParks.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        var altMap = await _marketAlts.GetByLocationAsync();
        var pending = await db.TrackedOrders.CountAsync(o => o.Status == "pending");

        var parkNames = parks.ToDictionary(p => p.Id, p => p.Name);

        var rows = rules
            .OrderBy(r => parkNames.GetValueOrDefault(r.ParkId, ""))
            .Select(r => new OrderRuleRow(
                r.Id,
                parkNames.GetValueOrDefault(r.ParkId, $"Park {r.ParkId}"),
                r.LocationName,
                altMap.TryGetValue(r.LocationId, out var a) ? a.CharacterName : "— unassigned —",
                r))
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Rules.Clear();
            foreach (var r in rows) Rules.Add(r);

            Parks.Clear();
            foreach (var p in parks) Parks.Add(new ParkOption { Id = p.Id, Name = p.Name });

            // The pending count is here because a rule with no orders behind it produces
            // nothing, and that silence is otherwise indistinguishable from a broken rule.
            Status = parks.Count == 0
                ? "No Indy Parks exist yet — create one in Indy Parks first."
                : rows.Count == 0
                    ? $"No rules yet. {pending:N0} pending customer order(s) waiting."
                    : $"{rows.Count:N0} rule(s) · {pending:N0} pending customer order(s)";
        });
    }

    private async Task AddAsync()
    {
        if (SelectedPark is null) { Status = "Pick an Indy Park to plan against."; return; }
        if (SelectedLocation is not SdeStationResult loc) { Status = "Pick a station or structure."; return; }

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorklistOrderRules.Add(new WorklistOrderRule
        {
            ParkId       = SelectedPark.Id,
            LocationId   = loc.StationId,
            LocationName = loc.Name,
            Enabled      = true,
        });
        await db.SaveChangesAsync();

        SelectedLocation = null;
        LocationText     = "";

        await LoadAsync();
        if (RulesChanged is not null) await RulesChanged();
    }
}
