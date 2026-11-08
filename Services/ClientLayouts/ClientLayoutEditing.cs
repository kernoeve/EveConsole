namespace EveConsole.Services.ClientLayouts;

/// <summary>The changes the editor can make to a layout, kept apart from any drawing.</summary>
public static class ClientLayoutEditing
{
    /// <summary>A grid line can't be dragged closer than this share of the grid to its neighbour.</summary>
    public const double LeastShare = 0.02;

    /// <summary>A new layout: one panel filling the monitor, with a client filling the panel.</summary>
    public static LayoutArea NewRoot() => new()
    {
        Content = AreaContent.Panel,
        Inside = new LayoutArea { Content = AreaContent.Client },
    };

    /// <summary>Every area directly below this one: grid cells, docked grids' cells, and the insides of its panels.</summary>
    private static IEnumerable<LayoutArea> Below(LayoutArea area)
    {
        foreach (var panel in area.Docked)
        {
            if (panel.Inside != null) yield return panel.Inside;
            if (panel.Grid != null)
                foreach (var cell in panel.Grid.Cells) yield return cell;
        }
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

    /// <summary>A grid by the id it's selected by: the area it fills, or the one docked.</summary>
    public static LayoutGrid? FindGrid(LayoutArea root, string id) =>
        FindDocked(root, id) is { Panel.Grid: { } docked } ? docked
        : FindArea(root, id) is { Content: AreaContent.Grid, Grid: { } full } ? full : null;

    /// <summary>The id the grid holding this cell is selected by, or null if it isn't a cell.</summary>
    public static string? FindGridHolder(LayoutArea root, string cellId)
    {
        if (root.Grid?.Cells.Any(c => c.Id == cellId) == true) return root.Id;
        foreach (var panel in root.Docked)
            if (panel.Grid?.Cells.Any(c => c.Id == cellId) == true) return panel.Id;
        foreach (var below in Below(root))
            if (FindGridHolder(below, cellId) is { } found) return found;
        return null;
    }

    /// <summary>The area whose grid holds this one as a cell, or null if it isn't a cell of a grid filling an area.</summary>
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
    ///
    /// <para>A cell's parent is its grid, and a grid's is the panel it's in: Panel › Grid › Panel.
    /// ⚠️ Not the panel the grid is in, straight from the cell. That skipped the grid, and a grid
    /// filling a panel then had no way to be selected at all.</para>
    /// </summary>
    public static string? ParentOf(LayoutArea root, string id)
    {
        if (FindDocked(root, id) is { } docked) return VisibleId(root, docked.Area);
        if (FindGridHolder(root, id) is { } holder) return holder;
        if (FindInsideOwner(root, id) is not { } panelId) return null;

        // A control, client or grid filling a panel is a thing of its own inside it. Any other inside is the panel.
        return FindArea(root, id)?.Content is AreaContent.Control or AreaContent.Client or AreaContent.Grid ? panelId : ParentOf(root, panelId);
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
        {
            if (panel.Inside != null && Search(panel.Inside, id, grids) is { } found) return found;
            foreach (var cell in panel.Grid?.Cells ?? [])
                if (Search(cell, id, grids + 1) is { } inDocked) return inDocked;
        }
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

    /// <summary>Docks a control full: it takes the area's whole middle, in place of what was there.
    /// A client full is the area's own client place.</summary>
    public static void FillWithControl(LayoutArea area, PanelControlKind kind)
    {
        area.Grid = null;
        area.Inside = null;
        area.Options = null;
        if (kind == PanelControlKind.Client)
        {
            area.Content = AreaContent.Client;
            area.Control = null;
            return;
        }
        area.Content = AreaContent.Control;
        area.Control = kind.ToString();
    }

    /// <summary>The most columns, and the most rows, a grid can have.</summary>
    public const int MaxGridLines = 12;

    /// <summary>
    /// Gives a grid this many columns and rows. Each cell keeps its place, by row and column, and
    /// with it everything in it and its id; new cells are empty panels. A new column or row takes the
    /// average share of the ones there, so it starts the same size as they are on average.
    /// ⚠️ Cells past the new edge are dropped, whatever they hold: that is what Undo is for.
    /// </summary>
    public static bool ResizeGrid(LayoutGrid grid, int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, MaxGridLines);
        rows = Math.Clamp(rows, 1, MaxGridLines);
        var oldColumns = Math.Max(grid.Columns.Count, 1);
        var oldRows = Math.Max(grid.Rows.Count, 1);
        if (columns == grid.Columns.Count && rows == grid.Rows.Count && grid.Cells.Count == columns * rows) return false;

        var cells = new List<LayoutArea>(columns * rows);
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var index = row * oldColumns + column;
                cells.Add(row < oldRows && column < oldColumns && index < grid.Cells.Count
                    ? grid.Cells[index]
                    : new LayoutArea { Content = AreaContent.Panel });
            }
        grid.Cells = cells;
        grid.Columns = Resized(grid.Columns, columns);
        grid.Rows = Resized(grid.Rows, rows);
        return true;
    }

