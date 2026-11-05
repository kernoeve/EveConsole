using EveConsole.Services;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>The Jabber tool. Tells the view model whether it can be seen, because that decides what counts as read.</summary>
public partial class JabberView : UserControl
{
    private Window? _window;

    public JabberView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not JabberViewModel vm) return;

            vm.ScrollToEndRequested -= ScrollToEnd;
            vm.ScrollToEndRequested += ScrollToEnd;
            vm.Confirm = AskAsync;
            NoteVisibility();
        };

        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window is not null)
            {
                _window.Activated   += OnWindowActivated;
                _window.Deactivated += OnWindowDeactivated;
            }
            NoteVisibility();
        };

        DetachedFromVisualTree += (_, _) =>
        {
            if (_window is not null)
            {
                _window.Activated   -= OnWindowActivated;
                _window.Deactivated -= OnWindowDeactivated;
            }
            _window = null;
            if (DataContext is JabberViewModel vm) vm.IsOnScreen = false;
        };

        DraftBox.AddHandler(KeyDownEvent, OnDraftKey, RoutingStrategies.Tunnel);
        StatusBox.LostFocus += (_, _) => (DataContext as JabberViewModel)?.ApplyStatusMessage();
        StatusBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) (DataContext as JabberViewModel)?.ApplyStatusMessage(); };
    }

    // ⚠️ Told by which event, not by asking the window. It raises Activated before its IsActive turns true, so asking
    // there answers no, and after one click away nothing open would ever count as read again.
    private void OnWindowActivated(object? sender, EventArgs e) => NoteVisibility(true);
    private void OnWindowDeactivated(object? sender, EventArgs e) => NoteVisibility(false);

    private void NoteVisibility(bool? windowActive = null)
    {
        if (DataContext is not JabberViewModel vm) return;

        vm.IsOnScreen     = this.IsAttachedToVisualTree();
        vm.IsWindowActive = windowActive ?? _window?.IsActive ?? true;

        // Coming back to a conversation with new messages in it is reading them.
        _ = vm.MarkOpenConversationReadAsync();
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        if (DataContext is JabberViewModel { Messages.Count: > 0 } vm)
            MessageList.ScrollIntoView(vm.Messages[^1]);
    }, DispatcherPriority.Background);

    /// <summary>Enter sends and Shift+Enter is a new line, as in every chat client.</summary>
    private void OnDraftKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        if (DataContext is JabberViewModel vm && !string.IsNullOrWhiteSpace(vm.Draft))
            vm.SendCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    /// <summary>Asks which file, then sends it to the open conversation.</summary>
    private void OnMessagePerson(object? sender, RoutedEventArgs e) => (DataContext as JabberViewModel)?.MessageSelectedPerson();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e) => (DataContext as JabberViewModel)?.MessageSelectedPerson();

    private async void OnAttach(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not JabberViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;

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

    private async Task<bool> AskAsync(string message)
    {
        // ⚠️ No window means nobody to ask, so nothing that cannot be undone goes ahead.
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        return await new ConfirmDialog(message).ShowDialog<bool>(owner);
    }
}
