using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveConsole.Services.ClientLayouts;

/// <summary>Which edge of an area a panel is docked to.</summary>
public enum DockEdge { Top, Bottom, Left, Right }

/// <summary>What the middle of an area holds, once its docked panels have taken their share.</summary>
public enum AreaContent { Empty, Client, Panel, Grid }

/// <summary>
/// The one building block of a layout. A monitor's layout is an area; a grid's cells are areas.
///
/// <para>⚠️ A layout holds shape only. Which characters run in a client area is the monitor's
/// business, because one layout can be used on several monitors. <see cref="Id"/> is what ties
/// those assignments to the area, so it has to survive every edit of the layout.</para>
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
}

/// <summary>A panel docked to one edge of an area. Its size is in pixels on every monitor.</summary>
public sealed class DockedPanel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DockEdge Edge { get; set; }
    public int Size { get; set; } = 100;
    public string Name { get; set; } = "";
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
