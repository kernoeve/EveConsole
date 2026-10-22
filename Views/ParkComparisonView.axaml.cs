using Avalonia.Controls;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>Indy Parks, Compare. The park list is read again each time the tab is shown, so a
/// park added or renamed on the other tab is there.</summary>
public partial class ParkComparisonView : UserControl
{
    public ParkComparisonView()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is ParkComparisonViewModel vm) await vm.LoadParksAsync();
        };
    }
}
