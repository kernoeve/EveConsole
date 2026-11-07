namespace EveConsole.Services.ClientLayouts;

/// <summary>The changes the editor can make to a layout, kept apart from any drawing.</summary>
public static class ClientLayoutEditing
{
    /// <summary>A grid line can't be dragged closer than this share of the grid to its neighbour.</summary>
    public const double LeastShare = 0.02;

    /// <summary>Every area directly below this one: grid cells, and the insides of its panels.</summary>
    private static IEnumerable<LayoutArea> Below(LayoutArea area)
    {
        foreach (var panel in area.Docked)
            if (panel.Inside != null) yield return panel.Inside;
        if (area.Inside != null) yield return area.Inside;
        if (area.Grid == null) yield break;
        foreach (var cell in area.Grid.Cells) yield return cell;
    }

    /// <summary>Every control in a layout, docked or filling an area, however deep, with its id, name and choices.</summary>
    public static IEnumerable<(string Id, string Name, string? Control, ControlOptions? Options)> Controls(LayoutArea root)
    {
        if (root.Content == AreaContent.Control) yield return (root.Id, root.Name, root.Control, root.Options);
        foreach (var panel in root.Docked)
            if (panel.Control != null) yield return (panel.Id, panel.Name, panel.Control, panel.Options);
        foreach (var below in Below(root))
            foreach (var found in Controls(below))
                yield return found;
    }

    public static LayoutArea? FindArea(LayoutArea root, string id)
    {
        if (root.Id == id) return root;
        foreach (var below in Below(root))
            if (FindArea(below, id) is { } found) return found;
        return null;
    }

    /// <summary>The area whose grid holds this one as a cell, or null if it isn't a cell.</summary>
    public static LayoutArea? FindGridOwner(LayoutArea root, string id)
    {
        if (root.Grid?.Cells.Any(c => c.Id == id) == true) return root;
        foreach (var below in Below(root))
            if (FindGridOwner(below, id) is { } found) return found;
        return null;
    }

    /// <summary>A docked panel or control, and the area it is docked in.</summary>
    public static (LayoutArea Area, DockedPanel Panel)? FindDocked(LayoutArea root, string panelId)
    {
        foreach (var panel in root.Docked)
            if (panel.Id == panelId) return (root, panel);
        foreach (var below in Below(root))
            if (FindDocked(below, panelId) is { } found) return found;
        return null;
    }

    /// <summary>
    /// What someone working on this layout sees as the thing with this id's parent: an area inside a
    /// panel is that panel, as far as anyone can tell. Null for the top of the layout.
    /// </summary>
    public static string? ParentOf(LayoutArea root, string id)
    {
        if (FindDocked(root, id) is { } docked) return VisibleId(root, docked.Area);
        if (FindGridOwner(root, id) is { } owner) return VisibleId(root, owner);
        if (FindInsideOwner(root, id) is not { } panelId) return null;

        // A control or client filling a panel is a thing of its own inside it. Any other inside is the panel.
        return FindArea(root, id)?.Content is AreaContent.Control or AreaContent.Client ? panelId : ParentOf(root, panelId);
    }

    /// <summary>The panel an area is the inside of, or its own id if it isn't one.</summary>
    public static string VisibleId(LayoutArea root, LayoutArea area) => FindInsideOwner(root, area.Id) ?? area.Id;

    /// <summary>The id of the panel whose inside this is, or null if it isn't one.</summary>
    private static string? FindInsideOwner(LayoutArea area, string insideId)
    {
        if (area.Inside?.Id == insideId) return area.Id;
        foreach (var panel in area.Docked)
            if (panel.Inside?.Id == insideId) return panel.Id;
        foreach (var below in Below(area))
            if (FindInsideOwner(below, insideId) is { } found) return found;
        return null;
    }

    /// <summary>How many grids this area sits inside, counting through panels.</summary>
    public static int GridsAbove(LayoutArea root, string id) => Search(root, id, 0) ?? 0;

    private static int? Search(LayoutArea area, string id, int grids)
    {
        if (area.Id == id) return grids;
        foreach (var panel in area.Docked)
            if (panel.Inside != null && Search(panel.Inside, id, grids) is { } found) return found;
        if (area.Inside != null && Search(area.Inside, id, grids) is { } inside) return inside;
        if (area.Grid == null) return null;
        foreach (var cell in area.Grid.Cells)
            if (Search(cell, id, grids + 1) is { } inCell) return inCell;
        return null;
    }

    /// <summary>Docks a panel last in order, so it fits in whatever is left.</summary>
    public static DockedPanel Dock(LayoutArea area, DockEdge edge, int size, string name)
    {
        var panel = new DockedPanel { Edge = edge, Size = Math.Max(size, 1), Name = name };
        area.Docked.Add(panel);
        return panel;
    }

