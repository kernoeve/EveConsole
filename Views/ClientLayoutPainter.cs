using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EveConsole.Localization;
using EveConsole.Services.ClientLayouts;

namespace EveConsole.Views;

/// <summary>Draws a calculated layout, for the editor and for the map of monitors alike.</summary>
internal sealed class ClientLayoutPainter(Control owner)
{
    private readonly Control _owner = owner;

    public IBrush Brush(string key)
    {
        return _owner.TryFindResource(key, _owner.ActualThemeVariant, out var found) && found is IBrush brush
            ? brush
            : Brushes.Gray;
    }

    /// <summary>Screen pixels to on-screen control units, keeping the proportions.</summary>
    public readonly record struct View(double Scale, double OffsetX, double OffsetY)
    {
        public Rect ToControl(LayoutRect r) =>
            new(OffsetX + r.X * Scale, OffsetY + r.Y * Scale, r.Width * Scale, r.Height * Scale);

        public (int X, int Y) ToScreen(Point p) =>
            ((int)Math.Round((p.X - OffsetX) / Scale), (int)Math.Round((p.Y - OffsetY) / Scale));

        public static View Fit(LayoutRect space, Size size, double margin = 2)
        {
            if (space.Width <= 0 || space.Height <= 0) return new View(1, 0, 0);
            var scale = Math.Min((size.Width - margin * 2) / space.Width, (size.Height - margin * 2) / space.Height);
            scale = Math.Max(scale, 0.0001);
            return new View(
                scale,
                (size.Width - space.Width * scale) / 2 - space.X * scale,
                (size.Height - space.Height * scale) / 2 - space.Y * scale);
        }
    }

    public void Draw(DrawingContext context, ClientLayoutResult result, View view, string? selected)
    {
        var line = new Pen(Brush("BorderStrongBrush"), 1);
        var accent = new Pen(Brush("AccentBrush"), 2);

        foreach (var empty in result.Empties)
        {
            var rect = view.ToControl(empty.Rect);
            context.DrawRectangle(Brush("SurfaceBaseBrush"), selected == empty.AreaId ? accent : line, rect);
            Label(context, rect, null, ClientLayoutsText.CanvasEmpty, "TextFaintBrush");
        }
        for (var i = 0; i < result.Clients.Count; i++)
        {
            var client = result.Clients[i];
            var rect = view.ToControl(client.Rect);
            context.DrawRectangle(Brush("AccentSurfaceBrush"), selected == client.AreaId ? accent : line, rect);
            Label(context, rect,
                string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.CanvasClient, i + 1),
                Size(client.Rect), "TextPrimaryBrush");
        }
        foreach (var panel in result.Panels)
        {
            if (panel.Rect.IsEmpty) continue;
            var rect = view.ToControl(panel.Rect);
            ClientPanelSurface.Draw(context, rect, panel.Look, EdgeIn(view), ClientPanelSurface.ThemeGround(null));
            context.DrawRectangle(null, line, rect);
            if ((panel.Parts?.Count ?? 0) == 0)
                Label(context, rect, NameOf(panel.Name), Size(panel.Rect), "TextSecondaryBrush");
            foreach (var part in panel.Parts ?? []) DrawPart(context, part, view, line, result);
        }

        // Last, so nothing inside or beside it covers the outline of what's selected.
        if (selected == null) return;
        foreach (var panel in result.Panels)
        {
            if (panel.Id == selected) context.DrawRectangle(null, accent, view.ToControl(panel.Rect));
            foreach (var part in panel.Parts ?? [])
                if (part.Id == selected) context.DrawRectangle(null, accent, view.ToControl(part.Rect));
        }

