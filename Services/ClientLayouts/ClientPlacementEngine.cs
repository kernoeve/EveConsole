namespace EveConsole.Services.ClientLayouts;

/// <summary>What the app can see of a game client's window. It never looks at anything else.</summary>
public readonly record struct ClientWindowInfo(long Handle, string Title, LayoutRect Rect, bool Borderless, bool Minimized = false);

/// <summary>One monitor as the placement rules see it.</summary>
public sealed record PlacementMonitor(string Key, LayoutRect Bounds, IReadOnlyList<PlacedClient> Cells);

/// <summary>Everything the rules need to know about where clients go on this desktop.</summary>
public sealed class PlacementPlan
{
    public IReadOnlyList<PlacementMonitor> Monitors { get; init; } = [];

    /// <summary>Character name to its one position on this desktop.</summary>
    public IReadOnlyDictionary<string, (string MonitorKey, string AreaId)> Positions { get; init; }
        = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A move for the caller to carry out. The engine itself touches no windows.</summary>
public readonly record struct ClientMove(long Handle, LayoutRect Rect);

public enum PlacementNoticeKind
{
    /// <summary>The client isn't in Fixed Window mode, so it is left alone.</summary>
    NotFixedWindow,

    /// <summary>The character's position is held by another logged-in character.</summary>
    PositionInUse,

    /// <summary>A logged-in character is in no group on this desktop.</summary>
    CharacterHasNoPosition,

    /// <summary>The window moved without us, so it is left alone until a layout is applied.</summary>
    MovedByHand,

    /// <summary>A move we asked for never arrived, or something changed it straight after.</summary>
    MoveDidNotHold,
}

public readonly record struct PlacementNotice(PlacementNoticeKind Kind, long Handle, string? Character);

public sealed class PlacementStep
{
    public List<ClientMove> Moves { get; } = [];
    public List<PlacementNotice> Notices { get; } = [];
}

/// <summary>
/// The rules for keeping clients in their places, as plain logic with no windows or timers, so
/// they can be checked without a game running.
///
/// <para>⚠️ It never fights a window. If a client ends up somewhere other than where it was sent,
/// the user moved it or the game did, and it is left alone until a layout is applied again.
/// Nothing here may ever move a client someone could be flying, except at the moment it logs in
/// or when the user presses Apply.</para>
/// </summary>
public sealed class ClientPlacementEngine
{
    private const string CharacterPrefix = "EVE - ";

    /// <summary>How long an asked-for move may take to arrive. A client that is loading can be slow.</summary>
    public static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(5);

    private sealed class Tracked
    {
        public string Title = "";
        public LayoutRect Last;
        public string? MonitorKey;
        public string? AreaId;
        public LayoutRect? Expected;
        public LayoutRect? Sent;
        public DateTime SentAt;
        public bool LeftAlone;
        public bool NotFixed;
        public bool Noted;
    }

    private readonly Dictionary<long, Tracked> _clients = [];
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private PlacementPlan _plan = new();
    private bool _primed;

    /// <summary>Character names seen in client windows, which the editor offers alongside the app's own.</summary>
    public IReadOnlyCollection<string> SeenCharacters => _seen;

    /// <summary>The character in a client window's title, or null while it's at the login screen.</summary>
    public static string? CharacterIn(string title) =>
        title.StartsWith(CharacterPrefix, StringComparison.Ordinal) && title.Length > CharacterPrefix.Length
            ? title[CharacterPrefix.Length..]
            : null;

