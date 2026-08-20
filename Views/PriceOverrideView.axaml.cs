using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class PriceOverrideView : UserControl
{
    public PriceOverrideView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is not PriceOverrideViewModel vm) return;

        vm.ShowAddItemDialog = async () =>
        {
            var dialog = new AddItemDialog(async text =>
            {
                var results = await vm.SearchTypesAsync(text);
                return results.Select(r => new TypeResultVm(r.TypeId, r.Name)).ToList();
            }, showQuantity: false);
            return await dialog.ShowDialog<AddItemDialogResult?>(GetWindow());
        };
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DataContext is PriceOverrideViewModel vm)
        {
            // Don't hijack Delete while editing a cell's text.
            if (MainGrid.CurrentColumn is { IsReadOnly: false } && e.Source is TextBox) return;
            vm.DeleteSelectedCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    /// <summary>The item name, in the Item Browser. The row carries the link itself.</summary>
    private void OnOpenRowItem(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as PriceOverrideRow)?.OpenItem();

    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;
}
