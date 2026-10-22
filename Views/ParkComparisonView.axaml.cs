using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using EveConsole.Controls;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>Indy Parks, Compare. The park list is read again each time the tab is shown, so a
/// park added or renamed on the other tab is there.</summary>
public partial class ParkComparisonView : UserControl
{
    private ParkComparisonViewModel? _vm;

    /// <summary>The wash over each group of the summary's columns and its heading, three in turn,
    /// so no two neighbouring groups share one. The classes are styled in the view.</summary>
    private static readonly string[] WashKeys    = ["ColumnGroupABrush", "ColumnGroupBBrush", "ColumnGroupCBrush"];
    private static readonly string[] CellClasses = ["ga", "gb", "gc"];

    public ParkComparisonView()
    {
        InitializeComponent();
        SummaryBand.Target = SummaryGrid;
        // ⚠️ Both. The tab's content is attached before its DataContext binding resolves, so on
        // the first showing only the second finds a view model; after that the first does.
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is ParkComparisonViewModel vm) await vm.LoadParksAsync();
        };
        DataContextChanged += async (_, _) =>
        {
            Attach(DataContext as ParkComparisonViewModel);
            if (DataContext is ParkComparisonViewModel vm) await vm.LoadParksAsync();
        };
    }

    private void Attach(ParkComparisonViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        RebuildSummaryColumns();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ParkComparisonViewModel.SummaryGroups)) RebuildSummaryColumns();
        if (e.PropertyName == nameof(ParkComparisonViewModel.HasSummary))    SizeSummary();
    }

    /// <summary>
    /// Tall enough for its rows and the sideways scroll bar beneath them, to a limit. ⚠️ Left to
    /// size itself, the grid fits its rows only and the scroll bar, which it needs as soon as
    /// there are a few groups, is drawn over the last row.
    /// </summary>
    private void SizeSummary()
    {
        if (_vm is null) return;
        const double header = 34, row = 22, bar = 16, most = 260;
        SummaryGrid.Height = Math.Min(most, header + row * _vm.SummaryRows.Count + bar);
    }

    // ── The summary's columns ───────────────────────────────────────────────

    /// <summary>
    /// The row's name, then for each park (rows are jobs) or job (rows are parks) its cost, cost
    /// against the best, time and time against the best. Built here because a DataGrid's columns
    /// are not bindable; each cell binds to its row's cell for that group by index.
    /// </summary>
    private void RebuildSummaryColumns()
    {
        SummaryGrid.Columns.Clear();
        if (_vm is null) { SummaryBand.SetGroups([]); return; }

        SummaryGrid.Columns.Add(new DataGridTextColumn
        {
            Header  = _vm.SummaryByPark ? IndustryText.CompareColPark : IndustryText.CompareColJob,
            Binding = new Binding(nameof(CompareSummaryRowVm.Title)),
            Width   = new DataGridLength(_vm.SummaryByPark ? 150 : 230),
        });

        for (var i = 0; i < _vm.SummaryGroups.Count; i++)
        {
            var index = i;
            var columns = new[]
            {
                Column(IndustryText.CompareColCost,   $"Cells[{index}].CostText",     $"Cells[{index}].FigureColor", 84, r => (double)Cell(r, index).Total, $"Cells[{index}].Detail"),
                Column(IndustryText.CompareColVsBest, $"Cells[{index}].OverBestText", $"Cells[{index}].OverColor",   86, r => Cell(r, index).OverBest,      null),
                Column(IndustryText.CompareColTime,   $"Cells[{index}].TimeText",     $"Cells[{index}].FigureColor", 78, r => Cell(r, index).Seconds,       null),
                Column(IndustryText.CompareColVsBest, $"Cells[{index}].SlowerText",   $"Cells[{index}].SlowerColor", 86, r => Cell(r, index).SlowerBy,      null),
            };
            columns[0].CellStyleClasses.Add("gs");   // the line where one group's wash ends and the next begins
            foreach (var column in columns)
            {
                column.CellStyleClasses.Add(CellClasses[index % 3]);
                SummaryGrid.Columns.Add(column);
            }
        }
        SummaryBand.SetGroups(_vm.SummaryGroups.Select((title, i) => new ColumnGroup(1 + 4 * i, 4, title, WashKeys[i % 3])));
    }

    private static CompareSummaryCellVm Cell(CompareSummaryRowVm row, int index) => row.Cells[index];

    /// <summary>A column bound by path and sorted by a key, with a tip when given. A template
    /// column has no binding of its own for the grid to sort on or to copy, so it is handed a
    /// comparer and told it may sort, and told what Ctrl+C on a row copies: the text shown.</summary>
    private static DataGridTemplateColumn Column(string header, string textPath, string colorPath, double width,
                                                 Func<CompareSummaryRowVm, double> key, string? tipPath) =>
        new()
        {
            Header = header,
            Width  = new DataGridLength(width),
            ClipboardContentBinding = new Binding(textPath),
            CanUserSort        = true,
            CustomSortComparer = Comparer<object>.Create((a, b) =>
                (a is CompareSummaryRowVm ra ? key(ra) : double.MaxValue).CompareTo(b is CompareSummaryRowVm rb ? key(rb) : double.MaxValue)),
            CellTemplate = new FuncDataTemplate<CompareSummaryRowVm>((_, _) =>
            {
                var block = new TextBlock
                {
                    TextAlignment = TextAlignment.Right,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    [!TextBlock.TextProperty]       = new Binding(textPath),
                    [!TextBlock.ForegroundProperty] = new Binding(colorPath),
                };
                if (tipPath is not null) block[!ToolTip.TipProperty] = new Binding(tipPath);
                return block;
            }),
        };
}
