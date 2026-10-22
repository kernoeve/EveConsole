using System.Text;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.ViewModels;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.EntityFrameworkCore;

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
        SolidColorPaint labels, SolidColorPaint separators, double textSize = 9)
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
                Labels = [.. days.Select(d => d.ToString("MM-dd"))], LabelsPaint = labels, TextSize = textSize,
                MinStep = Math.Max(1, days.Count / 6), SeparatorsPaint = null,
            }],
            [new Axis
            {
                Labeler = v => $"{v:0.#}%", LabelsPaint = labels, TextSize = textSize, MinLimit = 0,
                SeparatorsPaint = separators,
            }],
            string.Format(IndustryText.IndexChartsNote, days[0], days[^1]));
    }

    /// <summary>Today's index for every activity, a row a system, as a monospaced table.</summary>
    public static string Table(
        IReadOnlyList<IndexSystem> systems, IReadOnlyDictionary<(int SystemId, string Activity), double> current,
        string heading, string formatName)
    {
        if (systems.Count == 0) return "";
        string[] heads = [IndustryText.IndexColSystem, .. CostIndexHistoryService.Activities.Select(ActivityName)];
        bool[]   right = [false, .. CostIndexHistoryService.Activities.Select(_ => true)];
        var cells = systems.Select(s => new[] { s.Name }
            .Concat(CostIndexHistoryService.Activities.Select(a => current.TryGetValue((s.SystemId, a), out var v) ? $"{v * 100:0.##}%" : "–"))
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
