// Checks the client layout rules with constructed layouts and windows. No game, no window and no
// database is involved. A wrong answer here would move a client someone is flying, or put panels
// over a game client, and either would look like a working feature in a quick try.
using EveConsole.Services.ClientLayouts;

var failures = 0;
var passes = 0;

void Check(bool ok, string what)
{
    if (ok) passes++;
    else { failures++; Console.WriteLine("FAILED: " + what); }
}

void Same<T>(T actual, T wanted, string what) => Check(Equals(actual, wanted), $"{what}: got {actual}, wanted {wanted}");

var monitor4K = new LayoutRect(0, 0, 3840, 2112);

// ---- Docking and corners ----
{
    var area = new LayoutArea { Content = AreaContent.Client };
    area.Docked.Add(new DockedPanel { Edge = DockEdge.Top, Size = 100 });
    area.Docked.Add(new DockedPanel { Edge = DockEdge.Bottom, Size = 100 });
    var result = ClientLayoutCalculator.Calculate(area, monitor4K);
    Same(result.Clients[0].Rect, new LayoutRect(0, 100, 3840, 1912), "top and bottom leave the middle");
    Same(result.Panels[0].Rect, new LayoutRect(0, 0, 3840, 100), "top panel");
    Same(result.Panels[1].Rect, new LayoutRect(0, 2012, 3840, 100), "bottom panel");
}
{
    // The first docked owns the corner.
    var area = new LayoutArea { Content = AreaContent.Client };
    var top = ClientLayoutEditing.Dock(area, DockEdge.Top, 100, "top");
    var left = ClientLayoutEditing.Dock(area, DockEdge.Left, 200, "left");
    var result = ClientLayoutCalculator.Calculate(area, monitor4K);
    Same(result.Panels[0].Rect, new LayoutRect(0, 0, 3840, 100), "top docked first has the full width");
    Same(result.Panels[1].Rect, new LayoutRect(0, 100, 200, 2012), "left docked next fits below it");

    // Send to Back makes it as if docked first.
    ClientLayoutEditing.SendToBack(area, left);
    result = ClientLayoutCalculator.Calculate(area, monitor4K);
    Same(result.Panels[0].Id, left.Id, "send to back is first in order");
    Same(result.Panels[0].Rect, new LayoutRect(0, 0, 200, 2112), "left sent to back has the full height");
    Same(result.Panels[1].Rect, new LayoutRect(200, 0, 3640, 100), "top then fits beside it");
    Same(result.Clients[0].Rect, new LayoutRect(200, 100, 3640, 2012), "the middle is the same size either way");

    // Bring to Front makes it last.
    ClientLayoutEditing.BringToFront(area, left);
    result = ClientLayoutCalculator.Calculate(area, monitor4K);
    Same(result.Panels[1].Id, left.Id, "bring to front is last in order");
    Same(top.Id, result.Panels[0].Id, "and the other panel is first again");
}
{
    // Right and bottom edges, and several on one edge: the earliest sits closest to the edge.
    var area = new LayoutArea { Content = AreaContent.Empty };
    ClientLayoutEditing.Dock(area, DockEdge.Right, 100, "a");
    ClientLayoutEditing.Dock(area, DockEdge.Right, 50, "b");
    ClientLayoutEditing.Dock(area, DockEdge.Bottom, 10, "c");
    var result = ClientLayoutCalculator.Calculate(area, new LayoutRect(10, 20, 1000, 500));
    Same(result.Panels[0].Rect, new LayoutRect(910, 20, 100, 500), "first on the right is at the edge");
    Same(result.Panels[1].Rect, new LayoutRect(860, 20, 50, 500), "second stacks inside it");
    Same(result.Panels[2].Rect, new LayoutRect(10, 510, 850, 10), "bottom fits in what is left");
    Same(result.Empties[0].Rect, new LayoutRect(10, 20, 850, 490), "the empty middle is reported");
}
{
    // Panels that don't fit are clamped and reported, and never go negative.
    var area = new LayoutArea { Content = AreaContent.Client };
    ClientLayoutEditing.Dock(area, DockEdge.Top, 5000, "big");
    ClientLayoutEditing.Dock(area, DockEdge.Bottom, 100, "next");
    var result = ClientLayoutCalculator.Calculate(area, monitor4K);
    Same(result.Panels[0].Rect, monitor4K, "too big a panel stops at the space");
    Same(result.Panels[1].Rect.Height, 0, "the next has no room");
    Same(result.Clients[0].Rect.Height, 0, "nor has the middle");
    Check(result.Problems.Any(p => p.Kind == LayoutProblemKind.PanelsDontFit), "panels that don't fit are reported");
}

// ---- Grids ----
{
    // The 4K example: 100 px strips, 2 x 2, a 40 px left panel in one cell.
    var area = new LayoutArea { Content = AreaContent.Grid, Grid = LayoutGrid.Equal(2, 2) };
    area.Docked.Add(new DockedPanel { Edge = DockEdge.Top, Size = 100 });
    area.Docked.Add(new DockedPanel { Edge = DockEdge.Bottom, Size = 100 });
    foreach (var cell in area.Grid.Cells) cell.Content = AreaContent.Client;
    ClientLayoutEditing.Dock(area.Grid.Cells[3], DockEdge.Left, 40, "cell panel");
    var result = ClientLayoutCalculator.Calculate(area, monitor4K);
    Same(result.Clients[0].Rect, new LayoutRect(0, 100, 1920, 956), "cell 1");
    Same(result.Clients[1].Rect, new LayoutRect(1920, 100, 1920, 956), "cell 2, across first");
    Same(result.Clients[2].Rect, new LayoutRect(0, 1056, 1920, 956), "cell 3, then down");
    Same(result.Clients[3].Rect, new LayoutRect(1960, 1056, 1880, 956), "cell 4 less its panel");
    Same(result.Panels.Last().Rect, new LayoutRect(1920, 1056, 40, 956), "the panel in the cell");
    Same(result.Grids[0].Rect, new LayoutRect(0, 100, 3840, 1912), "the grid fills what the strips leave");
}
{
    // The nested example: 2 x 1 with a 1 x 2 in the left cell.
    var area = new LayoutArea { Content = AreaContent.Grid, Grid = LayoutGrid.Equal(2, 1) };
    var inner = area.Grid.Cells[0];
    inner.Content = AreaContent.Grid;
    inner.Grid = LayoutGrid.Equal(1, 2);
    inner.Grid.Cells[0].Content = AreaContent.Client;
    inner.Grid.Cells[1].Content = AreaContent.Panel;
    area.Grid.Cells[1].Content = AreaContent.Client;
    var result = ClientLayoutCalculator.Calculate(area, monitor4K);
    Same(result.Clients.Count, 2, "two client positions");
    Same(result.Clients[0].Rect, new LayoutRect(0, 0, 1920, 1056), "top left");
    Same(result.Panels[0].Rect, new LayoutRect(0, 1056, 1920, 1056), "a panel filling a cell");
    Same(result.Clients[1].Rect, new LayoutRect(1920, 0, 1920, 2112), "the tall right one");
    Same(ClientLayoutCalculator.GridDepth(area), 2, "two levels of grid");
    Same(ClientLayoutCalculator.ClientAreaIds(area).Count(), 2, "client ids found through the nesting");
    Check(!result.Problems.Any(p => p.Kind == LayoutProblemKind.SmallClient), "these are not small");
}
{
    // Cells always meet exactly and fill the space, whatever the shares and the rounding do.
    foreach (var shares in new[] { new[] { 1.0, 1.0, 1.0 }, new[] { 1.0, 2.0, 3.0 }, new[] { 0.1, 0.7, 0.2 }, new[] { 7.0, 3.0, 11.0 } })
    {
        foreach (var length in new[] { 1000, 1001, 1439, 2560, 3839 })
        {
            var edges = ClientLayoutCalculator.Edges([.. shares], 3, 50, length);
            Same(edges[0], 50, "the first edge is the start");
            Same(edges[3], 50 + length, "the last edge is the end");
            Check(edges[0] < edges[1] && edges[1] < edges[2] && edges[2] < edges[3], "edges rise");
        }
    }
    Same(ClientLayoutCalculator.Edges([0, 0], 2, 0, 100)[1], 50, "all-bad shares are equal");
    Same(ClientLayoutCalculator.Edges([double.NaN, 1], 2, 0, 100)[1], 0, "a share that isn't a number counts as nothing");
    Same(ClientLayoutCalculator.Edges([-1, 1, 1], 3, 0, 100)[1], 0, "nor does a negative one");
    Same(ClientLayoutCalculator.Edges([1], 2, 0, 100)[1], 100, "a missing share counts as nothing");
}
{
    // A grid with too few cells still works, and says so.
    var area = new LayoutArea { Content = AreaContent.Grid, Grid = LayoutGrid.Equal(2, 2) };
    area.Grid.Cells.RemoveAt(3);
    var result = ClientLayoutCalculator.Calculate(area, monitor4K);
    Check(result.Problems.Any(p => p.Kind == LayoutProblemKind.BadGrid), "a grid short of cells is reported");
    Same(result.Panels.Count, 3, "with the cells it has, each a panel");
}
{
    // A small client position warns, at exactly the limit it doesn't.
    var at = new LayoutArea { Content = AreaContent.Client };
    Check(!ClientLayoutCalculator.Calculate(at, new LayoutRect(0, 0, 800, 600)).Problems.Any(), "800x600 doesn't warn");
    Check(ClientLayoutCalculator.Calculate(at, new LayoutRect(0, 0, 799, 600)).Problems.Any(p => p.Kind == LayoutProblemKind.SmallClient), "799 wide warns");
    Check(ClientLayoutCalculator.Calculate(at, new LayoutRect(0, 0, 800, 599)).Problems.Any(p => p.Kind == LayoutProblemKind.SmallClient), "599 tall warns");

    // Small panels don't warn: that's what small areas are for.
    var panel = new LayoutArea { Content = AreaContent.Panel };
    Check(!ClientLayoutCalculator.Calculate(panel, new LayoutRect(0, 0, 100, 100)).Problems.Any(), "a small panel doesn't warn");
}

// ---- Depth cap ----
{
    var root = new LayoutArea();
    var area = root;
    var made = 0;
    while (ClientLayoutEditing.Split(root, area, 2, 1))
    {
        made++;
        area = area.Grid!.Cells[1];
        if (made > 20) break;
    }
    Same(made, ClientLayoutCalculator.MaxGridDepth, "splitting stops at the cap");
    Same(ClientLayoutCalculator.MaxGridDepth, 5, "the cap is 5");
    Same(ClientLayoutCalculator.GridDepth(root), 5, "depth counted");
    Check(!ClientLayoutCalculator.Calculate(root, monitor4K).Problems.Any(), "the cap itself is allowed");

    // A deeper tree, say one shared from elsewhere, is reported and its excess ignored.
    area.Content = AreaContent.Grid;
    area.Grid = LayoutGrid.Equal(2, 1);
    area.Grid.Cells[0].Content = AreaContent.Client;
    var result = ClientLayoutCalculator.Calculate(root, monitor4K);
    Check(result.Problems.Any(p => p.Kind == LayoutProblemKind.TooDeep), "past the cap is reported");
    Check(result.Clients.Count == 0, "past the cap is not placed");
}

// ---- Editing ----
{
    // Splitting keeps a client position's id, so its characters stay.
    var root = new LayoutArea { Content = AreaContent.Client };
    var id = root.Id;
    var dock = ClientLayoutEditing.Dock(root, DockEdge.Top, 100, "top");
    Check(ClientLayoutEditing.Split(root, root, 2, 2), "split works");
    Same(root.Grid!.Cells[0].Id, id, "the first cell takes the old id");
    Same(root.Grid.Cells[0].Content, AreaContent.Client, "and the client");
    Check(root.Id != id, "the outer area gets a new id");
    Same(root.Docked[0], dock, "docked panels stay outside");
    Same(ClientLayoutEditing.FindArea(root, id), root.Grid.Cells[0], "found by id");
    Same(ClientLayoutEditing.FindGridOwner(root, id), root, "its owner found");
    Same(ClientLayoutEditing.GridsAbove(root, id), 1, "one grid above");
    Same(ClientLayoutEditing.FindDocked(root, dock.Id)?.Panel, dock, "docked panel found");

    Check(!ClientLayoutEditing.Split(root, root.Grid.Cells[1], 1, 1), "a 1 x 1 split means nothing");
    Check(!ClientLayoutEditing.Split(root, root.Grid.Cells[1], 0, 2), "nor a grid with no columns");

    ClientLayoutEditing.SetContent(root.Grid.Cells[1], AreaContent.Panel);
    Same(root.Grid.Cells[1].Content, AreaContent.Panel, "content set");
    ClientLayoutEditing.SetContent(root.Grid.Cells[1], AreaContent.Grid);
    Same(root.Grid.Cells[1].Content, AreaContent.Panel, "a grid only comes from a split");

    ClientLayoutEditing.RemoveGrid(root);
    Same(root.Content, AreaContent.Empty, "removing a grid leaves it empty");
    Check(root.Grid == null, "and it's gone");
}
{
    // Dragging a line moves only its two neighbours and keeps their total.
    var shares = new List<double> { 1, 1, 1, 1 };
    ClientLayoutEditing.MoveGridLine(shares, 1, 600, 0, 1000);
    Same(Math.Round(shares[0], 6), 1.0, "the far left stays");
    Same(Math.Round(shares[3], 6), 1.0, "the far right stays");
    Same(Math.Round(shares[1], 6), 1.4, "the left neighbour grows");
    Same(Math.Round(shares[2], 6), 0.6, "the right neighbour shrinks");

    ClientLayoutEditing.MoveGridLine(shares, 1, 5000, 0, 1000);
    Check(shares[2] > 0, "a line can't go past its neighbour");
    ClientLayoutEditing.MoveGridLine(shares, 1, -5000, 0, 1000);
    Check(shares[1] > 0, "in either direction");
    Same(Math.Round(shares.Sum(), 6), 4.0, "the total never changes");

    var before = shares.ToList();
    ClientLayoutEditing.MoveGridLine(shares, 3, 500, 0, 1000);
    Check(shares.SequenceEqual(before), "a line past the last does nothing");
}
{
    var panel = new LayoutRect(100, 100, 200, 50);
    Same(ClientLayoutEditing.DockSizeFromDrag(DockEdge.Top, panel, 0, 180), 80, "top drag");
    Same(ClientLayoutEditing.DockSizeFromDrag(DockEdge.Bottom, panel, 0, 120), 30, "bottom drag");
    Same(ClientLayoutEditing.DockSizeFromDrag(DockEdge.Left, panel, 170, 0), 70, "left drag");
    Same(ClientLayoutEditing.DockSizeFromDrag(DockEdge.Right, panel, 250, 0), 50, "right drag");
    Same(ClientLayoutEditing.DockSizeFromDrag(DockEdge.Top, panel, 0, 0), 1, "never below one");
}

