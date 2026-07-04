using System.Reactive.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using EveCortex.ViewModels;
using ReactiveUI;

namespace EveCortex.Views;

public partial class AssetBrowserView : ReactiveUserControl<AssetBrowserViewModel>
{
    private record FilterRowUi(ComboBox ColPicker, ComboBox OpPicker, TextBox ValBox, StackPanel Row);
    private readonly List<FilterRowUi> _filterControls = [];

    private bool _handlersAdded;
    private bool _initialized;

    private readonly Dictionary<DataGrid, (string? Col, bool Desc)> _aggSort = [];

    private readonly CellSelectionService _selectionSvc = new();
    private DataGrid? _cellGrid;
    private GridRow?  _anchorRow;
    private string?   _anchorCol;
    private GridRow?  _currentRow;
    private string?   _currentCol;
    private bool      _isDragging;

    private const string RowSelectorTag = "__rowsel__";

    public AssetBrowserView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is null) return;

        AssetGrid.ItemsSource = ViewModel.Rows;

        if (!_handlersAdded)
        {
            AssetGrid.AddHandler(
                ScrollViewer.ScrollChangedEvent,
                new EventHandler<ScrollChangedEventArgs>(OnGridScrollChanged),
                RoutingStrategies.Bubble);

            AssetGrid.Sorting    += OnDetailSorting;
            LocationGrid.Sorting += OnLocationSorting;
            SystemGrid.Sorting   += OnSystemSorting;
            RegionGrid.Sorting   += OnRegionSorting;

            foreach (var grid in new[] { AssetGrid, LocationGrid, SystemGrid, RegionGrid })
            {
                grid.AddHandler(InputElement.PointerMovedEvent,
                    new EventHandler<PointerEventArgs>(OnGridPointerMoved), RoutingStrategies.Tunnel);
                grid.AddHandler(InputElement.PointerReleasedEvent,
                    new EventHandler<PointerReleasedEventArgs>(OnGridPointerReleased), RoutingStrategies.Tunnel);
            }

            _handlersAdded = true;
        }

        ViewModel.WhenAnyValue(vm => vm.Columns)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(cols => RegenerateColumns(AssetGrid, cols));

        LocationGrid.ItemsSource = ViewModel.LocationRows;
        ViewModel.WhenAnyValue(vm => vm.LocationColumns)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(cols => RegenerateColumns(LocationGrid, cols, noHide: true));

        SystemGrid.ItemsSource = ViewModel.SystemRows;
        ViewModel.WhenAnyValue(vm => vm.SystemColumns)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(cols => RegenerateColumns(SystemGrid, cols, noHide: true));

        RegionGrid.ItemsSource = ViewModel.RegionRows;
        ViewModel.WhenAnyValue(vm => vm.RegionColumns)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(cols => RegenerateColumns(RegionGrid, cols, noHide: true));

        AttachContextMenu(AssetGrid, withItemBrowser: true);
        AttachContextMenu(LocationGrid);
        AttachContextMenu(SystemGrid);
        AttachContextMenu(RegionGrid);

        if (!_initialized)
        {
            AddFilterRow(withRemove: false);
            _initialized = true;
        }
    }

    private void RegenerateColumns(DataGrid grid, IReadOnlyList<string> columns, bool noHide = false)
    {
        grid.ItemsSource = null;
        grid.Columns.Clear();

        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header        = "",
            Tag           = RowSelectorTag,
            IsReadOnly    = true,
            Width         = new DataGridLength(20),
            CanUserSort   = false,
            CanUserResize = false,
            CellTemplate  = new FuncDataTemplate<GridRow>((_, _) =>
                new TextBlock
                {
                    Text                = "▶",
                    Padding             = new Thickness(2, 0),
                    VerticalAlignment   = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize            = 9,
                    Foreground          = Brushes.Gray,
                }),
        });

        foreach (var col in columns)
        {
            if (!noHide && AssetBrowserViewModel.HiddenColumns.Contains(col)) continue;
            var captured = col;
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header      = captured,
                Tag         = captured,
                IsReadOnly  = true,
                CanUserSort = true,
                CellTemplate = new FuncDataTemplate<GridRow>(
                    (_, _) => new SelectableCell(grid, captured, _selectionSvc)),
            });
        }

        if (ViewModel is null) return;
        grid.ItemsSource = grid == AssetGrid      ? ViewModel.Rows
                         : grid == LocationGrid   ? ViewModel.LocationRows
                         : grid == SystemGrid     ? ViewModel.SystemRows
                                                  : ViewModel.RegionRows;
    }

    private void AttachContextMenu(DataGrid grid, bool withItemBrowser = false)
    {
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => ExecuteCopy(grid, includeHeaders: false);

        var copyH = new MenuItem { Header = "Copy w/Headers" };
        copyH.Click += (_, _) => ExecuteCopy(grid, includeHeaders: true);

        var menu = new ContextMenu { Items = { copy, copyH } };

        if (withItemBrowser)
        {
            var openItem = new MenuItem { Header = "Open in Item Browser" };
            openItem.Click += (_, _) =>
            {
                if (ViewModel?.OpenInItemBrowser is null || _anchorRow is null) return;
                var typeIdStr = _anchorRow["Type Id"];
                if (!int.TryParse(typeIdStr, out var typeId) || typeId <= 0) return;
                ViewModel.OpenInItemBrowser(typeId, _anchorRow["Type Name"]);
            };
            menu.Items.Insert(0, openItem);
            menu.Items.Insert(1, new Separator());
        }

        grid.ContextMenu = menu;
    }

    private void OnGridScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.HasMore) return;
        if (e.Source is not ScrollViewer sv) return;
        if (e.OffsetDelta.Y == 0 && e.ExtentDelta.Y == 0) return;
        if (sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 300)
            _ = ViewModel.LoadMoreAsync();
    }

    private async void OnDetailSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (ViewModel is null) return;
        var col = (e.Column.Tag as string) ?? "";
        if (string.IsNullOrEmpty(col) || col == RowSelectorTag) return;
        e.Handled = true;
        await ViewModel.SortAsync(col);
        UpdateSortHeaders(AssetGrid);
    }

    private void OnLocationSorting(object? sender, DataGridColumnEventArgs e) => ClientSort(LocationGrid, e);
    private void OnSystemSorting  (object? sender, DataGridColumnEventArgs e) => ClientSort(SystemGrid,   e);
    private void OnRegionSorting  (object? sender, DataGridColumnEventArgs e) => ClientSort(RegionGrid,   e);

    private void ClientSort(DataGrid grid, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        var colName = e.Column.Tag as string ?? "";
        if (string.IsNullOrEmpty(colName) || colName == RowSelectorTag) return;

        _aggSort.TryGetValue(grid, out var prev);
        bool desc = prev.Col == colName && !prev.Desc;
        _aggSort[grid] = (colName, desc);

        var items = (grid.ItemsSource as IEnumerable<GridRow>)?.ToList();
        if (items is null || items.Count == 0) return;

        var sample  = items.FirstOrDefault(r => r[colName].Length > 0)?[colName] ?? "";
        bool numeric = double.TryParse(sample.Replace(",", ""), out _);

        var sorted = numeric
            ? (desc ? items.OrderByDescending(r => ParseNum(r[colName])) : items.OrderBy(r => ParseNum(r[colName])))
            : (desc ? items.OrderByDescending(r => r[colName])           : items.OrderBy(r => r[colName]));

        grid.ItemsSource = null;
        grid.ItemsSource = sorted.ToList();

        _selectionSvc.Clear();
        _anchorRow = null; _anchorCol = null; _currentRow = null; _currentCol = null;

        foreach (var c in grid.Columns)
        {
            var tag = c.Tag as string ?? "";
            if (tag == RowSelectorTag) continue;
            c.Header = tag == colName ? $"{tag} {(desc ? '▼' : '▲')}" : tag;
        }
    }

    private static double ParseNum(string s) =>
        double.TryParse(s.Replace(",", ""), out var v) ? v : double.MinValue;

    private void UpdateSortHeaders(DataGrid grid)
    {
        if (ViewModel is null) return;
        foreach (var c in grid.Columns)
        {
            var name = c.Tag as string ?? "";
            if (name == RowSelectorTag) continue;
            c.Header = ViewModel.SortColumn == name
                ? $"{name} {(ViewModel.SortDescending ? '▼' : '▲')}"
                : name;
        }
    }

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        _cellGrid = null; _anchorRow = null; _anchorCol = null;
        _currentRow = null; _currentCol = null; _isDragging = false;
        _selectionSvc.Clear();
    }

    private void OnDetailCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
        => TrackCell(AssetGrid, e);

    private void OnAggCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
        => TrackCell((sender as DataGrid)!, e);

    private void TrackCell(DataGrid grid, DataGridCellPointerPressedEventArgs e)
    {
        var props = e.PointerPressedEventArgs.GetCurrentPoint(null).Properties;
        if (!props.IsLeftButtonPressed) return;

        var row    = e.Row?.DataContext as GridRow;
        var colTag = e.Column?.Tag as string;
        if (row is null || colTag is null) return;

        bool shift = (e.PointerPressedEventArgs.KeyModifiers & KeyModifiers.Shift) != 0;
        _isDragging = true;

        if (colTag == RowSelectorTag)
        {
            var dataCols = grid.Columns
                .Select(c => c.Tag as string ?? "")
                .Where(t => t.Length > 0 && t != RowSelectorTag)
                .ToList();
            if (dataCols.Count == 0) return;

            if (shift && _cellGrid == grid && _anchorRow is not null)
            {
                _currentRow = row;
                _currentCol = dataCols[^1];
            }
            else
            {
                _cellGrid   = grid;
                _anchorRow  = row;
                _anchorCol  = dataCols[0];
                _currentRow = row;
                _currentCol = dataCols[^1];
            }
        }
        else
        {
            if (shift && _cellGrid == grid && _anchorRow is not null)
            {
                _currentRow = row;
                _currentCol = colTag;
            }
            else
            {
                _cellGrid   = grid;
                _anchorRow  = row;
                _anchorCol  = colTag;
                _currentRow = row;
                _currentCol = colTag;
            }
        }
        RefreshSelectionService();
    }

    private void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || _cellGrid is null || sender is not DataGrid grid || grid != _cellGrid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) { _isDragging = false; return; }

        var pos = e.GetPosition(grid);
        var (dataRow, colTag) = HitTestCell(grid, pos);
        if (dataRow is null || colTag is null || colTag == RowSelectorTag) return;

        _currentRow = dataRow;
        _currentCol = colTag;
        RefreshSelectionService();
    }

    private void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
    }

    private static (GridRow? Row, string? ColTag) HitTestCell(DataGrid grid, Point pos)
    {
        if (grid.InputHitTest(pos) is not Control hit) return (null, null);

        GridRow? row = null;
        string?  col = null;
        Visual?  cur = hit;

        while (cur is not null)
        {
            if (row is null && cur is Control ctrl && ctrl.DataContext is GridRow gr) row = gr;
            if (col is null && cur is SelectableCell sc) col = sc.ColName;
            if (row is not null && col is not null) break;
            cur = cur.GetVisualParent();
        }
        return (row, col);
    }

    private void RefreshSelectionService()
    {
        if (_cellGrid is null || _anchorRow is null || _anchorCol is null)
        {
            _selectionSvc.Clear();
            return;
        }

        var allCols = _cellGrid.Columns
            .Select(c => c.Tag as string ?? "")
            .Where(h => h.Length > 0 && h != RowSelectorTag)
            .ToList();
        var allRows = (_cellGrid.ItemsSource as IEnumerable<GridRow>)?.ToList();
        if (allRows is null || allCols.Count == 0) { _selectionSvc.Clear(); return; }

        int ac = allCols.IndexOf(_anchorCol);
        int ar = allRows.IndexOf(_anchorRow);
        if (ac < 0 || ar < 0) { _selectionSvc.Clear(); return; }

        int cc = allCols.IndexOf(_currentCol  ?? _anchorCol);
        int cr = allRows.IndexOf(_currentRow  ?? _anchorRow);
        if (cc < 0) cc = ac;
        if (cr < 0) cr = ar;

        _selectionSvc.Set(_cellGrid, allRows, allCols,
            Math.Min(ar, cr), Math.Max(ar, cr),
            Math.Min(ac, cc), Math.Max(ac, cc));
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (e.KeyModifiers & KeyModifiers.Control) == 0) return;
        e.Handled = true;
        if (sender is DataGrid grid)
            ExecuteCopy(grid, includeHeaders: false);
    }

    private void ExecuteCopy(DataGrid grid, bool includeHeaders)
    {
        if (_cellGrid != grid || _anchorRow is null || _anchorCol is null) return;

        var allCols = grid.Columns
            .Select(c => c.Tag as string ?? "")
            .Where(h => h.Length > 0 && h != RowSelectorTag)
            .ToList();
        if (allCols.Count == 0) return;

        var allRows = (grid.ItemsSource as IEnumerable<GridRow>)?.ToList();
        if (allRows is null) return;

        int ac = allCols.IndexOf(_anchorCol);
        int cc = allCols.IndexOf(_currentCol ?? _anchorCol);
        int c0 = Math.Min(ac, cc); if (c0 < 0) c0 = 0;
        int c1 = Math.Max(ac, cc); if (c1 < 0) c1 = c0;
        var colRange = allCols.Skip(c0).Take(c1 - c0 + 1).ToList();

        int ar = allRows.IndexOf(_anchorRow);
        int cr = allRows.IndexOf(_currentRow ?? _anchorRow);

        string text;
        if (ar < 0 || cr < 0)
        {
            text = _anchorRow[_anchorCol];
        }
        else
        {
            int r0 = Math.Min(ar, cr), r1 = Math.Max(ar, cr);
            var sb = new StringBuilder();
            if (includeHeaders) sb.AppendLine(string.Join("\t", colRange));
            for (int r = r0; r <= r1; r++)
                sb.AppendLine(string.Join("\t", colRange.Select(c => allRows[r][c])));
            text = sb.ToString().TrimEnd();
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        _ = clipboard?.SetTextAsync(text);
    }

    private FilterRowUi CreateFilterRow(bool withRemove)
    {
        var colPicker = new ComboBox
        {
            Width             = 155,
            FontSize          = 11,
            ItemsSource       = AssetBrowserViewModel.FilterableColumns,
            PlaceholderText   = "column…",
            MaxDropDownHeight = 300,
        };

        var opPicker = new ComboBox
        {
            Width             = 155,
            FontSize          = 11,
            ItemsSource       = EsiExplorerViewModel.Operators,
            SelectedIndex     = 0,
            MaxDropDownHeight = 300,
        };

        var valBox = new TextBox
        {
            Watermark = "value…",
            Width     = 200,
            FontSize  = 11,
        };
        valBox.KeyDown += OnFilterKeyDown;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(colPicker);
        row.Children.Add(opPicker);
        row.Children.Add(valBox);

        var controls = new FilterRowUi(colPicker, opPicker, valBox, row);

        if (withRemove)
        {
            var removeBtn = new Button
            {
                Content           = "×",
                FontSize          = 13,
                Padding           = new Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var captured = controls;
            removeBtn.Click += (_, _) => RemoveFilterRow(captured);
            row.Children.Add(removeBtn);
        }

        return controls;
    }

    private void AddFilterRow(bool withRemove)
    {
        var controls = CreateFilterRow(withRemove);
        _filterControls.Add(controls);
        FilterRowsPanel.Children.Add(controls.Row);
        AddFilterButton.IsEnabled = _filterControls.Count < 10;
    }

    private void RemoveFilterRow(FilterRowUi controls)
    {
        FilterRowsPanel.Children.Remove(controls.Row);
        _filterControls.Remove(controls);
        AddFilterButton.IsEnabled = _filterControls.Count < 10;
    }

    private void ResetFilterRows()
    {
        while (_filterControls.Count > 1)
        {
            var last = _filterControls[^1];
            FilterRowsPanel.Children.Remove(last.Row);
            _filterControls.RemoveAt(_filterControls.Count - 1);
        }
        if (_filterControls.Count > 0)
        {
            _filterControls[0].ColPicker.SelectedItem = null;
            _filterControls[0].OpPicker.SelectedIndex  = 0;
            _filterControls[0].ValBox.Text              = "";
        }
        AddFilterButton.IsEnabled = true;
    }

    private void OnApplyFilterClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var filters = _filterControls
            .Select(fc => (
                Column: fc.ColPicker.SelectedItem as string,
                Op:     fc.OpPicker.SelectedItem as FilterOp,
                Value:  fc.ValBox.Text))
            .ToList();
        _ = ViewModel.ApplyFiltersAsync(filters);
    }

    private void OnClearFilterClick(object? sender, RoutedEventArgs e)
    {
        ResetFilterRows();
        _ = ViewModel?.ClearFiltersAsync();
    }

    private void OnAddFilterClick(object? sender, RoutedEventArgs e)
    {
        if (_filterControls.Count >= 10) return;
        AddFilterRow(withRemove: true);
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnApplyFilterClick(sender, e);
    }

    private void OnAssetGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel?.OpenInItemBrowser is null || _anchorRow is null) return;
        var typeIdStr = _anchorRow["Type Id"];
        if (!int.TryParse(typeIdStr, out var typeId) || typeId <= 0) return;
        ViewModel.OpenInItemBrowser(typeId, _anchorRow["Type Name"]);
    }

    private void OnLoadMoreClick(object? sender, RoutedEventArgs e)
    {
        _ = ViewModel?.LoadMoreAsync();
    }
}
