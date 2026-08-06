using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class JumpPlannerView : UserControl
{
    /// <summary>The waypoint currently being dragged, if any.</summary>
    private WaypointVm? _dragging;

    public JumpPlannerView() => InitializeComponent();

    private async void OnWaypointPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not WaypointVm waypoint) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;

        // The remove button sits inside the same row; pressing it must not start a drag.
        if (e.Source is Control source && source.FindAncestorOfType<Button>() is not null) return;

        _dragging = waypoint;
        try
        {
            var data = new DataObject();
            data.Set(DataFormats.Text, waypoint.Name);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        finally { _dragging = null; }
    }

    private void OnWaypointDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _dragging is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnWaypointDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not JumpPlannerViewModel vm) return;
        if (sender is not Control control || control.DataContext is not WaypointVm target) return;
        if (_dragging is null || ReferenceEquals(_dragging, target)) return;

        vm.MoveWaypoint(_dragging, target);
        e.Handled = true;
    }
}
