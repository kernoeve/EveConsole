using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// Direct messages from every service: a tool like any other, so it can sit in a tab or be dragged out to a window of
/// its own. Tells the view model whether it can be seen, because that decides what counts as read.
/// </summary>
public partial class MessagesView : UserControl
{
    private MessagesTabVm? _watching;
    private MessagesViewModel? _vm;
    private Window? _window;

    public MessagesView()
    {
        InitializeComponent();

        DraftBox.AddHandler(KeyDownEvent, OnDraftKey, RoutingStrategies.Tunnel);
        EmoteBox.Target = DraftBox;
        FindBox.AddHandler(KeyDownEvent, OnFindKey, RoutingStrategies.Tunnel);

        DataContextChanged += (_, _) => Adopt(DataContext as MessagesViewModel);
    }

    private void Adopt(MessagesViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;

        _vm = vm;
        if (vm is null) return;

        vm.PropertyChanged += OnVmChanged;
        if (this.IsAttachedToVisualTree()) Arrive();
        Watch(vm.SelectedTab);
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessagesViewModel.SelectedTab)) Watch(_vm?.SelectedTab);

        // The point of pressing the button is to type a name.
        if (e.PropertyName == nameof(MessagesViewModel.IsFinding) && _vm is { IsFinding: true })
            Dispatcher.UIThread.Post(() => FindBox.Focus(), DispatcherPriority.Background);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Dragged out to another window, it arrives here again with that one.
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.Activated   += OnWindowActivated;
            _window.Deactivated += OnWindowDeactivated;
        }
        Arrive();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_window is not null)
        {
            _window.Activated   -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
        }
        _window = null;
        if (_vm is not null) _vm.IsBeingLookedAt = false;
    }

    private void Arrive()
    {
        if (_vm is not { } vm) return;

        vm.Confirm = AskAsync;
        vm.IsBeingLookedAt = _window?.IsActive ?? true;
        _ = vm.RestoreTabsAsync();
        ScrollToEnd();
    }

    // ⚠️ By which event, not by asking the window: it raises Activated before its IsActive turns true.
    private void OnWindowActivated(object? sender, EventArgs e) { if (_vm is not null) _vm.IsBeingLookedAt = true; }
    private void OnWindowDeactivated(object? sender, EventArgs e) { if (_vm is not null) _vm.IsBeingLookedAt = false; }

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
        if (_watching is { Lines.Count: > 0 } tab) LineList.ScrollIntoView(tab.Lines[^1]);
    }, DispatcherPriority.Background);

    /// <summary>Enter sends and Shift+Enter is a new line, as in every chat client.</summary>
    private void OnDraftKey(object? sender, KeyEventArgs e)
    {
        // Choosing from the emote list comes first: Enter there picks one.
        if (EmoteBox.HandleKey(e)) return;
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        if (_watching is { CanSend: true } tab && !string.IsNullOrWhiteSpace(tab.Draft))
            tab.SendCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    private void OnFindKey(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm) return;

        if (e.Key == Key.Enter) { e.Handled = true; _ = vm.StartWithFirstFoundAsync(); }
        else if (e.Key == Key.Escape) { e.Handled = true; vm.IsFinding = false; }
    }

    private void OnPersonTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: MessagesPersonVm person } && _vm is { } vm) _ = vm.StartConversationAsync(person);
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: MessagesTabVm tab }) _vm?.CloseTab(tab);
    }

    private async void OnAttach(object? sender, RoutedEventArgs e)
    {
        if (_watching is not { } tab || TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
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

    private async Task<bool> AskAsync(string message)
    {
        // ⚠️ No window means nobody to ask, so nothing that cannot be undone goes ahead.
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        return await new ConfirmDialog(message).ShowDialog<bool>(owner);
    }
}
