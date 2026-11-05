using Avalonia.Controls;
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
                _window.Activated   += OnWindowChanged;
                _window.Deactivated += OnWindowChanged;
            }
            NoteVisibility();
        };

        DetachedFromVisualTree += (_, _) =>
        {
            if (_window is not null)
            {
                _window.Activated   -= OnWindowChanged;
                _window.Deactivated -= OnWindowChanged;
            }
            _window = null;
            if (DataContext is JabberViewModel vm) vm.IsOnScreen = false;
        };

        DraftBox.AddHandler(KeyDownEvent, OnDraftKey, RoutingStrategies.Tunnel);
    }

    private void OnWindowChanged(object? sender, EventArgs e) => NoteVisibility();

    private void NoteVisibility()
    {
        if (DataContext is not JabberViewModel vm) return;

        vm.IsOnScreen     = this.IsAttachedToVisualTree();
        vm.IsWindowActive = _window?.IsActive ?? true;

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

    /// <summary>Opens a link from a message in the browser. Only web addresses ever become links.</summary>
    private async void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string url } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && TopLevel.GetTopLevel(this) is { } top)
            await top.Launcher.LaunchUriAsync(uri);
    }

    private async Task<bool> AskAsync(string message)
    {
        // ⚠️ No window means nobody to ask, so nothing that cannot be undone goes ahead.
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        return await new ConfirmDialog(message).ShowDialog<bool>(owner);
    }
}
