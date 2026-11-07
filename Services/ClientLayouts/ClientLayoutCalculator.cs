namespace EveConsole.Services.ClientLayouts;

/// <summary>A place a game client goes, with the id of the area that assignments refer to.</summary>
public readonly record struct PlacedClient(string AreaId, LayoutRect Rect);

/// <summary>
/// A top-level panel's place on screen, which becomes one window. <see cref="Id"/> is the docked
/// panel's or the area's. <see cref="Parts"/> is everything inside it, parents before children.
/// </summary>
public readonly record struct PlacedPanel(string Id, string Name, LayoutRect Rect, PanelLook? Look = null, IReadOnlyList<PlacedPart>? Parts = null);

public enum PlacedPartKind { Panel, Control, EmptyCell, Client }

/// <summary>
/// Something inside a top-level panel, in real screen pixels like everything else.
/// <see cref="Bare"/>: a panel holding nothing, which shows its name and size instead.
/// </summary>
public readonly record struct PlacedPart(PlacedPartKind Kind, string Id, string Name, LayoutRect Rect, PanelLook? Look = null, string? Control = null, bool Bare = false, ControlOptions? Options = null);

public enum LayoutProblemKind
{
    /// <summary>The panels docked in an area don't leave its middle any room.</summary>
    PanelsDontFit,

    /// <summary>A client position is usable, but below the size the game is meant to be played at.</summary>
    SmallClient,

    /// <summary>Grids nested past <see cref="ClientLayoutCalculator.MaxGridDepth"/>; the deeper ones are ignored.</summary>
    TooDeep,

    /// <summary>A grid whose cell count doesn't match its columns and rows; the missing cells are empty.</summary>
    BadGrid,
}

public readonly record struct LayoutProblem(LayoutProblemKind Kind, string AreaId, LayoutRect Rect);

/// <summary>An area with nothing in its middle, which the editor still has to draw and let you click.</summary>
public readonly record struct PlacedEmpty(string AreaId, LayoutRect Rect);

/// <summary>A grid's place on screen, which the editor needs to turn a dragged line into a share.</summary>
public readonly record struct PlacedGrid(string AreaId, LayoutRect Rect);

public sealed class ClientLayoutResult
{
    public List<PlacedClient> Clients { get; } = [];
    public List<PlacedPanel> Panels { get; } = [];
    public List<PlacedEmpty> Empties { get; } = [];
    public List<PlacedGrid> Grids { get; } = [];
    public List<LayoutProblem> Problems { get; } = [];
}

/// <summary>Turns a layout into exact screen rectangles for one monitor.</summary>
public static class ClientLayoutCalculator
{
    /// <summary>The user's cap: grids can nest this many levels.</summary>
    public const int MaxGridDepth = 5;

    /// <summary>
    /// Below this, the editor warns about a client position. The client accepted far less when
    /// tested (400x300), but its UI starts scaling down once it gets very small.
    /// </summary>
    public const int SmallClientWidth = 800, SmallClientHeight = 600;

    public static ClientLayoutResult Calculate(LayoutArea root, LayoutRect space)
    {
        var result = new ClientLayoutResult();
        Place(root, space, 0, result, null, false);
        return result;
    }

    /// <summary>How many levels of grid this area holds at its deepest, counting through panels.</summary>
    public static int GridDepth(LayoutArea area)
    {
        var deepest = area.Inside == null ? 0 : GridDepth(area.Inside);
        foreach (var panel in area.Docked)
            if (panel.Inside != null) deepest = Math.Max(deepest, GridDepth(panel.Inside));
        if (area.Content == AreaContent.Grid && area.Grid != null)
            deepest = Math.Max(deepest, 1 + (area.Grid.Cells.Count == 0 ? 0 : area.Grid.Cells.Max(GridDepth)));
        return deepest;
    }

