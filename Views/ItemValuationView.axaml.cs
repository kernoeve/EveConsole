using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ItemValuationView : UserControl
{
    private ItemValuationViewModel? _vm;

    public ItemValuationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.CompareColumnsChanged -= RebuildCompareColumns;
            _vm = DataContext as ItemValuationViewModel;
            if (_vm is null) return;
            _vm.CopyToClipboard = async text =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null) await clipboard.SetTextAsync(text);
            };
            _vm.CompareColumnsChanged += RebuildCompareColumns;
            _ = _vm.LoadAsync();
        };
    }

    /// <summary>
    /// The compare grid's columns follow the stations compared: unit, total and per cent for
    /// each, the primary first. Built here because a DataGrid's columns are not bindable, and
    /// each cell binds to its row's cell for that station by index.
    /// </summary>
    private void RebuildCompareColumns()
    {
        if (_vm is null) return;
        while (CompareGrid.Columns.Count > 2) CompareGrid.Columns.RemoveAt(CompareGrid.Columns.Count - 1);

        for (var i = 0; i < _vm.CompareColumns.Count; i++)
        {
            var station = _vm.CompareColumns[i];
            var index = i;
            var shortName = station.Name.Length > 28 ? station.Name[..28] + "…" : station.Name;
            CompareGrid.Columns.Add(Column($"{shortName}  unit",  $"Cells[{index}].UnitText",  $"Cells[{index}].Color", 110));
            CompareGrid.Columns.Add(Column("total",               $"Cells[{index}].TotalText", $"Cells[{index}].Color", 120));
            CompareGrid.Columns.Add(Column("%",                   $"Cells[{index}].PctText",   $"Cells[{index}].Color", 64));
        }
    }

    private static DataGridTemplateColumn Column(string header, string textPath, string colorPath, double width) =>
        new()
        {
            Header = header,
            Width  = new DataGridLength(width),
            CellTemplate = new FuncDataTemplate<CompareRowVm>((_, _) => new TextBlock
            {
                TextAlignment = TextAlignment.Right,
                Margin = new Avalonia.Thickness(0, 0, 6, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                [!TextBlock.TextProperty]       = new Binding(textPath),
                [!TextBlock.ForegroundProperty] = new Binding(colorPath),
            }),
        };

    private void OnAppraiseClick(object? sender, RoutedEventArgs e) { if (_vm is not null) _ = _vm.AppraiseAsync(); }
    private void OnCopyClick(object? sender, RoutedEventArgs e)     { if (_vm is not null) _ = _vm.CopyAsync(); }
    private void OnClearClick(object? sender, RoutedEventArgs e)    { _vm?.Clear(); }
    private void OnAddCompareClick(object? sender, RoutedEventArgs e) { _vm?.AddCompare(); }

    private void OnRemoveCompare(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && (sender as Control)?.DataContext is MarketStation station) _vm.RemoveCompare(station);
    }

    private void OnOpenItem(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && (sender as Control)?.DataContext is ValueRowVm row) _vm.OpenItem(row.TypeId);
    }

    private void OnOpenCompareItem(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && (sender as Control)?.DataContext is CompareRowVm row) _vm.OpenItem(row.TypeId);
    }

    /// <summary>Ctrl+Enter appraises without leaving the text box, since a paste is usually
    /// followed by nothing else.</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _vm is not null)
        {
            e.Handled = true;
            _ = _vm.AppraiseAsync();
        }
    }
}
