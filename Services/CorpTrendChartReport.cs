using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using SkiaSharp;

namespace EveConsole.Services;

/// <summary>
/// The two monthly trend charts, as series and as a picture.
///
/// <para>⚠️ The ONE definition of what those charts plot. It was built inside the Corp Activity
/// view model, which meant a scheduled post could only have had a second copy — and a chart that
/// disagreed with the screen it was named after would be worse than no chart. The screen binds
/// these series; the scheduler draws them.</para>
///
/// <para>⚠️ Renders without a window. SKCartesianChart draws straight to a Skia surface, so a task
/// firing at 00:01 with nothing on screen produces the same picture. Taking a bitmap of the live
/// control would have needed the UI thread and a visible chart, which a scheduled run has neither
/// of.</para>
/// </summary>
public static class CorpTrendChartReport
{
    // The app's own chart palette, so a posted picture is recognisably from this tool.
    private static readonly SKColor Grid   = new(30, 30, 42);
    private static readonly SKColor Label  = new(120, 120, 140);
    private static readonly SKColor Canvas = new(13, 13, 18);

    private static readonly SKColor Green  = new(106, 170, 136);
    private static readonly SKColor Red    = new(204, 100, 100);
    private static readonly SKColor Gold   = new(200, 168,  75);
    private static readonly SKColor Blue   = new( 91, 155, 213);
    private static readonly SKColor Purple = new(155, 120, 200);

    public sealed record Chart(ISeries[] Series, Axis[] XAxes, Axis[] YAxes, string Title);

    private static LineSeries<double> Line(
        string name, IEnumerable<double> values, SKColor color, int scalesYAt = 0) =>
        new()
        {
            Name           = name,
            Values         = values.ToArray(),
            Stroke         = new SolidColorPaint(color, 2),
            Fill           = null,
            GeometrySize   = 0,
            EasingFunction = null,
            ScalesYAt      = scalesYAt,
        };

    private static Axis XAxis(string[] labels) => new()
    {
        Labels          = labels,
        LabelsRotation  = -45,
        TextSize        = 9,
        SeparatorsPaint = new SolidColorPaint(Grid),
        LabelsPaint     = new SolidColorPaint(Label),
    };

    private static Axis YAxis(string unit) => new()
    {
        TextSize        = 9,
        MinLimit        = 0,
        LabelsPaint     = new SolidColorPaint(Label),
        SeparatorsPaint = new SolidColorPaint(Grid),
        Labeler         = v => $"{v:F1}{unit}",
    };

    /// <summary>Income, expenses and the taxes behind them, in billions.</summary>
    public static Chart? IskTrends(IReadOnlyList<MonthlyActivityRow> rows)
    {
        if (rows.Count == 0) return null;

        // Oldest first: a trend read left to right is the only way anyone reads one.
        var ordered = rows.OrderBy(r => r.Month).ToList();
        var labels  = ordered.Select(r => r.Month).ToArray();

        ISeries[] series =
        [
            Line("Income",       ordered.Select(r => (double)(r.TotalIncome    / 1_000_000_000m)), Green),
            Line("Expenses",     ordered.Select(r => (double)(r.TotalExpense   / 1_000_000_000m)), Red),
            Line("Ratting Tax",  ordered.Select(r => (double)(r.RattingTax     / 1_000_000_000m)), Gold),
            Line("Industry Tax", ordered.Select(r => (double)(r.IndustryTax    / 1_000_000_000m)), Blue),
            Line("Proj Payouts", ordered.Select(r => (double)(r.ProjectPayouts / 1_000_000_000m)), Purple),
        ];

        return new Chart(series, [XAxis(labels)], [YAxis("B")], "ISK Trends (billions)");
    }

    /// <summary>
    /// Kills and losses against units mined.
    ///
    /// <para>Two Y axes on purpose: mined units run to the millions and would flatten a kill count
    /// to a line along the floor if they shared a scale.</para>
    /// </summary>
    public static Chart? ActivityTrends(IReadOnlyList<MonthlyActivityRow> rows)
    {
        if (rows.Count == 0) return null;

        var ordered = rows.OrderBy(r => r.Month).ToList();
        var labels  = ordered.Select(r => r.Month).ToArray();

        ISeries[] series =
        [
            Line("Kills",       ordered.Select(r => (double)r.Kills),      Green, 0),
            Line("Losses",      ordered.Select(r => (double)r.Losses),     Red,   0),
            Line("Units Mined", ordered.Select(r => (double)r.UnitsMined), Gold,  1),
        ];

        Axis[] yAxes =
        [
            new Axis
            {
                TextSize = 9, MinLimit = 0,
                LabelsPaint     = new SolidColorPaint(Label),
                SeparatorsPaint = new SolidColorPaint(Grid),
            },
            new Axis
            {
                TextSize = 9, MinLimit = 0,
                Position        = LiveChartsCore.Measure.AxisPosition.End,
                LabelsPaint     = new SolidColorPaint(Label),
                // Transparent, or the second axis draws its own grid over the first one's.
                SeparatorsPaint = new SolidColorPaint(SKColors.Transparent),
                // ⚠️ One decimal on the millions. F0 rounded 1.2M and 1.4M to the same
                // string, so two gridlines carried the identical label and the axis stopped
                // being readable exactly where the mined figures live.
                Labeler         = v => v >= 1_000_000 ? $"{v / 1_000_000:F1}M" : $"{v:N0}",
            },
        ];

        return new Chart(series, [XAxis(labels)], yAxes,
                         "Activity Trends (kills and losses left, units mined right)");
    }

    /// <summary>
    /// The chart as a PNG.
    ///
    /// <para>Sized for a Slack post: wide enough for twelve months of labels without them
    /// colliding, short enough not to dominate the channel.</para>
    /// </summary>
    public static byte[] RenderPng(Chart chart, int width = 1100, int height = 420)
    {
        var sk = new SKCartesianChart
        {
            Width      = width,
            Height     = height,
            Series     = chart.Series,
            XAxes      = chart.XAxes,
            YAxes      = chart.YAxes,
            Background = Canvas,
            LegendPosition  = LiveChartsCore.Measure.LegendPosition.Top,
            LegendTextPaint = new SolidColorPaint(new SKColor(200, 200, 216)),
        };

        using var stream = new MemoryStream();
        sk.SaveImage(stream, SkiaSharp.SKEncodedImageFormat.Png);
        return stream.ToArray();
    }
}
