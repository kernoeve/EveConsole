using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class PlanetaryIndustryView : UserControl
{
    public PlanetaryIndustryView() => InitializeComponent();

    /// <summary>A double-click on a colony opens its detail.</summary>
    private void OnColonyDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is PlanetaryIndustryViewModel vm) vm.OpenSelected();
    }
}
