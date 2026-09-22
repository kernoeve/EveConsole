using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class AgentGridView : ReactiveUserControl<AgentGridViewModel>
{
    public AgentGridView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
    }

    private AgentGridViewModel? _bound;

    /// <summary>
    /// Builds the columns and hands the view model the two things only a view can do — reach the
    /// clipboard and open a save dialog.
    /// </summary>
    private void Bind()
    {
        if (DataContext is not AgentGridViewModel vm || ReferenceEquals(vm, _bound)) return;
        _bound = vm;

        Grid.Columns.Clear();
        for (int i = 0; i < vm.Columns.Length; i++)
        {
            Grid.Columns.Add(new DataGridTextColumn
            {
                Header = vm.Columns[i],
                // ⚠️ Indexer binding against AgentGridRow. The row has no properties to bind to —
                // its shape is whatever the agent asked for — so the column index IS the path.
                Binding = new Binding($"[{i}]"),
                Width   = new DataGridLength(1, DataGridLengthUnitType.Auto),
            });
        }

        vm.CopyToClipboard = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(text);
        };

        vm.SaveTextFile = SaveTextFileAsync;
    }

    private async Task<string?> SaveTextFileAsync(string suggestedName, string contents)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title                  = "Save grid",
            SuggestedFileName      = suggestedName,
            DefaultExtension       = Path.GetExtension(suggestedName).TrimStart('.'),
            ShowOverwritePrompt    = true,
            FileTypeChoices =
            [
                new FilePickerFileType("CSV")      { Patterns = ["*.csv"] },
                new FilePickerFileType("Markdown") { Patterns = ["*.md"]  },
                new FilePickerFileType("All files"){ Patterns = ["*"]     },
            ],
        });
        if (file is null) return null;   // cancelled

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(contents);
        return file.Name;
    }

    private async void OnSaveCsvClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AgentGridViewModel vm) await vm.SaveCsvAsync();
    }

    /// <summary>
    /// Mirrors the grid's selection into the view model so the copy commands can read it without
    /// reaching into the control.
    /// </summary>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not AgentGridViewModel vm) return;
        vm.Selected.Clear();
        foreach (var item in Grid.SelectedItems)
            if (item is AgentGridRow row) vm.Selected.Add(row);
    }

    /// <summary>
    /// ⚠️ Ctrl+C is handled here rather than by the DataGrid's own ClipboardCopyMode, which is
    /// switched off. Its built-in copy emits comma-separated text, and whether a spreadsheet
    /// splits that into columns on paste depends on the machine's list separator — on a European
    /// install the whole row lands in one cell. Tabs always split.
    /// </summary>
    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (DataContext is not AgentGridViewModel vm) return;
        vm.CopyCommand.Execute().Subscribe();
        e.Handled = true;
    }
}
