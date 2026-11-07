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
/// clients and panels. Both are needed, as the taskbar goes by whichever of them is on top.</para>
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

    public ClientPanelHost(ClientLayoutService service, AppErrorLogger errorLogger)
    {
        _errorLogger = errorLogger;
        service.PanelsChanged += states => Dispatcher.UIThread.Post(() => Update(states));
        service.ClientsChanged += clients => Dispatcher.UIThread.Post(() => UpdateClients(clients));
    }

    /// <summary>What panel items read and act through. Set before placement starts.</summary>
    public void UseMainScreen(MainWindowViewModel mainScreen) => _mainScreen = mainScreen;

    private void Update(IReadOnlyList<PanelState> states)
    {
        var wanted = states.ToDictionary(s => (s.MonitorKey, s.Panel.Id));
        foreach (var key in _windows.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            _windows[key].Close();
            _windows.Remove(key);
        }

        foreach (var (key, state) in wanted)
        {
            _windows.TryGetValue(key, out var window);
            if (!state.Show)
            {
                window?.Hide();
                continue;
            }
            if (window == null)
            {
                _windows[key] = window = new ClientPanelWindow();
                var monitor = key.MonitorKey;
                window.Pressed += () => BringMonitorForward(monitor);
            }
            window.Show(state.Panel, new PanelSurroundings(_mainScreen, key.MonitorKey, _focus));
            if (window.TryGetPlatformHandle()?.Handle is { } handle)
                MarkFullScreen(handle.ToInt64(), state.CoverTaskbar);
        }
        _foreground ??= new ForegroundWatch(ForegroundChanged);
    }

    private void UpdateClients(IReadOnlyCollection<ClientOnMonitor> wanted)
    {
        var now = wanted.ToDictionary(c => c.Handle);
        _focus.Update(wanted);
        foreach (var old in _clients.Values.Where(c => c.CoverTaskbar))
            if (!now.TryGetValue(old.Handle, out var client) || !client.CoverTaskbar)
                MarkFullScreen(old.Handle, false);
        foreach (var client in wanted.Where(c => c.CoverTaskbar))
            if (!_clients.TryGetValue(client.Handle, out var old) || !old.CoverTaskbar)
                MarkFullScreen(client.Handle, true);

        // One already at the front won't be reported again, such as a client that has just arrived
        // on this monitor after login.
        var front = ForegroundWatch.Current;
        var newHere = now.TryGetValue(front, out var inFront)
            && (!_clients.TryGetValue(front, out var was) || was.MonitorKey != inFront.MonitorKey);
        _clients = now;
        if (newHere)
        {
            _focus.Foreground(front);
            BringPanelsForward(inFront.MonitorKey);
        }
    }

    private void ForegroundChanged(long window)
    {
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
            }
        });
    }

    /// <summary>
    /// The taskbar not moving is a look problem, not a reason to stop placing clients, so a failure
    /// is logged once and the rest carries on.
    /// </summary>
    private void MarkFullScreen(long handle, bool fullScreen)
    {
        if (_taskbarFailed) return;
        try
        {
            TaskbarHint.MarkFullScreen(handle, fullScreen);
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
        UpdateClients([]);
        foreach (var window in _windows.Values) window.Close();
        _windows.Clear();
    }
}
