namespace EveConsole.Services.ClientLayouts;

/// <summary>
/// What can be docked in a panel besides other panels and grids. <see cref="Client"/> is a game
/// client's place. Most of the rest are the main window's own: its top bar, its status bar, the agent
/// beside its tools, and the Overview's alerts.
/// </summary>
public enum PanelControlKind
{
    Client,
    Settings, ServerStatus, EveTime, LocalTime, UsersOnline, Mumble, Worklist, Menu, ToolContainer,
    Agent, DirectMessages, GroupChat, AppVersion, SdeVersion, Database, BackgroundOwner, EsiStatus,
    Theme, UiScale, Alarms, Scheduler, Alerts, EveMail,
    Orders, SalesBuildCost, SalesMarket, Notifications, EveNews, Killmails,
    GroupChatRoom, DirectMessageChat,
    Intel,
}

/// <summary>How a Mumble control shows: one line with who is talking beside it, or the whole channel as a list.</summary>
public enum MumbleControlMode { SingleLine, List }

/// <summary>How a Worklist control shows: the list itself, or one line of counts that drops the list down when clicked.</summary>
public enum WorklistControlMode { List, SingleLine }

/// <summary>How an Intel control shows: the live list, or one line with the nearest threat that drops the list down when clicked.</summary>
public enum IntelControlMode { List, SingleLine }

