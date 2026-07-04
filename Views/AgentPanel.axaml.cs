using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.Interactivity;
using EveCortex.ViewModels;
using ReactiveUI;

namespace EveCortex.Views;

public partial class AgentPanel : ReactiveUserControl<AgentPanelViewModel>
{
    public AgentPanel()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is null) return;

        // Auto-scroll when a message is added or streaming text updates
        ViewModel.Messages.CollectionChanged += (_, _) => ScrollToBottom();
        ViewModel.WhenAnyValue(vm => vm.StreamingText)
                 .Subscribe(_ => ScrollToBottom());
    }

    private void ScrollToBottom()
    {
        Dispatcher.UIThread.Post(() =>
        {
            MessageScroller.ScrollToEnd();
            InputBox.Focus();
        }, DispatcherPriority.Background);
    }

    private void OnSendClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = ViewModel?.SendAsync();

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
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

    private void OnMicClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (ViewModel.IsRecording)
            _ = ViewModel.StopAndTranscribeAsync();
        else
            ViewModel.StartRecording();
    }

    private void OnClearClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ViewModel?.ClearHistory();

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.IsOpen = false;
    }
}