// ---- Stored form ----
{
    // As the editor makes them now: a panel on the monitor, a grid of panels in it.
    var root = ClientLayoutEditing.NewRoot();
    root.Inside!.Content = AreaContent.Empty;
    ClientLayoutEditing.DockGrid(root, root.Inside, DockPlace.Full, 2, 1);
    var grid = root.Inside.Grid!;
    grid.Cells[0].Inside = new LayoutArea { Content = AreaContent.Client };
    grid.Cells[1].Name = "Map";
    grid.Columns[0] = 2.5;
    ClientLayoutEditing.Dock(root, DockEdge.Right, 77, "side");
    var json = ClientLayoutJson.Write(root);
    var back = ClientLayoutJson.Read(json);
    Same(ClientLayoutJson.Write(back), json, "a layout made now reads back unchanged");
    Same(back.Id, root.Id, "ids survive storage");
    Same(back.Inside!.Grid!.Cells[0].Inside!.Id, grid.Cells[0].Inside!.Id, "client ids survive storage");
    Same(back.Inside.Grid.Cells[1].Name, "Map", "names survive storage");
    Same(back.Inside.Grid.Columns[0], 2.5, "shares survive storage");
    Same(back.Docked[0].Edge, DockEdge.Right, "edges survive storage");
    Same(back.Docked[0].Size, 77, "sizes survive storage");
    Same(ClientLayoutJson.Read("not json").Content, AreaContent.Empty, "a damaged layout reads as empty");
    Same(ClientLayoutJson.Read(null).Content, AreaContent.Empty, "so does a missing one");
    Same(AppliedDesktop.Read("not json").Monitors.Count, 0, "a damaged applied state reads as nothing");
    Check(ClientLayoutJson.Write(root).Contains("\"Right\""), "enums are stored by name, so reordering them can't change a layout");
}

// ---- The planner ----
const string Big = "big", Small = "small", Away = "away";
var bigBounds = new LayoutRect(0, 0, 3840, 2160);
var bigWork = new LayoutRect(0, 0, 3840, 2112);
var smallBounds = new LayoutRect(3840, -330, 2560, 1440);
var plugged = new List<DesktopMonitor>
{
    new(Big, bigBounds, bigWork, true),
    new(Small, smallBounds, new LayoutRect(3840, -330, 2560, 1392), false),
};

LayoutArea TwoClients(out string first, out string second)
{
    var area = new LayoutArea { Content = AreaContent.Grid, Grid = LayoutGrid.Equal(2, 1) };
    area.Grid.Cells[0].Content = AreaContent.Client;
    area.Grid.Cells[1].Content = AreaContent.Client;
    first = area.Grid.Cells[0].Id;
    second = area.Grid.Cells[1].Id;
    return area;
}

{
    var shared = new LayoutArea { Content = AreaContent.Client };
    ClientLayoutEditing.Dock(shared, DockEdge.Top, 100, "strip");
    var json = ClientLayoutJson.Write(shared);
    var applied = new AppliedDesktop
    {
        Monitors =
        [
            new AppliedMonitor { HardwareKey = Big, Definition = json, PanelStart = PanelStart.WithEve, Characters = { [shared.Id] = ["Alpha", "Bravo"] } },
            new AppliedMonitor { HardwareKey = Small, Definition = json, FullMonitor = true, Characters = { [shared.Id] = ["Alpha", "Charlie"] } },
            new AppliedMonitor { HardwareKey = Away, Definition = json, Characters = { [shared.Id] = ["Delta"] } },
        ],
    };
    var plan = ClientLayoutPlanner.Build(AppliedDesktop.Read(applied.Write()), plugged);

    Same(plan.Placement.Monitors.Count, 2, "a monitor not plugged in has no cells");
    Same(plan.Panels.Count, 2, "nor panels");
    Same(plan.Placement.Monitors[0].Cells[0].Rect, new LayoutRect(0, 100, 3840, 2012), "work area by default");
    Same(plan.Placement.Monitors[1].Cells[0].Rect, new LayoutRect(3840, -230, 2560, 1340), "full monitor when set, and one layout fits both");
    Same(plan.Panels[0].Start, PanelStart.WithEve, "panel start comes from the monitor");
    Same(plan.Panels[1].Start, PanelStart.WithConsole, "per monitor");
    Check(!plan.Panels[0].CoverTaskbar, "the taskbar stays in front by default");
    Check(plan.Panels[1].CoverTaskbar, "and moves behind on a monitor using all of itself");
    Same(plan.Placement.Positions["Bravo"].MonitorKey, Big, "a character's position");
    Same(plan.Placement.Positions["bravo"].MonitorKey, Big, "names match whatever the case");
    Same(plan.Placement.Positions["Alpha"].MonitorKey, Big, "one position per desktop, the first wins");
    Same(plan.Placement.Positions["Charlie"].MonitorKey, Small, "group members share the shape");
    Check(!plan.Placement.Positions.ContainsKey("Delta"), "a character on a missing monitor has no position");
}
{
    Check(ClientLayoutPlanner.PanelsShow(PanelStart.WithConsole, 0), "with the console shows with no client");
    Check(!ClientLayoutPlanner.PanelsShow(PanelStart.WithEve, 0), "with EVE waits");
    Check(ClientLayoutPlanner.PanelsShow(PanelStart.WithEve, 1), "the first client is enough");
    Check(ClientLayoutPlanner.PanelsShow(PanelStart.WithEve, 4), "and more");
}

