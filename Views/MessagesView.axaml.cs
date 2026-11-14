using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        ChatPaste.Hook(DraftBox, () => _watching is { CanAttach: true, IsSendingFile: false }, (content, name, size) => _watching!.SendFileAsync(content, name, size));
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
        ChatPictureVm.AnyShown += OnPictureShown;
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
        ChatPictureVm.AnyShown -= OnPictureShown;
        _window = null;
        if (_vm is not null) _vm.IsBeingLookedAt = false;
    }

    // ── Pictures in messages ──────────────────────────────────────────────────

    private void OnShowPicture(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ChatPictureVm picture }) picture.Show();
    }

    private void OnOpenPicture(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ChatPictureVm picture }) ExternalLinks.Open(picture.Url);
    }

    /// <summary>A picture arriving grows its line: a chat read at its end stays there, one scrolled back is left alone.</summary>
    private void OnPictureShown()
    {
        if (LineList.Scroll is { } scroll && scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height < ChatPictureVm.MostHeight + 120)
            ScrollToEnd();
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

        // Up in an empty box corrects this account's latest message; Escape stops replying or correcting.
        if (e.Key == Key.Up && string.IsNullOrEmpty(DraftBox.Text) && _watching?.BeginEditLast() == true)
        {
            e.Handled = true;
            DraftBox.CaretIndex = DraftBox.Text?.Length ?? 0;
            return;
        }
        if (e.Key == Key.Escape && _watching is { HasDraftBar: true } tab2) { e.Handled = true; tab2.CancelDraftMode(); return; }

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


    // ── Reactions ─────────────────────────────────────────────────────────────

    private Flyout? _reactFlyout;
    private ReactionPicker? _reactPicker;
    private Func<string, Task>? _reacting;

    /// <summary>The message a control under it belongs to.</summary>
    private static T? LineOf<T>(object? sender) where T : class =>
        (sender as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<T>().FirstOrDefault();

    /// <summary>
    /// The picker, at the message's own button. ⚠️ Put away when the window stops being the one in use: a flyout is a
    /// topmost window of its own and would otherwise sit over the next program.
    /// </summary>
    private void OpenReactionPicker(Control at, Func<string, Task> react)
    {
        if (_reactFlyout is null)
        {
            _reactPicker = new ReactionPicker();
            _reactFlyout = new Flyout { Content = _reactPicker, Placement = PlacementMode.TopEdgeAlignedRight };
            _reactPicker.Picked += emoji =>
            {
                _reactFlyout.Hide();
                if (_reacting is { } chosen) _ = chosen(emoji);
            };
            if (TopLevel.GetTopLevel(this) is Window window) window.Deactivated += (_, _) => _reactFlyout.Hide();
        }

        _reacting = react;
        _reactPicker!.Reset();
        _reactFlyout.ShowAt(at);
    }

    private void OnReply(object? sender, RoutedEventArgs e)
    {
        if (LineOf<MessagesLineVm>(sender) is { } line && _watching is { } tab) { tab.BeginReply(line); DraftBox.Focus(); }
    }

    private void OnEditOwn(object? sender, RoutedEventArgs e)
    {
        if (LineOf<MessagesLineVm>(sender) is { } line && _watching is { } tab) { tab.BeginEdit(line); DraftBox.Focus(); DraftBox.CaretIndex = DraftBox.Text?.Length ?? 0; }
    }

    private async void OnDeleteOwn(object? sender, RoutedEventArgs e)
    {
        if (LineOf<MessagesLineVm>(sender) is not { } line || _watching is not { } tab) return;
        if (await AskAsync(EveConsole.Localization.JabberText.ConfirmDeleteOwn)) await tab.DeleteOwnAsync(line);
    }

    private void OnCancelDraftMode(object? sender, RoutedEventArgs e) => _watching?.CancelDraftMode();

    private void OnAddReaction(object? sender, RoutedEventArgs e)
    {
        if (sender is Control at && LineOf<MessagesLineVm>(sender) is { } line && _watching is { } tab)
            OpenReactionPicker(at, emoji => tab.ToggleReactionAsync(line, emoji));
    }

    private void OnReactionChip(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ReactionChipVm chip } && LineOf<MessagesLineVm>(sender) is { } line && _watching is { } tab)
            _ = tab.ToggleReactionAsync(line, chip.Emoji);
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
