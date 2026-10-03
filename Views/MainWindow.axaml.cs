using System.Reactive.Linq;
using System.Text;
using EveConsole.Services;
using Avalonia;
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    /// <summary>The windows tabs have been dragged out into, by their tabs.</summary>
    private readonly Dictionary<TabWorkspace, ToolHostWindow> _hosts = new();

    /// <summary>The main window — which runs tab dragging, since it knows every window.</summary>
    internal static MainWindow? Current { get; private set; }

    // Tab drag state
    private PointerPressedEventArgs? _tabDragPressArgs;
    private ToolTab?                 _tabBeingDragged;
    private bool                     _isDraggingTab;

    private bool _started;

    public MainWindow()
    {
        InitializeComponent();

        // Which window is which, in the taskbar and in alt-tab, when a profile is running beside
        // the ordinary copy. The database is named in the title bar's own hover; this is for
        // telling two windows apart before reading either.
        if (AppConfig.ProfileName is { } profile) Title = $"EVE Console — {profile}";
        Current = this;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Load icon from assets stream so Windows taskbar picks it up correctly.
        using var stream = AssetLoader.Open(new Uri("avares://EveConsole/Assets/ec.ico"));
        Icon = new WindowIcon(stream);

        RestoreWindowState();
        TryStartup();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SaveWindowState();
        base.OnClosing(e);
    }

    /// <summary>
    /// Puts the window back where it was.
    ///
    /// <para>⚠️ Kept in config.json, not the database. It is per-installation UI state rather
    /// than the user's data, the file is readable before the database is even opened, and when a
    /// window does end up somewhere unreachable a text file is something a person can fix.</para>
    ///
    /// <para>An installation that last ran under the old arrangement is read out of the database
    /// once and written to the file from then on, so nobody's window jumps for the upgrade.</para>
    /// </summary>
    private void RestoreWindowState()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var prefs = vm.AppPrefs;

        var saved = Services.AppConfig.GetMainWindow();

        var w        = saved?.Width  ?? prefs.GetLong("window.width",  0);
        var h        = saved?.Height ?? prefs.GetLong("window.height", 0);
        var stateStr = saved?.State  ?? prefs.Get("window.state");

        if (w > 200 && h > 100)
        {
            Width  = w;
            Height = h;
        }

        // Restore position first so the window lands on the right monitor.
        // long.MinValue is used as sentinel for "never saved".
        var x = saved?.X ?? prefs.GetLong("window.x", long.MinValue);
        var y = saved?.Y ?? prefs.GetLong("window.y", long.MinValue);

        if (x != long.MinValue && y != long.MinValue)
            PlaceOnScreen(new Avalonia.PixelPoint((int)x, (int)y));

        // Maximize after position is set so it maximizes on the correct monitor.
        if (stateStr == "Maximized")
            WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Remembers where the window was.
    ///
    /// <para>Written straight to config.json rather than queued through the preference table: this
    /// runs while the window is closing, and a write that has to reach the database may not finish
    /// before the process does.</para>
    /// </summary>
    /// <summary>
    /// Puts the window at a remembered point, moved and shrunk until it fits a real screen.
    ///
    /// <para>⚠️ The WHOLE window has to land on the display, not just its top-left corner.
    /// Testing the corner alone lets a window that was dragged three-quarters off the edge come
    /// back exactly that way — and since every dialog in the app centres on its owner, the SDE and
    /// update prompts then open centred on a window that is mostly not there, which is the failure
    /// this was meant to prevent.</para>
    ///
    /// <para>⚠️ WorkingArea is in physical pixels; Width and Height are logical. They differ by
    /// the display's scaling, and on a 150% monitor treating one as the other misplaces the window
    /// by a third of its size.</para>
    ///
    /// <para>⚠️ A maximised window's corner is never on its own screen, so the screen has to be
    /// chosen by overlap rather than by containment. See the note below.</para>
    /// </summary>
    private void PlaceOnScreen(Avalonia.PixelPoint point)
    {
        var all = Screens?.All;
        if (all is null || all.Count == 0) return;   // no screens to reason about; let the OS place it

        // Width/Height are NaN until a window has been measured, which it has not been at this
        // point on a first run; Bounds carries the size actually in effect.
        var logicalW = double.IsNaN(Width)  ? Bounds.Width  : Width;
        var logicalH = double.IsNaN(Height) ? Bounds.Height : Height;

        // The screen that would hold most of the window, not the one under its top-left corner.
        //
        // ⚠️ A maximised window's corner is never on its own screen: Windows grows a maximised
        // frame past the working area by the resize border on all four sides, so the remembered
        // point sits a few pixels above and left of the monitor — outside its Bounds. Asking
        // which screen contains that point therefore answered "none" for every maximised window,
        // and the fallback put it on the primary. That is why a window maximised on a second
        // monitor came back maximised on the main one while an ordinary window returned correctly.
        //
        // Each screen is measured at its own scaling, because the same window is a different
        // number of physical pixels on a 100% monitor than on a 150% one.
        var target = all
            .Select(sc => (Screen: sc, Overlap: OverlapWith(sc, point, logicalW, logicalH)))
            .Where(t => t.Overlap > 0)
            .OrderByDescending(t => t.Overlap)
            .Select(t => t.Screen)
            .FirstOrDefault()
            ?? Screens?.Primary        // saved against a monitor that is no longer attached
            ?? all[0];

        var area  = target.WorkingArea;
        var scale = target.Scaling <= 0 ? 1.0 : target.Scaling;

        // Bigger than the screen it is going to: shrink it, or there is no position that fits and
        // the clamp below would have nothing to choose between.
        if (logicalW * scale > area.Width)  Width  = logicalW = area.Width  / scale;
        if (logicalH * scale > area.Height) Height = logicalH = area.Height / scale;

        var w = (int)(logicalW * scale);
        var h = (int)(logicalH * scale);

        // Math.Max on the upper bound: a screen smaller than the window after rounding would give
        // Clamp a max below its min, which throws.
        var maxX = Math.Max(area.X, area.X + area.Width  - w);
        var maxY = Math.Max(area.Y, area.Y + area.Height - h);

        Position = new Avalonia.PixelPoint(
            Math.Clamp(point.X, area.X, maxX),
            Math.Clamp(point.Y, area.Y, maxY));
    }

    /// <summary>
    /// How many physical pixels of a window of this logical size, placed at this point, would land
    /// on the given screen. Zero if none of it would.
    /// </summary>
    private static long OverlapWith(Avalonia.Platform.Screen screen,
                                    Avalonia.PixelPoint point, double logicalW, double logicalH)
    {
        var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;

        var rect = new Avalonia.PixelRect(
            point.X, point.Y,
            (int)(logicalW * scale), (int)(logicalH * scale));

        // Bounds, not WorkingArea: a window over the taskbar is still on that monitor, and a
        // maximised frame overhangs the working area by design.
        var shared = screen.Bounds.Intersect(rect);
        return (long)shared.Width * shared.Height;
    }

    private void SaveWindowState()
    {
        // ⚠️ Size only while Normal. Maximised, Width and Height report the whole screen, and
        // storing that as the restore size means un-maximising gives a window the size of the
        // monitor. Zero tells AppConfig to keep what it had.
        var normal = WindowState == WindowState.Normal;

        // Position is saved even when maximised, because it is what names the monitor.
        Services.AppConfig.SetMainWindow(
            Position.X, Position.Y,
            normal ? (int)Width  : 0,
            normal ? (int)Height : 0,
            WindowState.ToString());

        // Mirror the center of the current screen to config.json so the splash can
        // find the right monitor before DI starts. Using screen center rather than
        // Position because maximized windows have a small negative border offset that
        // puts Position just outside the monitor's Bounds, confusing screen detection.
        var screen = Screens?.ScreenFromWindow(this);
        if (screen is not null)
            AppConfig.SetWindowPosition(
                screen.Bounds.X + screen.Bounds.Width  / 2,
                screen.Bounds.Y + screen.Bounds.Height / 2);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm) WatchHosts(vm);
        if (IsVisible) TryStartup();
    }

    // ── Windows of tabs ───────────────────────────────────────────────────────

    private MainWindowViewModel? _watchedHosts;
    /// <summary>Where and how big the next window of tabs opens: where its tab was let go, the size
    /// of the side it came from.</summary>
    private (PixelPoint Position, Size Size)? _nextHostPlace;

    /// <summary>A window for every workspace a tab is dragged out into, closed when it empties.</summary>
    private void WatchHosts(MainWindowViewModel vm)
    {
        if (_watchedHosts == vm) return;
        _watchedHosts = vm;
        vm.Hosts.CollectionChanged += (_, e) =>
        {
            foreach (var ws in e.NewItems?.OfType<TabWorkspace>() ?? []) OpenHost(vm, ws);
            foreach (var ws in e.OldItems?.OfType<TabWorkspace>() ?? [])
                if (_hosts.Remove(ws, out var gone)) gone.Close();
        };
        vm.ShowWorkspaceRequested += ws =>
        {
            if (_hosts.TryGetValue(ws, out var w)) { if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal; w.Activate(); }
        };
    }

    private void OpenHost(MainWindowViewModel vm, TabWorkspace ws)
    {
        var win = new ToolHostWindow(ws) { DataContext = vm };
        if (_nextHostPlace is { } place)
        {
            win.Width  = Math.Max(win.MinWidth,  place.Size.Width);
            win.Height = Math.Max(win.MinHeight, place.Size.Height);
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Position = place.Position;
            _nextHostPlace = null;
        }
        _hosts[ws] = win;
        // Closed by its own close box: its tools close with it. (Closed because it emptied, it is
        // no longer in the list and there is nothing to close.)
        win.Closed += (_, _) =>
        {
            if (_hosts.Remove(ws)) vm.CloseWorkspace(ws);
        };
        win.Show();
    }

    // ── Agent panel width ────────────────────────────────────────────────────
    //
    // The panel sits on the right, so dragging its left edge LEFT makes it wider. The floor is
    // the width it was designed at; the ceiling leaves the content area a usable minimum.
    private const double AgentPanelMinWidth   = 360;
    private const double ContentMinWidth      = 480;
    private double? _agentDragStartX;
    private double  _agentDragStartWidth;

    private void OnAgentResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _agentDragStartX     = e.GetPosition(this).X;
        _agentDragStartWidth = AgentDock.Bounds.Width;
        e.Pointer.Capture(AgentResizeHandle);
        e.Handled = true;
    }

    private void OnAgentResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_agentDragStartX is not { } startX) return;
        var proposed = _agentDragStartWidth - (e.GetPosition(this).X - startX);
        var ceiling  = Math.Max(AgentPanelMinWidth, Bounds.Width - ContentMinWidth);
        AgentDock.Width = Math.Clamp(proposed, AgentPanelMinWidth, ceiling);
        e.Handled = true;
    }

    private void OnAgentResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_agentDragStartX is null) return;
        _agentDragStartX = null;
        e.Pointer.Capture(null);
        // Remembered per installation, like the window itself.
        Services.AppConfig.SetAgentPanelWidth((int)AgentDock.Width);
        e.Handled = true;
    }

    private void TryStartup()
    {
        if (_started || DataContext is not MainWindowViewModel vm) return;
        _started = true;

        // The width the capsuleer last dragged the agent panel to, if they ever did.
        if (Services.AppConfig.GetAgentPanelWidth() is { } savedWidth && savedWidth >= AgentPanelMinWidth)
            AgentDock.Width = savedWidth;

        var agentService = vm.AgentVm.Service;
        agentService.WindowOpenRequested  += name => Dispatcher.UIThread.Post(() => OpenToolByName(vm, name));
        agentService.DataRefreshRequested += ()   => Dispatcher.UIThread.Post(() => vm.ForceResolveNamesAsync());
        agentService.ContextProvider       = () => BuildAgentContext(vm);
        agentService.OnScreenProvider      = () => OnScreenName(vm.SelectedTab);

        // ⚠️ Invoke, not Post. The tool has to return a status message to the model in the same
        // call, so it needs the tab name back — and the agent runs on a background thread, while
        // the tab strip is bound to the UI thread.
        agentService.ShowTableCallback = (title, caption, columns, rows) =>
            Dispatcher.UIThread.Invoke(() =>
            {
                var grid = new EveConsole.ViewModels.AgentGridViewModel(title, caption, columns, rows);
                vm.OpenAgentTab(title, grid);
                return $"Opened a tab named \"{title}\" with {rows.Count:N0} row(s).";
            });

        agentService.ShowDocumentCallback = (title, markdown) =>
            Dispatcher.UIThread.Invoke(() =>
            {
                var doc = new EveConsole.ViewModels.AgentDocumentViewModel(title, markdown);
                vm.OpenAgentTab(title, doc);
                return $"Opened a document tab named \"{title}\".";
            });

        // Alarm actions that need the UI. The dialog is deliberately owner-less and top-most —
        // an alarm is usually wanted precisely when EVE Console is behind the game client.
        vm.AlarmActions.ShowDialogCallback = (title, message, button, onAcknowledge) =>
            new Views.AlarmDialogWindow(title, message, button, onAcknowledge).Show();

        vm.AlarmActions.NotifyAgentCallback = message => vm.AgentVm.NotifyAsync(message);
        vm.AlarmActions.AnnounceCallback    = text    => vm.AgentVm.AnnounceAsync(text);

        // A wake-up call is acknowledged by answering the agent — anything at all — and the
        // acknowledgement goes back through the runner, which is what quiets every client.
        vm.AlarmActions.AwaitReplyCallback  = (ack, said) => vm.AgentVm.ExpectReply(ack, said);
        vm.AgentVm.AcknowledgeCallback      = ack     => vm.AlarmActions.AcknowledgeAsync(ack);

        // A repeating sound brings its own window with the one button that stops it, whether or
        // not a Dialog action was chosen; the runner closes it when the sound ends of itself.
        var soundWindows = new Dictionary<(long, string), Views.AlarmSoundWindow>();
        vm.AlarmActions.SoundStartedCallback = (ack, name, summary, acknowledge) =>
        {
            var key = (ack.AlarmId, ack.ScopeKey);
            if (soundWindows.TryGetValue(key, out var open)) { open.Activate(); return; }

            var w = new Views.AlarmSoundWindow(name, summary, acknowledge);
            w.Closed += (_, _) => soundWindows.Remove(key);
            soundWindows[key] = w;
            w.Show();
        };
        vm.AlarmActions.SoundStoppedCallback = ack =>
        {
            if (soundWindows.Remove((ack.AlarmId, ack.ScopeKey), out var w))
                try { w.Close(); } catch { /* already gone */ }
        };
        vm.AlarmActions.AgentAvailable      =
            () => agentService.Settings.Enabled && agentService.Roles.Conversation is { CanAnswer: true };

        agentService.NavigateEntityCallback = (kind, id, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                var player = kind is EveConsole.Services.EntityKind.Pilot
                                  or EveConsole.Services.EntityKind.PlayerCorp
                                  or EveConsole.Services.EntityKind.Alliance;
                vm.OpenTool(player ? "player_entities" : "npc_entities");
                if (player) vm.PlayerEntitiesVm.Open(kind, id);
                else        vm.NpcEntitiesVm.Open(kind, id);
            });

        agentService.NavigateItemCallback = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        agentService.ConfigureItemBrowserCallback = (tab, src, reg) =>
            Dispatcher.UIThread.Invoke(() =>
            {
                vm.OpenTool("items");

                var ib      = vm.ItemBrowserVm;
                var results = new List<string>();
                if (!string.IsNullOrWhiteSpace(tab)) results.Add(ib.ShowDetailTab(tab));
                if (!string.IsNullOrWhiteSpace(src)) results.Add(ib.TrySelectMarketSource(src));
                if (!string.IsNullOrWhiteSpace(reg)) results.Add(ib.TrySelectHistoryRegion(reg));
                return results.Count > 0 ? string.Join(" ", results) : "Nothing to configure.";
            });

        vm.TradeOpportunitiesVm.ItemNavigationRequested = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        vm.IndustryOpportunitiesVm.ItemNavigationRequested = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        vm.MarketLevelVm.OpenInItemBrowser = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        vm.CorpActivityVm.RequestOpenInItemBrowser = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        vm.InvLevelVm.OpenInItemBrowser = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        vm.SalePostingVm.OpenInItemBrowser = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        vm.AssetBrowserVm.OpenInItemBrowser = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        agentService.FilterAssetsCallback = (location, character, item) =>
            Dispatcher.UIThread.Post(() =>
            {
                vm.OpenTool("assets");

                var filters = new List<(string Column, string Value)>();
                if (!string.IsNullOrEmpty(location))  filters.Add(("Location Name", location!));
                if (!string.IsNullOrEmpty(character)) filters.Add(("Owner Name",    character!));
                if (!string.IsNullOrEmpty(item))      filters.Add(("Type Name",     item!));
                _ = vm.AssetBrowserVm.ApplyAgentFilterAsync(filters);
            });

        agentService.FilterIndustryCallback = (activity, status, search, owner) =>
            Dispatcher.UIThread.Post(() =>
            {
                vm.OpenTool("industry");
                _ = vm.IndustryBrowserVm.ApplyAgentFilterAsync(activity, status, search, owner);
            });

        agentService.SelectCharacterCallback = name =>
            Dispatcher.UIThread.Post(() =>
            {
                vm.OpenTool("characters");

                var match = vm.CharacterViewerVm.Characters
                    .FirstOrDefault(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    vm.CharacterViewerVm.SelectedCharacter = match;
            });

        agentService.CaptureTabCallback = tabName => CaptureTabAsync(tabName);

        _ = HandleStartupFlowAsync(vm);
    }

    // First-run experience vs. normal startup. On the very first launch (no SDE imported
    // and the welcome has never been shown) we auto-download the game data, greet the
    // capsuleer, and open Settings so they can add their ESI characters. Otherwise we
    // fall back to offering an SDE update when a newer build is available.
    private async Task HandleStartupFlowAsync(MainWindowViewModel vm)
    {
        var welcomeShown = vm.AppPrefs.Get("app.welcome_shown") == "true";
        var sdeImported  = await vm.SdeVm.IsSdeImportedAsync();

        if (!welcomeShown && !sdeImported)
        {
            await vm.AppPrefs.SetAsync("app.welcome_shown", "true");

            // Start the SDE + Hoboleaks download in the background — no prompt.
            _ = vm.SdeVm.RunFirstTimeImportAsync();

            await new WelcomeWindow().ShowDialog(this);
            await OpenSettingsAsync(vm);
        }
        else
        {
            // This build added SDE or Hobo columns that the startup pass created empty. Refill
            // them the way a first launch fills everything: in the background, no dialog.
            if (App.SdeSchemaGrew || App.HoboSchemaGrew)
                _ = vm.SdeVm.RunSchemaRefreshAsync(App.SdeSchemaGrew, App.HoboSchemaGrew);

            // The "newer build available" prompt — unless the SDE import is already running in
            // the background, in which case what the dialog would offer is what is happening,
            // and the import fetches the newest build regardless. A Hobo-only refresh does not
            // touch the SDE, so the prompt still stands for that.
            if (!App.SdeSchemaGrew)
                vm.SdeVm.WhenAnyValue(x => x.UpdateAvailable)
                    .Where(available => available)
                    .Take(1)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(async _ =>
                    {
                        var dialog = new SdeUpdateDialog { DataContext = vm.SdeVm };
                        await dialog.ShowDialog(this);
                    });
        }

        // App update prompt (Velopack) — only when a new version is found and not already declined.
        vm.UpdateVm.WhenAnyValue(x => x.ShouldPrompt)
            .Where(prompt => prompt)
            .Take(1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                var dialog = new UpdateDialog { DataContext = vm.UpdateVm };
                await dialog.ShowDialog(this);
            });

        vm.OverviewVm.OpenAlertSettingsRequested = () => _ = OpenSettingsAsync(vm, EveConsole.Localization.SettingsText.TabAlerts);
        // The PI tool's Characters tab points at the PI switch, which is on Settings → Characters.
        vm.PlanetaryIndustryVm.OpenPiSettingsRequested = () => _ = OpenSettingsAsync(vm, EveConsole.Localization.SettingsText.TabCharacters);

        // Normally already done during startup, while the splash was up; the cached task makes this
        // a no-op in that case.
        _ = vm.OverviewVm.EnsureLoadedAsync();
    }

    // ── Agent navigation ──────────────────────────────────────────────────────

    private void OpenToolByName(MainWindowViewModel vm, string name)
    {
        // Any tool in the catalogue, by id or by name. It listed 17 of the 40 before, and dropped
        // the rest without a word — including "background", which the agent was offered.
        if (EveConsole.Agent.AppKnowledge.Tool(name)?.Id is not { } toolId) return;

        // Wherever it is — the main window or a window of its own, which comes forward.
        try   { vm.OpenTool(toolId); }
        catch (ArgumentException) { /* a catalogue id the window does not know: nothing to open */ }
    }

    // ── Title bar actions ─────────────────────────────────────────────────────

    private void OnAgentToggleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.AgentVm.ToggleOpen();
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        await new AboutWindow().ShowDialog(this);
    }

    private void OnAlarmsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.OpenTool("alarms");
    }

    /// <summary>
    /// The theme menu, built fresh each time it opens.
    ///
    /// <para>Built here rather than declared in the markup because the CHECK has to be right: the
    /// theme can be changed from the Settings window too, and a menu assembled once would go on
    /// ticking whatever was on when it was made.</para>
    /// </summary>
    /// <summary>The UI scale menu, built on click for the same reason as the theme's: the tick
    /// has to show what is on now, and Settings can change it too.</summary>
    private void OnUiScaleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;

        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };

        foreach (var choice in UiScaleChoice.All)
        {
            var item = new MenuItem
            {
                Header     = choice.Name,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked  = Math.Abs(choice.Scale - UiScaleService.Scale) < 0.001,
            };
            var scale = choice.Scale;
            item.Click += (_, _) => UiScaleService.Apply(scale);
            menu.Items.Add(item);
        }

        menu.ShowAt(anchor);
    }

    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;

        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        foreach (var choice in ThemeService.All)
        {
            var item = new MenuItem
            {
                Header     = choice.Name,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked  = choice.Key == ThemeService.Current,
            };

            // Captured, not read off the sender: a MenuItem's Click gives back the item, and
            // recovering the key from its header would break the moment one was renamed.
            var key = choice.Key;
            item.Click += (_, _) => ThemeService.Apply(key);

            menu.Items.Add(item);
        }

        menu.ShowAt(anchor);
    }

    private void OnSchedulerClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.OpenTool("scheduler");
    }

    private async void OnGearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await OpenSettingsAsync(vm);
    }

    private async Task OpenSettingsAsync(MainWindowViewModel vm, string? initialTab = null)
    {
        // If the Market VM loaded before the SDE finished importing (first run), its
        // region dropdowns are unresolved — reload now that the SDE data is available.
        if (vm.MarketVm.RegionOptions.Count == 0)
            await vm.MarketVm.ReloadAsync();

        await vm.CharacterVm.RefreshTokenStateAsync();
        await vm.PollingSettingsVm.LoadAsync(vm.CharacterVm.Characters);
        vm.CorpTop10SettingsVm.Load();
        var dbVm = new DatabaseSettingsViewModel(vm.AppPrefs, vm.DbBackup);
        // The Worklist tool's own industry view model, not a second one: the Industry tab edits
        // the character list that tool plans against, and two instances over one table would not
        // see each other's edits.
        var settingsVm = new SettingsViewModel(vm.WorklistVm.IndustryVm,
                                               vm.CharacterVm, vm.SdeVm, vm.UpdateVm, vm.MarketVm, vm.TimerVm,
                                               vm.AgentVm.Service, vm.PriceHistorySettingsVm,
                                               vm.AlertSettingsVm, vm.PollingSettingsVm,
                                               vm.CorpTop10SettingsVm, dbVm, vm.SlackSettingsVm, vm.DiscordSettingsVm,
                                               vm.GameLogSettingsVm, vm.ChatLogSettingsVm, vm.ZkbSettingsVm,
                                               vm.MapStatsSettingsVm, vm.OtherSettingsVm, vm.DataRetentionVm,
                                               // Over the shared preferences, which are already in memory.
                                               new PiSettingsViewModel(vm.Pi.Tax, vm.Pi.Settings),
                                               vm.TtsService, vm.SpeechInputService, vm.HotkeyService);
        var settingsWin = new SettingsWindow { DataContext = settingsVm };
        settingsWin.WireDatabase(dbVm, this);
        if (initialTab is not null) settingsWin.SelectTab(initialTab);
        await settingsWin.ShowDialog(this);
        // The tabs save as they are changed; what was still waiting on a typing pause is saved as
        // the window closes. Awaited before anything here reads what was changed.
        await settingsWin.PendingSaves;
        // Slack token / channel or a Discord webhook may have changed — re-evaluate the post
        // buttons' visibility.
        vm.CorpActivityVm.RefreshSlackState();
        vm.SalePostingVm.RefreshSlackState();
        vm.CorpActivityVm.RefreshDiscordState();
        vm.SalePostingVm.RefreshDiscordState();
    }

    private void OnResolveNamesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        _ = vm.ForceResolveNamesAsync();
    }

    private void OnEveTimeClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        OpenInBrowser(vm.EveTimeUrl);
    }

    private void OnServerStatusClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        => OpenInBrowser(EveConsole.Services.UiLinkSettings.ServerStatusUrl);

    private void OnEsiThrottleClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.OpenEsiLimits();
    }

    /// <summary>Opens the release page for whichever version the badge is talking about.</summary>
    private void OnReleaseLinkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) OpenInBrowser(vm.UpdateVm.ReleaseUrl);
    }

    /// <summary>
    /// The SDE link opens the Settings tab that can do something about it, rather than a
    /// download page — importing the SDE is the application's job, not the browser's.
    /// </summary>
    private void OnSdeUpdateLinkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) _ = OpenSettingsAsync(vm, EveConsole.Localization.SettingsText.TabSde);
    }

    /// <summary>
    /// Silences alarms on this machine, or lets them speak again.
    ///
    /// <para>No confirmation either way. Muting loses nothing — the worker goes on recording every
    /// firing as an alert — and a prompt in front of somebody reaching for the mute button during
    /// a fight would be its own kind of failure.</para>
    /// </summary>
    private void OnAlarmMuteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.AlarmsMuted = !vm.AlarmsMuted;
    }

    /// <summary>Hands the URL to the OS default browser. Guarded because a user-supplied
    /// EVE-time URL can be anything, and a malformed one must not take the app down.</summary>
    private static void OpenInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Link] could not open {url}: {ex.Message}");
        }
    }

    /// <summary>A status-bar label opens the Background Processes tool at the tab that says more.</summary>
    private void OnBackgroundLabelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && (sender as Control)?.DataContext is StatusBarItem item)
            vm.OpenBackgroundProcesses(item.Tab);
    }

    // The red "N bad tokens" beside ESI Calls: the fix is a re-authorisation, which lives on the
    // ESI Tokens page, so that is where it goes.
    private void OnStatusWarningClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) _ = OpenSettingsAsync(vm, EveConsole.Localization.SettingsText.TabEsiTokens);
    }

    // ── Tab detach (right-click → Open in New Window) ─────────────────────────

    private void OnDetachMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var cm  = mi.GetLogicalAncestors().OfType<ContextMenu>().FirstOrDefault();
        var tab = cm?.PlacementTarget?.DataContext as ToolTab ?? vm.SelectedTab;
        if (tab is null) return;
        vm.DetachTab(tab);
    }

    // ── Tab dragging: along a strip, to the other side, into another window, or out ──

    /// <summary>
    /// How far a tab must move before a press becomes a drag — far enough that a click on a tab
    /// never moves it.
    /// </summary>
    private const double TabDragStart = 6;

    /// <summary>
    /// A point in a visual, on the screen, and back — how a tab dragged in one window finds its
    /// place in another. Avalonia's own, except under a headless test, where every window reports
    /// the same origin and the test supplies its own.
    /// </summary>
    internal static Func<Visual, Point, PixelPoint> ScreenOf = (v, p) => v.PointToScreen(p);
    internal static Func<Visual, PixelPoint, Point> ClientOf = (v, s) => v.PointToClient(s);

    private Visual? _tabDragSource;
    private PixelPoint _tabDragStart;

    /// <summary>Every window of tabs, the main one first, with its tabs' view.</summary>
    private IEnumerable<(Window Window, WorkspaceView View)> TabWindows()
    {
        yield return (this, MainTabs);
        foreach (var w in _hosts.Values) yield return (w, w.View);
    }

    /// <summary>
    /// The window of tabs under a point on the screen: the one the tab came from if it is there,
    /// otherwise another — the one worked in last first, as the likeliest to be on top.
    /// </summary>
    private (Window Window, WorkspaceView View)? WindowAt(PixelPoint screen, Window from)
    {
        bool Holds(Window w)
        {
            if (!w.IsVisible || w.WindowState == WindowState.Minimized) return false;
            var tl = ScreenOf(w, default);
            var br = ScreenOf(w, new Point(w.Bounds.Width, w.Bounds.Height));
            return screen.X >= tl.X && screen.Y >= tl.Y && screen.X < br.X && screen.Y < br.Y;
        }
        var all = TabWindows().ToList();
        return all.Where(x => x.Window == from && Holds(x.Window))
                  .Concat(all.Where(x => x.Window != from && x.Window.IsActive && Holds(x.Window)))
                  .Concat(all.Where(x => x.Window != from && Holds(x.Window)))
                  .Select(x => ((Window, WorkspaceView)?)x).FirstOrDefault();
    }

    internal void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual v || !e.GetCurrentPoint(v).Properties.IsLeftButtonPressed) return;
        _tabDragPressArgs = e;
        _isDraggingTab    = false;
        _tabBeingDragged  = (sender as Control)?.DataContext as ToolTab;
        _tabDragSource    = v;
        _tabDragStart     = ScreenOf(v, e.GetPosition(v));
    }

    internal void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDragPressArgs is null || _tabBeingDragged is not { } tab || _tabDragSource is not { } src
            || DataContext is not MainWindowViewModel vm || vm.WorkspaceOf(tab) is not { } from) return;
        if (!e.GetCurrentPoint(src).Properties.IsLeftButtonPressed) { EndTabDrag(vm); return; }

        var screen = ScreenOf(src, e.GetPosition(src));
        if (!_isDraggingTab)
        {
            if (Math.Abs(screen.X - _tabDragStart.X) < TabDragStart && Math.Abs(screen.Y - _tabDragStart.Y) < TabDragStart) return;
            _isDraggingTab = true;
            foreach (var (_, view) in TabWindows())
            {
                view.PrepareDrag();
                if (view.Workspace is { } ws) { ws.IsDraggingTab = true; ws.IsDragSource = ws == from; }
            }
        }

        var sourceWindow = TopLevel.GetTopLevel(src) as Window ?? this;
        // ⚠️ Out of every window, nothing happens until the tab is let go. Acting at once — the
        // way a tab used to leave the one window there was — would open a window of its own
        // halfway across the gap between two windows, for a tab on its way from one to the other.
        ShowTabDrop(tab, screen, WindowAt(screen, sourceWindow), from);
    }

    internal void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (_isDraggingTab && _tabBeingDragged is { } tab && _tabDragSource is { } src && vm.WorkspaceOf(tab) is { } from)
        {
            var screen = ScreenOf(src, e.GetPosition(src));
            var sourceWindow = TopLevel.GetTopLevel(src) as Window ?? this;
            if (WindowAt(screen, sourceWindow) is { } over)
            {
                var at = ClientOf(over.View, screen);
                if (over.View.DropTarget(at, tab) is { } target)
                {
                    vm.MoveTab(tab, target.Pane, target.Index);
                    if (over.Window != sourceWindow) over.Window.Activate();
                }
                else if (over.View.Workspace == from && MainWindowViewModel.CanLeaveMain(tab)
                         && over.View.DetachArea(tab) is { } area && area.Contains(at)
                         && !(!from.IsMain && from.OpenTabs.Count() == 1))
                {
                    EndTabDrag(vm);
                    DetachAt(vm, tab, screen, from);
                    return;
                }
            }
            // Let go out of every window: a window of its own, there — or, for the last tab of a
            // window of its own, that window, moved there.
            else if (!from.IsMain && from.OpenTabs.Count() == 1)
                sourceWindow.Position = new PixelPoint(screen.X - 200, screen.Y - 15);
            else if (MainWindowViewModel.CanLeaveMain(tab))
            {
                EndTabDrag(vm);
                DetachAt(vm, tab, screen, from);
                return;
            }
        }
        EndTabDrag(vm);
    }

    /// <summary>Opens <paramref name="tab"/> in a window of its own where it was let go, sized like
    /// the side it came from.</summary>
    private void DetachAt(MainWindowViewModel vm, ToolTab tab, PixelPoint screen, TabWorkspace from)
    {
        var fromView = TabWindows().FirstOrDefault(x => x.View.Workspace == from).View ?? MainTabs;
        var size = from.PaneOf(tab) is { } pane ? fromView.SideOf(pane).Bounds.Size : new Size(1200, 760);
        _nextHostPlace = (new PixelPoint(screen.X - 200, screen.Y - 15), size);
        vm.DetachTab(tab);
    }

    /// <summary>Marks where the tab would go in the window under the pointer, and clears the rest.</summary>
    private void ShowTabDrop(ToolTab tab, PixelPoint screen, (Window Window, WorkspaceView View)? over, TabWorkspace from)
    {
        foreach (var (_, view) in TabWindows())
        {
            if (over is { } o && o.View == view)
            {
                var at = ClientOf(view, screen);
                var target = view.DropTarget(at, tab);
                view.ShowDropTarget(target);
                view.ShowDetachHint(target is null && view.Workspace == from && MainWindowViewModel.CanLeaveMain(tab)
                                    && !(!from.IsMain && from.OpenTabs.Count() == 1)
                                    && view.DetachArea(tab) is { } area && area.Contains(at) ? area : null);
            }
            else view.ClearMarks();
        }
    }

    private void EndTabDrag(MainWindowViewModel vm)
    {
        _tabDragPressArgs = null; _tabBeingDragged = null; _tabDragSource = null; _isDraggingTab = false;
        foreach (var (_, view) in TabWindows())
        {
            view.ClearMarks();
            if (view.Workspace is { } ws) { ws.IsDraggingTab = false; ws.IsDragSource = false; }
        }
    }

    /// <summary>The tool on the main window's side being worked in — what "the current tab" is for a screenshot.</summary>
    private Control ActiveContent => MainTabs.ActiveContent;

    // ── Agent context snapshot ─────────────────────────────────────────────────

    /// <summary>The tool a tab shows, by the name the Tool Reference uses — "Universe Map", not
    /// the tab's "Universe" — or the tab's own title for one that is not a tool.</summary>
    private static string? OnScreenName(ToolTab? tab) =>
        tab is null ? null : EveConsole.Agent.AppKnowledge.Tool(tab.Id)?.Name ?? tab.Title;

    private string BuildAgentContext(MainWindowViewModel vm)
    {
        var sb = new StringBuilder();

        // The clock, first. Every message in the history carries the time it was sent, and this
        // is what those are measured against — without it the stamps are dates with no "ago".
        var now = DateTimeOffset.UtcNow;
        sb.AppendLine(FormattableString.Invariant($"Now: {now:yyyy-MM-dd HH:mm} EVE time ({now.ToLocalTime():d MMM yyyy HH:mm} for the capsuleer, {now.ToLocalTime():dddd})."));

        // ⚠️ What is on screen, first and unmistakable, with the guide's own words about it. A
        // small model asked "what is this?" answered from memory, named the wrong tool, and went
        // on naming it after the capsuleer had moved on — the other open tabs were listed right
        // below the active one, and read as candidates.
        var active = vm.SelectedTab;
        var onScreen = OnScreenName(active);
        sb.AppendLine($"On screen: {onScreen ?? "no tool"} — the tab the capsuleer has open now.");
        if (EveConsole.Agent.AppKnowledge.EntryFor(active?.Id) is { Length: > 0 } entry)
            sb.AppendLine($"What the Tool Reference says about it:\n{entry}");
        else if (onScreen is not null)
            sb.AppendLine("The Tool Reference has no entry for it: say only what its name makes plain, and that you have no description of it.");

        // By the Tool Reference's names, like the tab on screen: a tab's title is in the interface
        // language, and this is read by the model.
        // Split, the other half of the window shows a second tool — on screen too, but not the one
        // being worked in.
        var beside = vm.OtherSideTab;
        if (beside is not null)
            sb.AppendLine($"Also on screen, beside it in the other half of the window: {OnScreenName(beside) ?? beside.Title}.");
        var otherTabs = vm.OpenTabs.Where(t => !ReferenceEquals(t, active) && !ReferenceEquals(t, beside)).Select(t => OnScreenName(t) ?? t.Title).ToList();
        if (otherTabs.Count > 0)
            sb.AppendLine($"Other tabs open behind it (not on screen): {string.Join(", ", otherTabs)}");

        // Windows of their own, each with the tools in it, the one showing first.
        var detached = vm.Hosts.Select(ws => string.Join(" + ", ws.OpenTabs.OrderBy(t => t == ws.SelectedTab ? 0 : 1)
            .Select(t => OnScreenName(t) ?? t.Title))).Where(x => x.Length > 0).ToList();
        if (detached.Count > 0)
            sb.AppendLine($"Windows of their own: {string.Join("; ", detached)}");

        // (open_window's own description lists every tool id. It was repeated here, in part, and a
        // small model lifted names from the list into its description of an unrelated tool.)
        sb.AppendLine("You know what each tool does (see your Tool Reference) — explain and guide from that knowledge; only use capture_tab to read specific on-screen values you cannot get from the data tools.");

        if (vm.CharacterViewerVm.SelectedCharacter is { } ch)
            sb.AppendLine($"Selected character: {ch.Name} (ID: {ch.Id})");

        return sb.ToString().TrimEnd();
    }

    // ── Tab screenshot ─────────────────────────────────────────────────────────

    private Task<(byte[]? image, string description)> CaptureTabAsync(string tabName)
    {
        var vm = DataContext as MainWindowViewModel;
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                // A tool in a window of its own is captured there, showing; anything else is the
                // main window's side being worked in.
                Avalonia.Visual? target = ActiveContent;
                if (vm is not null && tabName != "current"
                    && vm.AllTabs.FirstOrDefault(t => t.Id == tabName) is { } tab
                    && vm.WorkspaceOf(tab) is { IsMain: false } ws && _hosts.TryGetValue(ws, out var host))
                {
                    ws.SelectedTab = tab;
                    Dispatcher.UIThread.RunJobs();
                    target = host.View.ActiveContent;
                }

                if (target is null) return ((byte[]?)null, "Target not found.");

                var bounds = target.Bounds;
                int w = Math.Max((int)bounds.Width,  1);
                int h = Math.Max((int)bounds.Height, 1);

                using var bmp = new RenderTargetBitmap(new Avalonia.PixelSize(w, h),
                                                        new Avalonia.Vector(96, 96));
                bmp.Render(target);

                using var ms = new MemoryStream();
                bmp.Save(ms);

                // ⚠️ Named from the tab's id, not its title: the title is in the interface
                // language, and the agent's guide knows every tool by its English name.
                var current = tabName == "current" ? vm?.SelectedTab : null;
                var title   = current is not null ? OnScreenName(current) ?? "" : tabName;
                var intent  = EveConsole.Agent.AppKnowledge.TabIntent(current?.Id ?? title);
                var desc   = $"Screenshot of the {title} tab.";
                if (!string.IsNullOrEmpty(intent)) desc += $" ({intent})";

                return (ms.ToArray(), desc);
            }
            catch (Exception ex)
            {
                return ((byte[]?)null, $"Capture failed: {ex.Message}");
            }
        }).GetTask();
    }
}
