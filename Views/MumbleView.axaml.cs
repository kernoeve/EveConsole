using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
            if (DataContext is not MumbleViewModel vm) return;

            vm.ScrollChatRequested -= ScrollChat;
            vm.ScrollChatRequested += ScrollChat;
            vm.Confirm = AskAsync;
        };

        DraftBox.AddHandler(KeyDownEvent, OnDraftKey, RoutingStrategies.Tunnel);
    }

    private void ScrollChat() => Dispatcher.UIThread.Post(() =>
    {
        if (DataContext is MumbleViewModel { Chat.Count: > 0 } vm) ChatList.ScrollIntoView(vm.Chat[^1]);
    }, DispatcherPriority.Background);

    /// <summary>Double-clicking a channel joins it, as in every Mumble client.</summary>
    private async void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MumbleViewModel { SelectedRow.IsChannel: true } vm) await vm.JoinSelectedAsync();
    }

    private void OnDraftKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        if (DataContext is MumbleViewModel vm && !string.IsNullOrWhiteSpace(vm.Draft))
            vm.SendCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    private async Task<bool> AskAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        return await new ConfirmDialog(message).ShowDialog<bool>(owner);
    }
}
