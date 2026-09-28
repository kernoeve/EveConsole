using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class NotificationCard : UserControl
{
    public NotificationCard()
    {
        InitializeComponent();
    }

    // The whole card opens the notification in the Notifications tool.
    private void OnTapped(object? sender, TappedEventArgs e)
        => (DataContext as NotificationBoxVm)?.Open();
}
