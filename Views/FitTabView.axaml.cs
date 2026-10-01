using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using EveConsole.Controls;
using EveConsole.Localization;
using EveConsole.Services.Fitting;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>One fit in the fitting tool: the ring, the module list and the fit's numbers.</summary>
public partial class FitTabView : UserControl
{
    public FitTabView()
    {
        InitializeComponent();
        Ring.SlotContextRequested += OnRingContext;
        AddHandler(DragDrop.DragOverEvent, OnItemDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropMarks());
        AddHandler(DragDrop.DropEvent, OnItemDrop);
    }

    private FitTabViewModel? Vm => DataContext as FitTabViewModel;

    /// <summary>Right-click on the ring: what can be done to that module, as in the game.</summary>
    private void OnRingContext(FittingSlot slot, Point at)
    {
        if (Vm is not { } vm || slot.Tag is not FittingModuleRowVm { IsEmpty: false } row) return;
        vm.SelectedModule = row;

        var items = new List<MenuItem>();
        MenuItem Item(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => act();
            return mi;
        }
        if (row.CanToggle)
        {
            items.Add(row.IsOff
                ? Item(FittingText.MenuPutOnline, () => vm.SetState(row, ModuleState.Online))
                : Item(FittingText.MenuPutOffline, () => vm.SetState(row, ModuleState.Offline)));
            if (row.CanActivate)
                items.Add(row.State >= ModuleState.Active
                    ? Item(FittingText.MenuDeactivate, () => vm.SetState(row, ModuleState.Online))
                    : Item(FittingText.MenuActivate, () => vm.SetState(row, ModuleState.Active)));
            if (row.CanOverheat)
                items.Add(row.IsHeated
                    ? Item(FittingText.MenuStopOverheating, () => vm.SetState(row, ModuleState.Active))
                    : Item(FittingText.MenuOverheat, () => vm.SetState(row, ModuleState.Overheated)));
        }
        if (row.Charge is { } charge)
            items.Add(Item(string.Format(FittingText.MenuUnload, charge.DisplayName), () => row.Charge = null));
        items.Add(Item(FittingText.Remove, () => vm.Remove(row)));

        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(Ring);
    }

    // ── Items dragged from the finder ──
    // Onto a slot — on the ring, or a row of the list under it, empty or filled — or anywhere
    // else on the fit, which adds the item as a double-click does. A fit's tab being dragged is
    // left to the side it is on.

    private Border? _markedRow;

    private static CatalogEntry? Dragged(DragEventArgs e) => e.Data.Get(FittingView.ItemFormat) as CatalogEntry;

    /// <summary>The slot under the pointer, and what marks it while dragging: the ring's slot, or
    /// the row's border. No slot: null.</summary>
    private (FittingModuleRowVm? Row, FittingSlot? RingSlot, Border? RowBorder) TargetOf(DragEventArgs e)
    {
        var onRing = e.GetPosition(Ring);
        if (new Rect(Ring.Bounds.Size).Contains(onRing))
            return Ring.SlotAt(onRing) is { Tag: FittingModuleRowVm row } slot ? (row, slot, null) : (null, null, null);
        var hit = this.InputHitTest(e.GetPosition(this)) as Visual;
        var border = hit?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.DataContext is FittingModuleRowVm);
        return border?.DataContext is FittingModuleRowVm r ? (r, null, border) : (null, null, null);
    }

    private void OnItemDragOver(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm || Dragged(e) is not { } entry) return;
        var (row, slot, border) = TargetOf(e);
        var ok = vm.HasShip && vm.CanDrop(entry, row);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        Ring.DropSlot = ok ? slot : null;
        if (border != _markedRow)
        {
            _markedRow?.Classes.Remove("droptarget");
            _markedRow = null;
        }
        if (ok && border is not null && _markedRow is null)
        {
            border.Classes.Add("droptarget");
            _markedRow = border;
        }
        e.Handled = true;
    }

    private void OnItemDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm || Dragged(e) is not { } entry) return;
        var (row, _, _) = TargetOf(e);
        ClearDropMarks();
        e.Handled = true;
        if (!vm.HasShip) { vm.Status = FittingText.StatusPickHullFirst; return; }
        vm.Tool.ActivePane = vm.Pane;
        _ = vm.DropAsync(entry, row);
    }

    private void ClearDropMarks()
    {
        Ring.DropSlot = null;
        _markedRow?.Classes.Remove("droptarget");
        _markedRow = null;
    }

    /// <summary>The fits that could boost this one are listed as the list opens: open tabs change.</summary>
    private void OnBoosterPickerOpened(object? sender, EventArgs e)
    {
        if (Vm is { } vm) _ = vm.RefreshBoosterChoicesAsync();
    }

    /// <summary>The module last clicked is where a charge picked in the finder is loaded.</summary>
    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm && (sender as Control)?.DataContext is FittingModuleRowVm row && !row.IsEmpty)
            vm.SelectedModule = row;
    }
}
