using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// While a view's model is waiting for a key (<see cref="IKeyCaptureHost"/>), the next key or mouse button pressed in
/// its window is that key, and does nothing else.
/// </summary>
public static class KeyCaptureHook
{
    /// <summary>
    /// ⚠️ The handlers go on the window, not on the view. A key goes only as far as what has the focus, and after a
    /// click on a button nothing in the view need have it, so a handler there would see a key now and then and miss
    /// the rest.
    /// </summary>
    public static void Attach(Control view)
    {
        TopLevel? window = null;

        void Hook()
        {
            if (window is not null) return;
            window = TopLevel.GetTopLevel(view);
            window?.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
            window?.AddHandler(InputElement.PointerPressedEvent, OnPointer, RoutingStrategies.Tunnel);
        }

        // Waiting for a key, and still on screen: a view on a tab no longer showing does not get to swallow keys.
        IKeyCaptureHost? Waiting()
        {
            if (view.DataContext is not IKeyCaptureHost { IsCapturingKey: true } host) return null;
            if (view.IsEffectivelyVisible) return host;

            host.CancelKeyCapture();
            return null;
        }

        void OnKey(object? sender, KeyEventArgs e)
        {
            if (Waiting() is not { } host) return;

            e.Handled = true;
            if (e.Key == Key.Escape) host.CancelKeyCapture();
            else host.TakeCapturedKey(GlobalHotkeyService.VkOf(e.Key));
        }

        // The same for a mouse button. The left one never counts: it is how anything is clicked, so it cancels.
        void OnPointer(object? sender, PointerPressedEventArgs e)
        {
            if (Waiting() is not { } host) return;

            var vk = e.GetCurrentPoint(view).Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.MiddleButtonPressed => GlobalHotkeyService.VkMouseMiddle,
                PointerUpdateKind.XButton1Pressed     => GlobalHotkeyService.VkMouseBack,
                PointerUpdateKind.XButton2Pressed     => GlobalHotkeyService.VkMouseForward,
                _                                     => 0,
            };

            if (vk == 0 || !GlobalHotkeyService.AvailableHere(vk)) { host.CancelKeyCapture(); return; }

            e.Handled = true;
            host.TakeCapturedKey(vk);
        }

        view.AttachedToVisualTree += (_, _) => Hook();
        view.DetachedFromVisualTree += (_, _) =>
        {
            window?.RemoveHandler(InputElement.KeyDownEvent, OnKey);
            window?.RemoveHandler(InputElement.PointerPressedEvent, OnPointer);
            window = null;
            (view.DataContext as IKeyCaptureHost)?.CancelKeyCapture();
        };
        if (view.GetVisualRoot() is not null) Hook();
    }
}
