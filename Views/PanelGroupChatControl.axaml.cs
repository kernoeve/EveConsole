using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>One group chat room on a panel. See <see cref="PanelGroupChatVm"/>.</summary>
public partial class PanelGroupChatControl : UserControl
{
    private readonly PanelGroupChatVm? _vm;

    public PanelGroupChatControl() => InitializeComponent();

    public PanelGroupChatControl(PanelGroupChatVm vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        // Clicked into, it can be typed in, and what it shows has been read.
        AddHandler(PointerPressedEvent, (_, _) =>
        {
            (TopLevel.GetTopLevel(this) as ClientPanelWindow)?.TakeFocus();
            vm.MarkRead();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        DraftBox.KeyDown += OnDraftKeyDown;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is null) return;
        _vm.ScrollToEndRequested += ScrollToEnd;
        _vm.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is null) return;
        _vm.ScrollToEndRequested -= ScrollToEnd;
        _vm.Dispose();
    }

    /// <summary>Enter sends, as in the Group Chat tool.</summary>
    private void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || _vm is null) return;
        e.Handled = true;
        _vm.SendCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        if (_vm is { Lines.Count: > 0 } vm) LineList.ScrollIntoView(vm.Lines[^1]);
    }, DispatcherPriority.Background);
}