    /// <summary>True for any window title a game client can have.</summary>
    public static bool IsClientTitle(string title) =>
        title == "EVE" || title.StartsWith(CharacterPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Sets the plan without moving anything: what is on screen stays until Apply. A new client
    /// uses the new plan. A tracked client whose cell no longer exists simply stops holding one.
    /// </summary>
    public void UsePlan(PlacementPlan plan)
    {
        _plan = plan;
        foreach (var c in _clients.Values)
            if (c.AreaId != null && CellRect(c.MonitorKey, c.AreaId) == null)
                (c.MonitorKey, c.AreaId) = (null, null);
    }

    /// <summary>
    /// The regular look at what's on screen. Clients present at the first look are already running,
    /// perhaps being flown, so they are only noted; one sitting exactly in a cell holds that cell.
    /// </summary>
    public PlacementStep Step(IReadOnlyList<ClientWindowInfo> windows, DateTime now)
    {
        var step = new PlacementStep();
        foreach (var gone in _clients.Keys.Where(k => windows.All(w => w.Handle != k)).ToList())
            _clients.Remove(gone);

        foreach (var w in windows)
        {
            var character = CharacterIn(w.Title);
            if (character != null) _seen.Add(character);

            // Windows parks a minimized window far off screen, which says nothing about where it
            // belongs. It keeps its place and its last position, and is looked at once it's back.
            if (w.Minimized)
            {
                if (!_clients.ContainsKey(w.Handle))
                    _clients[w.Handle] = new Tracked { Title = w.Title, Last = w.Rect, LeftAlone = true, NotFixed = !w.Borderless };
                continue;
            }

            if (!_clients.TryGetValue(w.Handle, out var c))
            {
                c = new Tracked { Title = w.Title, Last = w.Rect, NotFixed = !w.Borderless };
                _clients[w.Handle] = c;
                if (c.NotFixed)
                {
                    step.Notices.Add(new PlacementNotice(PlacementNoticeKind.NotFixedWindow, w.Handle, character));
                    continue;
                }
                if (!_primed) { ClaimCellItSitsIn(c); continue; }
                if (character == null) TakeEmptyCell(w.Handle, c, step, now);
                else GoToPosition(w.Handle, c, character, step, now);
                continue;
            }

            // Fixed Window can be turned on while the client runs; from then on it can be placed.
            if (c.NotFixed && w.Borderless) c.NotFixed = false;

            var titleChanged = w.Title != c.Title;
            c.Title = w.Title;
            c.Last = w.Rect;
            Check(w, c, step, now);

            if (titleChanged && character != null && !c.NotFixed && !c.LeftAlone)
                GoToPosition(w.Handle, c, character, step, now);
        }
        _primed = true;
        return step;
    }

    /// <summary>
    /// The user pressed Apply: everything is placed afresh, including clients left alone before.
    /// Logged-in characters go first, so a client at login never holds a character's place.
    /// </summary>
    public PlacementStep Apply(IReadOnlyList<ClientWindowInfo> windows, DateTime now)
    {
        var step = Step(windows, now);
        step.Moves.Clear();
        _primed = true;

        var live = windows.Where(w => w.Borderless && !w.Minimized && _clients.ContainsKey(w.Handle)).ToList();
        foreach (var w in live)
        {
            var c = _clients[w.Handle];
            c.NotFixed = false;
            c.LeftAlone = false;
            c.Noted = false;
            c.Sent = null;
            c.Last = w.Rect;
            (c.MonitorKey, c.AreaId) = (null, null);
        }

        foreach (var w in live)
            if (CharacterIn(w.Title) is { } character && _plan.Positions.ContainsKey(character))
                GoToPosition(w.Handle, _clients[w.Handle], character, step, now);
        foreach (var w in live)
        {
            if (CharacterIn(w.Title) is not { } character || _plan.Positions.ContainsKey(character)) continue;
            ClaimCellItSitsIn(_clients[w.Handle]);
            step.Notices.Add(new PlacementNotice(PlacementNoticeKind.CharacterHasNoPosition, w.Handle, character));
        }
        foreach (var w in live.Where(w => CharacterIn(w.Title) == null))
            TakeEmptyCell(w.Handle, _clients[w.Handle], step, now);
        return step;
    }

    /// <summary>How many clients are on a monitor right now, which decides "start with EVE" panels.</summary>
    public int ClientsOn(LayoutRect monitorBounds) =>
        _clients.Values.Count(c => Contains(monitorBounds, c.Last));

    /// <summary>Which clients are on a monitor right now, which decides whose windows cover its taskbar.</summary>
    public IEnumerable<long> ClientHandlesOn(LayoutRect monitorBounds) =>
        _clients.Where(c => Contains(monitorBounds, c.Value.Last)).Select(c => c.Key);

    /// <summary>Who is logged in on a tracked client, or null at its login screen.</summary>
    public string? CharacterOn(long handle) => _clients.TryGetValue(handle, out var c) ? CharacterIn(c.Title) : null;

    /// <summary>Is this window where it should be, and if not, whose doing was that?</summary>
    private static void Check(ClientWindowInfo w, Tracked c, PlacementStep step, DateTime now)
    {
        if (c.Sent is { } sent)
        {
            if (w.Rect == sent) { c.Sent = null; return; }
            if (now - c.SentAt < MoveTimeout) return;
            c.Sent = null;
            LetGo(w, c, step, PlacementNoticeKind.MoveDidNotHold);
            return;
        }
        if (c.AreaId == null || c.Expected == null || w.Rect == c.Expected) return;
        LetGo(w, c, step, PlacementNoticeKind.MovedByHand);
    }

    private static void LetGo(ClientWindowInfo w, Tracked c, PlacementStep step, PlacementNoticeKind kind)
    {
        c.LeftAlone = true;
        (c.MonitorKey, c.AreaId) = (null, null);
        c.Expected = null;
        if (c.Noted) return;
        c.Noted = true;
        step.Notices.Add(new PlacementNotice(kind, w.Handle, CharacterIn(w.Title)));
    }

    private void TakeEmptyCell(long handle, Tracked c, PlacementStep step, DateTime now)
    {
        var monitor = _plan.Monitors.FirstOrDefault(m => Contains(m.Bounds, c.Last));
        if (monitor == null) return;
        foreach (var cell in monitor.Cells)
        {
            if (Holder(monitor.Key, cell.AreaId) != null) continue;
            Send(handle, c, monitor.Key, cell, step, now);
            return;
        }
    }

    private void GoToPosition(long handle, Tracked c, string character, PlacementStep step, DateTime now)
    {
        if (!_plan.Positions.TryGetValue(character, out var position))
        {
            step.Notices.Add(new PlacementNotice(PlacementNoticeKind.CharacterHasNoPosition, handle, character));
            return;
        }
        if (c.MonitorKey == position.MonitorKey && c.AreaId == position.AreaId) return;
        if (CellRect(position.MonitorKey, position.AreaId) is not { } target) return;

        var holder = Holder(position.MonitorKey, position.AreaId);
        if (holder is { } other && CharacterIn(_clients[other].Title) != null)
        {
            step.Notices.Add(new PlacementNotice(PlacementNoticeKind.PositionInUse, handle, character));
            return;
        }

        var (fromMonitor, fromArea) = (c.MonitorKey, c.AreaId);
        Send(handle, c, position.MonitorKey, new PlacedClient(position.AreaId, target), step, now);
        if (holder is not { } waiting) return;

        // A client still at login gives way: it takes the place this one came from, if any.
        var held = _clients[waiting];
        (held.MonitorKey, held.AreaId) = (null, null);
        if (fromArea != null && CellRect(fromMonitor, fromArea) is { } swap)
            Send(waiting, held, fromMonitor!, new PlacedClient(fromArea, swap), step, now);
        else
            TakeEmptyCell(waiting, held, step, now);
    }

    private static void Send(long handle, Tracked c, string monitorKey, PlacedClient cell, PlacementStep step, DateTime now)
    {
        c.MonitorKey = monitorKey;
        c.AreaId = cell.AreaId;
        c.Expected = cell.Rect;
        if (c.Last == cell.Rect) { c.Sent = null; return; }
        c.Sent = cell.Rect;
        c.SentAt = now;
        step.Moves.Add(new ClientMove(handle, cell.Rect));
    }

    private void ClaimCellItSitsIn(Tracked c)
    {
        foreach (var monitor in _plan.Monitors)
        {
            foreach (var cell in monitor.Cells)
            {
                if (cell.Rect != c.Last || Holder(monitor.Key, cell.AreaId) != null) continue;
                c.MonitorKey = monitor.Key;
                c.AreaId = cell.AreaId;
                c.Expected = cell.Rect;
                return;
            }
        }
    }

    private long? Holder(string monitorKey, string areaId)
    {
        foreach (var (handle, c) in _clients)
            if (c.MonitorKey == monitorKey && c.AreaId == areaId) return handle;
        return null;
    }

    private LayoutRect? CellRect(string? monitorKey, string areaId)
    {
        var monitor = _plan.Monitors.FirstOrDefault(m => m.Key == monitorKey);
        if (monitor == null) return null;
        foreach (var cell in monitor.Cells)
            if (cell.AreaId == areaId) return cell.Rect;
        return null;
    }

    /// <summary>A window belongs to the monitor its middle is on.</summary>
    private static bool Contains(LayoutRect monitor, LayoutRect window)
    {
        var x = window.X + window.Width / 2;
        var y = window.Y + window.Height / 2;
        return x >= monitor.X && x < monitor.Right && y >= monitor.Y && y < monitor.Bottom;
    }
}
