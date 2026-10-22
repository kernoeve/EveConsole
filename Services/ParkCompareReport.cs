using System.Text;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.ViewModels;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using SkiaSharp;

namespace EveConsole.Services;

/// <summary>
/// One job-list line as it is kept: on this machine for the Compare tab, and in a scheduled
/// post's section. The names are short because the tab's list was saved this way before there was
/// anything else to save it for, and a list saved then has to load now.
/// </summary>
/// <param name="T">The item or blueprint.</param>
/// <param name="N">Its English name, for when the SDE names are not loaded.</param>
/// <param name="Q">The first box: units, runs per copy, or runs wanted.</param>
/// <param name="Me">The second box: ME for a build, copies, the ME level, or the TE percent.</param>
/// <param name="Op">The operation, by <see cref="CompareOp"/> name; none is a build.</param>
/// <param name="D">The decryptor, 0 for none.</param>
/// <param name="B">A blueprint, so the science operations are offered.</param>
/// <param name="I">A blueprint that can be invented from.</param>
public sealed record CompareJobSpec(int T, string N, long Q, int Me, string? Op = null, int D = 0, bool B = false, bool I = false)
{
    public CompareOp Operation => Enum.TryParse<CompareOp>(Op, out var op) ? op : CompareOp.Build;

    /// <summary>The line as the service takes it. TE research is kept in percent, two a level.</summary>
    public ParkCompareJob ToJob(int line) => new(T, N, Math.Max(1, Q),
        Operation == CompareOp.Build ? Math.Clamp(Me, 0, 10) : 10, Operation,
        Copies:          Operation == CompareOp.Copy ? Math.Max(1, Me) : 1,
        TargetLevel:     Operation switch
        {
            CompareOp.MeResearch => Math.Clamp(Me, 1, 10),
            CompareOp.TeResearch => Math.Clamp(Me, 2, 20) / 2,
            _                    => 10,
        },
        DecryptorTypeId: Operation == CompareOp.Invention ? D : 0,
        Line:            line);

    /// <summary>The line as charts, summaries and posts name it.</summary>
    public string Label => ParkCompareReport.LineLabel(T, N, Operation);
}

/// <summary>Where a cost or a time stands against the best park for its job.</summary>
public enum Standing { None, Best, Behind, FarBehind }

/// <summary>One line in one park, ranked against the other parks doing the same line.</summary>
public sealed record RankedCost(ParkCost Cost, bool IsBest, double OverBest, bool IsFastest, double SlowerBy)
{
    public bool Done => Cost.Buildable;

    public string ItemName      => SdeNames.Type(Cost.Job.TypeId, Cost.Job.TypeName);
    public string UnitsText     => Done ? Cost.Produced.ToString("N0") : "–";
    public string MaterialsText => Done ? MarketFmt.Isk((double)Cost.Materials) : "–";
    public string JobCostText   => Done ? MarketFmt.Isk((double)Cost.JobCost) : "–";
    public string LeftoversText => !Done ? "–" : Cost.Leftovers > 0 ? "−" + MarketFmt.Isk((double)Cost.Leftovers) : "0";
    public string TotalText     => Done ? MarketFmt.Isk((double)Cost.Total) : IndustryText.CompareNotBuilt;
    public string PerUnitText   => Done ? MarketFmt.Isk((double)Cost.PerUnit) : "–";
    public string OverBestText  => !Done ? "" : IsBest ? IndustryText.CompareBest : ParkCompareReport.Over(OverBest);
    public string TimeText      => Done && Cost.Seconds is { } s ? PlanTime.Format(s) : "–";
    public string SlowerText    => !Done || Cost.Seconds is null ? "" : IsFastest ? IndustryText.CompareBest : ParkCompareReport.Over(SlowerBy);

    public Standing CostStanding => !Done ? Standing.None : IsBest ? Standing.Best
                                  : OverBest >= ParkCompareReport.FarBehind ? Standing.FarBehind : Standing.Behind;
    public Standing TimeStanding => !Done || Cost.Seconds is null ? Standing.None : IsFastest ? Standing.Best
                                  : SlowerBy >= ParkCompareReport.FarBehind ? Standing.FarBehind : Standing.Behind;
}

