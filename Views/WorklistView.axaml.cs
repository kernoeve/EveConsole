using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class WorklistView : ReactiveUserControl<WorklistViewModel>
{
    public WorklistView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens and closes the manifest under a haul row.
    ///
    /// <para>Done here rather than by binding <see cref="DataGridRow.AreDetailsVisible"/> in a
    /// style, because the DataGrid writes that property itself as it loads each row, and a local
    /// write outranks a style setter — the binding would be overwritten on scroll. Setting it on
    /// the row goes through the grid's own bookkeeping, which is keyed by item index and so
    /// survives the row being recycled to a different position.</para>
    ///
    /// <para>The grid-wide mode stays Collapsed for the same reason the earlier attempt failed
    /// visibly: <c>Visible</c> attaches a details presenter to every row, which skews the row
    /// height estimate and paints blank bands into the middle of the list while scrolling.</para>
    /// </summary>
    private void OnManifestToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        if (control.FindAncestorOfType<DataGridRow>() is not { } row) return;

        row.AreDetailsVisible = !row.AreDetailsVisible;

        // The glyph lives on the item so it stays correct when the row is recycled.
        if (row.DataContext is WorklistRowVm vm) vm.IsExpanded = row.AreDetailsVisible;
    }
}
