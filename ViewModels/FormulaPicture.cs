using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace EveConsole.ViewModels;

/// <summary>
/// A reaction formula's picture, as the game shows one: the product on a green-teal hexagon
/// ground, where an original blueprint has its blue grid.
///
/// <para>⚠️ Drawn here because the image server has no such picture: it serves a formula only
/// as a blueprint, /bp or /bpc, both on the blue ground, so every formula read as a blueprint
/// original. The colours are the game's own — a vertical teal from #3d6e69 to #66a69f, a pale
/// hexagon lattice, a bright inner rule — taken from its texture by eye; nothing of the game's
/// is copied.</para>
/// </summary>
internal static class FormulaPicture
{
    private static readonly Color Top    = Color.Parse("#3d6e69");
    private static readonly Color Bottom = Color.Parse("#66a69f");
    private static readonly Color Rule   = Color.Parse("#a08ef2ea");
    private static readonly Color Hexes  = Color.Parse("#4dbffff6");

    /// <summary>The product's icon on the formula ground. On the UI thread: it renders.</summary>
    public static Bitmap Compose(Bitmap product, int size = 64)
    {
        var target = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var dc = target.CreateDrawingContext())
        {
            var all = new Rect(0, 0, size, size);
            dc.FillRectangle(new LinearGradientBrush
            {
                StartPoint    = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint      = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = [new GradientStop(Top, 0), new GradientStop(Bottom, 1)],
            }, all);

            // Flat-topped hexagons, edge to edge, a tenth of the picture across.
            var r    = size / 12.0;
            var w    = r * 1.5;
            var h    = r * Math.Sqrt(3);
            var pen  = new Pen(new SolidColorBrush(Hexes), 0.75);
            var hexes = new StreamGeometry();
            using (var g = hexes.Open())
                for (var col = 0; col * w < size + r; col++)
                    for (var row = -1; row * h < size + h; row++)
                    {
                        var cx = col * w;
                        var cy = row * h + (col % 2 == 1 ? h / 2 : 0);
                        g.BeginFigure(new Point(cx + r, cy), false);
                        for (var k = 1; k <= 6; k++)
                        {
                            var a = Math.PI / 3 * k;
                            g.LineTo(new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
                        }
                        g.EndFigure(true);
                    }
            using (dc.PushClip(all)) dc.DrawGeometry(null, pen, hexes);

            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Rule), 1), all.Deflate(3.5));
            dc.DrawImage(product, new Rect(product.Size), all);
        }
        return target;
    }
}
