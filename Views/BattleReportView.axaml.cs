using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class BattleReportView : UserControl
{
    public BattleReportView()
    {
        InitializeComponent();

        // Both, as System Index does: whichever comes second is the one with a view model.
        AttachedToVisualTree += async (_, _) => await ShowAsync();
        DataContextChanged   += async (_, _) => { if (this.IsAttachedToVisualTree()) await ShowAsync(); };
    }

    private async Task ShowAsync()
    {
        if (DataContext is not BattleReportViewModel vm) return;

        vm.ConfirmDelete = async message =>
            TopLevel.GetTopLevel(this) is Window owner && await new ConfirmDialog(message).ShowDialog<bool>(owner);
        await vm.InitializeAsync();
    }
}
