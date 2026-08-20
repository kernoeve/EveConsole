using Avalonia;
using Avalonia.ReactiveUI;
using EveConsole.Services;
using Velopack;

namespace EveConsole;

class Program
{
    // Avalonia requires this to remain synchronous — don't add async here
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks (these invoke the exe
        // with special args and exit before the UI starts).
        VelopackApp.Build().Run();

        // One instance per user. Must come after the Velopack hooks — those invoke this exe with
        // special arguments and exit, and blocking them would break installs and updates.
        if (!SingleInstance.TryAcquire(args)) return;

        // One-time carry-forward from a pre-rename Eve Cortex install — must happen before any
        // config or database is read.
        AppConfig.MigrateLegacyDataIfNeeded();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
