using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EveConsole.Services.ClientLayouts;

namespace EveConsole.Views;

/// <summary>The background of a panel: its color and edge style. The editor draws panels with the same code.</summary>
public sealed class ClientPanelSurface : Control
{
    private PanelLook? _look;

    public PanelLook? Look
    {
        get => _look;
        set
        {
            _look = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Draw(context, new Rect(Bounds.Size), _look, 2);
    }

    /// <param name="edge">How thick a raised, inset or ridged edge is, in the units being drawn in.</param>
    public static void Draw(DrawingContext context, Rect box, PanelLook? look, double edge)
    {
        if (!Color.TryParse(PanelLooks.ColorOf(look), out var color)) color = Color.Parse(PanelLooks.DefaultColor);
        context.FillRectangle(new SolidColorBrush(color), box);

        var light = new SolidColorBrush(Mix(color, Colors.White, 0.22));
        var dark = new SolidColorBrush(Mix(color, Colors.Black, 0.6));
        switch (PanelLooks.StyleOf(look))
        {
            case PanelStyle.Raised:
                Bevel(context, box, edge, light, dark);
                break;
            case PanelStyle.Inset:
                Bevel(context, box, edge, dark, light);
                break;
            case PanelStyle.Ridge:
                Bevel(context, box, edge / 2, light, dark);
                Bevel(context, box.Deflate(edge / 2), edge / 2, dark, light);
                break;
            default:
                context.DrawRectangle(null, new Pen(new SolidColorBrush(Mix(color, Colors.White, 0.1)), 1), box.Deflate(0.5));
                break;
        }
    }

    /// <summary>Top and left in one color, bottom and right in the other, which is what reads as depth.</summary>
    private static void Bevel(DrawingContext context, Rect box, double edge, IBrush topLeft, IBrush bottomRight)
    {
        var across = Math.Min(edge, Math.Min(box.Width, box.Height) / 2);
        context.FillRectangle(topLeft, new Rect(box.X, box.Y, box.Width, across));
        context.FillRectangle(topLeft, new Rect(box.X, box.Y, across, box.Height));
        context.FillRectangle(bottomRight, new Rect(box.X, box.Bottom - across, box.Width, across));
        context.FillRectangle(bottomRight, new Rect(box.Right - across, box.Y, across, box.Height));
    }

    private static Color Mix(Color from, Color to, double amount)
    {
        byte Channel(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromRgb(Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }
}