// ---- What panels hold ----
{
    // A panel holding docked controls, a docked panel with a grid, and a control docked full in a cell.
    var root = new LayoutArea { Content = AreaContent.Client };
    var bar = ClientLayoutEditing.Dock(root, DockEdge.Top, 25, "bar");
    var inBar = ClientLayoutEditing.InsideOf(bar);
    var gear = ClientLayoutEditing.DockControl(inBar, PanelControlKind.Settings, DockEdge.Left);
    Same(gear.Size, PanelControls.DefaultSize(PanelControlKind.Settings, DockEdge.Left), "a control starts at a size that suits it");
    gear.Size = 40;
    var clock = ClientLayoutEditing.DockControl(inBar, PanelControlKind.EveTime, DockEdge.Right);
    clock.Size = 120;
    var side = ClientLayoutEditing.Dock(root, DockEdge.Right, 300, "side");
    var inSide = ClientLayoutEditing.InsideOf(side);
    var sub = ClientLayoutEditing.Dock(inSide, DockEdge.Top, 100, "sub");
    Check(ClientLayoutEditing.Split(root, inSide, 1, 2), "a panel can hold a grid");
    var filled = inSide.Grid!.Cells[1];
    ClientLayoutEditing.FillWithControl(filled, PanelControlKind.LocalTime);
    var emptyCell = inSide.Grid.Cells[0];

    var result = ClientLayoutCalculator.Calculate(root, monitor4K);
    Same(result.Panels.Count, 2, "panels at the top are the windows, and what's in them isn't");
    var barPlaced = result.Panels.Single(p => p.Id == bar.Id);
    var sidePlaced = result.Panels.Single(p => p.Id == side.Id);
    Same(barPlaced.Rect, new LayoutRect(0, 0, 3840, 25), "the bar takes its edge");
    Same(barPlaced.Parts!.Single(p => p.Id == gear.Id).Rect, new LayoutRect(0, 0, 40, 25), "a control docked left has its width and the bar's height");
    Same(barPlaced.Parts!.Single(p => p.Id == clock.Id).Rect, new LayoutRect(3720, 0, 120, 25), "docked right, from the other end");
    Same(barPlaced.Parts!.Single(p => p.Id == clock.Id).Control, "EveTime", "knowing which control it is");
    var subPlaced = sidePlaced.Parts!.Single(p => p.Id == sub.Id);
    Check(subPlaced.Kind == PlacedPartKind.Panel && subPlaced.Bare, "a panel in a panel, with nothing in it yet");
    Same(subPlaced.Rect, new LayoutRect(3540, 25, 300, 100), "fitted inside its parent");
    Same(sidePlaced.Parts!.Single(p => p.Id == filled.Id).Rect, new LayoutRect(3540, 1119, 300, 993), "a control docked full takes its whole cell");
    Check(sidePlaced.Parts!.Single(p => p.Id == emptyCell.Id).Kind == PlacedPartKind.EmptyCell, "an empty cell stays a thing to click");
    Same(result.Clients.Count, 1, "one client so far");
    Same(result.Clients[0].Rect, new LayoutRect(0, 25, 3540, 2087), "which has what's left");
    Check(result.Grids.Any(g => g.AreaId == inSide.Id), "a grid in a panel can have its lines dragged");

    // Parents, as someone working on the layout sees them.
    Same(ClientLayoutEditing.ParentOf(root, gear.Id), bar.Id, "a control's parent is its panel");
    Same(ClientLayoutEditing.ParentOf(root, bar.Id), root.Id, "a panel's is what it's docked on");
    Same(ClientLayoutEditing.ParentOf(root, filled.Id), inSide.Id, "a cell in a panel's grid has the grid as parent");
    Same(ClientLayoutEditing.ParentOf(root, inSide.Id), side.Id, "and the grid has the panel");
    Same(ClientLayoutEditing.ParentOf(root, root.Id), null, "the monitor has none");
    var full = new LayoutArea { Content = AreaContent.Panel };
    var fullInside = ClientLayoutEditing.InsideOf(full);
    ClientLayoutEditing.FillWithControl(fullInside, PanelControlKind.EveTime);
    Same(ClientLayoutEditing.ParentOf(full, fullInside.Id), full.Id, "a control docked full has its panel as parent");
    Check(ClientLayoutEditing.FindDocked(root, gear.Id) != null, "something docked deep inside can be found");

    // A client can go in any cell, including inside a panel.
    emptyCell.Content = AreaContent.Client;
    var bottom = ClientLayoutEditing.Dock(root, DockEdge.Bottom, 500, "holds a client");
    var inBottom = ClientLayoutEditing.InsideOf(bottom);
    inBottom.Content = AreaContent.Client;
    var withClients = ClientLayoutCalculator.Calculate(root, monitor4K);
    Same(withClients.Clients.Count, 3, "clients inside panels are clients like any other");
    Check(withClients.Clients.Any(c => c.AreaId == emptyCell.Id), "one in a cell of a panel's grid");
    Same(withClients.Clients.Single(c => c.AreaId == inBottom.Id).Rect, new LayoutRect(0, 1612, 3540, 500), "and one filling a docked panel");
    var holes = withClients.Panels.Single(p => p.Id == bottom.Id).Parts!.Where(p => p.Kind == PlacedPartKind.Client).ToList();
    Same(holes.Single().Rect, new LayoutRect(0, 1612, 3540, 500), "its panel knows where to leave a hole for it");
    Same(withClients.Panels.Single(p => p.Id == side.Id).Parts!.Count(p => p.Kind == PlacedPartKind.Client), 1, "as does the panel with one in a cell");
    Same(ClientLayoutCalculator.ClientAreaIds(root).Count(), 3, "and they can all have characters assigned");
    Same(ClientLayoutEditing.ParentOf(root, inBottom.Id), bottom.Id, "a client filling a panel has that panel as parent");
    Same(ClientLayoutEditing.ParentOf(root, emptyCell.Id), inSide.Id, "one in a cell has the grid as parent");
    root.Docked.Remove(bottom);
    emptyCell.Content = AreaContent.Empty;

    // The depth cap counts through panels.
    var deep = new LayoutArea();
    var walk = deep;
    for (var i = 0; i < ClientLayoutCalculator.MaxGridDepth; i++)
    {
        Check(ClientLayoutEditing.Split(deep, walk, 2, 1), "a level of grid " + (i + 1));
        var cell = walk.Grid!.Cells[0];
        var panel = ClientLayoutEditing.Dock(cell, DockEdge.Top, 10, "p");
        walk = ClientLayoutEditing.InsideOf(panel);
    }
    Check(!ClientLayoutEditing.CanSplit(deep, walk), "the cap holds through panels");

    // And through panels that fill a cell.
    var deepFull = new LayoutArea();
    var walkFull = deepFull;
    for (var i = 0; i < ClientLayoutCalculator.MaxGridDepth; i++)
    {
        Check(ClientLayoutEditing.Split(deepFull, walkFull, 2, 1), "a level of grid in a filled panel " + (i + 1));
        var cell = walkFull.Grid!.Cells[0];
        cell.Content = AreaContent.Panel;
        walkFull = ClientLayoutEditing.InsideOf(cell);
    }
    Check(!ClientLayoutEditing.CanSplit(deepFull, walkFull), "the cap holds through panels filling cells");
    Same(ClientLayoutCalculator.GridDepth(deep), ClientLayoutCalculator.MaxGridDepth, "and depth is counted through them");

    // Read back, including what a newer copy of the app might have saved.
    gear.Control = "SomethingFromANewerVersion";
    var read = ClientLayoutJson.Read(ClientLayoutJson.Write(root));
    Same(read.Docked[0].Inside!.Docked[0].Control, "SomethingFromANewerVersion", "a control this copy doesn't know isn't lost");
    Check(PanelControls.Parse("SomethingFromANewerVersion") == null, "and is skipped when showing");
    Same(ClientLayoutJson.Read("{\"Docked\":[{\"Edge\":\"Top\",\"Size\":25,\"Name\":\"old\",\"Items\":[\"EveTime\"]}]}").Docked[0].Name, "old", "a layout saved before controls could dock still reads");

    // How a panel looks.
    Check(PanelLooks.ColorOf(null) == null, "a panel with no color of its own takes the theme's");
    Same(PanelLooks.StyleOf(null), PanelStyle.Flat, "and flat");
    Same(PanelLooks.StyleOf(new PanelLook { Style = "Inset" }), PanelStyle.Inset, "a style is read back by name");
    Same(PanelLooks.StyleOf(new PanelLook { Style = "FromANewerVersion" }), PanelStyle.Flat, "one this copy doesn't know is flat");

    // What a panel holds keeps clear of its edge; a flat one has none.
    PlacedPart HeldIn(string? style, double at)
    {
        var bar = new LayoutArea { Content = AreaContent.Empty };
        var dock = ClientLayoutEditing.Dock(bar, DockEdge.Top, 40, "bar");
        dock.Look = style == null ? null : new PanelLook { Style = style };
        ClientLayoutEditing.DockControl(ClientLayoutEditing.InsideOf(dock), PanelControlKind.EveTime, DockEdge.Left);
        return ClientLayoutCalculator.Calculate(bar, new LayoutRect(0, 0, 400, 300), at).Panels.Single().Parts!.Single(p => p.Kind == PlacedPartKind.Control);
    }
    Same(HeldIn(null, 1).Rect, new LayoutRect(0, 0, 110, 40), "a flat panel's control reaches its edge");
    Same(HeldIn("Raised", 1).Rect, new LayoutRect(4, 4, 110, 32), "a raised panel's starts 4 in");
    Same(HeldIn("Inset", 1).Rect, new LayoutRect(4, 4, 110, 32), "an inset panel's too");
    Same(HeldIn("Ridge", 1.5).Rect, new LayoutRect(6, 6, 165, 48), "the margin scales with the UI");
    Same(HeldIn("Card", 1).Rect, new LayoutRect(5, 5, 110, 30), "a card's is 5, clear of its rounded corners and border");
    Same(PanelLooks.StyleOf(new PanelLook { Style = "Card" }), PanelStyle.Card, "a card is read back by name");
    Check(PanelLooks.CardMargin >= PanelLooks.CardRadius + 1, "the margin clears the curve and the border, so a square control docked full stays inside");

    // Padding: inside the edge's margin on a panel, around the cells on a grid, each side at the UI scale.
    var padded = new PanelLook { Style = "Raised", Padding = new LayoutPadding { Left = 10, Top = 2, Right = 6, Bottom = 0 } };
    Same(PanelLooks.Inside(new LayoutRect(0, 0, 200, 100), padded, 1), new LayoutRect(14, 6, 176, 90), "a panel's padding is past its edge's margin");
    Same(PanelLooks.Inside(new LayoutRect(0, 0, 200, 100), new PanelLook { Padding = padded.Padding }, 2), new LayoutRect(20, 4, 168, 96), "on a flat panel it is all there is, at the UI scale");
    Same(PanelLooks.Inside(new LayoutRect(0, 0, 200, 100), new PanelLook(), 1), new LayoutRect(0, 0, 200, 100), "none by default");
    Same(PanelLooks.Padded(new LayoutRect(0, 0, 20, 20), new LayoutPadding { Left = 30, Right = 30 }, 1), new LayoutRect(20, 0, 0, 20), "more padding than room leaves nothing");
    var gridded = new LayoutArea { Content = AreaContent.Panel, Inside = new LayoutArea() };
    ClientLayoutEditing.DockGrid(gridded, gridded.Inside, DockPlace.Full, 2, 1);
    gridded.Inside.Grid!.Padding = new LayoutPadding { Left = 8, Top = 8, Right = 8, Bottom = 8 };
    var gridResult = ClientLayoutCalculator.Calculate(gridded, new LayoutRect(0, 0, 416, 216));
    Same(gridResult.Grids.Single().Rect, new LayoutRect(8, 8, 400, 200), "a grid's cells share what its padding leaves");
    var cellParts = gridResult.Panels.Single().Parts!.Where(p => p.Kind == PlacedPartKind.Panel).ToList();
    Same(cellParts.Count, 2, "its cells are panels");
    Same(cellParts[1].Rect, new LayoutRect(212, 8, 196, 200), "and divide it, with the padding between them too");
    Same(ClientLayoutJson.Read(ClientLayoutJson.Write(gridded)).Inside!.Grid!.Padding!.Left, 8, "a grid's padding is saved");
    // A grid's padding is round its cells and between them: padding 3 is 3 between each panel and the next.
    var spaced = new LayoutArea { Content = AreaContent.Panel, Inside = new LayoutArea() };
    ClientLayoutEditing.DockGrid(spaced, spaced.Inside, DockPlace.Full, 3, 1);
    spaced.Inside.Grid!.Padding = new LayoutPadding { Left = 3, Top = 3, Right = 3, Bottom = 3 };
    var spacedResult = ClientLayoutCalculator.Calculate(spaced, new LayoutRect(0, 0, 312, 106));
    var spacedCells = spacedResult.Panels.Single().Parts!.Where(p => p.Kind == PlacedPartKind.Panel).Select(p => p.Rect).ToList();
    Check(spacedCells.SequenceEqual([new LayoutRect(3, 3, 100, 100), new LayoutRect(106, 3, 100, 100), new LayoutRect(209, 3, 100, 100)]),
        "padding 3: 3 at the edges and 3 between the panels, which stay equal: " + string.Join(" ", spacedCells));
    Same(spacedResult.Grids.Single().GapX, 3, "the gap is known to the editor, for dragging lines");
    spaced.Inside.Grid.Padding = new LayoutPadding { Left = 10, Top = 10, Right = 10, Bottom = 10 };
    Same(ClientLayoutCalculator.Calculate(spaced, new LayoutRect(0, 0, 340, 120)).Panels.Single().Parts!.Where(p => p.Kind == PlacedPartKind.Panel).Skip(1).First().Rect.X,
        10 + 100 + 10, "padding 10 is 10 between them too");
    Same(ClientLayoutCalculator.Calculate(spaced, new LayoutRect(0, 0, 680, 240), 2).Panels.Single().Parts!.Where(p => p.Kind == PlacedPartKind.Panel).Skip(1).First().Rect.X,
        20 + 200 + 20, "and grows with the UI scale");
    spaced.Inside.Grid.Padding = new LayoutPadding { Left = 4, Top = 0, Right = 8, Bottom = 2 };
    var uneven = ClientLayoutCalculator.Calculate(spaced, new LayoutRect(0, 0, 312, 100)).Grids.Single();
    Check(uneven.GapX == 6 && uneven.GapY == 0, "uneven sides: the gap between columns is left and right's average; one row has no gap between rows");
    // Undo and redo: whole steps, back into the same layout object.
    var undoRoot = ClientLayoutEditing.NewRoot();
    var history = new LayoutHistory(undoRoot);
    Check(!history.CanUndo && !history.Record(undoRoot), "nothing to undo before a change");
    var firstDock = ClientLayoutEditing.Dock(undoRoot, DockEdge.Top, 40, "first");
    Check(history.Record(undoRoot) && history.CanUndo, "a change is a step");
    ClientLayoutEditing.Dock(undoRoot, DockEdge.Left, 60, "second");
    firstDock.Name = "renamed";   // not recorded yet: an undo takes it as a step first
    var clientId = undoRoot.Inside!.Id;
    Check(history.Undo(undoRoot) && undoRoot.Docked.Count == 1 && undoRoot.Docked[0].Name == "first", "undo takes back all since the last step, recorded or not, as one");
    Check(history.Undo(undoRoot) && undoRoot.Docked.Count == 0 && undoRoot.Inside?.Content == AreaContent.Client && undoRoot.Inside.Id == clientId, "back to the start, the client keeping its id");
    Check(!history.Undo(undoRoot) && history.CanRedo, "no further");
    Check(history.Redo(undoRoot) && history.Redo(undoRoot) && undoRoot.Docked[0].Name == "renamed" && undoRoot.Docked.Count == 2, "redo puts it all back");
    history.Undo(undoRoot);
    undoRoot.Content = AreaContent.Empty;
    Check(!history.Redo(undoRoot) && !history.CanRedo, "a new change after an undo ends redo");
    for (var i = 0; i < LayoutHistory.Limit + 20; i++) { undoRoot.Name = "n" + i; history.Record(undoRoot); }
    var steps = 0;
    while (history.Undo(undoRoot)) steps++;
    Same(steps, LayoutHistory.Limit, "only so many steps are kept");

    // Dock order: one thing moved to any place in one step, the rest keeping their order.
    var ordered = new LayoutArea();
    var dA = ClientLayoutEditing.Dock(ordered, DockEdge.Top, 10, "a");
    var dB = ClientLayoutEditing.Dock(ordered, DockEdge.Left, 10, "b");
    var dC = ClientLayoutEditing.Dock(ordered, DockEdge.Top, 10, "c");
    var dD = ClientLayoutEditing.Dock(ordered, DockEdge.Right, 10, "d");
    Check(ClientLayoutEditing.MoveDocked(ordered, dD, 0), "moving the last to the start");
    Same(string.Join("", ordered.Docked.Select(d => d.Name)), "dabc", "goes there in one step");
    ClientLayoutEditing.MoveDocked(ordered, dD, 2);
    Same(string.Join("", ordered.Docked.Select(d => d.Name)), "abdc", "and back into the middle");
    ClientLayoutEditing.MoveDocked(ordered, dA, 99);
    Same(string.Join("", ordered.Docked.Select(d => d.Name)), "bdca", "past the end is the end");
    Check(!ClientLayoutEditing.MoveDocked(new LayoutArea(), dA, 0), "one not in that area isn't moved");

    // Docked somewhere else, or full, from the editor.
    var host = new LayoutArea();
    var time = ClientLayoutEditing.DockControl(host, PanelControlKind.EveTime, DockEdge.Left);
    Same(ClientLayoutEditing.PlaceOf(host, time.Id), DockPlace.Left, "a docked control's place is its edge");
    Same(ClientLayoutEditing.Redock(host, time.Id, DockPlace.Top), time.Id, "docked to another edge, it keeps its id");
    Check(time.Edge == DockEdge.Top && time.Size == PanelControls.DefaultSize(PanelControlKind.EveTime, DockEdge.Top), "and turned across, a control takes the size that suits it there");
    var boxed = ClientLayoutEditing.Dock(host, DockEdge.Right, 77, "boxed");
    boxed.Look = new PanelLook { Style = "Inset" };
    var boxedClient = ClientLayoutEditing.InsideOf(boxed);
    boxedClient.Content = AreaContent.Client;
    ClientLayoutEditing.Redock(host, boxed.Id, DockPlace.Bottom);
    Same(boxed.Size, 77, "a panel keeps its own size on another edge");
    Check(ClientLayoutEditing.CanPlace(host, boxed.Id, DockPlace.Full), "full while the middle is empty");
    var fullId = ClientLayoutEditing.Redock(host, boxed.Id, DockPlace.Full);
    Check(fullId == host.Id && host.Content == AreaContent.Panel && host.Inside == boxedClient && host.Look?.Style == "Inset" && host.Name == "boxed" && !host.Docked.Contains(boxed),
        "made full, a panel takes the middle with all it holds");
    Same(ClientLayoutEditing.PlaceOf(host, host.Id), DockPlace.Full, "and its place is full");
    var other = ClientLayoutEditing.Dock(host, DockEdge.Left, 50, "other");
    Check(!ClientLayoutEditing.CanPlace(host, other.Id, DockPlace.Full) && ClientLayoutEditing.Redock(host, other.Id, DockPlace.Full) == null, "never full over a middle that holds something");
    var back = ClientLayoutEditing.Redock(host, host.Id, DockPlace.Right);
    var backPanel = host.Docked.Last();
    Check(back == backPanel.Id && host.Content == AreaContent.Empty && backPanel.Inside == boxedClient && backPanel.Name == "boxed" && backPanel.Edge == DockEdge.Right,
        "and docked again, it's docked last, holding the same");
    Same(boxedClient.Id, ClientLayoutCalculator.ClientAreaIds(host).Single(), "its client keeps its id throughout");

    var split = new LayoutArea();
    ClientLayoutEditing.Split(split, split, 2, 1);
    var splitGrid = split.Grid;
    var dockedGrid = ClientLayoutEditing.Redock(split, split.Id, DockPlace.Top);
    Check(dockedGrid != null && split.Content == AreaContent.Empty && split.Docked.Single().Grid == splitGrid && split.Docked.Single().Id == dockedGrid, "a grid docked to an edge is docked there itself, by the id it had");
    Check(ClientLayoutEditing.PlaceOf(split, split.Id) == null, "and the empty middle left has no place to change");

    // Splitting carries everything across, so nothing set on a panel is lost.
    var whole = new LayoutArea { Content = AreaContent.Panel, Name = "whole", Look = new PanelLook { Style = "Raised" } };
    var wholeInside = ClientLayoutEditing.InsideOf(whole);
    var wholeId = whole.Id;
    Check(ClientLayoutEditing.Split(whole, whole, 2, 1), "splitting a panel");
    var first = whole.Grid!.Cells[0];
    Check(first.Id == wholeId && first.Inside == wholeInside && first.Look?.Style == "Raised" && first.Name == "whole", "keeps it whole in the first cell");
    Check(whole.Inside == null && whole.Look == null, "and leaves none of it on the grid");
}

