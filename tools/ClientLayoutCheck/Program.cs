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
    Same(result.Empties.Count, 3, "with the cells it has");
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
    var root = new LayoutArea { Content = AreaContent.Grid, Grid = LayoutGrid.Equal(2, 1) };
    root.Grid.Cells[0].Content = AreaContent.Client;
    root.Grid.Cells[1].Content = AreaContent.Panel;
    root.Grid.Cells[1].Name = "Map";
    root.Grid.Columns[0] = 2.5;
    ClientLayoutEditing.Dock(root, DockEdge.Right, 77, "side");
    var back = ClientLayoutJson.Read(ClientLayoutJson.Write(root));
    Same(back.Id, root.Id, "ids survive storage");
    Same(back.Grid!.Cells[0].Id, root.Grid.Cells[0].Id, "cell ids survive storage");
    Same(back.Grid.Cells[1].Name, "Map", "names survive storage");
    Same(back.Grid.Columns[0], 2.5, "shares survive storage");
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

// ---- The placement rules ----
var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
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
    Same(step.Notices.Single().Character, "Bravo", "a client that was at login is handled when it logs in");
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
    // A character in no group stays, and the app offers to add it.
    var e = Engine(out var c1, out _, out _);
    e.Step([], now);
    e.Step([At(1, "EVE", opening)], now);
    var step = e.Step([At(1, "EVE - Echo", c1)], now.AddSeconds(5));
    Same(step.Moves.Count, 0, "no position, it stays");
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
    Check(step.Moves.Contains(new ClientMove(1, c1)), "a client at login takes what's left");
    Check(!step.Moves.Any(m => m.Handle == 4), "a character with no position stays");
    Check(!step.Moves.Any(m => m.Handle == 5), "a framed client is not placed");
    Check(step.Notices.Any(n => n.Kind == PlacementNoticeKind.CharacterHasNoPosition && n.Character == "Echo"), "the unplaced character is mentioned");

    // Applying again with everything in place moves nothing.
    var settled = new[] { At(1, "EVE", c1), At(2, "EVE - Alpha", c2), At(3, "EVE - Bravo", far), w4, w5 };
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
    e.Step([At(1, "EVE", far)], now.AddSeconds(1));
    Same(e.ClientsOn(bigBounds), 0, "moved away, none left there");
    Same(e.ClientsOn(smallBounds), 1, "it's over here now");
    e.Step([], now.AddSeconds(2));
    Same(e.ClientsOn(smallBounds), 0, "closed, none left");
}

Console.WriteLine(failures == 0
    ? $"PASSED: client layout check: all {passes} passed."
    : $"FAILED: client layout check: {failures} failed, {passes} passed.");
return failures == 0 ? 0 : 1;
