using Avalonia;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using EveConsole.Services;
using Velopack;

namespace EveConsole;

class Program
{
    /// <summary>Run the background work with no window. See <see cref="RunHeadless"/>.</summary>
    public const string HeadlessArgument = "--headless";

    // Avalonia requires this to remain synchronous — don't add async here
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks (these invoke the exe
        // with special args and exit before the UI starts).
        VelopackApp.Build().Run();

        var headless = args.Any(a => string.Equals(a, HeadlessArgument, StringComparison.OrdinalIgnoreCase));
        if (headless) AppRuntime.MarkHeadless();

        // One instance per user, and only where that means anything: on SQLite. A worker and a
        // desktop client on one machine are two clients of one PostgreSQL database, which is the
        // arrangement this whole mode exists to serve.
        if (!SingleInstance.TryAcquire(args)) return;

        // One-time carry-forward from a pre-rename Eve Cortex install — must happen before any
        // config or database is read.
        AppConfig.MigrateLegacyDataIfNeeded();

        if (headless) RunHeadless(args);
        else          BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    private static void RunHeadless(string[] args)
    {
        if (OperatingSystem.IsWindows())
            try { AttachConsole(AttachParentProcess); } catch { /* no console to borrow */ }

        BuildAvaloniaApp().SetupWithoutStarting();

        using var stopping = new CancellationTokenSource();

        // ⚠️ Both signals. Ctrl+C is how somebody runs it in a terminal to see what it does;
        // SIGTERM is how every service manager and container runtime asks a process to stop, and a
        // worker that ignored it would be killed instead — dropping its lease the slow way, so the
        // next client waits out the socket rather than taking over on its next tick.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;              // ours to handle: stop cleanly rather than being killed
            Console.WriteLine("Stopping…");
            stopping.Cancel();
        };

        System.Runtime.Loader.AssemblyLoadContext.Default.Unloading += _ => stopping.Cancel();

        Dispatcher.UIThread.MainLoop(stopping.Token);

        // The lease is released explicitly rather than left to the socket closing, so whichever
        // client takes over does so on its next tick instead of waiting for the server to notice.
        Shutdown();
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
