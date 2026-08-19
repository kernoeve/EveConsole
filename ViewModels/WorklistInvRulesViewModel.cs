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

/// <summary>
/// One rule as shown in the grid, editable in place.
///
/// <para>Every field writes straight through to the database on change rather than waiting for a
/// Save. The grid is the whole editor — there is nowhere else to press Save, and a rule left
/// half-edited because the user clicked away would silently not be the rule they think it is.</para>
/// </summary>
public sealed class InvRuleRow : ReactiveObject
{
    private readonly Func<InvRuleRow, Task> _save;
    private readonly Func<long, string>     _altFor;

    /// <summary>Suppresses writes while the constructor fills the fields in.</summary>
    private readonly bool _loaded;

    public WorklistInvRule Rule { get; }
    public int             Id   => Rule.Id;

    /// <summary>
    /// The dropdown contents, held on the row rather than reached for through the visual tree.
    ///
    /// <para>A cell template binding its ItemsSource with <c>$parent[UserControl]</c> has to walk
    /// up to an ancestor that is not attached yet while the cell is being realised, so the list
    /// arrives after SelectedItem — and a ComboBox handed a selection that is not in its (still
    /// empty) items clears it. That is what drew every Group and Action cell blank until the tab
    /// was left and re-entered. Binding to the row's own DataContext resolves immediately.</para>
    /// </summary>
    public IEnumerable<InvGroupOption> GroupOptions  { get; }
    public IReadOnlyList<string>       ActionOptions { get; }

    public InvRuleRow(WorklistInvRule rule, InvGroupOption? group,
                      IEnumerable<InvGroupOption> groupOptions, IReadOnlyList<string> actionOptions,
                      Func<InvRuleRow, Task> save, Func<long, string> altFor)
    {
        Rule          = rule;
        GroupOptions  = groupOptions;
        ActionOptions = actionOptions;
        _save         = save;
        _altFor       = altFor;

        _group        = group;
        _action       = rule.Action;
        _threshold    = rule.ThresholdPercent;
        _fill         = rule.FillTargetPercent;
        _locationName = rule.LocationName;

        _loaded = true;
    }

    private InvGroupOption? _group;
    public InvGroupOption? Group
    {
        get => _group;
        set
        {
            // A null is the combo clearing itself, not the user unsetting the group — there is no
            // such thing as a rule without one. Accepting it would blank the cell and save.
            if (value is null) return;
            this.RaiseAndSetIfChanged(ref _group, value);
            Persist();
        }
    }

    /// <summary>Sorted and searched on, so the grid keeps working when the cell shows a combo.</summary>
    public string GroupName => _group?.Name ?? $"Group {Rule.GroupId}";

    private string _action;
    public string Action
    {
        get => _action;
        set
        {
            this.RaiseAndSetIfChanged(ref _action, value);
            this.RaisePropertyChanged(nameof(NeedsLocation));
            this.RaisePropertyChanged(nameof(LocationDisplay));
            this.RaisePropertyChanged(nameof(AltText));
            Persist();
        }
    }

    private double _threshold;
    public double Threshold
    {
        get => _threshold;
        set { this.RaiseAndSetIfChanged(ref _threshold, value); Persist(); }
    }

