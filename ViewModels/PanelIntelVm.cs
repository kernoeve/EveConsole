using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services.ClientLayouts;
using EveConsole.Services.Intel;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Live intel on a panel beside a game client: what was reported, killed or pasted within a few jumps
/// of the character in that monitor's client (<see cref="MonitorFocus"/>), newest first, as one row
/// per event. Or, on one line, the nearest threat and how many more there are.
///
/// <para>It reads nothing itself. The shared snapshot (<see cref="IntelSnapshotService"/>) is projected
/// through <see cref="IntelLens"/> for this one character: jumps from the system it is in (the newer of
/// ESI's location and the chat log's), its standings, threats only (blue and light blue hidden), live
/// items and clear calls, filtered by the range chosen for the control and then cut to 50.</para>
///
/// <para>A jump moves the rows at once: the focus or the snapshot changing projects again, and a
/// walk not yet cached shows "…" until it is, never waiting on the UI thread. A one-second clock ages
/// and fades the rows between snapshots. Nothing is ever sent to the game client.</para>
/// </summary>
public sealed class PanelIntelVm : ReactiveObject, IDisposable
{
    /// <summary>The most rows a panel shows, after every filter.</summary>
    public const int MaxRows = 50;

    private readonly IntelSnapshotService? _snapshots;
    private readonly IntelDistanceService? _distances;
    private readonly ObservableCollection<Character> _characters;
    private readonly MonitorFocus _focus;
    private readonly string _monitor;
    private readonly DispatcherTimer _clock;
    private IDisposable? _subscription;
    private IntelSnapshot _snapshot = IntelSnapshot.Empty;
    private long _projection;
    private bool _started;
    private readonly HashSet<int> _walking = [];

    public PanelIntelVm(IntelSnapshotService? snapshots, IntelDistanceService? distances,
                        ObservableCollection<Character> characters, MonitorFocus focus, string monitor, ControlOptions? options)
    {
        _snapshots = snapshots;
        _distances = distances;
        _characters = characters;
        _focus = focus;
        _monitor = monitor;
        Range = PanelControls.IntelRangeOf(options);
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        _clock.Stop();
    }

    /// <summary>How far the control looks, in jumps.</summary>
    public int Range { get; }

    public string RangeText => string.Format(CultureInfo.CurrentCulture, IntelText.RangeShort, Range);

    /// <summary>The clock rows are aged by; a check sets its own.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public IntelRowActions Actions { get; set; } = IntelRowActions.None;

    /// <summary>Opens the Intel tool where any tool opens; null while there is none.</summary>
    public Action? OpenTool { get; set; }

    /// <summary>Opens the Intel tool's Paste tab with this character and its system chosen; null
    /// while there is none.</summary>
    public Action<long?, int?>? OpenPaste { get; set; }

    /// <summary>Settings on this panel's monitor, at Chat Logs, where intel channels are set.</summary>
    public Action? OpenChatLogSettings { get; set; }

    public bool CanOpenTool => OpenTool is not null;
    public bool CanPaste => OpenPaste is not null;

    public ReactiveCommand<Unit, Unit> OpenToolCommand => _openTool ??= ReactiveCommand.Create(() => OpenTool?.Invoke());
    private ReactiveCommand<Unit, Unit>? _openTool;

    public ReactiveCommand<Unit, Unit> PasteCommand => _paste ??= ReactiveCommand.Create(() => OpenPaste?.Invoke(_shownId, _shownSystem));
    private ReactiveCommand<Unit, Unit>? _paste;

    public ReactiveCommand<Unit, Unit> OpenChatLogSettingsCommand =>
        _openChatLogs ??= ReactiveCommand.Create(() => OpenChatLogSettings?.Invoke());
    private ReactiveCommand<Unit, Unit>? _openChatLogs;

    // ── The character ────────────────────────────────────────────────────────

    public ObservableCollection<string> Names { get; } = [];

