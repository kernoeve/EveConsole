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

    private static readonly (int Columns, int Rows)[] GridChoices =
        [(2, 1), (1, 2), (2, 2), (3, 1), (1, 3), (3, 2), (2, 3), (3, 3), (4, 2), (4, 4)];

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

    static ClientLayoutCanvas() =>
        AffectsRender<ClientLayoutCanvas>(RootProperty, SpaceProperty, RevisionProperty, SelectedIdProperty);

    public ClientLayoutCanvas()
    {
        _painter = new ClientLayoutPainter(this);
        ClipToBounds = true;
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
        _painter.Draw(context, ClientLayoutCalculator.Calculate(root, Space), view, SelectedId);
    }

    // ---- What's under the pointer ----

    /// <summary>A line that can be dragged: a docked panel's inner edge, or a line in a grid.</summary>
    private abstract record Grab(bool Vertical);
    private sealed record DockGrab(DockedPanel Panel, LayoutRect At, bool Vertical) : Grab(Vertical);
    private sealed record GridGrab(List<double> Shares, int Line, int Start, int Length, bool Vertical) : Grab(Vertical);

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
            var grid = ClientLayoutEditing.FindArea(root, placed.AreaId)?.Grid;
            if (grid == null) continue;
            var r = placed.Rect;
            if (y >= r.Y && y < r.Bottom)
            {
                var xs = ClientLayoutCalculator.Edges(grid.Columns, Math.Max(grid.Columns.Count, 1), r.X, r.Width);
                for (var i = 1; i < xs.Length - 1; i++)
                    if (Math.Abs(x - xs[i]) <= reach) return new GridGrab(grid.Columns, i - 1, r.X, r.Width, true);
            }
            if (x >= r.X && x < r.Right)
            {
                var ys = ClientLayoutCalculator.Edges(grid.Rows, Math.Max(grid.Rows.Count, 1), r.Y, r.Height);
                for (var i = 1; i < ys.Length - 1; i++)
                    if (Math.Abs(y - ys[i]) <= reach) return new GridGrab(grid.Rows, i - 1, r.Y, r.Height, false);
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
                ClientLayoutEditing.MoveGridLine(grid.Shares, grid.Line, grid.Vertical ? x : y, grid.Start, grid.Length);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var at = e.GetPosition(this);
        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsLeftButtonPressed)
        {
            _grab = FindGrab(at);
            if (_grab != null)
            {
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }
        SelectedId = FindAt(at);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
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

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
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
            if (docked.Panel.Control == null) DockedPanelMenu(items, root, docked.Panel);
            items.Add(Item(ClientLayoutsText.MenuSendToBack, () => ClientLayoutEditing.SendToBack(docked.Area, docked.Panel)));
            items.Add(Item(ClientLayoutsText.MenuBringToFront, () => ClientLayoutEditing.BringToFront(docked.Area, docked.Panel)));
            items.Add(new Separator());
            items.Add(Item(docked.Panel.Control == null ? ClientLayoutsText.MenuRemovePanel : ClientLayoutsText.MenuRemoveControl, () =>
            {
                docked.Area.Docked.Remove(docked.Panel);
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
            return docked.Panel.Name.Length > 0 ? docked.Panel.Name : ClientLayoutsText.PanelDefaultName;
        if (ClientLayoutEditing.FindArea(root, id) is not { } area) return "";
        if (area == root) return ClientLayoutsText.ParentMonitor;
        return area.Content switch
        {
            AreaContent.Panel => area.Name.Length > 0 ? area.Name : ClientLayoutsText.PanelDefaultName,
            AreaContent.Grid => ClientLayoutsText.ParentGrid,
            _ => ClientLayoutsText.ParentCell,
        };
    }

    /// <summary>A docked panel's menu starts with what can go inside it.</summary>
    private void DockedPanelMenu(List<Control> items, LayoutArea root, DockedPanel panel)
    {
        InsideMenu(items, root, () => ClientLayoutEditing.InsideOf(panel), panel.Inside);
        items.Add(new Separator());
    }

    private void AreaMenu(List<Control> items, LayoutArea root, LayoutArea area)
    {
        // A control docked full is a thing of its own, with nothing to dock around it.
        if (area.Content == AreaContent.Control)
        {
            items.Add(Item(ClientLayoutsText.MenuRemoveControl, () =>
            {
                ClientLayoutEditing.SetContent(area, AreaContent.Empty);
                SelectedId = null;
            }));
            return;
        }

        if (area.Content == AreaContent.Panel)
        {
            InsideMenu(items, root, () => ClientLayoutEditing.InsideOf(area), area.Inside);
            items.Add(new Separator());
        }

        // Docking on the area itself, around whatever its middle holds.
        items.Add(DockPanelMenu(area.Content == AreaContent.Panel ? ClientLayoutsText.MenuDockBeside : ClientLayoutsText.MenuDockHere, () => area));
        items.Add(DockControlMenu(() => area));

        if (area.Content == AreaContent.Grid)
            items.Add(Item(ClientLayoutsText.MenuRemoveGrid, () =>
            {
                ClientLayoutEditing.RemoveGrid(area);
                SelectedId = null;
            }));
        else if (area.Content != AreaContent.Panel)
            items.Add(SplitMenu(root, () => area, area));

        items.Add(new Separator());
        items.Add(Holds(ClientLayoutsText.MenuHoldsClient, area, AreaContent.Client));
        items.Add(Holds(ClientLayoutsText.MenuHoldsPanel, area, AreaContent.Panel));
        items.Add(Holds(ClientLayoutsText.MenuHoldsNothing, area, AreaContent.Empty));
    }

    /// <summary>What can be put inside a panel. The area is only made if something is put there.</summary>
    private void InsideMenu(List<Control> items, LayoutArea root, Func<LayoutArea> inside, LayoutArea? current)
    {
        items.Add(DockPanelMenu(ClientLayoutsText.MenuDockInside, inside));
        items.Add(DockControlMenu(inside));
        if (current?.Content == AreaContent.Grid)
            items.Add(Item(ClientLayoutsText.MenuRemoveGrid, () => ClientLayoutEditing.RemoveGrid(current)));
        else
            items.Add(SplitMenu(root, inside, current));

        var holdsClient = Item(ClientLayoutsText.MenuHoldsClient, () => ClientLayoutEditing.SetContent(inside(), AreaContent.Client));
        holdsClient.ToggleType = MenuItemToggleType.Radio;
        holdsClient.IsChecked = current?.Content == AreaContent.Client;
        items.Add(holdsClient);
    }

    /// <param name="area">The area as it is now, if it exists yet, for whether the depth cap allows it.</param>
    private MenuItem SplitMenu(LayoutArea root, Func<LayoutArea> target, LayoutArea? area)
    {
        return new MenuItem
        {
            Header = ClientLayoutsText.MenuSplit,
            IsEnabled = area == null || ClientLayoutEditing.CanSplit(root, area),
            ItemsSource = GridChoices.Select(c => (Control)Item(
                string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.CanvasSize, c.Columns, c.Rows),
                () => ClientLayoutEditing.Split(root, target(), c.Columns, c.Rows))).ToList(),
        };
    }

    private MenuItem Holds(string header, LayoutArea area, AreaContent content)
    {
        var item = Item(header, () => ClientLayoutEditing.SetContent(area, content));
        item.ToggleType = MenuItemToggleType.Radio;
        item.IsChecked = area.Content == content;
        return item;
    }

    private MenuItem DockPanelMenu(string header, Func<LayoutArea> target)
    {
        return new MenuItem
        {
            Header = header,
            ItemsSource = EdgeChoices().Select(e => (Control)Item(e.Header, () =>
            {
                var panel = ClientLayoutEditing.Dock(target(), e.Edge, 100, ClientLayoutsText.PanelDefaultName);
                SelectedId = panel.Id;
            })).ToList(),
        };
    }

    /// <summary>Each control, then where to dock it, including full.</summary>
    private MenuItem DockControlMenu(Func<LayoutArea> target)
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
                })).Append(Item(ClientLayoutsText.MenuEdgeFull, () =>
                {
                    var area = target();
                    ClientLayoutEditing.FillWithControl(area, kind);
                    SelectedId = area.Id;
                })).ToList(),
            }).ToList(),
        };
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
