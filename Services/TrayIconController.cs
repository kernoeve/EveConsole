using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using EveConsole.Localization;

namespace EveConsole.Services;

/// <summary>
/// The desktop app's notification-area icon: always there while the app runs, saying which client
/// does the background work, and bringing the main window back.
///
/// <para>With panels and Tool Containers, the main window is no longer needed for the app to be
/// useful, so closing it can leave the app running here instead (see
/// <see cref="AppConfig.GetKeepRunningInTray"/>). Exit on its menu ends the app.</para>
///
/// <para>⚠️ In the desktop app's own process. It used to be a process of its own, started at logon
/// with --tray, so as to show while the app was closed. A worker running as a Windows service still
/// can't show it (session 0 has no desktop); the desktop app can, for as long as it runs.</para>
/// </summary>
public sealed class TrayIconController
{
    private readonly WorkerLease           _lease;
    private readonly WorkerActivityService _activity;

    private TrayIcon?       _icon;
    private DispatcherTimer? _poll;

    /// <summary>Brings the main window back, shown and in front.</summary>
    public Action? ShowWindow { get; set; }

    /// <summary>Ends the app, as closing the main window does when it isn't kept running here.</summary>
    public Action? Quit { get; set; }

    public TrayIconController(WorkerLease lease, WorkerActivityService activity)
    {
        _lease    = lease;
        _activity = activity;
    }

    public bool IsVisible => _icon is not null;

    // ── The separate tray process this replaced ───────────────────────────────

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName    = "EveConsoleTray";

    /// <summary>
    /// Takes away what started the old tray process at logon — the Run value on Windows,
    /// <c>~/.config/autostart</c> on Linux, <c>~/Library/LaunchAgents</c> on macOS — and, for
    /// someone who had it on, keeps the app running in the notification area instead: they
    /// wanted it there. Run by the desktop app as it starts, and by a leftover --tray start,
    /// which then exits. Nothing to do once it's gone.
    /// </summary>
    public static void RetireLogonTray()
    {
        try
        {
            if (!StartedAtLogon()) return;
            if (OperatingSystem.IsLinux()) LinuxAutostart.Remove();
            else if (OperatingSystem.IsMacOS()) MacAutostart.Remove();
            else if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(RunName, throwOnMissingValue: false);
            }
            AppConfig.SetKeepRunningInTray(true);
        }
        catch
        {
            // Left as it was; tried again at the next start. A leftover --tray start just exits.
        }
    }

    private static bool StartedAtLogon()
    {
        if (OperatingSystem.IsLinux()) return LinuxAutostart.StartsAtLogin();
        if (OperatingSystem.IsMacOS()) return MacAutostart.StartsAtLogin();
        if (!OperatingSystem.IsWindows()) return false;

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunName) is string;
    }

    public void Show()
    {
        if (_icon is not null) return;

        try
        {
            _icon = new TrayIcon
            {
                // The menu bar draws its icons from a PNG; an .ico there shows as a blank slot.
                Icon        = new WindowIcon(AssetLoader.Open(new Uri(OperatingSystem.IsMacOS()
                                  ? "avares://EveConsole/Assets/logo_mark.png"
                                  : "avares://EveConsole/Assets/ec.ico"))),
                ToolTipText = "EVE Console",
                IsVisible   = true,
                Menu        = BuildMenu(),
            };

            // Double-click is the convention for "give me the window back"; Avalonia surfaces a
            // single Clicked, so a double-click (or a single one) brings it back.
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
    /// ⚠️ Built ONCE, when the icon is made. Nothing on it changes, and the menu bar on macOS will
    /// not have a tray icon's menu replaced: every swap threw "The menu being updated does not
    /// match" from Avalonia.Native, unhandled, every 30 seconds for as long as the icon was up.
    /// </summary>
    private NativeMenu BuildMenu()
    {
        var monitor = new NativeMenuItem(ShellText.NavBackgroundProcesses);
        monitor.Click += (_, _) => ShowBackgroundProcesses?.Invoke();

        var open = new NativeMenuItem(ShellText.TrayOpenApp);
        open.Click += (_, _) => ShowWindow?.Invoke();

        var quit = new NativeMenuItem(ShellText.TrayExit);
        quit.Click += (_, _) => Quit?.Invoke();

        // Exit ends the app, alarms and all; the background work goes on wherever else it runs.
        return [open, monitor, new NativeMenuItemSeparator(), quit];
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

        // The tooltip only: the menu is fixed (see BuildMenu).
        Dispatcher.UIThread.Post(() =>
        {
            if (_icon is not null) _icon.ToolTipText = tip;
        });
    }
}
