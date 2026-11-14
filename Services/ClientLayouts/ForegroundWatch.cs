using System.Runtime.InteropServices;

namespace EveConsole.Services.ClientLayouts;

/// <summary>
/// Says when a window comes to the front, so a monitor's panels can come forward with its client;
/// and, separately, when what's on top of a monitor may have changed without that (a window
/// minimised or restored, a move finished, one of our windows shown, hidden or clicked), so the
/// taskbar can be put right (see <see cref="FrontWindows"/>).
///
/// <para>Our own windows are reported too: the main window or a window of tabs coming to the front
/// is what brings the taskbar back over a layout.</para>
///
/// <para>⚠️ Windows reports this to us, and only which window it was. The hooks stay out of context
/// on purpose: nothing loads into any other process, a client included. Never change that.</para>
///
/// <para>Make it on the UI thread: Windows delivers the reports through the message loop of the
/// thread that asked, after the change, so a report never arrives inside our own Activate().</para>
/// </summary>
public sealed class ForegroundWatch : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND   = 0x0003;
    private const uint EVENT_SYSTEM_CAPTURESTART = 0x0008;
    private const uint EVENT_SYSTEM_MOVESIZEEND  = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND  = 0x0017;
    private const uint EVENT_OBJECT_SHOW         = 0x8002;
    private const uint EVENT_OBJECT_HIDE         = 0x8003;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT     = 0x0000;

    private readonly WinEventProc _foreground;    // kept: Windows holds only a pointer to them
    private readonly WinEventProc _restacked;
    private readonly List<IntPtr> _hooks = [];

    public ForegroundWatch(Action<long> changed, Action? restacked = null)
    {
        _foreground = (_, _, window, objectId, _, _, _) =>
        {
            if (objectId == 0) changed(window.ToInt64());   // the window itself, not a part of it
        };
        _restacked = (_, _, _, objectId, childId, _, _) =>
        {
            if (objectId == 0 && childId == 0) restacked?.Invoke();
        };
        if (!OperatingSystem.IsWindows()) return;

        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, _foreground, 0);
        if (restacked is null) return;
        // Any program's: a minimise or restore, or a move ending, changes what is on top of a monitor.
        Hook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND, _restacked, 0);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, _restacked, 0);
        // Ours only, as these come often: a window shown or hidden (the main window to the tray and
        // back), a click, which raises a window that already had focus without telling anyone, and a
        // window moved or resized some other way than by dragging (Win+Shift+Arrow, a snap, maximise
        // or restore, a window of tabs placed by our own code after a tab is dropped). The cursor's
        // and caret's own location reports are dropped by the objectId filter.
        var me = (uint)Environment.ProcessId;
        Hook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE, _restacked, me);
        Hook(EVENT_SYSTEM_CAPTURESTART, EVENT_SYSTEM_CAPTURESTART, _restacked, me);
        Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, _restacked, me);
    }

    private void Hook(uint min, uint max, WinEventProc proc, uint process)
    {
        var hook = SetWinEventHook(min, max, IntPtr.Zero, proc, process, 0, WINEVENT_OUTOFCONTEXT);
        if (hook != IntPtr.Zero) _hooks.Add(hook);
    }

    public static long Current => OperatingSystem.IsWindows() ? GetForegroundWindow().ToInt64() : 0;

    public void Dispose()
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
