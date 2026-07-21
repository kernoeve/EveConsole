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
}
