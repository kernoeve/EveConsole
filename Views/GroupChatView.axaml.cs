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

/// <summary>Group Chat. Tells the view model whether it can be seen, because that decides what counts as read.</summary>
public partial class GroupChatView : UserControl
{
    private GroupChatViewModel? _vm;
    private Window? _window;

    public GroupChatView()
    {
        InitializeComponent();

        DraftBox.AddHandler(KeyDownEvent, OnDraftKey, RoutingStrategies.Tunnel);
        EmoteBox.Target = DraftBox;

        DataContextChanged += (_, _) => Adopt(DataContext as GroupChatViewModel);
    }

    private void Adopt(GroupChatViewModel? vm)
    {
        if (_vm is not null) _vm.ScrollToEndRequested -= ScrollToEnd;

        _vm = vm;
        if (vm is null) return;

        vm.ScrollToEndRequested += ScrollToEnd;
        if (this.IsAttachedToVisualTree()) Arrive();
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
        if (_vm is { } vm) vm.IsBeingLookedAt = _window?.IsActive ?? true;
        ScrollToEnd();
    }

    // ⚠️ By which event, not by asking the window: it raises Activated before its IsActive turns true.
    private void OnWindowActivated(object? sender, EventArgs e) { if (_vm is not null) _vm.IsBeingLookedAt = true; }
    private void OnWindowDeactivated(object? sender, EventArgs e) { if (_vm is not null) _vm.IsBeingLookedAt = false; }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        if (_vm is { Lines.Count: > 0 } vm) LineList.ScrollIntoView(vm.Lines[^1]);
    }, DispatcherPriority.Background);

    /// <summary>Enter sends and Shift+Enter is a new line, as in every chat client.</summary>
    private void OnDraftKey(object? sender, KeyEventArgs e)
    {
        // Choosing from the emote list comes first: Enter there picks one.
        if (EmoteBox.HandleKey(e)) return;
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        if (_vm is { CanSend: true } vm && !string.IsNullOrWhiteSpace(vm.Draft))
            vm.SendCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    private async void OnAttach(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count == 0) return;

        var file = files[0];
        var size = (await file.GetBasicPropertiesAsync()).Size ?? 0;
        await using var content = await file.OpenReadAsync();
        await vm.SendFileAsync(content, file.Name, (long)size);
    }

    /// <summary>Opens a link from a message in the browser. Only web addresses ever become links.</summary>
    private void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string url } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
            ExternalLinks.Open(url);
    }
}
