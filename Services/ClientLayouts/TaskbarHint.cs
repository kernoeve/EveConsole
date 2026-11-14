using System.Runtime.InteropServices;

namespace EveConsole.Services.ClientLayouts;

/// <summary>
/// Tells the Windows taskbar to treat a window as full screen, so the taskbar on that monitor moves
/// behind it. That's what happens by itself for a window filling the whole monitor, and it stops
/// happening once panels share the monitor and no one window fills it.
///
/// <para>⚠️ This talks to the taskbar. Nothing is sent to the window named, so it stays within the
/// limit in <see cref="ClientWindows"/>: a client is told nothing and has nothing of its own changed.
/// Tested with a window in another process: the taskbar moves back whether or not that window has
/// focus, and only on its own monitor.</para>
///
/// <para>⚠️ A mark is not "while this window is active": the taskbar hides on every monitor whose
/// top window is marked. And going from a marked window to an unmarked one of the same process on
/// the same monitor, it doesn't come back (a known Windows bug: the shell only looks again when
/// the foreground moves to another process). So the marks come off a monitor while one of our own ordinary windows is in front
/// of its layout, by <c>ClientPanelHost.Reconcile</c>, rather than being set once and left.</para>
///
/// <para>Call this on the UI thread: the taskbar's COM object belongs to the thread that made it.</para>
/// </summary>
public static class TaskbarHint
{
    private static ITaskbarList2? _taskbar;

    /// <summary>
    /// Asking about a window that has just closed fails, which is fine: nothing is left to mark.
    ///
    /// <para><paramref name="ownWindow"/>: one of ours (a panel), which may also carry the shell's
    /// "never full screen" property while unmarked. ⚠️ Only on our own windows: setting a property
    /// on a client would be changing something of the client's.</para>
    /// </summary>
    public static void MarkFullScreen(long handle, bool fullScreen, bool ownWindow = false)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_taskbar == null)
        {
            var taskbar = (ITaskbarList2)new TaskbarList();
            Marshal.ThrowExceptionForHR(taskbar.HrInit());
            _taskbar = taskbar;
        }

        // ⚠️ An unmark alone isn't always "not full screen": it hands the window back to the shell's
        // own guess, and a panel holding a client in a hole can be as big as the monitor. On some
        // builds of Windows 10 and 11 that guess sticks; NonRudeHWND says no outright.
        // Set before the mark call, which is what makes the shell look again.
        var window = new IntPtr(handle);
        if (ownWindow)
        {
            if (fullScreen) RemoveProp(window, NonRude);
            else SetProp(window, NonRude, new IntPtr(1));
        }
        _ = _taskbar.MarkFullscreenWindow(window, fullScreen);
    }

    private const string NonRude = "NonRudeHWND";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr window, string name, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr window, string name);

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarList;

    [ComImport, Guid("602D4995-B13A-429b-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullScreen);
    }
}
