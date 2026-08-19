using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class NotificationsView : ReactiveUserControl<NotificationsViewModel>
{
    public NotificationsView()
    {
        InitializeComponent();
    }

    // Each row carries its own navigation; the button's DataContext is the row it sits in.
    private static NotificationRowVm? Row(object? sender)
        => (sender as Control)?.DataContext as NotificationRowVm;

    private void OnOpenNotifCharacter(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Row(sender)?.OpenCharacter();
    private void OnOpenNotifSender(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Row(sender)?.OpenSender();
}
