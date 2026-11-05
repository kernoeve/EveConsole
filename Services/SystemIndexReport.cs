using System.Text;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.ViewModels;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace EveConsole.Services;

/// <summary>A system in a System Index list: its place in the list picks its colour.</summary>
public sealed record IndexSystem(int SystemId, string Name, string Region, int Slot);

/// <summary>
/// The System Index tool as a report: each activity's index over time, a line a system, and
/// today's indices as a table.
///
/// <para>⚠️ The ONE definition: the tool binds these charts and a scheduled post draws them, so
/// the post cannot disagree with the tool. On screen the table's dot beside each system is the
/// charts' legend; a posted picture has no table beside it, so it draws its own legend.</para>
/// </summary>
public static class SystemIndexReport
{
    public static string ActivityName(string activity) => activity switch
    {
        "manufacturing"                   => IndustryText.IndexManufacturing,
        "reaction"                        => IndustryText.IndexReaction,
        "invention"                       => IndustryText.IndexInvention,
        "copying"                         => IndustryText.IndexCopying,
        "researching_material_efficiency" => IndustryText.IndexMeResearch,
        "researching_time_efficiency"     => IndustryText.IndexTeResearch,
        _                                 => activity,
    };

    /// <summary>
    /// One activity's index each day, a line a system in its slot's colour, in percent. A day
    /// with no sample for a system is a gap in its line, not a guess. Null when no system has a
    /// history for the activity. Past the palette's eight, systems are left off.
    /// </summary>
    public static CompareChart? Chart(
        string activity, IReadOnlyList<IndexSystem> systems,
        IReadOnlyDictionary<(int SystemId, string Activity), SortedDictionary<DateOnly, double>> daily,
        Func<SolidColorPaint> labels, Func<SolidColorPaint> separators, double textSize = 9)
    {
        var days  = daily.Values.SelectMany(v => v.Keys).Distinct().OrderBy(d => d).ToList();
        var drawn = systems.Where(s => s.Slot < ChartPaint.MaxSeries && daily.ContainsKey((s.SystemId, activity))).ToList();
        if (drawn.Count == 0 || days.Count == 0) return null;

        return new CompareChart(
            [.. drawn.Select(s =>
            {
                var series = daily[(s.SystemId, activity)];
                return (ISeries)new LineSeries<double?>
                {
                    Name           = s.Name,
                    Values         = days.Select(d => series.TryGetValue(d, out var v) ? v * 100 : (double?)null).ToArray(),
                    GeometrySize   = 0,
                    LineSmoothness = 0,
                    Stroke         = ChartPaint.Series(s.Slot),
                    Fill           = null,
                };
            })],
            [new Axis
            {
                Labels = [.. days.Select(d => d.ToString("MM-dd"))], LabelsPaint = labels(), TextSize = textSize,
                MinStep = Math.Max(1, days.Count / 6), SeparatorsPaint = null,
            }],
            [new Axis
            {
                Labeler = v => $"{v:0.#}%", LabelsPaint = labels(), TextSize = textSize, MinLimit = 0,
                SeparatorsPaint = separators(),
            }],
            string.Format(IndustryText.IndexChartsNote, days[0], days[^1]));
    }

    /// <summary>
    /// How far an index has moved in the past week, in points. Null without a reading near enough
    /// to a week ago to call it that: three days either way of a gap is not a week's change.
    /// </summary>
    public static double? WeekChange(SortedDictionary<DateOnly, double>? series, double now)
    {
        if (series is null || series.Count == 0) return null;

        var target = series.Keys.Last().AddDays(-7);
        foreach (var day in series.Keys.Reverse())
        {
            if (day > target) continue;
            return day >= target.AddDays(-3) ? (now - series[day]) * 100 : null;
        }
        return null;
    }

