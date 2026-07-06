using System.Collections.ObjectModel;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace EveCortex.ViewModels;

public record TokenOption(long Id, string OwnerType, string DisplayName);

// Live per-region row for the price-history sweep monitor.
public class HistoryRegionRowVm : ReactiveObject
{
    public int    RegionId   { get; }
    public string RegionName { get; }

    public HistoryRegionRowVm(int regionId, string name) { RegionId = regionId; RegionName = name; }

    private int _refreshed;
    public int Refreshed
    {
        get => _refreshed;
        set { this.RaiseAndSetIfChanged(ref _refreshed, value); RaiseDerived(); }
    }

    private int _queue;
    public int Queue
    {
        get => _queue;
        set { this.RaiseAndSetIfChanged(ref _queue, value); RaiseDerived(); }
    }

    private void RaiseDerived()
    {
        this.RaisePropertyChanged(nameof(Total));
        this.RaisePropertyChanged(nameof(CountsText));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(StatusColor));
    }

    public int    Total      => Refreshed + Queue;
    public string CountsText => $"{Refreshed:N0} / {Total:N0}";

    // Current = fully refreshed; Filling = partial; Empty = nothing fresh yet.
    public string StatusText => Total == 0 ? "No tracked items"
                              : Queue == 0 ? "Current"
                              : Refreshed == 0 ? "Empty"
                              : "Filling";

    public string StatusColor => Total == 0 ? "#666677"
                               : Queue == 0 ? "#70ad47"
                               : Refreshed == 0 ? "#cc6666"
                               : "#c8a84b";
}

public class ScheduleRowVm
{
    public string         DisplayName  { get; init; } = "";
    public string         Endpoint     { get; init; } = "";
    public DateTimeOffset? LastCalledAt { get; init; }
    public DateTimeOffset? NextCallAt   { get; init; }

    public string LastCalledText => LastCalledAt.HasValue
        ? LastCalledAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "Never";

    public string NextCallText => NextCallAt.HasValue
        ? NextCallAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "—";

    public string TimeUntilText
    {
        get
        {
            if (!NextCallAt.HasValue) return "—";
            var remaining = NextCallAt.Value - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return "Due";
            if (remaining.TotalHours >= 1)   return $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m";
            if (remaining.TotalMinutes >= 1) return $"{(int)remaining.TotalMinutes}m {remaining.Seconds:00}s";
            return $"{(int)remaining.TotalSeconds}s";
        }
    }
}

