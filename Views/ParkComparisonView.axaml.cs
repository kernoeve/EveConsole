using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Controls;
using EveConsole.Localization;
using EveConsole.Services;
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
        SizeSummary();   // ⚠️ else a view made for results already there leaves the scroll bar over the last row
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

    // ── Pictures for the clipboard ──────────────────────────────────────────

    /// <summary>Pictures are drawn at twice the screen's scale, so they stay sharp pasted into
    /// Discord or a document and looked at full size.</summary>
    private const double PictureScale = 2;

    private void OnCopyCostChart(object? sender, RoutedEventArgs e) => _ = CopyAsync(CopyCostButton, CostChartPicture);
    private void OnCopyTimeChart(object? sender, RoutedEventArgs e) => _ = CopyAsync(CopyTimeButton, TimeChartPicture);
    private void OnCopySummary(object? sender, RoutedEventArgs e)   => _ = CopyAsync(CopySummaryButton, SummaryPicture);

    /// <summary>The cost chart's panel as it shows: title, the line charted, chart and legend.</summary>
    public Bitmap? CostChartPicture() => PanelPicture(CostChartPanel, CopyCostButton);

    /// <summary>The time chart's panel as it shows.</summary>
    public Bitmap? TimeChartPicture() => PanelPicture(TimeChartPanel, CopyTimeButton);

    /// <summary>Puts a picture on the clipboard, and says so on the button for a moment.</summary>
    private async Task CopyAsync(Button button, Func<Bitmap?> picture)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            if (picture() is not { } bitmap) return;
            await clipboard.SetBitmapAsync(bitmap);
            var label = button.Content;
            button.Content = IndustryText.CompareCopied;
            DispatcherTimer.RunOnce(() => button.Content = label, TimeSpan.FromSeconds(1.5));
        }
        catch (Exception ex)
        {
            _vm?.ShowStatus(string.Format(IndustryText.CompareCopyFailed, ex.Message));
        }
    }

    /// <summary>A panel on screen, drawn as it is, less its own copy button: hidden for the
    /// drawing only, it leaves its place empty rather than moving anything.</summary>
    private static Bitmap? PanelPicture(Control panel, Control button)
    {
        var size = panel.Bounds.Size;
        if (!panel.IsVisible || size.Width < 1 || size.Height < 1) return null;
        var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(size.Width * PictureScale), (int)Math.Ceiling(size.Height * PictureScale)),
            new Vector(96 * PictureScale, 96 * PictureScale));
        button.IsVisible = false;
        try { bitmap.Render(panel); }
        finally { button.IsVisible = true; }
        return bitmap;
    }

    /// <summary>
    /// The summary as a picture: every row and every column, however far the grid is scrolled,
    /// so it is drawn afresh off screen rather than captured. Headed by what it is and which way
    /// it is laid out, each group's four columns under its name and wash as on screen.
    /// </summary>
    public Bitmap? SummaryPicture()
    {
        if (_vm is null || _vm.SummaryRows.Count == 0) return null;
        var groups = _vm.SummaryGroups;
        var rows   = _vm.SummaryRows;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var i = 0; i < groups.Count * 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var i = 0; i < rows.Count + 2; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        void Place(Control c, int row, int col, int rowSpan = 1, int colSpan = 1)
        {
            Grid.SetRow(c, row); Grid.SetColumn(c, col);
            Grid.SetRowSpan(c, rowSpan); Grid.SetColumnSpan(c, colSpan);
            grid.Children.Add(c);
        }
        static TextBlock Text(string text, IBrush brush, bool right, bool strong = false) => new()
        {
            Text = text, Foreground = brush, FontSize = 11,
            FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal,
            Margin = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };

        // Grounds first, then the words over them: the header strip, and each group's wash from
        // its column headings down, with a line where it begins.
        Place(new Border { Background = Palette.SurfaceHeader }, 1, 0, 1, 1 + groups.Count * 4);
        for (var g = 0; g < groups.Count; g++)
        {
            var wash = Resource(WashKeys[g % 3]);
            var title = Text(groups[g], Palette.TextPrimary, right: false, strong: true);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            Place(new Border { Background = wash, Margin = new Thickness(1, 0, 0, 1), Child = title }, 0, 1 + 4 * g, 1, 4);
            Place(new Border { Background = wash, BorderBrush = Palette.BorderSubtle, BorderThickness = new Thickness(1, 0, 0, 0) },
                  1, 1 + 4 * g, rows.Count + 1, 4);
        }

        Place(Text(_vm.SummaryByPark ? IndustryText.CompareColPark : IndustryText.CompareColJob, Palette.TextMuted, right: false), 1, 0);
        string[] headers = [IndustryText.CompareColCost, IndustryText.CompareColVsBest, IndustryText.CompareColTime, IndustryText.CompareColVsBest];
        for (var g = 0; g < groups.Count; g++)
            for (var k = 0; k < 4; k++)
                Place(Text(headers[k], Palette.TextMuted, right: true), 1, 1 + 4 * g + k);

        for (var r = 0; r < rows.Count; r++)
        {
            Place(Text(rows[r].Title, Palette.TextPrimary, right: false), 2 + r, 0);
            for (var g = 0; g < groups.Count; g++)
            {
                var cell = rows[r].Cells[g];
                Place(Text(cell.CostText,     cell.FigureColor, right: true), 2 + r, 1 + 4 * g);
                Place(Text(cell.OverBestText, cell.OverColor,   right: true), 2 + r, 2 + 4 * g);
                Place(Text(cell.TimeText,     cell.FigureColor, right: true), 2 + r, 3 + 4 * g);
                Place(Text(cell.SlowerText,   cell.SlowerColor, right: true), 2 + r, 4 + 4 * g);
            }
        }

        var heading = new TextBlock
        {
            Text = $"{IndustryText.CompareSummaryTitle}  ·  {IndustryText.CompareSummaryRows}: {_vm.SummaryLayout.Label}",
            Foreground = Palette.TextMuted, FontSize = 12, FontWeight = FontWeight.SemiBold,
        };
        var note = new TextBlock { Text = IndustryText.CompareSummaryNote, Foreground = Palette.TextFaint, FontSize = 10.5 };
        var picture = new Border
        {
            Background = Palette.SurfaceBase,
            Padding    = new Thickness(12),
            Child      = new StackPanel { Spacing = 4, Children = { heading, note, new Border { Margin = new Thickness(0, 4, 0, 0), Child = grid } } },
        };
        picture.Measure(Size.Infinity);
        picture.Arrange(new Rect(picture.DesiredSize));

        var size = picture.Bounds.Size;
        var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(size.Width * PictureScale), (int)Math.Ceiling(size.Height * PictureScale)),
            new Vector(96 * PictureScale, 96 * PictureScale));
        bitmap.Render(picture);
        return bitmap;
    }

    private static IBrush Resource(string key) =>
        Application.Current?.FindResource(Application.Current.ActualThemeVariant, key) as IBrush ?? Brushes.Transparent;

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
