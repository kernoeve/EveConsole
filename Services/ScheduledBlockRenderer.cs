using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

// MarketFmt formats ISK the way every other surface in the app does. It lives with the view
// models because that is where it was first needed; a scheduled post quoting different figures
// from the same numbers would be worse than the tidier namespace.
using EveConsole.ViewModels;

namespace EveConsole.Services;

/// <summary>One piece of a composed message.</summary>
public sealed class MessageBlock
{
    public const string TypeText    = "text";
    public const string TypeTop10   = "corp_top10";
    public const string TypeMonthly = "corp_monthly";
    public const string TypeSale    = "sale_posting";
    public const string TypeProjects = "standing_projects";

    public string Type { get; set; } = TypeText;

    /// <summary>
    /// Whether this section's content comes from the data rather than from what was typed.
    ///
    /// <para>Text is the only section that always has something to say. Every other kind can
    /// render to nothing — an empty month, a posting with no stock, no project needing anything
    /// — which is what "do not post if the dynamic sections are empty" is about.</para>
    /// </summary>
    public bool IsDynamic => Type != TypeText;

    /// <summary>Static text blocks: what to say.</summary>
    public string Text { get; set; } = "";

    /// <summary>Corp blocks: whose figures.</summary>
    public long CorpId { get; set; }

    /// <summary>
    /// Corp blocks: how many months back, 0 being the month in progress.
    ///
    /// <para>⚠️ Relative, never a fixed month. A task that posts "last month" has to keep meaning
    /// last month every time it runs; a stored year and month would say January forever.</para>
    /// </summary>
    public int MonthsBack { get; set; } = 1;

    /// <summary>Top 10 blocks: which of the five lists to include, by key.</summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>
    /// Top 10 blocks: print the share of the total, and not what it was worth.
    ///
    /// <para>The same switch the Corp Activity tab offers, for the same reason: a ranking is
    /// often the point where the ISK is nobody else's business.</para>
    ///
    /// <para>⚠️ Kills keeps its number either way. Its "amount" is a count of kills, not a
    /// value, so there is nothing to withhold and dropping it would leave a list of names.</para>
    /// </summary>
    public bool HideIsk { get; set; }

    /// <summary>Sale posting blocks: which defined posting to render.</summary>
    public int PostingId { get; set; }

    /// <summary>Standing project blocks: "deliver_item" or "destroy_npc".</summary>
    public string ProjectType { get; set; } = "destroy_npc";

    /// <summary>
    /// Standing project blocks: how much of the list to report.
    ///
    /// <para>⚠️ Defaults to All, which is what these sections did before there was a choice. A
    /// task written yesterday has to keep saying what it said yesterday.</para>
    /// </summary>
    public string ProjectFilter { get; set; } = ProjectFilters.All;

    /// <summary>
    /// Standing project blocks: the heading over the table.
    ///
    /// <para>Empty means the one the section builds for itself. Stored empty rather than stamped
    /// with today's default, so a section that was never retitled keeps following the type and
    /// filter it is actually reporting on.</para>
    /// </summary>
    public string SectionTitle { get; set; } = "";

    /// <summary>Standing project blocks: print a header row above the table.</summary>
    public bool ShowHeaders { get; set; }

    /// <summary>
    /// Standing project blocks: exactly the projects to report on.
    ///
    /// <para>⚠️ Inclusions, not exclusions. A new section starts with everything ticked, but what
    /// gets stored is the list itself — so a project defined next month does NOT appear in a task
    /// written today. Nothing joins a saved post without somebody putting it there.</para>
    /// </summary>
    public List<long> IncludedProjectIds { get; set; } = [];
}

/// <summary>How much of a standing project list to report.</summary>
public static class ProjectFilters
{
    /// <summary>Only what no active project is currently covering.</summary>
    public const string Missing = "missing";

    /// <summary>That, plus the ones nearly finished and about to need replacing.</summary>
    public const string MissingAndLow = "missing_low";

    public const string All = "all";

    public static string Label(string key) => key switch
    {
        Missing       => "Missing projects",
        MissingAndLow => "Missing and low projects",
        _             => "All projects",
    };
}

