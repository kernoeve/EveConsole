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

    /// <summary>Per-order breakdown for aggregated rows; null so Avalonia shows no
    /// tooltip at all rather than an empty box on single-order rows.</summary>
    public string? OwnerTooltip { get; } =
        string.IsNullOrWhiteSpace(r.OwnerTooltip) ? null : r.OwnerTooltip;
    public string Price        { get; } = r.PriceText;
    public string Remaining    { get; } = r.RemainingText;
    public string RemainingPct { get; } = r.RemainingPercentText;
    public string Expiry       { get; } = r.ExpiryText;

    /// <summary>Highest competing bid at the same station, or "—" when the station
    /// isn't a tracked market source.</summary>
    public string StationBid { get; } = r.CompetingBidText;

    public bool IsOutbid { get; } = r.IsOutbid;

    /// <summary>Our price goes amber when someone is paying more — until it is raised,
    /// sellers fill their order instead of ours. Amber rather than red: like low volume
    /// and near expiry, it is a standing order that needs adjusting, not one that is
    /// absent. Red stays reserved for an order that isn't there at all.</summary>
    public string PriceColor { get; } = r.IsOutbid ? "#c8a84b" : "#c8c8d8";

    public string StationBidColor { get; } = r.IsOutbid ? "#c8a84b"
                                           : r.IsLocationTracked ? "#c8c8d8" : "#555566";

    public string? PriceTooltip { get; } = !r.IsLocationTracked
        ? "This station isn't a configured market source, so competing bids are unknown. Add it under Settings → Market."
        : r.IsOutbid
            ? $"Outbid by {r.OutbidBy:N2} ISK — the station's best bid is {r.CompetingBestBid:N2}."
            : r.CompetingBestBid is null
                ? "No other buy orders for this item here."
                : null;

    public string Status { get; } = r.MatchStatus == "matched" ? "Active" : "Missing";

    /// <summary>Colour cue: red when the order isn't there at all, amber when it is
    /// but is running out — either of volume or of time — green otherwise.</summary>
    /// <summary>Amber covers every "the order is there but wants adjusting" case —
    /// outbid, running low, nearing expiry. Red means the order does not exist.</summary>
    public string StatusColor { get; } = r.MatchStatus switch
    {
        "matched" when r.IsOutbid || r.IsLow || r.IsExpiringSoon => "#c8a84b",
        "matched"                                               => "#6a9a6a",
        _                                                       => "#cc6666",
    };

    /// <summary>Expiry gets its own colour so a healthy-volume order that is about to
    /// lapse is visible in the column that explains why.</summary>
    public string ExpiryColor { get; } = r.IsExpiringSoon ? "#c8a84b" : "#999999";

    public bool IsLow          { get; } = r.IsLow;
    public bool IsExpiringSoon { get; } = r.IsExpiringSoon;
    public bool IsMissing      { get; } = r.MatchStatus != "matched";

    /// <summary>Written out so the reason for a highlight never depends on reading
    /// colour. Both conditions can apply at once, so they are combined rather than
    /// one shadowing the other.</summary>
    public string Note { get; } = BuildNote(r);

    private static string BuildNote(StandingBuyOrderRow r)
    {
        if (r.MatchStatus != "matched") return "No live buy order at this location";

        var parts = new List<string>();
        // Outbid leads: the other two mean the order is running out, this one means it
        // is not working at all.
        if (r.IsOutbid)
            parts.Add($"Outbid by {r.OutbidBy:N2} ISK (station best {r.CompetingBestBid:N2})");
        if (r.IsLow)
            parts.Add($"Below {StandingBuyOrderService.LowRemainingThresholdPercent:N0}% of original volume");
        if (r.IsExpiringSoon)
            parts.Add($"Under {StandingBuyOrderService.LowTimeThresholdPercent:N0}% of its duration left");

        return parts.Count == 0 ? "" : string.Join("; ", parts) + " — needs attention";
    }
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

        // Counted per condition, not partitioned: one order can be both outbid and
        // running low, and each is a separate thing to go and fix. So these can sum
        // to more than the number of rows, unlike the Overview alert, which reports
        // each order once under its most urgent reason.
        var missing  = rows.Count(r => r.MatchStatus != "matched");
        var outbid   = rows.Count(r => r.IsOutbid);
        var low      = rows.Count(r => r.IsLow);
        var expiring = rows.Count(r => r.IsExpiringSoon);

        var parts = new List<string> { $"{rows.Count:N0} defined" };
        if (missing > 0)  parts.Add($"{missing:N0} missing");
        if (outbid > 0)   parts.Add($"{outbid:N0} outbid");
        if (low > 0)      parts.Add($"{low:N0} running low");
        if (expiring > 0) parts.Add($"{expiring:N0} expiring soon");
        if (missing == 0 && outbid == 0 && low == 0 && expiring == 0) parts.Add("all healthy");

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
