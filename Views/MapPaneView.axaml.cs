using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// One side of the map tool: its tab strip, and dragging tabs along it or across to the other
/// side. The same mechanics as the Fitting tool's panes.
/// </summary>
public partial class MapPaneView : UserControl
{
    /// <summary>The drag payload's format. In-process only: the payload is the tab's view model.</summary>
    public static readonly DataFormat<string> TabFormat = InProcessDrag.Format("EveConsole.MapTab");

    private MapTabViewModel? _pressed;
    private Point            _pressedAt;

    public MapPaneView()
    {
        InitializeComponent();

        // Whichever side is clicked becomes the active one, so new tabs open where the user is
        // working. Tunnelled and handled-too: the map and the page handle their own clicks.
        AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (Vm is { Tabs.Count: > 0 } vm) vm.Tool.ActivePane = vm;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropMark.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MapPaneViewModel? Vm => DataContext as MapPaneViewModel;

    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MapTabViewModel tab
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        tab.Tool.SelectedTab = tab;
        _pressed   = tab;
        _pressedAt = e.GetPosition(this);
    }

    private void OnTabReleased(object? sender, PointerReleasedEventArgs e) => _pressed = null;

    private async void OnTabMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } tab) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressed = null; return; }

        // A few pixels of wobble while clicking is not a drag.
        var d = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;

        _pressed = null;
        tab.Tool.IsDraggingTab = true;
        try
        {
            await InProcessDrag.RunAsync(e, TabFormat, tab, DragDropEffects.Move);
        }
        finally
        {
            tab.Tool.IsDraggingTab = false;
            DropMark.IsVisible     = false;
        }
    }

    private static MapTabViewModel? Dragged(DragEventArgs e) => InProcessDrag.Get<MapTabViewModel>(e, TabFormat);

    private bool OverStrip(DragEventArgs e) =>
        e.GetPosition(Strip) is var p && p.Y >= 0 && p.Y <= Strip.Bounds.Height;

    /// <summary>Where in the strip a tab dropped at <paramref name="x"/> would go, and the x of
    /// the gap it would fill.</summary>
    private (int Index, double X) InsertAt(double x)
    {
        var count = Vm?.Tabs.Count ?? 0;
        var right = 0.0;
        for (var i = 0; i < count; i++)
        {
            if (TabList.ContainerFromIndex(i) is not { } c || c.TranslatePoint(default, Strip) is not { } at) continue;
            if (x < at.X + c.Bounds.Width / 2) return (i, at.X);
            right = at.X + c.Bounds.Width;
        }
        return (count, right);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (Dragged(e) is null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        if (OverStrip(e))
        {
            var (_, x) = InsertAt(e.GetPosition(Strip).X);
            DropMark.Margin    = new Thickness(Math.Max(0, x - 1 - Strip.Padding.Left), 0, 0, 0);
            DropMark.IsVisible = true;
        }
        else DropMark.IsVisible = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropMark.IsVisible = false;
        if (Dragged(e) is not { } tab || Vm is not { } pane) return;

        // On the strip: at that position. Anywhere else on the other side: at its end. Dropping
        // a tab on its own side's content does nothing — it is already there.
        if (OverStrip(e)) pane.Tool.MoveTab(tab, pane, InsertAt(e.GetPosition(Strip).X).Index);
        else if (tab.Pane != pane) pane.Tool.MoveTab(tab, pane);
        e.Handled = true;
    }
}