/// <summary>
/// What a task is configured to do.
///
/// <para>One config for every task type rather than one each. The blocks are the part both types
/// share — the same composed message goes to Slack or into an alert — and a type only reads the
/// fields it needs.</para>
/// </summary>
public sealed class ScheduledTaskConfig
{
    /// <summary>Slack posts: "chan" or "hook", matching SlackDestination.</summary>
    public string DestinationKind { get; set; } = "";
    public string DestinationId   { get; set; } = "";

    /// <summary>Alerts: the headline. Empty falls back to the task's own name.</summary>
    public string AlertTitle { get; set; } = "";

    /// <summary>Alerts: what the alert says, under the headline.</summary>
    public string AlertText { get; set; } = "";

    public List<MessageBlock> Blocks { get; set; } = [];

    /// <summary>
    /// Slack posts: stay silent unless a dynamic section actually said something.
    ///
    /// <para>Off by default, because a post whose static text is the point should still go out.
    /// On, a message of headings over empty data is not worth sending — and a channel that only
    /// hears from this task when there is something to hear is a channel people keep reading.</para>
    /// </summary>
    public bool SkipIfNoDynamicContent { get; set; }

    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Opts);

    /// <summary>⚠️ Never throws. A task whose configuration cannot be read is reported as a task
    /// with nothing to say, which the runner records — an exception here would take the whole
    /// scheduling loop down with it.</summary>
    public static ScheduledTaskConfig FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ScheduledTaskConfig();
        try   { return JsonSerializer.Deserialize<ScheduledTaskConfig>(json!, Opts) ?? new ScheduledTaskConfig(); }
        catch { return new ScheduledTaskConfig(); }
    }
}

/// <summary>
/// A rendered message, and whether any of the data-driven part of it had anything to say.
/// </summary>
/// <param name="Text">The whole message.</param>
/// <param name="AnyDynamicContent">
/// True when at least one dynamic section rendered to something. ⚠️ False also when there are no
/// dynamic sections at all: "post only when there is data" cannot be satisfied by a message that
/// never asks for any.
/// </param>
public sealed record RenderedMessage(string Text, bool AnyDynamicContent);

/// <summary>
/// Turns message blocks into the text a scheduled post sends.
///
/// <para>⚠️ Renders without a view model. The same lists already appear on the Corp Activity
/// screen, but that export reads what the screen happens to have loaded — a selected corp, a
/// selected month, rows already fetched. A task firing at 00:01 has none of that, so it asks the
/// service for the figures it wants and formats them here.</para>
/// </summary>
public class ScheduledBlockRenderer(CorpActivityService corp, SalePostingService sales)
{
    /// <summary>The five lists, in the order the manual export prints them.</summary>
    public static readonly (string Key, string Title)[] Top10Categories =
    [
        ("ratting",  "Ratting Tax"),
        ("mining",   "Mining — Reprocessed Value"),
        ("kills",    "Kills"),
        ("projects", "Project Contributors"),
        ("industry", "Industry Tax"),
    ];

    public static string TitleFor(string key) =>
        Top10Categories.FirstOrDefault(c => c.Key == key).Title ?? key;

    /// <summary>Renders every block, in order, into one message.</summary>
    public async Task<RenderedMessage> RenderAsync(
        IReadOnlyList<MessageBlock> blocks, DateTime nowUtc, CancellationToken ct = default)
    {
        var parts      = new List<string>();
        var anyDynamic = false;

        foreach (var b in blocks)
        {
            ct.ThrowIfCancellationRequested();

            var text = b.Type switch
            {
                MessageBlock.TypeText     => b.Text.Trim(),
                MessageBlock.TypeTop10    => await Top10Async(b, nowUtc, ct),
                MessageBlock.TypeMonthly  => await MonthlyAsync(b, nowUtc, ct),
                MessageBlock.TypeSale     => await SalePostingAsync(b, ct),
                MessageBlock.TypeProjects => await StandingProjectsAsync(b, ct),
                _                         => "",
            };

            if (text.Length == 0) continue;

            parts.Add(text);
            if (b.IsDynamic) anyDynamic = true;
        }

        return new RenderedMessage(string.Join("\n\n", parts), anyDynamic);
    }

    /// <summary>The calendar month a "months back" block names.</summary>
    private static (int Year, int Month, string Label) Month(int monthsBack, DateTime nowUtc)
    {
        var first = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc)
                        .AddMonths(-Math.Max(0, monthsBack));

