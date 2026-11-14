using System.Runtime.InteropServices;

namespace EveConsole.Services.ClientLayouts;

/// <summary>
/// Which monitors have one of our own ordinary windows (the main window, a window of tabs, a
/// dialog) above their clients and panels: there, the taskbar has to come back.
///
/// <para>⚠️ Ours only. A window of another program in front of the layout is something Windows
/// already gets right: the taskbar comes back for it by itself. What it gets wrong is a window of
/// the same process as a marked one, on the same monitor (a known Windows bug: the shell only
/// looks again when the foreground moves to another process), and our panels are marked and
/// share our process.</para>
///
/// <para>⚠️ Read only, and nothing that waits on a window's owner: EnumWindows, visibility,
/// position and style are answered by Windows itself, so a busy client can't hold this up. Nothing
/// is ever sent to a client.</para>
/// </summary>
public static class FrontWindows
{
    /// <param name="covered">The monitors whose layout covers the taskbar, by key, with their bounds.</param>
    /// <param name="layout">The clients and panels on screen, by window handle, with their monitor.</param>
    public static HashSet<string> OursOnTop(IReadOnlyDictionary<string, LayoutRect> covered, IReadOnlyDictionary<long, string> layout)
    {
        var onTop = new HashSet<string>();
        if (!OperatingSystem.IsWindows() || covered.Count == 0) return onTop;

        var decided = new HashSet<string>();
        var me = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            // Top to bottom. The first of the layout's own windows on a monitor settles it.
            if (!IsWindowVisible(h) || IsIconic(h) || IsCloaked(h)) return true;
            if (layout.TryGetValue(h.ToInt64(), out var monitor))
            {
                if (covered.ContainsKey(monitor)) decided.Add(monitor);
            }
            else if (GetWindowThreadProcessId(h, out var pid) != 0 && pid == me && !FloatsOverGames(h)
                  && MonitorOf(h) is { } bounds)
            {
                // ⚠️ One monitor per window, the one Windows itself gives it (the one it is mostly
                // on), as the taskbar does. Counting every monitor it touched put the taskbar back
                // over a whole layout for a window's edge poking past the bezel.
                foreach (var (key, b) in covered)
                    if (b == bounds && decided.Add(key)) onTop.Add(key);
            }
            return decided.Count < covered.Count;
        }, IntPtr.Zero);
        return onTop;
    }

    /// <summary>
    /// One of our windows that floats over the games rather than standing apart from them: a tool
    /// window, one that never takes focus, or one kept on top that isn't in the taskbar (the Mumble
    /// overlay and talk popup, a menu or dropdown). The layout keeps the taskbar hidden under those.
    /// An alarm is on top but in the taskbar: a dialog to answer, and counted like the main window.
    /// </summary>
    private static bool FloatsOverGames(IntPtr window)
    {
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
        var style = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
        return (style & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0
            || ((style & WS_EX_TOPMOST) != 0 && (style & WS_EX_APPWINDOW) == 0);
    }

    /// <summary>The bounds of the monitor Windows counts a window as on. Asked of Windows, not the window.</summary>
    private static LayoutRect? MonitorOf(IntPtr window)
    {
        const uint MONITOR_DEFAULTTONEAREST = 2;
        var monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
        var r = info.rcMonitor;
        return new LayoutRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>
    /// Our own windows say so when they bring themselves forward without a change of focus (the
    /// main window raised by a panel's button while it already had focus), which Windows doesn't
    /// report. <c>ClientPanelHost</c> puts the taskbar right on it.
    /// </summary>
    public static event Action? OursRaised;

    public static void NoteRaised() => OursRaised?.Invoke();

    /// <summary>A window on another virtual desktop, or one Windows keeps hidden though "visible".</summary>
    private static bool IsCloaked(IntPtr window)
    {
        const int DWMWA_CLOAKED = 14;
        return DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out int value, int size);
}
