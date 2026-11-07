using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveConsole.Services.ClientLayouts;

/// <summary>Which edge of an area a panel or control is docked to.</summary>
public enum DockEdge { Top, Bottom, Left, Right }

/// <summary>What the middle of an area holds, once what's docked in it has taken its share.</summary>
public enum AreaContent { Empty, Client, Panel, Grid, Control }

/// <summary>
/// The one building block of a layout. A monitor's layout is an area, a grid's cells are areas, and
/// the inside of a panel is an area.
///
/// <para>⚠️ A layout holds shape only. Which characters run in a client area is the monitor's
/// business, because one layout can be used on several monitors. <see cref="Id"/> is what ties
/// those assignments to the area, so it has to survive every edit of the layout.</para>
///
/// <para>A client can sit in any area, including inside a panel. Its panel's window has a hole cut
/// where the client is, so it shows through and gets its own clicks.</para>
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
/// A panel or control docked to one edge of an area. <see cref="Size"/> is its width or its height,
/// whichever way it's docked, in pixels on every monitor. With <see cref="Control"/> set it's a
/// control; otherwise it's a panel, which can hold more of the same in <see cref="Inside"/>.
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
}

/// <summary>A tool a Tool Container opens with.</summary>
public sealed class ContainerTool
{
    /// <summary>The tool, by the id the left navigation opens it with.</summary>
    public string ToolId { get; set; } = "";

    /// <summary>Whether its tab can be closed. One that can't also stays in its container.</summary>
    public bool CanClose { get; set; } = true;
}

/// <summary>How a panel looks. Null values mean the defaults, which match the game's own UI.</summary>
public sealed class PanelLook
{
    /// <summary>"#RRGGBB", or null for <see cref="PanelLooks.DefaultColor"/>.</summary>
    public string? Color { get; set; }

    /// <summary>A <see cref="PanelStyle"/> by name, or null for flat.</summary>
    public string? Style { get; set; }
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

    /// <summary>A grid of equal cells, all empty.</summary>
    public static LayoutGrid Equal(int columns, int rows)
    {
        var grid = new LayoutGrid
        {
            Columns = [.. Enumerable.Repeat(1.0, columns)],
            Rows = [.. Enumerable.Repeat(1.0, rows)],
        };
        for (var i = 0; i < columns * rows; i++) grid.Cells.Add(new LayoutArea());
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
            return JsonSerializer.Deserialize<LayoutArea>(json, Options) ?? new LayoutArea();
        }
        catch (JsonException)
        {
            return new LayoutArea();
        }
    }
}
