using Avalonia;
using Avalonia.ReactiveUI;

namespace EveCortex;

class Program
{
    // Avalonia requires this to remain synchronous — don't add async here
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
