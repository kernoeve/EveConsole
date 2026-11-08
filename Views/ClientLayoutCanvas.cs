using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using EveConsole.Localization;
using EveConsole.Services.ClientLayouts;

namespace EveConsole.Views;

/// <summary>
/// One layout drawn on a chosen monitor's size. Right click an area or panel for what can be done
/// to it, and drag the lines between things to resize them. The changes themselves are in
/// <see cref="ClientLayoutEditing"/>; this only turns pointers into them.
/// </summary>
public sealed class ClientLayoutCanvas : Control
{
    /// <summary>How close to a line, in on-screen units, still counts as grabbing it.</summary>
    private const double GrabDistance = 5;

    public static readonly StyledProperty<LayoutArea?> RootProperty =
        AvaloniaProperty.Register<ClientLayoutCanvas, LayoutArea?>(nameof(Root));

    public static readonly StyledProperty<LayoutRect> SpaceProperty =
        AvaloniaProperty.Register<ClientLayoutCanvas, LayoutRect>(nameof(Space), new LayoutRect(0, 0, 3840, 2112));

    /// <summary>Bumped by the view model when the tree changed underneath us.</summary>
    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<ClientLayoutCanvas, int>(nameof(Revision));

    public static readonly StyledProperty<string?> SelectedIdProperty =
        AvaloniaProperty.Register<ClientLayoutCanvas, string?>(nameof(SelectedId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Told when a change is finished and should be saved.</summary>
    public static readonly StyledProperty<ICommandLike?> EditedProperty =
        AvaloniaProperty.Register<ClientLayoutCanvas, ICommandLike?>(nameof(Edited));

    private readonly ClientLayoutPainter _painter;
    private Grab? _grab;

    /// <summary>How far, in on-screen units, the pointer moves with the button down before it's a drag to move what was pressed.</summary>
    private const double DragStart = 6;

    /// <summary>What was pressed, and where: a drag that goes far enough moves it into another panel.</summary>
    private string? _pressedId;
    private Point _pressedAt;
    private bool _moving;

    /// <summary>Where it would go if let go now: a panel's id, the monitor, or nowhere.</summary>
    private string? _dropOn;

    static ClientLayoutCanvas() =>
        AffectsRender<ClientLayoutCanvas>(RootProperty, SpaceProperty, RevisionProperty, SelectedIdProperty);

    public ClientLayoutCanvas()
    {
        _painter = new ClientLayoutPainter(this);
        ClipToBounds = true;
        Focusable = true;   // for the editor's Ctrl+Z, once clicked
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public LayoutArea? Root { get => GetValue(RootProperty); set => SetValue(RootProperty, value); }
    public LayoutRect Space { get => GetValue(SpaceProperty); set => SetValue(SpaceProperty, value); }
    public int Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public string? SelectedId { get => GetValue(SelectedIdProperty); set => SetValue(SelectedIdProperty, value); }
    public ICommandLike? Edited { get => GetValue(EditedProperty); set => SetValue(EditedProperty, value); }

    private ClientLayoutPainter.View CurrentView() => ClientLayoutPainter.View.Fit(Space, Bounds.Size, 2);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var root = Root;
        if (root == null) return;
        var view = CurrentView();
        context.DrawRectangle(_painter.Brush("SurfacePanelBrush"), null, view.ToControl(Space));
        var result = ClientLayoutCalculator.Calculate(root, Space);
        _painter.Draw(context, result, view, SelectedId);

        // Where a drag would put it, outlined over everything.
        if (_moving && _dropOn is { } on && (on == ClientLayoutEditing.MonitorTarget ? Space : RectOf(result, on)) is { } target)
            context.DrawRectangle(null, new Pen(_painter.Brush("InfoBrush"), 3), view.ToControl(target).Deflate(1.5));
    }

    private static LayoutRect? RectOf(ClientLayoutResult result, string id)
    {
        foreach (var panel in result.Panels)
        {
            if (panel.Id == id) return panel.Rect;
            foreach (var part in panel.Parts ?? [])
                if (part.Id == id) return part.Rect;
        }
        return null;
    }

    /// <summary>
    /// The innermost panel under a point that what's being dragged can go into, or the monitor when
    /// it's over no panel that will take it and it's a panel itself. Null for nowhere.
    /// </summary>
    private string? DropTargetAt(Point at, string moving)
    {
        if (Root is not { } root) return null;
        var (x, y) = CurrentView().ToScreen(at);
        var result = ClientLayoutCalculator.Calculate(root, Space);
        foreach (var panel in result.Panels)
        {
            if (panel.Parts == null) continue;
            for (var i = panel.Parts.Count - 1; i >= 0; i--)
            {
                var part = panel.Parts[i];
                if (part.Kind == PlacedPartKind.Panel && Inside(part.Rect, x, y) && ClientLayoutEditing.CanMove(root, moving, part.Id)) return part.Id;
            }
        }
        foreach (var panel in result.Panels)
            if (Inside(panel.Rect, x, y) && ClientLayoutEditing.CanMove(root, moving, panel.Id)) return panel.Id;
        return ClientLayoutEditing.CanMove(root, moving, ClientLayoutEditing.MonitorTarget) ? ClientLayoutEditing.MonitorTarget : null;
    }

    // ---- What's under the pointer ----

    /// <summary>A line that can be dragged: a docked panel's inner edge, or a line in a grid.</summary>
    private abstract record Grab(bool Vertical);
    private sealed record DockGrab(DockedPanel Panel, LayoutRect At, bool Vertical) : Grab(Vertical);
    /// <summary>
    /// A line in a grid. <see cref="GridId"/> is what selects the grid: grabbing a line selects it.
    /// <see cref="Gap"/> is the space between its cells: shares are worked out on the length without the gaps.
    /// </summary>
    private sealed record GridGrab(string GridId, List<double> Shares, int Line, int Start, int Length, int Gap, bool Vertical) : Grab(Vertical);

    private Grab? FindGrab(Point at)
    {
        var root = Root;
        if (root == null) return null;
        var view = CurrentView();
        var result = ClientLayoutCalculator.Calculate(root, Space);
        var reach = GrabDistance / view.Scale;
        var (x, y) = view.ToScreen(at);

        // Innermost first, so the line of something inside a panel beats the panel's own.
        foreach (var (id, r) in InnermostFirst(result))
        {
            if (ClientLayoutEditing.FindDocked(root, id) is not { } found) continue;

            // ⚠️ A third of its thickness at most. A thin bar in a small preview is only a few
            // units tall, and its whole height would otherwise count as grabbing its edge, leaving
            // nothing to click to select it or what's in it.
            var thickness = found.Panel.Edge is DockEdge.Top or DockEdge.Bottom ? r.Height : r.Width;
            var edgeReach = Math.Min(reach, thickness / 3.0);
            var (near, vertical, along) = found.Panel.Edge switch
            {
                DockEdge.Top => (Math.Abs(y - r.Bottom) <= edgeReach, false, x >= r.X && x < r.Right),
                DockEdge.Bottom => (Math.Abs(y - r.Y) <= edgeReach, false, x >= r.X && x < r.Right),
                DockEdge.Left => (Math.Abs(x - r.Right) <= edgeReach, true, y >= r.Y && y < r.Bottom),
                _ => (Math.Abs(x - r.X) <= edgeReach, true, y >= r.Y && y < r.Bottom),
            };
            if (near && along) return new DockGrab(found.Panel, r, vertical);
        }

        foreach (var placed in result.Grids)
        {
            var grid = ClientLayoutEditing.FindGrid(root, placed.AreaId);
            if (grid == null) continue;
            var r = placed.Rect;
            // Each line sits in the middle of its gap, as the calculator lays the cells out.
            if (y >= r.Y && y < r.Bottom)
            {
                var n = Math.Max(grid.Columns.Count, 1);
                var gap = placed.GapX;
                var length = Math.Max(r.Width - gap * (n - 1), 1);
                var xs = ClientLayoutCalculator.Edges(grid.Columns, n, r.X, length);
                for (var i = 1; i < xs.Length - 1; i++)
                    if (Math.Abs(x - (xs[i] + (i - 1) * gap + gap / 2)) <= reach + gap / 2) return new GridGrab(placed.AreaId, grid.Columns, i - 1, r.X, length, gap, true);
            }
            if (x >= r.X && x < r.Right)
            {
                var n = Math.Max(grid.Rows.Count, 1);
                var gap = placed.GapY;
                var length = Math.Max(r.Height - gap * (n - 1), 1);
                var ys = ClientLayoutCalculator.Edges(grid.Rows, n, r.Y, length);
                for (var i = 1; i < ys.Length - 1; i++)
                    if (Math.Abs(y - (ys[i] + (i - 1) * gap + gap / 2)) <= reach + gap / 2) return new GridGrab(placed.AreaId, grid.Rows, i - 1, r.Y, length, gap, false);
            }
        }
        return null;
    }

    /// <summary>What's in panels, deepest things before what holds them, then the top-level panels.</summary>
    private static IEnumerable<(string Id, LayoutRect Rect)> InnermostFirst(ClientLayoutResult result)
    {
        foreach (var panel in result.Panels)
        {
            if (panel.Parts == null) continue;
            for (var i = panel.Parts.Count - 1; i >= 0; i--) yield return (panel.Parts[i].Id, panel.Parts[i].Rect);
        }
        foreach (var panel in result.Panels) yield return (panel.Id, panel.Rect);
    }

    /// <summary>The id of what's at a point: the innermost thing in a panel, then panels, then what they surround.</summary>
    private string? FindAt(Point at)
    {
        var root = Root;
        if (root == null) return null;
        var (x, y) = CurrentView().ToScreen(at);
        var result = ClientLayoutCalculator.Calculate(root, Space);
        foreach (var (id, rect) in InnermostFirst(result)) if (Inside(rect, x, y)) return id;
        foreach (var c in result.Clients) if (Inside(c.Rect, x, y)) return c.AreaId;
        foreach (var e in result.Empties) if (Inside(e.Rect, x, y)) return e.AreaId;
        return null;
    }

    private static bool Inside(LayoutRect r, int x, int y) => x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;

    // ---- Pointer ----

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);

        // Pressed on something and dragged far enough: moving it into another panel.
        if (_grab == null && _pressedId is { } pressed && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (!_moving && Math.Abs(at.X - _pressedAt.X) + Math.Abs(at.Y - _pressedAt.Y) >= DragStart)
            {
                _moving = true;
                e.Pointer.Capture(this);
            }
            if (_moving)
            {
                _dropOn = DropTargetAt(at, pressed);
                Cursor = new Cursor(_dropOn == null ? StandardCursorType.No : StandardCursorType.DragMove);
                InvalidateVisual();
                return;
            }
        }

        if (_grab == null)
        {
            var over = FindGrab(at);
            Cursor = over == null ? Cursor.Default : new Cursor(over.Vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);
            return;
        }

        var (x, y) = CurrentView().ToScreen(at);
        switch (_grab)
        {
            case DockGrab dock:
                dock.Panel.Size = ClientLayoutEditing.DockSizeFromDrag(dock.Panel.Edge, dock.At, x, y);
                break;
            case GridGrab grid:
                // Back into the gapless length the shares are worked out on.
                var along = (grid.Vertical ? x : y) - (grid.Line * grid.Gap + grid.Gap / 2);
                ClientLayoutEditing.MoveGridLine(grid.Shares, grid.Line, along, grid.Start, grid.Length);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // The keyboard comes here, so Ctrl+Z undoes the layout rather than letters in the box last typed in.
        Focus();
        var at = e.GetPosition(this);
        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsLeftButtonPressed)
        {
            _grab = FindGrab(at);
            if (_grab != null)
            {
                // A grid's line is the quick way to the grid itself, whose cells cover all of it.
                if (_grab is GridGrab line) SelectedId = line.GridId;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }
        SelectedId = FindAt(at);
        // Dragged far enough, what was pressed moves into another panel, where the rules allow it (see ClientLayoutEditing.CanMove).
        // A grid's cell can't leave its grid, so pressed on a cell, the drag moves the whole grid.
        _pressedId = point.IsLeftButtonPressed && SelectedId is { } pressedOn && Root is { } pressedRoot ? ClientLayoutEditing.DraggedBy(pressedRoot, pressedOn) : null;
        _pressedAt = at;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_moving)
        {
            var (moving, on) = (_pressedId, _dropOn);
            EndMove();
            e.Pointer.Capture(null);
            if (Root is { } root && moving != null && on != null && ClientLayoutEditing.Move(root, moving, on) is { } moved)
            {
                SelectedId = moved;
                Edited?.Invoke("");
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        _pressedId = null;
        if (_grab != null)
        {
            _grab = null;
            e.Pointer.Capture(null);
            Edited?.Invoke("");
            e.Handled = true;
            return;
        }
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        var id = FindAt(e.GetPosition(this));
        if (id == null || Root == null) return;
        Menu(Root, id).Open(this);
        e.Handled = true;
    }

    private void EndMove()
    {
        _moving = false;
        _pressedId = null;
        _dropOn = null;
        Cursor = Cursor.Default;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_moving)
        {
            // Taken away mid-drag — another window, Escape — so it stays where it was.
            EndMove();
            InvalidateVisual();
        }
        if (_grab == null) return;
        _grab = null;
        Edited?.Invoke("");
    }

    // ---- The right-click menu ----

    private ContextMenu Menu(LayoutArea root, string id) => new() { ItemsSource = MenuItems(root, id) };

    /// <summary>
    /// What can be done to the thing with this id, and a way up: anything but the monitor itself
    /// ends with its parent's whole menu as a submenu, whose own parent is a submenu of that, and so
    /// on. That's how to reach something completely covered by what it holds.
    /// </summary>
    private List<Control> MenuItems(LayoutArea root, string id)
    {
        var items = new List<Control>();
        if (ClientLayoutEditing.FindDocked(root, id) is { } docked)
        {
            var panel = docked.Panel;
            var isPanel = panel.Control == null && panel.Grid == null;
            if (isPanel)
            {
                InsideMenu(items, root, () => ClientLayoutEditing.InsideOf(panel), panel.Inside);
                items.Add(new Separator());
            }
            items.Add(Item(ClientLayoutsText.MenuSendToBack, () => ClientLayoutEditing.SendToBack(docked.Area, panel)));
            items.Add(Item(ClientLayoutsText.MenuBringToFront, () => ClientLayoutEditing.BringToFront(docked.Area, panel)));
            items.Add(new Separator());
            var remove = panel.Grid != null ? ClientLayoutsText.MenuRemoveGrid
                : PanelControls.IsClient(panel.Control) ? ClientLayoutsText.MenuRemoveClient
                : isPanel ? ClientLayoutsText.MenuRemovePanel : ClientLayoutsText.MenuRemoveControl;
            items.Add(Item(remove, () =>
            {
                docked.Area.Docked.Remove(panel);
                SelectedId = null;
            }));
        }
        else if (ClientLayoutEditing.FindArea(root, id) is { } area)
        {
            AreaMenu(items, root, area);
        }

        if (ClientLayoutEditing.ParentOf(root, id) is { } parent)
        {
            items.Add(new Separator());
            items.Add(new MenuItem
            {
                Header = string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.MenuParent, Describe(root, parent)),
                ItemsSource = new List<Control> { Item(ClientLayoutsText.MenuSelect, () => SelectedId = parent), new Separator() }
                    .Concat(MenuItems(root, parent)).ToList(),
            });
        }
        return items;
    }

