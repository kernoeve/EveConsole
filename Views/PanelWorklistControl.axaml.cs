using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>One character's worklist on a panel. See <see cref="PanelWorklistVm"/>.</summary>
public partial class PanelWorklistControl : UserControl
{
    public PanelWorklistControl() => InitializeComponent();

    /// <summary>False when the list drops down from a single line, which starts and stops the view model itself.</summary>
    public bool ManagesLifetime { get; init; } = true;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ManagesLifetime) (DataContext as PanelWorklistVm)?.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (ManagesLifetime) (DataContext as PanelWorklistVm)?.Dispose();
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WorklistRowVm row }) row.IsExpanded = !row.IsExpanded;
    }

    // The same links as the Worklist tool's rows; the tool they open goes where any tool opens.
    private void OnOpenRowItem(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as WorklistRowVm)?.OpenItem();
    private void OnOpenLocation(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as WorklistRowVm)?.OpenLocation();
    private void OnOpenChildItem(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as WorklistRowVm)?.OpenChildItem();

    /// <summary>A loop's stop or a trip ticked off as hauled — or a hauling service's trip as
    /// contracted, once its cargo is corrected to what went into the contract — as the Worklist
    /// tool does it, through the same worklist.</summary>
    private void OnMarkHauled(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: WorklistRowVm row } || DataContext is not PanelWorklistVm vm) return;
        if (vm.ContractedEditor(row) is { } editor) _ = ContractedAsync(vm, row, editor);
        else if (!row.IsCourierTick) _ = vm.MarkHauledAsync(row);
    }

    private async Task ContractedAsync(PanelWorklistVm vm, WorklistRowVm row, ContractedCargoVm editor)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var cargo = await new ContractedDialog { DataContext = editor }
            .ShowDialog<IReadOnlyList<(int TypeId, long Quantity)>?>(owner);
        if (cargo is { Count: > 0 }) await vm.MarkContractedAsync(row, cargo);
    }
}
