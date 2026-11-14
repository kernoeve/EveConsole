using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.Interactivity;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

public partial class AgentPanel : ReactiveUserControl<AgentPanelViewModel>
{
    public AgentPanel()
    {
        InitializeComponent();

        // ⚠️ Hold to talk, like the key — not click to start and click again to stop. The toggle
        // read as broken to anyone who held the button while speaking: the press started a
        // recording, the release did nothing, and the NEXT press stopped and transcribed the
        // earlier speech, which then appeared as if it had been delayed; a quick tap made a
        // near-empty clip that read as "no speech". Tunnelled, because the button keeps the
        // bubbling pointer events for its own click.
        MicButton.AddHandler(PointerPressedEvent,     OnMicPressed,     RoutingStrategies.Tunnel);
        MicButton.AddHandler(PointerReleasedEvent,    OnMicReleased,    RoutingStrategies.Tunnel);
        MicButton.AddHandler(PointerCaptureLostEvent, OnMicCaptureLost, RoutingStrategies.Tunnel);
    }

    /// <summary>False on a panel: its ✕ hides the main window's dock, which is nothing to do with the panel.</summary>
    public bool ShowsClose
    {
        get => CloseButton.IsVisible;
        set => CloseButton.IsVisible = value;
    }

    /// <summary>False on a panel: taking the keyboard there would take it from a game client.</summary>
    public bool FocusesInput { get; set; } = true;

    private AgentPanelViewModel? _watched;
    private IDisposable? _streaming;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Watch(ViewModel);
    }

    /// <summary>
    /// Auto-scroll when a message is added or streaming text updates.
    /// ⚠️ Dropped when the view goes: the conversation is shown on panels as well as in the main
    /// window, and a panel's view is made again whenever its layout is applied.
    /// </summary>
    private void Watch(AgentPanelViewModel? vm)
    {
        if (ReferenceEquals(vm, _watched)) return;
        if (_watched is not null) _watched.Messages.CollectionChanged -= OnMessagesChanged;
        _streaming?.Dispose();
        _streaming = null;
        _watched = vm;
        if (vm is null) return;
        vm.Messages.CollectionChanged += OnMessagesChanged;
        _streaming = vm.WhenAnyValue(x => x.StreamingText).Subscribe(_ => ScrollToBottom());
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(ViewModel);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(null);
    }

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ScrollToBottom();

    private void ScrollToBottom()
    {
        Dispatcher.UIThread.Post(() =>
        {
            MessageScroller.ScrollToEnd();
            if (FocusesInput) InputBox.Focus();
        }, DispatcherPriority.Background);
    }

    private void OnSendClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = ViewModel?.SendAsync();

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Any key at all: somebody is at the keyboard, which is all a wake-up call asks.
        ViewModel?.NoteActivity();
        if (e.Key == Key.Enter && ViewModel is not null)
        {
            e.Handled = true;
            _ = ViewModel.SendAsync();
        }
    }

    private void OnMuteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.IsMuted = !ViewModel.IsMuted;
    }

    private void OnMicPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || !e.GetCurrentPoint(MicButton).Properties.IsLeftButtonPressed) return;
        ViewModel.StartRecording();
    }

    private void OnMicReleased(object? sender, PointerReleasedEventArgs e)
        => _ = ViewModel?.StopAndTranscribeAsync();

    /// <summary>The window lost the pointer mid-hold — a switch to the game, say. Sent, not dropped.</summary>
    private void OnMicCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => _ = ViewModel?.StopAndTranscribeAsync();

    private void OnClearClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ViewModel?.ClearHistory();

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.IsOpen = false;
    }
}