        // A grid has no part of its own, its cells cover it: outlined dashed, round all its cells.
        foreach (var grid in result.Grids)
            if (grid.AreaId == selected)
                context.DrawRectangle(null, new Pen(Brush("AccentBrush"), 2, new DashStyle([3, 2], 0)), view.ToControl(grid.Rect));
    }

    private void DrawPart(DrawingContext context, PlacedPart part, View view, Pen line, ClientLayoutResult result)
    {
        if (part.Rect.IsEmpty) return;
        var rect = view.ToControl(part.Rect);
        switch (part.Kind)
        {
            case PlacedPartKind.Panel:
                ClientPanelSurface.Draw(context, rect, part.Look, EdgeIn(view), ClientPanelSurface.ThemeGround(null));
                context.DrawRectangle(null, line, rect);
                if (part.Bare) Label(context, rect, NameOf(part.Name), Size(part.Rect), "TextSecondaryBrush");
                break;
            case PlacedPartKind.Control:
                context.DrawRectangle(Brush("AccentSurfaceBrush"), line, rect);
                // A Tool Container is named, so a menu can say which one it opens tools in.
                var named = PanelControls.Parse(part.Control) == PanelControlKind.ToolContainer && part.Name.Length > 0;
                Label(context, rect, named ? part.Name : null, ControlName(part.Control), "TextPrimaryBrush");
                break;
            case PlacedPartKind.Client:
                context.DrawRectangle(Brush("AccentSurfaceBrush"), line, rect);
                Label(context, rect,
                    string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.CanvasClient, result.Clients.FindIndex(c => c.AreaId == part.Id) + 1),
                    Size(part.Rect), "TextPrimaryBrush");
                break;
            default:
                context.DrawRectangle(null, line, rect);
                Label(context, rect, null, ClientLayoutsText.CanvasEmpty, "TextFaintBrush");
                break;
        }
    }

    /// <summary>A panel's edge at its real size, but never so thin that a preview can't show it.</summary>
    private static double EdgeIn(View view) => Math.Max(2 * view.Scale, 1);

    private static string NameOf(string name) => name.Length > 0 ? name : ClientLayoutsText.PanelDefaultName;

    /// <summary>A control's name for people. One this copy doesn't know shows its saved name.</summary>
    public static string ControlName(string? control) => PanelControls.Parse(control) is { } kind ? ControlName(kind) : control ?? "";

    public static string ControlName(PanelControlKind kind) => kind switch
    {
        PanelControlKind.Client => ClientLayoutsText.ItemClient,
        PanelControlKind.Settings => ClientLayoutsText.ItemSettings,
        PanelControlKind.ServerStatus => ClientLayoutsText.ItemServerStatus,
        PanelControlKind.EveTime => ClientLayoutsText.ItemEveTime,
        PanelControlKind.LocalTime => ClientLayoutsText.ItemLocalTime,
        PanelControlKind.UsersOnline => ClientLayoutsText.ItemUsersOnline,
        PanelControlKind.Mumble => ClientLayoutsText.ItemMumble,
        PanelControlKind.Worklist => ClientLayoutsText.ItemWorklist,
        PanelControlKind.Menu => ClientLayoutsText.ItemMenu,
        PanelControlKind.ToolContainer => ClientLayoutsText.ItemToolContainer,
        PanelControlKind.Agent => ClientLayoutsText.ItemAgent,
        PanelControlKind.DirectMessages => ClientLayoutsText.ItemDirectMessages,
        PanelControlKind.GroupChat => ClientLayoutsText.ItemGroupChat,
        PanelControlKind.AppVersion => ClientLayoutsText.ItemAppVersion,
        PanelControlKind.SdeVersion => ClientLayoutsText.ItemSdeVersion,
        PanelControlKind.Database => ClientLayoutsText.ItemDatabase,
        PanelControlKind.BackgroundOwner => ClientLayoutsText.ItemBackgroundOwner,
        PanelControlKind.EsiStatus => ClientLayoutsText.ItemEsiStatus,
        PanelControlKind.Theme => ClientLayoutsText.ItemTheme,
        PanelControlKind.UiScale => ClientLayoutsText.ItemUiScale,
        PanelControlKind.Alarms => ClientLayoutsText.ItemAlarms,
        PanelControlKind.Scheduler => ClientLayoutsText.ItemScheduler,
        PanelControlKind.Alerts => ClientLayoutsText.ItemAlerts,
        PanelControlKind.EveMail => ClientLayoutsText.ItemEveMail,
        // The Overview's own titles for its sections.
        PanelControlKind.Orders => OverviewText.Orders,
        PanelControlKind.SalesBuildCost => ShellText.TabSaleListingBuild,
        PanelControlKind.SalesMarket => ShellText.TabSaleListingMarket,
        PanelControlKind.Notifications => OverviewText.Notifications,
        PanelControlKind.EveNews => OverviewText.EveOnlineNews,
        PanelControlKind.Killmails => OverviewText.PersonalKillmails,
        PanelControlKind.GroupChatRoom => ClientLayoutsText.ItemGroupChatRoom,
        PanelControlKind.DirectMessageChat => ClientLayoutsText.ItemDirectMessageChat,
        PanelControlKind.Intel => ClientLayoutsText.ItemIntel,
        _ => kind.ToString(),
    };

    private static string Size(LayoutRect r) =>
        string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.CanvasSize, r.Width, r.Height);

    /// <summary>A line or two in the middle of a box, or nothing if it wouldn't fit.</summary>
    private void Label(DrawingContext context, Rect box, string? title, string detail, string brushKey)
    {
        var brush = Brush(brushKey);
        var top = title == null ? null : Text(title, 11, brush);
        var bottom = Text(detail, 10, brush);
        var showBottom = bottom.Width + 4 <= box.Width;
        var height = (top?.Height ?? 0) + (showBottom ? bottom.Height : 0);
        if (height + 2 > box.Height) { showBottom = false; height = top?.Height ?? 0; }
        if (height + 2 > box.Height) return;

        var y = box.Y + (box.Height - height) / 2;
        if (top != null && top.Width + 4 <= box.Width)
        {
            context.DrawText(top, new Point(box.X + (box.Width - top.Width) / 2, y));
            y += top.Height;
        }
        if (showBottom) context.DrawText(bottom, new Point(box.X + (box.Width - bottom.Width) / 2, y));
    }

    public static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
}
