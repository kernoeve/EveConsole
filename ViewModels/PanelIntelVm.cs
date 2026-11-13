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
/// Live intel on a panel beside a game client: the systems with hostiles in them within a few jumps of
/// the character in that monitor's client (<see cref="MonitorFocus"/>), nearest first, one row per
/// system: how far, how many, in what, who, how long ago, and whether they killed there. Clicked, a row
/// opens to the pilots there and the system's reports and kills. Or, on one line, the nearest threat in
/// the same form, and how many more there are.
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

    /// <summary>One row per system with hostiles in it (or a camp, a flag, a clear), nearest first.</summary>
    public BulkObservableCollection<IntelSystemLineVm> Rows { get; } = [];

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

    private IntelSystemLineVm? _nearest;
    /// <summary>The nearest live threat, for the single line, as the list's own line; null when there is none.</summary>
    public IntelSystemLineVm? Nearest
    {
        get => _nearest;
        private set { this.RaiseAndSetIfChanged(ref _nearest, value); this.RaisePropertyChanged(nameof(HasNearest)); }
    }
    public bool HasNearest => Nearest is not null;

    private string _moreText = "";
    /// <summary>"+6 more within 10j".</summary>
    public string MoreText
    {
        get => _moreText;
        private set { this.RaiseAndSetIfChanged(ref _moreText, value); this.RaisePropertyChanged(nameof(HasMore)); }
    }

    private string _moreShort = "";
    /// <summary>"+6": the single line's count of the rest, short so the nearest system keeps the room; the
    /// words are its tooltip.</summary>
    public string MoreShort { get => _moreShort; private set => this.RaiseAndSetIfChanged(ref _moreShort, value); }
    public bool HasMore => MoreText.Length > 0;

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

        // No client on this monitor, or one whose character isn't in the app: there is nobody to measure
        // from, so every row would be from anywhere, past any range. None is shown; the note says why.
        // (A character whose system isn't known yet still lists the newest anywhere, as its footer says.)
        if (id is null)
        {
            Interlocked.Increment(ref _projection);
            Show(snapshot, IntelView.Empty, name, null, null, Clock());
            return;
        }

        var viewer = new IntelViewer
        {
            Origins = own is null ? [] : [new IntelOrigin(own.CharacterId, own.SystemId)],
            MaxJumps = Range,
            Mode = IntelGraphMode.Threat,
            Tiers = StandingTiers.Hostile,
            StandingsCharacterId = id,
            IncludeClear = true,
            IncludeKills = true,
            SystemCap = MaxRows,
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

        // One row per system: who is there leads, and its reports, kills and camp lines are its details. A camp's
        // kills are not each a line beside the camp's own.
        var events = SystemEvents(view);
        var held = Rows.ToDictionary(r => r.SystemId);
        var fresh = Lines(snapshot, view, now, Actions)
            .Select(row =>
            {
                if (!held.TryGetValue(row.SystemId, out var old)) return row;
                if (old.Signature == row.Signature) return old;
                row.IsOpen = old.IsOpen;
                return row;
            })
            .ToList();
        Rows.ResetTo(fresh);
        foreach (var row in fresh.Where(r => !held.TryGetValue(r.SystemId, out var old) || !ReferenceEquals(old, r)))
            _ = row.LoadPicturesAsync();

        // Who the watch list ignores is listed, dimmed, but is no threat to count or to put on the line.
        var threats = view.Systems.Where(s => s.State != IntelSystemState.Clear && s.Watch != IntelWatchMark.Ignored).ToList();
        // The nearest someone; with nobody about, the nearest camp, cyno or bubbles, rather than "nothing".
        var nearest = own is null ? null : view.Nearest ?? view.Systems.FirstOrDefault(r => r.State == IntelSystemState.FlagsOnly);
        // The list's own row for it: the single line reads exactly as the list does.
        Nearest = nearest is null ? null
            : fresh.FirstOrDefault(r => r.SystemId == nearest.SystemId)
              ?? new IntelSystemLineVm(nearest, snapshot, events.GetValueOrDefault(nearest.SystemId) ?? [], null, now, pending, Actions);
        var others = threats.Count - (nearest is null ? 0 : 1);
        MoreText = own is null || others <= 0 ? "" : string.Format(culture, IntelText.PanelMoreWithin, others, Range);
        MoreShort = MoreText.Length == 0 ? "" : "+" + others.ToString(culture);
        CountText = own is null ? "" : string.Format(culture, IntelText.PanelWithin, threats.Count, Range);
        WhereText = own is null
            ? (id is null ? "" : IntelText.PanelWhereUnknown)
            : string.Format(culture, IntelText.PanelIn, snapshot.SystemName(own.SystemId));

        // Why the list is empty, only when it is.
        NoteOpensChatLogs = false;
        string note;
        if (fresh.Count > 0) note = "";
        else if (name is null) note = IntelText.PanelNoClient;
        else if (id is null) note = string.Format(culture, IntelText.PanelNotInApp, name);
        else if (!snapshot.Settings.HasIntelChannels)
        {
            note = IntelText.PanelNoChannels;
            NoteOpensChatLogs = OpenChatLogSettings is not null;
        }
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

    /// <summary>The panel's rows for a projection: one per system the lens kept, in its order (nearest first), each
    /// with the system's feed lines as its details.</summary>
    public static List<IntelSystemLineVm> Lines(IntelSnapshot snapshot, IntelView view, DateTimeOffset now, IntelRowActions actions)
    {
        var events = SystemEvents(view);
        return [.. view.Systems.Select(s =>
            new IntelSystemLineVm(s, snapshot, events.GetValueOrDefault(s.SystemId) ?? [], null, now, view.Pending, actions))];
    }

    /// <summary>Each system's feed lines, newest first, with a camp's kills left to the camp's own line.</summary>
    public static Dictionary<int, List<IntelFeedRow>> SystemEvents(IntelView view)
    {
        var camps = view.Feed.Where(r => r.Event.Kind == IntelEventKind.CampFormed).Select(r => r.Event.CampKey).ToHashSet();
        return view.Feed
            .Where(r => !(r.Event.Kind == IntelEventKind.Kill && r.Event.CampKey is { } camp && camps.Contains(camp)))
            .GroupBy(r => r.Event.SystemId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Event.At).ThenByDescending(r => r.Event.Key, StringComparer.Ordinal).ToList());
    }

    private void Tick()
    {
        var now = Clock();
        var freshSeconds = _snapshot.Settings.FreshSeconds;
        foreach (var row in Rows) row.Tick(now, freshSeconds);
        if (Nearest is { } n && !Rows.Contains(n)) n.Tick(now, freshSeconds);
        IsFresh = Rows.Any(r => r.IsFresh);
    }
}