    private static string Describe(LayoutArea root, string id)
    {
        if (ClientLayoutEditing.FindDocked(root, id) is { } docked)
            return docked.Panel.Grid != null ? ClientLayoutsText.ParentGrid
                : docked.Panel.Name.Length > 0 ? docked.Panel.Name : ClientLayoutsText.PanelDefaultName;
        if (ClientLayoutEditing.FindArea(root, id) is not { } area) return "";
        if (area == root && area.Content != AreaContent.Panel) return ClientLayoutsText.ParentMonitor;
        return area.Content switch
        {
            AreaContent.Panel => area.Name.Length > 0 ? area.Name : ClientLayoutsText.PanelDefaultName,
            AreaContent.Grid => ClientLayoutsText.ParentGrid,
            _ => ClientLayoutsText.ParentCell,
        };
    }

    /// <summary>
    /// ⚠️ Only panels go on the monitor; everything else goes in a panel, and a grid's cells are
    /// panels. So the monitor offers panels and nothing else, and a cell can't be removed or moved —
    /// only what's in it.
    /// </summary>
    private void AreaMenu(List<Control> items, LayoutArea root, LayoutArea area)
    {
        var isCell = ClientLayoutEditing.FindGridHolder(root, area.Id) != null;
        switch (area.Content)
        {
            case AreaContent.Panel:
                InsideMenu(items, root, () => ClientLayoutEditing.InsideOf(area), area.Inside);
                if (area == root)
                {
                    items.Add(new Separator());
                    items.Add(DockPanelMenu(ClientLayoutsText.MenuDockOnMonitor, () => root, full: false));
                }
                if (!isCell)
                {
                    items.Add(new Separator());
                    items.Add(Item(ClientLayoutsText.MenuRemovePanel, () =>
                    {
                        ClientLayoutEditing.SetContent(area, AreaContent.Empty);
                        area.Inside = null;
                        area.Look = null;
                        SelectedId = null;
                    }));
                }
                break;

            case AreaContent.Control or AreaContent.Client or AreaContent.Grid:
                var remove = area.Content switch
                {
                    AreaContent.Grid => ClientLayoutsText.MenuRemoveGrid,
                    AreaContent.Client => ClientLayoutsText.MenuRemoveClient,
                    _ => ClientLayoutsText.MenuRemoveControl,
                };
                items.Add(Item(remove, () =>
                {
                    ClientLayoutEditing.SetContent(area, AreaContent.Empty);
                    SelectedId = null;
                }));
                break;

            default:
                if (area == root) items.Add(DockPanelMenu(ClientLayoutsText.MenuDockOnMonitor, () => root, full: true));
                else InsideMenu(items, root, () => area, area);
                break;
        }
    }

