using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace EveConsole.Services;

/// <summary>
/// The optional notification-area icon: what the background work is doing, without a window.
///
/// <para>⚠️ Its own process, started at logon with --tray, and it has to be. A Windows service
/// runs in session 0, which has no interactive desktop, so the process actually doing the
/// background work is incapable of putting anything in the notification area. Putting the icon in
/// the desktop client instead would mean it vanished whenever that client was closed — which is
/// precisely when somebody wants to know whether the worker is still going.</para>
///
/// <para>It reads: the worker row from the database, changes pushed over <see cref="ClientSignals"/>.
/// ⚠️ It does NOT handle alarms. Closing the desktop client is how somebody turns notifications
/// off, and a tray icon that went on sounding them would have taken that decision away from
/// them.</para>
///
/// <para>Off by default. An icon nobody asked for is clutter, and this one is only useful to
/// somebody who has a worker to keep an eye on.</para>
/// </summary>
public sealed class TrayIconController
{
    private readonly WorkerLease           _lease;
    private readonly WorkerActivityService _activity;

    private TrayIcon?       _icon;
    private DispatcherTimer? _poll;

    /// <summary>Starts a fresh EVE Console client. There is no window of ours to bring back.</summary>
    public Action? ShowWindow { get; set; }

    /// <summary>Ends this tray process. ⚠️ Not the background work — that belongs to the worker.</summary>
    public Action? Quit { get; set; }

    public TrayIconController(WorkerLease lease, WorkerActivityService activity)
    {
        _lease    = lease;
        _activity = activity;
    }

    public bool IsVisible => _icon is not null;

    // ── Starting with the session ─────────────────────────────────────────────

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName    = "EveConsoleTray";

    /// <summary>
    /// ⚠️ HKEY_CURRENT_USER on Windows, and <c>~/.config/autostart</c> on Linux — both per-person
    /// and per-session, which is exactly what a tray icon is. The machine-wide equivalents would
    /// ask for administrator approval to add an icon to one user's notification area, and would put
    /// it in everyone's.
    /// </summary>
    public static bool StartsAtLogon()
    {
        if (OperatingSystem.IsLinux()) return LinuxAutostart.StartsAtLogin();
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunName) is string;
        }
        catch { return false; }
    }

    public static void SetStartsAtLogon(bool on)
    {
        if (OperatingSystem.IsLinux()) { LinuxAutostart.SetStartsAtLogin(on); return; }
        if (!OperatingSystem.IsWindows()) return;

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null) return;

        if (!on) { key.DeleteValue(RunName, throwOnMissingValue: false); return; }

        // ⚠️ Quoted. Program Files has a space in it, and an unquoted path there is read as a
        // command plus arguments — Windows would try to run "C:\Program" and report nothing.
        if (AppLauncher.RelaunchPath is { } exe)
            key.SetValue(RunName, $"\"{exe}\" {Program.TrayArgument}");
    }

    /// <summary>Starts a tray process now, so ticking the box does something visible today.</summary>
    public static string? LaunchNow()
    {
        try
        {
            // ⚠️ RelaunchPath, not ProcessPath. Under an AppImage the latter is the binary inside
            // the temporary mount, and starting that directly skips the AppImage's own runtime —
            // it would come up without the environment its bundled libraries are found through.
            if (AppLauncher.RelaunchPath is not { } exe) return "Could not determine this application's path.";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, Program.TrayArgument)
            { UseShellExecute = true });

            return null;
        }
        catch (Exception ex) { return ex.Message.Split('\n')[0]; }
    }

    public void Show()
    {
        if (_icon is not null) return;

        try
        {
            _icon = new TrayIcon
            {
                Icon        = new WindowIcon(AssetLoader.Open(new Uri("avares://EveConsole/Assets/ec.ico"))),
                ToolTipText = "EVE Console",
                IsVisible   = true,
                Menu        = BuildMenu(),
            };

            // Double-click is the convention for "give me the window back"; Avalonia surfaces a
            // single Clicked, which is close enough and less fiddly than nothing.
            _icon.Clicked += (_, _) => ShowWindow?.Invoke();

            _activity.Changed += Refresh;

            // ⚠️ Polled as well. The lease's own events describe THIS process, and the whole point
            // of the icon is to report on a worker that is usually somebody else — a handover
            // elsewhere raises nothing here.
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _poll.Tick += (_, _) => Refresh();
            _poll.Start();

            Refresh();
        }
        catch
        {
            // A desktop with no notification area, or a platform that has none. The application is
            // entirely usable without it, so this is not worth reporting as a fault.
            _icon = null;
        }
    }

    public void Hide()
    {
        _activity.Changed -= Refresh;

        _poll?.Stop();
        _poll = null;

        if (_icon is null) return;

        _icon.IsVisible = false;
        _icon.Dispose();
        _icon = null;
    }

    /// <summary>
    /// Opens the Background Processes window. Set by whoever can build one.
    ///
    /// <para>The reason to have this on the menu at all: it is the whole monitoring view, and
    /// wanting it is not the same as wanting the application.</para>
    /// </summary>
    public Action? ShowBackgroundProcesses { get; set; }

    /// <summary>
    /// ⚠️ Built fresh on each open rather than once. NativeMenu is small, and a menu assembled at
    /// construction would keep whatever the first pass decided.
    /// </summary>
    private NativeMenu BuildMenu()
    {
        var monitor = new NativeMenuItem("Background Processes");
        monitor.Click += (_, _) => ShowBackgroundProcesses?.Invoke();

        var open = new NativeMenuItem("Open EVE Console");
        open.Click += (_, _) => ShowWindow?.Invoke();

        var quit = new NativeMenuItem("Exit");
        quit.Click += (_, _) => Quit?.Invoke();

        // ⚠️ No mute here, and no alarms anywhere in this process. Closing the desktop client is
        // how somebody turns notifications off; a tray icon that went on sounding them would have
        // taken that decision away, and a mute control would only be needed by a thing that made
        // noise. Exit ends this icon, not the background work — that belongs to the worker.
        return [monitor, open, new NativeMenuItemSeparator(), quit];
    }

    private void Refresh() => _ = RefreshAsync();

    /// <summary>
    /// ⚠️ Reads the worker row off the UI thread, then marshals only the assignment. Doing this
    /// synchronously is the obvious shortcut and it is a database round trip on the thread that
    /// draws — on a slow link a tooltip would stall the whole window, which is the fault this
    /// application already keeps a stall monitor to catch.
    ///
    /// <para>The activity signal that reaches here is raised from its listener, not the UI thread,
    /// so the marshalling is needed regardless of where the read happens.</para>
    /// </summary>
    private async Task RefreshAsync()
    {
        string worker;

        if (_lease.IsHolder)
        {
            worker = "Background work: this client";
        }
        else
        {
            var s = await WorkerLease.ReadStatusAsync();
            worker = s is not null && WorkerLease.IsLive(s)
                ? $"Background work: {s.HostName}{(s.Headless ? " (service)" : "")}"
                : "Background work: nothing is running it";
        }

        var tip = $"EVE Console\n{worker}";

        Dispatcher.UIThread.Post(() =>
        {
            if (_icon is null) return;

            // Rebuilt rather than edited: the mute item's text changes with the state, and a
            // NativeMenu is small enough that replacing it beats tracking which item to update.
            _icon.Menu        = BuildMenu();
            _icon.ToolTipText = tip;
        });
    }
}