// ---- Only panels on the monitor; clients, controls and grids in panels; grid cells are panels ----
{
    var space = new LayoutRect(0, 0, 2560, 1440);
    string Rects(ClientLayoutResult r) => string.Join(";", r.Clients.Select(c => c.AreaId + "@" + c.Rect)) + "|" +
        string.Join(";", r.Panels.SelectMany(p => p.Parts ?? []).Where(p => p.Kind == PlacedPartKind.Control).Select(p => p.Id + "@" + p.Rect));

    // An old layout: a grid straight on the monitor with a client, a control and nothing in its cells,
    // a control and a client docked on the monitor, and a panel docked there too.
    var old = new LayoutArea { Content = AreaContent.Grid, Grid = new LayoutGrid { Columns = [1, 1], Rows = [1, 1] } };
    var c1 = new LayoutArea { Content = AreaContent.Client };
    var c2 = new LayoutArea { Content = AreaContent.Control, Control = nameof(PanelControlKind.Worklist) };
    c2.Docked.Add(new DockedPanel { Edge = DockEdge.Top, Size = 30, Control = nameof(PanelControlKind.EveTime) });
    var c3 = new LayoutArea();
    var c4 = new LayoutArea { Content = AreaContent.Panel, Name = "Map", Look = new PanelLook { Style = "Raised" } };
    old.Grid.Cells.AddRange([c1, c2, c3, c4]);
    var bar = new DockedPanel { Edge = DockEdge.Top, Size = 40, Control = nameof(PanelControlKind.Menu) };
    var dockedClient = new DockedPanel { Edge = DockEdge.Left, Size = 900, Control = nameof(PanelControlKind.Client) };
    var side = new DockedPanel { Edge = DockEdge.Right, Size = 200, Name = "side" };
    old.Docked.AddRange([bar, dockedClient, side]);
    var oldRootId = old.Id;
    var before = ClientLayoutCalculator.Calculate(old, space);
    var oldClients = ClientLayoutCalculator.ClientAreaIds(old).ToList();

    var converted = ClientLayoutJson.Read(ClientLayoutJson.Write(old));
    Check(converted.Docked.All(d => d.Control == null && d.Grid == null), "read again, only panels are docked on the monitor");
    Same(converted.Content, AreaContent.Panel, "and what filled it is in a panel filling it");
    Same(converted.Inside!.Id, oldRootId, "which keeps the id it had");
    Same(converted.Docked[0].Inside!.Id, bar.Id, "a control docked on the monitor is in a panel in its place, keeping its id");
    Check(converted.Docked[0].Edge == DockEdge.Top && converted.Docked[0].Size == 40 && converted.Docked[0].Inside.Content == AreaContent.Control, "same edge, same size");
    Same(converted.Docked[1].Inside!.Content, AreaContent.Client, "a client docked there is a client in a panel");
    Same(converted.Docked[2].Name, "side", "a panel docked there stays as it was");
    Check(converted.Inside.Grid!.Cells.All(c => c.Content == AreaContent.Panel && c.Docked.Count == 0), "every cell is a panel");
    Same(converted.Inside.Grid.Cells[0].Inside!.Id, c1.Id, "a client cell's id goes with the client");
    Check(converted.Inside.Grid.Cells[1].Inside!.Docked.Count == 1, "what was docked in a cell goes in with it");
    Check(converted.Inside.Grid.Cells[2].Inside == null, "an empty cell is an empty panel");
    Check(converted.Inside.Grid.Cells[3].Name == "Map" && converted.Inside.Grid.Cells[3].Look?.Style == "Raised", "a panel cell is left as it was");
    var after = ClientLayoutCalculator.Calculate(converted, space);
    Same(Rects(after), Rects(before), "every client and control is exactly where it was");
    Check(ClientLayoutCalculator.ClientAreaIds(converted).SequenceEqual(oldClients), "and the clients keep their ids, in their order");
    var once = ClientLayoutJson.Write(converted);
    Same(ClientLayoutJson.Write(ClientLayoutMigration.Normalize(ClientLayoutJson.Read(once))), once, "reading it again changes nothing more");

    // A new layout is a panel filling the monitor with a client filling the panel.
    var fresh = ClientLayoutEditing.NewRoot();
    Check(fresh.Content == AreaContent.Panel && fresh.Inside?.Content == AreaContent.Client, "a new layout: a panel holding a client");
    Same(ClientLayoutCalculator.Calculate(fresh, space).Clients.Single().Rect, space, "which fills the monitor");
    Check(ClientLayoutPlanner.AllHole(ClientLayoutCalculator.Calculate(fresh, space).Panels.Single()), "and puts up no window: the client fills its panel");
    var framed = ClientLayoutEditing.NewRoot();
    framed.Look = new PanelLook { Style = "Raised" };
    Check(!ClientLayoutPlanner.AllHole(ClientLayoutCalculator.Calculate(framed, space).Panels.Single()), "a panel with an edge round its client does");

    // A client docked to an edge of a panel, and a grid docked beside it.
    var host = ClientLayoutEditing.NewRoot();
    var inside = host.Inside!;
    inside.Content = AreaContent.Empty;
    var game = ClientLayoutEditing.DockControl(inside, PanelControlKind.Client, DockEdge.Left);
    var gridId = ClientLayoutEditing.DockGrid(host, inside, DockPlace.Top, 2, 1);
    Check(gridId != null && inside.Docked.Last().Grid != null, "a grid docks to an edge");
    var grid = ClientLayoutEditing.FindGrid(host, gridId!)!;
    grid.Cells[1].Inside = new LayoutArea { Content = AreaContent.Client };
    var placed = ClientLayoutCalculator.Calculate(host, space);
    Same(placed.Clients.First().AreaId, game.Id, "a docked client is placed by its own id");
    Same(placed.Clients.First().Rect, new LayoutRect(0, 0, 800, 1440), "at its size on its edge");
    Check(placed.Panels.Single().Parts!.Any(p => p.Kind == PlacedPartKind.Client && p.Id == game.Id), "and is a hole in its panel");
    Check(ClientLayoutCalculator.ClientAreaIds(host).SequenceEqual([game.Id, grid.Cells[1].Inside!.Id]), "clients are found docked and in docked grids");
    Same(placed.Grids.Single().Rect, new LayoutRect(800, 0, 1760, 300), "the docked grid takes its edge of what's left");
    Same(placed.Panels.Single().Parts!.Count(p => p.Kind == PlacedPartKind.Panel), 2, "its cells are panels");
    Same(ClientLayoutEditing.ParentOf(host, grid.Cells[0].Id), gridId, "a cell's parent is its grid");
    Same(ClientLayoutEditing.GridsAbove(host, grid.Cells[1].Inside.Id), 1, "a docked grid counts toward the cap");
    Check(ClientLayoutEditing.PlaceOf(host, grid.Cells[0].Id) == null, "a cell has no place to change");
    Check(ClientLayoutEditing.DockGrid(host, host, DockPlace.Full, 2, 2) == null, "full never replaces what fills an area");

    // The way up: client › its cell's panel › the grid › the panel the grid fills.
    var chain = ClientLayoutEditing.NewRoot();
    chain.Inside!.Content = AreaContent.Empty;
    var fullGridId = ClientLayoutEditing.DockGrid(chain, chain.Inside, DockPlace.Full, 2, 2)!;
    Same(fullGridId, chain.Inside.Id, "a grid filling a panel is selected by its own id");
    var chainCell = chain.Inside.Grid!.Cells[3];
    var chainClient = ClientLayoutEditing.InsideOf(chainCell);
    chainClient.Content = AreaContent.Client;
    Same(ClientLayoutEditing.ParentOf(chain, chainClient.Id), chainCell.Id, "a client's parent is the cell's panel");
    Same(ClientLayoutEditing.ParentOf(chain, chainCell.Id), fullGridId, "a cell's parent is its grid");
    Same(ClientLayoutEditing.ParentOf(chain, fullGridId), chain.Id, "a grid's parent is the panel it fills");
    Same(ClientLayoutEditing.ParentOf(chain, chain.Id), (string?)null, "and that panel, on the monitor, is the top");
    Same(ClientLayoutEditing.FindGrid(chain, fullGridId), chain.Inside.Grid, "the grid is found by that id");
    Same(ClientLayoutEditing.PlaceOf(chain, fullGridId), DockPlace.Full, "and has a place that can be changed");

    // Columns and rows, one at a time: cells keep their places, ids and what they hold.
    var sized = chain.Inside.Grid;
    sized.Columns[0] = 3;
    Check(ClientLayoutEditing.ResizeGrid(sized, 3, 2), "a column is added");
    Check(sized.Cells.Count == 6 && sized.Columns.Count == 3 && sized.Rows.Count == 2, "3 x 2 now");
    Same(sized.Cells[4], chainCell, "the bottom-right cell is still at row 2, column 2");
    Same(sized.Columns[2], 2.0, "the new column takes the average share");
    Check(sized.Cells[2].Content == AreaContent.Panel && sized.Cells[5].Content == AreaContent.Panel, "new cells are empty panels");
    Check(ClientLayoutEditing.ResizeGrid(sized, 3, 1), "a row is taken away");
    Check(sized.Cells.Count == 3 && !sized.Cells.Contains(chainCell) && sized.Columns[0] == 3, "its cells go, the rest stay as they were");
    Check(!ClientLayoutEditing.ResizeGrid(sized, 3, 1), "the same size changes nothing");
    ClientLayoutEditing.ResizeGrid(sized, 99, 0);
    Check(sized.Columns.Count == ClientLayoutEditing.MaxGridLines && sized.Rows.Count == 1, "columns and rows stay between 1 and the most allowed");

    // Moving them: each keeps its id, and what it holds.
    Same(ClientLayoutEditing.Redock(host, gridId!, DockPlace.Full), gridId, "a docked grid made full keeps its id");
    Check(inside.Content == AreaContent.Grid && inside.Grid == grid && inside.Id == gridId, "and fills the panel");
    Same(ClientLayoutEditing.Redock(host, gridId!, DockPlace.Bottom), gridId, "docked again, still the same id");
    Check(inside.Content == AreaContent.Empty && inside.Docked.Last().Grid == grid && inside.Id != gridId, "and the panel's middle is free again");
    Same(ClientLayoutEditing.Redock(host, game.Id, DockPlace.Full), game.Id, "a docked client made full keeps its id");
    Check(inside.Content == AreaContent.Client && inside.Id == game.Id, "and is the panel's client");
    Same(ClientLayoutEditing.Redock(host, game.Id, DockPlace.Right), game.Id, "and docked again");
    Check(PanelControls.IsClient(inside.Docked.Last().Control) && inside.Docked.Last().Id == game.Id, "as a client, by the same id");
    Check(ClientLayoutCalculator.ClientAreaIds(host).Contains(game.Id), "its characters stay assigned throughout");
}

