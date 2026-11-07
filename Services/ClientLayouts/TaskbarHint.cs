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
/// <para>Call this on the UI thread: the taskbar's COM object belongs to the thread that made it.</para>
/// </summary>
public static class TaskbarHint
{
    private static ITaskbarList2? _taskbar;

    /// <summary>Asking about a window that has just closed fails, which is fine: nothing is left to mark.</summary>
    public static void MarkFullScreen(long handle, bool fullScreen)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_taskbar == null)
        {
            var taskbar = (ITaskbarList2)new TaskbarList();
            Marshal.ThrowExceptionForHR(taskbar.HrInit());
            _taskbar = taskbar;
        }
        _ = _taskbar.MarkFullscreenWindow(new IntPtr(handle), fullScreen);
    }

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
