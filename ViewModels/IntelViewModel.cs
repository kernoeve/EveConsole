using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using Avalonia.Media;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Intel;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A choice in one of the top bar's dropdowns: what it means, and what it says.</summary>
public sealed record IntelChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One source the Intel tool can show or hide: a channel, the kills, or the pastes.</summary>
public sealed class IntelSourceChoice(string key, string label, bool isOn, Action changed) : ReactiveObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;

    private bool _isOn = isOn;
    public bool IsOn
    {
        get => _isOn;
        set { if (_isOn == value) return; this.RaiseAndSetIfChanged(ref _isOn, value); changed(); }
    }
}

/// <summary>
/// The Intel tool: live intel from every intel channel, the kill feed and pastes, measured from the
/// user's characters online. Not tied to one character, unlike a panel: each row is measured from the
/// nearest of them, or from the one chosen, and coloured by that character's standings.
///
/// <para>It reads nothing of intel itself. The shared snapshot (<see cref="IntelSnapshotService"/>) is
/// projected through <see cref="IntelLens"/> with the top bar's filters, off the UI thread, while the
/// tool is on screen; a one-second clock ages and fades the rows between snapshots. Its filters are
/// kept on this PC, and a copy of the tool opens with them.</para>
///
/// <para>Tabs: the Board (a row per system), the Feed (every line, newest first, with what has expired
/// or been replaced, faded), and Paste (Local or d-scan into a sighting).</para>
/// </summary>
public sealed class IntelViewModel : ReactiveObject, IDisposable
{
    public const int BoardCap = 200;
    public const int FeedCap = 500;

    public const int TabBoard = 0, TabFeed = 1, TabPaste = 2;

    private const string KeyTab = "intel.tab", KeyFrom = "intel.from", KeyMaxJumps = "intel.max_jumps",
        KeyBridges = "intel.bridges", KeyTiers = "intel.tiers", KeySourcesOff = "intel.sources_off",
        KeyFeedMinutes = "intel.feed_minutes", KeyFeedKills = "intel.feed_kills", KeyFeedChatter = "intel.feed_chatter",
        KeyBoardSort = "intel.board_sort";

    private readonly IntelSnapshotService? _snapshots;
    private readonly IntelDistanceService? _distances;
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly DispatcherTimer _clock;
    private IDisposable? _subscription;
    private IntelSnapshot _snapshot = IntelSnapshot.Empty;
    private long _projection;
    private bool _onScreen;
    private readonly HashSet<(int, IntelGraphMode, long)> _walking = [];
    private Dictionary<int, string> _regions = [];
    private bool _regionsRead;