        return (first.Year, first.Month, first.ToString("MMMM yyyy"));
    }

    /// <summary>That same month as a half-open range, for the ranked lists.</summary>
    private static (DateTimeOffset From, DateTimeOffset To, string Label) Window(int monthsBack, DateTime nowUtc)
    {
        var (year, month, label) = Month(monthsBack, nowUtc);
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);

        return (new DateTimeOffset(first), new DateTimeOffset(first.AddMonths(1)), label);
    }

    private async Task<string> Top10Async(MessageBlock b, DateTime nowUtc, CancellationToken ct)
    {
        if (b.CorpId <= 0 || b.Categories.Count == 0) return "";

        var (from, to, label) = Window(b.MonthsBack, nowUtc);

        // Gathered before anything is printed, so every list shares one column grid rather than
        // each finding its own widths and the five sections stepping in and out.
        var lists = new List<(string Title, List<string[]> Cells)>();

        foreach (var key in b.Categories)
        {
            ct.ThrowIfCancellationRequested();

            // ⚠️ Project contributors come back already named and in a different shape. Every
            // other list is ids and amounts, so they are brought to one form here rather than
            // printed by a second copy of the loop below.
            List<(long Id, decimal Amount)> rows;
            Dictionary<long, string> names;

            if (key == "projects")
            {
                var contrib = await corp.GetTopProjectContributorsAsync(b.CorpId, from, to, null, ct);
                rows  = [.. contrib.Select(c => (c.CharacterId, c.IskPayout))];
                names = contrib
                    .GroupBy(c => c.CharacterId)
                    .ToDictionary(g => g.Key, g => g.First().Name);
            }
            else
            {
                var ranked = key switch
                {
                    "ratting"  => await corp.GetTopRattersAsync(b.CorpId, from, to, null, ct),
                    "mining"   => await corp.GetTopMinersAsync(b.CorpId, from, to, null, ct),
                    "kills"    => await corp.GetTopKillersAsync(b.CorpId, from, to, null, ct),
                    "industry" => await corp.GetTopIndustryAsync(b.CorpId, from, to, null, ct),
                    _          => [],
                };

                rows  = [.. ranked.Select(x => (x.CharacterId, x.Amount))];
                names = await corp.ResolveNamesAsync(
                    rows.Select(x => x.Id).Distinct().ToList(), ct);
            }

            if (rows.Count == 0) continue;

            // ⚠️ The share is of what this list totals, not of some corp-wide figure. These are
            // the top ten, so the total is the ten shown DASH which is what "% of total" means on
            // the tab this mirrors.
            var total = rows.Sum(r => r.Amount);

            // Kills counts kills; everything else is ISK. Printing "1,204 ISK" for a kill count
            // would be a units error nobody would question in a Slack post — and it is why kills
            // keeps its number even when the ISK is suppressed.
            var isKills   = key == "kills";
            var showValue = isKills || !b.HideIsk;

            var cells = new List<string[]>();
            var rank  = 0;

            foreach (var row in rows)
            {
                var who   = names.GetValueOrDefault(row.Id, row.Id.ToString());
                var value = isKills
                    ? ((long)row.Amount).ToString("N0")
                    : MarketFmt.Isk((double)row.Amount);

                var share = total > 0
                    ? ((double)(row.Amount / total) * 100.0).ToString("F1") + "%"
                    : "";

                cells.Add(showValue ? [$"{++rank}.", who, value, share]
                                    : [$"{++rank}.", who, share]);
            }

            lists.Add((TitleFor(key), cells));
        }

        if (lists.Count == 0) return "";

        // One grid across every list in this section.
        var columns = lists.Max(l => l.Cells.Max(c => c.Length));
        var widths  = new int[columns];
        foreach (var l in lists)
            foreach (var c in l.Cells)
                for (var i = 0; i < c.Length; i++)
                    widths[i] = Math.Max(widths[i], c[i].Length);

        var sb = new StringBuilder();
        sb.AppendLine($"*Top 10 — {label}*");

        foreach (var (title, cells) in lists)
        {
            sb.AppendLine();
            sb.AppendLine($"*{title}*");
            sb.AppendLine("```");

            foreach (var c in cells)
            {
                var line = new StringBuilder();
                for (var i = 0; i < c.Length; i++)
                {
                    // Numbers read right-aligned, the name reads left. The last cell is not padded,
                    // so there is no trailing whitespace inside the fence.
                    if (i == c.Length - 1)      line.Append(c[i].PadLeft(widths[i]));
                    else if (i == 1)            line.Append(c[i].PadRight(widths[i] + 2));
                    else                        line.Append(c[i].PadLeft(widths[i])).Append("  ");
                }
                sb.AppendLine(line.ToString());
            }

            sb.AppendLine("```");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The Corp Activity tool's Monthly Summary, rendered for a message.
    ///
    /// <para>⚠️ The same report the screen shows, not a second set of numbers. MonthlySummaryReport
    /// holds what that summary says; both the export button and this ask it.</para>
    /// </summary>
    private async Task<string> MonthlyAsync(MessageBlock b, DateTime nowUtc, CancellationToken ct)
    {
        if (b.CorpId <= 0) return "";

        var (year, month, _) = Month(b.MonthsBack, nowUtc);

        var summary = await corp.GetMonthSummaryAsync(b.CorpId, year, month, ct);
        var lines   = MonthlySummaryReport.Build(summary);

        var header = MonthlySummaryReport.Header(
            await CorpNameAsync(b.CorpId, ct),
            System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month),
            year);

        return MonthlySummaryReport.Export(lines, header, "Slack");
    }

    /// <summary>
    /// A defined sale posting, rendered for a message.
    ///
    /// <para>⚠️ The same render the Sale Posting tool and the store mail use, so a scheduled
    /// listing prices an item exactly as the screen does. Nothing about pricing is decided here.</para>
    ///
    /// <para>The tool posts block 0 as a message and the rest as threaded replies. A scheduled
    /// block is one piece of one message, so they are joined in the same order instead — the
    /// thread is a shape the tool's own button owns, not the posting's.</para>
    /// </summary>
    private async Task<string> SalePostingAsync(MessageBlock b, CancellationToken ct)
    {
        if (b.PostingId <= 0) return "";

        var posts = await sales.RenderAsync(b.PostingId, "Slack", ct);

        return string.Join("\n\n", posts
            .Select(p => p.Text.Trim())
            .Where(t => t.Length > 0));
    }

    /// <summary>
    /// The standing projects of one type, expanded.
    ///
    /// <para>⚠️ Expanded here, chosen unexpanded. One definition scoped to a region becomes a row
    /// per qualifying system, which is the whole point of reporting it — but the person picking
    /// which projects to report on picked definitions, so the exclusions are matched against
    /// DbId, which every expanded row still carries.</para>
    /// </summary>
    private async Task<string> StandingProjectsAsync(MessageBlock b, CancellationToken ct)
    {
        if (b.CorpId <= 0) return "";

        // ⚠️ The type comes from the DEFINITIONS, not from reading it back off an expanded row.
        // A row could only be classified by whether it names an item, and a delivery project saved
        // without one would then be filed under destroy-NPC — wrong, and silently so.
        //
        // Intersected with what still exists, so a deleted project drops out rather than being
        // looked for among rows that no longer mention it.
        var defs = await corp.GetStandingProjectsAsync(b.CorpId, ct);
        var want = b.IncludedProjectIds.ToHashSet();

        var keep = defs
            .Where(d => d.ProjectType == b.ProjectType && want.Contains(d.Id))
            .Select(d => d.Id)
            .ToHashSet();

        if (keep.Count == 0) return "";

        var rows = await corp.BuildMaintainGridRowsAsync(b.CorpId, ct);

        var wanted = rows
            .Where(r => keep.Contains(r.DbId))
            .Where(r => StandingProjectReport.Wanted(r, b.ProjectFilter))
            .ToList();

        var heading = b.SectionTitle.Trim().Length > 0
            ? b.SectionTitle.Trim()
            : StandingProjectReport.DefaultTitle(b.ProjectType, b.ProjectFilter);

        return StandingProjectReport.Export([.. wanted], heading, b.ProjectType, b.ShowHeaders);
    }

    /// <summary>The corp's name, or nothing — a header without one still reads correctly.</summary>
    private async Task<string?> CorpNameAsync(long corpId, CancellationToken ct)
    {
        var names = await corp.ResolveNamesAsync([corpId], ct);
        return names.GetValueOrDefault(corpId);
    }
}