// ---- Moving things between panels, and removing them ----
{
    // On the monitor: a panel down the left, and one filling the rest with a client and a clock in it.
    var root = ClientLayoutEditing.NewRoot();
    var main = root.Inside!;
    var clientId = main.Id;
    var clock = ClientLayoutEditing.DockControl(main, PanelControlKind.EveTime, DockEdge.Top);
    var side = ClientLayoutEditing.Dock(root, DockEdge.Left, 300, "side");

    Check(ClientLayoutEditing.CanMove(root, clock.Id, side.Id), "a docked control can move to another panel");
    Same(ClientLayoutEditing.Move(root, clock.Id, side.Id), clock.Id, "and keeps its id");
    Check(side.Inside!.Docked.Single() == clock && clock.Edge == DockEdge.Top && !main.Docked.Contains(clock), "docked there on the same edge, gone from where it was");
    Check(!ClientLayoutEditing.CanMove(root, clock.Id, side.Id), "moving it where it already is does nothing");

    // A full client into an empty panel fills it, by the same id.
    Same(ClientLayoutEditing.Move(root, clientId, side.Id), clientId, "a full client moves");
    Check(side.Inside.Content == AreaContent.Client && side.Inside.Id == clientId, "and fills the empty middle of its new panel");
    Check(main.Content == AreaContent.Empty && main.Id != clientId, "leaving the old one empty under a new id");
    Same(ClientLayoutCalculator.ClientAreaIds(root).Single(), clientId, "its characters stay assigned");

    // Full into a panel that's already filled: docked to the top instead.
    var other = ClientLayoutEditing.Dock(root, DockEdge.Right, 200, "other");
    ClientLayoutEditing.InsideOf(other).Content = AreaContent.Client;
    Same(ClientLayoutEditing.Move(root, clientId, other.Id), clientId, "a full client into a filled panel");
    Check(other.Inside!.Docked.Single().Id == clientId && PanelControls.IsClient(other.Inside.Docked.Single().Control) && other.Inside.Docked.Single().Edge == DockEdge.Top,
        "is docked to its top, still a client by the same id");

    // The monitor takes panels only; nothing goes into itself.
    Check(!ClientLayoutEditing.CanMove(root, clock.Id, ClientLayoutEditing.MonitorTarget), "a control can't go straight on the monitor");
    var inner = ClientLayoutEditing.Dock(side.Inside, DockEdge.Bottom, 50, "inner");
    Check(ClientLayoutEditing.CanMove(root, inner.Id, ClientLayoutEditing.MonitorTarget), "a panel can");
    Check(!ClientLayoutEditing.CanMove(root, side.Id, inner.Id), "a panel can't go into a panel it holds");
    Check(!ClientLayoutEditing.CanMove(root, side.Id, side.Id), "or into itself");
    Same(ClientLayoutEditing.Move(root, inner.Id, ClientLayoutEditing.MonitorTarget), inner.Id, "moved onto the monitor");
    Check(root.Docked.Contains(inner) && !side.Inside.Docked.Contains(inner), "it's docked there now");

    // Grids: a cell stays in its grid; the cap on nested grids holds for moves too.
    var gridId = ClientLayoutEditing.DockGrid(root, side.Inside, DockPlace.Bottom, 2, 1)!;
    var grid = ClientLayoutEditing.FindGrid(root, gridId)!;
    Check(!ClientLayoutEditing.CanMove(root, grid.Cells[0].Id, other.Id), "a grid's cell can't be moved out of it");
    Same(ClientLayoutEditing.Move(root, clock.Id, grid.Cells[1].Id), clock.Id, "but what's in a cell can move, and things can move into a cell");
    Same(ClientLayoutEditing.Move(root, gridId, other.Id), gridId, "a whole grid moves, with its cells");
    Check(other.Inside.Docked.Any(d => d.Grid == grid) && grid.Cells[1].Inside!.Docked.Single() == clock, "taking what's in them along");
    var deep = ClientLayoutEditing.NewRoot();
    deep.Inside!.Content = AreaContent.Empty;
    var walk = deep.Inside;
    LayoutArea lastCell = deep;
    for (var i = 0; i < 4; i++) { ClientLayoutEditing.DockGrid(deep, walk, DockPlace.Full, 2, 1); lastCell = walk.Grid!.Cells[1]; walk = ClientLayoutEditing.InsideOf(lastCell); }
    var twoDeep = ClientLayoutEditing.Dock(deep, DockEdge.Left, 100, "two");
    ClientLayoutEditing.DockGrid(deep, ClientLayoutEditing.InsideOf(twoDeep), DockPlace.Full, 2, 1);
    ClientLayoutEditing.DockGrid(deep, ClientLayoutEditing.InsideOf(twoDeep.Inside!.Grid!.Cells[0]), DockPlace.Full, 2, 1);
    Check(!ClientLayoutEditing.CanMove(deep, twoDeep.Id, lastCell.Id), "two levels of grid can't go four deep: past the cap");

    // Removing.
    var selectAfter = ClientLayoutEditing.Remove(root, clock.Id, out var removed);
    Check(removed && selectAfter == grid.Cells[1].Id && grid.Cells[1].Inside!.Docked.Count == 0, "removing a docked control selects the panel it was in");
    var keptCell = ClientLayoutEditing.Remove(root, grid.Cells[0].Id, out removed);
    Check(!removed && keptCell == grid.Cells[0].Id && grid.Cells.Count == 2, "a grid's cell isn't removed");
    ClientLayoutEditing.Remove(root, other.Inside.Id, out removed);
    Check(removed && other.Inside.Content == AreaContent.Empty, "a full client is removed from its panel");
    ClientLayoutEditing.Remove(root, root.Id, out removed);
    Check(removed && root.Content == AreaContent.Empty, "and so is the panel filling the monitor");
    ClientLayoutEditing.Remove(root, root.Id, out removed);
    Check(!removed, "but never the monitor itself");
    var probe = ClientLayoutEditing.NewRoot();
    var bare = ClientLayoutEditing.Dock(probe, DockEdge.Top, 30, "bare");
    var before = ClientLayoutJson.Write(probe);
    ClientLayoutEditing.CanMove(probe, probe.Inside!.Id, bare.Id);
    Same(ClientLayoutJson.Write(probe), before, "asking whether something can move changes nothing, not even an empty panel's inside");
}

// ---- Which character a monitor's controls follow ----
{
    const string M1 = "monitor-1", M2 = "monitor-2";
    ClientOnMonitor Client(long handle, string monitor, string? who) => new(handle, monitor, false, who);
    var focus = new MonitorFocus();
    var changes = new List<string>();
    focus.Changed += changes.Add;

    focus.Update([Client(1, M1, "Alpha"), Client(2, M1, "Bravo"), Client(3, M2, "Charlie")]);
    Same(string.Join(",", focus.NamesOn(M1)), "Alpha,Bravo", "the names are only those on that monitor");
    Same(string.Join(",", focus.NamesOn(M2)), "Charlie", "each monitor has its own");
    Same(focus.SelectedOn(M1), "Alpha", "before any client has been used, the first name");
    Check(changes.Contains(M1), "and those watching are told");

    changes.Clear();
    focus.Foreground(2);
    Same(focus.SelectedOn(M1), "Bravo", "bringing another client on the monitor forward changes it");
    Same(focus.SelectedOn(M2), "Charlie", "and not on another monitor");
    Same(string.Join(",", changes), M1, "only that monitor is told");

    focus.Foreground(3);
    Same(focus.SelectedOn(M1), "Bravo", "using a client on another monitor leaves this one's selection");
    focus.Foreground(999);
    Same(focus.SelectedOn(M1), "Bravo", "and so does some other program");

    focus.Choose(M1, "Alpha");
    Same(focus.SelectedOn(M1), "Alpha", "picking from the dropdown changes it");
    focus.Choose(M1, "Charlie");
    Same(focus.SelectedOn(M1), "Alpha", "but not to someone who isn't on that monitor");
    focus.Update([Client(1, M1, "Alpha"), Client(2, M1, "Bravo"), Client(3, M2, "Charlie")]);
    Same(focus.SelectedOn(M1), "Alpha", "and a pick holds while nothing changes");
    focus.Foreground(2);
    Same(focus.SelectedOn(M1), "Bravo", "until a client is brought forward");

    focus.Update([Client(1, M1, "Alpha"), Client(2, M1, null), Client(3, M2, "Charlie")]);
    Same(focus.SelectedOn(M1), "Alpha", "logged out, the name is gone from the list, so another is used");
    focus.Update([Client(1, M1, "Alpha"), Client(2, M1, "Delta"), Client(3, M2, "Charlie")]);
    Same(focus.SelectedOn(M1), "Delta", "and logging back in as someone else follows the client last used");

    focus.Update([Client(1, M1, "Alpha"), Client(3, M2, "Charlie")]);
    Same(focus.SelectedOn(M1), "Alpha", "the client last used closed, so someone still there");
    focus.Update([Client(3, M2, "Charlie")]);
    Same(focus.SelectedOn(M1), null, "and no one when no one is left");
    Same(focus.NamesOn(M1).Count, 0, "with no names to pick");

    focus.Update([Client(4, M1, null)]);
    focus.Foreground(4);
    Same(focus.SelectedOn(M1), null, "a client at its login screen has no name");
    focus.Update([Client(4, M1, "Echo")]);
    Same(focus.SelectedOn(M1), "Echo", "until it logs in");

    focus.Update([Client(5, M1, "echo"), Client(4, M1, "Echo")]);
    Same(focus.NamesOn(M1).Count, 1, "names match whatever the case");
}
{
    // Everyone set for a client's place, not just who is logged in on it.
    const string M1 = "monitor-1";
    var account = ClientOnMonitor.GroupOf(["Main", "Alt One", "Alt Two"]);
    var other = ClientOnMonitor.GroupOf(["Hauler", "Scout"]);
    var focus = new MonitorFocus();
    var changes = new List<string>();
    focus.Changed += changes.Add;
    focus.Update([new(1, M1, false, "Main", account), new(2, M1, false, "Hauler", other)]);
    focus.Foreground(1);
    Same(string.Join(",", focus.NamesOn(M1)), "Alt One,Alt Two,Main,Hauler", "the client's place's characters first, logged in or not, then who else is logged in");
    Same(focus.SelectedOn(M1), "Main", "the one logged in is the one selected");
    focus.Choose(M1, "Alt Two");
    Same(focus.SelectedOn(M1), "Alt Two", "another on that account can be picked, to see their tasks");
    focus.Update([new(1, M1, false, "Main", account), new(2, M1, false, "Hauler", other)]);
    Same(focus.SelectedOn(M1), "Alt Two", "and stays picked while nothing changes");
    changes.Clear();
    focus.Foreground(2);
    Same(string.Join(",", focus.NamesOn(M1)), "Hauler,Scout,Main", "using the other client offers its place's characters");
    Same(focus.SelectedOn(M1), "Hauler", "and selects who is logged in there");
    Check(changes.Contains(M1), "and those watching are told");
    focus.Foreground(1);
    focus.Update([new(1, M1, false, "Alt One", account), new(2, M1, false, "Hauler", other)]);
    Same(focus.SelectedOn(M1), "Alt One", "logging in as another on the account follows it");
    focus.Update([new(1, M1, false, null), new(2, M1, false, "Hauler", other)]);
    Same(string.Join(",", focus.NamesOn(M1)), "Alt One,Alt Two,Main,Hauler", "at the login screen, its place's characters are still offered");
    Same(focus.SelectedOn(M1), "Alt One", "and the last name stays until a new one appears");
    focus.Update([new(1, M1, false, "Alt Two", account), new(2, M1, false, "Hauler", other)]);
    Same(focus.SelectedOn(M1), "Alt Two", "which it then follows");
    focus.Update([new(2, M1, false, "Hauler", other)]);
    Same(focus.SelectedOn(M1), "Hauler", "the client closed, someone logged in is used");
    Same(string.Join(",", focus.NamesOn(M1)), "Hauler,Scout", "and its place's characters are offered instead");
    var picked = new MonitorFocus();
    picked.Update([new(1, M1, false, "Main", account), new(2, M1, false, "Hauler", other)]);
    picked.Foreground(1);
    picked.Choose(M1, "Hauler");
    picked.Update([new(1, M1, false, "Main", account)]);
    Same(picked.SelectedOn(M1), "Main", "the one picked gone, it's someone logged in, not the first name listed");

    var unused = new MonitorFocus();
    unused.Update([new(1, M1, false, "Main", account)]);
    unused.Choose(M1, "Alt Two");
    Same(unused.SelectedOn(M1), "Alt Two", "before any client is used, another on the account can be picked");
    Same(string.Join(",", unused.NamesOn(M1)), "Alt One,Alt Two,Main", "and the account's characters stay listed, the one picked among them");
    unused.Update([new(1, M1, false, "Main", account)]);
    Same(unused.SelectedOn(M1), "Alt Two", "and stays picked");

    var later = new MonitorFocus();
    later.Update([new(1, M1, false, "Main", null)]);
    later.Foreground(1);
    later.Update([new(1, M1, false, "Main", account)]);
    later.Update([new(1, M1, false, null)]);
    Same(string.Join(",", later.NamesOn(M1)), "Alt One,Alt Two,Main", "a place's characters arriving after the client was used are kept through a relog too");

    var told = new MonitorFocus();
    told.Update([new(1, M1, false, "Main", account), new(2, M1, false, null)]);
    told.Foreground(1);
    var tellings = new List<string>();
    told.Changed += tellings.Add;
    told.Foreground(2);
    Same(told.SelectedOn(M1), "Main", "a client at its login screen used leaves the name");
    Check(tellings.Contains(M1) && !told.NamesOn(M1).Contains("Alt One"), "but its choices change, and those watching are told");

    var lone = new MonitorFocus();
    lone.Update([new(1, M1, false, "Main", null)]);
    lone.Foreground(1);
    Same(string.Join(",", lone.NamesOn(M1)), "Main", "a character the layout doesn't place has only itself");
    Same(string.Join(",", ClientOnMonitor.GroupOf(["b", "A"]).Split('\n')), "A,b", "a place's characters are kept in name order");
}

// ---- The order of a Mumble channel list ----
{
    var at = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    var people = new (uint Session, string Name)[] { (1, "Zed"), (2, "Amy"), (3, "Bob"), (4, "Cat") };
    string Order(MumbleTalkOrder o) => string.Join(",", o.Order(people, p => p.Session, p => p.Name).Select(p => p.Name));

    var order = new MumbleTalkOrder();
    Same(Order(order), "Amy,Bob,Cat,Zed", "nobody has talked: by name");
    order.Heard([3u], at);
    Same(Order(order), "Bob,Amy,Cat,Zed", "someone talking goes to the top");
    order.Heard([3u, 1u], at.AddSeconds(1));
    Same(Order(order), "Zed,Bob,Amy,Cat", "the latest to start is first, whatever the names");
    order.Heard([3u, 1u], at.AddSeconds(2));
    Same(Order(order), "Zed,Bob,Amy,Cat", "and two talking at once don't swap places as they go on");
    order.Heard([3u], at.AddSeconds(3));
    Same(Order(order), "Bob,Zed,Amy,Cat", "stopped, they stay above those who haven't talked, under who's talking");
    order.Heard([], at.AddSeconds(4));
    Same(Order(order), "Bob,Zed,Amy,Cat", "the most recent talker first once everyone has stopped");
    order.Heard([2u], at.AddSeconds(5));
    order.Heard([], at.AddSeconds(6));
    Same(Order(order), "Amy,Bob,Zed,Cat", "and they float up again when they next talk");
    order.Heard([1u], at.AddSeconds(7));
    Same(Order(order), "Zed,Amy,Bob,Cat", "starting again counts as starting");
    order.Heard([], at.AddSeconds(8));

    order.KeepOnly([2u, 3u, 4u]);
    Same(Order(order), "Amy,Bob,Cat,Zed", "someone who left is forgotten, as their number can be reused");
}

