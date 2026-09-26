using Avalonia.Controls;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class NotificationsView : ReactiveUserControl<NotificationsViewModel>
{
    public NotificationsView()
    {
        InitializeComponent();

        // A notification opened from elsewhere is selected by the view model, possibly far down
        // its page; bring the row into sight. Posted, so the grid has its new rows first.
        NotifGrid.SelectionChanged += (_, _) =>
        {
            if (NotifGrid.SelectedItem is { } row)
                Dispatcher.UIThread.Post(() => NotifGrid.ScrollIntoView(row, null), DispatcherPriority.Background);
        };
    }

    // Each row carries its own navigation; the button's DataContext is the row it sits in.
    private static NotificationRowVm? Row(object? sender)
        => (sender as Control)?.DataContext as NotificationRowVm;

    private void OnOpenNotifCharacter(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Row(sender)?.OpenCharacter();
    private void OnOpenNotifSender(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Row(sender)?.OpenSender();

    // Anything named in the detail pane: a character, corporation, item, system or structure.
    private void OnOpenValue(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as NotifValueVm)?.OpenIt();
}
