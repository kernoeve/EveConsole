using System.Text;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Services;

/// <summary>
/// An Industry Ops list as a block of text for a scheduled post: the columns asked for, sorted,
/// the top rows only.
/// </summary>
public static class IndustryOppsReport
{
    /// <summary>
    /// One column the section can print: its stored key, its header, the cell, the number it
    /// sorts by (null for the item name, which sorts by text), and whether it lines up right.
    ///
    /// <para>⚠️ The headers are the Industry Ops grid's own, and the cells its own display
    /// strings, so a post reads exactly as the screen does.</para>
    /// </summary>
    public sealed record Column(string Key, string Header, Func<IndustryRow, string> Cell,
                                Func<IndustryRow, double>? Number, bool Right);

    public const string Item = "item";

    /// <summary>Every column, in the grid's order. The keys are what a section stores.</summary>
    public static IReadOnlyList<Column> Columns { get; } =
    [
        new(Item,              IndustryText.ColItem,          r => r.TypeName,                null,                   false),
        new("build_cost",      IndustryText.ColBuildCost,     r => r.BuildCostDisplay,        r => r.BuildCost,       true),
        new("sell_price",      IndustryText.ColSellPrice,     r => r.SellPriceDisplay,        r => r.SellPrice,       true),
        new("profit_unit",     IndustryText.ColProfitUnit,    r => r.ProfitUnitDisplay,       r => r.ProfitPerUnit,   true),
        new("margin",          IndustryText.ColMargin,        r => r.MarginDisplay,           r => r.Margin,          true),
        new("build_time",      IndustryText.ColBuildTime,     r => r.BuildTimeDisplay,        r => r.BuildSeconds,    true),
        new("slot_days",       IndustryText.ColSlotDays,      r => r.SlotDaysDisplay,         r => r.SlotDays,        true),
        new("profit_slot_day", IndustryText.ColProfitSlotDay, r => r.ProfitPerSlotDayDisplay, r => r.ProfitPerSlotDay, true),
        new("units_30d",       IndustryText.ColUnitsSold30d,  r => r.UnitVol30dDisplay,       r => r.UnitVol30d,      true),
        new("avg_price_30d",   IndustryText.ColAvgPrice30d,   r => r.AvgPrice30dDisplay,      r => r.AvgPrice30d,     true),
        new("margin_30d",      IndustryText.ColMargin30d,     r => r.Margin30dDisplay,        r => r.Margin30d,       true),
        new("sales_30d",       IndustryText.ColIskSold30d,    r => r.IskVol30dDisplay,        r => r.IskVol30d,       true),
        new("profit_30d",      IndustryText.ColProfit30d,     r => r.Profit30dDisplay,        r => r.Profit30d,       true),
    ];

    /// <summary>What a new section prints: what it is, what it makes, and whether it sells.</summary>
    public static readonly string[] DefaultColumns = [Item, "profit_unit", "margin", "units_30d", "sales_30d"];

    /// <summary>What a new section sorts by: the tool's own default.</summary>
    public const string DefaultSort = "sales_30d";

    /// <summary>
    /// The rows sorted by the column asked for, the first <paramref name="limit"/> of them
    /// (0: all). A row with no figure for the column — a 30-day margin with no sales — sorts
    /// last either way: it is not the lowest, it is unknown.
    /// </summary>
    public static List<IndustryRow> Top(IEnumerable<IndustryRow> rows, string sortKey, bool descending, int limit)
    {
        var col = Columns.FirstOrDefault(c => c.Key == sortKey) ?? Columns.First(c => c.Key == DefaultSort);

        IOrderedEnumerable<IndustryRow> sorted;
        if (col.Number is { } n)
        {
            var known = rows.OrderBy(r => double.IsNaN(n(r)));
            sorted = descending ? known.ThenByDescending(n) : known.ThenBy(n);
        }
        else
            sorted = descending
                ? rows.OrderByDescending(r => r.TypeName, StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderBy(r => r.TypeName, StringComparer.CurrentCultureIgnoreCase);

        // Ties broken by the item name, so the same data always posts the same list.
        var list = sorted.ThenBy(r => r.TypeName, StringComparer.CurrentCultureIgnoreCase).ToList();
        return limit > 0 ? [.. list.Take(limit)] : list;
    }

    /// <summary>
    /// The table. The item always comes first, then the columns asked for in the grid's order.
    /// Nothing at all for no rows.
    /// </summary>
    public static string Export(
        IReadOnlyList<IndustryRow> rows, IEnumerable<string> columnKeys, string heading,
        bool showHeaders, string formatName = "Slack")
    {
        if (rows.Count == 0) return "";

        var want = columnKeys.ToHashSet();
        var cols = Columns.Where(c => c.Key == Item || want.Contains(c.Key)).ToList();

        var headers = cols.Select(c => c.Header).ToArray();
        var aligns  = cols.Select(c => c.Right).ToArray();
        var cells   = rows.Select(r => cols.Select(c => c.Cell(r)).ToArray()).ToList();

        var widths = new int[cols.Count];
        if (showHeaders)
            for (var i = 0; i < cols.Count; i++) widths[i] = MonoColumns.Width(headers[i]);
        foreach (var c in cells)
            for (var i = 0; i < cols.Count; i++)
                widths[i] = Math.Max(widths[i], MonoColumns.Width(c[i]));

        var sb = new StringBuilder();

        // Blank is a choice somebody made, as it is for a standing projects section: no line.
        if (heading.Trim().Length > 0) sb.AppendLine(OutputFormat.ByName(formatName).Bold(heading.Trim()));

        sb.AppendLine("```");
        if (showHeaders)
        {
            sb.AppendLine(StandingProjectReport.Row(headers, widths, aligns));
            sb.AppendLine(StandingProjectReport.Row([.. widths.Select(w => new string('-', w))], widths, aligns));
        }
        foreach (var c in cells) sb.AppendLine(StandingProjectReport.Row(c, widths, aligns));
        sb.AppendLine("```");

        // The grid's "*" — priced from 30-day history, there being no sell orders — said once.
        if (rows.Any(r => !r.HasSellOrders) && cols.Any(c => c.Key == "sell_price"))
            sb.AppendLine(IndustryText.OppsReportHistoryPriced);

        return sb.ToString().TrimEnd();
    }
}