    private static List<double> Resized(List<double> shares, int count)
    {
        var kept = shares.Take(count).ToList();
        var average = kept.Count > 0 && kept.Average() is var a && a > 0 && double.IsFinite(a) ? a : 1.0;
        while (kept.Count < count) kept.Add(average);
        return kept;
    }

    /// <summary>Whether a grid can go in this area without nesting grids past the cap.</summary>
    public static bool CanDockGrid(LayoutArea root, LayoutArea area) =>
        GridsAbove(root, area.Id) < ClientLayoutCalculator.MaxGridDepth;

    /// <summary>
    /// Docks a grid of empty panels to an edge of an area, or fills its middle with one when the
    /// middle is empty. Returns the id it's selected by, or null if it can't go there.
    /// </summary>
    public static string? DockGrid(LayoutArea root, LayoutArea area, DockPlace place, int columns, int rows)
    {
        if (columns < 1 || rows < 1 || columns * rows < 2 || !CanDockGrid(root, area)) return null;
        var grid = LayoutGrid.Equal(columns, rows);
        if (place != DockPlace.Full)
        {
            var docked = new DockedPanel { Edge = (DockEdge)place, Size = 300, Grid = grid };
            area.Docked.Add(docked);
            return docked.Id;
        }
        if (area.Content != AreaContent.Empty) return null;
        area.Content = AreaContent.Grid;
        area.Grid = grid;
        return area.Id;
    }

    /// <summary>The area inside a docked panel, made when it's first needed.</summary>
    public static LayoutArea InsideOf(DockedPanel panel) => panel.Inside ??= new LayoutArea();

    /// <summary>The area inside a panel filling an area, made when it's first needed.</summary>
    public static LayoutArea InsideOf(LayoutArea panel) => panel.Inside ??= new LayoutArea();

    /// <summary>
    /// Moves a docked panel or control to a place in its area's dock order: 0 is docked first and
    /// takes its full edge, the last fits in what the others leave. False if it isn't there.
    /// </summary>
    public static bool MoveDocked(LayoutArea area, DockedPanel panel, int index)
    {
        var from = area.Docked.IndexOf(panel);
        if (from < 0) return false;
        index = Math.Clamp(index, 0, area.Docked.Count - 1);
        if (index == from) return true;
        area.Docked.RemoveAt(from);
        area.Docked.Insert(index, panel);
        return true;
    }

    /// <summary>Where a panel, control, client or grid sits: docked to an edge, or full. Null for anything else.</summary>
    public static DockPlace? PlaceOf(LayoutArea root, string id)
    {
        if (FindDocked(root, id) is { } docked) return (DockPlace)docked.Panel.Edge;
        // A grid's cell stays where it is: a grid is made of its cells.
        if (FindGridHolder(root, id) != null) return null;
        return FindArea(root, id)?.Content is AreaContent.Panel or AreaContent.Control or AreaContent.Grid or AreaContent.Client ? DockPlace.Full : null;
    }

    /// <summary>
    /// Whether it can go there. Full only takes an area's middle when nothing else is in it; a client,
    /// a panel or a grid already there is never replaced from here.
    /// </summary>
    public static bool CanPlace(LayoutArea root, string id, DockPlace place)
    {
        if (PlaceOf(root, id) is not { } now) return false;
        if (place == now || place != DockPlace.Full) return true;
        return FindDocked(root, id) is { } docked && docked.Area.Content == AreaContent.Empty;
    }

