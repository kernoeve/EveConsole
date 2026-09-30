using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// OutputFormat lives with the view models because the export dropdown is where it was first
// needed. A scheduled post that formatted its own fences differently from the button on the
// screen would be worse than the tidier namespace.
using EveConsole.ViewModels;
using EveConsole.Localization;

namespace EveConsole.Services;

/// <summary>
/// The Corp Activity Monthly Summary, as data and as text.
///
/// <para>⚠️ The ONE definition of what that summary says. It was built inside the Corp Activity
/// view model, which meant a scheduled post could only have had a second copy — and a second copy
/// that disagreed with the screen would be the thing everybody asked about. The screen maps these
/// lines into its grid rows; the scheduler renders them straight to text.</para>
/// </summary>
public static class MonthlySummaryReport
{
    /// <summary>
    /// One line. Section headers are the same type with <see cref="IsHeader"/> set, so the grid
    /// and the export walk a single ordered list rather than each re-deriving the layout.
    /// </summary>
    public sealed record SummaryLine(
        string Label,
        string Value      = "",
        string Change     = "",
        string Percent    = "",
        bool   IsHeader   = false,
        string ValueColor = "#ccccdd",
        /// <summary>A summing line, ruled off from the rows it adds up.</summary>
        bool   IsTotal    = false);

    // ── The lines ────────────────────────────────────────────────────────────

    public static List<SummaryLine> Build(
        CorpActivityService.MonthSummary s, CorpReportTitles? titles = null)
    {
        var lines = new List<SummaryLine>();

        var c = s.Current;
        var p = s.Previous;

        // ⚠️ Renamed on the way in, so every line downstream already carries the heading the
        // reader chose. Renaming at print time instead would leave the grid and the export
        // showing different words for the same section.
        //
        // ⚠️ By the section's KEY (see CorpReportTitles.SummarySections), never by its words:
        // the default words are in the interface language.
        void Header(string key) =>
            lines.Add(new SummaryLine(titles?.SummaryTitle(key) ?? CorpReportTitles.SummaryDefault(key),
                                      IsHeader: true));

        // ISK line: value, signed absolute change, and the same change as a percentage.
        void Isk(string label, decimal cur, decimal prev, string? color = null, bool total = false) =>
            lines.Add(new SummaryLine(
                label, FormatIsk(cur), SignedIsk(cur - prev), Pct(cur, prev),
                ValueColor: color ?? "#ccccdd", IsTotal: total));

        void Count(string label, long cur, long prev, string? color = null) =>
            lines.Add(new SummaryLine(
                label, cur.ToString("N0"), SignedCount(cur - prev), Pct(cur, prev),
                ValueColor: color ?? "#ccccdd"));

        var w  = c.Wallet;
        var pw = p.Wallet;

        Header("income");
        // No "Mining tax" line: EVE has no corp mining-tax wallet entry, and this corp has
        // never had one. Mining is billed manually and lands in Donations, which cannot be
        // separated from other donations.
        Isk(CorpText.SummaryRowRattingTax,  w?.RattingTax     ?? 0m, pw?.RattingTax     ?? 0m);
        Isk(CorpText.SummaryRowIndustryTax, w?.IndustryTax    ?? 0m, pw?.IndustryTax    ?? 0m);
        Isk(CorpText.SummaryRowDonations,   w?.Donations      ?? 0m, pw?.Donations      ?? 0m);
        Isk(CorpText.SummaryRowContracts,   w?.ContractIncome ?? 0m, pw?.ContractIncome ?? 0m);
        Isk(CorpText.SummaryRowMarket,      w?.MarketIncome   ?? 0m, pw?.MarketIncome   ?? 0m);
        Isk(CorpText.SummaryRowOther,       w?.OtherIncome    ?? 0m, pw?.OtherIncome    ?? 0m);
        Isk(CorpText.SummaryRowTotalIncome, c.TotalIncome,           p.TotalIncome, total: true);

        Header("expenses");
        Isk(CorpText.SummaryRowMarket,         w?.MarketExpense   ?? 0m, pw?.MarketExpense   ?? 0m);
        Isk(CorpText.SummaryRowContracts,      w?.ContractExpense ?? 0m, pw?.ContractExpense ?? 0m);
        Isk(CorpText.SummaryRowProjectPayouts, w?.ProjectPayouts  ?? 0m, pw?.ProjectPayouts  ?? 0m);
        Isk(CorpText.SummaryRowWithdrawals,    w?.AccountWithdraw ?? 0m, pw?.AccountWithdraw ?? 0m);
        Isk(CorpText.SummaryRowOther,          w?.OtherExpense    ?? 0m, pw?.OtherExpense    ?? 0m);
        Isk(CorpText.SummaryRowTotalExpenses,  c.TotalExpense,           p.TotalExpense, total: true);

        Header("net");
        Isk(CorpText.SummaryRowNetPosition, c.Net, p.Net, c.Net >= 0 ? "#70ad47" : "#cc6666");

        Header("combat");
        Count(CorpText.SummaryRowKills,  c.Kills,  p.Kills);
        Count(CorpText.SummaryRowLosses, c.Losses, p.Losses);
        Isk(CorpText.SummaryRowIskDestroyed, c.IskDestroyed, p.IskDestroyed);
        Isk(CorpText.SummaryRowIskLost,      c.IskLost,      p.IskLost);
        lines.Add(new SummaryLine(
            CorpText.SummaryRowIskEfficiency,
            c.IskEfficiency is { } e ? $"{e:F1}%" : "—",
            c.IskEfficiency is { } e1 && p.IskEfficiency is { } e0
                ? string.Format(CorpText.SummaryChangePoints, (e1 - e0 > 0 ? "+" : ""), e1 - e0) : "",
            ValueColor: c.IskEfficiency is { } e2 ? (e2 >= 50 ? "#70ad47" : "#cc6666") : "#ccccdd"));

        Header("mining");
        Count(CorpText.SummaryRowUnitsMined, c.UnitsMined,  p.UnitsMined);
        Isk(CorpText.SummaryRowMinedValue,   c.MiningValue, p.MiningValue);

        Header("projects");
        Count(CorpText.SummaryRowCreated,      c.ProjectsCreated,        p.ProjectsCreated);
        Isk(CorpText.SummaryRowCreatedValue,   c.ProjectsCreatedValue,   p.ProjectsCreatedValue);
        Count(CorpText.SummaryRowCompleted,    c.ProjectsCompleted,      p.ProjectsCompleted);
        Isk(CorpText.SummaryRowCompletedValue, c.ProjectsCompletedValue, p.ProjectsCompletedValue);

        Header("members");
        Count(CorpText.SummaryRowActivePlayers, c.PlayersActive, p.PlayersActive);

        return lines;
    }

