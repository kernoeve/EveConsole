using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class InvLevelView : UserControl
{
    public InvLevelView()
    {
        InitializeComponent();
        // Row backgrounds are bound to each row's RowBackground (see InvLevelView.axaml) so they
        // update live as availability changes, rather than being set once on LoadingRow.
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is not InvLevelViewModel vm) return;

        vm.ShowAddGroupDialog = async (collections) =>
        {
            var dialog = new AddEditInvGroupDialog(null,
                (scope, text) => vm.SearchLocationsAsync(scope, text),
                collections);
            return await dialog.ShowDialog<InvGroupDialogResult?>(GetWindow());
        };

        vm.ShowEditGroupDialog = async (group, collections) =>
        {
            var existing = new InvGroupDialogResult(
                group.GroupName, group.Scope, group.LocationId, group.LocationName,
                group.IncludeAssets, group.IncludeIndustryJobs, group.IncludeMarketBuyOrders,
                group.IncludeContractsBuying, group.Multiplier, group.CollectionId);
            var dialog = new AddEditInvGroupDialog(existing,
                (scope, text) => vm.SearchLocationsAsync(scope, text),
                collections);
            return await dialog.ShowDialog<InvGroupDialogResult?>(GetWindow());
        };

        vm.ShowAddItemDialog = async () =>
        {
            var dialog = new AddItemDialog(async text =>
            {
                var results = await vm.SearchTypesAsync(text);
                return results.Select(r => new TypeResultVm(r.TypeId, r.Name)).ToList();
            });
            return await dialog.ShowDialog<AddItemDialogResult?>(GetWindow());
        };

        vm.ShowFitSelectorDialog = async () =>
        {
            var fitVm = vm.CreateFitSelectorViewModel();
            if (fitVm == null) return null;
            var win = new FitSelectorWindow(fitVm);
            return await win.ShowDialog<FitSelectorResult?>(GetWindow());
        };

        vm.ShowMarketGroupPickerDialog = async () =>
        {
            var pickerVm = new MarketGroupPickerViewModel(vm.GetBatchAddService()!);
            var win = new MarketGroupPickerWindow(pickerVm);
            return await win.ShowDialog<MarketGroupPickerResult?>(GetWindow());
        };

        vm.ShowBlueprintPickerDialog = async () =>
        {
            var svc    = vm.GetBatchAddService()!;
            var parks  = await svc.LoadParksAsync();
            var dialog = new BlueprintPickerDialog(
                text => svc.SearchBlueprintsAsync(text),
                parks);
            return await dialog.ShowDialog<BlueprintPickerResult?>(GetWindow());
        };

        vm.ShowConfirmLargeGroup = async (groupName, count) =>
        {
            var dlg = new ConfirmDialog(
                $"The selected group contains {count} items. Are you sure you want to add them all?");
            return await dlg.ShowDialog<bool>(GetWindow());
        };

        vm.ShowAddCollectionDialog = async () =>
        {
            var dlg = new NameDialog("Add Collection", "COLLECTION NAME");
            return await dlg.ShowDialog<string?>(GetWindow());
        };

        vm.ShowRenameCollectionDialog = async currentName =>
        {
            var dlg = new NameDialog("Rename Collection", "COLLECTION NAME", currentName);
            return await dlg.ShowDialog<string?>(GetWindow());
        };
    }

    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (DataContext is not InvLevelViewModel vm) return;
        if (e.Column.Tag is not string prop) return;
        vm.SortByProperty(prop);
        e.Handled = true;
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DataContext is InvLevelViewModel vm)
        {
            vm.DeleteSelectedItemCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;
}