    private double _fill;
    public double Fill
    {
        get => _fill;
        set { this.RaiseAndSetIfChanged(ref _fill, value); Persist(); }
    }

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
            Rule.LocationId = value.StationId;
            LocationName    = value.Name;
            this.RaisePropertyChanged(nameof(LocationDisplay));
            this.RaisePropertyChanged(nameof(AltText));
            Persist();
        }
    }

    /// <summary>A Build rule's site comes from the park, so there is no station to show or pick.</summary>
    public bool NeedsLocation => Action != "Build";

    public string LocationDisplay => NeedsLocation ? LocationName : "— from park —";

    public string AltText => Action == "Build" ? "— by skill —" : _altFor(Rule.LocationId);

    private void Persist()
    {
        if (!_loaded) return;

        Rule.GroupId           = _group?.Id ?? Rule.GroupId;
        Rule.Action            = _action;
        Rule.ThresholdPercent  = _threshold;
        Rule.FillTargetPercent = _fill;
        Rule.LocationName      = _locationName;

        this.RaisePropertyChanged(nameof(GroupName));
        _ = _save(this);
    }
}

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

    /// <summary>Buy places an order for the shortfall; Build starts a job for it.</summary>
    public IReadOnlyList<string> Actions { get; } = ["Buy", "Build"];

    private string _action = "Buy";
    public string Action
    {
        get => _action;
        set
        {
            this.RaiseAndSetIfChanged(ref _action, value);
            this.RaisePropertyChanged(nameof(NeedsLocation));
        }
    }

    /// <summary>
    /// Only a Buy rule needs a station: it says where the order goes and whose market alt places
    /// it. A Build rule's site comes from the Indy Park, which assigns a facility per category,
    /// so asking for one here would collect a value nothing reads.
    /// </summary>
    public bool NeedsLocation => Action != "Build";

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    /// <summary>
    /// Names the rules that can only ever produce blocked work.
    ///
    /// <para>A buy rule routes its purchases through the market alt at its station, so one pointing
    /// at a station with no alt raises items nothing can action. The grid already shows
    /// "— unassigned —" per row, but that is one cell among dozens: two rules had been sitting on
    /// Jita IV - Moon 5 instead of Moon 4 — same station name, one character apart in the picker —
    /// producing blocked buys that read as a mystery until the rows were compared by id.</para>
    ///
    /// <para>The station is named rather than just counted, because the fault is almost always
    /// that it is nearly the right one.</para>
    /// </summary>
    private string _altWarning = "";
    public string AltWarning { get => _altWarning; private set => this.RaiseAndSetIfChanged(ref _altWarning, value); }
    public bool HasAltWarning => AltWarning.Length > 0;

    private static string BuildAltWarning(
        IReadOnlyList<Models.WorklistInvRule> rules,
        IReadOnlyDictionary<int, string> groupNames,
        IReadOnlySet<long> altLocations)
    {
        // Build rules route by skills and slots, not by a market alt, so they are not at fault.
        var offenders = rules
            .Where(r => r.Enabled && r.Action != "Build" && !altLocations.Contains(r.LocationId))
            .Select(r => new
            {
                Group = groupNames.GetValueOrDefault(r.GroupId, $"group {r.GroupId}"),
                Where = r.LocationId == 0 ? "no station set" : r.LocationName,
            })
            .OrderBy(x => x.Group)
            .ToList();

        if (offenders.Count == 0) return "";

        var named = string.Join(", ", offenders.Take(4).Select(o => $"{o.Group} → {o.Where}"));
        var rest  = offenders.Count > 4 ? $", and {offenders.Count - 4} more" : "";

        return $"{offenders.Count} buy rule(s) point at a station with no market alt, so their "
             + $"purchases have no character to place them and will show as blocked: {named}{rest}. "
             + "Assign an alt on the Market tab, or re-pick the station on the rule.";
    }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var rules  = await db.WorklistInvRules.AsNoTracking().ToListAsync();
        var groups = await db.InvLevelGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        var altMap = await _marketAlts.GetByLocationAsync();

        var groupNames = groups.ToDictionary(g => g.Id, g => g.Name);
        var options    = groups.Select(g => new InvGroupOption(g.Id, g.Name)).ToList();

        // Surfaced per rule because a Buy rule pointing at a station with no market alt produces
        // blocked items, and this is where that is fixable. A Build rule routes by skills and
        // slots instead, so it has no market alt to show.
        string AltFor(long locationId) =>
            altMap.TryGetValue(locationId, out var d) ? d.CharacterName : "— unassigned —";

        var rows = rules
            .OrderBy(r => groupNames.GetValueOrDefault(r.GroupId, ""))
            .ThenByDescending(r => r.ThresholdPercent)
            .Select(r => new InvRuleRow(
                r,
                options.FirstOrDefault(o => o.Id == r.GroupId),
                Groups, Actions,
                SaveAsync,
                AltFor))
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Groups first, always. The rows carry a group each and the grid's combo boxes bind
            // their ItemsSource to this collection — realise a row against an empty one and the
            // combo cannot match its SelectedItem, so it draws blank and pushes null back.
            Groups.Clear();
            foreach (var g in options) Groups.Add(g);

            Rules.Clear();
            foreach (var r in rows) Rules.Add(r);

            Status = groups.Count == 0
                ? "No inventory level groups exist yet — create one in Inventory Levels first."
                : rows.Count == 0
                    ? "No rules yet."
                    : $"{rows.Count:N0} rule(s)";

            AltWarning = BuildAltWarning(rules, groupNames, altMap.Keys.ToHashSet());
            this.RaisePropertyChanged(nameof(HasAltWarning));
        });
    }

    /// <summary>
    /// Pending saves, one per row.
    ///
    /// <para>Per row rather than one shared timer, because cancelling a neighbour's pending write
    /// to start your own would drop it silently.</para>
    /// </summary>
    private readonly Dictionary<int, CancellationTokenSource> _pendingSaves = [];

    /// <summary>
    /// Writes one edited row back, a short pause after the last keystroke.
    ///
    /// <para>The pause matters: typing "75" into a percentage sets 7 and then 75, and every write
    /// rebuilds the whole worklist behind this tab — which is seconds of work. Coalescing a burst
    /// of keystrokes into one write keeps the cell responsive while it is being typed in.</para>
    ///
    /// <para>Deliberately does not reload the grid. A reload would rebuild every row while the
    /// user still has the cell open, taking the focus and the caret with it — so the edited row
    /// stays as it is and only the worklist behind it is rebuilt.</para>
    /// </summary>
    private async Task SaveAsync(InvRuleRow row)
    {
        if (_pendingSaves.Remove(row.Id, out var inFlight)) inFlight.Cancel();
        var cts = _pendingSaves[row.Id] = new CancellationTokenSource();

        try { await Task.Delay(500, cts.Token); }
        catch (OperationCanceledException) { return; }
        finally { if (ReferenceEquals(_pendingSaves.GetValueOrDefault(row.Id), cts)) _pendingSaves.Remove(row.Id); }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.WorklistInvRules
                .Where(x => x.Id == row.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.GroupId,           row.Rule.GroupId)
                    .SetProperty(x => x.Action,            row.Rule.Action)
                    .SetProperty(x => x.ThresholdPercent,  row.Rule.ThresholdPercent)
                    .SetProperty(x => x.FillTargetPercent, row.Rule.FillTargetPercent)
                    .SetProperty(x => x.LocationId,        row.Rule.LocationId)
                    .SetProperty(x => x.LocationName,      row.Rule.LocationName));

            Status = "Saved.";
            if (RulesChanged is not null) await RulesChanged();
        }
        catch (Exception ex)
        {
            Status = $"Could not save that change: {ex.Message}";
        }
    }

    private async Task AddAsync()
    {
        if (SelectedGroup is null) { Status = "Pick an inventory level group."; return; }

        // A Build rule has no station of its own — the park decides where the job runs.
        SdeStationResult? loc = SelectedLocation as SdeStationResult;
        if (NeedsLocation && loc is null) { Status = "Pick a station or structure for the buy order."; return; }
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
            LocationId        = loc?.StationId ?? 0,
            LocationName      = loc?.Name ?? "",
            Enabled           = true,
            Action            = Action,
        });
        await db.SaveChangesAsync();

        // The station is left as it was: rules are written a station at a time, so clearing it
        // after each add threw away the field the player was about to reuse. The field selects its
        // text on focus, so moving to another station is one click and a keystroke.

        await LoadAsync();
        if (RulesChanged is not null) await RulesChanged();
    }
}
