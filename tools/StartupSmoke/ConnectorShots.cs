using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;

using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.LogicalTree;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Connector;
using EveConsole.ViewModels;
using EveConsole.Views;
using Microsoft.EntityFrameworkCore;

// ─────────────────────────────────────────────────────────────────────────────
//  Connector settings pictures: StartupSmoke --connectors <output folder>
//
//  Renders Settings → Connectors from made-up connectors on a throwaway SQLite file, in the dark and
//  the light theme, clicks through it (selects each entry, ticks a function, ticks a scope another
//  connector already holds), saves a picture after each step, and prints every binding error Avalonia
//  logged. No network: the connectors are rows, and nothing here connects or refreshes.
//
//  ⚠️ The real Settings window, with a stand-in for its view model that has only ConnectorVm: errors
//  from the other tabs' bindings are counted, and only those under the Connectors tab printed.
//
//  Ids are invented: alliances 99xxxxxxx, corporations 98xxxxxxx, pilots 90xxxxxxx.
// ─────────────────────────────────────────────────────────────────────────────

static class ConnectorShots
{
    public static void Run(string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);

        // ⚠️ SQLite, pinned, and a data folder of its own: nothing of a real install is read or written.
        DbEngine.Pin(DbBackend.Sqlite);
        var profile = Path.Combine(outDir, "profile");
        if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        AppConfig.UseProfile(profile);
        Languages.ApplyAtStartup([]);

        var sink = new BindingSink();
        Logger.Sink = sink;

        AppBuilder.Configure<ShotApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .UseReactiveUI()
            .SetupWithoutStarting();
        Logger.Sink = sink;
        AvaloniaSynchronizationContext.InstallIfNeeded();

        foreach (var theme in new[] { "dark", "light" })
        {
            ThemeService.Apply(theme);
            sink.Step = theme + " setup";
            Scenario(outDir, theme, sink);
        }

