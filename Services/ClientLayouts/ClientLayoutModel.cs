using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveConsole.Services.ClientLayouts;

/// <summary>Which edge of an area a panel or control is docked to.</summary>
public enum DockEdge { Top, Bottom, Left, Right }

/// <summary>An edge, as <see cref="DockEdge"/> numbers them, or the whole of an area's middle.</summary>
public enum DockPlace { Top = DockEdge.Top, Bottom = DockEdge.Bottom, Left = DockEdge.Left, Right = DockEdge.Right, Full }

/// <summary>What the middle of an area holds, once what's docked in it has taken its share.</summary>
public enum AreaContent { Empty, Client, Panel, Grid, Control }

/// <summary>
/// The one building block of a layout. A monitor's layout is an area, a grid's cells are areas, and
/// the inside of a panel is an area.
///
/// <para>⚠️ Only panels go on the monitor itself: docked to its edges, or one filling its middle.
/// Everything else — controls, clients, grids, more panels — goes inside a panel. And a grid's cells
/// are all panels: a grid is how a panel is divided into more panels. Layouts saved before that rule
/// are brought into line as they're read (see <see cref="ClientLayoutMigration"/>).</para>
///
/// <para>⚠️ A layout holds shape only. Which characters run in a client area is the monitor's
/// business, because one layout can be used on several monitors. <see cref="Id"/> is what ties
/// those assignments to the area, so it has to survive every edit of the layout.</para>
///
/// <para>A client sits inside a panel, docked to an edge or filling it. The panel's window has a hole
/// cut where the client is, so it shows through and gets its own clicks.</para>
/// </summary>
public sealed class LayoutArea
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>In dock order: the first takes its full edge, the next fits in what is left.</summary>
    public List<DockedPanel> Docked { get; set; } = [];

    public AreaContent Content { get; set; } = AreaContent.Empty;

    /// <summary>Only when <see cref="Content"/> is <see cref="AreaContent.Grid"/>.</summary>
    public LayoutGrid? Grid { get; set; }

    /// <summary>The user's name for a panel that fills this area.</summary>
    public string Name { get; set; } = "";

    /// <summary>What a panel that fills this area holds. Null until something is put in it.</summary>
    public LayoutArea? Inside { get; set; }

    /// <summary>How a panel that fills this area looks.</summary>
    public PanelLook? Look { get; set; }

    /// <summary>Only when <see cref="Content"/> is <see cref="AreaContent.Control"/>: docked full. See <see cref="PanelControls"/>.</summary>
    public string? Control { get; set; }

    /// <summary>What's been chosen for that control.</summary>
    public ControlOptions? Options { get; set; }
}

/// <summary>
/// A panel, control, client or grid docked to one edge of an area. <see cref="Size"/> is its width
/// or its height, whichever way it's docked, in pixels at 100%. With <see cref="Grid"/> set it's a
/// grid; with <see cref="Control"/> set it's a control, or a client when that names
/// <see cref="PanelControlKind.Client"/>; otherwise it's a panel, which can hold more in
/// <see cref="Inside"/>. A client's id is what its characters are assigned by, as a client area's is.
/// </summary>
public sealed class DockedPanel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DockEdge Edge { get; set; }
    public int Size { get; set; } = 100;
    public string Name { get; set; } = "";

    /// <summary>Which control this is, by name. See <see cref="PanelControls"/>.</summary>
    public string? Control { get; set; }

    /// <summary>What's been chosen for that control.</summary>
    public ControlOptions? Options { get; set; }

    /// <summary>What this panel holds. Null until something is put in it.</summary>
    public LayoutArea? Inside { get; set; }

    public PanelLook? Look { get; set; }

    /// <summary>A grid docked to the edge: its cells are panels.</summary>
    public LayoutGrid? Grid { get; set; }
}

/// <summary>What's been chosen for a control that needs choices.</summary>
public sealed class ControlOptions
{
    /// <summary>The one Mumble server a Mumble control is for. A control is never for several.</summary>
    public int? MumbleServerId { get; set; }

    /// <summary>A <see cref="MumbleControlMode"/> by name, or null for a single line.</summary>
    public string? MumbleMode { get; set; }

    /// <summary>A <see cref="WorklistControlMode"/> by name, or null for the list.</summary>
    public string? WorklistMode { get; set; }

    /// <summary>An <see cref="IntelControlMode"/> by name, or null for the list.</summary>
    public string? IntelMode { get; set; }

