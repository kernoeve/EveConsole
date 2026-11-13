using System.Globalization;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.Services.Intel;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// The Intel tool's Pilots, Camps and Watch list tabs.
///
/// <para>Pilots is the one tab that reads intel itself: where each pilot was last seen goes back sixty days,
/// further than the snapshot's hour. It reads off the UI thread, when the tab is shown, when the window or
/// the search changes (300 ms after typing), and every half minute while on screen. Its rows are measured and
/// coloured again from each snapshot without reading anything.</para>
///
/// <para>Camps are the snapshot's, through the lens: active within the camp lifetime of the last kill, then
/// faded as ended until an hour after it.</para>
/// </summary>
public sealed partial class IntelViewModel
{
    private const string KeyPilotsWindow = "intel.pilots_window";

    /// <summary>How often the Pilots tab reads again while it is on screen.</summary>
    private static readonly TimeSpan PilotsEvery = TimeSpan.FromSeconds(30);

    private readonly IntelWatchList? _watchList;
    private IntelPilotResults _pilotResults = IntelPilotResults.Empty;
    private DateTimeOffset _pilotsReadAt = DateTimeOffset.MinValue;
    private long _pilotQuery;
    private bool _pilotsGiven;

    private void InitTabs()
    {
        var hours = (int)Read(() => UiState.GetLong(KeyPilotsWindow, 24), 24L);
        _selectedPilotWindow = PilotWindowChoices.FirstOrDefault(c => c.Value == hours) ?? PilotWindowChoices[2];
        WatchList = new IntelWatchTabVm(_watchList, _dbFactory);

        // The search box, on the Pilots tab, searches the database: once typing pauses.
        this.WhenAnyValue(x => x.SearchText).Skip(1).Throttle(TimeSpan.FromMilliseconds(300))
            .Subscribe(_ => Dispatcher.UIThread.Post(() => { if (_onScreen && IsPilotsTab) QueryPilots(); }));
    }

    private void TabsOnScreen(bool onScreen)
    {
        if (_watchList is not null)
        {
            _watchList.Changed -= OnWatchChanged;
            if (onScreen) _watchList.Changed += OnWatchChanged;
        }
        if (onScreen) TabShown();
    }

    private void OnWatchChanged() => Dispatcher.UIThread.Post(() => { if (_onScreen && IsWatchTab) WatchList.Load(); });

    /// <summary>A tab came into view: Pilots reads if what it has is old, the Watch list reads.</summary>
    private void TabShown()
    {
        if (!_onScreen) return;
        if (IsPilotsTab && !_pilotsGiven && Clock() - _pilotsReadAt >= PilotsEvery) QueryPilots();
        if (IsWatchTab) WatchList.Load();
    }

    private void TickTabs(DateTimeOffset now)
    {
        foreach (var row in PilotRows) row.Tick(now);
        foreach (var row in CampRows) row.Tick(now);
        if (_onScreen && IsPilotsTab && !_pilotsGiven && !PilotsBusy && now - _pilotsReadAt >= PilotsEvery) QueryPilots();
    }

    // ── Pilots ───────────────────────────────────────────────────────────────

    public IReadOnlyList<IntelChoice<int>> PilotWindowChoices { get; } =
    [
        new(1, IntelText.Window1h),
        new(6, IntelText.Window6h),
        new(24, IntelText.Window24h),
        new(24 * 7, IntelText.Window7d),
        new(24 * 60, IntelText.Window60d),
    ];

    private IntelChoice<int> _selectedPilotWindow = null!;
    /// <summary>How far back the Pilots tab looks: 1 h, 6 h, 24 h (the default), 7 d, 60 d.</summary>
    public IntelChoice<int> SelectedPilotWindow
    {
        get => _selectedPilotWindow;
        set
        {
            if (value is null || value == _selectedPilotWindow) return;
            this.RaiseAndSetIfChanged(ref _selectedPilotWindow, value);
            Save(() => UiState.SetLong(KeyPilotsWindow, value.Value));
            if (_onScreen && IsPilotsTab) QueryPilots();
        }
    }

    public BulkObservableCollection<IntelPilotListRowVm> PilotRows { get; } = [];

    private string _pilotsCount = "", _pilotsEmpty = "";
    public string PilotsCountText { get => _pilotsCount; private set => this.RaiseAndSetIfChanged(ref _pilotsCount, value); }
    public string PilotsEmptyText { get => _pilotsEmpty; private set => this.RaiseAndSetIfChanged(ref _pilotsEmpty, value); }

    private bool _pilotsBusy;
    public bool PilotsBusy { get => _pilotsBusy; private set => this.RaiseAndSetIfChanged(ref _pilotsBusy, value); }