    // ── The text ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The summary as text for Slack / Discord / forums.
    ///
    /// The rule lines under the title and each section are kept in every format — they carry the
    /// structure when a client renders markup weakly or not at all, and bold is an addition to
    /// them rather than a replacement.
    /// </summary>
    public static string Export(IReadOnlyList<SummaryLine> lines, string header, string formatName)
    {
        var fmt   = OutputFormat.ByName(formatName);
        var plain = fmt.Name == "Plain Text";

        var subtitle = CorpText.SummaryExportSubtitle;

        var sb = new StringBuilder();
        sb.AppendLine(plain ? header : fmt.Bold(header));
        sb.AppendLine(new string('=', Math.Max(header.Length, 32)));
        sb.AppendLine(plain ? subtitle : fmt.Bold(subtitle));

        string[] columnNames = [CorpText.SummaryColItem, CorpText.SummaryColAmount, CorpText.Change, "%"];

        // Widths measured across every value row AND the header, so all sections share one column
        // grid and the headings fit inside it.
        var cells  = lines.Where(l => !l.IsHeader)
                          .Select(l => new[] { l.Label, l.Value, l.Change, l.Percent })
                          .Append(columnNames)
                          .ToList();
        var widths = ColumnWidths(cells, 4);
        var (open, close) = CodeFence(fmt.Name);

        // A dash run per column, so the break lines up with the columns rather than running the
        // width of the widest line.
        string Rule() => PaddedRow([.. widths.Select(w => new string('-', w))], widths);

        var inFence = false;
        void CloseFence()
        {
            if (!inFence) return;
            if (close.Length > 0) sb.AppendLine(close);
            inFence = false;
        }

        foreach (var line in lines)
        {
            if (line.IsHeader)
            {
                CloseFence();
                sb.AppendLine();
                sb.AppendLine(plain ? line.Label : fmt.Bold(line.Label));
                // Slack draws a fenced block as an outlined box, so a rule above it is just
                // noise. Every other format keeps the rule — the box either is not drawn or is
                // not distinct enough to replace it.
                if (fmt.Name != "Slack")
                    sb.AppendLine(new string('-', Math.Max(line.Label.Length, 16)));
                continue;
            }

            if (!inFence)
            {
                if (open.Length > 0) sb.AppendLine(open);
                inFence = true;

                // ⚠️ Per section, not once at the top. Each fence is its own box in Slack, and a
                // heading in the first one describes nothing about the six below it.
                sb.AppendLine(PaddedRow(columnNames, widths));
                sb.AppendLine(Rule());
            }

            // A total is ruled off from the rows it sums, the same way the header is.
            if (line.IsTotal) sb.AppendLine(Rule());

            sb.AppendLine(PaddedRow([line.Label, line.Value, line.Change, line.Percent], widths));
        }
        CloseFence();

        var body = sb.ToString().TrimEnd();

        // Plain Text's Finalize is a markup stripper that also collapses runs of spaces, which
        // would flatten the column padding this block depends on. Nothing here emits markup in
        // the first place, so there is nothing to strip.
        return plain ? body : fmt.Finalize(body);
    }

