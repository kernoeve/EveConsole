using Avalonia;
using Avalonia.ReactiveUI;
using EveCortex.Services;
using Velopack;

namespace EveCortex;

class Program
{
    // Avalonia requires this to remain synchronous — don't add async here
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks (these invoke the exe
        // with special args and exit before the UI starts).
        VelopackApp.Build().Run();

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