/// <summary>The results pivoted: rows are the jobs or the parks, and the other the groups of
/// Cost, vs Best, Time and vs Best columns.</summary>
public sealed record ComparePivot(bool ByPark, IReadOnlyList<string> Groups, IReadOnlyList<ComparePivotRow> Rows);

/// <summary>A pivot row; a cell is null where that job is not done in that park.</summary>
public sealed record ComparePivotRow(string Title, IReadOnlyList<RankedCost?> Cells);

/// <summary>A chart's series and axes, and the line said under it.</summary>
public sealed record CompareChart(ISeries[] Series, Axis[] XAxes, Axis[] YAxes, string Note);

/// <summary>
/// Indy Parks Compare as a report: how the parks rank on each job, the summary pivot, the two
/// charts, and all of it as tables and pictures for a post.
///
/// <para>⚠️ The ONE definition. The Compare tab binds these and a scheduled post prints and
/// draws them, so a post can never disagree with the screen it was set up from.</para>
///
/// <para>⚠️ The pictures render without a window: the charts through SKCartesianChart, the
/// summary drawn on a Skia canvas, so a task firing with nothing on screen, or in the headless
/// worker, draws the same.</para>
/// </summary>
public static class ParkCompareReport
{
    /// <summary>A quarter dearer or slower than the best is far behind, as Item Valuation marks it.</summary>
    public const double FarBehind = 0.25;

    // ── Ranking and the summary ──────────────────────────────────────────────

    /// <summary>A gap under a tenth of a percent — invention, where materials swamp the job cost —
    /// gets two decimals, so it does not read as a tie.</summary>
    public static string Over(double fraction) => fraction < 0.001 ? $"+{fraction:P2}" : $"+{fraction:P1}";

    public static string OpLabel(CompareOp op) => op switch
    {
        CompareOp.Copy       => IndustryText.CompareOpCopy,
        CompareOp.Invention  => IndustryText.CompareOpInvention,
        CompareOp.MeResearch => IndustryText.CompareOpMeResearch,
        CompareOp.TeResearch => IndustryText.CompareOpTeResearch,
        _                    => IndustryText.CompareOpBuild,
    };

    /// <summary>The item, and the operation unless a build.</summary>
    public static string LineLabel(int typeId, string typeName, CompareOp op) =>
        op == CompareOp.Build ? SdeNames.Type(typeId, typeName) : $"{SdeNames.Type(typeId, typeName)} — {OpLabel(op)}";

    /// <summary>
    /// Each line's parks ranked on cost and on time: the cheapest and the quickest marked, every
    /// other by how far behind. In list order, and within a line the cheapest first, a park that
    /// cannot do it last.
    /// </summary>
    public static List<RankedCost> Rank(IEnumerable<ParkCost> costs)
    {
        var ranked = new List<RankedCost>();
        foreach (var line in costs.GroupBy(c => c.Job.Line).OrderBy(g => g.Key))
        {
            var done    = line.Where(c => c.Buildable).ToList();
            var best    = done.Select(c => c.Total).DefaultIfEmpty(0).Min();
            var fastest = done.Where(c => c.Seconds is not null).Select(c => c.Seconds!.Value).DefaultIfEmpty(0).Min();
            foreach (var c in line.OrderBy(c => !c.Buildable).ThenBy(c => c.Total))
                ranked.Add(new RankedCost(c,
                    IsBest:    c.Buildable && c.Total == best,
                    OverBest:  c.Buildable && best > 0 ? (double)((c.Total - best) / best) : 0,
                    IsFastest: c.Buildable && c.Seconds == fastest,
                    SlowerBy:  c.Buildable && c.Seconds is { } s && fastest > 0 ? (s - fastest) / fastest : 0));
        }
        return ranked;
    }

    /// <summary>The parks in the order they were ticked, which is the order of their colours.</summary>
    public static List<(int Id, string Name, int Slot)> Parks(IEnumerable<ParkCost> costs) =>
        [.. costs.Select(c => (c.ParkId, c.ParkName, c.Slot)).Distinct().OrderBy(p => p.Slot)];

