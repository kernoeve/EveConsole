using Avalonia;

namespace EveConsole.Controls;

/// <summary>
/// Where a camped gate's stub ends and where its kills tag goes: plain geometry, with no drawing, so
/// tools/IntelCheck can prove it without a screen.
/// </summary>
internal static class MapCampStub
{
    /// <summary>How far past the camped system's edge the tag's centre sits at the least: clear of the
    /// hostile mark beside a box, which sits on the same side for a gate to the east.</summary>
    public const double MarkClearance = 46;

    /// <summary>
    /// The stub's length from the camped system's centre <paramref name="p"/> along <paramref name="dir"/>,
    /// and the tag's centre, or null when the tag goes to a corner of the camped system instead.
    /// <para>The stub aims for <paramref name="target"/> (two fifths of the gate line) but at least
    /// <see cref="MarkClearance"/> past <paramref name="edge"/>. ⚠️ Neither the stub nor the tag may reach
    /// the far system's <paramref name="farFootprint"/> (its box, or its dot and name): drawn after the
    /// nodes, a tag there covered the far system's name and its click area. When the tag cannot sit at
    /// that length clear of the far system, the stub runs as far as it may and the tag is left off the
    /// line.</para>
    /// </summary>
    public static (double Reach, Point? TagCentre) Layout(
        Point p, Vector dir, double edge, double target, IReadOnlyList<Rect> farFootprint, double tagWidth, double tagHeight)
    {
        // How far along the line the far system's drawn form begins: for the stub, with room for its round
        // end and shadow; for the tag's centre, with room for half the tag on every side.
        var stubRoom = RayEntry(p, dir, farFootprint.Select(r => r.Inflate(4)));
        var tagRoom  = RayEntry(p, dir, farFootprint.Select(r => r.Inflate(new Thickness(tagWidth / 2 + 2, tagHeight / 2 + 2))));
        var want = Math.Max(target, edge + MarkClearance);
        return want <= tagRoom ? (want, p + dir * want) : (Math.Min(want, stubRoom), null);
    }

    /// <summary>How far along a ray from <paramref name="p"/> it first enters any of <paramref name="rects"/>;
    /// 0 when it starts inside one, <see cref="double.MaxValue"/> when it never meets them.</summary>
    public static double RayEntry(Point p, Vector dir, IEnumerable<Rect> rects)
    {
        var best = double.MaxValue;
        foreach (var r in rects)
        {
            double t0 = double.NegativeInfinity, t1 = double.PositiveInfinity;
            if (!Slab(p.X, dir.X, r.Left, r.Right, ref t0, ref t1) || !Slab(p.Y, dir.Y, r.Top, r.Bottom, ref t0, ref t1)) continue;
            if (t1 < 0) continue;
            best = Math.Min(best, Math.Max(t0, 0));
        }
        return best;

        static bool Slab(double o, double d, double lo, double hi, ref double t0, ref double t1)
        {
            if (Math.Abs(d) < 1e-12) return o >= lo && o <= hi;
            var a = (lo - o) / d;
            var b = (hi - o) / d;
            if (a > b) (a, b) = (b, a);
            t0 = Math.Max(t0, a);
            t1 = Math.Min(t1, b);
            return t0 <= t1;
        }
    }
}
