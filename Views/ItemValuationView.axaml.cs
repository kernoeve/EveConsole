using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EveConsole.Controls;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ItemValuationView : UserControl
{
    private ItemValuationViewModel? _vm;

    /// <summary>The wash over each group of columns and its heading: three in turn, so on the
    /// compare tab no two neighbouring stations share one. The classes are styled in the view.</summary>
    private static readonly string[] WashKeys    = ["ColumnGroupABrush", "ColumnGroupBBrush", "ColumnGroupCBrush"];
    private static readonly string[] CellClasses = ["ga", "gb", "gc"];

    /// <summary>The three values' headings over their columns; each carries its total, standing
    /// and coverage beneath once there is a result.</summary>
    private static readonly ColumnGroup[] ValueGroups =
    [
        new(3, 3, "Market value",      WashKeys[0]),
        new(6, 3, "Build value",       WashKeys[1]),
        new(9, 3, "Reprocessed value", WashKeys[2]),
    ];

    /// <summary>The pasted list's pane: how wide, remembered on this machine. Whether it is folded
    /// away is not remembered: it folds once the list is valued and opens again to be changed.</summary>
    private const  string ListWidthKey     = "valuation.listWidth";
    private const  double DefaultListWidth = 340;
    private double _listWidth = DefaultListWidth;

    public ItemValuationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ItemValuationViewModel);

        // Headings over the groups of columns, which the grid's own headers cannot span.
        ValuesBand.Target  = ValuesGrid;
        CompareBand.Target = CompareGrid;
        RefreshValuesBand();

        // The list pane at the width it was left, open until there is a result.
        _listWidth = Math.Max(120, UiState.GetLong(ListWidthKey, (long)DefaultListWidth));
        SetListHidden(false);
        Split.ColumnDefinitions[0].PropertyChanged += (_, e) =>
        {
            if (e.Property != ColumnDefinition.WidthProperty) return;
            var w = Split.ColumnDefinitions[0].Width;
            if (w.IsAbsolute && w.Value > 0) { _listWidth = w.Value; UiState.SetLong(ListWidthKey, (long)w.Value); }
        };

        // A paste is the whole point of the box, so it appraises on its own, and so does a line
        // typed in: Enter puts its newline in as usual and the appraisal follows. Both events
        // fire before the text lands, so the appraisal is queued behind them; and the key is
        // watched even once the box has handled it, since handling it is what the box does.
        // A paste is the list, so once it is valued the box folds away; a line typed in leaves
        // the box open, since the typing is not done.
        InputBox.AddHandler(TextBox.PastingFromClipboardEvent, (_, _) => AppraiseSoon(foldAfter: true));
        InputBox.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Enter) AppraiseSoon(foldAfter: false); }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void AppraiseSoon(bool foldAfter) =>
        Dispatcher.UIThread.Post(() => _ = AppraiseAsync(foldAfter), DispatcherPriority.Background);

    /// <summary>Values the list; then, when asked and there is a result, folds the box away so the
    /// tables have the width — the result being what a paste or the button was for.</summary>
    private async Task AppraiseAsync(bool foldAfter)
    {
        if (_vm is null) return;
        await _vm.AppraiseAsync();
        if (foldAfter && _vm.HasResult) SetListHidden(true);
    }

    /// <summary>
    /// A paste anywhere in the window while the tool is showing is the next list, when the box is
    /// folded away or open and empty: copy a hangar in the client, come back, paste — no click on
    /// Edit list or in the box first. The window hears it rather than the view, since after a
    /// paste folds the box, or after a switch of tabs, the focus is seldom inside the tool. A box
    /// that has the focus keeps its own paste, and a list being edited is not written over.
    /// </summary>
    private async void OnKeyDownAnywhere(object? sender, KeyEventArgs e)
    {
        try
        {
            if (_vm is null || _top is not { } top) return;
            if (InputBox.IsVisible && !string.IsNullOrWhiteSpace(_vm.InputText)) return;
            if (top.PlatformSettings?.HotkeyConfiguration.Paste.Any(g => g.Matches(e)) != true) return;
            if (top.FocusManager?.GetFocusedElement() is TextBox) return;
            e.Handled = true;
            var text = top.Clipboard is { } clipboard ? await clipboard.GetTextAsync() : null;
            if (string.IsNullOrWhiteSpace(text)) return;
            // Whatever was last copied is not always a list: a paste that names no item leaves
            // the list as it is, rather than putting the clipboard's stray text in its place.
            if (!await _vm.NamesAnItemAsync(text))
            {
                _vm.ShowStatus("The clipboard names no item, so the list was left as it was. Edit list takes any text.");
                return;
            }
            _vm.InputText = text;
            await AppraiseAsync(foldAfter: true);
        }
        catch (Exception ex)
        {
            _vm?.ShowStatus(AppErrorLogger.Line("Paste", ex));
        }
    }

    /// <summary>The window, while the view is in it: it hears the paste (see OnKeyDownAnywhere).</summary>
    private TopLevel? _top;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as ItemValuationViewModel);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnKeyDownAnywhere, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _top?.RemoveHandler(KeyDownEvent, OnKeyDownAnywhere);
        _top = null;
        Attach(null);
    }

    /// <summary>
    /// Follows the view model. The main window builds a fresh view each time the tab is shown
    /// while the view model keeps its result, so the compare columns are rebuilt on arrival
    /// rather than left to the next change of stations, which is how they went missing after a
    /// switch of tabs. A view on its way out lets go of the events, so it is not kept alive by them.
    /// </summary>
    private void Attach(ItemValuationViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null)
        {
            _vm.CompareColumnsChanged -= RebuildCompareColumns;
            _vm.PropertyChanged       -= OnVmPropertyChanged;
        }
        _vm = vm;
        if (_vm is null) return;
        _vm.CopyToClipboard = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(text);
        };
        _vm.CompareColumnsChanged += RebuildCompareColumns;
        _vm.PropertyChanged       += OnVmPropertyChanged;
        RebuildCompareColumns();
        RefreshValuesBand();
        SetListHidden(_vm.HasResult);   // a view built afresh meets the list as its result left it
        _ = _vm.LoadAsync();
    }

    /// <summary>The lines in both bands follow every appraisal, not only a change of stations.</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if      (e.PropertyName == nameof(ItemValuationViewModel.CompareTotals)) RefreshCompareBand();
        else if (e.PropertyName == nameof(ItemValuationViewModel.ValueTotals))   RefreshValuesBand();
    }

    // ── The values grid's band ──────────────────────────────────────────────

    /// <summary>Each value's heading, with its total, standing and coverage beneath once there
    /// is a result; the market line carries the station's age as well.</summary>
    private void RefreshValuesBand()
    {
        var totals = _vm?.ValueTotals ?? [];
        ValuesBand.SetGroups(ValueGroups.Select((g, i) => g with { Detail = i < totals.Count ? BandLine(totals[i]) : null }));
    }

    private static StackPanel BandLine(ValueTotalVm t) => BandLine(t.TotalText, t.PctText, t.Color, t.CoverageText, t.AgeText, t.Stale);

    /// <summary>A heading's second line: the total, its standing, the coverage, and the age when there is one.</summary>
    private static StackPanel BandLine(string totalText, string pctText, IBrush color, string coverageText, string ageText, bool stale)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        line.Children.Add(Text(totalText, 12, FontWeight.SemiBold, color));
        if (pctText.Length > 0) line.Children.Add(Text(pctText, 10, FontWeight.Normal, color));
        line.Children.Add(Faint(coverageText, "TextFaintBrush"));
        if (ageText.Length > 0) line.Children.Add(Faint(ageText, stale ? "WarnBrush" : "TextFaintBrush"));
        return line;
    }

    // ── The compare grid's columns and band ─────────────────────────────────

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
            var index = i;
            var columns = new[]
            {
                Column("Unit",    $"Cells[{index}].UnitText",  $"Cells[{index}].FigureColor", 110, r => Priced(r, index)?.Unit  ?? -1,    $"Cells[{index}].UnitExact"),
                Column("Total",   $"Cells[{index}].TotalText", $"Cells[{index}].FigureColor", 120, r => Priced(r, index)?.Total ?? -1,    $"Cells[{index}].TotalExact"),
                Column("vs best", $"Cells[{index}].PctText",   $"Cells[{index}].Color",       70,  r => Priced(r, index)?.Pct   ?? -1000, null),
            };
            columns[0].CellStyleClasses.Add("gs");   // the line where one station's wash ends and the next begins
            foreach (var column in columns)
            {
                column.CellStyleClasses.Add(CellClasses[index % 3]);
                CompareGrid.Columns.Add(column);
            }
        }
        RefreshCompareBand();
    }

    /// <summary>Each station's name over its three columns, with its total, standing, coverage
    /// and age beneath, and a × to take it out of the comparison; the primary station stays.</summary>
    private void RefreshCompareBand()
    {
        if (_vm is null) return;
        var totals = _vm.CompareTotals;
        CompareBand.SetGroups(_vm.CompareColumns.Select((s, i) =>
            new ColumnGroup(2 + 3 * i, 3, s.Name, WashKeys[i % 3], i < totals.Count ? StationLine(totals[i]) : null)));
    }

    private Control StationLine(CompareTotalVm total)
    {
        var line = BandLine(total.TotalText, total.PctText, total.Color, total.CoverageText, total.AgeText, total.Stale);
        if (!total.IsPrimary)
        {
            var remove = new Button
            {
                Content = "×", FontSize = 12, Padding = new Thickness(4, 0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Bind(Button.ForegroundProperty, remove.GetResourceObservable("TextMutedBrush"));
            ToolTip.SetTip(remove, "Take this station out of the comparison");
            remove.Click += (_, _) => _vm?.RemoveCompare(total.Station);
            line.Children.Add(remove);
        }
        return line;
    }

    private static TextBlock Text(string text, double size, FontWeight weight, IBrush brush) =>
        new() { Text = text, FontSize = size, FontWeight = weight, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Faint(string text, string brushKey)
    {
        var block = new TextBlock { Text = text, FontSize = 9, VerticalAlignment = VerticalAlignment.Center };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(brushKey));
        return block;
    }

    /// <summary>The row's cell for a station when it has a price: what the sort keys read, so
    /// a cell without one sorts under every real value.</summary>
    private static CompareCellVm? Priced(CompareRowVm row, int index) =>
        index < row.Cells.Count && row.Cells[index].Has ? row.Cells[index] : null;

    /// <summary>A column bound by path and sorted by a key, its exact figure in a tip. A template
    /// column has no binding of its own for the grid to sort on or to copy, so it is handed a
    /// comparer and told it may sort, and told what Ctrl+C on a row copies: the text shown.</summary>
    private static DataGridTemplateColumn Column(string header, string textPath, string colorPath, double width,
                                                 Func<CompareRowVm, double> key, string? tipPath) =>
        new()
        {
            Header = header,
            Width  = new DataGridLength(width),
            ClipboardContentBinding = new Binding(textPath),
            CanUserSort        = true,
            CustomSortComparer = Comparer<object>.Create((a, b) =>
                (a is CompareRowVm ra ? key(ra) : double.MinValue).CompareTo(b is CompareRowVm rb ? key(rb) : double.MinValue)),
            CellTemplate = new FuncDataTemplate<CompareRowVm>((_, _) =>
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

    // ── The list pane ───────────────────────────────────────────────────────

    /// <summary>Hide list folds the box away; Edit list brings it back, the caret at its end.</summary>
    private void OnToggleListClick(object? sender, RoutedEventArgs e)
    {
        var open = InputBox.IsVisible;
        SetListHidden(open);
        if (!open) EditList();
    }

    private void EditList()
    {
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
    }

    private void SetListHidden(bool hidden)
    {
        Split.ColumnDefinitions[0].Width = hidden ? new GridLength(0) : new GridLength(_listWidth);
        ListSplitter.IsVisible   = !hidden;
        InputBox.IsVisible       = !hidden;
        ToggleListButton.Content = hidden ? "Edit list" : "Hide list";
        ToolTip.SetTip(ToggleListButton, hidden
            ? "Bring the list back to change it. While it is folded away, a paste anywhere on the tool values a new list."
            : "Fold the list away to give the tables the whole width. It folds on its own once valued.");
    }

    // ── Buttons and boxes ───────────────────────────────────────────────────

    private void OnAppraiseClick(object? sender, RoutedEventArgs e)   { _ = AppraiseAsync(foldAfter: true); }
    private void OnCopyClick(object? sender, RoutedEventArgs e)       { if (_vm is not null) _ = _vm.CopyAsync(); }
    private void OnClearClick(object? sender, RoutedEventArgs e)      { _vm?.Clear(); SetListHidden(false); EditList(); }
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

    private void OnOpenItem(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && (sender as Control)?.DataContext is ValueRowVm row) _vm.OpenItem(row.TypeId);
    }

    private void OnOpenCompareItem(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && (sender as Control)?.DataContext is CompareRowVm row) _vm.OpenItem(row.TypeId);
    }
}