/// <summary>
/// A panel's edge treatment. <see cref="Card"/> is the Overview's own look: a lighter, rounded box
/// with a fine border, for the smaller controls that read best set off from what's round them.
/// </summary>
public enum PanelStyle { Flat, Raised, Inset, Ridge, Card }

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

    /// <summary>A game client's place, which is docked like a control but is a hole the client shows through.</summary>
    public static bool IsClient(string? control) => Parse(control) == PanelControlKind.Client;

    /// <summary>The size a control starts at: enough to read it across, one line's worth down.</summary>
    public static int DefaultSize(PanelControlKind kind, DockEdge edge)
    {
        if (edge is DockEdge.Top or DockEdge.Bottom)
            return kind switch
            {
                PanelControlKind.Client => 600,
                PanelControlKind.Worklist or PanelControlKind.Alerts or PanelControlKind.GroupChatRoom or PanelControlKind.DirectMessageChat
                    or PanelControlKind.Intel => 300,
                _ when OverviewSectionOf(kind) is not null => 320,
                PanelControlKind.ToolContainer or PanelControlKind.Agent => 400,
                _ => 28,
            };
        return kind switch
        {
            PanelControlKind.Client => 800,
            PanelControlKind.Settings or PanelControlKind.Menu or PanelControlKind.Alarms => 40,
            PanelControlKind.Scheduler => 34,
            PanelControlKind.DirectMessages or PanelControlKind.EveMail => 50,
            PanelControlKind.GroupChat => 80,
            PanelControlKind.ServerStatus or PanelControlKind.SdeVersion or PanelControlKind.BackgroundOwner => 170,
            PanelControlKind.AppVersion => 150,
            PanelControlKind.EveTime or PanelControlKind.Database or PanelControlKind.Theme => 110,
            PanelControlKind.LocalTime => 120,
            PanelControlKind.UiScale => 90,
            PanelControlKind.Mumble => 340,
            PanelControlKind.Worklist or PanelControlKind.Intel => 420,
            PanelControlKind.Agent or PanelControlKind.Alerts or PanelControlKind.GroupChatRoom => 360,
            PanelControlKind.DirectMessageChat => 460,
            _ when OverviewSectionOf(kind) is not null => 480,
            PanelControlKind.EsiStatus => 620,
            PanelControlKind.ToolContainer => 600,
            _ => 110,
        };
    }

    /// <summary>The Overview section a control shows, by the key the Overview's layouts use; null for any other control.</summary>
    public static string? OverviewSectionOf(PanelControlKind kind) => kind switch
    {
        PanelControlKind.Orders => "Orders",
        PanelControlKind.SalesBuildCost => "SaleListingBuild",
        PanelControlKind.SalesMarket => "SaleListingMarket",
        PanelControlKind.Notifications => "Notifications",
        PanelControlKind.EveNews => "News",
        PanelControlKind.Killmails => "PersonalKillmails",
        _ => null,
    };

    public static MumbleControlMode MumbleModeOf(ControlOptions? options) =>
        Enum.TryParse<MumbleControlMode>(options?.MumbleMode, out var mode) && Enum.IsDefined(mode) ? mode : MumbleControlMode.SingleLine;

    /// <summary>Saved before there was a choice, a Worklist control is the list.</summary>
    public static WorklistControlMode WorklistModeOf(ControlOptions? options) =>
        Enum.TryParse<WorklistControlMode>(options?.WorklistMode, out var mode) && Enum.IsDefined(mode) ? mode : WorklistControlMode.List;

    /// <summary>Saved before there was a choice, or by a newer copy with a mode this one doesn't know, an Intel control is the list.</summary>
    public static IntelControlMode IntelModeOf(ControlOptions? options) =>
        Enum.TryParse<IntelControlMode>(options?.IntelMode, out var mode) && Enum.IsDefined(mode) ? mode : IntelControlMode.List;

    /// <summary>The ranges an Intel control offers, in jumps by stargate and Thera or Turnur.</summary>
    public static IReadOnlyList<int> IntelRanges { get; } = [3, 5, 10, 15, 20];

    /// <summary>Ten jumps: a roaming gang's approach, without filling a small panel.</summary>
    public const int DefaultIntelRange = 10;

    /// <summary>The range an Intel control looks out to: one of <see cref="IntelRanges"/>, ten when none is saved
    /// or a saved one isn't offered.</summary>
    public static int IntelRangeOf(ControlOptions? options) =>
        options?.IntelMaxJumps is int jumps && IntelRanges.Contains(jumps) ? jumps : DefaultIntelRange;

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
    /// <summary>
    /// The colour a panel was given, or null: then it's the theme's page colour (SurfaceBase), so
    /// panels and the controls on them follow the theme. The Dark theme's is the game's own, sampled
    /// from its windows and icon strip, which run from #07090A to #0C0E10.
    /// </summary>
    public static string? ColorOf(PanelLook? look) => look?.Color;

    public static PanelStyle StyleOf(PanelLook? look) =>
        Enum.TryParse<PanelStyle>(look?.Style, out var style) && Enum.IsDefined(style) ? style : PanelStyle.Flat;

    /// <summary>
    /// How far in from a raised, inset or ridged panel's edge what it holds starts, in pixels at 100%.
    /// The edge is drawn 2 wide; the rest is a gap, so a control with a background of its own, or one
    /// with none that lets the panel show through, never sits on the bevel or hides its corners. A flat
    /// panel has no edge to keep clear and gives what it holds all of it.
    /// </summary>
    public const int EdgeMargin = 4;

    /// <summary>
    /// The same for a card, whose corners are rounded: past the curve as well as the border, so even
    /// a control docked full, square-cornered and opaque, stays inside the card at its corners. Just
    /// that — the radius and the border — so a thin bar styled as a card still has room for what's on
    /// it; more room is the panel's padding.
    /// </summary>
    public const int CardMargin = 5;

    /// <summary>A card's corner radius, in pixels at 100%: the Overview's sections'.</summary>
    public const double CardRadius = 4;

    /// <summary>How far in from its edge what a panel of this style holds starts, in pixels at 100%.</summary>
    public static int MarginOf(PanelStyle style) => style switch
    {
        PanelStyle.Flat => 0,
        PanelStyle.Card => CardMargin,
        _ => EdgeMargin,
    };

    /// <summary>The room inside a panel for what it holds: clear of its edge (see <see cref="EdgeMargin"/>),
    /// then its own padding.</summary>
    public static LayoutRect Inside(LayoutRect rect, PanelLook? look, double scale)
    {
        if (MarginOf(StyleOf(look)) is var edge and > 0)
        {
            var margin = Math.Min((int)Math.Round(edge * scale), Math.Min(rect.Width, rect.Height) / 2);
            rect = new LayoutRect(rect.X + margin, rect.Y + margin, rect.Width - 2 * margin, rect.Height - 2 * margin);
        }
        return Padded(rect, look?.Padding, scale);
    }

    /// <summary>
    /// A rectangle less its padding, each side at the UI scale. Padding wider than the room leaves
    /// nothing rather than less than nothing, kept where the left and top padding put it.
    /// </summary>
    public static LayoutRect Padded(LayoutRect rect, LayoutPadding? padding, double scale)
    {
        if (padding is null || padding.IsNone) return rect;
        int Side(int pixels) => Math.Max((int)Math.Round(pixels * scale), 0);
        var left = Math.Min(Side(padding.Left), rect.Width);
        var top = Math.Min(Side(padding.Top), rect.Height);
        var width = Math.Max(rect.Width - left - Side(padding.Right), 0);
        var height = Math.Max(rect.Height - top - Side(padding.Bottom), 0);
        return new LayoutRect(rect.X + left, rect.Y + top, width, height);
    }
}
