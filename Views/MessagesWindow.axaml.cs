using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>Direct messages from every service, in a window of their own so they can sit anywhere, even with the app behind a game.</summary>
public partial class MessagesWindow : Window
{
    private MessagesTabVm? _watching;

    public MessagesWindow()
    {
        InitializeComponent();

        var draft = this.FindControl<TextBox>("DraftBox");
        draft?.AddHandler(KeyDownEvent, OnDraftKey, RoutingStrategies.Tunnel);

        // ⚠️ By which event, not by asking the window: it raises Activated before its IsActive turns true.
        Activated += (_, _) => { if (DataContext is MessagesViewModel vm) vm.IsBeingLookedAt = true; };
        Deactivated += (_, _) => { if (DataContext is MessagesViewModel vm) vm.IsBeingLookedAt = false; };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MessagesViewModel vm) return;

            vm.Confirm = AskAsync;
            foreach (var tab in vm.Tabs) tab.Confirm = AskAsync;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MessagesViewModel.SelectedTab)) Watch(vm.SelectedTab);
            };
            Watch(vm.SelectedTab);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Watch(MessagesTabVm? tab)
    {
        if (_watching is not null) _watching.ScrollToEndRequested -= ScrollToEnd;
        _watching = tab;
        if (tab is not null)
        {
            tab.ScrollToEndRequested += ScrollToEnd;
            ScrollToEnd();
        }
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        if (_watching is { Lines.Count: > 0 } tab) this.FindControl<ListBox>("LineList")?.ScrollIntoView(tab.Lines[^1]);
    }, DispatcherPriority.Background);

    /// <summary>Enter sends and Shift+Enter is a new line, as in every chat client.</summary>
    private void OnDraftKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        if (_watching is { CanSend: true } tab && !string.IsNullOrWhiteSpace(tab.Draft))
            tab.SendCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: MessagesTabVm tab } && DataContext is MessagesViewModel vm) vm.CloseTab(tab);
    }

    private async void OnAttach(object? sender, RoutedEventArgs e)
    {
        if (_watching is not { } tab) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count == 0) return;

        var file = files[0];
        var size = (await file.GetBasicPropertiesAsync()).Size ?? 0;
        await using var content = await file.OpenReadAsync();
        await tab.SendFileAsync(content, file.Name, (long)size);
    }

    /// <summary>Opens a link from a message in the browser. Only web addresses ever become links.</summary>
    private void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string url } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
            ExternalLinks.Open(url);
    }

    private async Task<bool> AskAsync(string message) => await new ConfirmDialog(message).ShowDialog<bool>(this);
}

/// <summary>Keeps the one Messages window, and where it was left.</summary>
public sealed class MessagesWindowController
{
    public const string BoundsPref = "messages.window";

    private readonly MessagesViewModel _vm;
    private readonly AppPreferencesService _prefs;
    private MessagesWindow? _window;

    public MessagesWindowController(MessagesViewModel vm, AppPreferencesService prefs)
    {
        _vm = vm;
        _prefs = prefs;
        _vm.ShowWindowRequested += Show;
    }

    private void Show() => Dispatcher.UIThread.Post(() =>
    {
        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            return;
        }

        _window = new MessagesWindow { DataContext = _vm };
        Restore(_window);
        _window.Closing += (_, _) => Remember(_window);
        _window.Closed += (_, _) =>
        {
            _vm.IsBeingLookedAt = false;
            _window = null;
        };
        _window.Show();
        _window.Activate();
    });

    private void Restore(Window window)
    {
        var parts = _prefs.Get(BoundsPref)?.Split(',');
        if (parts is not { Length: 4 } || !parts.All(p => int.TryParse(p, out _))) return;

        var at = new PixelPoint(int.Parse(parts[0]), int.Parse(parts[1]));

        // ⚠️ Only somewhere still on a screen: a monitor unplugged since would open it where nobody can reach it.
        if (window.Screens.All.Any(s => s.WorkingArea.Contains(at)))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = at;
        }
        window.Width = Math.Max(window.MinWidth, int.Parse(parts[2]));
        window.Height = Math.Max(window.MinHeight, int.Parse(parts[3]));
    }

    private void Remember(Window? window)
    {
        if (window is null || window.WindowState != WindowState.Normal) return;
        _ = _prefs.SetAsync(BoundsPref, $"{window.Position.X},{window.Position.Y},{(int)window.Width},{(int)window.Height}");
    }

    /// <summary>The app is closing: it must not keep the process alive behind it.</summary>
    public void Close()
    {
        _vm.ShowWindowRequested -= Show;
        Remember(_window);
        _window?.Close();
        _window = null;
    }
}