        Console.WriteLine();
        Console.WriteLine($"binding errors under the Connectors tab: {sink.Inside.Count}");
        foreach (var e in sink.Inside.Distinct()) Console.WriteLine("  " + e);
        Console.WriteLine($"binding errors elsewhere in the window (stand-in view model, ignored): {sink.OutsideCount}");
        File.WriteAllLines(Path.Combine(outDir, "binding-errors-outside.txt"), sink.Outside.Distinct());
        Console.WriteLine($"other warnings/errors: {sink.Other.Count}");
        foreach (var e in sink.Other.Distinct().Take(40)) Console.WriteLine("  " + e);
        Console.Out.Flush();
        AppLauncher.ExitNow(0);
    }

    // ── One run, one theme, its own database ─────────────────────────────────

    private static void Scenario(string outDir, string theme, BindingSink sink)
    {
        var dbPath = Path.Combine(outDir, $"connectors-{theme}.db");
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }) if (File.Exists(f)) File.Delete(f);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=False", AppDb.KeepCollectionParameter).Options;
        var factory = new ShotFactory(options);
        Seed(factory);

        var store     = new LinkedCharacterStore(factory, null);
        var connector = new ConnectorService(new NoHttp(), factory, store, null, openBrowser: _ => throw new InvalidOperationException("no browser in the harness"));
        var vm        = new ConnectorSettingsViewModel(connector, store);

        var win = new SettingsWindow { DataContext = new SettingsStandIn { ConnectorVm = vm }, Width = 940, Height = 1900 };
        sink.Window = win;
        var tab = win.GetLogicalDescendants().OfType<TabItem>().First(t => (t.Header as string) == SettingsText.TabConnector);
        sink.Tab = tab;
        win.Show();
        Pump();
        win.SelectTab(SettingsText.TabConnector);
        Pump();

        sink.Step = $"{theme}: load";
        Wait(vm.LoadAsync());
        Pump();
        Report(vm, "after load");

        // The window at its real size first, then tall, so everything below the fold shows.
        Shot(win, outDir, $"{theme}-00-real-size-top", 940, 820);
        ScrollToEnd(tab);
        Shot(win, outDir, $"{theme}-00b-real-size-bottom", 940, 820);
        ScrollToStart(tab);
        Shot(win, outDir, $"{theme}-01-entry1-connected", 940, 1900);

        sink.Step = $"{theme}: select entry 2";
        Click(win, ListItem(win, vm.Entries[1]));
        Report(vm, "entry 2 selected");
        Shot(win, outDir, $"{theme}-02-entry2-not-connected", 940, 1900);

        // A function on the entry that is not connected: disabled, so the click must change nothing.
        sink.Step = $"{theme}: click a function on entry 2";
        var off = FunctionBox(win, ConnectorFunctions.LinkedCharacters);
        Console.WriteLine($"  entry 2 '{ConnectorFunctions.LinkedCharacters}' box: enabled={off?.IsEnabled} effectivelyEnabled={off?.IsEffectivelyEnabled} checked={off?.IsChecked}");
        if (off is not null) Click(win, off);
        Wait(Task.Delay(300)); Pump();
        Console.WriteLine($"  after click: checked={off?.IsChecked}");

        sink.Step = $"{theme}: select entry 3";
        Click(win, ListItem(win, vm.Entries[2]));
        Report(vm, "entry 3 selected");
        Shot(win, outDir, $"{theme}-03-entry3-connected", 940, 1900);

        // A corporation entry 1 already holds Groups for: refused, the box taken back, the reason shown.
        sink.Step = $"{theme}: tick a scope entry 1 holds";
        var scope = ScopeBox(win, 980_000_001);
        Console.WriteLine($"  entry 3 scope box for 980000001: {(scope is null ? "NOT FOUND" : "found, checked=" + scope.IsChecked)}");
        if (scope is not null) Click(win, scope);
        PumpUntil(() => vm.SelectedEntry?.ChoiceMessage.Length > 0, 5000);
        Wait(Task.Delay(400)); Pump();
        Report(vm, "after the refused tick");
        Console.WriteLine($"  scope box now: checked={ScopeBox(win, 980_000_001)?.IsChecked}");
        Shot(win, outDir, $"{theme}-04-entry3-overlap-refused", 940, 1900);
        Shot(win, outDir, $"{theme}-04b-entry3-refused-real-size", 940, 820, scrollToEnd: true);

        // Back to entry 1 and tick a function it offers: saved.
        sink.Step = $"{theme}: select entry 1, tick a function";
        Click(win, ListItem(win, vm.Entries[0]));
        var fn = FunctionBox(win, ConnectorFunctions.CharacterData);
        Console.WriteLine($"  entry 1 '{ConnectorFunctions.CharacterData}' box: {(fn is null ? "NOT FOUND" : $"enabled={fn.IsEnabled} checked={fn.IsChecked}")}");
        if (fn is not null) Click(win, fn);
        Wait(Task.Delay(600)); Pump();
        Report(vm, "after ticking Character data on entry 1");
        Shot(win, outDir, $"{theme}-05-entry1-function-ticked", 940, 1900);

        // A narrow window: what wraps, what clips.
        sink.Step = $"{theme}: narrow";
        Shot(win, outDir, $"{theme}-06-min-width", 700, 1900);

        win.Close();
        Pump();
        sink.Window = null; sink.Tab = null;
    }

    // ── Data ─────────────────────────────────────────────────────────────────

    private static void Seed(ShotFactory factory)
    {
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        db.Characters.AddRange(
            new Character { Id = 900_000_001, Name = "Example Main",  CorporationId = 980_000_001, AllianceId = 990_000_001 },
            new Character { Id = 900_000_002, Name = "Example Alt",   CorporationId = 980_000_002 },
            new Character { Id = 900_000_003, Name = "Example Third", CorporationId = 980_000_003, AllianceId = 990_000_002 });
        db.UniverseNames.AddRange(
            new UniverseName { EntityId = 980_000_001, Name = "Example Corp One",   Category = "corporation" },
            new UniverseName { EntityId = 980_000_002, Name = "Example Corp Two",   Category = "corporation" },
            new UniverseName { EntityId = 980_000_003, Name = "Example Corp Three", Category = "corporation" },
            new UniverseName { EntityId = 990_000_001, Name = "Example Alliance",   Category = "alliance" },
            new UniverseName { EntityId = 990_000_002, Name = "Example Second Alliance", Category = "alliance" });

        // 1: connected, two scopes (a corporation and an alliance), two functions ticked of four offered.
        db.Connectors.Add(new ConnectorEntry
        {
            Url = "http://localhost:5054/", Name = "Example Holdings Connector", Key = "harness-key-1",
            CharacterId = 900_000_001, CharacterName = "Example Main",
            Capabilities = "linked-characters,groups,character-data,fleet-participation",
            Corporations = "980000001", Alliances = "990000001",
            Functions = "linked-characters,groups", FunctionsChosen = true,
        });
        // 2: just added: not connected, no scope, nothing ticked.
        db.Connectors.Add(new ConnectorEntry());
        // 3: connected, one own corporation and one no character of the user's is in now.
        db.Connectors.Add(new ConnectorEntry
        {
            Url = "https://connector.example.net/", Name = "Example Corp Two Connector", Key = "harness-key-3",
            CharacterId = 900_000_002, CharacterName = "Example Alt",
            Capabilities = "linked-characters,groups,corporation-data",
            Corporations = "980000002,980000009", Alliances = "",
            Functions = "groups,corporation-data", FunctionsChosen = true,
        });
        db.SaveChanges();

        // What entry 1 gave.
        db.LinkedSources.Add(new LinkedSource
        {
            Source = LinkedCharacterStore.SourceKey(1), Label = "example",
            GeneratedAt = new DateTimeOffset(2026, 11, 12, 18, 0, 0, TimeSpan.Zero),
            ImportedAt  = new DateTimeOffset(2026, 11, 12, 18, 1, 0, TimeSpan.Zero),
            Accounts = 2, Characters = 3,
        });
        db.SaveChanges();
    }

    // ── Driving the window ───────────────────────────────────────────────────

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpUntil(Func<bool> done, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done() && DateTime.UtcNow < until) { Pump(); Thread.Sleep(10); }
        Pump();
    }

    private static void Wait(Task t)
    {
        PumpUntil(() => t.IsCompleted, 20_000);
        if (!t.IsCompleted) throw new TimeoutException("a task did not finish");
        t.GetAwaiter().GetResult();
    }

    private static void Shot(Window win, string outDir, string name, double w, double h, bool scrollToEnd = false)
    {
        win.Width = w; win.Height = h;
        Pump();
        if (scrollToEnd)
        {
            var tab = win.GetLogicalDescendants().OfType<TabItem>().First(t => (t.Header as string) == SettingsText.TabConnector);
            ScrollToEnd(tab);
        }
        Pump();
        var path = Path.Combine(outDir, name + ".png");
        win.CaptureRenderedFrame()?.Save(path);
        Console.WriteLine($"shot {path}");
        if (scrollToEnd)
        {
            var tab = win.GetLogicalDescendants().OfType<TabItem>().First(t => (t.Header as string) == SettingsText.TabConnector);
            ScrollToStart(tab);
        }
    }

    private static ScrollViewer? TabScroller(TabItem tab) => tab.Content as ScrollViewer;

    private static void ScrollToEnd(TabItem tab)   { TabScroller(tab)?.ScrollToEnd();  Pump(); }
    private static void ScrollToStart(TabItem tab) { TabScroller(tab)?.ScrollToHome(); Pump(); }

    private static void Click(Window win, Control? c)
    {
        if (c is null) { Console.WriteLine("  click: control NOT FOUND"); return; }
        c.BringIntoView();
        Pump();
        var p = c.TranslatePoint(new Point(Math.Min(12, c.Bounds.Width / 2), c.Bounds.Height / 2), win);
        if (p is null) { Console.WriteLine($"  click: {c.GetType().Name} not in the window"); return; }
        win.MouseMove(p.Value);
        win.MouseDown(p.Value, MouseButton.Left);
        win.MouseUp(p.Value, MouseButton.Left);
        Pump();
    }

    private static ListBoxItem? ListItem(Window win, ConnectorEntryViewModel entry) =>
        win.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.DataContext, entry));

    private static CheckBox? FunctionBox(Window win, string function) =>
        win.GetVisualDescendants().OfType<CheckBox>()
           .FirstOrDefault(c => c.IsEffectivelyVisible && c.DataContext is ConnectorFunctionOptionViewModel f && f.Function == function);

    private static CheckBox? ScopeBox(Window win, int id) =>
        win.GetVisualDescendants().OfType<CheckBox>()
           .FirstOrDefault(c => c.IsEffectivelyVisible && c.DataContext is ConnectorScopeOptionViewModel s && s.Id == id);

    private static void Report(ConnectorSettingsViewModel vm, string when)
    {
        Console.WriteLine($"-- {when}: selected #{vm.SelectedEntry?.Id} '{vm.SelectedEntry?.Title}'  listStatus='{vm.ListStatus}'");
        foreach (var e in vm.Entries)
            Console.WriteLine($"   #{e.Id} '{e.Title}' badge='{e.StatusLabel}' good={e.IsGood} warn={e.IsWarn} tip='{e.StatusTip}' | {e.ConnectedAs} | {e.ScopesLine} | {e.FunctionsLine}"
                            + $" | status='{e.Status}' detail='{e.Detail.Replace("\n", " / ")}' choiceMsg='{e.ChoiceMessage}'"
                            + $" | corp opts {e.CorporationOptions.Count} alli opts {e.AllianceOptions.Count} fn {string.Join(",", e.FunctionOptions.Select(f => $"{f.Function}:{(f.IsChecked ? "x" : "-")}{(f.IsEnabled ? "" : "(off)")}"))}");
    }
}