    /// <summary>
    /// What can be put inside a panel: panels, controls (a game client among them) and grids, each to
    /// an edge or filling it. Full only while nothing fills it already. The area is only made if
    /// something is put there.
    /// </summary>
    private void InsideMenu(List<Control> items, LayoutArea root, Func<LayoutArea> inside, LayoutArea? current)
    {
        var full = current == null || current.Content == AreaContent.Empty;
        items.Add(DockPanelMenu(ClientLayoutsText.MenuDockInside, inside, full));
        items.Add(DockControlMenu(inside, full));
        items.Add(DockGridMenu(root, inside, current, full));
    }

    private MenuItem DockPanelMenu(string header, Func<LayoutArea> target, bool full)
    {
        var choices = EdgeChoices().Select(e => (Control)Item(e.Header, () =>
        {
            var panel = ClientLayoutEditing.Dock(target(), e.Edge, 100, ClientLayoutsText.PanelDefaultName);
            SelectedId = panel.Id;
        })).ToList();
        choices.Add(FullItem(full, () =>
        {
            var area = target();
            area.Content = AreaContent.Panel;
            SelectedId = area.Id;
        }));
        return new MenuItem { Header = header, ItemsSource = choices };
    }

    /// <summary>Each control, a game client first, then where to dock it, including full.</summary>
    private MenuItem DockControlMenu(Func<LayoutArea> target, bool full)
    {
        return new MenuItem
        {
            Header = ClientLayoutsText.MenuDockControl,
            ItemsSource = Enum.GetValues<PanelControlKind>().Select(kind => (Control)new MenuItem
            {
                Header = ClientLayoutPainter.ControlName(kind),
                ItemsSource = EdgeChoices().Select(e => (Control)Item(e.Header, () =>
                {
                    var control = ClientLayoutEditing.DockControl(target(), kind, e.Edge);
                    SelectedId = control.Id;
                })).Append(FullItem(full, () =>
                {
                    var area = target();
                    ClientLayoutEditing.FillWithControl(area, kind);
                    SelectedId = area.Id;
                })).ToList(),
            }).ToList(),
        };
    }

