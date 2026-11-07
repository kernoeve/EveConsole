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
            context.DrawRectangle(Brush("SurfaceRaisedBrush"), selected == panel.Id ? accent : line, rect);
            Label(context, rect, panel.Name.Length > 0 ? panel.Name : ClientLayoutsText.PanelDefaultName, Size(panel.Rect), "TextSecondaryBrush");
        }
    }

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