    /// <summary>
    /// Docks a panel, control, client or grid to another edge, or makes it full, in the same area.
    /// Returns the id it is selected by afterwards, or null if it can't go there.
    ///
    /// <para>⚠️ It keeps its id, and what it holds goes with it, ids and all: a client's id is what
    /// its characters are assigned by, and a Tool Container's is what menus open tools in. Made full,
    /// the area takes its id; docked from full, it takes the area's and the area gets a new one.</para>
    /// </summary>
    public static string? Redock(LayoutArea root, string id, DockPlace place)
    {
        if (!CanPlace(root, id, place)) return null;
        if (PlaceOf(root, id) == place) return id;

        if (FindDocked(root, id) is { } docked)
        {
            var (area, panel) = docked;
            if (place != DockPlace.Full)
            {
                var edge = (DockEdge)place;
                // A control turned the other way starts at the size that suits it there; anything else keeps its own.
                var across = (edge is DockEdge.Top or DockEdge.Bottom) != (panel.Edge is DockEdge.Top or DockEdge.Bottom);
                if (across && panel.Grid == null && PanelControls.Parse(panel.Control) is { } kind) panel.Size = PanelControls.DefaultSize(kind, edge);
                panel.Edge = edge;
                return panel.Id;
            }

            area.Docked.Remove(panel);
            area.Id = panel.Id;
            area.Name = panel.Name;
            if (panel.Grid != null)
            {
                area.Content = AreaContent.Grid;
                area.Grid = panel.Grid;
            }
            else if (PanelControls.IsClient(panel.Control))
            {
                area.Content = AreaContent.Client;
            }
            else if (panel.Control != null)
            {
                area.Content = AreaContent.Control;
                area.Control = panel.Control;
                area.Options = panel.Options;
            }
            else
            {
                area.Content = AreaContent.Panel;
                area.Inside = panel.Inside;
                area.Look = panel.Look;
            }
            return area.Id;
        }

        if (FindArea(root, id) is not { } full) return null;
        var to = (DockEdge)place;
        var moved = new DockedPanel { Id = full.Id, Edge = to, Name = full.Name };
        switch (full.Content)
        {
            case AreaContent.Control:
                moved.Control = full.Control;
                moved.Options = full.Options;
                moved.Size = PanelControls.Parse(full.Control) is { } kind ? PanelControls.DefaultSize(kind, to) : 100;
                break;
            case AreaContent.Client:
                moved.Control = nameof(PanelControlKind.Client);
                moved.Size = PanelControls.DefaultSize(PanelControlKind.Client, to);
                break;
            case AreaContent.Panel:
                moved.Inside = full.Inside;
                moved.Look = full.Look;
                moved.Size = 100;
                break;
            default:
                moved.Grid = full.Grid;
                moved.Size = 300;
                break;
        }
        full.Id = Guid.NewGuid().ToString("N");
        full.Docked.Add(moved);
        full.Content = AreaContent.Empty;
        full.Name = "";
        full.Control = null;
        full.Options = null;
        full.Inside = null;
        full.Look = null;
        full.Grid = null;
        return moved.Id;
    }
    /// <summary>
    /// What dragging the thing with this id moves: the thing itself, or for a grid's cell — which
    /// can't leave its grid — the whole grid. So a grid on the drawing is dragged by any of its cells.
    /// </summary>
    public static string DraggedBy(LayoutArea root, string id) => FindGridHolder(root, id) ?? id;

    /// <summary>Where something can be moved to that isn't a panel: the monitor itself, which takes panels only.</summary>
    public const string MonitorTarget = "monitor";

    /// <summary>
    /// Whether the thing with this id — a panel, control, client or grid, docked or full — can be moved
    /// into the panel with <paramref name="targetId"/>, or onto the monitor (<see cref="MonitorTarget"/>).
    /// Not a grid's cell, which is part of its grid; not into itself or anything it holds; not into the
    /// panel it's already in; only panels onto the monitor; and not past the cap on nested grids.
    /// </summary>
    public static bool CanMove(LayoutArea root, string id, string targetId)
    {
        if (id == targetId || FindGridHolder(root, id) != null) return false;
        var (docked, full) = (FindDocked(root, id), FindArea(root, id));
        if (docked == null && full is not { Content: AreaContent.Panel or AreaContent.Control or AreaContent.Client or AreaContent.Grid }) return false;

        if (Target(root, targetId) is not { } target) return false;
        if (targetId == MonitorTarget)
        {
            var isPanel = docked is { } d ? d.Panel.Control == null && d.Panel.Grid == null : full!.Content == AreaContent.Panel;
            if (!isPanel) return false;
        }

        // Already there, or into what it holds.
        if (docked is { } inArea && ReferenceEquals(inArea.Area, target.Inside)) return false;
        if (docked == null && full!.Content != AreaContent.Panel && ReferenceEquals(full, target.Inside)) return false;
        if (ParentOf(root, id) == targetId) return false;
        var held = docked is { } x ? Subtree(x.Panel) : [full!];
        if (held.Any(h => FindArea(h, targetId) != null || FindDocked(h, targetId) != null)) return false;

        // The grids it holds, under the grids where it's going.
        var depth = docked is { } g ? (g.Panel.Grid is { } grid ? GridDepthOf(grid) : g.Panel.Inside is { } i ? ClientLayoutCalculator.GridDepth(i) : 0)
            : ClientLayoutCalculator.GridDepth(full!);
        return target.GridsAbove + depth <= ClientLayoutCalculator.MaxGridDepth;
    }

