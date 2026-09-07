using Avalonia;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using EveConsole.Services;
using Velopack;

namespace EveConsole;

class Program
{
    /// <summary>Run the background work with no window. See <see cref="RunHeadless"/>.</summary>
    public const string HeadlessArgument = "--headless";

    /// <summary>
    /// Run under the Windows service control manager. Implies headless, and additionally sends the
    /// app to <see cref="MachineConfig"/> for its database — a service is LocalSystem and has never
    /// seen the profile the desktop app saves into.
    /// </summary>
    public const string ServiceArgument = "--service";

    /// <summary>
    /// A notification-area icon in the user's session and nothing else. Started at logon so there
    /// is something to look at while the worker runs as a service, which cannot draw at all.
    /// </summary>
    public const string TrayArgument = "--tray";

    // Avalonia requires this to remain synchronous — don't add async here
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks (these invoke the exe
        // with special args and exit before the UI starts).
        VelopackApp.Build().Run();

        // ⚠️ Before everything else, and it exits. This is the elevated copy of ourselves, asked to
        // do the one thing an ordinary token cannot: create or delete a service and write a
        // machine-scoped credential. It must not take the single-instance lock, build a container
        // or open a database — its whole job is a handful of sc.exe calls and a file.
        if (OperatingSystem.IsWindows())
        {
            if (args.Any(a => string.Equals(a, WindowsServiceControl.InstallArgument, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.Exit(WindowsServiceControl.RunInstall());
                return;
            }

            if (args.Any(a => string.Equals(a, WindowsServiceControl.UninstallArgument, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.Exit(WindowsServiceControl.RunUninstall());
                return;
            }

            if (args.Any(a => string.Equals(a, WindowsServiceControl.RepointArgument, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.Exit(WindowsServiceControl.RunRepoint());
                return;
            }

            if (args.Any(a => string.Equals(a, WindowsServiceControl.AutoStartArgument, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.Exit(WindowsServiceControl.RunSetStartType(automatic: true));
                return;
            }

            if (args.Any(a => string.Equals(a, WindowsServiceControl.ManualArgument, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.Exit(WindowsServiceControl.RunSetStartType(automatic: false));
                return;
            }
        }

        var asTray = args.Any(a => string.Equals(a, TrayArgument, StringComparison.OrdinalIgnoreCase));
        if (asTray) AppRuntime.MarkTray();

        var asService = args.Any(a => string.Equals(a, ServiceArgument, StringComparison.OrdinalIgnoreCase));
        var headless  = asService
                     || args.Any(a => string.Equals(a, HeadlessArgument, StringComparison.OrdinalIgnoreCase));

        if (asService)     AppRuntime.MarkService();
        else if (headless) AppRuntime.MarkHeadless();

        // One instance per user, and only where that means anything: on SQLite. A worker and a
        // desktop client on one machine are two clients of one PostgreSQL database, which is the
        // arrangement this whole mode exists to serve.
        if (!SingleInstance.TryAcquire(args)) return;

        // One-time carry-forward from a pre-rename Eve Cortex install — must happen before any
        // config or database is read.
        AppConfig.MigrateLegacyDataIfNeeded();

        if (asService && OperatingSystem.IsWindows())
            System.ServiceProcess.ServiceBase.Run(new WindowsServiceHost(RunWorker));
        else if (headless)
            RunHeadless(args);
        else
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            // A backstop for the exits that do not pass through App's ShutdownRequested handler —
            // the version gate, the tray process, a lifetime shut down from somewhere else. Same
            // reason as there: off Windows, letting a fully started Avalonia process unwind races
            // its own D-Bus teardown, and the loser is an unhandled TaskCanceledException that
            // aborts the process with SIGABRT. See AppLauncher.ExitNow.
            if (!OperatingSystem.IsWindows()) AppLauncher.ExitNow();
        }
    }

    /// <summary>
    /// Sets Avalonia up and runs the dispatcher until the token is cancelled, then releases the
    /// lease. The whole of the worker, shared by <c>--headless</c> and the Windows service so the
    /// two cannot come to mean different things.
    /// </summary>
    private static int RunWorker(CancellationToken stopping)
    {
        BuildAvaloniaApp().SetupWithoutStarting();
        Dispatcher.UIThread.MainLoop(stopping);
        Shutdown();
        return 0;
    }

    /// <summary>
    /// Borrows the console of whoever launched us.
    ///
    /// <para>⚠️ Windows only, and not optional there. The project is a WinExe, which is what keeps
    /// a console window from flashing up behind the desktop app — but it also means the process
    /// starts with no console at all, so every Console.Write goes nowhere. A worker run from a
    /// terminal printed absolutely nothing until this was added, which is precisely the silence
    /// the startup banner exists to break.</para>
    ///
    /// <para>Failure is normal and ignored: a service or a scheduled task has no parent console to
    /// attach to. Linux needs none of this — stdout is already there.</para>
    /// </summary>
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    /// <summary>
    /// Starts the application with no windows and runs until told to stop.
    ///
    /// <para>⚠️ Avalonia is still set up, and the dispatcher still runs. It is tempting to treat
    /// headless as "no UI framework at all", and it does not work: the activity log schedules its
    /// flush through a DispatcherTimer, and several handlers post to the UI thread. Without a
    /// dispatcher loop those never run — the log would grow and never publish, and the worker
    /// would look alive while relaying nothing. SetupWithoutStarting gives the dispatcher without
    /// giving a window.</para>
    ///
    /// <para>No lifetime is set, which is what the startup path keys off: every window, the splash
    /// and the shutdown handler are already guarded on a classic desktop lifetime, so they skip on
    /// their own rather than needing a second startup written for this mode.</para>
    /// </summary>
    private static void RunHeadless(string[] args)
    {
        if (OperatingSystem.IsWindows())
            try { AttachConsole(AttachParentProcess); } catch { /* no console to borrow */ }

        using var stopping = new CancellationTokenSource();

        // ⚠️ Handled through PosixSignalRegistration rather than ProcessExit or the load context's
        // Unloading event. Those fire while .NET is already tearing the process down, which leaves
        // the clean release below racing the runtime — and losing it means the lease goes when the
        // socket finally closes instead of now, so the next client waits out the server's timeout
        // rather than taking over on its next tick. Cancel = true here says the shutdown is ours to
        // run; nothing kills the process until we return from the loop.
        //
        // SIGTERM is what systemd, Docker and every other supervisor send. SIGINT is Ctrl+C, which
        // is how somebody runs it in a terminal to watch what it does. Both mean the same thing.
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            Console.WriteLine("Stopping…");
            stopping.Cancel();
        }

        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using var sigint  = PosixSignalRegistration.Create(PosixSignal.SIGINT,  OnSignal);

        // The lease is released on the way out rather than left to the socket closing, so whichever
        // client takes over does so on its next tick instead of waiting for the server to notice.
        RunWorker(stopping.Token);
    }

    private static void Shutdown()
    {
        try
        {
            (App.Services?.GetService(typeof(WorkerLease))   as WorkerLease)?.Stop();
            (App.Services?.GetService(typeof(ClientSignals)) as ClientSignals)?.Stop();
        }
        catch { /* going away regardless; a failure here has nobody left to tell */ }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