/// <summary>The App's styles and palette, and none of its start-up: no splash, no database, no services.</summary>
sealed class ShotApp : App
{
    public override void OnFrameworkInitializationCompleted() { }
}

/// <summary>What the Connectors tab binds to on the Settings window's view model, and nothing else.</summary>
public sealed class SettingsStandIn
{
    public ConnectorSettingsViewModel? ConnectorVm { get; init; }
}

sealed class ShotFactory(DbContextOptions<AppDbContext> o) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(o);
}

/// <summary>No network: any call fails at once.</summary>
sealed class NoHttp : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new Refuse());

    private sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("no network in the harness: " + request.RequestUri);
    }
}

/// <summary>Avalonia's log: binding errors under the Connectors tab kept apart from the rest of the window's.</summary>
sealed class BindingSink : ILogSink
{
    public Window?  Window;
    public TabItem? Tab;
    public string   Step = "";
    public readonly List<string> Inside = [];
    public readonly List<string> Other  = [];
    public readonly List<string> Outside = [];
    public int OutsideCount;

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] values)
    {
        if (level < LogEventLevel.Warning) return;
        var i = 0;
        var text = Regex.Replace(messageTemplate, @"\{[^}]+\}", m => i < values.Length ? Convert.ToString(values[i++]) ?? "null" : m.Value);
        var where = Describe(source);
        var line = $"[{level} {area}] ({Step}) {where}: {text}";

        if (area == LogArea.Binding)
        {
            if (UnderTab(source)) Inside.Add(line);
            else { OutsideCount++; Outside.Add(line); }
        }
        else Other.Add(line);
    }

    private bool UnderTab(object? source)
    {
        if (Tab is null || source is not ILogical l) return false;
        if (ReferenceEquals(l, Tab)) return true;
        return l.GetLogicalAncestors().Any(a => ReferenceEquals(a, Tab))
            || (source is Visual v && v.GetVisualAncestors().Any(a => ReferenceEquals(a, Tab)));
    }

    private static string Describe(object? source)
    {
        if (source is null) return "null";
        var sb = new StringBuilder(source.GetType().Name);
        if (source is StyledElement { Name: { Length: > 0 } n }) sb.Append('#').Append(n);
        if (source is StyledElement { DataContext: { } dc }) sb.Append(" dc=").Append(dc.GetType().Name);
        if (source is TextBlock tb) sb.Append(" text='").Append(tb.Text).Append('\'');
        return sb.ToString();
    }
}
