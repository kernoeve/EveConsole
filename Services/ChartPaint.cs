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

    // ── Series that are things, not measures ─────────────────────────────────

    /// <summary>
    /// Colours for lines that each stand for one thing — a park, a system — in a fixed order.
    ///
    /// <para>⚠️ Checked, not chosen by eye: every adjacent pair stays apart under the three common
    /// colour-vision deficiencies and for full colour, and all eight clear 3:1 on the dark
    /// surfaces (dataviz validate_palette, surface #14141c). On the light theme's grey ground
    /// they fall under 3:1, which every chart drawn with them answers with a legend and a table
    /// beside it.</para>
    ///
    /// <para>⚠️ Never cycled: a ninth thing is left off the chart (the table still lists it), so
    /// two lines never share a colour. A thing keeps its slot by its place in the user's own list,
    /// so unticking another does not repaint it.</para>
    /// </summary>
    public static readonly string[] SeriesHex =
        ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"];

    public static int MaxSeries => SeriesHex.Length;

    /// <summary>The line for slot <paramref name="slot"/>, two pixels wide.</summary>
    public static SolidColorPaint Series(int slot) =>
        new(SkiaSharp.SKColor.Parse(SeriesHex[slot % SeriesHex.Length])) { StrokeThickness = 2 };

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

    // ── Legends and tooltips: published as resources for every chart ─────────

    /// <summary>Resource keys the application's chart styles read their legend and tooltip paints
    /// from (App.axaml). Replaced with the theme's on every theme change.</summary>
    public const string LegendTextKey   = "ChartLegendTextPaint";
    public const string TooltipTextKey  = "ChartTooltipTextPaint";
    public const string TooltipBackKey  = "ChartTooltipBackPaint";

    /// <summary>
    /// Puts the current theme's legend and tooltip paints where every chart's style finds them.
    ///
    /// <para>⚠️ Without these LiveCharts picks its own, light or dark, and it asks whether the
    /// application's theme IS Dark — not whether it inherits it. Blue, Pink and Beige Dark are
    /// themes of their own built on Dark, so it took them for light and drew near-black legend and
    /// tooltip text on a dark ground: the Market Overview's pie legends, and every chart like them,
    /// were unreadable there while plain Dark was fine.</para>
    /// </summary>
    public static void PublishChrome()
    {
        if (Avalonia.Application.Current is not { } app) return;
        app.Resources[LegendTextKey]  = Make("TextMuted");
        app.Resources[TooltipTextKey] = Make("TextPrimary");
        app.Resources[TooltipBackKey] = new SolidColorPaint(Palette.Sk("SurfaceRaised"));
    }

    /// <summary>
    /// Has every chart, as it loads, take its legend and tooltip paints from the resources
    /// <see cref="PublishChrome"/> keeps current — bound, so a theme change reaches a chart already
    /// on screen. A chart whose view sets one of them itself keeps its own.
    ///
    /// <para>⚠️ A class handler, not an application style: a style on the chart types never took
    /// (their paints stayed unset), so the charts are reached the one way that is sure to.</para>
    /// </summary>
    public static void FollowThemeInCharts()
    {
        PublishChrome();
        Avalonia.Controls.Control.LoadedEvent.AddClassHandler<LiveChartsGeneratedCode.SourceGenChart>((chart, _) =>
        {
            void Follow(Avalonia.AvaloniaProperty property, string key)
            {
                if (!chart.IsSet(property)) chart.Bind(property, Avalonia.Controls.ResourceNodeExtensions.GetResourceObservable(chart, key));
            }
            Follow(LiveChartsGeneratedCode.SourceGenChart.LegendTextPaintProperty,        LegendTextKey);
            Follow(LiveChartsGeneratedCode.SourceGenChart.TooltipTextPaintProperty,       TooltipTextKey);
            Follow(LiveChartsGeneratedCode.SourceGenChart.TooltipBackgroundPaintProperty, TooltipBackKey);
        });
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
        PublishChrome();

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