    /// <summary>The ids of every client position in a layout, in reading order.</summary>
    public static IEnumerable<string> ClientAreaIds(LayoutArea area)
    {
        foreach (var panel in area.Docked)
            if (panel.Inside != null)
                foreach (var id in ClientAreaIds(panel.Inside))
                    yield return id;

        if (area.Content == AreaContent.Client) yield return area.Id;
        if (area.Content == AreaContent.Panel && area.Inside != null)
            foreach (var id in ClientAreaIds(area.Inside))
                yield return id;
        if (area.Content != AreaContent.Grid || area.Grid == null) yield break;
        foreach (var cell in area.Grid.Cells)
            foreach (var id in ClientAreaIds(cell))
                yield return id;
    }

    /// <param name="host">The parts of the top-level panel this is inside, or null if it isn't in one.</param>
    /// <param name="isCell">It's a grid cell, so it's a thing of its own even when empty.</param>
    private static void Place(LayoutArea area, LayoutRect space, int depth, ClientLayoutResult result, List<PlacedPart>? host, bool isCell)
    {
        var middle = TakeDocked(area, space, depth, result, host);
        switch (area.Content)
        {
            case AreaContent.Client:
                result.Clients.Add(new PlacedClient(area.Id, middle));
                host?.Add(new PlacedPart(PlacedPartKind.Client, area.Id, "", middle));
                if (middle.Width < SmallClientWidth || middle.Height < SmallClientHeight)
                    result.Problems.Add(new LayoutProblem(LayoutProblemKind.SmallClient, area.Id, middle));
                break;

            case AreaContent.Panel:
                PlacePanel(area.Id, area.Name, area.Look, area.Inside, middle, depth, result, host);
                break;

            case AreaContent.Control when host != null:
                host.Add(new PlacedPart(PlacedPartKind.Control, area.Id, area.Name, middle, null, area.Control, false, area.Options));
                break;

            case AreaContent.Control:
                result.Panels.Add(new PlacedPanel(area.Id, "", middle, null, [new PlacedPart(PlacedPartKind.Control, area.Id, area.Name, middle, null, area.Control, false, area.Options)]));
                break;

            case AreaContent.Grid when area.Grid != null:
                if (depth >= MaxGridDepth)
                {
                    result.Problems.Add(new LayoutProblem(LayoutProblemKind.TooDeep, area.Id, middle));
                    Nothing(area, middle, result, host, isCell);
                    break;
                }
                result.Grids.Add(new PlacedGrid(area.Id, middle));
                PlaceGrid(area, area.Grid, middle, depth, result, host);
                break;

            default:
                Nothing(area, middle, result, host, isCell);
                break;
        }
    }

    /// <summary>The middle of a panel with nothing in it is just more of that panel.</summary>
    private static void Nothing(LayoutArea area, LayoutRect middle, ClientLayoutResult result, List<PlacedPart>? host, bool isCell)
    {
        if (host == null) result.Empties.Add(new PlacedEmpty(area.Id, middle));
        else if (isCell) host.Add(new PlacedPart(PlacedPartKind.EmptyCell, area.Id, "", middle));
    }

    private static void PlacePanel(string id, string name, PanelLook? look, LayoutArea? inside, LayoutRect rect, int depth, ClientLayoutResult result, List<PlacedPart>? host)
    {
        var bare = inside == null || (inside.Docked.Count == 0 && inside.Content == AreaContent.Empty);
        if (host == null)
        {
            var parts = new List<PlacedPart>();
            result.Panels.Add(new PlacedPanel(id, name, rect, look, parts));
            host = parts;
        }
        else
        {
            host.Add(new PlacedPart(PlacedPartKind.Panel, id, name, rect, look, null, bare));
        }
        if (inside != null) Place(inside, rect, depth, result, host, false);
    }