// ---- What's chosen for a control ----
{
    var root = new LayoutArea { Content = AreaContent.Panel };
    var inside = ClientLayoutEditing.InsideOf(root);
    var mumble = ClientLayoutEditing.DockControl(inside, PanelControlKind.Mumble, DockEdge.Left);
    Same(PanelControls.MumbleModeOf(mumble.Options), MumbleControlMode.SingleLine, "a Mumble control starts as one line");
    mumble.Options = new ControlOptions { MumbleServerId = 7, MumbleMode = "List" };
    var read = ClientLayoutJson.Read(ClientLayoutJson.Write(root));
    var again = read.Inside!.Docked[0];
    Same(again.Options?.MumbleServerId, 7, "the one server it's for is kept");
    Same(PanelControls.MumbleModeOf(again.Options), MumbleControlMode.List, "and its mode");
    Same(PanelControls.MumbleModeOf(new ControlOptions { MumbleMode = "FromANewerVersion" }), MumbleControlMode.SingleLine, "a mode this copy doesn't know is one line");
    var placed = ClientLayoutCalculator.Calculate(root, monitor4K).Panels.Single().Parts!.Single(p => p.Id == mumble.Id);
    Same(placed.Options?.MumbleServerId, 7, "and what's chosen reaches the screen");

    var cell = new LayoutArea();
    var cellRoot = new LayoutArea { Content = AreaContent.Panel, Inside = cell };
    ClientLayoutEditing.FillWithControl(cell, PanelControlKind.Worklist);
    Check(PanelControls.Parse(cell.Control) == PanelControlKind.Worklist, "the worklist can be docked full");
    cell.Options = new ControlOptions { MumbleServerId = 1 };
    ClientLayoutEditing.FillWithControl(cell, PanelControlKind.Mumble);
    Check(cell.Options == null, "another control in that place doesn't inherit what was chosen");
    Same(ClientLayoutCalculator.Calculate(cellRoot, monitor4K).Panels.Single().Parts!.Single().Control, "Mumble", "it's what shows");
}

// ---- The Worklist's modes, the menu and Tool Containers ----
{
    Same(PanelControls.WorklistModeOf(null), WorklistControlMode.List, "a Worklist control saved before the choice is the list");
    Same(PanelControls.WorklistModeOf(new ControlOptions { WorklistMode = "SingleLine" }), WorklistControlMode.SingleLine, "and can be one line");
    Same(PanelControls.WorklistModeOf(new ControlOptions { WorklistMode = "FromANewerVersion" }), WorklistControlMode.List, "a mode this copy doesn't know is the list");

    Same(ToolTargets.Of(null), ToolTargets.LastUsed, "a menu opens tools in the window used last unless told otherwise");
    Same(ToolTargets.Of(new ControlOptions { MenuTarget = "abc" }), "abc", "or in the container chosen");
    Same(PanelControls.DefaultSize(PanelControlKind.Menu, DockEdge.Left), 40, "a menu docks as narrow as the settings gear");
    Same(PanelControls.DefaultSize(PanelControlKind.ToolContainer, DockEdge.Top), 400, "a container docks big enough for a tool");

    ContainerTool T(string id, bool close = true) => new() { ToolId = id, CanClose = close };
    string Tools(ControlOptions o) => string.Join(",", PanelControls.ToolsOf(o).Select(t => t.ToolId + (t.CanClose ? "" : "!")));
    Same(Tools(new ControlOptions { ContainerTools = [T("a"), T("b")] }), "a!", "holding one tool, only the first tool, and it can't close");
    Same(Tools(new ControlOptions { ContainerTabs = true, ContainerTools = [T("a"), T("b", false), T(""), T("a", false)] }), "a,b!",
        "holding tabs, each tool once, as chosen, skipping blanks");
    Check(!PanelControls.Splits(new ControlOptions { ContainerSplit = true }), "one tool never splits");
    Check(PanelControls.Splits(new ControlOptions { ContainerTabs = true, ContainerSplit = true }), "tabs split when allowed");
    Check(!PanelControls.Splits(new ControlOptions { ContainerTabs = true }), "and only then");

    // Saved and read back, and found however deep it is.
    var root = new LayoutArea { Content = AreaContent.Client };
    var side = ClientLayoutEditing.Dock(root, DockEdge.Right, 800, "Side");
    var inside = ClientLayoutEditing.InsideOf(side);
    ClientLayoutEditing.Split(root, inside, 1, 2);
    var cell = inside.Grid!.Cells[1];
    ClientLayoutEditing.FillWithControl(cell, PanelControlKind.ToolContainer);
    cell.Name = "Market";
    cell.Options = new ControlOptions { ContainerTabs = true, ContainerSplit = true, ContainerTools = [T("worklist"), T("assets", false)] };
    var menu = ClientLayoutEditing.DockControl(ClientLayoutEditing.InsideOf(ClientLayoutEditing.Dock(root, DockEdge.Top, 28, "Top")), PanelControlKind.Menu, DockEdge.Left);
    menu.Options = new ControlOptions { MenuTarget = cell.Id };

    var read = ClientLayoutJson.Read(ClientLayoutJson.Write(root));
    var controls = ClientLayoutEditing.Controls(read).ToList();
    Same(controls.Count, 2, "both controls are found, through panels and grids");
    var container = controls.Single(c => c.Control == "ToolContainer");
    Same(container.Name, "Market", "the container keeps its name");
    Same(Tools(container.Options!), "worklist,assets!", "and its tools");
    Same(controls.Single(c => c.Control == "Menu").Options?.MenuTarget, cell.Id, "the menu keeps where it opens tools");
    var placed = ClientLayoutCalculator.Calculate(read, monitor4K).Panels.SelectMany(p => p.Parts ?? []).Single(p => p.Id == cell.Id);
    Same(placed.Name, "Market", "the name reaches the screen");
    Check(PanelControls.Splits(placed.Options), "and so does what it can do");
}

// ---- The UI scale ----
{
    // A 28 px bar at the top holding a 40 px gear at its left and a full control below it on a 300 px right side.
    var root = new LayoutArea { Content = AreaContent.Client };
    var top = ClientLayoutEditing.Dock(root, DockEdge.Top, 28, "Top");
    var gear = ClientLayoutEditing.DockControl(ClientLayoutEditing.InsideOf(top), PanelControlKind.Settings, DockEdge.Left);
    var side = ClientLayoutEditing.Dock(root, DockEdge.Right, 300, "Side");
    var sideInside = ClientLayoutEditing.InsideOf(side);
    ClientLayoutEditing.FillWithControl(sideInside, PanelControlKind.Alerts);
    var space = new LayoutRect(0, 0, 2000, 1000);

    foreach (var (scale, bar, gearWidth, sideWidth) in new[] { (1.0, 28, 40, 300), (1.25, 35, 50, 375), (0.75, 21, 30, 225) })
    {
        var result = ClientLayoutCalculator.Calculate(root, space, scale);
        var topPanel = result.Panels.Single(p => p.Id == top.Id);
        Same(topPanel.Rect, new LayoutRect(0, 0, 2000, bar), $"at {scale:P0} the top bar is the full width, its height scaled");
        Same(topPanel.Parts!.Single(p => p.Id == gear.Id).Rect, new LayoutRect(0, 0, gearWidth, bar), $"at {scale:P0} what's docked left in it has its width scaled");
        var sidePanel = result.Panels.Single(p => p.Id == side.Id);
        Same(sidePanel.Rect, new LayoutRect(2000 - sideWidth, bar, sideWidth, 1000 - bar), $"at {scale:P0} the side's width is scaled, its height what's left");
        Same(sidePanel.Parts!.Single(p => p.Id == sideInside.Id).Rect, sidePanel.Rect, $"at {scale:P0} what's full takes all its room");
        Same(result.Clients.Single().Rect, new LayoutRect(0, bar, 2000 - sideWidth, 1000 - bar), $"at {scale:P0} the client has the rest");
        Check(!result.Problems.Any(), $"at {scale:P0} everything fits");
    }
}

// ---- The main window's own controls, on panels ----
{
    foreach (var kind in Enum.GetValues<PanelControlKind>())
    {
        Same(PanelControls.Parse(kind.ToString()), kind, $"{kind} is read back by the name it's saved as");
        Check(PanelControls.DefaultSize(kind, DockEdge.Left) > 0 && PanelControls.DefaultSize(kind, DockEdge.Top) > 0, $"{kind} docks at a size");
    }
    Same(PanelControls.DefaultSize(PanelControlKind.Agent, DockEdge.Right), 360, "the companion docks as wide as the main window's");
    Same(PanelControls.DefaultSize(PanelControlKind.Alerts, DockEdge.Top), 300, "alerts dock tall enough for a few");
    Same(PanelControls.DefaultSize(PanelControlKind.Scheduler, DockEdge.Left), 34, "an icon docks narrow");

    // A layout from this version, read by one that doesn't know its newer controls, keeps them.
    var root = new LayoutArea { Content = AreaContent.Client };
    var bar = ClientLayoutEditing.InsideOf(ClientLayoutEditing.Dock(root, DockEdge.Top, 28, "Top"));
    foreach (var kind in new[] { PanelControlKind.DirectMessages, PanelControlKind.GroupChat, PanelControlKind.Alarms, PanelControlKind.EsiStatus })
        ClientLayoutEditing.DockControl(bar, kind, DockEdge.Left);
    var json = ClientLayoutJson.Write(root).Replace("\"EsiStatus\"", "\"FromANewerVersion\"", StringComparison.Ordinal);
    var controls = ClientLayoutEditing.Controls(ClientLayoutJson.Read(json)).Select(c => c.Control).ToList();
    Same(string.Join(",", controls), "DirectMessages,GroupChat,Alarms,FromANewerVersion", "every control is kept, a newer one by its saved name");
    Same(PanelControls.Parse("FromANewerVersion"), null, "and this copy doesn't build one it doesn't know");
}

