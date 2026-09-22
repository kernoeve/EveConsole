using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

public partial class EsiExplorerView : ReactiveUserControl<EsiExplorerViewModel>
{
    private record FilterRowUi(ComboBox ColPicker, ComboBox OpPicker, TextBox ValBox, StackPanel Row);
    private readonly List<FilterRowUi> _filterControls = [];


    private ScrollViewer? _gridScroll;
    private bool          _scrollHooked;
    private bool          _handlersAdded;
    private bool          _initialized;

    public EsiExplorerView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is null) return;

        TableList.ItemsSource = ViewModel.AllTables;
        EsiGrid.ItemsSource   = ViewModel.Rows;

        if (!_handlersAdded)
        {
            TableList.SelectionChanged += OnTableSelected;
            _handlersAdded = true;
        }

        ViewModel.WhenAnyValue(vm => vm.Columns)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(RegenerateColumns);

        if (!_initialized)
        {
            AddFilterRow(withRemove: false);
            _initialized = true;
        }
    }

    private void RegenerateColumns(IReadOnlyList<string> columns)
    {
        EsiGrid.ItemsSource = null;
        EsiGrid.Columns.Clear();

        if (columns.Count > 0)
        {
            foreach (var fc in _filterControls)
                fc.ColPicker.ItemsSource = columns;
        }

        foreach (var col in columns)
        {
            var captured = col;
            EsiGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header     = captured,
                IsReadOnly = true,
                ClipboardContentBinding = GridRowCopy.Binding(captured),
                CellTemplate = new FuncDataTemplate<GridRow>(
                    (row, _) => new TextBlock
                    {
                        Text              = row?[captured] ?? "",
                        Padding           = new Thickness(6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize          = 11,
                    }),
            });
        }

        EsiGrid.ItemsSource = ViewModel?.Rows;

        if (!_scrollHooked)
            EsiGrid.TemplateApplied += OnGridTemplateApplied;
    }

    private void OnGridTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        EsiGrid.TemplateApplied -= OnGridTemplateApplied;
        _gridScroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        if (_gridScroll is not null)
        {
            _gridScroll.ScrollChanged += OnGridScrollChanged;
            _scrollHooked = true;
        }
    }

    private void OnGridScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.HasMore || _gridScroll is null) return;
        var nearBottom = _gridScroll.Offset.Y >=
                         _gridScroll.Extent.Height - _gridScroll.Viewport.Height - 300;
        if (nearBottom)
            _ = ViewModel.LoadMoreAsync();
    }

    private void OnTableSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || TableList.SelectedItem is not TableEntry entry) return;
        ResetFilterRows();
        ViewModel.SelectedTable = entry;
    }

    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (ViewModel is null || e.Column.Header is not string col) return;
        e.Handled = true;
        _ = ViewModel.SortAsync(col);
    }

    private FilterRowUi CreateFilterRow(bool withRemove)
    {
        var colPicker = new ComboBox
        {
            Width            = 155,
            FontSize         = 11,
            ItemsSource      = ViewModel?.Columns,
            PlaceholderText  = "column…",
            MaxDropDownHeight = 300,
        };

        var opPicker = new ComboBox
        {
            Width            = 155,
            FontSize         = 11,
            ItemsSource      = EsiExplorerViewModel.Operators,
            SelectedIndex    = 0,
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
                Content          = "×",
                FontSize         = 13,
                Padding          = new Thickness(8, 2),
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

    /// <summary>
    /// The whole table to a CSV file the user names. A file half written — the export cancelled
    /// or failed — is not left behind.
    /// </summary>
    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel is null) return;
            if (ViewModel.IsExporting) { ViewModel.CancelExport(); return; }
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;

            var name = ViewModel.SelectedTable?.DisplayName ?? "table";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title             = "Export CSV",
                SuggestedFileName = $"{name}.csv",
                DefaultExtension  = "csv",
                FileTypeChoices   = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
            });
            if (file is null) return;

            var whole = false;
            try
            {
                await using (var stream = await file.OpenWriteAsync())
                    whole = await ViewModel.ExportCsvAsync(stream, file.Name);
            }
            finally
            {
                if (!whole) { try { await file.DeleteAsync(); } catch { /* the status line already says what happened */ } }
            }
        }
        catch (Exception ex)
        {
            ViewModel?.ShowStatus($"Export failed: {ex.Message}");
        }
    }

    private void OnLoadMoreClick(object? sender, RoutedEventArgs e)
    {
        _ = ViewModel?.LoadMoreAsync();
    }
}
