using System.Runtime.InteropServices;
using Avalonia.Threading;
using EveConsole.Services;
using EveConsole.Services.ClientLayouts;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// Keeps the panel windows in step with what <see cref="ClientLayoutService"/> says should show.
///
/// <para>A panel that should hide is hidden rather than closed: its window stays loaded so it can
/// come back at once when a client returns. They only close when placement is turned off, when a
/// layout no longer has them, or when the app exits.</para>
///
/// <para>A monitor's panels come forward together when one of them is clicked or when a client on
/// that monitor comes to the front, and that monitor's other clients come up beneath them. Nothing
/// here gives a client focus: after a panel click, whichever window had the keyboard keeps it until
/// the user clicks a client.</para>
///
/// <para>On a monitor set to use all of itself, it also has the taskbar move behind that monitor's
/// clients and panels. Both are marked, as the taskbar goes by whichever of them is on top. ⚠️
/// Except while one of our own ordinary windows (the main window, a window of tabs) is in front of
/// them on that monitor: see <see cref="Reconcile"/>.</para>
/// </summary>
public sealed class ClientPanelHost
{
    private readonly AppErrorLogger _errorLogger;
    private readonly Dictionary<(string Monitor, string Panel), ClientPanelWindow> _windows = [];
    private Dictionary<long, ClientOnMonitor> _clients = [];
    private readonly MonitorFocus _focus = new();
    private ForegroundWatch? _foreground;
    private MainWindowViewModel? _mainScreen;
    private bool _taskbarFailed;
    private int _raisingClients;

    /// <summary>The panels on screen, by window handle: their monitor, and whether it uses all of itself.</summary>
    private readonly Dictionary<long, (string Monitor, bool Cover)> _panels = [];

    /// <summary>What the taskbar was last told about each window, so only changes are sent.</summary>
    private readonly Dictionary<long, bool> _marked = [];

    /// <summary>Covered monitors with one of our ordinary windows in front of their layout.</summary>
    private HashSet<string> _oursOnTop = [];

    /// <summary>Each monitor's bounds, by key, read again whenever the layout is placed.</summary>
    private Dictionary<string, LayoutRect> _monitors = [];

    private bool _reconcilePosted;
    private readonly DispatcherTimer _reconcileAgain;