// ---- The Overview's saved layouts ----
{
    var dir = Path.Combine(Path.GetTempPath(), "evc-overview-layouts-" + Guid.NewGuid().ToString("N"));
    var file = Path.Combine(dir, "overview-layouts.json");
    string? config = null;   // config.json's single layout, as older builds read and write it
    var store = new EveConsole.Services.OverviewLayoutStore(file, () => config, json => config = json);
    var told = new List<IReadOnlyDictionary<string, string>>();
    store.Changed += told.Add;
    string Names() => string.Join(",", store.Choices.Select(c => c.Key.Length == 0 ? "[Default]" : c.Key));
    EveConsole.Models.OverviewLayout Grid(int rows) => new() { Rows = rows, Cols = 2 };
    try
    {
        Same(Names(), "[Default]", "with nothing saved there is just Default");
        Same(store.Get("").Rows, EveConsole.Models.OverviewLayout.Default().Rows, "and it's the built-in layout");
        config = Grid(4).ToJson();
        Same(store.Get(null).Rows, 4, "Default is the layout older builds keep in config.json");
        Check(!File.Exists(file), "and reading makes no file");

        var shown = store.Apply(new([("", "", Grid(5)), (null, "Compact", Grid(1)), (null, "Wide", Grid(2))], 1));
        Same(Names(), "[Default],Compact,Wide", "layouts added are kept, in order");
        Same(shown, "Compact", "and the one being edited is shown");
        Same(store.Get("Compact").Rows, 1, "each with its own grid");
        Same(store.Get("compact").Rows, 1, "found whatever the case");
        Same(EveConsole.Models.OverviewLayout.FromJsonOrDefault(config).Rows, 5, "Default's edits go to config.json, where older builds see them");
        Check(!File.ReadAllText(file).Contains("\"Rows\": 5"), "and only there");
        Check(store.Get("Compact").Sections.Count == EveConsole.Models.OverviewLayout.KnownSections.Length, "a layout read back has every section, placed or not");

        store.MainLayout = "Wide";
        Same(store.MainLayout, "Wide", "the main window's choice is kept");
        told.Clear();
        store.Apply(new([("", "", Grid(5)), ("Compact", "Small", Grid(1)), ("Wide", "Wide", Grid(3))], 2));
        Same(Names(), "[Default],Small,Wide", "a renamed layout keeps its place");
        Same(told.Single().GetValueOrDefault("Compact"), "Small", "and Overviews are told what it's called now");
        Same(store.Get("Wide").Rows, 3, "an edited one is saved");

        store.Apply(new([("", "", Grid(5)), ("Small", "Small", Grid(1))], 0));
        Same(Names(), "[Default],Small", "a layout left out is deleted");
        Same(store.MainLayout, "", "the main window showing it goes back to Default");
        Same(store.Get("Wide").Rows, 5, "and asking for it gets Default");
        Same(store.KeyOf("Wide"), "", "which is what its key now means");
        Check(!store.Exists("Wide") && store.Exists("Small") && store.Exists(""), "it no longer exists");

        shown = store.Apply(new([("", "", Grid(5)), ("Small", "Small", Grid(1)), (null, "small", Grid(2)), (null, "", Grid(2)), (null, "Default", Grid(2))], 4));
        Same(Names(), "[Default],Small,small (2),Layout,Default (2)", "names are made unique, blank ones named, and none can pass for Default");
        Same(shown, "Default (2)", "and the one shown follows its new name");

        store.MainLayout = "Small";
        File.WriteAllText(file, "{ not json");
        Same(Names(), "[Default],Small,small (2),Layout,Default (2)", "a damaged file reads its backup, the save before, instead");
        File.WriteAllText(file + ".bak", "{ not json");
        Same(Names(), "[Default]", "and with both damaged, only Default remains to show");
        Same(store.Get("").Rows, 5, "which is still there");
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}

// ---- Where a tab can go ----
{
    var main = new EveConsole.ViewModels.TabWorkspace(isMain: true);
    var other = new EveConsole.ViewModels.TabWorkspace(isMain: false);
    var tabs = EveConsole.ViewModels.TabWorkspace.Container(showsTabs: true, canSplit: false);
    var single = EveConsole.ViewModels.TabWorkspace.Container(showsTabs: false, canSplit: true);
    EveConsole.ViewModels.ToolTab Tab(string id) => new(id, id, new object());
    var plain = Tab("assets");
    var overview = Tab("overview");

    Check(tabs.IsContainer && !tabs.IsFloating && other.IsFloating && !main.IsFloating, "a container is neither the main window nor one of its own");
    Check(!single.CanSplit, "a container of one tool never splits");
    Check(EveConsole.ViewModels.TabWorkspace.CanMove(plain, main, tabs), "a tab can go into a container of tabs");
    Check(EveConsole.ViewModels.TabWorkspace.CanMove(plain, tabs, other), "and out again");
    Check(!EveConsole.ViewModels.TabWorkspace.CanMove(plain, main, single), "but not into one holding one tool");
    Check(!EveConsole.ViewModels.TabWorkspace.CanMove(plain, single, main), "nor out of one");
    Check(!EveConsole.ViewModels.TabWorkspace.CanDetach(plain, single), "nor to a window of its own from one");
    Check(!EveConsole.ViewModels.TabWorkspace.CanMove(overview, main, tabs), "the Overview stays in the main window");

    tabs.Add(plain);
    plain.PinnedIn = tabs;
    Check(!plain.CanClose, "a pinned tab can't close");
    Check(!EveConsole.ViewModels.TabWorkspace.CanMove(plain, tabs, main), "nor leave its container");
    Check(!EveConsole.ViewModels.TabWorkspace.CanDetach(plain, tabs), "even to a window of its own");
    Check(EveConsole.ViewModels.TabWorkspace.CanMove(plain, tabs, tabs), "but moves about inside it");
    plain.PinnedIn = null;
    Check(plain.CanClose, "unpinned, it closes again");

    tabs.Add(Tab("wallet"));
    tabs.IsDraggingTab = true;
    Check(!tabs.ShowSplitDropZone, "a container that can't split offers no right half");
    var splits = EveConsole.ViewModels.TabWorkspace.Container(showsTabs: true, canSplit: true);
    splits.Add(Tab("items"));
    splits.IsDraggingTab = true;
    Check(splits.ShowSplitDropZone, "one that can does");
}

// ---- Groups when a monitor changes layout ----
{
    EveConsole.Models.ClientLayoutCharacter Row(int id, int monitor, string area, string name) =>
        new() { Id = id, MonitorId = monitor, AreaId = area, CharacterName = name };

    var one = new LayoutArea { Content = AreaContent.Client };
    var two = TwoClients(out var twoFirst, out var twoSecond);

    // The new layout has none here: client 1's group comes along to client 1, as copies.
    var rows = new[] { Row(1, 5, one.Id, "Alpha"), Row(2, 5, one.Id, "Bravo"), Row(3, 6, twoFirst, "Echo") };
    var carried = ClientLayoutStore.CarriedCharacters(5, rows, one, two);
    Same(carried.Count, 2, "both of client 1's characters come along");
    Check(carried.All(r => r.MonitorId == 5 && r.AreaId == twoFirst && r.Id == 0), "as new rows, into the new client 1");
    Check(carried.Select(r => r.CharacterName).Order().SequenceEqual(["Alpha", "Bravo"]), "with their names");

    // Back to one client: client 1 comes back, and the second client's group has nowhere to go.
    rows = [Row(1, 5, twoFirst, "Alpha"), Row(2, 5, twoSecond, "Bravo")];
    carried = ClientLayoutStore.CarriedCharacters(5, rows, two, one);
    Same(carried.Single().AreaId, one.Id, "client 1 to client 1");
    Same(carried.Single().CharacterName, "Alpha", "only client 1's");

    // A layout with groups of its own on this monitor keeps exactly those.
    rows = [Row(1, 5, one.Id, "Alpha"), Row(2, 5, twoSecond, "Bravo")];
    Same(ClientLayoutStore.CarriedCharacters(5, rows, one, two).Count, 0, "the new layout's own groups are left as they are");

    // Another monitor's groups are never carried.
    rows = [Row(1, 6, one.Id, "Alpha")];
    Same(ClientLayoutStore.CarriedCharacters(5, rows, one, two).Count, 0, "only this monitor's groups");

    // Joining a group: it leaves its group in this layout and on other monitors, but not this
    // monitor's groups for its other layouts.
    rows = [Row(1, 5, one.Id, "Alpha"), Row(2, 5, twoSecond, "alpha"), Row(3, 6, twoFirst, "ALPHA"), Row(4, 5, twoFirst, "Bravo")];
    var gone = ClientLayoutStore.Displaced(rows, 5, new HashSet<string> { twoFirst, twoSecond }, "Alpha").Select(r => r.Id).Order().ToList();
    Check(gone.SequenceEqual([2, 3]), $"this layout's and another monitor's, ignoring case: got {string.Join(",", gone)}");
}

// ---- The placement rules ----
var now =new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
var opening = new LayoutRect(10, 10, 1000, 800);
var openingAway = new LayoutRect(3900, 10, 1000, 800);

ClientWindowInfo At(long handle, string title, LayoutRect rect, bool borderless = true, bool minimized = false) =>
    new(handle, title, rect, borderless, minimized);

ClientPlacementEngine Engine(out LayoutRect cell1, out LayoutRect cell2, out LayoutRect far, params (string Name, int Cell)[] characters)
{
    var bigLayout = TwoClients(out var first, out var second);
    var smallLayout = new LayoutArea { Content = AreaContent.Client };
    var applied = new AppliedDesktop
    {
        Monitors =
        [
            new AppliedMonitor { HardwareKey = Big, Definition = ClientLayoutJson.Write(bigLayout) },
            new AppliedMonitor { HardwareKey = Small, Definition = ClientLayoutJson.Write(smallLayout) },
        ],
    };
    foreach (var (name, cell) in characters)
    {
        var (monitor, id) = cell switch { 1 => (0, first), 2 => (0, second), _ => (1, smallLayout.Id) };
        if (!applied.Monitors[monitor].Characters.TryGetValue(id, out var names))
            applied.Monitors[monitor].Characters[id] = names = [];
        names.Add(name);
    }
    var plan = ClientLayoutPlanner.Build(applied, plugged);
    cell1 = plan.Placement.Monitors[0].Cells[0].Rect;
    cell2 = plan.Placement.Monitors[0].Cells[1].Rect;
    far = plan.Placement.Monitors[1].Cells[0].Rect;
    var engine = new ClientPlacementEngine();
    engine.UsePlan(plan.Placement);
    return engine;
}

{
    Same(ClientPlacementEngine.CharacterIn("EVE"), null, "the login screen has no character");
    Same(ClientPlacementEngine.CharacterIn("EVE - Some Pilot"), "Some Pilot", "the name in the title");
    Same(ClientPlacementEngine.CharacterIn("EVE - "), null, "an empty name is none");
    Check(ClientPlacementEngine.IsClientTitle("EVE"), "a client at login");
    Check(ClientPlacementEngine.IsClientTitle("EVE - Some Pilot"), "a logged-in client");
    Check(!ClientPlacementEngine.IsClientTitle("EVE Console"), "our own window is not a client");
    Check(!ClientPlacementEngine.IsClientTitle("EVEMail draft"), "nor something else that starts the same");
}
{
    // Clients already running when the app starts are left alone.
    var e = Engine(out var c1, out _, out _, ("Alpha", 2));
    var step = e.Step([At(1, "EVE - Alpha", opening), At(2, "EVE", opening)], now);
    Same(step.Moves.Count, 0, "nothing running at the first look moves");

    step = e.Step([At(1, "EVE - Alpha", opening), At(2, "EVE", opening)], now.AddSeconds(1));
    Same(step.Moves.Count, 0, "and it doesn't move on later looks");

    // One that was at the login screen isn't being flown, so its login is a normal login.
    step = e.Step([At(1, "EVE - Alpha", opening), At(2, "EVE - Bravo", opening)], now.AddSeconds(2));
    Same(step.Moves.Single(), new ClientMove(2, c1), "a client that was at login is placed when it logs in");
}
{
    // A starting client takes an empty cell on its own monitor.
    var e = Engine(out var c1, out var c2, out _);
    e.Step([], now);
    var step = e.Step([At(1, "EVE", opening)], now);
    Same(step.Moves.Single(), new ClientMove(1, c1), "first client takes the first cell");
    step = e.Step([At(1, "EVE", c1), At(2, "EVE", opening)], now.AddSeconds(1));
    Same(step.Moves.Single(), new ClientMove(2, c2), "the next takes the next");

    // No empty cell: it stays where it opened.
    step = e.Step([At(1, "EVE", c1), At(2, "EVE", c2), At(3, "EVE", opening)], now.AddSeconds(2));
    Same(step.Moves.Count, 0, "no empty cell, it stays");

    // A closed client frees its cell.
    e.Step([At(2, "EVE", c2), At(3, "EVE", opening)], now.AddSeconds(3));
    step = e.Step([At(2, "EVE", c2), At(3, "EVE", opening), At(4, "EVE", opening)], now.AddSeconds(4));
    Same(step.Moves.Single(), new ClientMove(4, c1), "a closed client's cell is free again");
}
{
    // On a monitor with no layout, it's left alone.
    var e = Engine(out _, out _, out _);
    e.UsePlan(new PlacementPlan());
    e.Step([], now);
    Same(e.Step([At(1, "EVE", opening)], now).Moves.Count, 0, "no layout, no move");
}
{
    // It takes a cell on the monitor it opened on, not any other.
    var e = Engine(out _, out _, out var far);
    e.Step([], now);
    Same(e.Step([At(1, "EVE", openingAway)], now).Moves.Single(), new ClientMove(1, far), "its own monitor");
}
{
    // Login moves it to its character's cell, on another monitor if needed.
    var e = Engine(out var c1, out var c2, out var far, ("Alpha", 3));
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    var step = e.Step([At(1, "EVE - Alpha", c1)], now.AddSeconds(5));
    Same(step.Moves.Single(), new ClientMove(1, far), "login goes to the character's monitor");
    Check(e.SeenCharacters.Contains("Alpha"), "the name is remembered for the editor");

    // The cell it left is free for the next one.
    step = e.Step([At(1, "EVE - Alpha", far), At(2, "EVE", opening)], now.AddSeconds(6));
    Same(step.Moves.Single(), new ClientMove(2, c1), "the cell it left is free");
}
{
    // Already in the right place: nothing to do.
    var e = Engine(out var c1, out _, out _, ("Alpha", 1));
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    Same(e.Step([At(1, "EVE - Alpha", c1)], now.AddSeconds(5)).Moves.Count, 0, "already in its cell, no move");
}
{
    // A client still at login gives way: they swap.
    var e = Engine(out var c1, out var c2, out _, ("Alpha", 2));
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    e.Step([At(1, "EVE", c1), At(2, "EVE", opening)], now.AddSeconds(1));
    var step = e.Step([At(1, "EVE - Alpha", c1), At(2, "EVE", c2)], now.AddSeconds(5));
    Same(step.Moves.Count, 2, "swap moves both");
    Same(step.Moves[0], new ClientMove(1, c2), "the character goes to its cell");
    Same(step.Moves[1], new ClientMove(2, c1), "the waiting client takes the other");
}
{
    // A logged-in character holding the cell is never moved, and the app says so.
    var e = Engine(out var c1, out var c2, out _, ("Alpha", 1), ("Bravo", 1));
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    e.Step([At(1, "EVE - Alpha", c1), At(2, "EVE", opening)], now.AddSeconds(1));
    var step = e.Step([At(1, "EVE - Alpha", c1), At(2, "EVE - Bravo", c2)], now.AddSeconds(5));
    Same(step.Moves.Count, 0, "a logged-in character's cell isn't taken");
    Same(step.Notices.Single().Kind, PlacementNoticeKind.PositionInUse, "the app says so");
    Same(step.Notices.Single().Character, "Bravo", "about the right character");
}
{
    // A group: whichever logs in goes there, and a client can change character.
    var e = Engine(out var c1, out var c2, out _, ("Alpha", 1), ("Bravo", 1));
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    Same(e.Step([At(1, "EVE - Bravo", c1)], now.AddSeconds(1)).Moves.Count, 0, "Bravo is in the same group");

    // Logging out stays where it is.
    Same(e.Step([At(1, "EVE", c1)], now.AddSeconds(2)).Moves.Count, 0, "logging out doesn't move it");
}
{
    // A character in no group keeps the cell its client took at login, and nothing is said.
    var e = Engine(out var c1, out _, out _);
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    var step = e.Step([At(1, "EVE - Echo", c1)], now.AddSeconds(5));
    Same(step.Moves.Count, 0, "no group, it keeps its cell");
    Same(step.Notices.Count, 0, "and that's no problem");
}
{
    // A character in no group with no cell takes a free one, one no group is set for first.
    var e = Engine(out var c1, out var c2, out _, ("Alpha", 1));
    e.Step([], now);
    Same(e.Step([At(1, "EVE - Echo", opening)], now).Moves.Single(), new ClientMove(1, c2), "an ungrouped cell first");

    // With only a grouped cell free, it takes that rather than staying out.
    e = Engine(out c1, out c2, out _, ("Alpha", 2));
    e.Step([], now);
    e.Step([At(2, "EVE", opening)], now);
    var step = e.Step([At(2, "EVE", c1), At(1, "EVE - Echo", opening)], now.AddSeconds(1));
    Same(step.Moves.Single(), new ClientMove(1, c2), "the free one, grouped or not");
}
{
    // No free cell at all: it stays, and the app says so.
    var e = Engine(out var c1, out var c2, out _);
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    e.Step([At(1, "EVE", c1), At(2, "EVE", opening)], now.AddSeconds(1));
    var step = e.Step([At(1, "EVE", c1), At(2, "EVE", c2), At(3, "EVE - Echo", opening)], now.AddSeconds(2));
    Same(step.Moves.Count, 0, "nowhere to go");
    Same(step.Notices.Single().Kind, PlacementNoticeKind.CharacterHasNoPosition, "and it's reported");
}
{
    // Starting already logged in, as a client restart can look: straight to its position.
    var e = Engine(out _, out var c2, out _, ("Alpha", 2));
    e.Step([], now);
    Same(e.Step([At(1, "EVE - Alpha", opening)], now).Moves.Single(), new ClientMove(1, c2), "a new logged-in client goes to its position");
}
{
    // Not in Fixed Window: left alone, once told, whatever it does.
    var e = Engine(out _, out _, out _, ("Alpha", 1));
    e.Step([], now);
    var step = e.Step([At(1, "EVE", opening, borderless: false)], now);
    Same(step.Moves.Count, 0, "a framed client is not placed");
    Same(step.Notices.Single().Kind, PlacementNoticeKind.NotFixedWindow, "the app says why");
    step = e.Step([At(1, "EVE - Alpha", opening, borderless: false)], now.AddSeconds(5));
    Same(step.Moves.Count, 0, "not at login either");
    Same(step.Notices.Count, 0, "and it's only told once");
}
{
    // Moved by hand: left alone from then on, and its cell is free.
    var e = Engine(out var c1, out var c2, out _, ("Alpha", 2));
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    e.Step([At(1, "EVE", c1)], now.AddSeconds(1));
    var moved = new LayoutRect(50, 50, 900, 700);
    var step = e.Step([At(1, "EVE", moved)], now.AddSeconds(2));
    Same(step.Moves.Count, 0, "we don't put it back");
    Same(step.Notices.Single().Kind, PlacementNoticeKind.MovedByHand, "it's noted");

    step = e.Step([At(1, "EVE - Alpha", moved)], now.AddSeconds(3));
    Same(step.Moves.Count, 0, "login doesn't move a client left alone either");

    step = e.Step([At(1, "EVE - Alpha", moved), At(2, "EVE", opening)], now.AddSeconds(4));
    Same(step.Moves.Single(), new ClientMove(2, c1), "its old cell is free");
}
{
    // A move still on its way isn't taken for a hand; one that never lands is let go without a fight.
    var e = Engine(out var c1, out _, out _);
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    var step = e.Step([At(1, "EVE", opening)], now.AddSeconds(1));
    Same(step.Notices.Count, 0, "still on its way");
    Same(step.Moves.Count, 0, "and it isn't sent twice");
    step = e.Step([At(1, "EVE", opening)], now + ClientPlacementEngine.MoveTimeout);
    Same(step.Moves.Count, 0, "it's never sent again");
    Same(step.Notices.Single().Kind, PlacementNoticeKind.MoveDidNotHold, "it's reported");
    Same(e.Step([At(1, "EVE", opening)], now.AddMinutes(1)).Notices.Count, 0, "once");
}
{
    // Minimized: Windows parks it far off screen, which says nothing.
    var parked = new LayoutRect(-32000, -32000, 160, 28);
    var e = Engine(out var c1, out _, out _);
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    e.Step([At(1, "EVE", c1)], now.AddSeconds(1));
    var step = e.Step([At(1, "EVE", parked, minimized: true)], now.AddSeconds(2));
    Same(step.Notices.Count, 0, "minimizing isn't moving");
    Same(step.Moves.Count, 0, "and it isn't moved");
    Same(e.ClientsOn(bigBounds), 1, "it still counts as on its monitor");
    Same(e.Step([At(1, "EVE", c1)], now.AddSeconds(3)).Notices.Count, 0, "restored, all is well");

    // One already minimized when first seen is no surprise either.
    e.Step([At(1, "EVE", c1), At(2, "EVE", parked, minimized: true)], now.AddSeconds(4));
    Same(e.ClientsOn(bigBounds), 1, "a window parked off screen is on no monitor");
}
{
    // Apply: places everything afresh, characters first, and forgives what was left alone.
    var e = Engine(out var c1, out var c2, out var far, ("Alpha", 2), ("Bravo", 3));
    var w1 = At(1, "EVE", opening);
    var w2 = At(2, "EVE - Alpha", opening);
    var w3 = At(3, "EVE - Bravo", new LayoutRect(100, 100, 900, 700));
    var w4 = At(4, "EVE - Echo", new LayoutRect(200, 200, 900, 700));
    var w5 = At(5, "EVE", opening, borderless: false);
    e.Step([w1, w2, w3, w4, w5], now);
    var step = e.Apply([w1, w2, w3, w4, w5], now.AddSeconds(1));
    Check(step.Moves.Contains(new ClientMove(2, c2)), "a character goes to its position");
    Check(step.Moves.Contains(new ClientMove(3, far)), "on any monitor");
    Check(step.Moves.Contains(new ClientMove(4, c1)), "a character in no group takes a free cell");
    Check(!step.Moves.Any(m => m.Handle == 1), "before a client at login, which finds none left");
    Check(!step.Moves.Any(m => m.Handle == 5), "a framed client is not placed");
    Check(!step.Notices.Any(n => n.Kind == PlacementNoticeKind.CharacterHasNoPosition), "nobody is left without a place");

    // Applying again with everything in place moves nothing.
    var settled = new[] { w1, At(2, "EVE - Alpha", c2), At(3, "EVE - Bravo", far), At(4, "EVE - Echo", c1), w5 };
    e.Step(settled, now.AddSeconds(2));
    Same(e.Apply(settled, now.AddSeconds(3)).Moves.Count, 0, "nothing to do the second time");
}
{
    // Apply gives back a client that was moved by hand.
    var e = Engine(out var c1, out _, out _);
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    e.Step([At(1, "EVE", c1)], now.AddSeconds(1));
    var moved = At(1, "EVE", new LayoutRect(50, 50, 900, 700));
    e.Step([moved], now.AddSeconds(2));
    Same(e.Apply([moved], now.AddSeconds(3)).Moves.Single(), new ClientMove(1, c1), "Apply places it again");

    // And after that it's watched again.
    e.Step([At(1, "EVE", c1)], now.AddSeconds(4));
    Same(e.Step([moved], now.AddSeconds(5)).Notices.Single().Kind, PlacementNoticeKind.MovedByHand, "and is noticed again");
}
{
    // Two logged-in characters sharing a position: the first gets it, the second stays and is told.
    var e = Engine(out var c1, out _, out _, ("Alpha", 1), ("Bravo", 1));
    var w1 = At(1, "EVE - Alpha", opening);
    var w2 = At(2, "EVE - Bravo", new LayoutRect(100, 100, 900, 700));
    e.Step([w1, w2], now);
    var step = e.Apply([w1, w2], now.AddSeconds(1));
    Same(step.Moves.Single(), new ClientMove(1, c1), "only one gets the cell");
    Check(step.Notices.Any(n => n.Kind == PlacementNoticeKind.PositionInUse && n.Character == "Bravo"), "the other is told");
}
{
    // A client sitting exactly in a cell when the app starts holds it.
    var e = Engine(out var c1, out var c2, out _);
    e.Step([At(1, "EVE - Echo", c1)], now);
    Same(e.Step([At(1, "EVE - Echo", c1), At(2, "EVE", opening)], now.AddSeconds(1)).Moves.Single(), new ClientMove(2, c2), "a cell already in use isn't handed out");
}
{
    // A new plan moves nothing, and a removed cell stops being held.
    var e = Engine(out var c1, out var c2, out _);
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    e.Step([At(1, "EVE", c1)], now.AddSeconds(1));
    var replacement = new LayoutArea { Content = AreaContent.Client };
    var plan = ClientLayoutPlanner.Build(new AppliedDesktop
    {
        Monitors = [new AppliedMonitor { HardwareKey = Big, Definition = ClientLayoutJson.Write(replacement) }],
    }, plugged);
    e.UsePlan(plan.Placement);
    Same(e.Step([At(1, "EVE", c1)], now.AddSeconds(2)).Moves.Count, 0, "a new plan moves nothing by itself");
    Same(e.Step([At(1, "EVE", c1), At(2, "EVE", opening)], now.AddSeconds(3)).Moves.Single(), new ClientMove(2, plan.Placement.Monitors[0].Cells[0].Rect), "a new client uses the new plan");
}
{
    // What panels on a monitor go by.
    var e = Engine(out var c1, out _, out var far);
    e.Step([], now);
    Same(e.ClientsOn(bigBounds), 0, "nobody yet");
    e.Step([At(1, "EVE", opening)], now);
    Same(e.ClientsOn(bigBounds), 1, "the first client");
    Same(e.ClientHandlesOn(bigBounds).Single(), 1L, "which is this one");
    e.Step([At(1, "EVE", far)], now.AddSeconds(1));
    Same(e.ClientsOn(bigBounds), 0, "moved away, none left there");
    Same(e.ClientsOn(smallBounds), 1, "it's over here now");
    Check(!e.ClientHandlesOn(bigBounds).Any(), "so it no longer covers the first monitor's taskbar");
    Same(e.ClientHandlesOn(smallBounds).Single(), 1L, "but this one's");
    e.Step([], now.AddSeconds(2));
    Same(e.ClientsOn(smallBounds), 0, "closed, none left");
    Check(!e.ClientHandlesOn(smallBounds).Any(), "and nothing to cover with");
}

{
    // A layout to a file and back, as another database would read it.
    // Controls go inside a panel: the root is one, holding a client.
    var root = ClientLayoutEditing.NewRoot();
    var inside = root.Inside!;
    inside.Docked.Add(new DockedPanel
    {
        Edge = DockEdge.Top, Size = 40, Name = "Voice", Control = "Mumble",
        Options = new ControlOptions { MumbleServerId = 7, MumbleMode = "Wide" },
    });
    inside.Docked.Add(new DockedPanel { Edge = DockEdge.Left, Control = "Mumble", Options = new ControlOptions { MumbleServerId = 9 } });
    var here = new List<EveConsole.Models.MumbleServer>
    {
        new() { Id = 7, Name = "Fleet", Host = "voice.example.org", Port = 64738 },
        new() { Id = 9, Name = "Other", Host = "other.example.org", Port = 64738 },
    };
    using var file = new MemoryStream();
    ClientLayoutFile.WriteAsync(file, "Main screen", root, here).GetAwaiter().GetResult();
    var written = System.Text.Encoding.UTF8.GetString(file.ToArray());
    Check(written.Contains("voice.example.org") && written.Contains("other.example.org"), "the file names each Mumble server a control uses by address");

    // Elsewhere the same server has another id, and the second isn't known.
    var there = new List<EveConsole.Models.MumbleServer> { new() { Id = 3, Host = "VOICE.example.org ", Port = 64738 } };
    file.Position = 0;
    var (name, back) = ClientLayoutFile.ReadAsync(file, there).GetAwaiter().GetResult();
    Same(name, "Main screen", "the name comes back");
    var sent = ClientLayoutEditing.Controls(root).ToList();
    var got = ClientLayoutEditing.Controls(back).ToList();
    Same(got.Count, 2, "both docked controls come back");
    Check(got.Select(c => c.Id).SequenceEqual(sent.Select(c => c.Id)), "ids are kept, as a menu names a Tool Container by its id");
    Same(got[0].Options?.MumbleServerId, (int?)3, "a Mumble control is tied to the server at the same address here");
    Same(got[0].Options?.MumbleMode, "Wide", "and keeps its other choices");
    Same(got[1].Options?.MumbleServerId, (int?)null, "one whose server isn't here is left to be picked");
    sent[0].Options!.MumbleServerId = 3;
    sent[1].Options!.MumbleServerId = null;
    Same(ClientLayoutJson.Write(back), ClientLayoutJson.Write(root), "apart from that, the layout is unchanged");

    bool Refused(string text, bool newer)
    {
        try { ClientLayoutFile.ReadAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), here).GetAwaiter().GetResult(); return false; }
        catch (LayoutFileException e) { return e.Newer == newer; }
    }
    Check(Refused("not json at all", newer: false), "a file that isn't JSON is refused as not a layout");
    Check(Refused("""{"name":"x","layout":{}}""", newer: false), "JSON without the format mark is refused");
    Check(Refused("""{"format":"eveconsole-client-layout","version":"one","layout":{}}""", newer: false), "a version that isn't a number is refused");
    Check(Refused("""{"format":"eveconsole-client-layout","version":99,"layout":{}}""", newer: true), "a newer version says so");
}

