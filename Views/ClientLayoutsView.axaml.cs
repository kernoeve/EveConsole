using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ClientLayoutsView : UserControl
{
    public ClientLayoutsView()
    {
        InitializeComponent();
        // Tunnelled and taken even when handled: the tree's own items handle presses to select.
        OutlineTree.AddHandler(PointerPressedEvent, Outline_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        OutlineTree.AddHandler(PointerMovedEvent, Outline_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        OutlineTree.AddHandler(PointerReleasedEvent, Outline_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        OutlineTree.AddHandler(PointerCaptureLostEvent, (_, _) => EndOutlineDrag(), RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Undo and redo on the platform's own keys: Ctrl+Z and Ctrl+Y (or Ctrl+Shift+Z) here, ⌘Z and
    /// ⇧⌘Z on a Mac.
    ///
    /// <para>⚠️ Not while typing in a box. A text box takes its own undo first, so Ctrl+Z there
    /// takes back letters, as anyone typing expects, and the layout's undo is a click on the
    /// drawing away: the drawing takes the keyboard when clicked.</para>
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Vm is not { } vm) return;
        var keys = TopLevel.GetTopLevel(this)?.PlatformSettings?.HotkeyConfiguration;
        if (keys is null) return;
        // Delete removes what's selected, as its menu's Remove does; on a Mac, so does ⌫.
        if (e.KeyModifiers == KeyModifiers.None && (e.Key == Key.Delete || (OperatingSystem.IsMacOS() && e.Key == Key.Back)))
        {
            vm.DeleteSelected();
            e.Handled = true;
        }
        else if (keys.Undo.Any(g => g.Matches(e))) { vm.Undo(); e.Handled = true; }
        else if (keys.Redo.Any(g => g.Matches(e)) || (e.Key == Key.Z && e.KeyModifiers == (keys.CommandModifiers | KeyModifiers.Shift)))
        {
            vm.Redo();
            e.Handled = true;
        }
    }

    private ClientLayoutsViewModel? Vm => DataContext as ClientLayoutsViewModel;

    private void DeviceName_LostFocus(object? sender, RoutedEventArgs e) => Vm?.SaveDeviceName();

    private void LayoutName_LostFocus(object? sender, RoutedEventArgs e) => Vm?.SaveLayoutName();

    // Both only while someone is typing here: these also fire when the values are loaded.
    private void DeviceName_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { IsFocused: true }) Vm?.TypedDeviceName();
    }

    private void ProfileName_LostFocus(object? sender, RoutedEventArgs e) => Vm?.SaveProfileName();

    private void ProfileName_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { IsFocused: true }) Vm?.TypedProfileName();
    }

    private void LayoutName_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { IsFocused: true }) Vm?.TypedLayoutName();
    }

    private void OtherDeviceName_LostFocus(object? sender, RoutedEventArgs e) =>
        ((sender as Control)?.DataContext as OtherDeviceVm)?.SaveName();

    // ── Outline: drag a line onto a panel's line to move it there ─────────────

    private OutlineNodeVm? _outlineDragged;
    private Point _outlineFrom;
    private bool _outlineMoving;
    private OutlineNodeVm? _outlineOn;

    /// <summary>The line under the pointer: the innermost item of the tree there.</summary>
    private OutlineNodeVm? OutlineLineAt(PointerEventArgs e)
    {
        for (var v = OutlineTree.InputHitTest(e.GetPosition(OutlineTree)) as Visual; v != null && v != OutlineTree; v = v.GetVisualParent())
            if (v is TreeViewItem { DataContext: OutlineNodeVm node }) return node;
        return null;
    }

    private void Outline_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(OutlineTree).Properties.IsLeftButtonPressed) return;
        _outlineDragged = OutlineLineAt(e) is { Id: not null } node ? node : null;
        _outlineFrom = e.GetPosition(OutlineTree);
    }

    private void Outline_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_outlineDragged is not { Id: { } id } || Vm is not { } vm || !e.GetCurrentPoint(OutlineTree).Properties.IsLeftButtonPressed) return;
        var at = e.GetPosition(OutlineTree);
        if (!_outlineMoving)
        {
            if (Math.Abs(at.X - _outlineFrom.X) + Math.Abs(at.Y - _outlineFrom.Y) < 6) return;
            _outlineMoving = true;
            e.Pointer.Capture(OutlineTree);
        }
        var over = OutlineLineAt(e);
        var on = over != null && vm.CanMoveTo(id, over.Id) ? over : null;
        if (!ReferenceEquals(on, _outlineOn))
        {
            if (_outlineOn != null) _outlineOn.IsDropTarget = false;
            _outlineOn = on;
            if (on != null) on.IsDropTarget = true;
        }
        OutlineTree.Cursor = new Cursor(on == null ? StandardCursorType.No : StandardCursorType.DragMove);
        e.Handled = true;
    }

    private void Outline_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_outlineMoving) { _outlineDragged = null; return; }
        var (dragged, on) = (_outlineDragged, _outlineOn);
        EndOutlineDrag();
        e.Pointer.Capture(null);
        if (dragged?.Id is { } id && on != null) Vm?.MoveTo(id, on.Id);
        e.Handled = true;
    }

    private void EndOutlineDrag()
    {
        if (_outlineOn != null) _outlineOn.IsDropTarget = false;
        _outlineOn = null;
        _outlineDragged = null;
        _outlineMoving = false;
        OutlineTree.Cursor = Cursor.Default;
    }

    // ── Dock order: drag a row up or down, or click one to select it ──────────

    private int _dragRow = -1;
    private bool _dragged;

    /// <summary>
    /// ⚠️ The list takes the pointer, not the row. A row moved in the list is a new row, and one that
    /// held the pointer would lose it the moment it moved, ending the drag after a single step.
    /// </summary>
    private void DockOrder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null || !e.GetCurrentPoint(DockOrderList).Properties.IsLeftButtonPressed) return;
        _dragRow = RowAt(e);
        if (_dragRow < 0) return;
        _dragged = false;
        e.Pointer.Capture(DockOrderList);
        e.Handled = true;
    }

    private void DockOrder_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragRow < 0 || Vm is not { } vm) return;
        var over = RowAt(e);
        if (over < 0 || over == _dragRow) return;
        vm.MoveDockOrder(_dragRow, over);
        _dragRow = over;
        _dragged = true;
    }

    private void DockOrder_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var row = _dragRow;
        _dragRow = -1;
        e.Pointer.Capture(null);
        // A click without a drag selects the row's panel or control.
        if (!_dragged && row >= 0 && Vm is { } vm && row < vm.DockOrder.Count) vm.SelectedId = vm.DockOrder[row].Panel.Id;
        else if (_dragged) Vm?.DockOrderDropped();
        _dragged = false;
    }

    private void DockOrder_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _dragRow = -1;
        if (_dragged) Vm?.DockOrderDropped();   // where it was let go of, as far as it got
        _dragged = false;
    }

    /// <summary>The row under the pointer, by its place in the list, or -1. Past the ends counts as the end.</summary>
    private int RowAt(PointerEventArgs e)
    {
        var count = DockOrderList.ItemCount;
        for (var i = 0; i < count; i++)
        {
            if (DockOrderList.ContainerFromIndex(i) is not Control row) continue;
            var y = e.GetPosition(row).Y;
            if (y < 0 && i == 0) return 0;
            if (y >= 0 && y < row.Bounds.Height + 2) return i;   // the gap below a row is that row's
            if (i == count - 1 && y >= row.Bounds.Height) return i;
        }
        return -1;
    }
}
