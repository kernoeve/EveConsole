namespace EveConsole.Services.ClientLayouts;

/// <summary>The changes the editor can make to a layout, kept apart from any drawing.</summary>
public static class ClientLayoutEditing
{
    /// <summary>A grid line can't be dragged closer than this share of the grid to its neighbour.</summary>
    public const double LeastShare = 0.02;

    public static LayoutArea? FindArea(LayoutArea root, string id)
    {
        if (root.Id == id) return root;
        if (root.Grid == null) return null;
        foreach (var cell in root.Grid.Cells)
            if (FindArea(cell, id) is { } found) return found;
        return null;
    }

    /// <summary>The area whose grid holds this one as a cell, or null for the top of the layout.</summary>
    public static LayoutArea? FindGridOwner(LayoutArea root, string id)
    {
        if (root.Grid == null) return null;
        foreach (var cell in root.Grid.Cells)
        {
            if (cell.Id == id) return root;
            if (FindGridOwner(cell, id) is { } found) return found;
        }
        return null;
    }

    /// <summary>A docked panel and the area it is docked in.</summary>
    public static (LayoutArea Area, DockedPanel Panel)? FindDocked(LayoutArea root, string panelId)
    {
        foreach (var panel in root.Docked)
            if (panel.Id == panelId) return (root, panel);
        if (root.Grid == null) return null;
        foreach (var cell in root.Grid.Cells)
            if (FindDocked(cell, panelId) is { } found) return found;
        return null;
    }

    /// <summary>How many grids this area sits inside.</summary>
    public static int GridsAbove(LayoutArea root, string id)
    {
        var count = 0;
        for (var owner = FindGridOwner(root, id); owner != null; owner = FindGridOwner(root, owner.Id)) count++;
        return count;
    }

    /// <summary>Docks a panel last in order, so it fits in whatever is left.</summary>
    public static DockedPanel Dock(LayoutArea area, DockEdge edge, int size, string name)
    {
        var panel = new DockedPanel { Edge = edge, Size = Math.Max(size, 1), Name = name };
        area.Docked.Add(panel);
        return panel;
    }

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
    /// a client position keeps its characters. Its docked panels stay on the outside.
    /// </summary>
    public static bool Split(LayoutArea root, LayoutArea area, int columns, int rows)
    {
        if (columns < 1 || rows < 1 || columns * rows < 2 || !CanSplit(root, area)) return false;

        var grid = LayoutGrid.Equal(columns, rows);
        var first = grid.Cells[0];
        first.Content = area.Content;
        first.Grid = area.Grid;
        first.Name = area.Name;
        first.Id = area.Id;

        area.Id = Guid.NewGuid().ToString("N");
        area.Content = AreaContent.Grid;
        area.Grid = grid;
        area.Name = "";
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

    /// <summary>Sets a middle that isn't a grid. A grid in that place is removed.</summary>
    public static void SetContent(LayoutArea area, AreaContent content)
    {
        if (content == AreaContent.Grid) return;
        area.Grid = null;
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

    /// <summary>A docked panel's size for a dragged inner edge.</summary>
    public static int DockSizeFromDrag(DockEdge edge, LayoutRect panel, int x, int y) => Math.Max(1, edge switch
    {
        DockEdge.Top => y - panel.Y,
        DockEdge.Bottom => panel.Bottom - y,
        DockEdge.Left => x - panel.X,
        _ => panel.Right - x,
    });
}