{
    // The templates that ship with the app: each one a layout, and nothing in it from one person's setup.
    var repo = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (repo != null && !File.Exists(Path.Combine(repo.FullName, "EveConsole.csproj"))) repo = repo.Parent;
    var folder = repo is null ? null : Path.Combine(repo.FullName, "LayoutTemplates");
    var files = folder != null && Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : [];
    Check(files.Length > 0, "the shipped layout templates are found (run from the repository)");
    foreach (var path in files)
    {
        var shown = Path.GetFileName(path);
        try
        {
            using var stream = File.OpenRead(path);
            var (name, root) = ClientLayoutFile.ReadAsync(stream, []).GetAwaiter().GetResult();
            Check(name.Length > 0, $"{shown}: has a name");
            Check(root.Content == AreaContent.Panel || root.Docked.Count > 0, $"{shown}: holds a layout");
            var text = File.ReadAllText(path);
            Check(!System.Text.RegularExpressions.Regex.IsMatch(text, "\"MumbleServerId\":\\s*\\d") && text.Contains("\"mumbleServers\": []"),
                  $"{shown}: names no Mumble server, which is one person's");
            Check(ClientLayoutEditing.Controls(root).All(c => string.IsNullOrEmpty(c.Options?.GroupChatRoom)), $"{shown}: names no chat room, which is one person's");
        }
        catch (LayoutFileException)
        {
            Check(false, $"{shown}: is a layout file");
        }
    }

    var blank = ClientLayoutEditing.BlankRoot();
    Check(blank.Content == AreaContent.Panel && blank.Inside is null && blank.Docked.Count == 0, "a blank layout is one empty panel");
}

Console.WriteLine(failures == 0
    ? $"PASSED: client layout check: all {passes} passed."
    : $"FAILED: client layout check: {failures} failed, {passes} passed.");
return failures == 0 ? 0 : 1;
