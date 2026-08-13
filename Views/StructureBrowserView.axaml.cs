using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class StructureBrowserView : UserControl
{
    public StructureBrowserView() => InitializeComponent();

    /// <summary>Saves the edited fields. A plain Click handler rather than a command because the
    /// save is a single fire-and-forget on the view model with no parameters to bind.</summary>
    private void OnSaveDetail(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StructureBrowserViewModel vm) _ = vm.SaveDetailAsync();
    }
}
