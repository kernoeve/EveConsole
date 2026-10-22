using Avalonia.Controls;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>The System Index tool. The saved list and the numbers are read each time it is shown.</summary>
public partial class SystemIndexView : UserControl
{
    public SystemIndexView()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is SystemIndexViewModel vm) await vm.InitializeAsync();
        };
    }
}
