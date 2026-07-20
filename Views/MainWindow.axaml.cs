using System.Reactive.Linq;
using System.Text;
using EveCortex.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveCortex.ViewModels;
using ReactiveUI;

namespace EveCortex.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    // Detached window handles
    private ApiActivityWindow?       _activityWindow;
    private CharacterViewerWindow?   _characterViewerWindow;
    private AssetBrowserWindow?      _assetBrowserWindow;
    private IndustryBrowserWindow?   _industryBrowserWindow;
    private ItemBrowserWindow?       _itemBrowserWindow;
    private EsiExplorerWindow?       _explorerWindow;
    private CorpActivityWindow?          _corpActivityWindow;
    private KillmailBrowserWindow?       _killmailBrowserWindow;
    private EveMailWindow?               _eveMailWindow;
    private WalletWindow?                _walletWindow;
    private NetWorthWindow?              _netWorthWindow;
    private InvLevelWindow?              _invLevelWindow;
    private MarketLevelWindow?           _marketLevelWindow;
    private TradeOpportunitiesWindow?    _tradeOpportunitiesWindow;
    private IndustryOpportunitiesWindow? _industryOpportunitiesWindow;
    private IndyParksWindow?             _indyParksWindow;
    private ProductionCalculatorWindow?  _productionCalculatorWindow;

    // Tab drag-to-detach state
    private PointerPressedEventArgs? _tabDragPressArgs;
    private ToolTab?                 _tabBeingDragged;
    private bool                     _isDraggingTab;

    private bool _started;

    public MainWindow() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Load icon from assets stream so Windows taskbar picks it up correctly.
        using var stream = AssetLoader.Open(new Uri("avares://EveCortex/Assets/ec.ico"));
        Icon = new WindowIcon(stream);

        RestoreWindowState();
        TryStartup();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SaveWindowState();
        base.OnClosing(e);
    }

    private void RestoreWindowState()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var prefs = vm.AppPrefs;

        var w = prefs.GetLong("window.width",  0);
        var h = prefs.GetLong("window.height", 0);
        if (w > 200 && h > 100)
        {
            Width  = w;
            Height = h;
        }

        // Restore position first so the window lands on the right monitor.
        // long.MinValue is used as sentinel for "never saved".
        var x = prefs.GetLong("window.x", long.MinValue);
        var y = prefs.GetLong("window.y", long.MinValue);
        if (x != long.MinValue && y != long.MinValue)
            Position = new Avalonia.PixelPoint((int)x, (int)y);

        // Maximize after position is set so it maximizes on the correct monitor.
        var stateStr = prefs.Get("window.state");
        if (stateStr == "Maximized")
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowState()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var prefs = vm.AppPrefs;

        _ = prefs.SetAsync("window.state", WindowState.ToString());

        if (WindowState == WindowState.Normal)
        {
            _ = prefs.SetLongAsync("window.width",  (long)Width);
            _ = prefs.SetLongAsync("window.height", (long)Height);
        }

        // Always save position so the monitor is remembered even when maximized.
        // When maximized, Position gives the top-left corner of the monitor.
        _ = prefs.SetLongAsync("window.x", Position.X);
        _ = prefs.SetLongAsync("window.y", Position.Y);

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
        if (IsVisible) TryStartup();
    }

    private void TryStartup()
    {
        if (_started || DataContext is not MainWindowViewModel vm) return;
        _started = true;

        var agentService = vm.AgentVm.Service;
        agentService.WindowOpenRequested  += name => Dispatcher.UIThread.Post(() => OpenToolByName(vm, name));
        agentService.DataRefreshRequested += ()   => Dispatcher.UIThread.Post(() => vm.ForceResolveNamesAsync());
        agentService.ContextProvider       = () => BuildAgentContext(vm);

        agentService.NavigateItemCallback = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_itemBrowserWindow?.IsVisible == true) _itemBrowserWindow.Activate();
                else vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        agentService.ConfigureItemBrowserCallback = (tab, src, reg) =>
            Dispatcher.UIThread.Invoke(() =>
            {
                if (_itemBrowserWindow?.IsVisible == true) _itemBrowserWindow.Activate();
                else vm.OpenTool("items");

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
                if (_itemBrowserWindow?.IsVisible == true) _itemBrowserWindow.Activate();
                else vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        vm.IndustryOpportunitiesVm.ItemNavigationRequested = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_itemBrowserWindow?.IsVisible == true) _itemBrowserWindow.Activate();
                else vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        vm.MarketLevelVm.OpenInItemBrowser = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_itemBrowserWindow?.IsVisible == true) _itemBrowserWindow.Activate();
                else vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        vm.CorpActivityVm.RequestOpenInItemBrowser = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_itemBrowserWindow?.IsVisible == true) _itemBrowserWindow.Activate();
                else vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        vm.InvLevelVm.OpenInItemBrowser = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_itemBrowserWindow?.IsVisible == true) _itemBrowserWindow.Activate();
                else vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        vm.AssetBrowserVm.OpenInItemBrowser = (typeId, name) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_itemBrowserWindow?.IsVisible == true) _itemBrowserWindow.Activate();
                else vm.OpenTool("items");
                _ = vm.ItemBrowserVm.NavigateToTypeAsync(typeId, name);
            });

        agentService.FilterAssetsCallback = (location, character, item) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_assetBrowserWindow?.IsVisible == true) _assetBrowserWindow.Activate();
                else vm.OpenTool("assets");

                var filters = new List<(string Column, string Value)>();
                if (!string.IsNullOrEmpty(location))  filters.Add(("Location Name", location!));
                if (!string.IsNullOrEmpty(character)) filters.Add(("Owner Name",    character!));
                if (!string.IsNullOrEmpty(item))      filters.Add(("Type Name",     item!));
                _ = vm.AssetBrowserVm.ApplyAgentFilterAsync(filters);
            });

        agentService.FilterIndustryCallback = (activity, status, search, owner) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_industryBrowserWindow?.IsVisible == true) _industryBrowserWindow.Activate();
                else vm.OpenTool("industry");
                _ = vm.IndustryBrowserVm.ApplyAgentFilterAsync(activity, status, search, owner);
            });

        agentService.SelectCharacterCallback = name =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_characterViewerWindow?.IsVisible == true) _characterViewerWindow.Activate();
                else vm.OpenTool("characters");

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

        vm.OverviewVm.OpenAlertSettingsRequested = () => _ = OpenSettingsAsync(vm, "Alerts");

        _ = vm.OverviewVm.LoadAsync();
    }

    // ── Agent navigation ──────────────────────────────────────────────────────

    private void OpenToolByName(MainWindowViewModel vm, string name)
    {
        var toolId = name.ToLowerInvariant() switch
        {
            "overview"       => "overview",
            "characters"     => "characters",
            "assets"         => "assets",
            "items"          => "items",
            "industry"       => "industry",
            "indy_parks"     => "indy_parks",
            "prod_calc"      => "prod_calc",
            "market_levels"  => "market_levels",
            "inv_levels"     => "inv_levels",
            "trade"          => "trade",
            "industry_opps"  => "industry_opps",
            "net_worth"      => "net_worth",
            "wallet"         => "wallet",
            "corp_activity"  => "corp_activity",
            "killmails"      => "killmails",
            "eve_mail"       => "eve_mail",
            "data"           => "data",
            _                => null
        };
        if (toolId is null) return;

        // If the tool is in a detached window, bring it forward instead.
        Window? detached = toolId switch
        {
            "assets"        => _assetBrowserWindow?.IsVisible    == true ? _assetBrowserWindow    : null,
            "industry"      => _industryBrowserWindow?.IsVisible == true ? _industryBrowserWindow : null,
            "characters"    => _characterViewerWindow?.IsVisible == true ? _characterViewerWindow : null,
            "items"         => _itemBrowserWindow?.IsVisible     == true ? _itemBrowserWindow     : null,
            "data"          => _explorerWindow?.IsVisible        == true ? _explorerWindow        : null,
            "corp_activity"  => _corpActivityWindow?.IsVisible        == true ? _corpActivityWindow        : null,
            "killmails"      => _killmailBrowserWindow?.IsVisible     == true ? _killmailBrowserWindow     : null,
            "eve_mail"       => _eveMailWindow?.IsVisible             == true ? _eveMailWindow             : null,
            "wallet"         => _walletWindow?.IsVisible              == true ? _walletWindow              : null,
            "net_worth"      => _netWorthWindow?.IsVisible            == true ? _netWorthWindow            : null,
            "inv_levels"     => _invLevelWindow?.IsVisible            == true ? _invLevelWindow            : null,
            "market_levels"  => _marketLevelWindow?.IsVisible         == true ? _marketLevelWindow         : null,
            "trade"          => _tradeOpportunitiesWindow?.IsVisible  == true ? _tradeOpportunitiesWindow  : null,
            "industry_opps"  => _industryOpportunitiesWindow?.IsVisible == true ? _industryOpportunitiesWindow : null,
            "indy_parks"     => _indyParksWindow?.IsVisible           == true ? _indyParksWindow           : null,
            "prod_calc"      => _productionCalculatorWindow?.IsVisible == true ? _productionCalculatorWindow : null,
            _                => null
        };

        if (detached is not null) detached.Activate();
        else vm.OpenTool(toolId);
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

        await vm.PollingSettingsVm.LoadAsync(vm.CharacterVm.Characters);
        vm.CorpTop10SettingsVm.Load();
        var dbVm = new DatabaseSettingsViewModel(vm.AppPrefs, vm.DbBackup);
        var settingsVm = new SettingsViewModel(vm.CharacterVm, vm.SdeVm, vm.UpdateVm, vm.MarketVm, vm.TimerVm,
                                               vm.AgentVm.Service, vm.PriceHistorySettingsVm,
                                               vm.AlertSettingsVm, vm.PollingSettingsVm,
                                               vm.CorpTop10SettingsVm, dbVm, vm.SlackSettingsVm,
                                               vm.TtsService, vm.SpeechInputService, vm.HotkeyService);
        var settingsWin = new SettingsWindow { DataContext = settingsVm };
        settingsWin.WireDatabase(dbVm, this);
        if (initialTab is not null) settingsWin.SelectTab(initialTab);
        await settingsWin.ShowDialog(this);
        // Slack token / channel may have changed — re-evaluate the post buttons' visibility.
        vm.CorpActivityVm.RefreshSlackState();
    }

    private void OnResolveNamesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        _ = vm.ForceResolveNamesAsync();
    }

    private void OnPollingStatusClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (_activityWindow is null || !_activityWindow.IsVisible)
        {
            _activityWindow = new ApiActivityWindow { DataContext = vm.ActivityVm };
            _activityWindow.Closed += (_, _) => _activityWindow = null;
            _activityWindow.Show();
        }
        else _activityWindow.Activate();
    }

    // ── Tab detach (right-click → Open in New Window) ─────────────────────────

    private void OnDetachMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var cm  = mi.GetLogicalAncestors().OfType<ContextMenu>().FirstOrDefault();
        var tab = cm?.PlacementTarget?.DataContext as ToolTab ?? vm.SelectedTab;
        if (tab is null) return;
        DetachToolInWindow(vm, tab);
    }

    // ── Tab drag-to-detach ────────────────────────────────────────────────────

    internal void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed) return;
        _tabDragPressArgs = e;
        _isDraggingTab    = false;
        _tabBeingDragged  = (sender as Control)?.DataContext as ToolTab;
    }

    internal void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDragPressArgs is null || _isDraggingTab) return;
        if (!e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed)
        {
            _tabDragPressArgs = null; _tabBeingDragged = null; return;
        }

        var ctrl  = sender as Control;
        var delta = e.GetPosition(ctrl) - _tabDragPressArgs.GetPosition(ctrl);
        if (Math.Abs(delta.Y) < 24) return;

        _isDraggingTab = true;
        var pressArgs  = _tabDragPressArgs;
        var dragTab    = _tabBeingDragged;
        _tabDragPressArgs = null; _tabBeingDragged = null;

        if (DataContext is not MainWindowViewModel vm || dragTab is null) return;

        var screenPt = this.PointToScreen(e.GetPosition(this));
        var win      = DetachToolInWindow(vm, dragTab);
        if (win is null) return;

        win.Position = new PixelPoint(screenPt.X - 200, screenPt.Y - 15);
        win.BeginMoveDrag(pressArgs);
    }

    internal void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _tabDragPressArgs = null; _tabBeingDragged = null; _isDraggingTab = false;
    }

    private Window? DetachToolInWindow(MainWindowViewModel vm, ToolTab tab)
    {
        Window? window = tab.Id switch
        {
            "characters"    => _characterViewerWindow  = new CharacterViewerWindow  { DataContext = vm.CharacterViewerVm },
            "assets"        => _assetBrowserWindow     = new AssetBrowserWindow     { DataContext = vm.AssetBrowserVm },
            "industry"      => _industryBrowserWindow  = new IndustryBrowserWindow  { DataContext = vm.IndustryBrowserVm },
            "items"         => _itemBrowserWindow      = new ItemBrowserWindow      { DataContext = vm.ItemBrowserVm },
            "data"          => _explorerWindow         = new EsiExplorerWindow      { DataContext = vm.ExplorerVm },
            "corp_activity"  => _corpActivityWindow        = new CorpActivityWindow        { DataContext = vm.CorpActivityVm },
            "killmails"      => _killmailBrowserWindow     = new KillmailBrowserWindow     { DataContext = vm.KillmailBrowserVm },
            "eve_mail"       => _eveMailWindow             = new EveMailWindow(vm.MailSvc) { DataContext = vm.EveMailVm },
            "wallet"         => _walletWindow              = new WalletWindow              { DataContext = vm.WalletVm },
            "net_worth"      => _netWorthWindow            = new NetWorthWindow            { DataContext = vm.NetWorthVm },
            "inv_levels"     => _invLevelWindow            = new InvLevelWindow            { DataContext = vm.InvLevelVm },
            "market_levels"  => _marketLevelWindow         = new MarketLevelWindow         { DataContext = vm.MarketLevelVm },
            "trade"          => _tradeOpportunitiesWindow  = new TradeOpportunitiesWindow  { DataContext = vm.TradeOpportunitiesVm },
            "industry_opps"  => _industryOpportunitiesWindow = new IndustryOpportunitiesWindow { DataContext = vm.IndustryOpportunitiesVm },
            "indy_parks"     => _indyParksWindow           = new IndyParksWindow           { DataContext = vm.IndyParksVm },
            "prod_calc"      => _productionCalculatorWindow = new ProductionCalculatorWindow { DataContext = vm.ProductionCalcVm },
            _                => null
        };
        if (window is null) return null;

        vm.MarkToolDetached(tab.Id);

        var toolId = tab.Id;
        window.Closed += (_, _) =>
        {
            switch (toolId)
            {
                case "characters":    _characterViewerWindow  = null; break;
                case "assets":        _assetBrowserWindow     = null; break;
                case "industry":      _industryBrowserWindow  = null; break;
                case "items":         _itemBrowserWindow      = null; break;
                case "data":          _explorerWindow         = null; break;
                case "corp_activity":  _corpActivityWindow        = null; break;
                case "killmails":      _killmailBrowserWindow     = null; break;
                case "eve_mail":       _eveMailWindow             = null; break;
                case "wallet":         _walletWindow              = null; break;
                case "net_worth":      _netWorthWindow            = null; break;
                case "inv_levels":     _invLevelWindow            = null; break;
                case "market_levels":  _marketLevelWindow         = null; break;
                case "trade":          _tradeOpportunitiesWindow  = null; break;
                case "industry_opps":  _industryOpportunitiesWindow = null; break;
                case "indy_parks":     _indyParksWindow           = null; break;
                case "prod_calc":      _productionCalculatorWindow = null; break;
            }
            vm.MarkToolReattached(toolId);
        };

        window.Show();
        return window;
    }

    // ── Agent context snapshot ─────────────────────────────────────────────────

    private string BuildAgentContext(MainWindowViewModel vm)
    {
        var sb = new StringBuilder();

        var activeTitle = vm.SelectedTab?.Title ?? "None";
        sb.AppendLine($"Active tab: {activeTitle}");
        var activeIntent = EveCortex.Agent.AppKnowledge.TabIntent(activeTitle);
        if (!string.IsNullOrEmpty(activeIntent))
            sb.AppendLine($"Active tab purpose: {activeIntent}");

        var openTabs = vm.OpenTabs.Select(t => t.Title).ToList();
        if (openTabs.Count > 0)
            sb.AppendLine($"Open tabs: {string.Join(", ", openTabs)}");

        var detached = new List<string>();
        if (_characterViewerWindow?.IsVisible == true) detached.Add("Characters");
        if (_assetBrowserWindow?.IsVisible    == true) detached.Add("Assets");
        if (_industryBrowserWindow?.IsVisible == true) detached.Add("Industry");
        if (_itemBrowserWindow?.IsVisible     == true) detached.Add("Items");
        if (_explorerWindow?.IsVisible        == true) detached.Add("ESI Explorer");
        if (_activityWindow?.IsVisible        == true) detached.Add("API Activity");
        if (detached.Count > 0)
            sb.AppendLine($"Detached windows: {string.Join(", ", detached)}");

        sb.AppendLine("You know what each tool does (see your Tool Reference) — explain and guide from that knowledge; only use capture_tab to read specific on-screen values you cannot get from the data tools.");
        sb.AppendLine("Available tool IDs for open_window: overview, characters, assets, items, industry, indy_parks, prod_calc, market_levels, inv_levels, trade, net_worth, wallet, corp_activity, killmails, eve_mail, data");

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
                Avalonia.Visual? target = tabName switch
                {
                    "assets"     when _assetBrowserWindow?.IsVisible == true    => _assetBrowserWindow,
                    "industry"   when _industryBrowserWindow?.IsVisible == true  => _industryBrowserWindow,
                    "characters" when _characterViewerWindow?.IsVisible == true  => _characterViewerWindow,
                    "items"      when _itemBrowserWindow?.IsVisible == true      => _itemBrowserWindow,
                    "data"       when _explorerWindow?.IsVisible == true         => _explorerWindow,
                    _                                                             => MainContent,
                };

                if (target is null) return ((byte[]?)null, "Target not found.");

                var bounds = target.Bounds;
                int w = Math.Max((int)bounds.Width,  1);
                int h = Math.Max((int)bounds.Height, 1);

                using var bmp = new RenderTargetBitmap(new Avalonia.PixelSize(w, h),
                                                        new Avalonia.Vector(96, 96));
                bmp.Render(target);

                using var ms = new MemoryStream();
                bmp.Save(ms);

                var title  = tabName == "current" ? (vm?.SelectedTab?.Title ?? "") : tabName;
                var intent = EveCortex.Agent.AppKnowledge.TabIntent(title);
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