    public ClientPanelHost(ClientLayoutService service, AppErrorLogger errorLogger)
    {
        _errorLogger = errorLogger;
        service.PanelsChanged += states => Dispatcher.UIThread.Post(() => Update(states));
        service.ClientsChanged += clients => Dispatcher.UIThread.Post(() => UpdateClients(clients));
        // ⚠️ Built stopped: this constructor of DispatcherTimer starts it.
        _reconcileAgain = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _reconcileAgain!.Stop();
            Reconcile();
        });
        _reconcileAgain.Stop();
        FrontWindows.OursRaised += () => Dispatcher.UIThread.Post(ScheduleReconcile);
    }

    /// <summary>What panel items read and act through. Set before placement starts.</summary>
    public void UseMainScreen(MainWindowViewModel mainScreen) => _mainScreen = mainScreen;

    private void Update(IReadOnlyList<PanelState> states)
    {
        var wanted = states.ToDictionary(s => (s.MonitorKey, s.Panel.Id));
        foreach (var key in _windows.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            Forget(_windows[key]);
            _windows[key].Close();
            _windows.Remove(key);
        }

        foreach (var (key, state) in wanted)
        {
            _windows.TryGetValue(key, out var window);
            if (!state.Show)
            {
                // A hidden window loses its mark (the shell drops it), so it's sent again on showing.
                if (window is not null) Forget(window);
                window?.Hide();
                continue;
            }
            if (window == null)
            {
                _windows[key] = window = new ClientPanelWindow();
                var monitor = key.MonitorKey;
                window.Pressed += () => BringMonitorForward(monitor);
                // A button's work runs on the release, and may bring the main window forward again.
                window.Released += ScheduleReconcile;
                window.MonitorForwardRequested += () => BringMonitorForward(monitor);
            }
            window.Show(state.Panel, new PanelSurroundings(_mainScreen, key.MonitorKey, _focus));
            if (window.TryGetPlatformHandle()?.Handle is { } handle)
                _panels[handle.ToInt64()] = (key.MonitorKey, state.CoverTaskbar);
        }
        _foreground ??= new ForegroundWatch(ForegroundChanged, ScheduleReconcile);
        _monitors = ClientWindows.FindMonitors().ToDictionary(m => m.Key, m => m.Bounds);
        ScheduleReconcile();
    }

    private void Forget(ClientPanelWindow window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } handle) return;
        _panels.Remove(handle.ToInt64());
        _marked.Remove(handle.ToInt64());
    }

    private void UpdateClients(IReadOnlyCollection<ClientOnMonitor> wanted)
    {
        var now = wanted.ToDictionary(c => c.Handle);
        _focus.Update(wanted);

        // One already at the front won't be reported again, such as a client that has just arrived
        // on this monitor after login.
        var front = ForegroundWatch.Current;
        var newHere = now.TryGetValue(front, out var inFront)
            && (!_clients.TryGetValue(front, out var was) || was.MonitorKey != inFront.MonitorKey);
        _clients = now;
        ScheduleReconcile();
        if (newHere)
        {
            _focus.Foreground(front);
            BringPanelsForward(inFront.MonitorKey);
        }
    }

    /// <summary>
    /// Puts the taskbar right on every covered monitor, soon: once at the next quiet moment, and once
    /// more a little after, since the taskbar can decide before a window has finished coming forward.
    /// Asked for on every focus change, every restack and every placement; the asks fold into one.
    /// </summary>
    private void ScheduleReconcile()
    {
        if (!_reconcilePosted)
        {
            _reconcilePosted = true;
            Dispatcher.UIThread.Post(() => { _reconcilePosted = false; Reconcile(); }, DispatcherPriority.Background);
        }
        _reconcileAgain.Stop();
        _reconcileAgain.Start();
    }

    /// <summary>
    /// A covered monitor's clients and panels are marked full screen, so the taskbar moves behind
    /// them, except while one of our own ordinary windows (the main window, a window of tabs, a
    /// dialog) is in front of them there. Then they're unmarked, and the taskbar comes back.
    ///
    /// <para>⚠️ Why ours only. Marked for good, they kept the taskbar hidden under the main window:
    /// Windows doesn't bring it back going from a marked window to an unmarked one of the same
    /// process on the same monitor (a known Windows bug; Chromium and Firefox both work around it),
    /// and our panels are marked and share our process. Another program's window in front of a
    /// layout, Windows already handles: it shows the taskbar for that by itself.</para>
    /// </summary>
    private void Reconcile()
    {
        // ⚠️ Not twice at once. The call to the taskbar is cross-process COM, which can pump messages,
        // so a WinEvent report can arrive in the middle of ApplyMarks; it waits its turn instead.
        if (_reconciling)
        {
            ScheduleReconcile();
            return;
        }
        _reconciling = true;
        try { ReconcileNow(); }
        finally { _reconciling = false; }
    }

    private bool _reconciling;

    private void ReconcileNow()
    {
        var covered = _panels.Values.Where(p => p.Cover).Select(p => p.Monitor)
            .Concat(_clients.Values.Where(c => c.CoverTaskbar).Select(c => c.MonitorKey))
            .Distinct()
            .Where(_monitors.ContainsKey)
            .ToDictionary(k => k, k => _monitors[k]);
        var layout = _panels.ToDictionary(p => p.Key, p => p.Value.Monitor);
        foreach (var client in _clients.Values) layout[client.Handle] = client.MonitorKey;

        try { _oursOnTop = FrontWindows.OursOnTop(covered, layout); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _oursOnTop = []; }
        ApplyMarks();
    }

    /// <summary>
    /// Sends the taskbar the marks <see cref="Reconcile"/> worked out. Only what changed: each call
    /// makes the taskbar look again, on every monitor.
    ///
    /// <para>⚠️ Unmarks first, so a monitor giving up its layout gets its taskbar back before another
    /// loses its own.</para>
    /// </summary>
    private void ApplyMarks()
    {
        var want = new Dictionary<long, (bool On, bool Own)>();
        foreach (var (handle, panel) in _panels) want[handle] = (panel.Cover && !_oursOnTop.Contains(panel.Monitor), true);
        foreach (var client in _clients.Values) want[client.Handle] = (client.CoverTaskbar && !_oursOnTop.Contains(client.MonitorKey), false);

        // Gone from the layout: unmarked if it was marked, then forgotten. A closed window fails
        // quietly, which is fine.
        foreach (var gone in _marked.Keys.Where(h => !want.ContainsKey(h)).ToList())
        {
            if (_marked[gone]) MarkFullScreen(gone, false, own: false);
            _marked.Remove(gone);
        }

        foreach (var (handle, w) in want.OrderBy(w => w.Value.On))
        {
            // Told once on first sight even when not wanted: that clears a mark an earlier run of the
            // app left behind (one that crashed), and gives a panel its "never full screen" property.
            if (_marked.TryGetValue(handle, out var was) && was == w.On) continue;
            MarkFullScreen(handle, w.On, w.Own);
            _marked[handle] = w.On;
        }
    }

    private void ForegroundChanged(long window)
    {
        // At once, not posted behind whatever the UI is busy with: a client clicked while the main
        // window was in front has to lose the taskbar now. Then again shortly, for anything that
        // settles after (see ScheduleReconcile).
        Reconcile();
        ScheduleReconcile();
        if (!_clients.TryGetValue(window, out var client)) return;
        _focus.Foreground(window);
        BringPanelsForward(client.MonitorKey);

        // Beneath the one that just came forward, which itself isn't touched and stays on top of them.
        RaiseClients(window, [.. _clients.Values
            .Where(c => c.MonitorKey == client.MonitorKey && c.Handle != window)
            .Select(c => c.Handle)]);
    }

    /// <summary>Returns the panel that ended up lowest, or null if none are showing.</summary>
    private ClientPanelWindow? BringPanelsForward(string monitor)
    {
        ClientPanelWindow? lowest = null;
        foreach (var (key, window) in _windows)
        {
            if (key.Monitor != monitor || !window.IsVisible) continue;
            window.BringForward();
            lowest ??= window;
        }
        return lowest;
    }

    /// <summary>A panel was clicked.</summary>
    private void BringMonitorForward(string monitor)
    {
        var lowest = BringPanelsForward(monitor)?.TryGetPlatformHandle()?.Handle;
        // The layout now over whatever of ours was in front there: the taskbar steps aside again.
        ScheduleReconcile();
        if (lowest is not { } panel) return;
        RaiseClients(panel.ToInt64(), [.. _clients.Values.Where(c => c.MonitorKey == monitor).Select(c => c.Handle)]);
    }

    /// <summary>
    /// On a worker because each client has to answer, and one that is busy loading mustn't hold up
    /// the app; clicks during that wait don't queue up behind it.
    /// </summary>
    private void RaiseClients(long beneath, List<long> clients)
    {
        if (clients.Count == 0 || Interlocked.Exchange(ref _raisingClients, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                ClientWindows.PutBeneath(beneath, clients);
            }
            finally
            {
                Volatile.Write(ref _raisingClients, 0);
                Dispatcher.UIThread.Post(ScheduleReconcile);
            }
        });
    }

    /// <summary>
    /// The taskbar not moving is a look problem, not a reason to stop placing clients, so a failure
    /// is logged once and the rest carries on.
    /// </summary>
    private void MarkFullScreen(long handle, bool fullScreen, bool own)
    {
        if (_taskbarFailed) return;
        try
        {
            TaskbarHint.MarkFullScreen(handle, fullScreen, own);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _taskbarFailed = true;
            _errorLogger.Log("ClientLayouts", "moving the taskbar behind a full monitor layout", ex);
        }
    }

    /// <summary>The app is exiting. Clients stay where they are, and the taskbar goes back to normal.</summary>
    public void CloseAll()
    {
        _foreground?.Dispose();
        _foreground = null;
        _reconcileAgain.Stop();
        _focus.Update([]);
        // Nothing left in the layout: every window still marked is unmarked as gone.
        _clients = [];
        _panels.Clear();
        ApplyMarks();
        foreach (var window in _windows.Values) window.Close();
        _windows.Clear();
        _marked.Clear();
    }
}
