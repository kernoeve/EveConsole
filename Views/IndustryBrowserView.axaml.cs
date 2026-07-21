using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

public partial class IndustryBrowserView : ReactiveUserControl<IndustryBrowserViewModel>
{
    private readonly CellSelectionService _selectionSvc = new();

    private DataGrid? _cellGrid;
    private GridRow?  _anchorRow;
    private string?   _anchorCol;
    private GridRow?  _currentRow;
    private string?   _currentCol;
    private bool      _isDragging;
    private bool      _handlersAdded;

    private readonly Dictionary<DataGrid, (string? Col, bool Desc)> _sortState = [];
    private bool _detailClockActive;

    private const string RowSelectorTag = "__rowsel__";

    public IndustryBrowserView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is null) return;

        ActivityPicker.ItemsSource   = IndustryBrowserViewModel.ActivityOptions;
        ActivityPicker.SelectedIndex = 0;
        StatusPicker.ItemsSource     = IndustryBrowserViewModel.StatusOptions;
        StatusPicker.SelectedIndex   = 1;
        OwnerPicker.SelectedIndex    = 0;

        if (!_handlersAdded)
        {
            JobsGrid.Sorting += OnSorting;
            JobsGrid.AddHandler(InputElement.PointerMovedEvent,
                new EventHandler<PointerEventArgs>(OnPointerMoved), RoutingStrategies.Tunnel);
            JobsGrid.AddHandler(InputElement.PointerReleasedEvent,
                new EventHandler<PointerReleasedEventArgs>(OnPointerReleased), RoutingStrategies.Tunnel);
            StatusPicker.SelectionChanged += OnStatusPickerChanged;
            _handlersAdded = true;
        }

        JobsGrid.ContextMenu = BuildContextMenu();
        BuildColumns();

        ViewModel.WhenAnyValue(vm => vm.SelectedRow).Subscribe(UpdateDetailPanel);
        ViewModel.WhenAnyValue(vm => vm.OwnerOptions).Subscribe(opts =>
        {
            var prev = OwnerPicker.SelectedItem as string;
            OwnerPicker.ItemsSource   = opts;
            OwnerPicker.SelectedIndex = opts.IndexOf(prev ?? "All Owners") is > 0 and var i ? i : 0;
        });

        _ = ViewModel.LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_detailClockActive)
        {
            ClockService.Tick -= RefreshDetailTimeLeft;
            _detailClockActive = false;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnStatusPickerChanged(object? sender, SelectionChangedEventArgs e)
    {
        var status = StatusPicker.SelectedItem as string ?? "";
        if (status is not ("active" or "paused" or "ready" or "All Statuses"))
        {
            if (FromDatePicker.SelectedDate is null)
                FromDatePicker.SelectedDate = DateTime.Today.AddDays(-90);
            if (ThruDatePicker.SelectedDate is null)
                ThruDatePicker.SelectedDate = DateTime.Today;
        }
    }

    private void BuildColumns()
    {
        JobsGrid.Columns.Clear();

        JobsGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "", Tag = RowSelectorTag, IsReadOnly = true,
            Width = new DataGridLength(20), CanUserSort = false, CanUserResize = false,
            CellTemplate = new FuncDataTemplate<GridRow>((_, _) =>
                new TextBlock
                {
                    Text = "▶", Padding = new Thickness(2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 9, Foreground = Brushes.Gray,
                }),
        });

        foreach (var col in IndustryBrowserViewModel.DisplayColumns)
        {
            var c = col;
            JobsGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = c, Tag = c, IsReadOnly = true, CanUserSort = true,
                CellTemplate = new FuncDataTemplate<GridRow>(
                    (_, _) => new SelectableCell(JobsGrid, c, _selectionSvc)),
            });
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var copy  = new MenuItem { Header = "Copy" };
        copy.Click  += (_, _) => ExecuteCopy(includeHeaders: false);
        var copyH = new MenuItem { Header = "Copy w/Headers" };
        copyH.Click += (_, _) => ExecuteCopy(includeHeaders: true);
        return new ContextMenu { Items = { copy, copyH } };
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e) => ApplyFilters();
    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        ActivityPicker.SelectedIndex = 0;
        StatusPicker.SelectedIndex   = 1;
        OwnerPicker.SelectedIndex    = 0;
        FromDatePicker.SelectedDate  = null;
        ThruDatePicker.SelectedDate  = null;
        SearchBox.Text               = "";
        ApplyFilters();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyFilters();
    }

    private void ApplyFilters()
    {
        if (ViewModel is null) return;

        // Build from the picked date's Y/M/D as UTC midnight — the picker returns a Local-kind
        // DateTime, so pairing it directly with TimeSpan.Zero throws "UTC Offset does not match".
        static DateTimeOffset? AsUtcDate(DateTime? d) => d.HasValue
            ? new DateTimeOffset(d.Value.Year, d.Value.Month, d.Value.Day, 0, 0, 0, TimeSpan.Zero) : null;

        DateTimeOffset? from = AsUtcDate(FromDatePicker.SelectedDate);
        DateTimeOffset? thru = AsUtcDate(ThruDatePicker.SelectedDate);

        _ = ViewModel.ApplyFiltersAsync(
            ActivityPicker.SelectedItem as string,
            StatusPicker.SelectedItem  as string,
            SearchBox.Text?.Trim(),
            from, thru,
            OwnerPicker.SelectedItem   as string);
        ClearSelection();
    }

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        var col = e.Column.Tag as string ?? "";
        if (string.IsNullOrEmpty(col) || col == RowSelectorTag) return;

        _sortState.TryGetValue(JobsGrid, out var prev);
        bool desc = prev.Col == col && !prev.Desc;
        _sortState[JobsGrid] = (col, desc);

        var rows = (JobsGrid.ItemsSource as IEnumerable<GridRow>)?.ToList();
        if (rows is null || rows.Count == 0) return;

        ViewModel?.Sort(rows, col, desc);
        ClearSelection();

        foreach (var c in JobsGrid.Columns)
        {
            var tag = c.Tag as string ?? "";
            if (tag == RowSelectorTag) continue;
            c.Header = tag == col ? $"{tag} {(desc ? '▼' : '▲')}" : tag;
        }
    }

    private void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        var props = e.PointerPressedEventArgs.GetCurrentPoint(null).Properties;
        if (!props.IsLeftButtonPressed) return;

        var row    = e.Row?.DataContext as GridRow;
        var colTag = e.Column?.Tag as string;
        if (row is null || colTag is null) return;

        if (ViewModel is not null) ViewModel.SelectedRow = row;

        bool shift = (e.PointerPressedEventArgs.KeyModifiers & KeyModifiers.Shift) != 0;
        _isDragging = true;

        if (colTag == RowSelectorTag)
        {
            var dataCols = DataCols();
            if (dataCols.Count == 0) return;
            if (shift && _anchorRow is not null)
            { _currentRow = row; _currentCol = dataCols[^1]; }
            else
            { _cellGrid = JobsGrid; _anchorRow = row; _anchorCol = dataCols[0]; _currentRow = row; _currentCol = dataCols[^1]; }
        }
        else
        {
            if (shift && _anchorRow is not null)
            { _currentRow = row; _currentCol = colTag; }
            else
            { _cellGrid = JobsGrid; _anchorRow = row; _anchorCol = colTag; _currentRow = row; _currentCol = colTag; }
        }
        RefreshSelection();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        if (!e.GetCurrentPoint(JobsGrid).Properties.IsLeftButtonPressed) { _isDragging = false; return; }

        var (row, col) = HitTestCell(e.GetPosition(JobsGrid));
        if (row is null || col is null || col == RowSelectorTag) return;
        _currentRow = row; _currentCol = col;
        RefreshSelection();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => _isDragging = false;

    private (GridRow?, string?) HitTestCell(Point pos)
    {
        if (JobsGrid.InputHitTest(pos) is not Control hit) return (null, null);
        GridRow? row = null; string? col = null;
        Visual? cur = hit;
        while (cur is not null)
        {
            if (row is null && cur is Control ctrl && ctrl.DataContext is GridRow gr) row = gr;
            if (col is null && cur is SelectableCell sc) col = sc.ColName;
            if (row is not null && col is not null) break;
            cur = cur.GetVisualParent();
        }
        return (row, col);
    }

    private void ClearSelection()
    {
        _cellGrid = null; _anchorRow = null; _anchorCol = null;
        _currentRow = null; _currentCol = null; _isDragging = false;
        _selectionSvc.Clear();
    }

    private void RefreshSelection()
    {
        if (_anchorRow is null || _anchorCol is null) { _selectionSvc.Clear(); return; }
        var cols = DataCols();
        var rows = (JobsGrid.ItemsSource as IEnumerable<GridRow>)?.ToList();
        if (rows is null || cols.Count == 0) { _selectionSvc.Clear(); return; }

        int ac = cols.IndexOf(_anchorCol), ar = rows.IndexOf(_anchorRow);
        if (ac < 0 || ar < 0) { _selectionSvc.Clear(); return; }

        int cc = cols.IndexOf(_currentCol ?? _anchorCol); if (cc < 0) cc = ac;
        int cr = rows.IndexOf(_currentRow ?? _anchorRow); if (cr < 0) cr = ar;

        _selectionSvc.Set(JobsGrid, rows, cols,
            Math.Min(ar, cr), Math.Max(ar, cr),
            Math.Min(ac, cc), Math.Max(ac, cc));
    }

    private List<string> DataCols() =>
        JobsGrid.Columns
            .Select(c => c.Tag as string ?? "")
            .Where(t => t.Length > 0 && t != RowSelectorTag)
            .ToList();

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (e.KeyModifiers & KeyModifiers.Control) != 0)
        { e.Handled = true; ExecuteCopy(includeHeaders: false); }
    }

    private void ExecuteCopy(bool includeHeaders)
    {
        if (_anchorRow is null || _anchorCol is null) return;
        var allCols = DataCols();
        if (allCols.Count == 0) return;
        var allRows = (JobsGrid.ItemsSource as IEnumerable<GridRow>)?.ToList();
        if (allRows is null) return;

        int ac = allCols.IndexOf(_anchorCol), cc = allCols.IndexOf(_currentCol ?? _anchorCol);
        int c0 = Math.Max(0, Math.Min(ac, cc)), c1 = Math.Max(0, Math.Max(ac, cc));
        var colRange = allCols.Skip(c0).Take(c1 - c0 + 1).ToList();

        int ar = allRows.IndexOf(_anchorRow), cr = allRows.IndexOf(_currentRow ?? _anchorRow);
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

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    private void UpdateDetailPanel(GridRow? row)
    {
        if (_detailClockActive)
        {
            ClockService.Tick -= RefreshDetailTimeLeft;
            _detailClockActive = false;
        }

        if (row is null)
        {
            DetailOwnerType.Text      = "";
            DetailOwner.Text          = "";
            DetailInstaller.Text      = "";
            DetailStartDate.Text      = "";
            DetailEndDate.Text        = "";
            DetailCompletedBy.Text    = "";
            DetailActivity.Text       = "";
            DetailStatus.Text         = "";
            DetailRuns.Text           = "";
            DetailItemsProduced.Text   = "";
            DetailSuccessChance.Text   = "";
            DetailBlueprint.Text      = "";
            DetailProduct.Text        = "";
            DetailFacility.Text       = "";
            DetailLocation.Text       = "";
            DetailME.Text             = "";
            DetailTE.Text             = "";
            DetailTimeLeft.Text       = "";
            DetailCost.Text           = "";
            DetailItemsProducedImg.Text = "";
            BlueprintImage.Source     = null;
            ProductImage.Source       = null;
            FacilityImage.Source      = null;
            return;
        }

        var ownerType = row["Owner Type"];
        DetailOwnerType.Text      = ownerType == "character" ? "Player" : ownerType == "corporation" ? "Corp" : ownerType;
        DetailOwner.Text          = row["Owner"];
        DetailInstaller.Text      = row["Installer"];
        DetailStartDate.Text      = row["Start Date"];
        DetailEndDate.Text        = row["End Date"];
        DetailCompletedBy.Text    = row[IndustryBrowserViewModel.ColCompletedBy];
        DetailActivity.Text       = row["Activity"];
        DetailStatus.Text         = row["Status"];
        DetailRuns.Text           = row["Runs"];
        DetailItemsProduced.Text  = row["Items Produced"];
        var prob = row["Probability"];
        DetailSuccessChance.Text  = double.TryParse(prob, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var p)
            ? $"{Math.Round(p * 100.0, 1):0.0}%"
            : "";
        DetailItemsProducedImg.Text = row["Items Produced"] is { Length: > 0 } ip ? $"× {ip}" : "";
        DetailBlueprint.Text      = row["Blueprint"];
        DetailProduct.Text        = row["Product"];
        DetailFacility.Text       = row["Facility"];
        var sys = row["Solar System"]; var reg = row["Region"]; var sec = row["Security"];
        DetailLocation.Text = string.Join("  ·  ",
            new[] { sys, reg, sec }.Where(s => !string.IsNullOrEmpty(s)));
        DetailME.Text             = row[IndustryBrowserViewModel.ColME];
        DetailTE.Text             = row[IndustryBrowserViewModel.ColTE];
        DetailCost.Text           = row["Cost"] is { Length: > 0 } c ? $"{c} ISK" : "";

        RefreshDetailTimeLeft();
        var sl = row["Status"].ToLowerInvariant();
        if (sl is "active" or "paused")
        {
            ClockService.Tick  += RefreshDetailTimeLeft;
            _detailClockActive  = true;
        }

        _ = LoadDetailImagesAsync(row);
    }

    private void RefreshDetailTimeLeft()
    {
        var row = ViewModel?.SelectedRow;
        if (row is null) { DetailTimeLeft.Text = ""; return; }
        DetailTimeLeft.Text = TimeRemainingHelper.Compute(row);
    }

    private async Task LoadDetailImagesAsync(GridRow row)
    {
        long.TryParse(row[IndustryBrowserViewModel.ColBlueprintTypeId].Replace(",", ""), out var bpTypeId);
        long.TryParse(row[IndustryBrowserViewModel.ColProductTypeId].Replace(",", ""),   out var prodTypeId);
        long.TryParse(row[IndustryBrowserViewModel.ColFacilityTypeId].Replace(",", ""),  out var facTypeId);

        int.TryParse(row[IndustryBrowserViewModel.ColActivityId].Replace(",", ""), out var actId);
        // Copying (5) and invention (8) output a blueprint COPY (lighter "bpc" icon); research
        // (3/4) outputs the original blueprint; everything else is a normal item.
        var prodVariant = actId switch { 5 or 8 => "bpc", 3 or 4 => "bp", _ => "icon" };

        var tasks = new[]
        {
            EveImageLoader.LoadTypeAsync(bpTypeId,  "bp"),
            EveImageLoader.LoadTypeAsync(prodTypeId, prodVariant),
            EveImageLoader.LoadTypeAsync(facTypeId,  "render"),
        };
        var imgs = await Task.WhenAll(tasks);
        Bitmap? bpImg = imgs[0], prodImg = imgs[1], facImg = imgs[2];

        if (ViewModel?.SelectedRow != row) return;

        BlueprintImage.Source = bpImg;
        ProductImage.Source   = prodImg;
        FacilityImage.Source  = facImg;
    }
}
