using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// One side of the main window: a tab strip and the tool showing. The dragging itself is the
/// window's (<see cref="MainWindow"/>), because a tab can go to the other side or out to a window
/// of its own; this reports presses and answers where things are.
/// </summary>
public partial class ToolPaneView : UserControl
{
    public ToolPaneView()
    {
        InitializeComponent();
        // A click anywhere on a side makes it the one being worked in.
        AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (DataContext is ToolPane { Tabs.Count: > 0 } pane && Owner?.DataContext is MainWindowViewModel vm)
                vm.ActivePane = pane;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    internal ToolPane? Pane => DataContext as ToolPane;

    /// <summary>The tool showing on this side — what a screenshot of the tab captures.</summary>
    internal Control ContentArea => PaneContent;

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)  => Owner?.OnTabPointerPressed(sender, e);
    private void OnTabPointerMoved(object? sender, PointerEventArgs e)           => Owner?.OnTabPointerMoved(sender, e);
    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e) => Owner?.OnTabPointerReleased(sender, e);

    /// <summary><paramref name="control"/>'s bounds in <paramref name="relativeTo"/>'s coordinates.</summary>
    private static Rect BoundsIn(Control control, Visual relativeTo) =>
        control.TranslatePoint(default, relativeTo) is { } at ? new Rect(at, control.Bounds.Size) : default;

    internal Rect StripBounds(Visual relativeTo)   => BoundsIn(Strip, relativeTo);
    internal Rect ContentBounds(Visual relativeTo) => BoundsIn(PaneContent, relativeTo);

    /// <summary>
    /// Where along this side's tabs one dropped at <paramref name="at"/> (in <paramref name="relativeTo"/>'s
    /// coordinates) would go. The tabs wrap onto more rows, so a tab is passed once the point is
    /// below its row, or level with it and past its middle.
    /// </summary>
    internal int InsertIndexAt(Point at, Visual relativeTo)
    {
        var count = Pane?.Tabs.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            if (TabStrip.ContainerFromIndex(i) is not { } c) continue;
            var r = BoundsIn(c, relativeTo);
            if (at.Y < r.Top || (at.Y < r.Bottom && at.X < r.Center.X)) return i;
        }
        return count;
    }

    /// <summary>Marks where a dragged tab would land: before tab <paramref name="index"/>, or after the last.</summary>
    internal void ShowDropMark(int index)
    {
        var count = Pane?.Tabs.Count ?? 0;
        var anchor = index < count ? TabStrip.ContainerFromIndex(index) : count > 0 ? TabStrip.ContainerFromIndex(count - 1) : null;
        var r      = anchor is null ? default : BoundsIn(anchor, Strip);
        if (r == default)
        {
            Canvas.SetLeft(DropMark, 2); Canvas.SetTop(DropMark, 0); DropMark.Height = Math.Max(20, Strip.Bounds.Height);
        }
        else
        {
            Canvas.SetLeft(DropMark, Math.Max(0, (index < count ? r.Left : r.Right) - 1));
            Canvas.SetTop(DropMark, r.Top);
            DropMark.Height = r.Height;
        }
        DropMark.IsVisible = true;
    }

    internal void HideDropMark() => DropMark.IsVisible = false;
}
