using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class MarketLevelView : UserControl
{
    public MarketLevelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is not MarketLevelViewModel vm) return;

        vm.ShowAddGroupDialog = async (collections) =>
        {
            var dialog = new AddEditGroupDialog(
                null, null, null, null,
                vm.AvailableStations, vm.MarketSources, collections);
            return await dialog.ShowDialog<GroupDialogResult?>(GetWindow());
        };

        vm.ShowEditGroupDialog = async (group, collections) =>
        {
            var dialog = new AddEditGroupDialog(
                group.GroupName,
                group.StationId == 0 ? null : group.StationId,
                group.SourceId,
                group.MaxPctOver,
                vm.AvailableStations,
                vm.MarketSources,
                collections,
                group.CollectionId,
                group.Multiplier);
            return await dialog.ShowDialog<GroupDialogResult?>(GetWindow());
        };

        vm.ShowAddItemDialog = async group =>
        {
            var dialog = new AddItemDialog(text => vm.SearchTypesAsync(text));
            return await dialog.ShowDialog<AddItemDialogResult?>(GetWindow());
        };

        vm.ShowFitSelectorDialog = async () =>
        {
            var dialogVm = vm.CreateFitSelectorViewModel();
            var window   = new FitSelectorWindow(dialogVm);
            return await window.ShowDialog<FitSelectorResult?>(GetWindow());
        };

        vm.ShowMarketGroupPickerDialog = async () =>
        {
            var svc = vm.GetBatchAddService();
            if (svc == null) return null;
            var pickerVm = new MarketGroupPickerViewModel(svc);
            var win      = new MarketGroupPickerWindow(pickerVm);
            return await win.ShowDialog<MarketGroupPickerResult?>(GetWindow());
        };

        vm.ShowBlueprintPickerDialog = async () =>
        {
            var svc   = vm.GetBatchAddService();
            if (svc == null) return null;
            var parks = await svc.LoadParksAsync();
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

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DataContext is MarketLevelViewModel vm)
        {
            vm.DeleteSelectedItemCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (e.Column.Tag is string propName && DataContext is MarketLevelViewModel vm)
            vm.SortByProperty(propName);
    }

    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;
}