    /// <summary>Reads the Pilots tab's rows, off the UI thread; a newer read started meanwhile wins.</summary>
    public void QueryPilots()
    {
        if (_dbFactory is not { } factory) return;
        var ticket = Interlocked.Increment(ref _pilotQuery);
        var text = SearchText;
        var now = Clock();
        var since = now - TimeSpan.FromHours(_selectedPilotWindow.Value);
        PilotsBusy = true;
        _ = Task.Run(async () =>
        {
            await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
            return await IntelPilotSearch.SearchAsync(db, text, since).ConfigureAwait(false);
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (Interlocked.Read(ref _pilotQuery) != ticket) return;
            PilotsBusy = false;
            _pilotsReadAt = now;
            if (t.IsFaulted)
            {
                PilotsEmptyText = string.Format(CultureInfo.CurrentCulture, IntelText.PilotsFailed, t.Exception?.GetBaseException().Message);
                return;
            }
            _pilotResults = t.Result;
            ShowPilots(_snapshot, Clock());
        }), TaskScheduler.Default);
    }

    /// <summary>Rows given rather than read: for a check with invented pilots.</summary>
    public void UsePilots(IntelPilotResults results)
    {
        _pilotsGiven = true;
        _pilotResults = results;
        _pilotsReadAt = Clock();
        ShowPilots(_snapshot, Clock());
    }

    /// <summary>The rows read, measured from the top bar's origin and coloured, from the snapshot as it is now.</summary>
    private void ShowPilots(IntelSnapshot snapshot, DateTimeOffset now)
    {
        var origins = Origins(snapshot);
        var mode = _useBridges ? IntelGraphMode.Travel : IntelGraphMode.Threat;
        var walks = new List<(IntelOrigin Origin, IReadOnlyDictionary<int, IntelHop> Hops)>();
        var pending = false;
        foreach (var o in origins.Distinct())
        {
            if (_distances?.TryCached(o.SystemId, mode, snapshot.Standings.BridgeAlliance(o.CharacterId)) is { } hops) walks.Add((o, hops));
            else if (_distances is not null) { pending = true; Walk(o, mode, snapshot); }
        }

        var actions = Actions;
        var held = PilotRows.ToDictionary(r => r.CharacterId);
        var rows = new List<IntelPilotListRowVm>();
        foreach (var r in _pilotResults.Rows)
        {
            int? best = null; var via = false; long? from = null;
            foreach (var (o, hops) in walks)
            {
                if (!hops.TryGetValue(r.SystemId, out var h)) continue;
                if (best is null || h.Jumps < best || (h.Jumps == best && via && !h.ViaWormhole))
                    (best, via, from) = (h.Jumps, h.ViaWormhole, o.CharacterId > 0 ? o.CharacterId : null);
            }
            var row = new IntelPilotListRowVm(r, _pilotResults.Names, snapshot, best, via, from, pending, actions, now);
            rows.Add(held.TryGetValue(r.CharacterId, out var old) && old.Signature == row.Signature ? old : row);
        }
        PilotRows.ResetTo(rows);
        foreach (var row in rows.Where(r => !held.TryGetValue(r.CharacterId, out var old) || !ReferenceEquals(old, r)))
            _ = row.LoadPicturesAsync();

        PilotsCountText = rows.Count >= IntelPilotSearch.MaxRows
            ? string.Format(CultureInfo.CurrentCulture, IntelText.PilotsCapped, IntelPilotSearch.MaxRows)
            : Plurals.Format(IntelText.ResourceManager, nameof(IntelText.PilotsCountOther), rows.Count);
        PilotsEmptyText = rows.Count > 0 || PilotsBusy ? ""
                        : SearchText.Trim().Length > 0 ? IntelText.ToolNothingMatches : IntelText.PilotsEmpty;
    }

    // ── Camps ────────────────────────────────────────────────────────────────

    public BulkObservableCollection<IntelCampRowVm> CampRows { get; } = [];

    private string _campsCount = "", _campsEmpty = "";
    public string CampsCountText { get => _campsCount; private set => this.RaiseAndSetIfChanged(ref _campsCount, value); }
    public string CampsEmptyText { get => _campsEmpty; private set => this.RaiseAndSetIfChanged(ref _campsEmpty, value); }

    private bool _campsNeedFeed;
    /// <summary>Nothing to show, and the full kill feed is off: the empty text says why, with the link.</summary>
    public bool CampsNeedFeed { get => _campsNeedFeed; private set => this.RaiseAndSetIfChanged(ref _campsNeedFeed, value); }

    private void ShowCamps(IntelSnapshot snapshot, IReadOnlyList<IntelCampRow> camps, DateTimeOffset now, bool pending)
    {
        var search = _searchText.Trim();
        var actions = Actions;
        var held = CampRows.ToDictionary(r => r.Key);
        var rows = camps
            .Select(c => new IntelCampRowVm(c, snapshot, pending, actions, now))
            .Where(r => search.Length == 0 || Matches(r.SystemName) || Matches(r.GateText) || Matches(_regions.GetValueOrDefault(r.SystemId)))
            .Select(r => held.TryGetValue(r.Key, out var old) && old.Signature == r.Signature ? old : r)
            .ToList();
        CampRows.ResetTo(rows);
        foreach (var row in rows.Where(r => !held.TryGetValue(r.Key, out var old) || !ReferenceEquals(old, r)))
            _ = row.LoadPicturesAsync();

        var active = rows.Count(r => r.IsActive);
        CampsCountText = Plurals.Format(IntelText.ResourceManager, nameof(IntelText.CampsActiveOther), active) + " · "
                       + Plurals.Format(IntelText.ResourceManager, nameof(IntelText.CampsEndedOther), rows.Count - active);
        CampsNeedFeed = rows.Count == 0 && search.Length == 0 && !snapshot.FeedStatus.FullFeed;
        CampsEmptyText = rows.Count > 0 ? ""
                       : search.Length > 0 ? IntelText.ToolNothingMatches
                       : CampsNeedFeed ? IntelText.CampsEmptyNoFeed
                       : IntelText.CampsEmpty;
    }

    // ── Watch list ───────────────────────────────────────────────────────────

    public IntelWatchTabVm WatchList { get; private set; } = null!;

    /// <summary>The watch list itself, for "Always alert" and "Ignore" on a pilot row; null in a check.</summary>
    public IntelWatchList? Watch => _watchList;
}
