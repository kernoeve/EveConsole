using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

/// <summary>Direct messages on a panel: who down the side, the conversation beside it. See <see cref="PanelDmVm"/>.</summary>
public partial class PanelDmControl : UserControl
{
    private readonly PanelDmVm? _vm;
    private MessagesTabVm? _watched;
    private IDisposable? _conversationSub;

    public PanelDmControl() => InitializeComponent();

    public PanelDmControl(PanelDmVm vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        // Clicked into, it can be typed in, and the conversation showing has been read.
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
        _vm.Start();
        _conversationSub = _vm.WhenAnyValue(x => x.Conversation).Subscribe(Watch);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _conversationSub?.Dispose();
        _conversationSub = null;
        Watch(null);
        _vm?.Dispose();
    }

    private void Watch(MessagesTabVm? conversation)
    {
        if (_watched is not null) _watched.ScrollToEndRequested -= ScrollToEnd;
        _watched = conversation;
        if (conversation is null) return;
        conversation.ScrollToEndRequested += ScrollToEnd;
        ScrollToEnd();
    }

    /// <summary>Enter sends, as in the Messages tool.</summary>
    private void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || _watched is not { } open) return;
        e.Handled = true;
        open.SendCommand.Execute().Subscribe(_ => _vm?.MarkRead(), _ => { });
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        if (_watched is { Lines.Count: > 0 } open) LineList.ScrollIntoView(open.Lines[^1]);
    }, DispatcherPriority.Background);
}
