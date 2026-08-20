using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class StructureBrowserView : UserControl
{
    public StructureBrowserView() => InitializeComponent();

    /// <summary>Saves the edited fields. A plain Click handler rather than a command because the
    /// save is a single fire-and-forget on the view model with no parameters to bind.</summary>
    private void OnSaveDetail(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StructureBrowserViewModel vm) _ = vm.SaveDetailAsync();
    }

    // ── Row links ─────────────────────────────────────────────────────────────
    //
    // Every row carries its own navigation, so these reach it through the button's DataContext.
    private static T? Row<T>(object? sender) where T : class
        => (sender as Control)?.DataContext as T;

    private void OnOpenRowType(object? sender, RoutedEventArgs e)
        => Row<StructureRow>(sender)?.OpenType();
    private void OnOpenRowSystem(object? sender, RoutedEventArgs e)
        => Row<StructureRow>(sender)?.OpenSystem();
    private void OnOpenRowConstellation(object? sender, RoutedEventArgs e)
        => Row<StructureRow>(sender)?.OpenConstellation();
    private void OnOpenRowRegion(object? sender, RoutedEventArgs e)
        => Row<StructureRow>(sender)?.OpenRegion();
    private void OnOpenRowCorp(object? sender, RoutedEventArgs e)
        => Row<StructureRow>(sender)?.OpenCorp();
    private void OnOpenRowAlliance(object? sender, RoutedEventArgs e)
        => Row<StructureRow>(sender)?.OpenAlliance();

    private void OnOpenFittingItem(object? sender, RoutedEventArgs e)
        => Row<FittingRow>(sender)?.OpenItem();
    private void OnOpenAssetItem(object? sender, RoutedEventArgs e)
        => Row<StructureAssetRow>(sender)?.OpenItem();
    private void OnOpenAssetOwner(object? sender, RoutedEventArgs e)
        => Row<StructureAssetRow>(sender)?.OpenOwner();
    private void OnOpenJobProduct(object? sender, RoutedEventArgs e)
        => Row<StructureJobRow>(sender)?.OpenProduct();

    // ── Selected structure ────────────────────────────────────────────────────
    //
    // The header line and the hull render sit outside any row template, so these read the
    // selection off the view model rather than a DataContext.
    private StructureRow? Sel => (DataContext as StructureBrowserViewModel)?.Selected;

    private void OnOpenSelectedSystem(object? sender, RoutedEventArgs e) => Sel?.OpenSystem();
    private void OnOpenSelectedRegion(object? sender, RoutedEventArgs e) => Sel?.OpenRegion();
    private void OnOpenSelectedType(object? sender, RoutedEventArgs e)   => Sel?.OpenType();
}
