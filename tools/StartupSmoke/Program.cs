using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using EveConsole;
using EveConsole.Localization;
using EveConsole.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  Startup smoke test
//
//  Answers one question on a machine nobody here can sit at: does EVE Console start?
//
//  Built for macOS, where there is no Mac to try it on — CI runs it on GitHub's macOS runner —
//  but it asks the same question anywhere. It starts the real App on a throwaway data folder,
//  as a first run would find it, on Avalonia's headless platform with real drawing; waits for
//  start-up to settle; saves a picture of every window that opened; and checks that a secret
//  survives the platform's own store (the Keychain on macOS).
//
//  It prints one verdict line, "SMOKE PASS" or "SMOKE FAIL: …", and CI looks for it. ⚠️ Not
//  the exit code: off Windows the app leaves through AppLauncher.ExitNow, which has to skip the
//  native teardown, and the code it leaves with is the app's own.
//
//  Usage: StartupSmoke [output folder] [seconds to wait, default 40]
// ─────────────────────────────────────────────────────────────────────────────

var outDir  = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "eveconsole-smoke"));
var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 40;
Directory.CreateDirectory(outDir);

var failures = new List<string>();

// What Program.Main does first: start-up reads Velopack's locator, and without it the splash
// says "Startup failed" and no main window ever opens.
Velopack.VelopackApp.Build().Run();

// A data folder of its own, empty: what a first run finds. Nothing of a real install is read.
var profile = Path.Combine(outDir, "profile");
if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
AppConfig.UseProfile(profile);
Languages.ApplyAtStartup([]);
Console.WriteLine($"data folder   {AppConfig.AppDataDir}");

// ── A secret through the platform's store ────────────────────────────────────
{
    var probe  = $"smoke ü \"quoted\" {Guid.NewGuid():N}";
    var stored = SecretStore.Protect(probe, "smoke-test");
    var back   = SecretStore.Unprotect(stored);
    Console.WriteLine($"secrets       {SecretStore.Available}, stored protected: {SecretStore.IsProtected(stored)}");
    if (back != probe) failures.Add("a saved secret did not read back as it was written");
    if (OperatingSystem.IsMacOS() && !SecretStore.IsProtected(stored))
        failures.Add("the macOS Keychain did not take the secret");
}

// ── The app itself ───────────────────────────────────────────────────────────
void Check()
{
    try
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life)
        {
            failures.Add("no desktop lifetime");
            Finish(null);
            return;
        }

        var windows = life.Windows.ToList();
        Console.WriteLine($"windows       {windows.Count}");
        if (windows.Count == 0) failures.Add("no window opened");
        if (life.MainWindow is null) failures.Add("no main window");

        for (var i = 0; i < windows.Count; i++)
        {
            var w    = windows[i];
            var path = Path.Combine(outDir, $"window-{i}-{w.GetType().Name}.png");
            w.CaptureRenderedFrame()?.Save(path);
            Console.WriteLine($"  {w.GetType().Name,-28} '{w.Title}'  {w.Bounds.Width:0}×{w.Bounds.Height:0}  {path}");
        }

        Finish(life);
    }
    catch (Exception ex)
    {
        failures.Add($"checking the windows threw: {ex.GetType().Name}: {ex.Message}");
        Finish(null);
    }
}

void Finish(IClassicDesktopStyleApplicationLifetime? life)
{
    Console.WriteLine(failures.Count == 0 ? "SMOKE PASS" : "SMOKE FAIL: " + string.Join("; ", failures));
    Console.Out.Flush();
    if (life is not null) life.Shutdown(failures.Count == 0 ? 0 : 1);
    else AppLauncher.ExitNow(1);
}

try
{
    AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont()
        .LogToTrace()
        .UseReactiveUI()
        .AfterSetup(_ => DispatcherTimer.RunOnce(Check, TimeSpan.FromSeconds(seconds)))
        .StartWithClassicDesktopLifetime([]);
}
catch (Exception ex)
{
    Console.WriteLine($"SMOKE FAIL: the app threw while starting: {ex}");
    Console.Out.Flush();
    AppLauncher.ExitNow(1);
}

// Off Windows a started Avalonia process must not unwind (see AppLauncher.ExitNow).
AppLauncher.ExitNow(failures.Count == 0 ? 0 : 1);
