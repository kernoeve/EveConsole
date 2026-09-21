using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ItemValuationView : UserControl
{
    private ItemValuationViewModel? _vm;

    public ItemValuationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ItemValuationViewModel);

        // A paste is the whole point of the box, so it appraises on its own. The event fires
        // before the text lands; the appraisal is queued behind it so the binding has caught up.
        InputBox.AddHandler(TextBox.PastingFromClipboardEvent, (_, _) =>
            Dispatcher.UIThread.Post(() => { if (_vm is not null) _ = _vm.AppraiseAsync(); }, DispatcherPriority.Background));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as ItemValuationViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Attach(null);
    }

    /// <summary>
    /// Follows the view model. The main window builds a fresh view each time the tab is shown
    /// while the view model keeps its result, so the compare columns are rebuilt on arrival
    /// rather than left to the next change of stations, which is how they went missing after a
    /// switch of tabs. A view on its way out lets go of the event, so it is not kept alive by it.
    /// </summary>
    private void Attach(ItemValuationViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null) _vm.CompareColumnsChanged -= RebuildCompareColumns;
        _vm = vm;
        if (_vm is null) return;
        _vm.CopyToClipboard = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(text);
        };
        _vm.CompareColumnsChanged += RebuildCompareColumns;
        RebuildCompareColumns();
        _ = _vm.LoadAsync();
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
            CompareGrid.Columns.Add(Column($"{shortName}  unit",  $"Cells[{index}].UnitText",  $"Cells[{index}].Color", 110, r => Priced(r, index)?.Unit  ?? -1));
            CompareGrid.Columns.Add(Column("total",               $"Cells[{index}].TotalText", $"Cells[{index}].Color", 120, r => Priced(r, index)?.Total ?? -1));
            CompareGrid.Columns.Add(Column("%",                   $"Cells[{index}].PctText",   $"Cells[{index}].Color", 64,  r => Priced(r, index)?.Pct   ?? -1000));
        }
    }

    /// <summary>The row's cell for a station when it has a price: what the sort keys read, so
    /// a cell without one sorts under every real value.</summary>
    private static CompareCellVm? Priced(CompareRowVm row, int index) =>
        index < row.Cells.Count && row.Cells[index].Has ? row.Cells[index] : null;

    /// <summary>A column bound by path and sorted by a key. A template column has no binding of
    /// its own for the grid to sort on, so it is handed a comparer and told it may sort.</summary>
    private static DataGridTemplateColumn Column(string header, string textPath, string colorPath, double width, Func<CompareRowVm, double> key) =>
        new()
        {
            Header = header,
            Width  = new DataGridLength(width),
            CanUserSort        = true,
            CustomSortComparer = Comparer<object>.Create((a, b) =>
                (a is CompareRowVm ra ? key(ra) : double.MinValue).CompareTo(b is CompareRowVm rb ? key(rb) : double.MinValue)),
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
    private void OnAddCompareClick(object? sender, RoutedEventArgs e) { _ = AddCompareAsync(); }

    /// <summary>Enter in the compare box adds, like the button.</summary>
    private void OnCompareKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = AddCompareAsync();
    }

    /// <summary>Adds, then empties the box itself: its text binding has nothing new to push when
    /// the view model's text was already empty, so the typed fragment would otherwise stay.</summary>
    private async Task AddCompareAsync()
    {
        if (_vm is null) return;
        await _vm.AddCompareAsync();
        CompareBox.SelectedItem = null;
        CompareBox.Text = "";
    }

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

    /// <summary>Enter appraises without leaving the text box; Shift+Enter is the way to start a
    /// new line when typing a list by hand, since a pasted one brings its own.</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _vm is not null)
        {
            e.Handled = true;
            _ = _vm.AppraiseAsync();
        }
    }
}