    /// <summary>The summary: a row per job and a group per park, or the other way round.</summary>
    public static ComparePivot Pivot(IReadOnlyList<RankedCost> ranked, IReadOnlyList<string> lineLabels, bool byPark)
    {
        var parks = Parks(ranked.Select(r => r.Cost));
        var lines = Enumerable.Range(0, lineLabels.Count).ToList();
        var byKey = ranked.ToDictionary(r => (r.Cost.Job.Line, r.Cost.ParkId));
        RankedCost? Cell(int line, int parkId) => byKey.GetValueOrDefault((line, parkId)) is { Done: true } r ? r : null;

        return byPark
            ? new(true,  [.. lines.Select(l => lineLabels[l])],
                  [.. parks.Select(p => new ComparePivotRow(p.Name, [.. lines.Select(l => Cell(l, p.Id))]))])
            : new(false, [.. parks.Select(p => p.Name)],
                  [.. lines.Select(l => new ComparePivotRow(lineLabels[l], [.. parks.Select(p => Cell(l, p.Id))]))]);
    }

    // ── The charts ───────────────────────────────────────────────────────────

    /// <summary>
    /// A line per park: the chosen line, or every line, costed with each day's indices. Days are
    /// those the history has for any of the systems involved; a day a system's index is missing
    /// carries its last known value forward. Null when there is no history to draw.
    /// </summary>
    public static CompareChart? CostChart(
        IReadOnlyList<ParkCost> costs,
        IReadOnlyDictionary<(int SystemId, string Activity), SortedDictionary<DateOnly, double>> history,
        int? line, SolidColorPaint labels, SolidColorPaint separators)
    {
        var byPark = costs.Where(x => x.Buildable && (line is null || x.Job.Line == line))
                          .GroupBy(x => (x.ParkId, x.ParkName, x.Slot))
                          .OrderBy(g => g.Key.Slot).ToList();
        var keys = byPark.SelectMany(g => g).SelectMany(x => x.Terms).Select(t => (t.SystemId, t.Activity)).Distinct().ToList();
        var days = keys.SelectMany(k => history.TryGetValue(k, out var s) ? s.Keys : Enumerable.Empty<DateOnly>())
                       .Distinct().OrderBy(d => d).ToList();
        if (byPark.Count == 0 || days.Count == 0) return null;

        var drawn  = byPark.Where(g => g.Key.Slot < ChartPaint.MaxSeries).ToList();
        var series = drawn.Select(g => (g.Key, Values: days.Select(d => (double?)(double)g.Sum(x => TotalAt(x,
                         (sys, act) => CostIndexHistoryService.On(history.GetValueOrDefault((sys, act)), d)))).ToArray())).ToList();

        // Where the index moves the total by under 2% (invention: materials swamp the job cost),
        // abbreviated ISK would label every gridline the same; whole ISK tells them apart.
        var all  = series.SelectMany(l => l.Values).OfType<double>().DefaultIfEmpty(0).ToList();
        var flat = all.Max() > 0 && (all.Max() - all.Min()) < 0.02 * all.Max();

        var left = byPark.Count - drawn.Count;
        return new CompareChart(
            [.. series.Select(l => (ISeries)new LineSeries<double?>
            {
                Name           = l.Key.ParkName,
                Values         = l.Values,
                GeometrySize   = 0,
                LineSmoothness = 0,
                Stroke         = ChartPaint.Series(l.Key.Slot),
                Fill           = null,
            })],
            [new Axis
            {
                Labels = [.. days.Select(d => d.ToString("MM-dd"))], LabelsPaint = labels, TextSize = 10,
                MinStep = Math.Max(1, days.Count / 6), SeparatorsPaint = null,
            }],
            [new Axis
            {
                Labeler = v => flat ? v.ToString("N0") : MarketFmt.Isk(v), LabelsPaint = labels, TextSize = 10,
                SeparatorsPaint = separators,
            }],
            string.Format(IndustryText.CompareChartNote, days[0], days[^1])
                + (left > 0 ? "  " + string.Format(IndustryText.CompareChartTooMany, ChartPaint.MaxSeries) : ""));
    }

    private static decimal TotalAt(ParkCost cost, Func<int, string, double?> indexOn) => ParkComparisonService.TotalAt(cost, indexOn);