public class ApiActivityViewModel : ReactiveObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EsiPollingService    _polling;
    private readonly TimerSettingsService _timerSettings;
    private readonly MarketHistoryService _history;
    private readonly ContractsService     _contracts;

    public ObservableCollection<ActivityEntry>       Entries        { get; }
    public ObservableCollection<InFlightCall>        InFlight       { get; }
    public ObservableCollection<TokenOption>         TokenOptions   { get; } = [];
    public ObservableCollection<ScheduleRowVm>       Schedule       { get; } = [];
    public ObservableCollection<ScheduleRowVm>       MarketSchedule { get; } = [];
    public ObservableCollection<HistoryRegionRowVm>  HistoryRegions { get; } = [];

    private string _historyState = "";
    public string HistoryState
    {
        get => _historyState;
        private set => this.RaiseAndSetIfChanged(ref _historyState, value);
    }

    // ── Contract-items monitor ──────────────────────────────────────────────────
    private string _contractsState = "";
    public string ContractsState { get => _contractsState; private set => this.RaiseAndSetIfChanged(ref _contractsState, value); }

    private string _contractsPublicText = "—";
    public string ContractsPublicText { get => _contractsPublicText; private set => this.RaiseAndSetIfChanged(ref _contractsPublicText, value); }

    private string _contractsOwnedText = "—";
    public string ContractsOwnedText { get => _contractsOwnedText; private set => this.RaiseAndSetIfChanged(ref _contractsOwnedText, value); }

    private string _contractsDeferredText = "—";
    public string ContractsDeferredText { get => _contractsDeferredText; private set => this.RaiseAndSetIfChanged(ref _contractsDeferredText, value); }

    private bool _hasNoInFlight = true;
    public bool HasNoInFlight
    {
        get => _hasNoInFlight;
        private set => this.RaiseAndSetIfChanged(ref _hasNoInFlight, value);
    }

    private TokenOption? _selectedToken;
    public TokenOption? SelectedToken
    {
        get => _selectedToken;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedToken, value);
            if (value != null)
                _ = LoadScheduleAsync(value);
        }
    }

    public ApiActivityViewModel(
        ApiActivityLog       log,
        IServiceScopeFactory scopeFactory,
        EsiPollingService    polling,
        TimerSettingsService timerSettings,
        MarketHistoryService history,
        ContractsService     contracts)
    {
        Entries        = log.Entries;
        InFlight       = log.InFlightCalls;
        _scopeFactory  = scopeFactory;
        _polling       = polling;
        _timerSettings = timerSettings;
        _history       = history;
        _contracts     = contracts;

        InFlight.CollectionChanged += (_, _) => HasNoInFlight = InFlight.Count == 0;
    }

    // ── Contract-items monitor ──────────────────────────────────────────────────

    public async Task RefreshContractsAsync()
    {
        ContractsService.ContractItemsStatus s;
        try { s = await _contracts.GetItemsStatusAsync(); }
        catch { return; /* best-effort monitor */ }

        int pubQueue   = s.PublicTotal - s.PublicPulled;
        int ownedQueue = s.OwnedTotal  - s.OwnedPulled;

        ContractsPublicText   = $"{s.PublicPulled:N0} / {s.PublicTotal:N0} pulled · {pubQueue:N0} queued";
        ContractsOwnedText    = $"{s.OwnedPulled:N0} / {s.OwnedTotal:N0} pulled · {ownedQueue:N0} queued";
        ContractsDeferredText = $"{s.Deferred:N0} deferred (corp contracts issued by another corp — not pulled)";
        ContractsState = s.Running
            ? $"● Running — {pubQueue + ownedQueue:N0} contracts queued for items"
            : (pubQueue + ownedQueue) > 0
                ? $"○ Idle — {pubQueue + ownedQueue:N0} contracts queued for items"
                : "○ Idle — all item pulls complete";
    }

    // ── Price-history sweep monitor ─────────────────────────────────────────────

    // Recomputes accurate per-region counts from the DB (no ESI), then syncs the rows.
    public async Task RefreshHistorySweepAsync()
    {
        try { await _history.RefreshStatusesAsync(); } catch { /* best-effort monitor */ }
        SyncHistorySweep();
    }

    // Copies the service's in-memory counts into the bound collection. Call on the UI thread.
    public void SyncHistorySweep()
    {
        var snap = _history.SweepStatuses;

        foreach (var s in snap)
        {
            var row = HistoryRegions.FirstOrDefault(r => r.RegionId == s.RegionId);
            if (row is null)
            {
                row = new HistoryRegionRowVm(s.RegionId, s.RegionName);
                HistoryRegions.Add(row);
            }
            row.Refreshed = s.Refreshed;
            row.Queue     = s.Queue;
        }
        foreach (var row in HistoryRegions.Where(r => snap.All(s => s.RegionId != r.RegionId)).ToList())
            HistoryRegions.Remove(row);

        int totalQueue = snap.Sum(s => s.Queue);
        HistoryState = _history.IsSweeping
            ? $"● Running — {totalQueue:N0} item{(totalQueue == 1 ? "" : "s")} queued"
            : totalQueue > 0
                ? $"○ Idle — {totalQueue:N0} item{(totalQueue == 1 ? "" : "s")} queued for next sweep"
                : "○ Idle — all tracked items current";
    }

    public async Task LoadTokenOptionsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var chars = await db.Characters.AsNoTracking()
            .Where(c => c.RefreshToken != "")
            .OrderBy(c => c.Name)
            .ToListAsync();

        var corps = await db.Corporations.AsNoTracking()
            .Where(c => c.RefreshToken != "")
            .OrderBy(c => c.Name)
            .ToListAsync();

        TokenOptions.Clear();
        foreach (var c in chars)
            TokenOptions.Add(new TokenOption(c.Id, "character", c.Name));
        foreach (var corp in corps)
            TokenOptions.Add(new TokenOption(corp.Id, "corporation", $"[Corp] {corp.Name}"));

        if (_selectedToken is null && TokenOptions.Count > 0)
            SelectedToken = TokenOptions[0];
    }

    public async Task LoadMarketScheduleAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var configs = await db.MarketPricingConfigs.AsNoTracking()
            .Where(c => c.IsEnabled)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .ToListAsync();

        int intervalSec = _timerSettings.GetInterval("market.refresh", 3600);

        var rows = configs.Select(cfg =>
        {
            DateTimeOffset? lastCalled = cfg.LastRefreshed;
            DateTimeOffset? nextCall   = lastCalled.HasValue
                ? lastCalled.Value.AddSeconds(intervalSec)
                : null;
            return new ScheduleRowVm
            {
                DisplayName  = cfg.LocationName,
                Endpoint     = cfg.Method,
                LastCalledAt = lastCalled,
                NextCallAt   = nextCall,
            };
        }).ToList();

        MarketSchedule.Clear();
        foreach (var row in rows)
            MarketSchedule.Add(row);
    }

    private async Task LoadScheduleAsync(TokenOption token)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var records = await db.EsiCallRecords.AsNoTracking()
            .Where(r => r.OwnerId == token.Id && r.OwnerType == token.OwnerType)
            .ToListAsync();

        var recordMap = records.ToDictionary(r => r.Endpoint);

        var endpointInfos = token.OwnerType == "character"
            ? _polling.CharacterEndpointInfos
            : _polling.CorpEndpointInfos;

        var rows = endpointInfos.Select(ep =>
        {
            recordMap.TryGetValue(ep.Key, out var rec);
            DateTimeOffset? lastCalled = rec?.LastCalledAt;
            int intervalSec = _timerSettings.GetInterval(ep.Key, ep.DefaultSeconds);
            DateTimeOffset? nextCall = lastCalled.HasValue
                ? lastCalled.Value.AddSeconds(intervalSec)
                : null;
            return new ScheduleRowVm
            {
                DisplayName  = ep.DisplayName,
                Endpoint     = ep.Key,
                LastCalledAt = lastCalled,
                NextCallAt   = nextCall,
            };
        }).ToList();

        Schedule.Clear();
        foreach (var row in rows)
            Schedule.Add(row);
    }
}