    /// <summary>
    /// Moves a panel, control, client or grid into another panel, or a panel onto the monitor. Docked,
    /// it's docked there on the same edge at the same size, last in order; full, it fills the new
    /// panel if nothing does, and is docked to the top otherwise. Returns the id it's selected by, or
    /// null if it can't go there (see <see cref="CanMove"/>).
    ///
    /// <para>⚠️ It keeps its id, and what it holds goes with it, ids and all, as <see cref="Redock"/>
    /// does: a client's characters stay assigned wherever it's moved.</para>
    /// </summary>
    public static string? Move(LayoutArea root, string id, string targetId)
    {
        if (!CanMove(root, id, targetId) || Target(root, targetId) is not { } target) return null;

        // Taken out of where it was, as a docked thing.
        DockedPanel moving;
        var wasFull = false;
        if (FindDocked(root, id) is { } docked)
        {
            docked.Area.Docked.Remove(docked.Panel);
            moving = docked.Panel;
        }
        else
        {
            var full = FindArea(root, id)!;
            wasFull = true;
            moving = new DockedPanel { Id = full.Id, Edge = DockEdge.Top, Name = full.Name };
            switch (full.Content)
            {
                case AreaContent.Control:
                    moving.Control = full.Control;
                    moving.Options = full.Options;
                    break;
                case AreaContent.Client:
                    moving.Control = nameof(PanelControlKind.Client);
                    break;
                case AreaContent.Panel:
                    moving.Inside = full.Inside;
                    moving.Look = full.Look;
                    break;
                default:
                    moving.Grid = full.Grid;
                    break;
            }
            moving.Size = moving.Grid != null ? 300
                : PanelControls.Parse(moving.Control) is { } kind ? PanelControls.DefaultSize(kind, DockEdge.Top) : 100;
            full.Id = Guid.NewGuid().ToString("N");
            full.Content = AreaContent.Empty;
            full.Name = "";
            full.Control = null;
            full.Options = null;
            full.Inside = null;
            full.Look = null;
            full.Grid = null;
        }

        // Put where it's going: full again if it was and there's room, docked otherwise.
        var into = target.Make();
        into.Docked.Add(moving);
        if (wasFull && into.Content == AreaContent.Empty) return Redock(root, moving.Id, DockPlace.Full);
        return moving.Id;
    }

    /// <summary>
    /// Removes the thing with this id, as its menu's Remove does: a docked panel, control, client or
    /// grid, or one filling an area. A grid's cell stays, being part of its grid, and so does the
    /// monitor. Returns what to select afterwards — what it was in — or null if nothing was removed.
    /// </summary>
    public static string? Remove(LayoutArea root, string id, out bool removed)
    {
        removed = false;
        if (FindGridHolder(root, id) != null) return id;
        var parent = ParentOf(root, id);
        if (FindDocked(root, id) is { } docked)
        {
            docked.Area.Docked.Remove(docked.Panel);
            removed = true;
            return parent;
        }
        if (FindArea(root, id) is not { Content: not AreaContent.Empty } area || (ReferenceEquals(area, root) && area.Content != AreaContent.Panel)) return id;
        SetContent(area, AreaContent.Empty);
        area.Inside = null;
        area.Look = null;
        area.Name = "";
        removed = true;
        return parent;
    }

    /// <summary>
    /// Where a move into this target goes: the panel's inside as it is now (null if it has none yet),
    /// a way to make it when the move happens, and the grids it's under.
    /// ⚠️ Looking never makes the inside: the drag asks on every movement, and an inside made for a
    /// panel only passed over would change the layout, and leave a step to undo, for nothing.
    /// </summary>
    private sealed record MoveTarget(LayoutArea? Inside, Func<LayoutArea> Make, int GridsAbove);

    private static MoveTarget? Target(LayoutArea root, string targetId)
    {
        if (targetId == MonitorTarget) return new(root, () => root, 0);
        if (FindDocked(root, targetId) is { Panel: { Control: null, Grid: null } panel } docked)
            return new(panel.Inside, () => InsideOf(panel), GridsAbove(root, docked.Area.Id));
        if (FindArea(root, targetId) is { Content: AreaContent.Panel } area)
            return new(area.Inside, () => InsideOf(area), GridsAbove(root, area.Id));
        return null;
    }

    /// <summary>The areas something docked holds: its inside, or its grid's cells.</summary>
    private static List<LayoutArea> Subtree(DockedPanel panel)
    {
        var held = new List<LayoutArea>();
        if (panel.Inside != null) held.Add(panel.Inside);
        if (panel.Grid != null) held.AddRange(panel.Grid.Cells);
        return held;
    }

    private static int GridDepthOf(LayoutGrid grid) =>
        1 + (grid.Cells.Count == 0 ? 0 : grid.Cells.Max(ClientLayoutCalculator.GridDepth));
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