    /// <summary>
    /// Every activity's chart in one picture, made to be read at the size Slack shows it: wide,
    /// because Slack limits a picture's height and a tall one shrinks to fit; large text and thick
    /// lines; and one legend, at the top, for all of them. Null when no system has a history.
    /// </summary>
    public static byte[]? CombinedPng(
        IReadOnlyList<IndexSystem> systems,
        IReadOnlyDictionary<(int SystemId, string Activity), SortedDictionary<DateOnly, double>> daily)
    {
        const int Across = 3, PanelW = 440, PanelH = 270, TitleH = 36, Pad = 12, LegendRowH = 36;

        var drawn = systems.Where(s => s.Slot < ChartPaint.MaxSeries).ToList();
        var acts  = CostIndexHistoryService.Activities;
        var (labels, separators) = ParkCompareReport.PostPaints();

        var typeface = ParkCompareReport.PickTypeface([.. drawn.Select(s => s.Name), .. acts.Select(ActivityName)]);
        var bold     = SKTypeface.FromFamilyName(typeface.FamilyName, SKFontStyle.Bold) ?? typeface;
        using var legendFont = new SKFont(typeface, 24);
        using var titleFont  = new SKFont(bold, 26);
        using var paint      = new SKPaint { IsAntialias = true };

        var width = Across * PanelW + (Across + 1) * Pad;

        // The legend wraps, so a long list of systems still names them all.
        const float Swatch = 34, AfterSwatch = 8, BetweenEntries = 36;
        var entries = new List<(IndexSystem System, float X, int Row)>();
        var (lx, row) = ((float)Pad + 8, 0);
        foreach (var s in drawn)
        {
            var need = Swatch + AfterSwatch + ParkCompareReport.Measure(legendFont, s.Name);
            if (lx + need > width - Pad && lx > Pad + 8) { lx = Pad + 8; row++; }
            entries.Add((s, lx, row));
            lx += need + BetweenEntries;
        }
        var legendH = (row + 1) * LegendRowH + 8;

        var rows   = (acts.Length + Across - 1) / Across;
        var height = legendH + rows * (PanelH + TitleH) + (rows + 1) * Pad;

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(ParkCompareReport.Canvas);

        foreach (var (s, x, r) in entries)
        {
            var mid = 8 + r * LegendRowH + LegendRowH / 2f;
            paint.Color = SKColor.Parse(ChartPaint.SeriesHex[s.Slot]);
            paint.StrokeWidth = 5;
            canvas.DrawLine(x, mid, x + Swatch, mid, paint);
            paint.Color = new SKColor(220, 220, 232);
            canvas.DrawText(s.Name, x + Swatch + AfterSwatch, mid + legendFont.Size * 0.36f, legendFont, paint);
        }

        var any = false;
        for (var i = 0; i < acts.Length; i++)
        {
            var x = Pad + i % Across * (PanelW + Pad);
            var y = legendH + Pad + i / Across * (PanelH + TitleH + Pad);

            paint.Color = SKColors.White;
            canvas.DrawText(ActivityName(acts[i]), x + 8, y + 26, titleFont, paint);

            if (Chart(acts[i], systems, daily, labels, separators, textSize: 20) is not { } chart) continue;
            any = true;

            // Five dates fit across at this size, where the chart's own six run together. ⚠️ Through
            // a labeler: an axis given a list of labels ignores the step it is forced to.
            foreach (var axis in chart.XAxes)
            {
                var dates = axis.Labels ?? [];
                axis.Labels         = null;
                axis.Labeler        = v => (int)Math.Round(v) is var at && at >= 0 && at < dates.Count ? dates[at] : "";
                axis.MinStep        = Math.Max(1, dates.Count / 4);
                axis.ForceStepToMin = true;
            }
            foreach (var line in chart.Series.OfType<LineSeries<double?>>())
                if (line.Stroke is SolidColorPaint stroke) stroke.StrokeThickness = 3;

            var sk = new SKCartesianChart
            {
                Width = PanelW, Height = PanelH, Series = chart.Series, XAxes = chart.XAxes, YAxes = chart.YAxes,
                Background = ParkCompareReport.Canvas, LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden,
            };
            using var stream = new MemoryStream();
            sk.SaveImage(stream, SKEncodedImageFormat.Png);
            using var panel = SKBitmap.Decode(stream.ToArray());
            canvas.DrawBitmap(panel, x, y + TitleH);
        }

        if (!any) return null;

        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Today's table as a picture, in the park summary's look: a system a row, an activity a column
    /// under its name and wash, each value over its week's change. The lowest in green, a rise in
    /// red, a fall in green. For a post that wants the colours Slack's own table cannot have.
    /// </summary>
    /// <param name="values">By system then activity: today's index as a fraction, null when unknown.</param>
    /// <param name="changes">The same: the change over the week in points, null when unknown.</param>
    public static byte[] TablePng(
        IReadOnlyList<string> systems, IReadOnlyList<string> activities, string systemHeader,
        double?[][] values, double?[][] changes)
    {
        const float Pad = 16, Cell = 14, RowH = 92, HeadH = 62;

        var typeface = ParkCompareReport.PickTypeface([systemHeader, .. systems, .. activities]);
        var bold     = SKTypeface.FromFamilyName(typeface.FamilyName, SKFontStyle.Bold) ?? typeface;
        using var head   = new SKFont(bold, 30);
        using var value  = new SKFont(typeface, 32);
        using var strong = new SKFont(bold, 32);
        using var small  = new SKFont(typeface, 25);

        var lowest = Enumerable.Range(0, activities.Count)
            .Select(a => values.Select(v => v[a]).Where(v => v is not null).DefaultIfEmpty(null).Min()).ToArray();

        var nameW = Math.Max(ParkCompareReport.Measure(head, systemHeader), systems.Select(s => ParkCompareReport.Measure(strong, s)).DefaultIfEmpty(0).Max()) + 2 * Cell;

        static string Change(double c) => Math.Round(c, 2) switch
        {
            > 0 and var r => $"▲ {r:0.00}",
            < 0 and var r => $"▼ {Math.Abs(r):0.00}",
            _             => "=",
        };

        var columnW = activities.Select(a => ParkCompareReport.Measure(head, a)).DefaultIfEmpty(0).Max() + 2 * Cell;
        columnW = Math.Max(columnW, ParkCompareReport.Measure(strong, "100.00%") + 2 * Cell);

        var width  = 2 * Pad + nameW + columnW * activities.Count;
        var height = 2 * Pad + HeadH + RowH * systems.Count;

        using var surface = SKSurface.Create(new SKImageInfo((int)Math.Ceiling(width), (int)Math.Ceiling(height)));
        var canvas = surface.Canvas;
        canvas.Clear(ParkCompareReport.Canvas);

        using var fill = new SKPaint { IsAntialias = true };
        void Right(string s, float right, float y, SKFont f, SKColor colour)
        {
            fill.Color = colour;
            canvas.DrawText(s, right - ParkCompareReport.Measure(f, s), y + f.Size * 0.36f, f, fill);
        }
        void Rule(float y, byte alpha)
        {
            fill.Color = ParkCompareReport.RuleColour.WithAlpha(alpha);
            canvas.DrawRect(Pad, y, width - 2 * Pad, 1, fill);
        }

        for (var a = 0; a < activities.Count; a++)
        {
            var x = Pad + nameW + columnW * a;
            fill.Color = ParkCompareReport.Wash(a);
            canvas.DrawRect(x, Pad, columnW, height - 2 * Pad, fill);
            Right(activities[a], x + columnW - Cell, Pad + HeadH / 2, head, ParkCompareReport.HeadText);
        }

        fill.Color = ParkCompareReport.HeadText;
        canvas.DrawText(systemHeader, Pad + Cell, Pad + HeadH / 2 + head.Size * 0.36f, head, fill);
        Rule(Pad + HeadH, 255);

        for (var s = 0; s < systems.Count; s++)
        {
            var top = Pad + HeadH + s * RowH;
            fill.Color = ParkCompareReport.HeadText;
            canvas.DrawText(systems[s], Pad + Cell, top + RowH / 2 + strong.Size * 0.36f, strong, fill);

            for (var a = 0; a < activities.Count; a++)
            {
                var right = Pad + nameW + columnW * (a + 1) - Cell;
                if (values[s][a] is not { } v)
                {
                    Right("–", right, top + 30, value, ParkCompareReport.FaintText);
                    continue;
                }

                var isLow = v == lowest[a];
                Right(Pct(v), right, top + 30, isLow ? strong : value, isLow ? ParkCompareReport.BrightGood : ParkCompareReport.BodyText);

                if (changes[s][a] is { } change)
                    Right(Change(change), right, top + 67, small, Math.Round(change, 2) switch
                    {
                        > 0 => ParkCompareReport.BrightBad,
                        < 0 => ParkCompareReport.BrightGood,
                        _   => ParkCompareReport.FaintText,
                    });
            }

            Rule(top + RowH, 160);
        }

        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>An index as a percent, always two decimals, so a column of them lines up on the
    /// decimal point: 2.00%, not 2%.</summary>
    public static string Pct(double index) => $"{index * 100:0.00}%";

    /// <summary>Today's index for every activity, a row a system, as a monospaced table.</summary>
    public static string Table(
        IReadOnlyList<IndexSystem> systems, IReadOnlyDictionary<(int SystemId, string Activity), double> current,
        string heading, string formatName)
    {
        if (systems.Count == 0) return "";
        string[] heads = [IndustryText.IndexColSystem, .. CostIndexHistoryService.Activities.Select(ActivityName)];
        bool[]   right = [false, .. CostIndexHistoryService.Activities.Select(_ => true)];
        var cells = systems.Select(s => new[] { s.Name }
            .Concat(CostIndexHistoryService.Activities.Select(a => current.TryGetValue((s.SystemId, a), out var v) ? Pct(v) : "–"))
            .ToArray()).ToList();

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

    /// <summary>Systems by id, named as the interface names them, in the order given; an id the
    /// SDE does not know is left out.</summary>
    public static async Task<List<IndexSystem>> LookUpAsync(
        IDbContextFactory<AppDbContext> dbFactory, IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        await SdeNames.EnsureLoadedAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await (from s in db.SdeSolarSystems.AsNoTracking()
                          join r in db.SdeRegions.AsNoTracking() on s.RegionId equals r.RegionId into rs
                          from r in rs.DefaultIfEmpty()
                          where ids.Contains(s.SolarSystemId)
                          select new { s.SolarSystemId, s.Name, s.RegionId, Region = r != null ? r.Name : "" }).ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.SolarSystemId);
        return [.. ids.Where(byId.ContainsKey).Select(id => byId[id]).Select((r, slot) => new IndexSystem(
            r.SolarSystemId, SdeNames.SolarSystem(r.SolarSystemId, r.Name),
            r.Region.Length > 0 ? SdeNames.Region(r.RegionId, r.Region) : "", slot))];
    }

    /// <summary>Systems whose name starts with what was typed (or whose shown name contains it),
    /// for a search box: up to 30, named and in the interface's order.</summary>
    public static async Task<List<IndexSystem>> SearchAsync(
        IDbContextFactory<AppDbContext> dbFactory, string text, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length < 2) return [];
        await SdeNames.EnsureLoadedAsync();
        var shownIds = SdeNames.Find(SdeNameKind.SolarSystem, text).Select(id => (int)id).ToList();
        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            ids = await db.SdeSolarSystems.AsNoTracking()
                .Where(s => EF.Functions.Like(s.Name, $"{text}%") || shownIds.Contains(s.SolarSystemId))
                .OrderBy(s => s.Name).Take(30).Select(s => s.SolarSystemId).ToListAsync(ct);
        return [.. (await LookUpAsync(dbFactory, ids, ct)).OrderBy(s => s.Name, StringComparer.CurrentCulture)];
    }
}