    /// <summary>The title line, built the one way so the screen and a scheduled post agree.</summary>
    public static string Header(string? corpName, string monthName, int year, string prefix = "")
    {
        var line = string.IsNullOrWhiteSpace(corpName)
            ? string.Format(CorpText.SummaryHeader, monthName, year)
            : string.Format(CorpText.SummaryHeaderCorp, corpName, monthName, year);

        // Prepended verbatim with one space. Whatever punctuation belongs after it is the
        // author's to type, since only they know whether it reads as a label or a sentence.
        return string.IsNullOrWhiteSpace(prefix) ? line : $"{prefix.Trim()} {line}";
    }

    // ── Column layout for exported text ──────────────────────────────────────
    //
    // Columns are space-padded and the data rows are wrapped in the target platform's
    // code-block syntax, which switches it to a monospace font where padding is exact.
    //
    // Tabs were tried first and cannot work: Slack renders messages proportionally and its tab
    // stops are pixel-based, so which stop a row reaches depends on the pixel width of the text
    // before it, and no count of characters predicts that. Adjusting the tab count per platform
    // only moves the misalignment. Fencing is also the only approach that works for Copy to
    // Clipboard, which is how most people post — the Block Kit rich_text route would only have
    // helped the API-based Post to Slack button.

    private static (string Open, string Close) CodeFence(string formatName) => formatName switch
    {
        "Slack" or "Discord" or "Markdown" => ("```", "```"),
        "HTML"                             => ("<pre>", "</pre>"),
        "BBCode"                           => ("[code]", "[/code]"),
        _                                  => ("", ""),   // Plain Text needs no fence
    };

    private static int[] ColumnWidths(IReadOnlyList<string[]> rows, int columns)
    {
        var widths = new int[columns];
        for (var i = 0; i < columns; i++)
            widths[i] = rows.Count == 0 ? 0 : rows.Max(r => MonoColumns.Width(r[i]));
        return widths;
    }

    /// <summary>Space-padded row. Two spaces of gutter between columns; the last populated cell
    /// is not padded, so there is no trailing whitespace.</summary>
    private static string PaddedRow(string[] cells, int[] widths)
    {
        var last = cells.Length - 1;
        while (last > 0 && string.IsNullOrEmpty(cells[last])) last--;

        var sb = new StringBuilder();
        for (var i = 0; i <= last; i++)
            sb.Append(i == last ? cells[i] : MonoColumns.PadRight(cells[i], widths[i] + 2));
        return sb.ToString();
    }

    // ── Numbers ──────────────────────────────────────────────────────────────

    private static string Pct(decimal current, decimal previous)
    {
        if (previous == 0m) return "";
        var change = (double)((current - previous) / Math.Abs(previous)) * 100.0;
        if (Math.Abs(change) < 0.05) return "0.0%";
        return $"{(change > 0 ? "+" : "")}{change:F1}%";
    }

    private static string Pct(long current, long previous) => Pct((decimal)current, previous);

    private static string SignedIsk(decimal delta) =>
        delta == 0m ? "—" : $"{(delta > 0 ? "+" : "-")}{FormatIsk(Math.Abs(delta))}";

    private static string SignedCount(long delta) =>
        delta == 0 ? "—" : $"{(delta > 0 ? "+" : "-")}{Math.Abs(delta):N0}";

    private static string FormatIsk(decimal value)
    {
        var v   = (double)value;
        var abs = Math.Abs(v);
        if (abs >= 1_000_000_000) return $"{v / 1_000_000_000:F2}B";
        if (abs >= 1_000_000)     return $"{v / 1_000_000:F2}M";
        if (abs >= 1_000)         return $"{v / 1_000:F1}K";
        return $"{v:N0}";
    }
}