    /// <summary>
    /// Where to dock a grid. It starts as 2 × 2 empty panels, and its columns and rows are set beside
    /// the drawing once it's selected.
    /// </summary>
    /// <param name="current">The area as it is now, if it exists yet, for whether the depth cap allows it.</param>
    private MenuItem DockGridMenu(LayoutArea root, Func<LayoutArea> target, LayoutArea? current, bool full)
    {
        MenuItem At(string header, DockPlace place, bool enabled)
        {
            var item = Item(header, () => { if (ClientLayoutEditing.DockGrid(root, target(), place, 2, 2) is { } id) SelectedId = id; });
            item.IsEnabled = enabled;
            return item;
        }
        var places = EdgeChoices().Select(e => (Control)At(e.Header, (DockPlace)e.Edge, true)).ToList();
        places.Add(At(ClientLayoutsText.MenuEdgeFull, DockPlace.Full, full));
        return new MenuItem
        {
            Header = ClientLayoutsText.MenuDockGrid,
            IsEnabled = current == null || ClientLayoutEditing.CanDockGrid(root, current),
            ItemsSource = places,
        };
    }
    /// <summary>Full, offered only while nothing fills the area: it never replaces what's there.</summary>
    private MenuItem FullItem(bool enabled, Action change)
    {
        var item = Item(ClientLayoutsText.MenuEdgeFull, change);
        item.IsEnabled = enabled;
        return item;
    }
    private static (string Header, DockEdge Edge)[] EdgeChoices() =>
    [
        (ClientLayoutsText.MenuEdgeTop, DockEdge.Top),
        (ClientLayoutsText.MenuEdgeBottom, DockEdge.Bottom),
        (ClientLayoutsText.MenuEdgeLeft, DockEdge.Left),
        (ClientLayoutsText.MenuEdgeRight, DockEdge.Right),
    ];

    private MenuItem Item(string header, Action change)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            change();
            InvalidateVisual();
            Edited?.Invoke("");
        };
        return item;
    }
}