    private string? _selectedName;
    public string? SelectedName
    {
        get => _selectedName;
        set
        {
            // A ComboBox reports null while its list is being changed; that isn't a choice.
            if (value == null || _selectedName == value) return;
            // ⚠️ Acted on once the dropdown has finished choosing: choosing can reorder its names.
            Dispatcher.UIThread.Post(() => _focus.Choose(_monitor, value));
        }
    }

    /// <summary>The character for the single line, or why there's no one.</summary>
    public string LineName => _selectedName ?? IntelText.PanelNoClientShort;

    private string _whereText = "";
    /// <summary>"in QZ-X77", or that it isn't known.</summary>
    public string WhereText { get => _whereText; private set => this.RaiseAndSetIfChanged(ref _whereText, value); }

    private string _countText = "";
    /// <summary>"7 within 10j".</summary>
    public string CountText { get => _countText; private set => this.RaiseAndSetIfChanged(ref _countText, value); }

    // ── The rows ─────────────────────────────────────────────────────────────

    public BulkObservableCollection<IntelEventRowVm> Rows { get; } = [];

    private string _note = "";
    /// <summary>Why the list is empty, when it is.</summary>
    public string Note { get => _note; private set { this.RaiseAndSetIfChanged(ref _note, value); this.RaisePropertyChanged(nameof(HasNote)); } }
    public bool HasNote => Note.Length > 0;

    private bool _noteOpensChatLogs;
    /// <summary>The note is about intel channels: a link to where they are set goes with it.</summary>
    public bool NoteOpensChatLogs { get => _noteOpensChatLogs; private set => this.RaiseAndSetIfChanged(ref _noteOpensChatLogs, value); }

    private string _footer = IntelText.PanelFooter;
    /// <summary>What the list leaves out, and how it measures; with the caveats of the moment.</summary>
    public string Footer { get => _footer; private set => this.RaiseAndSetIfChanged(ref _footer, value); }

    // ── The single line ──────────────────────────────────────────────────────

    private IntelNearestVm? _nearest;
    /// <summary>The nearest live threat, for the single line; null when there is none.</summary>
    public IntelNearestVm? Nearest
    {
        get => _nearest;
        private set { this.RaiseAndSetIfChanged(ref _nearest, value); this.RaisePropertyChanged(nameof(HasNearest)); }
    }
    public bool HasNearest => Nearest is not null;

    private string _moreText = "";
    /// <summary>"+6 more within 10j".</summary>
    public string MoreText { get => _moreText; private set => this.RaiseAndSetIfChanged(ref _moreText, value); }

    private string _lineEmpty = "";
    /// <summary>What the line says with no threat to show: nothing near, or why it can't tell.</summary>
    public string LineEmptyText { get => _lineEmpty; private set => this.RaiseAndSetIfChanged(ref _lineEmpty, value); }

    private bool _isFresh;
    /// <summary>Anything fresh within five jumps: the line lights up. Never a sound.</summary>
    public bool IsFresh { get => _isFresh; private set => this.RaiseAndSetIfChanged(ref _isFresh, value); }

