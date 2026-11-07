using System.Runtime.InteropServices;

namespace EveConsole.Services.ClientLayouts;

/// <summary>
/// Says when another program's window comes to the front, so a monitor's panels can come forward
/// with its client.
///
/// <para>⚠️ Windows reports this to us, and only which window it was. The hook stays out of context
/// on purpose: nothing loads into any other process, a client included. Never change that.</para>
///
/// <para>Make it on the UI thread: Windows delivers the reports through the message loop of the
/// thread that asked.</para>
/// </summary>
public sealed class ForegroundWatch : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;    // WINEVENT_OUTOFCONTEXT is 0

    private readonly WinEventProc _callback;    // kept: Windows holds only a pointer to it
    private IntPtr _hook;

    public ForegroundWatch(Action<long> changed)
    {
        _callback = (_, _, window, objectId, _, _, _) =>
        {
            if (objectId == 0) changed(window.ToInt64());   // the window itself, not a part of it
        };
        if (OperatingSystem.IsWindows())
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, WINEVENT_SKIPOWNPROCESS);
    }

    public static long Current => OperatingSystem.IsWindows() ? GetForegroundWindow().ToInt64() : 0;

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
