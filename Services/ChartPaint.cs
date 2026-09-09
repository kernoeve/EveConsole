using System.Reflection;
using System.Runtime.CompilerServices;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

namespace EveConsole.Services;

/// <summary>
/// The chrome of a chart — its gridlines and axis text — in the current theme.
///
/// <para>⚠️ Chrome only. A series colour says which line is which and is chosen to stay apart from
/// its neighbours; it is data, not decoration, and belongs to the chart rather than to the theme.
/// What was wrong on a light page was the frame around it: separators drawn at #282A3C and labels
/// at a colour picked to glow on black.</para>
///
/// <para>⚠️ A new paint per call, deliberately. LiveCharts draws through Skia and takes a VALUE, so
/// unlike every Avalonia brush here these cannot follow the theme on their own. Nor can one be
/// shared and mutated when the theme changes: a Paint carries drawing state tied to the canvas it
/// is used on, so handing the same instance to two charts is not safe. They are rebuilt instead —
/// see <see cref="Restyle"/>.</para>
/// </summary>
public static class ChartPaint
{
    /// <summary>Gridlines and separators.</summary>
    public static SolidColorPaint Separators => Make("BorderSubtle");

    /// <summary>Axis tick labels.</summary>
    public static SolidColorPaint Labels => Make("TextMuted");

    /// <summary>Axis labels that are deliberately quieter than the ticks.</summary>
    public static SolidColorPaint FaintLabels => Make("TextFaint");

    /// <summary>An axis title carrying the app's accent, as the ISK axes do.</summary>
    public static SolidColorPaint AccentLabels => Make("Accent");

    // ── Remembering what a paint was for ──────────────────────────────────────

    /// <summary>
    /// Which token each paint was built from.
    ///
    /// <para>⚠️ A side table rather than a field, because the role has to survive without
    /// subclassing a third-party drawing type — and rebuilding a paint on a theme change is
    /// impossible without it. An axis holds a paint and cannot say whether it is a tick label, a
    /// quiet one, or an accented title; the paint is the only thing that knows, and only because
    /// it was recorded here when it was made.</para>
    ///
    /// <para>Weak keys, so a paint belonging to a chart that has gone is collected with it.</para>
    /// </summary>
    private static readonly ConditionalWeakTable<SolidColorPaint, string> Roles = new();

    private static SolidColorPaint Make(string token)
    {
        var paint = new SolidColorPaint(Palette.Sk(token));
        Roles.AddOrUpdate(paint, token);
        return paint;
    }

    // ── Following the theme ───────────────────────────────────────────────────

    private static readonly List<WeakReference<object>> Owners = [];

    /// <summary>
    /// Registers a view model whose axes should be restyled when the theme changes.
    ///
    /// <para>⚠️ The OWNER is tracked, not the axes, and that is what makes this survive a reload.
    /// Several of these view models replace their axis arrays wholesale when their data refreshes,
    /// so anything holding the axes directly would be restyling the set that was on screen two
    /// loads ago. Reading them off the owner each time asks what is displayed now.</para>
    ///
    /// <para>Held weakly: registering must not be the reason a closed tool stays in memory.</para>
    /// </summary>
    public static void TrackAxesOf(object owner)
    {
        lock (Owners)
        {
            Owners.RemoveAll(w => !w.TryGetTarget(out _));
            if (Owners.Any(w => w.TryGetTarget(out var o) && ReferenceEquals(o, owner))) return;
            Owners.Add(new WeakReference<object>(owner));
        }
    }

    /// <summary>
    /// Rebuilds the axis chrome of every tracked chart in the current theme.
    ///
    /// <para>⚠️ Assigns a NEW paint rather than recolouring the existing one. Setting the axis
    /// property is what tells the chart something changed; mutating a paint in place would leave
    /// the colours right and the picture unchanged until something else redrew it.</para>
    /// </summary>
    public static void Restyle()
    {
        List<object> owners;
        lock (Owners)
        {
            Owners.RemoveAll(w => !w.TryGetTarget(out _));
            owners = [.. Owners.Select(w => w.TryGetTarget(out var o) ? o : null).OfType<object>()];
        }

        foreach (var owner in owners)
        foreach (var axes in AxisArrays(owner))
        foreach (var axis in axes)
        {
            if (axis is null) continue;

            if (axis.LabelsPaint is SolidColorPaint lp && Roles.TryGetValue(lp, out var labelToken))
                axis.LabelsPaint = Make(labelToken);

            if (axis.SeparatorsPaint is SolidColorPaint sp && Roles.TryGetValue(sp, out var sepToken))
                axis.SeparatorsPaint = Make(sepToken);
        }
    }

    /// <summary>
    /// Every axis array a view model is currently exposing.
    ///
    /// <para>Reflection, because the alternative is each view model declaring where its axes are —
    /// twenty-six of them across nine files, and a list that would be wrong the first time somebody
    /// added a chart without knowing to update it.</para>
    /// </summary>
    private static IEnumerable<Axis[]> AxisArrays(object owner)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var f in owner.GetType().GetFields(Any))
            if (f.FieldType == typeof(Axis[]) && f.GetValue(owner) is Axis[] fromField)
                yield return fromField;

        foreach (var p in owner.GetType().GetProperties(Any))
        {
            if (p.PropertyType != typeof(Axis[]) || !p.CanRead || p.GetIndexParameters().Length > 0)
                continue;

            Axis[]? fromProperty = null;
            try { fromProperty = p.GetValue(owner) as Axis[]; }
            catch { /* a getter that throws is not this method's problem to solve */ }

            if (fromProperty is not null) yield return fromProperty;
        }
    }
}
