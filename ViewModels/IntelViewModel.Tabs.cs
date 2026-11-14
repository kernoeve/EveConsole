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
/// coloured again from each snapshot without reading anything. The standings chips filter it as they filter the
/// Board: by the tier the row's nearest character gives, before the cap (the read pages on until the cap is filled
/// with pilots shown), and again on each snapshot.</para>
///
/// <para>Camps are the snapshot's, through the lens: active within the camp lifetime of the last kill, then
/// faded as ended until an hour after it.</para>
/// </summary>
public sealed partial class IntelViewModel
{
    private const string KeyPilotsWindow = "intel.pilots_window", KeyPilotsSort = "intel.pilots_sort";

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
        var sort = Read(() => UiState.Get(KeyPilotsSort), null)?.Split(',');
        if (sort is [var column, var direction] && Enum.TryParse<PilotColumn>(column, out var kept) && Enum.IsDefined(kept))
            (_pilotSort, _pilotSortDescending) = (kept, direction == "desc");
        PilotHeaders = [.. Enum.GetValues<PilotColumn>().Select(c => new IntelSortHeaderVm(c, PilotColumnName(c), SortPilotsBy))];
        foreach (var h in PilotHeaders) h.Show(_pilotSort, _pilotSortDescending);
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

    /// <summary>A tab came into view: Pilots shows what came while it was hidden and reads if what it has is old, the
    /// Watch list reads.</summary>
    private void TabShown()
    {
        if (IsPilotsTab && _pilotsStale) ShowPilots(_snapshot, Clock());
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

    /// <summary>Reads the Pilots tab's rows, off the UI thread; a newer read started meanwhile wins. The standings
    /// chips filter as it reads, so the cap counts the pilots shown.</summary>
    public void QueryPilots()
    {
        if (_dbFactory is not { } factory) return;
        var ticket = Interlocked.Increment(ref _pilotQuery);
        var text = SearchText;
        var now = Clock();
        var since = now - TimeSpan.FromHours(_selectedPilotWindow.Value);
        // Built here from immutable parts (the snapshot's standings, the cached walks, a copy of the tiers): safe to
        // call from the thread that reads.
        var (filter, _) = PilotFilterFor(_snapshot);
        PilotsBusy = true;
        _ = Task.Run(async () =>
        {
            await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
            return await IntelPilotSearch.SearchAsync(db, text, since, filter.Judge).ConfigureAwait(false);
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

    /// <summary>The Pilots tab's measure and filter, from the snapshot and the walks cached now: the Board's rule
    /// for jumps and standings. A walk not cached is asked for, and the rows wait for it with "…".</summary>
    private (IntelPilotFilter Filter, bool Pending) PilotFilterFor(IntelSnapshot snapshot)
    {
        var mode = _useBridges ? IntelGraphMode.Travel : IntelGraphMode.Threat;
        var walks = new List<(IntelOrigin Origin, IReadOnlyDictionary<int, IntelHop> Hops)>();
        var pending = false;
        foreach (var o in Origins(snapshot).Distinct())
        {
            if (_distances?.TryCached(o.SystemId, mode, snapshot.Standings.BridgeAlliance(o.CharacterId)) is { } hops) walks.Add((o, hops));
            else if (_distances is not null) { pending = true; Walk(o, mode, snapshot); }
        }
        return (new IntelPilotFilter(snapshot.Standings, walks, Tiers), pending);
    }

    /// <summary>More rows changed than this, and the list is reset once rather than each row replaced.</summary>
    private const int ManyChanged = 40;

    private long _pilotShow;
    private bool _pilotsStale;
    private Task _pilotsShown = Task.CompletedTask;

    /// <summary>The newest showing of the Pilots tab's rows, done once they are on the list. A check runs the UI
    /// thread's work until it is done; nothing ever blocks on it.</summary>
    public Task PilotsShown => _pilotsShown;

    /// <summary>
    /// The rows read, measured from the top bar's origin, filtered by the standings chips, coloured and sorted, from
    /// the snapshot as it is now. Only while the tab is on screen: hidden, it is marked to do when shown.
    ///
    /// <para>⚠️ The rows are built, filtered and sorted off the UI thread (from immutable parts and a copy of what
    /// is shown), and the newest showing asked for wins. On the UI thread only the swap: rows reading the same are
    /// the ones already on screen, pictures and all, so a refresh that changed nothing changes nothing on screen, a
    /// changed row is replaced alone, and only new rows fetch pictures. The list is virtualized (the view), so even
    /// a new order builds only the rows in sight. Before, every refresh and every header click rebuilt all 500 rows
    /// on the UI thread: two to three seconds each, and clicks made meanwhile queued up behind it.</para>
    /// </summary>
    private void ShowPilots(IntelSnapshot snapshot, DateTimeOffset now)
    {
        if (!IsPilotsTab)
        {
            _pilotsStale = true;
            return;
        }
        _pilotsStale = false;

        var (filter, pending) = PilotFilterFor(snapshot);
        var results = _pilotResults;
        var held = PilotRows.ToDictionary(r => r.CharacterId);
        var (sort, descending) = (_pilotSort, _pilotSortDescending);
        var actions = Actions;
        var ticket = ++_pilotShow;
        var built = Task.Run(() => BuildPilots(results, filter, snapshot, held, pending, actions, sort, descending, now));
        _pilotsShown = built.ContinueWith(t => Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ticket != _pilotShow) return;
            if (t.IsFaulted)
            {
                PilotsEmptyText = string.Format(CultureInfo.CurrentCulture, IntelText.PilotsFailed, t.Exception?.GetBaseException().Message);
                return;
            }
            var (rows, hidden) = t.Result;
            // A few rows changed: each replaced alone. Many (the origin moved, every jump count with it): one reset is
            // cheaper than hundreds of replacements.
            var changed = rows.Count != PilotRows.Count ? rows.Count : rows.Where((r, i) => !ReferenceEquals(r, PilotRows[i])).Count();
            if (changed > ManyChanged) PilotRows.ResetTo(rows);
            else PilotRows.SyncTo(rows, r => r.CharacterId, r => r.Signature);
            foreach (var row in rows.Where(r => !held.TryGetValue(r.CharacterId, out var old) || !ReferenceEquals(old, r)))
                _ = row.LoadPicturesAsync();

            var culture = CultureInfo.CurrentCulture;
            var count = results.Capped ? string.Format(culture, IntelText.PilotsCapped, IntelPilotSearch.MaxRows)
                      : Plurals.Format(IntelText.ResourceManager, nameof(IntelText.PilotsCountOther), rows.Count);
            var hiddenAll = results.Hidden + hidden;
            PilotsCountText = hiddenAll == 0 ? count
                : count + " · " + Plurals.Format(IntelText.ResourceManager, nameof(IntelText.PilotsHiddenOther), hiddenAll);
            PilotsEmptyText = rows.Count > 0 || PilotsBusy ? ""
                            : hiddenAll > 0 ? IntelText.PilotsAllHidden
                            : SearchText.Trim().Length > 0 ? IntelText.ToolNothingMatches : IntelText.PilotsEmpty;
        }).GetTask(), TaskScheduler.Default).Unwrap();
    }

    /// <summary>The rows to show, off the UI thread: each measured and given its tier, those of a tier switched off
    /// left out (counted), the user's own characters left out (not counted), a row reading as one already shown kept, then sorted.</summary>
    private static (List<IntelPilotListRowVm> Rows, int Hidden) BuildPilots(
        IntelPilotResults results, IntelPilotFilter filter, IntelSnapshot snapshot, IReadOnlyDictionary<long, IntelPilotListRowVm> held,
        bool pending, IntelRowActions actions, PilotColumn sort, bool descending, DateTimeOffset now)
    {
        var rows = new List<IntelPilotListRowVm>(results.Rows.Count);
        var hidden = 0;
        foreach (var r in results.Rows)
        {
            var (jumps, via, from, tier) = filter.Place(r);
            // The user's own characters: left out, not counted (no chip brings them back).
            var verdict = filter.Judge(tier);
            if (verdict == IntelPilotVerdict.Own) continue;
            if (verdict == IntelPilotVerdict.Hidden) { hidden++; continue; }
            var row = new IntelPilotListRowVm(r, results.Names, snapshot, jumps, via, from, pending, actions, now);
            rows.Add(held.TryGetValue(r.CharacterId, out var old) && old.Signature == row.Signature ? old : row);
        }
        return ([.. SortPilots(rows, sort, descending)], hidden);
    }

    /// <summary>What the Pilots tab reads by changed: the standings chips, where distances count from, the bridges.
    /// The read again fills the cap under the new rule (blues switched back on come back, not only those still
    /// read); meanwhile the rows already read are filtered at once.</summary>
    private void PilotsFilterChanged()
    {
        if (_onScreen && IsPilotsTab && !_pilotsGiven) QueryPilots();
    }

    // ── Pilots: the sort ─────────────────────────────────────────────────────

    /// <summary>A column of the Pilots tab, each sortable by clicking its header.</summary>
    public enum PilotColumn { Jumps, Name, System, Seen, Ship, How, Corporation, Alliance }

    private PilotColumn _pilotSort = PilotColumn.Seen;
    private bool _pilotSortDescending = true;

    /// <summary>The Pilots tab's headers, in <see cref="PilotColumn"/> order: each sorts by its column.</summary>
    public IReadOnlyList<IntelSortHeaderVm> PilotHeaders { get; private set; } = [];

    // Each header by name: a compiled binding reads a property, not an index into a read-only list.
    public IntelSortHeaderVm HeadJumps       => PilotHeaders[(int)PilotColumn.Jumps];
    public IntelSortHeaderVm HeadName        => PilotHeaders[(int)PilotColumn.Name];
    public IntelSortHeaderVm HeadSystem      => PilotHeaders[(int)PilotColumn.System];
    public IntelSortHeaderVm HeadSeen        => PilotHeaders[(int)PilotColumn.Seen];
    public IntelSortHeaderVm HeadShip        => PilotHeaders[(int)PilotColumn.Ship];
    public IntelSortHeaderVm HeadHow         => PilotHeaders[(int)PilotColumn.How];
    public IntelSortHeaderVm HeadCorporation => PilotHeaders[(int)PilotColumn.Corporation];
    public IntelSortHeaderVm HeadAlliance    => PilotHeaders[(int)PilotColumn.Alliance];

    public PilotColumn PilotSort => _pilotSort;
    public bool PilotSortDescending => _pilotSortDescending;

    private static string PilotColumnName(PilotColumn column) => column switch
    {
        PilotColumn.Jumps       => IntelText.ColJumps,
        PilotColumn.Name        => IntelText.ColPilot,
        PilotColumn.System      => IntelText.ColLastSeenIn,
        PilotColumn.Seen        => IntelText.ColSeen,
        PilotColumn.Ship        => IntelText.ColHull,
        PilotColumn.How         => IntelText.ColHow,
        PilotColumn.Corporation => IntelText.ColCorporation,
        _                       => IntelText.ColAlliance,
    };

    /// <summary>A header clicked: its column, or the other way round when it already sorts by it. The arrow turns at
    /// once; the rows are sorted off the UI thread and swapped in, the same rows, with nothing read again.</summary>
    public void SortPilotsBy(PilotColumn column)
    {
        (_pilotSort, _pilotSortDescending) = NextPilotSort(_pilotSort, _pilotSortDescending, column);
        var (sort, descending) = (_pilotSort, _pilotSortDescending);
        Save(() => UiState.Set(KeyPilotsSort, sort + "," + (descending ? "desc" : "asc")));
        foreach (var h in PilotHeaders) h.Show(sort, descending);
        ShowPilots(_snapshot, Clock());
    }

    /// <summary>The sort after a header is clicked: the same column turns round; another starts newest first for
    /// when they were seen, nearest first for jumps, and A to Z for the rest.</summary>
    public static (PilotColumn Column, bool Descending) NextPilotSort(PilotColumn current, bool descending, PilotColumn clicked) =>
        clicked == current ? (current, !descending) : (clicked, clicked == PilotColumn.Seen);

    /// <summary>
    /// The Pilots tab's rows in a column's order. A blank (no hull, no alliance, jumps not known) goes last either
    /// way round; rows alike in the column keep newest first.
    /// </summary>
    public static IEnumerable<IntelPilotListRowVm> SortPilots(IEnumerable<IntelPilotListRowVm> rows, PilotColumn column, bool descending)
    {
        var text = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<IntelPilotListRowVm> ByText(Func<IntelPilotListRowVm, string?> key)
        {
            var blankLast = rows.OrderBy(r => string.IsNullOrEmpty(key(r)) ? 1 : 0);
            return descending ? blankLast.ThenByDescending(r => key(r) ?? "", text) : blankLast.ThenBy(r => key(r) ?? "", text);
        }
        var sorted = column switch
        {
            PilotColumn.Seen => descending ? rows.OrderByDescending(r => r.SeenAt) : rows.OrderBy(r => r.SeenAt),
            PilotColumn.Jumps => descending
                ? rows.OrderBy(r => r.Jumps is null ? 1 : 0).ThenByDescending(r => r.Jumps ?? 0)
                : rows.OrderBy(r => r.Jumps is null ? 1 : 0).ThenBy(r => r.Jumps ?? 0),
            PilotColumn.Name        => ByText(r => r.Name),
            PilotColumn.System      => ByText(r => r.SystemName),
            PilotColumn.Ship        => ByText(r => r.Hull?.Text),
            PilotColumn.How         => ByText(r => r.SourceText),
            PilotColumn.Corporation => ByText(r => r.Corporation?.Text),
            _                       => ByText(r => r.Alliance?.Text),
        };
        return sorted.ThenByDescending(r => r.SeenAt).ThenBy(r => r.CharacterId);
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
        var held = CampRows.DistinctBy(r => r.Key).ToDictionary(r => r.Key);
        var rows = camps
            .Select(c => new IntelCampRowVm(c, snapshot, pending, actions, now))
            .Where(r => search.Length == 0 || Matches(r.SystemName) || Matches(r.GateText) || Matches(_regions.GetValueOrDefault(r.SystemId)))
            .Select(r => held.TryGetValue(r.Key, out var old) && old.Signature == r.Signature ? old : r)
            .ToList();
        // Unchanged rows raise nothing; a changed one is replaced alone.
        CampRows.SyncTo(rows, r => r.Key, r => r.Signature);
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