    /// <summary>Docks a control last in order, at a size that suits it.</summary>
    public static DockedPanel DockControl(LayoutArea area, PanelControlKind kind, DockEdge edge)
    {
        var control = new DockedPanel { Edge = edge, Size = PanelControls.DefaultSize(kind, edge), Control = kind.ToString() };
        area.Docked.Add(control);
        return control;
    }

    /// <summary>Docks a control full: it takes the area's whole middle, in place of what was there.</summary>
    public static void FillWithControl(LayoutArea area, PanelControlKind kind)
    {
        area.Grid = null;
        area.Inside = null;
        area.Content = AreaContent.Control;
        area.Control = kind.ToString();
        area.Options = null;
    }

    /// <summary>The area inside a docked panel, made when it's first needed.</summary>
    public static LayoutArea InsideOf(DockedPanel panel) => panel.Inside ??= new LayoutArea();

    /// <summary>The area inside a panel filling an area, made when it's first needed.</summary>
    public static LayoutArea InsideOf(LayoutArea panel) => panel.Inside ??= new LayoutArea();

    /// <summary>First in order, as if docked first, so it takes its full edge.</summary>
    public static void SendToBack(LayoutArea area, DockedPanel panel)
    {
        if (!area.Docked.Remove(panel)) return;
        area.Docked.Insert(0, panel);
    }

    /// <summary>Last in order, so it fits in what the others leave.</summary>
    public static void BringToFront(LayoutArea area, DockedPanel panel)
    {
        if (!area.Docked.Remove(panel)) return;
        area.Docked.Add(panel);
    }

    public static bool CanSplit(LayoutArea root, LayoutArea area) =>
        GridsAbove(root, area.Id) < ClientLayoutCalculator.MaxGridDepth;

    /// <summary>
    /// Turns an area's middle into a grid. What it held moves to the first cell, keeping its id, so
    /// a client position keeps its characters. What's docked in it stays on the outside.
    /// </summary>
    public static bool Split(LayoutArea root, LayoutArea area, int columns, int rows)
    {
        if (columns < 1 || rows < 1 || columns * rows < 2 || !CanSplit(root, area)) return false;

        var grid = LayoutGrid.Equal(columns, rows);
        var first = grid.Cells[0];
        first.Content = area.Content;
        first.Grid = area.Grid;
        first.Name = area.Name;
        first.Inside = area.Inside;
        first.Look = area.Look;
        first.Control = area.Control;
        first.Options = area.Options;
        first.Id = area.Id;

        area.Id = Guid.NewGuid().ToString("N");
        area.Content = AreaContent.Grid;
        area.Grid = grid;
        area.Name = "";
        area.Inside = null;
        area.Look = null;
        area.Control = null;
        area.Options = null;
        return true;
    }

    /// <summary>
    /// Removes a grid. Client positions inside it are gone, and the characters assigned to them
    /// become unassigned on each monitor that uses the layout.
    /// </summary>
    public static void RemoveGrid(LayoutArea area)
    {
        area.Grid = null;
        area.Content = AreaContent.Empty;
    }

    /// <summary>Sets a middle that isn't a grid. A grid or full control in that place is removed.</summary>
    public static void SetContent(LayoutArea area, AreaContent content)
    {
        if (content is AreaContent.Grid or AreaContent.Control) return;
        area.Grid = null;
        area.Control = null;
        area.Content = content;
    }

    /// <summary>
    /// Moves the line between two columns or rows to a position on screen. Only those two change,
    /// and together they keep their share, so the rest of the grid stays where it is.
    /// </summary>
    public static void MoveGridLine(List<double> shares, int line, int position, int start, int length)
    {
        if (line < 0 || line + 1 >= shares.Count || length <= 0) return;
        var total = shares.Sum();
        if (total <= 0) return;

        var before = shares.Take(line).Sum();
        var pair = shares[line] + shares[line + 1];
        var least = Math.Min(total * LeastShare, pair / 2);
        var left = (position - start) / (double)length * total - before;
        left = Math.Clamp(left, least, pair - least);
        shares[line] = left;
        shares[line + 1] = pair - left;
    }

    /// <summary>A docked panel's or control's size for a dragged inner edge.</summary>
    public static int DockSizeFromDrag(DockEdge edge, LayoutRect panel, int x, int y) => Math.Max(1, edge switch
    {
        DockEdge.Top => y - panel.Y,
        DockEdge.Bottom => panel.Bottom - y,
        DockEdge.Left => x - panel.X,
        _ => panel.Right - x,
    });
}
