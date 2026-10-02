using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

/// <summary>
/// A window's tabs, on one side or two. Answers where things are for a dragged tab — which strip
/// or side a point is over, where a tab would land, where letting go opens a window of its own —
/// in its own coordinates, and shows the marks. The drag itself is <see cref="MainWindow"/>'s.
/// </summary>
public partial class WorkspaceView : UserControl
{
    /// <summary>
    /// How far down into its own side a tab must be dragged for letting go to open it in a window
    /// of its own. Dragging it out of every window does the same, but a maximised window has no
    /// outside to drag to.
    /// </summary>
    private const double DetachDepth = 90;

    public WorkspaceView()
    {
        InitializeComponent();
        // Two halves while the right side holds a tab, one side otherwise.
        this.GetObservable(DataContextProperty)
            .Select(dc => dc is TabWorkspace ws ? ws.WhenAnyValue(x => x.IsSplit) : Observable.Return(false))
            .Switch()
            .Subscribe(split => Sides.ColumnDefinitions[1].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0));
    }

    internal TabWorkspace? Workspace => DataContext as TabWorkspace;

    internal ToolPaneView SideOf(ToolPane pane) => pane.IsRight ? RightSide : LeftSide;

    private IEnumerable<ToolPaneView> ShownSides => Workspace?.IsSplit == true ? [LeftSide, RightSide] : [LeftSide];

    /// <summary>The tool on the side being worked in.</summary>
    internal Control ActiveContent => Workspace?.ActivePane.IsRight == true ? RightSide.ContentArea : LeftSide.ContentArea;

    /// <summary>Before a drag shows anything: the split zone covers the right half below the tabs.</summary>
    internal void PrepareDrag() => SplitDropZone.Margin = new Thickness(0, LeftSide.StripBounds(this).Height + 1, 0, 0);

    private Rect SplitZoneBounds() =>
        SplitDropZone.TranslatePoint(default, this) is { } p ? new Rect(p, SplitDropZone.Bounds.Size) : default;

    /// <summary>
    /// Where <paramref name="tab"/> dropped at <paramref name="at"/> goes: a strip (at a place along
    /// it), the split zone (the right side), or a side's tool (the end of its strip) — but not the
    /// side it came from, when it came from here. Null for nowhere.
    /// </summary>
    internal (ToolPane Pane, int? Index)? DropTarget(Point at, ToolTab tab)
    {
        if (Workspace is not { } ws) return null;
        foreach (var side in ShownSides)
            if (side.Pane is { } pane && side.StripBounds(this).Contains(at))
                return (pane, side.InsertIndexAt(at, this));
        if (ws.ShowSplitDropZone && SplitZoneBounds().Contains(at)) return (ws.RightPane, null);
        foreach (var side in ShownSides)
            if (side.Pane is { } pane && pane != ws.PaneOf(tab) && side.ContentBounds(this).Contains(at))
                return (pane, null);
        return null;
    }

    /// <summary>
    /// Where letting go of <paramref name="tab"/>, from here, opens it in a window of its own: well
    /// down into its own side, clear of the split zone. Only acted on when the tab is let go, so a
    /// drag passing through on its way elsewhere does not trip it.
    /// </summary>
    internal Rect? DetachArea(ToolTab tab)
    {
        if (Workspace is not { } ws || ws.PaneOf(tab) is not { } from) return null;
        var content = SideOf(from).ContentBounds(this);
        var width   = ws.ShowSplitDropZone ? content.Width / 2 : content.Width;
        return new Rect(content.Left, content.Top + DetachDepth, width, Math.Max(0, content.Height - DetachDepth));
    }

    /// <summary>Marks where a tab would land here, and hides every other mark.</summary>
    internal void ShowDropTarget((ToolPane Pane, int? Index)? target)
    {
        foreach (var side in new[] { LeftSide, RightSide })
        {
            // The split zone is its own mark.
            if (target is { } t && side.Pane == t.Pane && !(Workspace?.ShowSplitDropZone == true && t.Pane.IsRight))
                side.ShowDropMark(t.Index ?? t.Pane.Tabs.Count);
            else side.HideDropMark();
        }
    }

    internal void ShowDetachHint(Rect? area)
    {
        if (area is { } r)
        {
            DetachHint.Margin = new Thickness(r.Left, r.Top, 0, 0);
            DetachHint.Width  = r.Width;
            DetachHint.Height = r.Height;
        }
        DetachHint.IsVisible = area is not null;
    }

    internal void ClearMarks()
    {
        LeftSide.HideDropMark();
        RightSide.HideDropMark();
        DetachHint.IsVisible = false;
    }
}