    /// <summary>What's docked, in order, each taking its edge of whatever is left. Returns the middle.</summary>
    private static LayoutRect TakeDocked(LayoutArea area, LayoutRect space, int depth, ClientLayoutResult result, List<PlacedPart>? host)
    {
        var left = space;
        var ran = false;
        foreach (var panel in area.Docked)
        {
            var along = panel.Edge is DockEdge.Top or DockEdge.Bottom ? left.Height : left.Width;
            var size = Math.Clamp(panel.Size, 0, along);
            if (size < panel.Size) ran = true;
            LayoutRect rect;
            switch (panel.Edge)
            {
                case DockEdge.Top:
                    rect = new LayoutRect(left.X, left.Y, left.Width, size);
                    left = new LayoutRect(left.X, left.Y + size, left.Width, left.Height - size);
                    break;
                case DockEdge.Bottom:
                    rect = new LayoutRect(left.X, left.Bottom - size, left.Width, size);
                    left = new LayoutRect(left.X, left.Y, left.Width, left.Height - size);
                    break;
                case DockEdge.Left:
                    rect = new LayoutRect(left.X, left.Y, size, left.Height);
                    left = new LayoutRect(left.X + size, left.Y, left.Width - size, left.Height);
                    break;
                default:
                    rect = new LayoutRect(left.Right - size, left.Y, size, left.Height);
                    left = new LayoutRect(left.X, left.Y, left.Width - size, left.Height);
                    break;
            }
            if (panel.Control == null)
                PlacePanel(panel.Id, panel.Name, panel.Look, panel.Inside, rect, depth, result, host);
            else if (host != null)
                host.Add(new PlacedPart(PlacedPartKind.Control, panel.Id, panel.Name, rect, null, panel.Control, false, panel.Options));
            else
                result.Panels.Add(new PlacedPanel(panel.Id, "", rect, null, [new PlacedPart(PlacedPartKind.Control, panel.Id, panel.Name, rect, null, panel.Control, false, panel.Options)]));
        }
        if (ran || (area.Docked.Count > 0 && left.IsEmpty && !space.IsEmpty))
            result.Problems.Add(new LayoutProblem(LayoutProblemKind.PanelsDontFit, area.Id, left));
        return left;
    }

    private static void PlaceGrid(LayoutArea area, LayoutGrid grid, LayoutRect space, int depth, ClientLayoutResult result, List<PlacedPart>? host)
    {
        var columns = Math.Max(grid.Columns.Count, 1);
        var rows = Math.Max(grid.Rows.Count, 1);
        if (grid.Cells.Count != columns * rows)
            result.Problems.Add(new LayoutProblem(LayoutProblemKind.BadGrid, area.Id, space));

        var xs = Edges(grid.Columns, columns, space.X, space.Width);
        var ys = Edges(grid.Rows, rows, space.Y, space.Height);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= grid.Cells.Count) continue;
                var rect = new LayoutRect(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]);
                Place(grid.Cells[index], rect, depth + 1, result, host, true);
            }
        }
    }

    /// <summary>
    /// Cell edges from proportions. Each edge is rounded from the running total rather than each
    /// cell from its own share, so the cells always meet exactly and fill the space, whatever the
    /// rounding does. A share that isn't a positive number counts as nothing; if all of them are
    /// like that, the cells are equal.
    /// </summary>
    public static int[] Edges(List<double> shares, int count, int start, int length)
    {
        var clean = new double[count];
        for (var i = 0; i < count; i++)
        {
            var share = i < shares.Count ? shares[i] : 0;
            clean[i] = double.IsFinite(share) && share > 0 ? share : 0;
        }
        var total = clean.Sum();
        if (total <= 0)
        {
            Array.Fill(clean, 1.0);
            total = count;
        }

        var edges = new int[count + 1];
        var running = 0.0;
        for (var i = 0; i < count; i++)
        {
            edges[i] = start + (int)Math.Round(length * running / total, MidpointRounding.AwayFromZero);
            running += clean[i];
        }
        edges[count] = start + length;
        return edges;
    }
}