    public IntelViewModel(IntelSnapshotService? snapshots, IntelDistanceService? distances, IntelPasteService? paste,
                          UniverseMapService? map, IDbContextFactory<AppDbContext>? dbFactory,
                          ObservableCollection<Character> characters)
    {
        _snapshots = snapshots;
        _distances = distances;
        _dbFactory = dbFactory;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        _clock.Stop();

        _selectedTab = (int)Math.Clamp(Read(() => UiState.GetLong(KeyTab, TabBoard), TabBoard), TabBoard, TabPaste);
        _fromId = Read(() => UiState.GetLong(KeyFrom, 0), 0L);
        var jumps = (int)Read(() => UiState.GetLong(KeyMaxJumps, 15), 15L);
        _selectedJumps = JumpsChoices.FirstOrDefault(c => (c.Value ?? 0) == jumps) ?? JumpsChoices[2];
        _useBridges = Read(() => UiState.GetBool(KeyBridges, false), false);
        var tiers = Read(() => UiState.Get(KeyTiers), null);
        var shown = tiers is null
            ? [.. StandingTiers.Hostile]
            : tiers.Split(',', StringSplitOptions.RemoveEmptyEntries)
                   .Select(t => int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (StandingTier?)n : null)
                   .Where(t => t is not null).Select(t => t!.Value).ToHashSet();
        _showRed = shown.Contains(StandingTier.Red);
        _showOrange = shown.Contains(StandingTier.Orange);
        _showNeutral = shown.Contains(StandingTier.Neutral);
        _showLightBlue = shown.Contains(StandingTier.LightBlue);
        _showBlue = shown.Contains(StandingTier.Blue);
        _sourcesOff = [.. (Read(() => UiState.Get(KeySourcesOff), null) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        var minutes = (int)Read(() => UiState.GetLong(KeyFeedMinutes, 60), 60L);
        _selectedWindow = WindowChoices.FirstOrDefault(c => c.Value == minutes) ?? WindowChoices[^1];
        _showKills = Read(() => UiState.GetBool(KeyFeedKills, true), true);
        _showChatter = Read(() => UiState.GetBool(KeyFeedChatter, false), false);
        var sort = (int)Read(() => UiState.GetLong(KeyBoardSort, 0), 0L);
        _selectedSort = SortChoices.FirstOrDefault(c => (int)c.Value == sort) ?? SortChoices[0];

        FromChoices.Add(new IntelChoice<long>(0, IntelText.FromNearest));
        _selectedFrom = FromChoices[0];

        Paste = new IntelPasteVm(this, paste, map, characters);

        DismissWarningCommand = ReactiveCommand.Create(DismissWarning);
        OpenKillMailsCommand = ReactiveCommand.Create(() => OpenSettingsTab?.Invoke(SettingsText.TabKillMails));
    }

    private static T Read<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static void Save(Action write)
    {
        try { write(); }
        catch { /* A view's state is a nicety: a setting that cannot be kept is simply not kept. */ }
    }

    // ── What the app gives it ────────────────────────────────────────────────

    /// <summary>Opens Settings at a tab, by its header: the warning's link to Kill Mails.</summary>
    public Action<string>? OpenSettingsTab { get; set; }

    /// <summary>Frames a system on the Universe Map.</summary>
    public Action<int>? ShowOnMap { get; set; }

    /// <summary>"Intel parsing: worker on …": who parses, as the title bar knows it.</summary>
    public Func<string>? WorkerLine { get; set; }

    /// <summary>The clock rows are aged by; a check sets its own.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private IntelRowActions Actions => new()
    {
        ShowOnMap = ShowOnMap,
    };

    // ── Lifetime ─────────────────────────────────────────────────────────────

    /// <summary>Reads intel while the tool is on screen, and nothing while it is not.</summary>
    public void SetOnScreen(bool onScreen)
    {
        if (onScreen == _onScreen) return;
        _onScreen = onScreen;
        if (onScreen)
        {
            if (_distances is not null) _distances.Changed += OnDistancesChanged;
            if (_snapshots is not null)
            {
                _snapshots.Changed += OnSnapshot;
                _subscription = _snapshots.Subscribe();
                if (_snapshots.Current.Version >= _snapshot.Version) _snapshot = _snapshots.Current;
            }
            _clock.Start();
            _ = ReadRegionsAsync();
            Paste.OnScreen();
            Project();
        }
        else
        {
            if (_distances is not null) _distances.Changed -= OnDistancesChanged;
            if (_snapshots is not null) _snapshots.Changed -= OnSnapshot;
            _subscription?.Dispose();
            _subscription = null;
            _clock.Stop();
        }
    }

    /// <summary>A copy closed: nothing read any more, and nothing left listening to the app's lists.</summary>
    public void Dispose()
    {
        SetOnScreen(false);
        Paste.Detach();
    }

    /// <summary>Shows a snapshot given rather than read: for a check with invented intel.</summary>
    public void Apply(IntelSnapshot snapshot)
    {
        _snapshot = snapshot;
        Project();
    }

    /// <summary>Region names given rather than read: for a check with invented systems.</summary>
    public void UseRegions(IReadOnlyDictionary<int, string> regions)
    {
        _regions = new Dictionary<int, string>(regions);
        _regionsRead = true;
    }

    public IntelSnapshot Snapshot => _snapshot;

    private void OnSnapshot(IntelSnapshot snapshot) => Dispatcher.UIThread.Post(() =>
    {
        if (!_onScreen || snapshot.Version < _snapshot.Version) return;
        _snapshot = snapshot;
        Project();
    });

    private void OnDistancesChanged() => Dispatcher.UIThread.Post(() => { if (_onScreen) Project(); });

    /// <summary>Every system's region, read once: the Board names it beside the system.</summary>
    private async Task ReadRegionsAsync()
    {
        if (_regionsRead || _dbFactory is null) return;
        _regionsRead = true;
        try
        {
            var regions = await Task.Run(async () =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var names = await db.SdeRegions.AsNoTracking().Select(r => new { r.RegionId, r.Name }).ToListAsync();
                var byId = names.ToDictionary(r => r.RegionId, r => SdeNames.Region(r.RegionId, r.Name));
                return (await db.SdeSolarSystems.AsNoTracking().Select(s => new { s.SolarSystemId, s.RegionId }).ToListAsync())
                    .Where(s => byId.ContainsKey(s.RegionId))
                    .ToDictionary(s => s.SolarSystemId, s => byId[s.RegionId]);
            });
            _regions = regions;
            if (_onScreen) Project();
        }
        catch
        {
            // Regions are a nicety beside the system's name; without them the column is blank.
        }
    }

    // ── Tabs ─────────────────────────────────────────────────────────────────

    private int _selectedTab;
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value < TabBoard || value > TabPaste) return;
            this.RaiseAndSetIfChanged(ref _selectedTab, value);
            this.RaisePropertyChanged(nameof(IsBoardTab));
            this.RaisePropertyChanged(nameof(IsFeedTab));
            this.RaisePropertyChanged(nameof(IsPasteTab));
            this.RaisePropertyChanged(nameof(ShowsFilters));
            Save(() => UiState.SetLong(KeyTab, value));
        }
    }

    public bool IsBoardTab => _selectedTab == TabBoard;
    public bool IsFeedTab  => _selectedTab == TabFeed;
    public bool IsPasteTab => _selectedTab == TabPaste;

    /// <summary>The top bar's filters: the Board and the Feed, not Paste.</summary>
    public bool ShowsFilters => _selectedTab != TabPaste;

    public IntelPasteVm Paste { get; }

    /// <summary>The Paste tab, with this character and system chosen: the panel's paste icon.</summary>
    public void ShowPaste(long? characterId, int? systemId)
    {
        SelectedTab = TabPaste;
        Paste.Choose(characterId, systemId);
    }

    /// <summary>The Board with a system's row opened: "Open in Intel tool" on a row elsewhere.</summary>
    public void ShowSystem(int systemId)
    {
        SelectedTab = TabBoard;
        _focusSystem = systemId;
        if (BoardRows.FirstOrDefault(r => r.SystemId == systemId) is { } row)
        {
            row.IsOpen = true;
            FocusRequested?.Invoke(row);
            _focusSystem = null;
        }
    }

    private int? _focusSystem;

    /// <summary>A row the view should bring into sight.</summary>
    public event Action<IntelBoardRowVm>? FocusRequested;

    // ── The top bar ──────────────────────────────────────────────────────────

    public ObservableCollection<IntelChoice<long>> FromChoices { get; } = [];

    private long _fromId;
    private IntelChoice<long>? _selectedFrom;
    /// <summary>Whom distances are counted from: the nearest online character (0), or one of them.</summary>
    public IntelChoice<long>? SelectedFrom
    {
        get => _selectedFrom;
        set
        {
            // A ComboBox reports null while its list is being changed; that isn't a choice.
            if (value is null || ReferenceEquals(value, _selectedFrom)) return;
            this.RaiseAndSetIfChanged(ref _selectedFrom, value);
            if (_fromId == value.Value) return;
            _fromId = value.Value;
            Save(() => UiState.SetLong(KeyFrom, value.Value));
            Paste.FromChanged();
            Project();
        }
    }

    /// <summary>The character chosen in "Distance from", or 0 for the nearest.</summary>
    public long FromCharacterId => _fromId;

    public IReadOnlyList<IntelChoice<int?>> JumpsChoices { get; } =
    [
        new(5, string.Format(CultureInfo.CurrentCulture, IntelText.WithinJumps, 5)),
        new(10, string.Format(CultureInfo.CurrentCulture, IntelText.WithinJumps, 10)),
        new(15, string.Format(CultureInfo.CurrentCulture, IntelText.WithinJumps, 15)),
        new(20, string.Format(CultureInfo.CurrentCulture, IntelText.WithinJumps, 20)),
        new(30, string.Format(CultureInfo.CurrentCulture, IntelText.WithinJumps, 30)),
        new(null, IntelText.WithinAll),
    ];

    private IntelChoice<int?> _selectedJumps;
    public IntelChoice<int?> SelectedJumps
    {
        get => _selectedJumps;
        set
        {
            if (value is null || value == _selectedJumps) return;
            this.RaiseAndSetIfChanged(ref _selectedJumps, value);
            Save(() => UiState.SetLong(KeyMaxJumps, value.Value ?? 0));
            Project();
        }
    }

    private bool _useBridges;
    /// <summary>Count the user's own jump bridges in the distances: how far for travel, not for a threat.</summary>
    public bool UseBridges
    {
        get => _useBridges;
        set
        {
            if (_useBridges == value) return;
            this.RaiseAndSetIfChanged(ref _useBridges, value);
            Save(() => UiState.SetBool(KeyBridges, value));
            Project();
        }
    }

    private bool _showRed, _showOrange, _showNeutral, _showLightBlue, _showBlue;
    public bool ShowRed       { get => _showRed;       set => SetTier(ref _showRed, value); }
    public bool ShowOrange    { get => _showOrange;    set => SetTier(ref _showOrange, value); }
    public bool ShowNeutral   { get => _showNeutral;   set => SetTier(ref _showNeutral, value); }
    public bool ShowLightBlue { get => _showLightBlue; set => SetTier(ref _showLightBlue, value); }
    public bool ShowBlue      { get => _showBlue;      set => SetTier(ref _showBlue, value); }

    private void SetTier(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        this.RaisePropertyChanged(name);
        var tiers = Tiers;
        Save(() => UiState.Set(KeyTiers, string.Join(",", tiers.Select(t => ((int)t).ToString(CultureInfo.InvariantCulture)))));
        Project();
    }

    private HashSet<StandingTier> Tiers
    {
        get
        {
            var tiers = new HashSet<StandingTier>();
            if (_showRed) tiers.Add(StandingTier.Red);
            if (_showOrange) tiers.Add(StandingTier.Orange);
            if (_showNeutral) tiers.Add(StandingTier.Neutral);
            if (_showLightBlue) tiers.Add(StandingTier.LightBlue);
            if (_showBlue) tiers.Add(StandingTier.Blue);
            return tiers;
        }
    }

    public static IBrush RedBrush       => IntelTierColors.Red;
    public static IBrush OrangeBrush    => IntelTierColors.Orange;
    public static IBrush NeutralBrush   => IntelTierColors.Neutral;
    public static IBrush LightBlueBrush => IntelTierColors.LightBlue;
    public static IBrush BlueBrush      => IntelTierColors.Blue;

    private readonly HashSet<string> _sourcesOff;

    /// <summary>Every channel the snapshot knows of, then the kills and the pastes.</summary>
    public ObservableCollection<IntelSourceChoice> Sources { get; } = [];

    private string _sourcesText = IntelText.SourcesAll;
    public string SourcesText { get => _sourcesText; private set => this.RaiseAndSetIfChanged(ref _sourcesText, value); }

    private void SourceChanged()
    {
        _sourcesOff.Clear();
        foreach (var s in Sources.Where(s => !s.IsOn)) _sourcesOff.Add(s.Key);
        Save(() => UiState.Set(KeySourcesOff, _sourcesOff.Count == 0 ? null : string.Join('\n', _sourcesOff)));
        UpdateSourcesText();
        Project();
    }

    private void UpdateSourcesText()
    {
        var on = Sources.Count(s => s.IsOn);
        SourcesText = on == Sources.Count ? IntelText.SourcesAll
                    : on == 0 ? IntelText.SourcesNone
                    : string.Format(CultureInfo.CurrentCulture, IntelText.SourcesSome, on, Sources.Count);
    }

    /// <summary>The channels the snapshot knows, kept in step without losing what was switched off.</summary>
    private void SyncSources(IntelSnapshot snapshot)
    {
        var channels = snapshot.Channels
            .Concat(snapshot.Feed.Where(e => e.SourceKey is not IntelSnapshot.SourceKills and not IntelSnapshot.SourcePaste).Select(e => e.SourceKey))
            .Concat(snapshot.Chatter.Select(c => c.Channel))
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var wanted = channels.Select(c => (Key: c, Label: c))
            .Append((Key: IntelSnapshot.SourceKills, Label: IntelText.SourceKills))
            .Append((Key: IntelSnapshot.SourcePaste, Label: IntelText.SourcePastes))
            .ToList();
        if (Sources.Select(s => s.Key).SequenceEqual(wanted.Select(w => w.Key))) return;
        Sources.Clear();
        foreach (var (key, label) in wanted)
            Sources.Add(new IntelSourceChoice(key, label, !_sourcesOff.Contains(key), SourceChanged));
        UpdateSourcesText();
    }

    private string _searchText = "";
    /// <summary>A system, region or pilot to look for: rows not matching are left out.</summary>
    public string SearchText
    {
        get => _searchText;
        set { if (_searchText == value) return; this.RaiseAndSetIfChanged(ref _searchText, value); Project(); }
    }

    // ── The kill-feed warning ────────────────────────────────────────────────

    private bool _showFeedWarning;
    /// <summary>The full zKillboard feed is off, and nobody has dismissed the warning.</summary>
    public bool ShowFeedWarning { get => _showFeedWarning; private set => this.RaiseAndSetIfChanged(ref _showFeedWarning, value); }

    private bool _dismissedHere;

    public ReactiveCommand<Unit, Unit> DismissWarningCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenKillMailsCommand { get; }

    private void DismissWarning()
    {
        // Hidden here at once; on every PC once the shared setting is read.
        _dismissedHere = true;
        ShowFeedWarning = false;
        if (_snapshots is not null) _ = _snapshots.DismissFeedWarningAsync();
    }

    // ── The status strip ─────────────────────────────────────────────────────

    private string _systemsText = "", _hostilesText = "", _campsText = "", _updatedText = "", _workerText = "";
    public string SystemsText  { get => _systemsText;  private set => this.RaiseAndSetIfChanged(ref _systemsText, value); }
    public string HostilesText { get => _hostilesText; private set => this.RaiseAndSetIfChanged(ref _hostilesText, value); }
    public string CampsText    { get => _campsText;    private set => this.RaiseAndSetIfChanged(ref _campsText, value); }
    public string UpdatedText  { get => _updatedText;  private set => this.RaiseAndSetIfChanged(ref _updatedText, value); }
    public string WorkerText   { get => _workerText;   private set => this.RaiseAndSetIfChanged(ref _workerText, value); }

    private string _note = "";
    /// <summary>Why the tool shows nothing, when it doesn't; or what it can't tell.</summary>
    public string Note { get => _note; private set { this.RaiseAndSetIfChanged(ref _note, value); this.RaisePropertyChanged(nameof(HasNote)); } }
    public bool HasNote => Note.Length > 0;

    // ── The Board ────────────────────────────────────────────────────────────

    public enum BoardSort { Nearest, Newest, MostHostiles }

    public IReadOnlyList<IntelChoice<BoardSort>> SortChoices { get; } =
    [
        new(BoardSort.Nearest, IntelText.SortNearest),
        new(BoardSort.Newest, IntelText.SortNewest),
        new(BoardSort.MostHostiles, IntelText.SortMostHostiles),
    ];

    private IntelChoice<BoardSort> _selectedSort;
    public IntelChoice<BoardSort> SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value is null || value == _selectedSort) return;
            this.RaiseAndSetIfChanged(ref _selectedSort, value);
            Save(() => UiState.SetLong(KeyBoardSort, (int)value.Value));
            Project();
        }
    }

    public BulkObservableCollection<IntelBoardRowVm> BoardRows { get; } = [];

    private string _boardMore = "";
    /// <summary>"12 more beyond the limit", when the Board was cut.</summary>
    public string BoardMoreText { get => _boardMore; private set => this.RaiseAndSetIfChanged(ref _boardMore, value); }

    private string _boardEmpty = "";
    public string BoardEmptyText { get => _boardEmpty; private set => this.RaiseAndSetIfChanged(ref _boardEmpty, value); }

    // ── The Feed ─────────────────────────────────────────────────────────────

    public IReadOnlyList<IntelChoice<int>> WindowChoices { get; } =
    [
        new(15, string.Format(CultureInfo.CurrentCulture, IntelText.LastMinutes, 15)),
        new(30, string.Format(CultureInfo.CurrentCulture, IntelText.LastMinutes, 30)),
        new(60, string.Format(CultureInfo.CurrentCulture, IntelText.LastMinutes, 60)),
    ];

    private IntelChoice<int> _selectedWindow;
    public IntelChoice<int> SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (value is null || value == _selectedWindow) return;
            this.RaiseAndSetIfChanged(ref _selectedWindow, value);
            Save(() => UiState.SetLong(KeyFeedMinutes, value.Value));
            Project();
        }
    }

    private bool _showKills;
    /// <summary>Kills in the Feed: off keeps a busy kill feed from drowning the reports.</summary>
    public bool ShowKills
    {
        get => _showKills;
        set { if (_showKills == value) return; this.RaiseAndSetIfChanged(ref _showKills, value); Save(() => UiState.SetBool(KeyFeedKills, value)); Project(); }
    }

    private bool _showChatter;
    /// <summary>The intel channels' lines that gave no intel, muted, for context.</summary>
    public bool ShowChatter
    {
        get => _showChatter;
        set { if (_showChatter == value) return; this.RaiseAndSetIfChanged(ref _showChatter, value); Save(() => UiState.SetBool(KeyFeedChatter, value)); Project(); }
    }

    /// <summary>The Feed's lines: <see cref="IntelFeedLineVm"/> and <see cref="IntelChatterLineVm"/>.</summary>
    public BulkObservableCollection<object> FeedRows { get; } = [];

    private string _feedCount = "";
    public string FeedCountText { get => _feedCount; private set => this.RaiseAndSetIfChanged(ref _feedCount, value); }

    private string _feedEmpty = "";
    public string FeedEmptyText { get => _feedEmpty; private set => this.RaiseAndSetIfChanged(ref _feedEmpty, value); }

    // ── Projection ───────────────────────────────────────────────────────────

    /// <summary>The own characters the distances are counted from, as the snapshot has them.</summary>
    private IReadOnlyList<IntelOrigin> Origins(IntelSnapshot snapshot) =>
        _fromId == 0
            ? [.. snapshot.Own.Select(o => new IntelOrigin(o.CharacterId, o.SystemId))]
            : [.. snapshot.Own.Where(o => o.CharacterId == _fromId).Select(o => new IntelOrigin(o.CharacterId, o.SystemId))];

    private void SyncFrom(IntelSnapshot snapshot)
    {
        var wanted = new List<IntelChoice<long>> { FromChoices[0] };
        foreach (var o in snapshot.Own) wanted.Add(new IntelChoice<long>(o.CharacterId, o.Name));
        if (_fromId != 0 && snapshot.Own.All(o => o.CharacterId != _fromId))
        {
            var name = snapshot.Standings.Own(_fromId)?.Name ?? _fromId.ToString(CultureInfo.InvariantCulture);
            wanted.Add(new IntelChoice<long>(_fromId, string.Format(CultureInfo.CurrentCulture, IntelText.FromOffline, name)));
        }
        if (!FromChoices.Select(c => (c.Value, c.Label)).SequenceEqual(wanted.Select(c => (c.Value, c.Label))))
        {
            for (var i = FromChoices.Count - 1; i >= 1; i--) FromChoices.RemoveAt(i);
            foreach (var c in wanted.Skip(1)) FromChoices.Add(c);
        }
        var chosen = FromChoices.FirstOrDefault(c => c.Value == _fromId) ?? FromChoices[0];
        if (!ReferenceEquals(chosen, _selectedFrom))
        {
            _selectedFrom = chosen;
            this.RaisePropertyChanged(nameof(SelectedFrom));
        }
    }

    /// <summary>
    /// The snapshot through the lens with the top bar's filters, twice (the Board's live systems and
    /// the Feed's window), off the UI thread; the rows are swapped in on it. A newer projection started
    /// meanwhile wins. A walk not cached yet is asked for, and the rows come again when it is done.
    /// </summary>
    private void Project()
    {
        var snapshot = _snapshot;
        SyncFrom(snapshot);
        SyncSources(snapshot);
        Paste.SnapshotChanged(snapshot);

        var origins = Origins(snapshot);
        var allowed = Sources.Where(s => s.IsOn).Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mode = _useBridges ? IntelGraphMode.Travel : IntelGraphMode.Threat;
        var tiers = Tiers;
        var board = new IntelViewer
        {
            Origins = origins, MaxJumps = _selectedJumps.Value, Mode = mode, Tiers = tiers, Sources = allowed,
            IncludeClear = true, IncludeKills = true, SystemCap = int.MaxValue, FeedCap = int.MaxValue,
        };
        var feed = board with
        {
            IncludeKills = _showKills, IncludeExpired = true, IncludeSuperseded = true,
            FeedWindow = TimeSpan.FromMinutes(_selectedWindow.Value), FeedCap = FeedCap,
        };
        var now = Clock();
        var ticket = Interlocked.Increment(ref _projection);
        var distances = _distances;
        _ = Task.Run(() => (Board: IntelLens.Project(snapshot, board, distances, now), Feed: IntelLens.Project(snapshot, feed, distances, now)))
            .ContinueWith(t =>
            {
                if (t.IsFaulted || Interlocked.Read(ref _projection) != ticket) return;
                var (boardView, feedView) = t.Result;
                Dispatcher.UIThread.Post(() =>
                {
                    if (Interlocked.Read(ref _projection) != ticket) return;
                    Show(snapshot, boardView, feedView, now);
                    foreach (var missing in boardView.MissingOrigins) Walk(missing, mode, snapshot);
                });
            }, TaskScheduler.Default);
    }

    private void Walk(IntelOrigin origin, IntelGraphMode mode, IntelSnapshot snapshot)
    {
        var alliance = snapshot.Standings.Own(origin.CharacterId)?.AllianceId is long a and > 0 ? a : (long?)null;
        if (_distances is not { } distances || !_walking.Add((origin.SystemId, mode, alliance ?? 0))) return;
        _ = Task.Run(async () =>
        {
            var walked = false;
            try { await distances.JumpsFromAsync(origin.SystemId, mode, alliance).ConfigureAwait(false); walked = true; }
            catch { /* The next snapshot asks again. */ }
            Dispatcher.UIThread.Post(() =>
            {
                _walking.Remove((origin.SystemId, mode, alliance ?? 0));
                if (walked && _onScreen) Project();
            });
        });
    }

    private bool Matches(string? text) =>
        _searchText.Length == 0 || (text?.Contains(_searchText.Trim(), StringComparison.CurrentCultureIgnoreCase) ?? false);

    private void Show(IntelSnapshot snapshot, IntelView boardView, IntelView feedView, DateTimeOffset now)
    {
        var origins = Origins(snapshot);
        var culture = CultureInfo.CurrentCulture;
        var pending = boardView.Pending;
        var actions = Actions;
        var search = _searchText.Trim();

        // ── The Board ──
        var eventsBySystem = boardView.Feed
            .SelectMany(r => r.FollowUps.Prepend(r))
            .GroupBy(r => r.Event.SystemId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Event.At).ToList());
        var rows = boardView.Systems
            .Where(s => search.Length == 0
                        || Matches(snapshot.SystemName(s.SystemId))
                        || Matches(_regions.GetValueOrDefault(s.SystemId))
                        || s.Pilots.Any(p => Matches(p.Pilot.Name)))
            .ToList();
        IEnumerable<IntelSystemRow> sorted = _selectedSort.Value switch
        {
            BoardSort.Newest => rows.OrderByDescending(r => r.NewestAt).ThenBy(r => r.SystemId),
            BoardSort.MostHostiles => rows.OrderByDescending(r => r.Pilots.Count + r.Unnamed)
                                          .ThenBy(r => r.Jumps is null ? 1 : 0).ThenBy(r => r.Jumps ?? 0).ThenByDescending(r => r.NewestAt),
            _ => rows,
        };
        var held = BoardRows.ToDictionary(r => r.SystemId);
        var board = sorted.Take(BoardCap)
            .Select(r =>
            {
                var fresh = new IntelBoardRowVm(r, snapshot, eventsBySystem.GetValueOrDefault(r.SystemId) ?? [],
                                                _regions.GetValueOrDefault(r.SystemId), now, pending, actions);
                if (held.TryGetValue(r.SystemId, out var old))
                {
                    if (old.Signature == fresh.Signature) return old;
                    fresh.IsOpen = old.IsOpen;
                }
                return fresh;
            })
            .ToList();
        BoardRows.ResetTo(board);
        foreach (var row in board.Where(r => !held.TryGetValue(r.SystemId, out var old) || !ReferenceEquals(old, r)))
            _ = row.LoadPicturesAsync();
        BoardMoreText = rows.Count > BoardCap
            ? string.Format(culture, IntelText.MoreBeyondLimit, rows.Count - BoardCap) : "";
        if (_focusSystem is int focus && board.FirstOrDefault(r => r.SystemId == focus) is { } focused)
        {
            focused.IsOpen = true;
            FocusRequested?.Invoke(focused);
            _focusSystem = null;
        }

        var live = boardView.Systems.Where(s => s.State != IntelSystemState.Clear).ToList();
        SystemsText = Plurals.Format(IntelText.ResourceManager, nameof(IntelText.StatusSystemsOther), live.Count);
        HostilesText = Plurals.Format(IntelText.ResourceManager, nameof(IntelText.StatusHostilesOther), live.Sum(s => s.Pilots.Count + s.Unnamed));
        CampsText = Plurals.Format(IntelText.ResourceManager, nameof(IntelText.StatusCampsOther), boardView.Systems.Sum(s => s.Camps.Count));

        // ── The Feed ──
        var heldLines = FeedRows.OfType<IntelFeedLineVm>().ToDictionary(l => l.Key);
        var lines = new List<(DateTimeOffset At, object Line)>();
        foreach (var r in feedView.Feed)
        {
            if (!FeedMatches(r, snapshot)) continue;
            var parent = Line(r, false);
            var kids = r.FollowUps.Select(f => Line(f, true)).ToList();
            lines.Add((r.Event.At, new IntelFeedGroup(parent, kids)));
        }
        if (_showChatter)
        {
            var since = now - TimeSpan.FromMinutes(_selectedWindow.Value);
            var allowed = Sources.Where(s => s.IsOn).Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in snapshot.Chatter.Where(c => c.At >= since && allowed.Contains(c.Channel)))
                if (search.Length == 0 || Matches(c.Message) || Matches(c.Sender))
                    lines.Add((c.At, new IntelChatterLineVm(c, now)));
        }
        var ordered = lines.OrderByDescending(l => l.At).Take(FeedCap).ToList();
        var flat = new List<object>();
        foreach (var (_, line) in ordered)
        {
            if (line is IntelFeedGroup g) { flat.Add(g.Parent); flat.AddRange(g.Children); }
            else flat.Add(line);
        }
        FeedRows.ResetTo(flat);
        foreach (var l in flat.OfType<IntelFeedLineVm>().Where(l => !heldLines.TryGetValue(l.Key, out var old) || !ReferenceEquals(old, l)))
            _ = l.Row.LoadPicturesAsync();
        FeedCountText = Plurals.Format(IntelText.ResourceManager, nameof(IntelText.FeedCountOther), flat.Count);

        IntelFeedLineVm Line(IntelFeedRow r, bool child)
        {
            var row = new IntelEventRowVm(r, snapshot, now, feedView.Pending, actions);
            var key = (child ? "f" : "") + row.Key;
            return heldLines.TryGetValue(key, out var old) && old.Row.Signature == row.Signature ? old : new IntelFeedLineVm(row, child);
        }

        // ── The warning and the notes ──
        // Dismissed here: hidden until the shared setting says so too, after which it follows that
        // (the full feed switched on clears it, and Settings → Intel can show it again).
        if (snapshot.FeedStatus.Dismissed || snapshot.FeedStatus.FullFeed) _dismissedHere = false;
        ShowFeedWarning = snapshot.Version > 0 && snapshot.FeedStatus.ShowWarning && !_dismissedHere;

        string note;
        if (snapshot.Version == 0 && _snapshots is not null) note = IntelText.PanelReading;
        else if (!snapshot.Settings.HasIntelChannels && snapshot.Channels.Count == 0) note = IntelText.ToolNoChannels;
        else if (snapshot.Own.Count == 0) note = IntelText.ToolNobodyOnline;
        else if (_fromId != 0 && origins.Count == 0) note = IntelText.ToolFromOffline;
        else note = "";
        Note = note;
        BoardEmptyText = board.Count > 0 ? "" : search.Length > 0 ? IntelText.ToolNothingMatches : IntelText.ToolBoardEmpty;
        FeedEmptyText = flat.Count > 0 ? "" : search.Length > 0 ? IntelText.ToolNothingMatches : IntelText.ToolFeedEmpty;

        Tick();
    }

    private bool FeedMatches(IntelFeedRow r, IntelSnapshot snapshot)
    {
        if (_searchText.Trim().Length == 0) return true;
        var e = r.Event;
        return Matches(snapshot.SystemName(e.SystemId)) || Matches(e.Message) || Matches(e.Reporter)
               || e.Pilots.Any(p => Matches(p.Name)) || Matches(e.VictimName)
               || r.FollowUps.Any(f => f.Event.Pilots.Any(p => Matches(p.Name)) || Matches(f.Event.Message));
    }

    private sealed record IntelFeedGroup(IntelFeedLineVm Parent, IReadOnlyList<IntelFeedLineVm> Children);

    private void Tick()
    {
        var now = Clock();
        var freshSeconds = _snapshot.Settings.FreshSeconds;
        foreach (var row in BoardRows) row.Tick(now, freshSeconds);
        foreach (var line in FeedRows)
        {
            if (line is IntelFeedLineVm l) l.Row.Tick(now, freshSeconds);
            else if (line is IntelChatterLineVm c) c.Tick(now);
        }
        UpdatedText = _snapshot.Version == 0 ? ""
                    : now - _snapshot.At < TimeSpan.FromSeconds(10) ? IntelText.StatusUpdatedNow
                    : string.Format(CultureInfo.CurrentCulture, IntelText.StatusUpdated, IntelWords.Age(_snapshot.At, now));
        WorkerText = WorkerLine?.Invoke() ?? "";
        Paste.Tick(now);
    }
}

