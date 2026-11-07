using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EveConsole.Services.ClientLayouts;

/// <summary>A monitor on this desktop, in real pixels.</summary>
public sealed record DesktopMonitor(string Key, LayoutRect Bounds, LayoutRect Work, bool Primary);

/// <summary>
/// The app's only contact with game clients: reading their window titles and positions, and
/// setting their size and position. Windows only; everywhere else it finds nothing.
///
/// <para>⚠️ This is a limit the owner set with the game's rules in mind. Never add input, focus
/// changes, window styles, memory access or screen capture for a client here or anywhere else.
/// That includes bringing a client to the front.</para>
/// </summary>
public static class ClientWindows
{
    private const string ClientProcessName = "exefile";

    public static List<ClientWindowInfo> FindClients()
    {
        var found = new List<ClientWindowInfo>();
        if (!OperatingSystem.IsWindows()) return found;

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var title = Title(h);
            if (!ClientPlacementEngine.IsClientTitle(title) || !IsClientProcess(h)) return true;
            if (!GetWindowRect(h, out var r)) return true;
            var style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
            found.Add(new ClientWindowInfo(
                h.ToInt64(),
                title,
                new LayoutRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                (style & WS_CAPTION) != WS_CAPTION,
                IsIconic(h)));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Moves a client without changing its stacking or focus. Asynchronous on purpose: a client
    /// that is busy loading can't hang the caller, and the next look shows whether it arrived.
    /// </summary>
    public static void Move(ClientMove move)
    {
        if (!OperatingSystem.IsWindows()) return;
        const uint flags = SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS;
        var r = move.Rect;
        if (!SetWindowPos(new IntPtr(move.Handle), IntPtr.Zero, r.X, r.Y, r.Width, r.Height, flags))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static List<DesktopMonitor> FindMonitors()
    {
        var found = new List<DesktopMonitor>();
        if (!OperatingSystem.IsWindows()) return found;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(m, ref info))
                found.Add(new DesktopMonitor(HardwareKey(info.szDevice), ToRect(info.rcMonitor), ToRect(info.rcWork), (info.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// The monitor's hardware identity rather than its Windows number, which can change. Falls back
    /// to the display name if Windows won't say.
    /// </summary>
    private static string HardwareKey(string displayName)
    {
        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(displayName, i, ref device, EDD_GET_DEVICE_INTERFACE_NAME); i++)
        {
            if ((device.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0 && !string.IsNullOrEmpty(device.DeviceID))
                return device.DeviceID;
            device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        }
        return displayName;
    }

    private static bool IsClientProcess(IntPtr h)
    {
        GetWindowThreadProcessId(h, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals(ClientProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;   // it just exited
        }
        catch (InvalidOperationException)
        {
            return false;   // the same, a moment later
        }
    }

    private static string Title(IntPtr h)
    {
        var text = new StringBuilder(128);
        GetWindowText(h, text, text.Capacity);
        return text.ToString();
    }

    private static LayoutRect ToRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200, SWP_ASYNCWINDOWPOS = 0x4000;
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;
    private const int DISPLAY_DEVICE_ACTIVE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr param);
    private delegate bool MonitorEnumProc(IntPtr m, IntPtr dc, IntPtr rect, IntPtr param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")] private static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE info, uint flags);
}
