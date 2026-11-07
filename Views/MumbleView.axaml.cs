using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>The Mumble tool.</summary>
public partial class MumbleView : UserControl
{
    public MumbleView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MumbleViewModel vm) vm.Confirm = AskAsync;
        };
    }

    // ⚠️ On the window, not on this view. A key goes only as far as what has the focus, and after a click on a button
    // nothing in this view need have it, so a handler here would see a key now and then and miss the rest.
    private TopLevel? _window;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this);
        _window?.AddHandler(KeyDownEvent, OnCaptureKey, RoutingStrategies.Tunnel);
        _window?.AddHandler(PointerPressedEvent, OnCapturePointer, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _window?.RemoveHandler(KeyDownEvent, OnCaptureKey);
        _window?.RemoveHandler(PointerPressedEvent, OnCapturePointer);
        _window = null;
        (DataContext as MumbleViewModel)?.CancelKeyCapture();
    }

    /// <summary>Waiting for a key, and still on screen: a tool on a tab no longer showing does not get to swallow keys.</summary>
    private MumbleViewModel? Capturing()
    {
        if (DataContext is not MumbleViewModel { IsCapturingKey: true } vm) return null;
        if (IsEffectivelyVisible) return vm;

        vm.CancelKeyCapture();
        return null;
    }

    /// <summary>While a talk key is being chosen, the next key pressed is that key and does nothing else.</summary>
    private void OnCaptureKey(object? sender, KeyEventArgs e)
    {
        if (Capturing() is not { } vm) return;

        e.Handled = true;
        if (e.Key == Key.Escape) vm.CancelKeyCapture();
        else vm.TakeCapturedKey(GlobalHotkeyService.VkOf(e.Key));
    }

    /// <summary>The same for a mouse button. The left one never talks: it is how anything is clicked, so it cancels.</summary>
    private void OnCapturePointer(object? sender, PointerPressedEventArgs e)
    {
        if (Capturing() is not { } vm) return;

        var vk = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.MiddleButtonPressed => GlobalHotkeyService.VkMouseMiddle,
            PointerUpdateKind.XButton1Pressed     => GlobalHotkeyService.VkMouseBack,
            PointerUpdateKind.XButton2Pressed     => GlobalHotkeyService.VkMouseForward,
            _                                     => 0,
        };

        if (vk == 0 || !GlobalHotkeyService.AvailableHere(vk)) { vm.CancelKeyCapture(); return; }

        e.Handled = true;
        vm.TakeCapturedKey(vk);
    }

    /// <summary>Double-clicking a channel joins it, as in every Mumble client, and a person opens the conversation with them.</summary>
    private async void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MumbleViewModel vm) return;

        // ⚠️ Not from the arrow: clicking it twice quickly is opening and closing, not joining whatever is selected.
        if (e.Source is Avalonia.Visual from && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(from, true) is not null) return;

        if (vm.SelectedRow is { IsChannel: true }) await vm.JoinSelectedAsync();
        else vm.MessageSelectedPerson();
    }

    private async void OnUnban(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EveConsole.Services.Mumble.MumbleBan ban } && DataContext is MumbleViewModel vm) await vm.UnbanAsync(ban);
    }

    private void OnToggleChannel(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MumbleRowVm row }) (DataContext as MumbleViewModel)?.ToggleChannel(row);
        e.Handled = true;
    }

    private void OnMessagePerson(object? sender, RoutedEventArgs e) => (DataContext as MumbleViewModel)?.MessageSelectedPerson();

    private async Task<bool> AskAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        return await new ConfirmDialog(message).ShowDialog<bool>(owner);
    }
}

/// <summary>A ban's second line, for the list.</summary>
public static class MumbleBanText
{
    public static readonly Avalonia.Data.Converters.IValueConverter Convert =
        new Avalonia.Data.Converters.FuncValueConverter<EveConsole.Services.Mumble.MumbleBan, string>(b => b is null ? "" : MumbleViewModel.BanText(b));
}
