using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using EveConsole.Services;
using EveConsole.Services.ClientLayouts;

namespace EveConsole.Views;

/// <summary>
/// The background of a panel: its color and edge style. The editor draws panels with the same code.
/// A panel with no color of its own takes the theme's page color, and follows the theme as it changes.
/// </summary>
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // ⚠️ The theme's event as well as the variant's: a theme of the user's own can change its
        // colours without the variant changing.
        ThemeService.Changed += InvalidateVisual;
        ActualThemeVariantChanged += OnVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeService.Changed -= InvalidateVisual;
        ActualThemeVariantChanged -= OnVariantChanged;
    }

    private void OnVariantChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Draw(context, new Rect(Bounds.Size), _look, 2, ThemeGround(this));
    }

    /// <summary>The theme's page color, which a panel with none of its own takes.</summary>
    public static Color ThemeGround(IResourceHost? from) => ThemeColor(from, "SurfaceBase", "#0A0D0F");

    /// <summary>One of the theme's colours, as it is now.</summary>
    private static Color ThemeColor(IResourceHost? from, string key, string fallback)
    {
        var host = from ?? Application.Current;
        var variant = (from as IThemeVariantHost)?.ActualThemeVariant ?? Application.Current?.ActualThemeVariant;
        return host is not null && host.TryFindResource(key, variant, out var found) && found is Color color
            ? color
            : Color.Parse(fallback);
    }

    /// <param name="edge">How thick a raised, inset or ridged edge is, in the units being drawn in.</param>
    /// <param name="ground">The color for a panel that has none of its own: see <see cref="ThemeGround"/>.</param>
    public static void Draw(DrawingContext context, Rect box, PanelLook? look, double edge, Color ground)
    {
        Color? own = PanelLooks.ColorOf(look) is { } text && Color.TryParse(text, out var parsed) ? parsed : null;
        var color = own ?? ground;

        if (PanelLooks.StyleOf(look) == PanelStyle.Card)
        {
            DrawCard(context, box, edge, own);
            return;
        }

        // ⚠️ Depth on a near-black ground can't come from darkening: the shadow side is already as
        // dark as the face. So the light is spent generously — a bright outer line, a softer one
        // inside it — the face itself is shaded as if lit from above, and an inset panel gets an
        // inner shadow falling across its top and left, which is what sells a recess.
        var half = edge / 2;
        switch (PanelLooks.StyleOf(look))
        {
            // ⚠️ The face carries the depth, not only the edges. On a near-black ground a shadow edge
            // is the same black as everything round it and can't be seen, so a raised panel was only a
            // faint lit line on two sides. Raised now stands lighter than what's round it and sunken
            // sits darker, as real surfaces do, and the edges and shadow sharpen that.
            // ⚠️ By how light the panel is: a dark one needs a lot of lift to look raised and can take a
            // lot of darkening to look sunk, a light one the other way round — the dark theme's amounts
            // turned an inset panel on the light theme a heavy grey.
            case PanelStyle.Raised:
                var lift = Lightness(color);
                context.FillRectangle(Vertical(Mix(color, Colors.White, Lerp(0.11, 0.4, lift)), Mix(color, Colors.White, Lerp(0.04, 0.12, lift))), box);
                Bevel(context, box, half, Mix(color, Colors.White, Lerp(0.55, 0.9, lift)), Mix(color, Colors.Black, Lerp(1, 0.55, lift)));
                Bevel(context, box.Deflate(half), half, Mix(color, Colors.White, Lerp(0.26, 0.6, lift)), Mix(color, Colors.Black, Lerp(0.55, 0.3, lift)));
                break;

            case PanelStyle.Inset:
                var sink = Lightness(color);
                context.FillRectangle(Vertical(Mix(color, Colors.Black, Lerp(0.55, 0.12, sink)), Mix(color, Colors.Black, Lerp(0.3, 0.05, sink))), box);
                Bevel(context, box, half, Mix(color, Colors.Black, Lerp(0.75, 0.45, sink)), Mix(color, Colors.White, Lerp(0.5, 0.85, sink)));
                Bevel(context, box.Deflate(half), half, Mix(color, Colors.Black, Lerp(1, 0.3, sink)), Mix(color, Colors.White, Lerp(0.18, 0.5, sink)));
                InnerShadow(context, box.Deflate(edge), edge * 5, (byte)Math.Round(Lerp(170, 70, sink)));
                break;

            case PanelStyle.Ridge:
                // A raised line around the panel: lit outside edge, a groove's shadow, then lit again.
                context.FillRectangle(new SolidColorBrush(color), box);
                Bevel(context, box, half, Mix(color, Colors.White, 0.4), Mix(color, Colors.Black, 0.8));
                Bevel(context, box.Deflate(half), half, Mix(color, Colors.Black, 0.8), Mix(color, Colors.White, 0.4));
                break;

            default:
                context.FillRectangle(new SolidColorBrush(color), box);
                context.DrawRectangle(null, new Pen(new SolidColorBrush(Mix(color, Colors.White, 0.1)), 1), box.Deflate(0.5));
                break;
        }
    }

    /// <summary>
    /// The Overview's section look: a rounded box, lighter than the page, with a fine border. In the
    /// theme's panel colour and subtle border, or in a colour of its own, the border a little lighter.
    /// ⚠️ Only the card is drawn. Outside its rounded corners is whatever it sits on — the panel
    /// round it, or the page — as an Overview card's corners show the page.
    /// </summary>
    /// <param name="edge">The usual 2-wide edge, in the units being drawn in; the card is sized from it.</param>
    private static void DrawCard(DrawingContext context, Rect box, double edge, Color? own)
    {
        var unit = edge / 2;
        var fill = own ?? ThemeColor(null, "SurfacePanel", "#11151A");
        var line = own is { } c ? Mix(c, Colors.White, 0.12) : ThemeColor(null, "BorderSubtle", "#1F2630");
        var radius = PanelLooks.CardRadius * unit;
        var inner = box.Deflate(unit / 2);
        context.DrawRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(line), unit), inner, radius, radius);
    }

    /// <summary>Top and left in one color, bottom and right in the other, which is what reads as
    /// depth. The corners are mitred, so neither side's colour runs past the other's.</summary>
    private static void Bevel(DrawingContext context, Rect box, double across, Color topLeft, Color bottomRight)
    {
        across = Math.Min(across, Math.Min(box.Width, box.Height) / 2);
        if (across <= 0) return;
        Point P(double x, double y) => new(x, y);
        var outer = new[] { P(box.X, box.Y), P(box.Right, box.Y), P(box.Right, box.Bottom), P(box.X, box.Bottom) };
        var inner = new[] { P(box.X + across, box.Y + across), P(box.Right - across, box.Y + across), P(box.Right - across, box.Bottom - across), P(box.X + across, box.Bottom - across) };
        // Top, then left: the light side. Bottom and right: the dark.
        Fill(context, topLeft, outer[0], outer[1], inner[1], inner[0]);
        Fill(context, topLeft, outer[0], inner[0], inner[3], outer[3]);
        Fill(context, bottomRight, outer[3], inner[3], inner[2], outer[2]);
        Fill(context, bottomRight, outer[1], outer[2], inner[2], inner[1]);
    }

    private static void Fill(DrawingContext context, Color color, params Point[] corners)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(corners[0], true);
            for (var i = 1; i < corners.Length; i++) g.LineTo(corners[i]);
            g.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(color), null, geometry);
    }

    /// <summary>How light a colour looks, 0 for black to 1 for white.</summary>
    private static double Lightness(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    private static double Lerp(double dark, double light, double at) => dark + (light - dark) * Math.Clamp(at, 0, 1);

    /// <summary>A shadow falling inward from the top and left of a recess, fading out over <paramref name="depth"/>.</summary>
    private static void InnerShadow(DrawingContext context, Rect box, double depth, byte strength)
    {
        depth = Math.Min(depth, Math.Min(box.Width, box.Height) / 2);
        if (depth <= 0) return;
        var dark = Color.FromArgb(strength, 0, 0, 0);
        var clear = Color.FromArgb(0, 0, 0, 0);
        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(dark, 0), new GradientStop(clear, 1) },
        }, new Rect(box.X, box.Y, box.Width, depth));
        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb((byte)(strength * 3 / 4), 0, 0, 0), 0), new GradientStop(clear, 1) },
        }, new Rect(box.X, box.Y, depth, box.Height));
    }

    /// <summary>The face shaded from top to bottom, as if lit from above.</summary>
    private static IBrush Vertical(Color top, Color bottom) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
    };

    private static Color Mix(Color from, Color to, double amount)
    {
        byte Channel(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromRgb(Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }
}
