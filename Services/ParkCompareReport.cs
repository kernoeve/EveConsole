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
    /// The line as a post names it: the operation always, a build's too. A list on the Compare tab
    /// reads fine with builds left bare, but a post is read on its own, beside research and copies.
    /// </summary>
    public static string PostLabel(int typeId, string typeName, CompareOp op) =>
        $"{SdeNames.Type(typeId, typeName)} — {OpLabel(op)}";

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

    // ── For Slack's own blocks ───────────────────────────────────────────────

    /// <summary>The value and how far behind it is, or the value alone where it is the best.</summary>
    private static string WithGap(string value, string gap, bool best) => best || gap.Length == 0 ? value : $"{value} {gap}";

    /// <summary>The summary for <see cref="SlackRichBlocks.ParkTable"/>.</summary>
    public static List<SlackParkRow> TableRows(ComparePivot jobs) =>
    [
        .. jobs.Rows.Select(r => new SlackParkRow(r.Title, [.. r.Cells.Select(c => c is null ? null : new SlackParkCell(
            WithGap(c.TotalText, c.OverBestText, c.IsBest), c.IsBest,
            WithGap(c.TimeText, c.SlowerText, c.IsFastest), c.IsFastest))])),
    ];

    /// <summary>A job's title in its box: what is made, and the operation.</summary>
    public static string BoxTitle(ParkJobFacts facts, string label) =>
        facts.Op == CompareOp.Build ? string.Format(AlarmsText.JobBoxBuildTitle, facts.Name) : label;

    /// <summary>
    /// What a job is, for its box. Build: how many of what, and what they sell for, to read against
    /// the costs under it. Copy, invention and research: what comes out.
    /// </summary>
    public static string BoxAbout(ParkJobFacts f, long produced)
    {
        static string Bold(string s) => $"*{SlackColumns.Escape(s)}*";

        return f.Op switch
        {
            CompareOp.Build => string.Join("\n", new[]
            {
                f.IsReaction ? IndustryText.IndexReaction : IndustryText.IndexManufacturing,
                Bold(string.Format(AlarmsText.JobBoxUnits, produced, f.Name)),
                f.UnitValue is { } unit ? string.Format(AlarmsText.JobBoxValue, Bold(MarketFmt.Isk((double)(unit * produced)))) : "",
            }.Where(l => l.Length > 0)),
            CompareOp.Copy => $"{IndustryText.IndexCopying}\n{Bold(string.Format(IndustryText.CompareDetailCopy, f.Copies, f.Quantity))}",
            CompareOp.Invention when f.Target.Length > 0 =>
                $"{IndustryText.IndexInvention}\n{Bold(string.Format(AlarmsText.JobBoxInventTarget, f.Target))}\n"
                + Bold(string.Format(AlarmsText.JobBoxInventRuns, f.TargetRuns, f.TargetMe, f.TargetTe)),
            CompareOp.Invention  => IndustryText.IndexInvention,
            CompareOp.MeResearch => $"{IndustryText.IndexMeResearch}\n{Bold(string.Format(AlarmsText.JobBoxMeRange, f.Level))}",
            _                    => $"{IndustryText.IndexTeResearch}\n{Bold(string.Format(AlarmsText.JobBoxTeRange, f.Level * 2))}",
        };
    }

    /// <summary>A job's picture. ⚠️ Only what the image server has: a render of a ship, an icon of
    /// anything else, a blueprint's own image. An address it does not answer fails the whole post.</summary>
    public static string BoxImage(ParkJobFacts f) => f.Op switch
    {
        CompareOp.Build when f.HasRender => $"https://images.evetech.net/types/{f.ImageTypeId}/render?size=128",
        CompareOp.Build                  => $"https://images.evetech.net/types/{f.ImageTypeId}/icon?size=64",
        CompareOp.Copy                   => $"https://images.evetech.net/types/{f.ImageTypeId}/bpc?size=64",
        _                                => $"https://images.evetech.net/types/{f.ImageTypeId}/bp?size=64",
    };

    // ── The charts ───────────────────────────────────────────────────────────

    /// <summary>
    /// A line per park: the chosen line, or every line, costed with each day's indices. Days are
    /// those the history has for any of the systems involved; a day a system's index is missing
    /// carries its last known value forward. Null when there is no history to draw.
    /// </summary>
    public static CompareChart? CostChart(
        IReadOnlyList<ParkCost> costs,
        IReadOnlyDictionary<(int SystemId, string Activity), SortedDictionary<DateOnly, double>> history,
        int? line, Func<SolidColorPaint> labels, Func<SolidColorPaint> separators)
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
                Labels = [.. days.Select(d => d.ToString("MM-dd"))], LabelsPaint = labels(), TextSize = 10,
                MinStep = Math.Max(1, days.Count / 6), SeparatorsPaint = null,
            }],
            [new Axis
            {
                Labeler = v => flat ? v.ToString("N0") : MarketFmt.Isk(v), LabelsPaint = labels(), TextSize = 10,
                SeparatorsPaint = separators(),
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
        Func<SolidColorPaint> labels, Func<SolidColorPaint> separators)
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
                Labels = [.. lines.Select(l => lineLabels[l])], LabelsPaint = labels(), TextSize = 10,
                // Job names side by side collide past a few; tilted, each keeps its own room.
                LabelsRotation = lines.Count > 3 ? 20 : 0, MinStep = 1, SeparatorsPaint = null,
            }],
            [new Axis
            {
                Labeler = v => v <= 0 ? "0" : PlanTime.Format(v * 3600), LabelsPaint = labels(), TextSize = 10,
                MinLimit = 0, SeparatorsPaint = separators(),
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
    internal static readonly SKColor Canvas   = new(13, 13, 18);
    internal static readonly SKColor FaintText = new(110, 110, 125);

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

    /// <summary>The paints a posted chart's axes use: the dark palette, whatever the app's theme.
    /// Makers, not paints: each axis gets its own.</summary>
    public static (Func<SolidColorPaint> Labels, Func<SolidColorPaint> Separators) PostPaints() =>
        (() => new SolidColorPaint(new SKColor(120, 120, 140)), () => new SolidColorPaint(new SKColor(30, 30, 42)));

    /// <summary>
    /// The summary as a PNG, drawn to be read at the size Slack shows it: about 600 wide however big
    /// it is. A column per group under its name and wash, each cell the cost above the time, each
    /// with how far behind it is; best in green and a quarter or more behind in red.
    ///
    /// <para>⚠️ One column per group, not four. Four made a picture so wide that Slack's size took its
    /// text below reading. No heading in the picture either: the post's title goes beside it, where
    /// its emoji are drawn rather than printed as letters.</para>
    /// </summary>
    public static byte[] SummaryPng(ComparePivot pivot)
    {
        var first = pivot.ByPark ? IndustryText.CompareColPark : IndustryText.CompareColJob;
        var texts = new List<string> { first };
        texts.AddRange(pivot.Groups);
        foreach (var row in pivot.Rows)
        {
            texts.Add(row.Title);
            foreach (var c in row.Cells) if (c is not null) texts.AddRange([c.TotalText, c.OverBestText, c.TimeText, c.SlowerText]);
        }

        // Faces are not disposed: the font manager keeps them, and the default one is shared.
        var typeface = PickTypeface(texts);
        var bold     = SKTypeface.FromFamilyName(typeface.FamilyName, SKFontStyle.Bold) ?? typeface;
        using var head   = new SKFont(bold, 32);
        using var name   = new SKFont(typeface, 30);
        using var value  = new SKFont(typeface, 30);
        using var strong = new SKFont(bold, 30);
        using var small  = new SKFont(typeface, 24);

        const float pad = 16, cell = 14, rowH = 84, headH = 58, between = 24;

        var nameW = Math.Max(Measure(head, first), pivot.Rows.Select(r => Measure(name, r.Title)).DefaultIfEmpty(0).Max()) + 2 * cell;

        var columnW = pivot.Groups.Select(g => Measure(head, g) + 2 * cell).DefaultIfEmpty(0).Max();
        foreach (var row in pivot.Rows)
            foreach (var c in row.Cells)
            {
                if (c is null) continue;
                columnW = Math.Max(columnW, 2 * cell + between + Measure(c.IsBest ? strong : value, c.TotalText) + Measure(small, c.OverBestText));
                columnW = Math.Max(columnW, 2 * cell + between + Measure(small, c.TimeText) + Measure(small, c.SlowerText));
            }

        var width  = 2 * pad + nameW + columnW * pivot.Groups.Count;
        var height = 2 * pad + headH + rowH * pivot.Rows.Count;

        using var surface = SKSurface.Create(new SKImageInfo((int)Math.Ceiling(width), (int)Math.Ceiling(height)));
        var canvas = surface.Canvas;
        canvas.Clear(Canvas);

        using var fill = new SKPaint { IsAntialias = true };
        void Write(string s, float x, float y, SKFont f, SKColor colour)
        {
            fill.Color = colour;
            canvas.DrawText(s, x, y + f.Size * 0.36f, f, fill);
        }
        void WriteRight(string s, float right, float y, SKFont f, SKColor colour) => Write(s, right - Measure(f, s), y, f, colour);
        void Rule(float y, byte alpha)
        {
            fill.Color = RuleColour.WithAlpha(alpha);
            canvas.DrawRect(pad, y, width - 2 * pad, 1, fill);
        }

        static SKColor Colour(Standing s) => s switch
        {
            Standing.Best      => BrightGood,
            Standing.FarBehind => BrightBad,
            _                  => BodyText,
        };

        for (var g = 0; g < pivot.Groups.Count; g++)
        {
            var x = pad + nameW + columnW * g;
            fill.Color = Wash(g);
            canvas.DrawRect(x, pad, columnW, height - 2 * pad, fill);
            Write(pivot.Groups[g], x + (columnW - Measure(head, pivot.Groups[g])) / 2, pad + headH / 2, head, HeadText);
        }

        Write(first, pad + cell, pad + headH / 2, head, HeadText);
        Rule(pad + headH, 255);

        for (var r = 0; r < pivot.Rows.Count; r++)
        {
            var top = pad + headH + r * rowH;
            Write(pivot.Rows[r].Title, pad + cell, top + rowH / 2, name, HeadText);

            for (var g = 0; g < pivot.Groups.Count; g++)
            {
                var x = pad + nameW + columnW * g;
                if (pivot.Rows[r].Cells[g] is not { } c)
                {
                    Write("–", x + cell, top + 24, value, FaintText);
                    continue;
                }

                Write(c.TotalText, x + cell, top + 24, c.IsBest ? strong : value, Colour(c.CostStanding));
                WriteRight(c.OverBestText, x + columnW - cell, top + 24, small, Colour(c.CostStanding));
                Write(c.TimeText, x + cell, top + 60, small, Colour(c.TimeStanding));
                WriteRight(c.SlowerText, x + columnW - cell, top + 60, small, Colour(c.TimeStanding));
            }

            Rule(top + rowH, 160);
        }

        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static readonly SKColor RuleColour = new(42, 44, 56);
    internal static readonly SKColor HeadText   = new(232, 232, 240);
    internal static readonly SKColor BodyText   = new(205, 205, 218);
    internal static readonly SKColor BrightGood = new(72, 199, 116);
    internal static readonly SKColor BrightBad  = new(240, 96, 96);
    private static readonly SKColor[] SolidWashes = [new(27, 38, 54), new(46, 33, 28), new(22, 44, 42)];

    /// <summary>A column's tint, in turn, so neighbours can be told apart.</summary>
    internal static SKColor Wash(int column) => SolidWashes[column % SolidWashes.Length];

    /// <summary>How wide a text is in a font. ⚠️ Through a paint: SkiaSharp 2.88's SKFont measures
    /// glyph ids only, not a string.</summary>
    internal static float Measure(SKFont font, string text)
    {
        using var paint = new SKPaint(font);
        return paint.MeasureText(text);
    }

    /// <summary>
    /// A face that has every character to be drawn: the usual sans first, and where a name in the
    /// interface language needs glyphs it lacks — Chinese, Japanese, Korean — the face the system
    /// offers for the first such character.
    /// </summary>
    internal static SKTypeface PickTypeface(IEnumerable<string> texts)
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
