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

        foreach (var placed in result.Panels)
        {
            if (ClientLayoutEditing.FindDocked(root, placed.Id) is not { } found) continue;
            var r = placed.Rect;
            var (near, vertical, along) = found.Panel.Edge switch
            {
                DockEdge.Top => (Math.Abs(y - r.Bottom) <= reach, false, x >= r.X && x < r.Right),
                DockEdge.Bottom => (Math.Abs(y - r.Y) <= reach, false, x >= r.X && x < r.Right),
                DockEdge.Left => (Math.Abs(x - r.Right) <= reach, true, y >= r.Y && y < r.Bottom),
                _ => (Math.Abs(x - r.X) <= reach, true, y >= r.Y && y < r.Bottom),
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

    /// <summary>The id of the panel or area at a point. Panels are looked at first: they're on the outside.</summary>
    private string? FindAt(Point at)
    {
        var root = Root;
        if (root == null) return null;
        var (x, y) = CurrentView().ToScreen(at);
        var result = ClientLayoutCalculator.Calculate(root, Space);
        foreach (var p in result.Panels) if (Inside(p.Rect, x, y)) return p.Id;
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

    private ContextMenu Menu(LayoutArea root, string id)
    {
        var items = new List<Control>();
        if (ClientLayoutEditing.FindDocked(root, id) is { } docked) DockedMenu(items, docked.Area, docked.Panel);
        else if (ClientLayoutEditing.FindArea(root, id) is { } area) AreaMenu(items, root, area);
        return new ContextMenu { ItemsSource = items };
    }

    private void DockedMenu(List<Control> items, LayoutArea area, DockedPanel panel)
    {
        items.Add(Item(ClientLayoutsText.MenuSendToBack, () => ClientLayoutEditing.SendToBack(area, panel)));
        items.Add(Item(ClientLayoutsText.MenuBringToFront, () => ClientLayoutEditing.BringToFront(area, panel)));
        items.Add(new Separator());
        items.Add(Item(ClientLayoutsText.MenuRemovePanel, () =>
        {
            area.Docked.Remove(panel);
            SelectedId = null;
        }));
    }

    private void AreaMenu(List<Control> items, LayoutArea root, LayoutArea area)
    {
        items.Add(DockMenu(ClientLayoutsText.MenuDockHere, area));
        var owner = ClientLayoutEditing.FindGridOwner(root, area.Id);
        if (owner != null && owner != root) items.Add(DockMenu(ClientLayoutsText.MenuDockAroundGrid, owner));
        if (area != root) items.Add(DockMenu(ClientLayoutsText.MenuDockMonitor, root));
        items.Add(new Separator());

        var split = new MenuItem { Header = ClientLayoutsText.MenuSplit, IsEnabled = ClientLayoutEditing.CanSplit(root, area) };
        split.ItemsSource = GridChoices.Select(c => (Control)Item(
            string.Format(ClientLayoutsText.CanvasSize, c.Columns, c.Rows),
            () => ClientLayoutEditing.Split(root, area, c.Columns, c.Rows))).ToList();
        items.Add(split);
        if (owner != null) items.Add(Item(ClientLayoutsText.MenuRemoveGrid, () =>
        {
            ClientLayoutEditing.RemoveGrid(owner);
            SelectedId = null;
        }));
        items.Add(new Separator());

        items.Add(Holds(ClientLayoutsText.MenuHoldsClient, area, AreaContent.Client));
        items.Add(Holds(ClientLayoutsText.MenuHoldsPanel, area, AreaContent.Panel));
        items.Add(Holds(ClientLayoutsText.MenuHoldsNothing, area, AreaContent.Empty));
    }

    private MenuItem Holds(string header, LayoutArea area, AreaContent content)
    {
        var item = Item(header, () => ClientLayoutEditing.SetContent(area, content));
        item.ToggleType = MenuItemToggleType.Radio;
        item.IsChecked = area.Content == content;
        return item;
    }

    private MenuItem DockMenu(string header, LayoutArea target)
    {
        return new MenuItem
        {
            Header = header,
            ItemsSource = new List<Control>
            {
                DockChoice(ClientLayoutsText.MenuEdgeTop, target, DockEdge.Top),
                DockChoice(ClientLayoutsText.MenuEdgeBottom, target, DockEdge.Bottom),
                DockChoice(ClientLayoutsText.MenuEdgeLeft, target, DockEdge.Left),
                DockChoice(ClientLayoutsText.MenuEdgeRight, target, DockEdge.Right),
            },
        };
    }

    private MenuItem DockChoice(string header, LayoutArea target, DockEdge edge) => Item(header, () =>
    {
        var panel = ClientLayoutEditing.Dock(target, edge, 100, ClientLayoutsText.PanelDefaultName);
        SelectedId = panel.Id;
    });

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