    /// <summary>How far an Intel control looks, in jumps; null for ten (<see cref="PanelControls.IntelRangeOf"/>).</summary>
    public int? IntelMaxJumps { get; set; }

    /// <summary>
    /// Where the menu opens tools: <see cref="ToolTargets.NewWindow"/>, <see cref="ToolTargets.LastUsed"/>,
    /// or a Tool Container's id. Null is the last used window.
    /// </summary>
    public string? MenuTarget { get; set; }

    /// <summary>A Tool Container holding tabs, rather than one tool.</summary>
    public bool? ContainerTabs { get; set; }

    /// <summary>A Tool Container of tabs that can split in two, as the app's own tab windows do.</summary>
    public bool? ContainerSplit { get; set; }

    /// <summary>The tools a Tool Container opens with, in order.</summary>
    public List<ContainerTool>? ContainerTools { get; set; }

    /// <summary>
    /// The room a Group Chat control opens on: a Jabber room or a Mumble server, as the Group Chat
    /// tool's list names it (kind, server and room). Its dropdown can show another for a while.
    /// </summary>
    public string? GroupChatRoom { get; set; }
}

/// <summary>A tool a Tool Container opens with.</summary>
public sealed class ContainerTool
{
    /// <summary>The tool, by the id the left navigation opens it with.</summary>
    public string ToolId { get; set; } = "";

    /// <summary>Whether its tab can be closed. One that can't also stays in its container.</summary>
    public bool CanClose { get; set; } = true;

    /// <summary>For the Overview: which of its saved layouts it opens with, by key. Null or empty is Default.</summary>
    public string? Layout { get; set; }
}

/// <summary>How a panel looks. Null values mean the defaults, which match the game's own UI.</summary>
public sealed class PanelLook
{
    /// <summary>"#RRGGBB", or null for the theme's page color (see <see cref="PanelLooks.ColorOf"/>).</summary>
    public string? Color { get; set; }

    /// <summary>A <see cref="PanelStyle"/> by name, or null for flat.</summary>
    public string? Style { get; set; }

    /// <summary>Room kept free inside the panel, past its edge's own margin. Null is none.</summary>
    public LayoutPadding? Padding { get; set; }
}

/// <summary>
/// Room kept free around what an area holds, in pixels at 100%: each side grows with the UI scale,
/// like a docked size. All four default to 0.
/// </summary>
public sealed class LayoutPadding
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }

    public bool IsNone => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

/// <summary>
/// A grid of areas, row by row. Column widths and row heights are proportions rather than pixels,
/// so the same layout fits a 4K monitor and a 1440p one.
/// </summary>
public sealed class LayoutGrid
{
    public List<double> Columns { get; set; } = [];
    public List<double> Rows { get; set; } = [];

    /// <summary>Columns.Count x Rows.Count areas, across and then down.</summary>
    public List<LayoutArea> Cells { get; set; } = [];

    /// <summary>
    /// Room kept free round the cells and between them, so the panels in them don't touch: padding 3
    /// leaves 3 at the edges and 3 between each cell and the next (see the calculator for uneven sides).
    /// Null is none. A grid has no edge or colour of its own: the panel it's in, or its cells, have those.
    /// </summary>
    public LayoutPadding? Padding { get; set; }


    /// <summary>A grid of equal cells, each an empty panel.</summary>
    public static LayoutGrid Equal(int columns, int rows)
    {
        var grid = new LayoutGrid
        {
            Columns = [.. Enumerable.Repeat(1.0, columns)],
            Rows = [.. Enumerable.Repeat(1.0, rows)],
        };
        for (var i = 0; i < columns * rows; i++) grid.Cells.Add(new LayoutArea { Content = AreaContent.Panel });
        return grid;
    }
}

/// <summary>A rectangle in real screen pixels.</summary>
public readonly record struct LayoutRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Layouts are stored as JSON: one tree, read and written whole.</summary>
public static class ClientLayoutJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write(LayoutArea root) => JsonSerializer.Serialize(root, Options);

    /// <summary>An empty area if the text can't be read, so a damaged row can't stop the app.</summary>
    public static LayoutArea Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new LayoutArea();
        try
        {
            // Read as it's meant to be now, whenever it was saved.
            return ClientLayoutMigration.Normalize(JsonSerializer.Deserialize<LayoutArea>(json, Options) ?? new LayoutArea());
        }
        catch (JsonException)
        {
            return new LayoutArea();
        }
    }
}
