using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Models;
using EveConsole.Services;
using ReactiveUI;
using Avalonia.Media;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

/// <summary>One row in the Standing Buy Orders grid.</summary>
public class StandingBuyOrderRowVm(StandingBuyOrderRow r)
{
    public long   DbId         { get; } = r.DbId;
    public int    TypeId       { get; } = r.TypeId;
    public string TypeName     { get; } = r.TypeName;
    public string LocationName { get; } = r.LocationName;
    public string Owner        { get; } = r.OwnerDisplay;

    // ── Links ─────────────────────────────────────────────────────────────────
    public bool HasItemLink     => r.TypeId     > 0 && r.TypeName.Length     > 0;
    public bool HasLocationLink => r.LocationId > 0 && r.LocationName.Length > 0;
    /// <summary>Only where one owner holds the order — see StandingBuyOrderRow.OwnerId.</summary>
    public bool HasOwnerLink    => r.OwnerId    > 0 && r.OwnerDisplay.Length > 0;

    public void OpenItem() => EntityNavigator.Instance.Item(r.TypeId);

    /// <summary>⚠️ Station versus structure by int range: SdeStations keys on an int, so an id
    /// above that range cannot be a station.</summary>
    public void OpenLocation()
    {
        if (r.LocationId <= 0) return;
        if (r.LocationId <= int.MaxValue)
            EntityNavigator.Instance.Entity(EntityKind.Station, r.LocationId);
        else
            EntityNavigator.Instance.Structure(r.LocationId);
    }

    public void OpenOwner() => EntityNavigator.Instance.Entity(
        r.OwnerType == "corporation" ? EntityKind.PlayerCorp : EntityKind.Pilot, r.OwnerId);

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
    public IBrush PriceColor { get; } = r.IsOutbid ? Palette.Warn : Palette.TextPrimary;

    public IBrush StationBidColor { get; } = r.IsOutbid ? Palette.Warn
                                           : r.IsLocationTracked ? Palette.TextPrimary : Palette.TextFaint;

    public string? PriceTooltip { get; } = !r.IsLocationTracked
        ? MarketText.TipStationNotTracked
        : r.IsOutbid
            ? string.Format(MarketText.TipOutbidBy, r.OutbidBy, r.CompetingBestBid)
            : r.CompetingBestBid is null
                ? MarketText.TipNoOtherBids
                : null;

    /// <summary>"Outbid" beats "Active" because it is the more actionable truth: the
    /// order is live, but nothing will fill it until the price moves. It also carries
    /// that state onto the Overview panel, which has no price column of its own.
    /// Volume and expiry stay out of here — those are already shown as their own
    /// columns, whereas the competing bid is not.</summary>
    public string Status { get; } = r.MatchStatus != "matched" ? MarketText.OrderStateMissing
                                  : r.IsOutbid                 ? MarketText.OrderStateOutbid
                                  : MarketText.OrderStateActive;

    /// <summary>Colour cue: red when the order isn't there at all, amber when it is
    /// but is running out — either of volume or of time — green otherwise.</summary>
    /// <summary>Amber covers every "the order is there but wants adjusting" case —
    /// outbid, running low, nearing expiry. Red means the order does not exist.</summary>
    public IBrush StatusColor { get; } = r.MatchStatus switch
    {
        "matched" when r.IsOutbid || r.IsLow || r.IsExpiringSoon => Palette.Warn,
        "matched"                                               => Palette.Good,
        _                                                       => Palette.Bad,
    };

    /// <summary>Expiry gets its own colour so a healthy-volume order that is about to
    /// lapse is visible in the column that explains why.</summary>
    public IBrush ExpiryColor { get; } = r.IsExpiringSoon ? Palette.Warn : Palette.TextMuted;

    /// <summary>Sort key tracking the status colour — red, then orange, then healthy.
    /// Derived here rather than in the caller so it cannot drift from StatusColor.</summary>
    public int SeverityRank { get; } = r.MatchStatus != "matched"                ? 0   // red
                                     : r.IsOutbid || r.IsLow || r.IsExpiringSoon ? 1   // orange
                                     : 2;                                              // green

    public bool IsLow          { get; } = r.IsLow;
    public bool IsExpiringSoon { get; } = r.IsExpiringSoon;
    public bool IsMissing      { get; } = r.MatchStatus != "matched";

    /// <summary>Written out so the reason for a highlight never depends on reading
    /// colour. Both conditions can apply at once, so they are combined rather than
    /// one shadowing the other.</summary>
    public string Note { get; } = BuildNote(r);

    private static string BuildNote(StandingBuyOrderRow r)
    {
        if (r.MatchStatus != "matched") return MarketText.NoteNoLiveOrder;

        var parts = new List<string>();
        // Outbid leads: the other two mean the order is running out, this one means it
        // is not working at all.
        if (r.IsOutbid)
            parts.Add(string.Format(MarketText.NoteOutbidBy, r.OutbidBy, r.CompetingBestBid));
        if (r.IsLow)
            parts.Add(string.Format(MarketText.NoteBelowVolume, StandingBuyOrderService.LowRemainingThresholdPercent));
        if (r.IsExpiringSoon)
            parts.Add(string.Format(MarketText.NoteUnderDuration, StandingBuyOrderService.LowTimeThresholdPercent));

        return parts.Count == 0 ? "" : string.Format(MarketText.NoteNeedsAttention, string.Join("; ", parts));
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
            c.ThrownExceptions.Subscribe(ex => StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message));

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
        StatusText = CommonText.Loading;
        var keep = Selected?.DbId;

        var rows = await _service.BuildGridRowsAsync();

        Rows.Clear();
        foreach (var r in rows) Rows.Add(new StandingBuyOrderRowVm(r));

        // Keep the selection across a refresh so editing then refreshing doesn't
        // dump the user back at the top of the grid.
        if (keep is { } id) Selected = Rows.FirstOrDefault(r => r.DbId == id);

        if (rows.Count == 0)
        {
            StatusText = MarketText.StatusNoStandingOrders;
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

        var parts = new List<string> { string.Format(MarketText.StatusCountDefined, rows.Count) };
        if (missing > 0)  parts.Add(string.Format(MarketText.StatusCountMissing, missing));
        if (outbid > 0)   parts.Add(string.Format(MarketText.StatusCountOutbid, outbid));
        if (low > 0)      parts.Add(string.Format(MarketText.StatusCountRunningLow, low));
        if (expiring > 0) parts.Add(string.Format(MarketText.StatusCountExpiringSoon, expiring));
        if (missing == 0 && outbid == 0 && low == 0 && expiring == 0) parts.Add(MarketText.StatusAllHealthy);

        StatusText = string.Join("  ·  ", parts);
    }

    private async Task AddAsync()
    {
        if (ShowDialog is null) return;
        var result = await ShowDialog(null);
        if (result is null) return;

        if (!await _service.AddAsync(result))
        {
            StatusText = string.Format(MarketText.StatusStandingOrderExists, result.TypeName, result.LocationName);
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
