using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class NotificationsView : ReactiveUserControl<NotificationsViewModel>
{
    public NotificationsView()
    {
        InitializeComponent();
    }
}
