using Avalonia;
using Avalonia.ReactiveUI;
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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
