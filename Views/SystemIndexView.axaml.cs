using Avalonia.Controls;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>The System Index tool. The saved list and the numbers are read each time it is shown.</summary>
public partial class SystemIndexView : UserControl
{
    public SystemIndexView()
    {
        InitializeComponent();
        // Both, as Indy Parks Compare does: whichever comes second is the one with a view model.
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is SystemIndexViewModel vm) await vm.InitializeAsync();
        };
        DataContextChanged += async (_, _) =>
        {
            if (DataContext is SystemIndexViewModel vm && this.IsAttachedToVisualTree()) await vm.InitializeAsync();
        };
    }
}
