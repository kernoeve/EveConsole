using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveConsole.Models;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class StandingBuyOrdersView : ReactiveUserControl<StandingBuyOrdersViewModel>
{
    public StandingBuyOrdersView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not StandingBuyOrdersViewModel vm) return;

        // Dialogs are owned by the view, matching how CorpActivityView wires up the
        // standing-project dialog.
        vm.ShowDialog = async existing =>
        {
            var dialog = new StandingBuyOrderDialog(vm.SearchService, existing);
            return await dialog.ShowDialog<StandingBuyOrder?>(GetWindow());
        };

        vm.ConfirmDelete = async () =>
        {
            var dlg = new ConfirmDialog("Are you sure you want to delete this standing buy order?");
            return await dlg.ShowDialog<bool>(GetWindow());
        };
    }

    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;
}