    // ── Lifetime ─────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_started) return;
        _started = true;
        _focus.Changed += OnFocusChanged;
        _characters.CollectionChanged += OnCharactersChanged;
        if (_distances is not null) _distances.Changed += OnDistancesChanged;
        if (_snapshots is not null)
        {
            _snapshots.Changed += OnSnapshot;
            _subscription = _snapshots.Subscribe();
            if (_snapshots.Current.Version >= _snapshot.Version) _snapshot = _snapshots.Current;
        }
        _clock.Start();
        ShowSelection();
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        _focus.Changed -= OnFocusChanged;
        _characters.CollectionChanged -= OnCharactersChanged;
        if (_distances is not null) _distances.Changed -= OnDistancesChanged;
        if (_snapshots is not null) _snapshots.Changed -= OnSnapshot;
        _subscription?.Dispose();
        _subscription = null;
        _clock.Stop();
    }

    /// <summary>Shows a snapshot given rather than read: for a check with invented intel.</summary>
    public void Apply(IntelSnapshot snapshot)
    {
        _snapshot = snapshot;
        Project();
    }

    private void OnSnapshot(IntelSnapshot snapshot) => Dispatcher.UIThread.Post(() =>
    {
        if (!_started || snapshot.Version < _snapshot.Version) return;
        _snapshot = snapshot;
        Project();
    });

    private void OnDistancesChanged() => Dispatcher.UIThread.Post(() => { if (_started) Project(); });

    private void OnFocusChanged(string monitor)
    {
        if (monitor == _monitor) ShowSelection();
    }

    private void OnCharactersChanged(object? sender, NotifyCollectionChangedEventArgs e) => Project();

    private void ShowSelection()
    {
        var names = _focus.NamesOn(_monitor);
        foreach (var gone in Names.Where(n => !names.Contains(n)).ToList()) Names.Remove(gone);
        for (var i = 0; i < names.Count; i++)
        {
            var at = Names.IndexOf(names[i]);
            if (at < 0) Names.Insert(Math.Min(i, Names.Count), names[i]);
            else if (at != i) Names.Move(at, i);
        }

        _selectedName = _focus.SelectedOn(_monitor);
        this.RaisePropertyChanged(nameof(SelectedName));
        this.RaisePropertyChanged(nameof(LineName));
        // The new character's origin, standings and notes at once, without waiting for a snapshot.
        Project();
    }

    // ── Projection ───────────────────────────────────────────────────────────

    private long? _shownId;
    private int? _shownSystem;

    /// <summary>
    /// The snapshot through the lens for this character, off the UI thread; the rows are swapped in
    /// on it. A newer projection started meanwhile wins. A walk not cached yet is asked for, and the
    /// rows come again when it is done.
    /// </summary>
    private void Project()
    {
        var snapshot = _snapshot;
        var name = _selectedName;
        var id = name == null ? null
            : _characters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
        var own = id is long cid ? snapshot.Own.FirstOrDefault(o => o.CharacterId == cid) : null;
        _shownId = id;
        _shownSystem = own?.SystemId;

        var viewer = new IntelViewer
        {
            Origins = own is null ? [] : [new IntelOrigin(own.CharacterId, own.SystemId)],
            MaxJumps = Range,
            Mode = IntelGraphMode.Threat,
            Tiers = StandingTiers.Hostile,
            StandingsCharacterId = id,
            IncludeClear = true,
            IncludeKills = true,
            FeedCap = MaxRows,
        };
        var now = Clock();
        var ticket = Interlocked.Increment(ref _projection);
        var distances = _distances;
        _ = Task.Run(() => IntelLens.Project(snapshot, viewer, distances, now)).ContinueWith(t =>
        {
            if (t.IsFaulted || Interlocked.Read(ref _projection) != ticket) return;
            var view = t.Result;
            Dispatcher.UIThread.Post(() =>
            {
                if (Interlocked.Read(ref _projection) != ticket) return;
                Show(snapshot, view, name, id, own, now);
                foreach (var missing in view.MissingOrigins) Walk(missing.SystemId);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Asks for the walk from a system off the UI thread, and projects again when it is cached.</summary>
    private void Walk(int systemId)
    {
        if (_distances is not { } distances || !_walking.Add(systemId)) return;
        _ = Task.Run(async () =>
        {
            var walked = false;
            try { await distances.JumpsFromAsync(systemId, IntelGraphMode.Threat).ConfigureAwait(false); walked = true; }
            catch { /* The next snapshot asks again; asking again here at once would only fail again. */ }
            Dispatcher.UIThread.Post(() =>
            {
                _walking.Remove(systemId);
                if (walked && _started) Project();
            });
        });
    }

    private void Show(IntelSnapshot snapshot, IntelView view, string? name, long? id, OwnPosition? own, DateTimeOffset now)
    {
        var culture = CultureInfo.CurrentCulture;
        var pending = view.Pending;

        // Kept where the row reads the same, so its pictures stay and nothing blinks.
        var held = Rows.ToDictionary(r => r.Key);
        // A camp is one row, as new as its last kill: its kills are not each a row of their own beside it.
        var camps = view.Feed.Where(r => r.Event.Kind == IntelEventKind.CampFormed).Select(r => r.Event.CampKey).ToHashSet();
        var fresh = view.Feed
            .Where(r => !(r.Event.Kind == IntelEventKind.Kill && r.Event.CampKey is { } camp && camps.Contains(camp)))
            .Select(r => new IntelEventRowVm(r, snapshot, now, pending, Actions))
            .Select(r => held.TryGetValue(r.Key, out var old) && old.Signature == r.Signature ? old : r)
            .OrderByDescending(r => r.NewestAt).ThenByDescending(r => r.Key, StringComparer.Ordinal)
            .ToList();
        Rows.ResetTo(fresh);
        foreach (var row in fresh.Where(r => !held.ContainsKey(r.Key) || !ReferenceEquals(held[r.Key], r)))
            _ = row.LoadPicturesAsync();

        var threats = view.Systems.Where(s => s.State != IntelSystemState.Clear).ToList();
        // The nearest someone; with nobody about, the nearest camp, cyno or bubbles, rather than "nothing".
        var nearest = own is null ? null : view.Nearest ?? view.Systems.FirstOrDefault(r => r.State == IntelSystemState.FlagsOnly);
        Nearest = nearest is null ? null : new IntelNearestVm(nearest, snapshot, now, pending);
        if (Nearest is { } n) _ = n.LoadPicturesAsync();
        var others = threats.Count - (nearest is null ? 0 : 1);
        MoreText = own is null || others <= 0 ? "" : string.Format(culture, IntelText.PanelMoreWithin, others, Range);
        CountText = own is null ? "" : string.Format(culture, IntelText.PanelWithin, threats.Count, Range);
        WhereText = own is null
            ? (id is null ? "" : IntelText.PanelWhereUnknown)
            : string.Format(culture, IntelText.PanelIn, snapshot.SystemName(own.SystemId));

        // Why the list is empty, when it is.
        NoteOpensChatLogs = false;
        string note;
        if (name is null) note = IntelText.PanelNoClient;
        else if (id is null) note = string.Format(culture, IntelText.PanelNotInApp, name);
        else if (!snapshot.Settings.HasIntelChannels && fresh.Count == 0)
        {
            note = IntelText.PanelNoChannels;
            NoteOpensChatLogs = OpenChatLogSettings is not null;
        }
        else if (fresh.Count > 0) note = "";
        else if (snapshot.Version == 0) note = IntelText.PanelReading;
        else if (own is null) note = string.Format(culture, IntelText.PanelNothingAnywhere, name);
        else note = string.Format(culture, IntelText.PanelNothingWithin, Range, name);
        Note = note;

        LineEmptyText = name is null ? IntelText.PanelNoClientShort
            : id is null ? string.Format(culture, IntelText.PanelNotInAppShort, name)
            : own is null ? IntelText.PanelWhereUnknownShort
            : snapshot.Version == 0 ? IntelText.PanelReading
            : string.Format(culture, IntelText.PanelNothingWithinShort, Range);

        // The caveats of the moment, after what the list always leaves out.
        var footer = new List<string> { IntelText.PanelFooter };
        if (id is not null && own is null) footer.Add(string.Format(culture, IntelText.PanelLocationUnknown, name));
        if (id is long viewer && snapshot.Standings.Own(viewer) is not null && !snapshot.Standings.HasPersonalContacts(viewer))
            footer.Add(IntelText.PanelPersonalUnknown);
        Footer = string.Join(" · ", footer);

        Tick();
    }

    private void Tick()
    {
        var now = Clock();
        var freshSeconds = _snapshot.Settings.FreshSeconds;
        foreach (var row in Rows) row.Tick(now, freshSeconds);
        Nearest?.Tick(now);
        IsFresh = Rows.Any(r => r.IsFresh);
    }
}

/// <summary>
/// The single line's threat: the nearest system with someone in it, how far, who, what they fly,
/// what else was said, and how long ago.
/// </summary>
public sealed class IntelNearestVm : ReactiveObject
{
    private const int MaxHulls = 2;
    private readonly DateTimeOffset _newest;

    public IntelNearestVm(IntelSystemRow row, IntelSnapshot snapshot, DateTimeOffset now, bool pending)
    {
        var culture = CultureInfo.CurrentCulture;
        SystemId = row.SystemId;
        SystemName = snapshot.SystemName(row.SystemId);
        JumpsText = IntelWords.Jumps(row.Jumps, pending);
        Band = row.Jumps is null && pending ? IntelJumpBand.Unknown : IntelWords.Band(row.Jumps);
        TierBrush = IntelTierColors.Of(row.Tier);
        var count = row.Pilots.Count + row.Unnamed;
        var flags = row.Facts.Aggregate(0, (f, x) => f | (int)x.Flag);
        var camp = row.Camps.FirstOrDefault();
        StateText = row.State switch
        {
            IntelSystemState.NoVisual => string.Format(culture, IntelText.StateNoVisual, count),
            IntelSystemState.FlagsOnly when camp is not null => string.Format(culture, IntelText.StateCamp, camp.KillMailIds.Count),
            IntelSystemState.FlagsOnly => IntelWords.Flags(flags),
            _ => string.Format(culture, IntelText.StateHostile, count),
        };

        // The hulls, each with its icon, as many as fit; then the unnamed hulls said.
        var hulls = row.Pilots.Where(p => p.Pilot.Ship is { Length: > 0 })
            .GroupBy(p => (p.Pilot.ShipTypeId ?? 0, p.Pilot.Ship!))
            .OrderByDescending(g => g.Count())
            .Select(g => IntelPictureVm.Ship(g.Key.Item1, g.Key.Item2, g.Count()))
            .ToList();
        if (row.Intel.Unnamed is { Ships: { Length: > 0 } said } && row.Unnamed > 0)
            hulls.AddRange(EveConsole.Services.IntelDisplay.ParseShips(said).Select(s => IntelPictureVm.Ship(0, s.Name, s.Count)));
        Pictures = [.. hulls.Take(MaxHulls)];
        MoreHulls = hulls.Count > MaxHulls ? "+" + (hulls.Count - MaxHulls).ToString(culture) : "";

        FlagsText = row.State == IntelSystemState.FlagsOnly && camp is null ? "" : IntelWords.Flags(flags, camp);
        _newest = row.NewestAt;
        Tick(now);
    }

    public int SystemId { get; }
    public string SystemName { get; }
    public string JumpsText { get; }
    public IntelJumpBand Band { get; }
    public bool IsHere => Band == IntelJumpBand.Here;
    public bool IsNear => Band == IntelJumpBand.Near;
    public bool IsMid  => Band == IntelJumpBand.Mid;
    public bool IsFar  => Band == IntelJumpBand.Far;
    public Avalonia.Media.IBrush TierBrush { get; }
    public string StateText { get; }
    public IReadOnlyList<IntelPictureVm> Pictures { get; }
    public string MoreHulls { get; }
    public bool HasMoreHulls => MoreHulls.Length > 0;
    public string FlagsText { get; }
    public bool HasFlags => FlagsText.Length > 0;

    private string _ageText = "";
    public string AgeText { get => _ageText; private set => this.RaiseAndSetIfChanged(ref _ageText, value); }

    public void Tick(DateTimeOffset now) => AgeText = IntelWords.Age(_newest, now);

    public Task LoadPicturesAsync() => Task.WhenAll(Pictures.Select(p => p.LoadAsync()));
}