    /// <summary>
    /// A group of bars per line — the chosen one, or every line in the list's order — named for
    /// the job, with a bar per park in the park's colour on the cost chart. A park where a job is
    /// not done, or its time is unknown, has no bar in that group. Null when nothing has a time.
    /// </summary>
    public static CompareChart? TimeChart(
        IReadOnlyList<ParkCost> costs, IReadOnlyList<string> lineLabels, int? line,
        SolidColorPaint labels, SolidColorPaint separators)
    {
        var lines = line is { } only ? [only] : Enumerable.Range(0, lineLabels.Count).ToList();
        var parks = costs.Where(x => x.Buildable && x.Slot < ChartPaint.MaxSeries && (line is null || x.Job.Line == line))
                         .GroupBy(x => (x.ParkId, x.ParkName, x.Slot))
                         .Where(g => g.Any(x => x.Seconds is not null))
                         .OrderBy(g => g.Key.Slot).ToList();
        if (parks.Count == 0 || lines.Count == 0) return null;

        return new CompareChart(
            [.. parks.Select(g => (ISeries)new ColumnSeries<double?>
            {
                Name        = g.Key.ParkName,
                Values      = [.. lines.Select(l => g.FirstOrDefault(x => x.Job.Line == l)?.Seconds is { } sec ? sec / 3600.0 : (double?)null)],
                YToolTipLabelFormatter = p => $"{g.Key.ParkName}: {PlanTime.Format(p.Coordinate.PrimaryValue * 3600)}",
                Fill        = new SolidColorPaint(SKColor.Parse(ChartPaint.SeriesHex[g.Key.Slot])),
                Stroke      = null,
                MaxBarWidth = 48,
                Padding     = 4,
                Rx          = 3,
                Ry          = 3,
            })],
            [new Axis
            {
                Labels = [.. lines.Select(l => lineLabels[l])], LabelsPaint = labels, TextSize = 10,
                // Job names side by side collide past a few; tilted, each keeps its own room.
                LabelsRotation = lines.Count > 3 ? 20 : 0, MinStep = 1, SeparatorsPaint = null,
            }],
            [new Axis
            {
                Labeler = v => v <= 0 ? "0" : PlanTime.Format(v * 3600), LabelsPaint = labels, TextSize = 10,
                MinLimit = 0, SeparatorsPaint = separators,
            }],
            "");
    }

    // ── Tables, for a post ───────────────────────────────────────────────────

