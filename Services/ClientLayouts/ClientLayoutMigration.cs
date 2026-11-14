namespace EveConsole.Services.ClientLayouts;

/// <summary>
/// Brings a layout saved under the older, looser rules into line with today's: only panels on the
/// monitor, and only panels in a grid's cells (see <see cref="LayoutArea"/>).
///
/// <para>Nothing is lost and nothing moves on screen: what broke a rule is wrapped in a flat panel of
/// exactly its place, with no padding, so it draws where it did. ⚠️ And every id stays with what it
/// belonged to — the thing wrapped keeps its id and the new panel gets a fresh one — because a
/// client's id is what its characters are assigned by, and a Tool Container's is what a menu opens
/// tools in.</para>
///
/// <para>Safe to run on any layout any number of times: one already in line comes back unchanged.</para>
/// </summary>
public static class ClientLayoutMigration
{
    public static LayoutArea Normalize(LayoutArea root)
    {
        // The monitor: what's docked on it that isn't a panel goes in a panel docked in its place.
        for (var i = 0; i < root.Docked.Count; i++)
        {
            var item = root.Docked[i];
            if (IsPanel(item)) continue;
            root.Docked[i] = new DockedPanel { Edge = item.Edge, Size = item.Size, Inside = Filling(item) };
        }

        // And a client, control or grid filling its middle goes in a panel filling it.
        if (root.Content is AreaContent.Client or AreaContent.Control or AreaContent.Grid)
        {
            var inner = new LayoutArea
            {
                Id = root.Id,
                Content = root.Content,
                Grid = root.Grid,
                Control = root.Control,
                Options = root.Options,
                Name = root.Name,
            };
            root.Id = NewId();
            root.Content = AreaContent.Panel;
            root.Inside = inner;
            root.Grid = null;
            root.Control = null;
            root.Options = null;
            root.Name = "";
            root.Look = null;
        }

        CellsArePanels(root);
        return root;
    }

    private static bool IsPanel(DockedPanel item) => item.Control == null && item.Grid == null;

    /// <summary>The inside of a panel that something docked held, filled with that thing, which keeps its id.</summary>
    private static LayoutArea Filling(DockedPanel item)
    {
        var inside = new LayoutArea { Id = item.Id, Name = item.Name };
        if (item.Grid != null)
        {
            inside.Content = AreaContent.Grid;
            inside.Grid = item.Grid;
        }
        else if (PanelControls.IsClient(item.Control))
        {
            inside.Content = AreaContent.Client;
        }
        else
        {
            inside.Content = AreaContent.Control;
            inside.Control = item.Control;
            inside.Options = item.Options;
        }
        return inside;
    }

    /// <summary>Every grid's cells, however deep: a cell that isn't simply a panel becomes a panel holding what it was.</summary>
    private static void CellsArePanels(LayoutArea area)
    {
        foreach (var docked in area.Docked)
        {
            if (docked.Inside != null) CellsArePanels(docked.Inside);
            if (docked.Grid != null) Cells(docked.Grid);
        }
        if (area.Inside != null) CellsArePanels(area.Inside);
        if (area.Grid != null) Cells(area.Grid);
    }

    private static void Cells(LayoutGrid grid)
    {
        for (var i = 0; i < grid.Cells.Count; i++)
        {
            var cell = grid.Cells[i];
            if (cell.Content != AreaContent.Panel || cell.Docked.Count > 0)
            {
                // What it held, docked things and all, under its own id; a panel round it, under a new one.
                var held = cell.Content != AreaContent.Empty || cell.Docked.Count > 0;
                grid.Cells[i] = cell = new LayoutArea
                {
                    Content = AreaContent.Panel,
                    Inside = held ? cell : null,
                };
            }
            CellsArePanels(cell);
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
