namespace EveConsole.Services.ClientLayouts;

/// <summary>What can be docked in a panel besides other panels.</summary>
public enum PanelControlKind { Settings, ServerStatus, EveTime, LocalTime, UsersOnline, Mumble, Worklist, Menu, ToolContainer }

/// <summary>How a Mumble control shows: one line with who is talking beside it, or the whole channel as a list.</summary>
public enum MumbleControlMode { SingleLine, List }

/// <summary>How a Worklist control shows: the list itself, or one line of counts that drops the list down when clicked.</summary>
public enum WorklistControlMode { List, SingleLine }

/// <summary>A panel's edge treatment.</summary>
public enum PanelStyle { Flat, Raised, Inset, Ridge }

/// <summary>
/// ⚠️ Controls and styles are saved by name as plain text rather than as enums. Layouts live in a
/// database that several copies of the app can share, and a copy that doesn't know a newer one must
/// skip it and keep it, not treat the whole layout as damaged.
/// </summary>
public static class PanelControls
{
    /// <summary>Null for a name this copy of the app doesn't know.</summary>
    public static PanelControlKind? Parse(string? name) =>
        Enum.TryParse<PanelControlKind>(name, out var kind) && Enum.IsDefined(kind) ? kind : null;

    /// <summary>The size a control starts at: enough to read it across, one line's worth down.</summary>
    public static int DefaultSize(PanelControlKind kind, DockEdge edge)
    {
        if (edge is DockEdge.Top or DockEdge.Bottom)
            return kind switch
            {
                PanelControlKind.Worklist => 300,
                PanelControlKind.ToolContainer => 400,
                _ => 28,
            };
        return kind switch
        {
            PanelControlKind.Settings or PanelControlKind.Menu => 40,
            PanelControlKind.ServerStatus => 170,
            PanelControlKind.EveTime => 110,
            PanelControlKind.LocalTime => 120,
            PanelControlKind.Mumble => 340,
            PanelControlKind.Worklist => 420,
            PanelControlKind.ToolContainer => 600,
            _ => 110,
        };
    }

    public static MumbleControlMode MumbleModeOf(ControlOptions? options) =>
        Enum.TryParse<MumbleControlMode>(options?.MumbleMode, out var mode) && Enum.IsDefined(mode) ? mode : MumbleControlMode.SingleLine;

    /// <summary>Saved before there was a choice, a Worklist control is the list.</summary>
    public static WorklistControlMode WorklistModeOf(ControlOptions? options) =>
        Enum.TryParse<WorklistControlMode>(options?.WorklistMode, out var mode) && Enum.IsDefined(mode) ? mode : WorklistControlMode.List;

    public static bool HoldsTabs(ControlOptions? options) => options?.ContainerTabs == true;

    /// <summary>Only a container of tabs splits.</summary>
    public static bool Splits(ControlOptions? options) => HoldsTabs(options) && options?.ContainerSplit == true;

    /// <summary>
    /// The tools a container opens with, as they apply: one tool and no closing it when it holds one
    /// tool, and each tool only once, as a tool is only ever open in one place.
    /// </summary>
    public static IReadOnlyList<ContainerTool> ToolsOf(ControlOptions? options)
    {
        var tools = (options?.ContainerTools ?? [])
            .Where(t => t.ToolId.Length > 0)
            .DistinctBy(t => t.ToolId)
            .Select(t => new ContainerTool { ToolId = t.ToolId, CanClose = t.CanClose, Layout = t.Layout })
            .ToList();
        if (HoldsTabs(options)) return tools;
        return [.. tools.Take(1).Select(t => new ContainerTool { ToolId = t.ToolId, CanClose = false, Layout = t.Layout })];
    }
}

/// <summary>Where a menu opens a tool, besides a Tool Container's id.</summary>
public static class ToolTargets
{
    public const string NewWindow = "NewWindow";
    public const string LastUsed = "LastUsed";

    /// <summary>The menu's target: a container's id, or one of the two above.</summary>
    public static string Of(ControlOptions? options) => options?.MenuTarget is { Length: > 0 } target ? target : LastUsed;
}

public static class PanelLooks
{
    /// <summary>Sampled from the game's own windows and icon strip, which run from #07090A to #0C0E10.</summary>
    public const string DefaultColor = "#0A0D0F";

    public static string ColorOf(PanelLook? look) => look?.Color ?? DefaultColor;

    public static PanelStyle StyleOf(PanelLook? look) =>
        Enum.TryParse<PanelStyle>(look?.Style, out var style) && Enum.IsDefined(style) ? style : PanelStyle.Flat;
}