    /// <summary>
    /// The summary as a monospaced table: the row names, then each group's four columns under the
    /// group's name, a bar between groups.
    /// </summary>
    public static string SummaryTable(ComparePivot pivot, string heading, string formatName)
    {
        if (pivot.Rows.Count == 0) return "";
        string[] heads = [IndustryText.CompareColCost, IndustryText.CompareColVsBest, IndustryText.CompareColTime, IndustryText.CompareColVsBest];
        var first = pivot.ByPark ? IndustryText.CompareColPark : IndustryText.CompareColJob;

        var cells = pivot.Rows.Select(r => r.Cells.SelectMany(c => c is null
                        ? new[] { "–", "", "–", "" }
                        : new[] { c.TotalText, c.OverBestText, c.TimeText, c.SlowerText }).ToArray()).ToList();

        var titleWidth = Math.Max(MonoColumns.Width(first), pivot.Rows.Max(r => MonoColumns.Width(r.Title)));
        var widths = new int[pivot.Groups.Count * 4];
        for (var i = 0; i < widths.Length; i++)
            widths[i] = Math.Max(MonoColumns.Width(heads[i % 4]), cells.Max(c => MonoColumns.Width(c[i])));
        // A group's name wider than its four columns widens the last of them.
        for (var g = 0; g < pivot.Groups.Count; g++)
        {
            var span = widths.Skip(4 * g).Take(4).Sum() + 2 * 3;
            var need = MonoColumns.Width(pivot.Groups[g]);
            if (need > span) widths[4 * g + 3] += need - span;
        }

        string Line(string title, Func<int, string> group) =>
            (MonoColumns.PadRight(title, titleWidth) + string.Concat(Enumerable.Range(0, pivot.Groups.Count).Select(g => " | " + group(g)))).TrimEnd();
        string Cells(string[] row, int g) =>
            string.Join("  ", Enumerable.Range(0, 4).Select(k => MonoColumns.PadLeft(row[4 * g + k], widths[4 * g + k])));
        int Span(int g) => widths.Skip(4 * g).Take(4).Sum() + 2 * 3;
        // A group's name centred over its four columns, so each sits over its own.
        string Centred(int g)
        {
            var name = pivot.Groups[g];
            var gap  = Span(g) - MonoColumns.Width(name);
            return MonoColumns.PadRight(new string(' ', Math.Max(0, gap / 2)) + name, Span(g));
        }

        var sb = new StringBuilder();
        if (heading.Trim().Length > 0) sb.AppendLine(OutputFormat.ByName(formatName).Bold(heading.Trim()));
        sb.AppendLine("```");
        sb.AppendLine(Line("", Centred));
        sb.AppendLine(Line(first, g => string.Join("  ", Enumerable.Range(0, 4).Select(k => MonoColumns.PadLeft(heads[k], widths[4 * g + k])))));
        sb.AppendLine(Line(new string('-', titleWidth), g => new string('-', Span(g))));
        for (var r = 0; r < pivot.Rows.Count; r++) sb.AppendLine(Line(pivot.Rows[r].Title, g => Cells(cells[r], g)));
        sb.AppendLine("```");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Every line in every park, as the Compare tab's grid lists them.</summary>
    public static string DetailTable(IReadOnlyList<RankedCost> ranked, string heading, string formatName)
    {
        if (ranked.Count == 0) return "";
        string[] heads =
        [
            IndustryText.ColItem, IndustryText.CompareColOperation, IndustryText.CompareColPark, IndustryText.CompareUnits,
            IndustryText.CompareColTotal, IndustryText.CompareColPerUnit, IndustryText.CompareColVsBest,
            IndustryText.CompareColTime, IndustryText.CompareColVsBest,
        ];
        bool[] right = [false, false, false, true, true, true, true, true, true];
        var cells = ranked.Select(r => new[]
        {
            r.ItemName, OpLabel(r.Cost.Job.Op), r.Cost.ParkName, r.UnitsText,
            r.TotalText, r.PerUnitText, r.OverBestText, r.TimeText, r.SlowerText,
        }).ToList();

        var widths = new int[heads.Length];
        for (var i = 0; i < heads.Length; i++)
            widths[i] = Math.Max(MonoColumns.Width(heads[i]), cells.Max(c => MonoColumns.Width(c[i])));

        var sb = new StringBuilder();
        if (heading.Trim().Length > 0) sb.AppendLine(OutputFormat.ByName(formatName).Bold(heading.Trim()));
        sb.AppendLine("```");
        sb.AppendLine(StandingProjectReport.Row(heads, widths, right));
        sb.AppendLine(StandingProjectReport.Row([.. widths.Select(w => new string('-', w))], widths, right));
        foreach (var c in cells) sb.AppendLine(StandingProjectReport.Row(c, widths, right));
        sb.AppendLine("```");
        return sb.ToString().TrimEnd();
    }

    // ── Pictures, for a post ─────────────────────────────────────────────────

    // The app's own dark chart palette, as the scheduled corp charts use, so a posted picture is
    // recognisably from this tool and reads on Discord's and Slack's dark themes.
    private static readonly SKColor Canvas   = new(13, 13, 18);
    private static readonly SKColor Header   = new(24, 24, 32);
    private static readonly SKColor Rule     = new(40, 40, 52);
    private static readonly SKColor Primary  = new(220, 220, 230);
    private static readonly SKColor Muted    = new(150, 150, 165);
    private static readonly SKColor Faint    = new(110, 110, 125);
    private static readonly SKColor Good     = new(106, 170, 136);
    private static readonly SKColor Bad      = new(204, 100, 100);
    private static readonly SKColor[] Washes = [new(91, 155, 213, 0x2c), new(237, 125, 49, 0x2c), new(26, 188, 156, 0x2c)];

    /// <summary>Drawn at twice the size laid out, so text stays sharp when the picture is opened.</summary>
    private const float Scale = 2;

    /// <summary>A chart as a PNG, on the dark ground, its legend under it.</summary>
    public static byte[] ChartPng(CompareChart chart, int width = 1100, int height = 460)
    {
        // Larger labels than on screen: a posted picture is often seen scaled down. The axes
        // are the post's own, built for this picture, so setting them here touches nothing else.
        foreach (var axis in chart.XAxes.Concat(chart.YAxes)) axis.TextSize = Math.Max(axis.TextSize, 13);
        var sk = new SKCartesianChart
        {
            Width           = width,
            Height          = height,
            Series          = chart.Series,
            XAxes           = chart.XAxes,
            YAxes           = chart.YAxes,
            Background      = Canvas,
            LegendPosition  = LiveChartsCore.Measure.LegendPosition.Bottom,
            LegendTextPaint = new SolidColorPaint(new SKColor(200, 200, 216)),
        };
        using var stream = new MemoryStream();
        sk.SaveImage(stream, SKEncodedImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>The paints a posted chart's axes use: the dark palette, whatever the app's theme.</summary>
    public static (SolidColorPaint Labels, SolidColorPaint Separators) PostPaints() =>
        (new SolidColorPaint(new SKColor(120, 120, 140)), new SolidColorPaint(new SKColor(30, 30, 42)));

    /// <summary>
    /// The summary as a PNG: a heading and a note, then each group's four columns under its name
    /// and wash, a line where each group begins, best in green and a quarter or more behind in red.
    /// </summary>
    public static byte[] SummaryPng(ComparePivot pivot, string heading, string note)
    {
        string[] heads = [IndustryText.CompareColCost, IndustryText.CompareColVsBest, IndustryText.CompareColTime, IndustryText.CompareColVsBest];
        var first = pivot.ByPark ? IndustryText.CompareColPark : IndustryText.CompareColJob;
        var texts = new List<string> { heading, note, first };
        texts.AddRange(heads); texts.AddRange(pivot.Groups);
        foreach (var row in pivot.Rows)
        {
            texts.Add(row.Title);
            foreach (var c in row.Cells) if (c is not null) texts.AddRange([c.TotalText, c.OverBestText, c.TimeText, c.SlowerText]);
        }

        // Faces are not disposed: the font manager keeps them, and the default one is shared.
        var typeface = PickTypeface(texts);
        var bold     = SKTypeface.FromFamilyName(typeface.FamilyName, SKFontStyle.Bold) ?? typeface;
        using var body     = new SKFont(typeface, 11);
        using var strong   = new SKFont(bold, 11);
        using var title    = new SKFont(bold, 12.5f);
        using var small    = new SKFont(typeface, 10.5f);

        const float pad = 14, cellPad = 8, rowH = 22;
        float Text(SKFont f, string s) => Measure(f, s);

        // Column widths: the row names, then each of the four in every group.
        var titleW = Math.Max(Text(body, first), pivot.Rows.Select(r => Text(body, r.Title)).DefaultIfEmpty(0).Max()) + 2 * cellPad;
        var widths = new float[pivot.Groups.Count * 4];
        for (var i = 0; i < widths.Length; i++)
        {
            var g = i / 4; var k = i % 4;
            var w = Text(body, heads[k]);
            foreach (var row in pivot.Rows)
                if (row.Cells[g] is { } c) w = Math.Max(w, Text(body, k switch { 0 => c.TotalText, 1 => c.OverBestText, 2 => c.TimeText, _ => c.SlowerText }));
            widths[i] = w + 2 * cellPad;
        }
        for (var g = 0; g < pivot.Groups.Count; g++)
        {
            var span = widths.Skip(4 * g).Take(4).Sum();
            var need = Text(strong, pivot.Groups[g]) + 2 * cellPad;
            if (need > span) widths[4 * g + 3] += need - span;
        }

        var tableW = titleW + widths.Sum();
        var width  = Math.Max(tableW, Math.Max(Text(title, heading), Text(small, note))) + 2 * pad;
        var top    = pad + 18 + 18 + 6;
        var height = top + rowH * (2 + pivot.Rows.Count) + pad;

        var info = new SKImageInfo((int)Math.Ceiling(width * Scale), (int)Math.Ceiling(height * Scale));
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Scale(Scale);
        canvas.Clear(Canvas);

        using var fill = new SKPaint { IsAntialias = true };
        void Box(float x, float y, float w, float h, SKColor color) { fill.Color = color; canvas.DrawRect(x, y, w, h, fill); }
        void Write(string s, float x, float y, SKFont f, SKColor color, bool alignRight = false, float cellW = 0, bool centre = false)
        {
            fill.Color = color;
            var w  = Measure(f, s);
            var at = centre ? x + (cellW - w) / 2 : alignRight ? x + cellW - cellPad - w : x + cellPad;
            canvas.DrawText(s, at, y + rowH / 2 + f.Size * 0.36f, f, fill);
        }

        fill.Color = Muted;  canvas.DrawText(heading, pad, pad + 13, title, fill);
        fill.Color = Faint;  canvas.DrawText(note,    pad, pad + 31, small, fill);

        float x0 = pad, yBand = top, yHead = top + rowH, yRows = top + 2 * rowH;
        Box(x0, yHead, tableW, rowH, Header);

        var x = x0 + titleW;
        for (var g = 0; g < pivot.Groups.Count; g++)
        {
            var span = widths.Skip(4 * g).Take(4).Sum();
            var wash = Washes[g % 3];
            Box(x + 1, yBand, span - 1, rowH - 1, wash);
            Box(x, yHead, span, rowH * (1 + pivot.Rows.Count), wash);
            Box(x, yHead, 1, rowH * (1 + pivot.Rows.Count), Rule);
            Write(pivot.Groups[g], x, yBand, strong, Primary, cellW: span, centre: true);
            x += span;
        }

        Write(first, x0, yHead, body, Muted);
        x = x0 + titleW;
        for (var i = 0; i < widths.Length; i++) { Write(heads[i % 4], x, yHead, body, Muted, alignRight: true, cellW: widths[i]); x += widths[i]; }

        static SKColor Colour(Standing s) => s switch
        {
            Standing.Best      => Good,
            Standing.FarBehind => Bad,
            Standing.None      => Faint,
            _                  => Primary,
        };

        for (var r = 0; r < pivot.Rows.Count; r++)
        {
            var y = yRows + r * rowH;
            Write(pivot.Rows[r].Title, x0, y, body, Primary);
            x = x0 + titleW;
            for (var g = 0; g < pivot.Groups.Count; g++)
            {
                var c = pivot.Rows[r].Cells[g];
                string[] values = c is null ? ["–", "", "–", ""] : [c.TotalText, c.OverBestText, c.TimeText, c.SlowerText];
                SKColor[] colours = c is null
                    ? [Faint, Faint, Faint, Faint]
                    : [Primary, Colour(c.CostStanding), Primary, Colour(c.TimeStanding)];
                for (var k = 0; k < 4; k++) { Write(values[k], x, y, body, colours[k], alignRight: true, cellW: widths[4 * g + k]); x += widths[4 * g + k]; }
            }
        }

        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>How wide a text is in a font. ⚠️ Through a paint: SkiaSharp 2.88's SKFont measures
    /// glyph ids only, not a string.</summary>
    private static float Measure(SKFont font, string text)
    {
        using var paint = new SKPaint(font);
        return paint.MeasureText(text);
    }

    /// <summary>
    /// A face that has every character to be drawn: the usual sans first, and where a name in the
    /// interface language needs glyphs it lacks — Chinese, Japanese, Korean — the face the system
    /// offers for the first such character.
    /// </summary>
    private static SKTypeface PickTypeface(IEnumerable<string> texts)
    {
        var face = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.FromFamilyName("DejaVu Sans") ?? SKTypeface.Default;
        using var font = new SKFont(face);
        foreach (var t in texts)
        {
            if (string.IsNullOrEmpty(t) || font.ContainsGlyphs(t)) continue;
            foreach (var rune in t.EnumerateRunes())
                if (!font.ContainsGlyph(rune.Value) && SKFontManager.Default.MatchCharacter(rune.Value) is { } other)
                    return other;
        }
        return face;
    }
}
