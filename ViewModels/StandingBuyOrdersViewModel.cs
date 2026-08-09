using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Models;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One row in the Standing Buy Orders grid.</summary>
public class StandingBuyOrderRowVm(StandingBuyOrderRow r)
{
    public long   DbId         { get; } = r.DbId;
    public int    TypeId       { get; } = r.TypeId;
    public string TypeName     { get; } = r.TypeName;
    public string LocationName { get; } = r.LocationName;
    public string Owner        { get; } = r.OwnerDisplay;
    public string Price        { get; } = r.PriceText;
    public string Remaining    { get; } = r.RemainingText;
    public string RemainingPct { get; } = r.RemainingPercentText;

    public string Status { get; } = r.MatchStatus == "matched" ? "Active" : "Missing";

    /// <summary>Colour cue: red when the order isn't there at all, amber when it is
    /// but has nearly run out, muted otherwise.</summary>
    public string StatusColor { get; } = r.MatchStatus switch
    {
        "matched" when r.IsLow => "#c8a84b",
        "matched"              => "#6a9a6a",
        _                      => "#cc6666",
    };

    public bool IsLow    { get; } = r.IsLow;
    public bool IsMissing{ get; } = r.MatchStatus != "matched";

    /// <summary>Shown in the row so the reason for the highlight is explicit rather
    /// than left to the colour alone.</summary>
    public string Note { get; } = r.MatchStatus != "matched"
        ? "No live buy order at this location"
        : r.IsLow
            ? $"Below {StandingBuyOrderService.LowRemainingThresholdPercent:N0}% of original volume — needs topping up"
            : "";
}

/// <summary>
/// Standing Buy Orders: define the buy orders you want kept up at a station or
/// structure, and see whether they are actually there.
///
/// The counterpart to the Standing Projects sub-tab in Corp Activity — same idea of
/// declaring intent and matching it against live data.
/// </summary>
public class StandingBuyOrdersViewModel : ReactiveObject
{
    private readonly StandingBuyOrderService _service;

    /// <summary>Exposed for the dialog, which reuses this service's item and station
    /// search helpers — same arrangement as CorpActivityViewModel.Service.</summary>
    public CorpActivityService SearchService { get; }

    /// <summary>Set by the view; shows the add/edit dialog and returns the result.</summary>
    public Func<StandingBuyOrder?, Task<StandingBuyOrder?>>? ShowDialog { get; set; }

    /// <summary>Set by the view; confirms a delete before it happens.</summary>
    public Func<Task<bool>>? ConfirmDelete { get; set; }

    public ObservableCollection<StandingBuyOrderRowVm> Rows { get; } = [];

    public StandingBuyOrdersViewModel(StandingBuyOrderService service, CorpActivityService searchService)
    {
        _service      = service;
        SearchService = searchService;

        AddCommand     = ReactiveCommand.CreateFromTask(AddAsync);
        EditCommand    = ReactiveCommand.CreateFromTask(EditAsync);
        DeleteCommand  = ReactiveCommand.CreateFromTask(DeleteAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);

        foreach (var c in new IReactiveCommand[] { AddCommand, EditCommand, DeleteCommand, RefreshCommand })
            c.ThrownExceptions.Subscribe(ex => StatusText = $"Error: {ex.Message}");

        _ = LoadAsync();
    }

    public ReactiveCommand<Unit, Unit> AddCommand     { get; }
    public ReactiveCommand<Unit, Unit> EditCommand    { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand  { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    private StandingBuyOrderRowVm? _selected;
    public StandingBuyOrderRowVm? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private async Task LoadAsync()
    {
        StatusText = "Loading…";
        var keep = Selected?.DbId;

        var rows = await _service.BuildGridRowsAsync();

        Rows.Clear();
        foreach (var r in rows) Rows.Add(new StandingBuyOrderRowVm(r));

        // Keep the selection across a refresh so editing then refreshing doesn't
        // dump the user back at the top of the grid.
        if (keep is { } id) Selected = Rows.FirstOrDefault(r => r.DbId == id);

        if (rows.Count == 0)
        {
            StatusText = "No standing buy orders defined yet.";
            return;
        }

        var missing = rows.Count(r => r.MatchStatus != "matched");
        var low     = rows.Count(r => r.IsLow);
        var parts   = new List<string> { $"{rows.Count:N0} defined" };
        if (missing > 0) parts.Add($"{missing:N0} missing");
        if (low > 0)     parts.Add($"{low:N0} running low");
        if (missing == 0 && low == 0) parts.Add("all healthy");

        StatusText = string.Join("  ·  ", parts);
    }

    private async Task AddAsync()
    {
        if (ShowDialog is null) return;
        var result = await ShowDialog(null);
        if (result is null) return;

        if (!await _service.AddAsync(result))
        {
            StatusText = $"A standing order for {result.TypeName} at {result.LocationName} already exists.";
            return;
        }
        await LoadAsync();
    }

    private async Task EditAsync()
    {
        if (ShowDialog is null || Selected is null) return;

        var existing = (await _service.GetAllAsync()).FirstOrDefault(o => o.Id == Selected.DbId);
        if (existing is null) return;

        var result = await ShowDialog(existing);
        if (result is null) return;

        await _service.UpdateAsync(result);
        await LoadAsync();
    }

    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        if (ConfirmDelete is not null && !await ConfirmDelete()) return;

        await _service.DeleteAsync(Selected.DbId);
        Selected = null;
        await LoadAsync();
    }
}