/// <summary>
/// One system on the Intel tool's Board: how far, what state, how many, in what, what else was said,
/// which way they are moving, how long ago, from whom it is nearest, and where it was heard. Opened,
/// it lists the system's reports, follow-ups and kills, each as every intel view shows a line.
/// </summary>
public sealed class IntelBoardRowVm : ReactiveObject
{
    private const int MaxHulls = 3;
    private readonly DateTimeOffset _newest;
    private readonly IReadOnlyList<(DateTimeOffset At, TimeSpan Life)> _items;

    public IntelBoardRowVm(IntelSystemRow row, IntelSnapshot snapshot, IReadOnlyList<IntelFeedRow> events, string? region,
                           DateTimeOffset now, bool pending, IntelRowActions actions)
    {
        var culture = CultureInfo.CurrentCulture;
        Actions = actions;
        SystemId = row.SystemId;
        SystemName = snapshot.SystemName(row.SystemId);
        RegionText = region ?? "";
        JumpsText = IntelWords.Jumps(row.Jumps, pending);
        Band = row.Jumps is null && pending ? IntelJumpBand.Unknown : IntelWords.Band(row.Jumps);
        var nearest = row.NearestOrigin is long o ? snapshot.Own.FirstOrDefault(x => x.CharacterId == o) : null;
        NearestId = nearest?.CharacterId ?? 0;
        NearestName = nearest?.Name ?? "";
        JumpsTip = IntelWords.JumpsTip(row.Jumps, row.ViaWormhole, nearest?.Name);
        IsClear = row.State == IntelSystemState.Clear;
        TierBrush = IsClear ? null : IntelTierColors.Of(row.Tier);
        StandingsTip = IsClear ? null : IntelWords.StandingsTip(snapshot.Standings, row.StandingsFrom);

        var camp = row.Camps.FirstOrDefault();
        var count = row.Pilots.Count + row.Unnamed;
        HostileCount = count;
        (State, StateText) = row.State switch
        {
            IntelSystemState.Clear => (IntelRowState.Clear, IntelText.BoardClear),
            IntelSystemState.NoVisual => (IntelRowState.NoVisual, IntelText.BoardNoVisual),
            IntelSystemState.FlagsOnly when camp is not null => (IntelRowState.Camp, IntelText.BoardCamp),
            IntelSystemState.FlagsOnly => (IntelRowState.Flags, IntelText.BoardFlags),
            _ when camp is not null => (IntelRowState.Camp, IntelText.BoardCamp),
            _ => (IntelRowState.Hostile, IntelText.BoardHostile),
        };
        HostilesText = count > 0 ? count.ToString(culture) : IsClear ? "" : "–";

        // The hulls flown, most first, each with its icon: the named pilots', the camp's, then the ones said.
        var hulls = row.Pilots.Where(p => p.Pilot.Ship is { Length: > 0 })
            .GroupBy(p => (p.Pilot.ShipTypeId ?? 0, p.Pilot.Ship!))
            .Select(g => (g.Key.Item1, g.Key.Item2, Count: g.Count()))
            .ToList();
        foreach (var c in row.Camps)
            foreach (var g in c.Attackers.Where(a => a.ShipTypeId is > 0).GroupBy(a => a.ShipTypeId!.Value))
                if (hulls.All(h => h.Item1 != g.Key))
                    hulls.Add((g.Key, events.SelectMany(e => e.Event.Pilots).FirstOrDefault(p => p.ShipTypeId == g.Key)?.Ship
                                      ?? g.Key.ToString(CultureInfo.InvariantCulture), g.Count()));
        if (row.Intel.Unnamed is { Ships: { Length: > 0 } said } && row.Unnamed > 0)
            foreach (var (n, name) in IntelDisplay.ParseShips(said))
                if (hulls.All(h => !string.Equals(h.Item2, name, StringComparison.OrdinalIgnoreCase)))
                    hulls.Add((0, name, n));
        var ordered = hulls.OrderByDescending(h => h.Count).ThenBy(h => h.Item2, StringComparer.CurrentCulture).ToList();
        Hulls = [.. ordered.Take(MaxHulls).Select(h => IntelPictureVm.Ship(h.Item1, h.Item2, h.Count))];
        HullsMore = ordered.Count > MaxHulls ? "+" + (ordered.Count - MaxHulls).ToString(culture) : "";

        var flags = row.Facts.Aggregate(0, (f, x) => f | (int)x.Flag);
        if (camp is not null) flags |= (int)Monitoring.IntelRules.IntelFlags.Camp;
        FlagsText = IntelWords.Flags(flags, camp);

        var moving = new List<string>();
        if (row.Intel.Moves.FirstOrDefault() is { } move && now - move.At <= TimeSpan.FromMinutes(30))
            moving.Add(string.Format(culture, IntelText.MovingFrom, snapshot.SystemName(move.FromSystemId)));
        if (row.HeadingSystemId is int heading && heading != row.SystemId)
            moving.Add(string.Format(culture, IntelText.MovingHeading, snapshot.SystemName(heading)));
        MovingText = string.Join(", ", moving);

        // Where it was heard: the channels, then the kills and the pastes.
        var sources = events.Select(e => e.Event.SourceKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var words = sources.Where(s => s is not IntelSnapshot.SourceKills and not IntelSnapshot.SourcePaste)
            .Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (sources.Contains(IntelSnapshot.SourceKills) || row.Camps.Count > 0) words.Add(IntelText.SourceKillsShort);
        if (sources.Contains(IntelSnapshot.SourcePaste)) words.Add(IntelText.SourcePasteShort);
        SourcesText = string.Join(", ", words);

        Events = [.. events.Select(e => new IntelEventRowVm(e, snapshot, now, pending, actions))];
        OnePilotId = row.Pilots.Count == 1 ? row.Pilots[0].Pilot.CharacterId : 0;

        _newest = row.NewestAt;
        NewestAt = row.NewestAt;
        var items = new List<(DateTimeOffset, TimeSpan)>();
        items.AddRange(row.Pilots.Select(p => (p.Pilot.At, p.Pilot.Lifetime)));
        if (row.Unnamed > 0 && row.Intel.Unnamed is { } u) items.Add((u.At, u.Lifetime));
        items.AddRange(row.Facts.Select(f => (f.At, f.Lifetime)));
        items.AddRange(row.Camps.Select(c => (c.LastKill, c.ExpiresAt - c.LastKill)));
        if (IsClear && row.Intel.Clear is { } clear) items.Add((clear.At, clear.Lifetime));
        _items = items;

        Signature = string.Join("|", SystemId, JumpsText, row.Tier, StateText, HostilesText, FlagsText, MovingText, SourcesText,
                                NearestName, RegionText, HullsMore, string.Join(",", Hulls.Select(h => h.Id + ":" + h.Text)),
                                string.Join(",", Events.Select(e => e.Signature)));
        Tick(now, snapshot.Settings.FreshSeconds);
    }

    public IntelRowActions Actions { get; }
    public string Signature { get; }

    public int SystemId { get; }
    public string SystemName { get; }
    public string RegionText { get; }
    public bool HasRegion => RegionText.Length > 0;

    public string JumpsText { get; }
    public bool HasJumps => JumpsText.Length > 0;
    public string? JumpsTip { get; }
    public IntelJumpBand Band { get; }
    public bool IsHere => Band == IntelJumpBand.Here;
    public bool IsNear => Band == IntelJumpBand.Near;
    public bool IsMid => Band == IntelJumpBand.Mid;
    public bool IsFar => Band == IntelJumpBand.Far;
    public bool IsPending => Band == IntelJumpBand.Unknown && HasJumps;

    public IBrush? TierBrush { get; }
    public string? StandingsTip { get; }

    public IntelRowState State { get; }
    public string StateText { get; }
    public bool IsClear { get; }
    public bool IsHostileState => State == IntelRowState.Hostile;
    public bool IsNoVisualState => State == IntelRowState.NoVisual;
    public bool IsCampState => State == IntelRowState.Camp;
    public bool IsFlagsState => State == IntelRowState.Flags;

    public int HostileCount { get; }
    public string HostilesText { get; }
    public IReadOnlyList<IntelPictureVm> Hulls { get; }
    public string HullsMore { get; }
    public bool HasHullsMore => HullsMore.Length > 0;
    public string FlagsText { get; }
    public string MovingText { get; }
    public string SourcesText { get; }

    /// <summary>The character the jumps are counted from, with their portrait.</summary>
    public long NearestId { get; }
    public string NearestName { get; }
    public bool HasNearest => NearestId > 0;

    private Avalonia.Media.Imaging.Bitmap? _nearestPicture;
    public Avalonia.Media.Imaging.Bitmap? NearestPicture { get => _nearestPicture; private set => this.RaiseAndSetIfChanged(ref _nearestPicture, value); }

    /// <summary>The one pilot named, for "Open pilot"; 0 when there are none or several.</summary>
    public long OnePilotId { get; }

    public DateTimeOffset NewestAt { get; }

    /// <summary>The system's reports, follow-ups and kills, newest first.</summary>
    public IReadOnlyList<IntelEventRowVm> Events { get; }

    private bool _isOpen;
    public bool IsOpen { get => _isOpen; set => this.RaiseAndSetIfChanged(ref _isOpen, value); }

    public void Toggle() => IsOpen = !IsOpen;

    private string _ageText = "";
    public string AgeText { get => _ageText; private set => this.RaiseAndSetIfChanged(ref _ageText, value); }

    private double _opacity = 1;
    public double Opacity { get => _opacity; private set => this.RaiseAndSetIfChanged(ref _opacity, value); }

    public void Tick(DateTimeOffset now, int freshSeconds)
    {
        AgeText = IntelWords.Age(_newest, now);
        Opacity = _items.Count == 0 ? 1 : _items.Max(i => IntelLens.Opacity(i.At, i.Life, now));
        foreach (var e in Events) e.Tick(now, freshSeconds);
    }

    public async Task LoadPicturesAsync()
    {
        var loads = Hulls.Select(h => h.LoadAsync()).Concat(Events.Select(e => e.LoadPicturesAsync())).ToList();
        if (NearestId > 0)
            loads.Add(Task.Run(async () =>
            {
                var bitmap = await EveImageCache.GetAsync($"https://images.evetech.net/characters/{NearestId}/portrait?size=32").ConfigureAwait(false);
                if (bitmap is not null) Dispatcher.UIThread.Post(() => NearestPicture = bitmap);
            }));
        await Task.WhenAll(loads).ConfigureAwait(false);
    }

    /// <summary>For a check with no network: the picture given rather than fetched.</summary>
    public void ShowNearest(Avalonia.Media.Imaging.Bitmap? picture) => NearestPicture = picture;

    public void OpenSystem() => EntityNavigator.Instance.System(SystemId);
    public void OpenPilot() { if (OnePilotId > 0) EntityNavigator.Instance.Entity(EntityKind.Pilot, OnePilotId); }
    public void OpenNearest() { if (NearestId > 0) EntityNavigator.Instance.Entity(EntityKind.Pilot, NearestId); }
    public void ShowOnMap() => Actions.ShowOnMap?.Invoke(SystemId);
    public bool CanShowOnMap => Actions.ShowOnMap is not null;

    public ReactiveCommand<Unit, Unit> OpenSystemCommand => _openSystem ??= ReactiveCommand.Create(OpenSystem);
    private ReactiveCommand<Unit, Unit>? _openSystem;

    public ReactiveCommand<Unit, Unit> OpenNearestCommand => _openNearest ??= ReactiveCommand.Create(OpenNearest);
    private ReactiveCommand<Unit, Unit>? _openNearest;
}

/// <summary>A line of the Feed: an intel event, or a follow-up indented under it.</summary>
public sealed class IntelFeedLineVm(IntelEventRowVm row, bool isFollowUp)
{
    public IntelEventRowVm Row { get; } = row;
    public bool IsFollowUp { get; } = isFollowUp;
    public string Key => (IsFollowUp ? "f" : "") + Row.Key;
}

/// <summary>A line in an intel channel that gave no intel, shown muted in the Feed when asked for.</summary>
public sealed class IntelChatterLineVm : ReactiveObject
{
    private readonly DateTimeOffset _at;

    public IntelChatterLineVm(IntelChatter line, DateTimeOffset now)
    {
        _at = line.At;
        Message = line.Message;
        TimeText = line.At.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        SourceText = string.Format(CultureInfo.CurrentCulture, IntelText.SourceChannelBy, line.Channel, line.Sender);
        Tick(now);
    }

    public string Message { get; }
    public string TimeText { get; }
    public string SourceText { get; }
    public string KindText => IntelText.KindChatter;

    private string _ageText = "";
    public string AgeText { get => _ageText; private set => this.RaiseAndSetIfChanged(ref _ageText, value); }

    public void Tick(DateTimeOffset now) => AgeText = IntelWords.Age(_at, now);
}
