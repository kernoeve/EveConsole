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

    public string Type { get; set; } = TypeText;

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
}

/// <summary>What a "post to Slack" task is configured to do.</summary>
public sealed class SlackPostConfig
{
    /// <summary>"chan" or "hook", matching SlackDestination.</summary>
    public string DestinationKind { get; set; } = "";
    public string DestinationId   { get; set; } = "";

    public List<MessageBlock> Blocks { get; set; } = [];

    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Opts);

    /// <summary>⚠️ Never throws. A task whose configuration cannot be read is reported as a task
    /// with nothing to say, which the runner records — an exception here would take the whole
    /// scheduling loop down with it.</summary>
    public static SlackPostConfig FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new SlackPostConfig();
        try   { return JsonSerializer.Deserialize<SlackPostConfig>(json!, Opts) ?? new SlackPostConfig(); }
        catch { return new SlackPostConfig(); }
    }
}

/// <summary>
/// Turns message blocks into the text a scheduled post sends.
///
/// <para>⚠️ Renders without a view model. The same lists already appear on the Corp Activity
/// screen, but that export reads what the screen happens to have loaded — a selected corp, a
/// selected month, rows already fetched. A task firing at 00:01 has none of that, so it asks the
/// service for the figures it wants and formats them here.</para>
/// </summary>
public class ScheduledBlockRenderer(CorpActivityService corp)
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
    public async Task<string> RenderAsync(
        IReadOnlyList<MessageBlock> blocks, DateTime nowUtc, CancellationToken ct = default)
    {
        var parts = new List<string>();

        foreach (var b in blocks)
        {
            ct.ThrowIfCancellationRequested();

            var text = b.Type switch
            {
                MessageBlock.TypeText    => b.Text.Trim(),
                MessageBlock.TypeTop10   => await Top10Async(b, nowUtc, ct),
                MessageBlock.TypeMonthly => await MonthlyAsync(b, nowUtc, ct),
                _                        => "",
            };

            if (text.Length > 0) parts.Add(text);
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>The window a "months back" block covers: that whole calendar month.</summary>
    private static (DateTimeOffset From, DateTimeOffset To, string Label) Window(int monthsBack, DateTime nowUtc)
    {
        var first = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc)
                        .AddMonths(-Math.Max(0, monthsBack));

        return (new DateTimeOffset(first),
                new DateTimeOffset(first.AddMonths(1)),
                first.ToString("MMMM yyyy"));
    }

    private async Task<string> Top10Async(MessageBlock b, DateTime nowUtc, CancellationToken ct)
    {
        if (b.CorpId <= 0 || b.Categories.Count == 0) return "";

        var (from, to, label) = Window(b.MonthsBack, nowUtc);
        var sb = new StringBuilder();

        sb.AppendLine($"*Top 10 — {label}*");

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

            sb.AppendLine();
            sb.AppendLine($"*{TitleFor(key)}*");
            sb.AppendLine("```");

            var rank = 0;
            foreach (var row in rows)
            {
                // Kills counts kills; everything else is ISK. Printing "1,204 ISK" for a kill
                // count would be a units error nobody would question in a Slack post.
                var amount = key == "kills"
                    ? ((long)row.Amount).ToString("N0")
                    : MarketFmt.Isk((double)row.Amount);

                var who = names.GetValueOrDefault(row.Id, row.Id.ToString());
                sb.AppendLine($"{++rank,2}. {who,-24} {amount,18}");
            }

            sb.AppendLine("```");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The month's headline figures.
    ///
    /// <para>Deliberately the same five lists' totals rather than a second set of numbers: a
    /// summary that disagreed with the list under it would be the thing everybody asked about.</para>
    /// </summary>
    private async Task<string> MonthlyAsync(MessageBlock b, DateTime nowUtc, CancellationToken ct)
    {
        if (b.CorpId <= 0) return "";

        var (from, to, label) = Window(b.MonthsBack, nowUtc);

        var ratting  = await corp.GetTopRattersAsync (b.CorpId, from, to, null, ct);
        var mining   = await corp.GetTopMinersAsync  (b.CorpId, from, to, null, ct);
        var kills    = await corp.GetTopKillersAsync (b.CorpId, from, to, null, ct);
        var industry = await corp.GetTopIndustryAsync(b.CorpId, from, to, null, ct);
        var projects = await corp.GetTopProjectContributorsAsync(b.CorpId, from, to, null, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"*Monthly summary — {label}*");
        sb.AppendLine("```");
        sb.AppendLine($"{"Ratting tax",-24}{MarketFmt.Isk((double)ratting.Sum(r => r.Amount)),18}");
        sb.AppendLine($"{"Mining (reprocessed)",-24}{MarketFmt.Isk((double)mining.Sum(r => r.Amount)),18}");
        sb.AppendLine($"{"Industry tax",-24}{MarketFmt.Isk((double)industry.Sum(r => r.Amount)),18}");
        sb.AppendLine($"{"Project contributions",-24}{MarketFmt.Isk((double)projects.Sum(r => r.IskPayout)),18}");
        sb.AppendLine($"{"Kills",-24}{((long)kills.Sum(r => r.Amount)).ToString("N0"),18}");
        sb.AppendLine("```");

        return sb.ToString().TrimEnd();
    }
}
