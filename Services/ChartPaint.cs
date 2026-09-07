using LiveChartsCore.SkiaSharpView.Painting;

namespace EveConsole.Services;

/// <summary>
/// The chrome of a chart — its gridlines and axis text — in the current theme.
///
/// <para>⚠️ Chrome only. A series colour says which line is which and is chosen to stay apart from
/// its neighbours; it is data, not decoration, and it belongs to the chart rather than to the
/// theme. What was wrong on a light page was the frame around it: separators drawn at #282A3C and
/// labels at a colour picked to glow on black.</para>
///
/// <para>⚠️ New paint per call, deliberately. LiveCharts draws through Skia and takes a VALUE, so
/// unlike every Avalonia brush here these cannot follow the theme on their own — the colour is
/// read when the paint is made. Handing out a cached one would fix the colour at whatever the
/// theme was the first time a chart was built. It still means a chart already on screen keeps its
/// frame until something rebuilds it, which a refresh or reopening the tab does.</para>
/// </summary>
public static class ChartPaint
{
    /// <summary>Gridlines and separators.</summary>
    public static SolidColorPaint Separators => new(Palette.Sk("BorderSubtle"));

    /// <summary>Axis tick labels.</summary>
    public static SolidColorPaint Labels => new(Palette.Sk("TextMuted"));

    /// <summary>Axis labels that are deliberately quieter than the ticks.</summary>
    public static SolidColorPaint FaintLabels => new(Palette.Sk("TextFaint"));

    /// <summary>An axis title carrying the app's accent, as the ISK axes do.</summary>
    public static SolidColorPaint AccentLabels => new(Palette.Sk("Accent"));
}
