using System.Collections.ObjectModel;
using System.Globalization;
using EveConsole.Localization;
using EveConsole.Services.ClientLayouts;
using EveConsole.Views;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// One line of the layout editor's outline: something in the layout, with what it holds below it.
/// <see cref="Id"/> is what selecting it selects; null for a line that only groups others.
/// </summary>
public sealed class OutlineNodeVm(string? id, string label, string detail) : ReactiveObject
{
    public string? Id { get; } = id;
    public string Label { get; } = label;

    /// <summary>Where it sits, in a word or two: its edge, Full, a grid's size.</summary>
    public string Detail { get; } = detail;

    public bool HasDetail => Detail.Length > 0;
    public ObservableCollection<OutlineNodeVm> Children { get; } = [];
    public OutlineNodeVm? Parent { get; private set; }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            Expanded?.Invoke(this, value);
        }
    }

    private bool _isDropTarget;
    /// <summary>Where a line being dragged in the outline would go, if let go now.</summary>
    public bool IsDropTarget { get => _isDropTarget; set => this.RaiseAndSetIfChanged(ref _isDropTarget, value); }

    /// <summary>For the outline to remember what was folded away, across rebuilds.</summary>
    internal Action<OutlineNodeVm, bool>? Expanded { get; set; }

    internal OutlineNodeVm Add(OutlineNodeVm child)
    {
        child.Parent = this;
        Children.Add(child);
        return child;
    }

    internal string Key => Id ?? "monitor";
}

/// <summary>
/// The outline of a layout, as the editor's tree shows it: the monitor, the panels on it, and what
/// each holds, in dock order and then what fills it — Panel › Grid › Panel › Client, the same way
/// up that the right-click menu's Parent goes.
/// </summary>
public static class LayoutOutline
{
    public static OutlineNodeVm Build(LayoutArea root)
    {
        var clients = ClientLayoutCalculator.ClientAreaIds(root).ToList();
        // The monitor is its own line only while nothing fills it; otherwise the panel filling it is.
        var monitor = new OutlineNodeVm(root.Content == AreaContent.Panel ? null : root.Id, ClientLayoutsText.ParentMonitor, "");
        Area(monitor, root, clients);
        return monitor;
    }

    /// <summary>What's docked in an area, in dock order, then what fills it.</summary>
    private static void Area(OutlineNodeVm into, LayoutArea area, List<string> clients)
    {
        foreach (var docked in area.Docked) Docked(into, docked, clients);
        var full = ClientLayoutsText.MenuEdgeFull;
        switch (area.Content)
        {
            case AreaContent.Panel:
                Inside(into.Add(new OutlineNodeVm(area.Id, PanelName(area.Name), full)), area.Inside, clients);
                break;
            case AreaContent.Grid when area.Grid != null:
                Cells(into.Add(new OutlineNodeVm(area.Id, ClientLayoutsText.ParentGrid, Size(area.Grid) + " · " + full)), area.Grid, clients);
                break;
            case AreaContent.Control:
                into.Add(new OutlineNodeVm(area.Id, ClientLayoutPainter.ControlName(area.Control), full));
                break;
            case AreaContent.Client:
                into.Add(new OutlineNodeVm(area.Id, ClientName(area.Id, clients), full));
                break;
        }
    }

    private static void Docked(OutlineNodeVm into, DockedPanel docked, List<string> clients)
    {
        var edge = EdgeName(docked.Edge);
        if (docked.Grid != null)
            Cells(into.Add(new OutlineNodeVm(docked.Id, ClientLayoutsText.ParentGrid, Size(docked.Grid) + " · " + edge)), docked.Grid, clients);
        else if (PanelControls.IsClient(docked.Control))
            into.Add(new OutlineNodeVm(docked.Id, ClientName(docked.Id, clients), edge));
        else if (docked.Control != null)
            into.Add(new OutlineNodeVm(docked.Id, Named(docked.Name, ClientLayoutPainter.ControlName(docked.Control)), edge));
        else
            Inside(into.Add(new OutlineNodeVm(docked.Id, PanelName(docked.Name), edge)), docked.Inside, clients);
    }

    private static void Inside(OutlineNodeVm panel, LayoutArea? inside, List<string> clients)
    {
        if (inside != null) Area(panel, inside, clients);
    }

    private static void Cells(OutlineNodeVm grid, LayoutGrid cells, List<string> clients)
    {
        var columns = Math.Max(cells.Columns.Count, 1);
        for (var i = 0; i < cells.Cells.Count; i++)
        {
            var cell = cells.Cells[i];
            var where = string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.OutlineCell, i / columns + 1, i % columns + 1);
            Inside(grid.Add(new OutlineNodeVm(cell.Id, PanelName(cell.Name), where)), cell.Inside, clients);
        }
    }

    private static string Size(LayoutGrid grid) =>
        string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.CanvasSize, Math.Max(grid.Columns.Count, 1), Math.Max(grid.Rows.Count, 1));

    private static string ClientName(string id, List<string> clients) =>
        string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.CanvasClient, clients.IndexOf(id) + 1);

    private static string PanelName(string name) => name.Length > 0 ? name : ClientLayoutsText.PanelDefaultName;

    /// <summary>A Tool Container's name before what it is, so two of them can be told apart.</summary>
    private static string Named(string name, string what) => name.Length > 0 ? name + " · " + what : what;

    private static string EdgeName(DockEdge edge) => edge switch
    {
        DockEdge.Top => ClientLayoutsText.MenuEdgeTop,
        DockEdge.Bottom => ClientLayoutsText.MenuEdgeBottom,
        DockEdge.Left => ClientLayoutsText.MenuEdgeLeft,
        _ => ClientLayoutsText.MenuEdgeRight,
    };

    /// <summary>Every line, depth first, for finding one and for telling whether anything changed.</summary>
    public static IEnumerable<(OutlineNodeVm Node, int Depth)> Walk(OutlineNodeVm node, int depth = 0)
    {
        yield return (node, depth);
        foreach (var child in node.Children)
            foreach (var below in Walk(child, depth + 1))
                yield return below;
    }
}
