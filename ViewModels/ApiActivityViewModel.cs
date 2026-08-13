using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace EveConsole.ViewModels;

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
    private readonly LpStoreService       _lpStore;

    private readonly ZkillboardSettings         _zkbSettings;
    private readonly ZkillboardPollingService    _zkbPolling;
    private readonly ZkillboardFirehoseService   _zkbFirehose;
    private readonly ZkillboardBackfillService   _zkbBackfill;
    private readonly ZkillboardPostService       _zkbPost;
    private readonly IntelService                _intel;
    private readonly MonitoringSettings          _monitoring;
    private readonly EntityNameBackfillService   _nameCache;
    private readonly AlarmService                _alarms;

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
        ContractsService     contracts,
        ZkillboardSettings        zkbSettings,
        ZkillboardPollingService  zkbPolling,
        ZkillboardFirehoseService zkbFirehose,
        ZkillboardBackfillService zkbBackfill,
        ZkillboardPostService     zkbPost,
        IntelService              intel,
        MonitoringSettings        monitoring,
        EntityNameBackfillService nameCache,
        AlarmService              alarms,
        LpStoreService            lpStore)
    {
        Entries        = log.Entries;
        InFlight       = log.InFlightCalls;
        _scopeFactory  = scopeFactory;
        _polling       = polling;
        _timerSettings = timerSettings;
        _history       = history;
        _contracts     = contracts;
        _lpStore       = lpStore;
        _zkbSettings   = zkbSettings;
        _zkbPolling    = zkbPolling;
        _zkbFirehose   = zkbFirehose;
        _zkbBackfill   = zkbBackfill;
        _zkbPost       = zkbPost;
        _intel         = intel;
        _monitoring    = monitoring;
        _nameCache     = nameCache;
        _alarms        = alarms;

        // Fire-and-forget: the sweep reports its own progress through StructureSweepRunning and
        // the summary lines, so the command does not need to await it to keep the UI honest.
        RunStructureSweep = ReactiveCommand.Create(() =>
        {
            _ = _polling.ForceResolveStructureNamesAsync();
        });

        RunStructureSweep.ThrownExceptions.Subscribe(_ => { });

        InFlight.CollectionChanged += (_, _) => HasNoInFlight = InFlight.Count == 0;
    }

    // ── Background process monitors (zKillboard, name cache) ────────────────────
    //
    // Each service already publishes a StatusText it maintains as it works; this just
    // mirrors them onto the UI thread so the window can show what is running versus idle
    // without any of them having to know a window exists.

    private string _zkbLiveState = "";
    public string ZkbLiveState { get => _zkbLiveState; private set => this.RaiseAndSetIfChanged(ref _zkbLiveState, value); }

    private string _zkbLiveDetail = "";
    public string ZkbLiveDetail { get => _zkbLiveDetail; private set => this.RaiseAndSetIfChanged(ref _zkbLiveDetail, value); }

    private string _zkbBackfillDetail = "";
    public string ZkbBackfillDetail { get => _zkbBackfillDetail; private set => this.RaiseAndSetIfChanged(ref _zkbBackfillDetail, value); }

    private string _zkbPostDetail = "";
    public string ZkbPostDetail { get => _zkbPostDetail; private set => this.RaiseAndSetIfChanged(ref _zkbPostDetail, value); }

    private string _zkbScopeText = "";
    public string ZkbScopeText { get => _zkbScopeText; private set => this.RaiseAndSetIfChanged(ref _zkbScopeText, value); }

    private string _zkbCoverageText = "";
    public string ZkbCoverageText { get => _zkbCoverageText; private set => this.RaiseAndSetIfChanged(ref _zkbCoverageText, value); }

    private string _nameCacheState = "";
    public string NameCacheState { get => _nameCacheState; private set => this.RaiseAndSetIfChanged(ref _nameCacheState, value); }

    private string _intelState = "—";
    public string IntelState { get => _intelState; private set => this.RaiseAndSetIfChanged(ref _intelState, value); }

    private string _intelChannelsText = "—";
    public string IntelChannelsText { get => _intelChannelsText; private set => this.RaiseAndSetIfChanged(ref _intelChannelsText, value); }

    private string _intelDetail = "—";
    public string IntelDetail { get => _intelDetail; private set => this.RaiseAndSetIfChanged(ref _intelDetail, value); }

    private string _intelBacklogText = "—";
    public string IntelBacklogText { get => _intelBacklogText; private set => this.RaiseAndSetIfChanged(ref _intelBacklogText, value); }

    private string _nameCacheCountText = "—";
    public string NameCacheCountText { get => _nameCacheCountText; private set => this.RaiseAndSetIfChanged(ref _nameCacheCountText, value); }

    private string _alarmState = "—";
    public string AlarmState { get => _alarmState; private set => this.RaiseAndSetIfChanged(ref _alarmState, value); }

    private string _alarmDetail = "—";
    public string AlarmDetail { get => _alarmDetail; private set => this.RaiseAndSetIfChanged(ref _alarmDetail, value); }

    private string _alarmNextText = "—";
    public string AlarmNextText { get => _alarmNextText; private set => this.RaiseAndSetIfChanged(ref _alarmNextText, value); }

    private string _alarmLastFireText = "—";
    public string AlarmLastFireText { get => _alarmLastFireText; private set => this.RaiseAndSetIfChanged(ref _alarmLastFireText, value); }

    // ── Structures ───────────────────────────────────────────────────────────

    private string _structureState = "—";
    public string StructureState { get => _structureState; private set => this.RaiseAndSetIfChanged(ref _structureState, value); }

    private string _structureSweepText = "—";
    public string StructureSweepText { get => _structureSweepText; private set => this.RaiseAndSetIfChanged(ref _structureSweepText, value); }

    private string _structureNextText = "—";
    public string StructureNextText { get => _structureNextText; private set => this.RaiseAndSetIfChanged(ref _structureNextText, value); }

    private string _structureCountsText = "—";
    public string StructureCountsText { get => _structureCountsText; private set => this.RaiseAndSetIfChanged(ref _structureCountsText, value); }

    private string _publicStructureText = "—";
    public string PublicStructureText { get => _publicStructureText; private set => this.RaiseAndSetIfChanged(ref _publicStructureText, value); }

    private bool _structureSweepRunning;
    /// <summary>Mirrors the polling service, so the Sweep now button disables while one runs.</summary>
    public bool StructureSweepRunning { get => _structureSweepRunning; private set => this.RaiseAndSetIfChanged(ref _structureSweepRunning, value); }

    /// <summary>Runs the structure sweep now rather than waiting for the hour to come round.</summary>
    public ReactiveCommand<Unit, Unit> RunStructureSweep { get; }

    /// <summary>Cheap, in-memory only — safe to call on the window's 2s tick.</summary>
    public void SyncBackgroundProcesses()
    {
        var enabled = _zkbSettings.Enabled;
        var allScope = _zkbSettings.Scope == ZkbScope.All;

        ZkbLiveState = !enabled
            ? "○ Disabled — zKillboard import is switched off"
            : allScope
                ? "● All kills — live capture via the R2Z2 firehose"
                : "● My characters & corp — live capture via interval poll";

        ZkbScopeText     = allScope ? "All kills (universe-wide)" : "My characters & corp";
        ZkbLiveDetail    = allScope ? _zkbFirehose.StatusText : _zkbPolling.StatusText;
        ZkbBackfillDetail = _zkbBackfill.StatusText;
        ZkbPostDetail    = _zkbSettings.PostEnabled ? _zkbPost.StatusText : "○ Off — not submitting kills to zKillboard";
        ZkbCoverageText  = _zkbSettings.LastFullDay is { } d
            ? $"Daily dumps imported through {d:yyyy-MM-dd}"
            : "No daily dump imported yet";

        // Intel. Chat import is the gate — parsing runs off its loop, so with chat off nothing
        // reaches the parser however many channels are ticked.
        var intelChannels = _monitoring.ChatIntelChannels;
        IntelState = !_monitoring.ChatEnabled
            ? "○ Disabled — chat log import is switched off"
            : intelChannels.Count == 0
                ? "○ No intel channels — tick one under Settings → Chat Logs"
                : _intel.IsRunning
                    ? "● Parsing intel channels"
                    : "● Watching intel channels";

        IntelChannelsText = intelChannels.Count == 0 ? "none" : string.Join(", ", intelChannels);
        IntelDetail       = _intel.StatusText;
        IntelBacklogText  = _intel.Backlog > 0
            ? $"{_intel.Backlog:N0} message(s) still to consider"
            : "Caught up";

        NameCacheState = _nameCache.StatusText;

        // Structures. The work is a side task of the polling loop rather than a declared
        // endpoint, so without this it appears in neither the call schedule nor the activity log
        // except during the brief bursts when it is actually resolving.
        StructureState = _polling.StructureSweepRunning
            ? "● Sweeping — resolving structures now"
            : "● Watching — sweeps hourly, public list daily";

        StructureSweepText = _polling.StructureSweepAt is { } at
            ? $"{at.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "not yet this session";

        var next = _polling.StructureSweepNextAt;
        StructureNextText = next <= DateTimeOffset.UtcNow
            ? "due now"
            : $"{next.ToLocalTime():HH:mm:ss} ({(next - DateTimeOffset.UtcNow).TotalMinutes:N0} min)";

        StructureCountsText   = _polling.StructureSweepSummary;
        PublicStructureText   = _polling.PublicStructureSummary;
        StructureSweepRunning = _polling.StructureSweepRunning;

        // Alarms. Nothing is defined out of the box, so "no alarms" is the normal resting
        // state rather than a fault.
        AlarmState = _alarms.ArmedCount == 0
            ? "○ No alarms armed — create one in the Alarms tool"
            : $"● Watching {_alarms.ArmedCount} alarm(s)";

        AlarmDetail   = _alarms.StatusText;
        AlarmNextText = _alarms.NextDueAt is { } due
            ? due.ToLocalTime().ToString("HH:mm:ss")
            : "—";
        AlarmLastFireText = _alarms.LastFireAt is { } fired
            ? fired.ToLocalTime().ToString("d MMM HH:mm:ss")
            : "Nothing has fired this session";
    }

    /// <summary>Hits the DB for the cached-name total, so it runs on the slower tick.</summary>
    public async Task RefreshNameCacheAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var total = await db.UniverseNames.CountAsync();
            NameCacheCountText = $"{total:N0} name(s) cached";
        }
        catch { /* best-effort monitor */ }
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

    // ── LP store monitor ────────────────────────────────────────────────────────

    private string _lpStoreState = "";
    public string LpStoreState { get => _lpStoreState; private set => this.RaiseAndSetIfChanged(ref _lpStoreState, value); }

    private string _lpStoreProgressText = "—";
    public string LpStoreProgressText { get => _lpStoreProgressText; private set => this.RaiseAndSetIfChanged(ref _lpStoreProgressText, value); }

    private string _lpStoreOffersText = "—";
    public string LpStoreOffersText { get => _lpStoreOffersText; private set => this.RaiseAndSetIfChanged(ref _lpStoreOffersText, value); }

    private string _lpStoreLastText = "—";
    public string LpStoreLastText { get => _lpStoreLastText; private set => this.RaiseAndSetIfChanged(ref _lpStoreLastText, value); }

    private string _lpStoreDetail = "";
    public string LpStoreDetail { get => _lpStoreDetail; private set => this.RaiseAndSetIfChanged(ref _lpStoreDetail, value); }

    public async Task RefreshLpStoreAsync()
    {
        LpStoreService.LpStoreStatus s;
        try { s = await _lpStore.GetStatusAsync(); }
        catch { return; /* best-effort monitor */ }

        int remaining = Math.Max(0, s.CorpsTotal - s.CorpsChecked);

        LpStoreProgressText = $"{s.CorpsChecked:N0} / {s.CorpsTotal:N0} corporations checked · {remaining:N0} to go";
        LpStoreOffersText   = $"{s.Offers:N0} offer(s) from {s.CorpsWithStore:N0} store(s)";
        LpStoreLastText     = s.LastCheckedAt is { } t
            ? t.ToLocalTime().ToString("d MMM HH:mm:ss")
            : "never";
        LpStoreDetail       = _lpStore.StatusText;

        // The first pass is the one worth watching: until it finishes, an item with no LP
        // tab is indistinguishable from one that simply has not been fetched yet.
        LpStoreState = s.Running
            ? $"● Sweeping — {remaining:N0} corporation(s) still to check"
            : s.CorpsChecked == 0
                ? "○ Idle — no corporation checked yet; the first sweep starts shortly after launch"
                : remaining > 0
                    ? $"○ Idle — {remaining:N0} corporation(s) not yet checked, catalogue incomplete"
                    : "○ Idle — every corporation checked";
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
