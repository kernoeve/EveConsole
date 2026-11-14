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
    /// <para>The headers are the Industry Ops grid's own unless renamed. ISK is shortened one step
    /// further than the grid does it, to K under a million (<see cref="Isk"/>): a post is read
    /// at a glance, and a column of "17,880.00" is wider than it is useful.</para>
    /// </summary>
    public sealed record Column(string Key, string Header, Func<IndustryRow, string> Cell,
                                Func<IndustryRow, double>? Number, bool Right);

    public const string Item = "item";

    /// <summary>Every column, in the grid's order. The keys are what a section stores.</summary>
    public static IReadOnlyList<Column> Columns { get; } =
    [
        new(Item,              IndustryText.ColItem,          r => r.TypeName,                null,                   false),
        new("build_cost",      IndustryText.ColBuildCost,     r => Isk(r.BuildCost),          r => r.BuildCost,       true),
        // "*": priced from 30-day history, there being no sell orders, as the grid marks it.
        new("sell_price",      IndustryText.ColSellPrice,     r => Isk(r.SellPrice) + (r.HasSellOrders ? "" : " *"), r => r.SellPrice, true),
        new("profit_unit",     IndustryText.ColProfitUnit,    r => Isk(r.ProfitPerUnit),      r => r.ProfitPerUnit,   true),
        new("margin",          IndustryText.ColMargin,        r => r.MarginDisplay,           r => r.Margin,          true),
        new("build_time",      IndustryText.ColBuildTime,     r => r.BuildTimeDisplay,        r => r.BuildSeconds,    true),
        new("slot_days",       IndustryText.ColSlotDays,      r => r.SlotDaysDisplay,         r => r.SlotDays,        true),
        new("profit_slot_day", IndustryText.ColProfitSlotDay, r => Isk(r.ProfitPerSlotDay),   r => r.ProfitPerSlotDay, true),
        new("units_30d",       IndustryText.ColUnitsSold30d,  r => r.UnitVol30dDisplay,       r => r.UnitVol30d,      true),
        new("avg_price_30d",   IndustryText.ColAvgPrice30d,   r => r.HasSales30d ? Isk(r.AvgPrice30d) : "–", r => r.AvgPrice30d, true),
        new("margin_30d",      IndustryText.ColMargin30d,     r => r.Margin30dDisplay,        r => r.Margin30d,       true),
        new("sales_30d",       IndustryText.ColIskSold30d,    r => Isk(r.IskVol30d),          r => r.IskVol30d,       true),
        new("profit_30d",      IndustryText.ColProfit30d,     r => r.HasSales30d ? Isk(r.Profit30d) : "–", r => r.Profit30d, true),
    ];

    /// <summary>ISK as T, B, M or K with two decimals, and as it is under a thousand.</summary>
    public static string Isk(double v)
    {
        var abs  = Math.Abs(v);
        var sign = v < 0 ? "-" : "";
        return abs switch
        {
            >= 1_000_000_000_000 => $"{sign}{abs / 1_000_000_000_000:N2}T",
            >= 1_000_000_000     => $"{sign}{abs / 1_000_000_000:N2}B",
            >= 1_000_000         => $"{sign}{abs / 1_000_000:N2}M",
            >= 1_000             => $"{sign}{abs / 1_000:N2}K",
            _                    => $"{sign}{abs:N2}",
        };
    }

    /// <summary>What a new section prints: what it is, what it makes, and whether it sells.</summary>
    public static readonly string[] DefaultColumns = [Item, "profit_unit", "margin", "units_30d", "sales_30d"];

    /// <summary>What a new section sorts by: the tool's own default.</summary>
    public const string DefaultSort = "profit_30d";

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

    /// <summary>The item, then the columns asked for in the order given.</summary>
    private static List<Column> Chosen(IEnumerable<string> columnKeys)
    {
        var byKey = Columns.ToDictionary(c => c.Key);
        var cols  = new List<Column> { byKey[Item] };
        foreach (var key in columnKeys.Distinct())
            if (key != Item && byKey.TryGetValue(key, out var c)) cols.Add(c);
        return cols;
    }

    private static string HeaderOf(Column c, IReadOnlyDictionary<string, string>? headerNames) =>
        headerNames?.GetValueOrDefault(c.Key) is { Length: > 0 } named ? named : c.Header;

    /// <summary>Whether the rows lean on the note about the grid's "*": priced from 30-day history.</summary>
    public static bool NeedsHistoryNote(IReadOnlyList<IndustryRow> rows, IEnumerable<string> columnKeys) =>
        rows.Any(r => !r.HasSellOrders) && columnKeys.Contains("sell_price");

    /// <summary>How number 1 names its 30-day figures, in full: its numbers stand alone, without a
    /// column of them to read them against. The rest use the grid's own headers.</summary>
    private static string TopLabel(Column c) => c.Key switch
    {
        "units_30d"     => AlarmsText.OppsTopUnits,
        "avg_price_30d" => AlarmsText.OppsTopAvg,
        "margin_30d"    => AlarmsText.OppsTopMargin,
        "sales_30d"     => AlarmsText.OppsTopSales,
        "profit_30d"    => AlarmsText.OppsTopProfit,
        _               => c.Header,
    };

    private static bool IsThirtyDay(Column c) => c.Key.EndsWith("_30d", StringComparison.Ordinal);

    /// <summary>A row with figures about as wide as real ones get, for judging a section's columns
    /// when it is set up, with no rows to measure.</summary>
    private static readonly IndustryRow TypicalRow = new()
    {
        BuildCost = 999_990_000, SellPrice = 999_990_000, HasSellOrders = true, ProfitPerUnit = 999_990_000,
        Margin = 0.999, BuildSeconds = 8_280, SlotDays = 0.1, ProfitPerSlotDay = 999_990_000,
        UnitVol30d = 1_000, IskVol30d = 1_900_000_000_000,
    };

    /// <summary>The item, then the columns in order for as long as they fit Slack's box.</summary>
    private static List<Column> FittingSlackBox(
        List<Column> cols, IReadOnlyCollection<IndustryRow> rows, bool showHeaders, IReadOnlyDictionary<string, string>? headerNames)
    {
        var kept   = new List<Column> { cols[0] };
        var widths = new List<int>();
        foreach (var c in cols.Skip(1))
        {
            widths.Add(rows.Select(r => SlackColumns.Width(c.Cell(r)))
                           .Append(showHeaders ? SlackColumns.Width(HeaderOf(c, headerNames)) : 0).Max());
            if (!SlackRichBlocks.ItemColumnsFit(widths)) break;
            kept.Add(c);
        }
        return kept;
    }

    /// <summary>
    /// The columns asked for that fit Slack's box with these rows, those last in the order left
    /// out. Number 1's figures are not in a column under it, so its row does not count.
    /// </summary>
    public static List<string> SlackBoxKeys(
        IReadOnlyList<IndustryRow> rows, IEnumerable<string> columnKeys, bool showHeaders,
        IReadOnlyDictionary<string, string>? headerNames = null) =>
        [.. FittingSlackBox(Chosen(columnKeys), [.. rows.Skip(1)], showHeaders, headerNames).Skip(1).Select(c => c.Key)];

    /// <summary>The same for a section being set up, its figures not known yet: what the editor offers.</summary>
    public static List<string> SlackBoxKeys(
        IEnumerable<string> columnKeys, bool showHeaders, IReadOnlyDictionary<string, string>? headerNames = null) =>
        [.. FittingSlackBox(Chosen(columnKeys), [TypicalRow], showHeaders, headerNames).Skip(1).Select(c => c.Key)];

    /// <summary>
    /// The rows as a list for Slack's boxes, with number 1's figures picked out of them.
    ///
    /// <para>⚠️ Number 1's figures go in the grid's order, whatever order the section's columns are
    /// in, and those over 30 days go together under a line that says so: on their own, nothing
    /// else tells a 30-day margin from today's.</para>
    ///
    /// <para>Only the columns that fit the box, the editor having offered no more than should: real
    /// figures can still come out wider than it judged. A column left out goes from number 1's
    /// figures too.</para>
    /// </summary>
    public static SlackItemList SlackList(
        IReadOnlyList<IndustryRow> rows, IEnumerable<string> columnKeys, string title, string sortKey,
        bool showHeaders, string picture, IReadOnlyDictionary<string, string>? headerNames = null)
    {
        var cols = Chosen(SlackBoxKeys(rows, columnKeys, showHeaders, headerNames));
        var top  = rows.Count > 0 ? rows[0] : null;

        List<SlackTopFact> Facts(bool thirtyDay) => top is null ? [] :
        [
            .. Columns.Where(c => c.Key != Item && cols.Contains(c) && IsThirtyDay(c) == thirtyDay)
                      .Select(c => new SlackTopFact(TopLabel(c), c.Cell(top), c.Key == sortKey)),
        ];

        return new SlackItemList(
            title, AlarmsText.OppsTopItem, picture,
            Facts(false), AlarmsText.OppsTopPeriod, Facts(true), AlarmsText.RichFactLine,
            showHeaders,
            [.. cols.Select(c => new SlackRichColumn(HeaderOf(c, headerNames), c.Right))],
            [.. rows.Select(r => new SlackItemRow(
                $"https://images.evetech.net/types/{r.TypeId}/icon?size=64",
                [.. cols.Select(c => c.Cell(r))]))]);
    }

    /// <summary>
    /// The table. The item always comes first, then the columns asked for in the order given,
    /// each under its renamed header if it has one. Nothing at all for no rows.
    /// </summary>
    public static string Export(
        IReadOnlyList<IndustryRow> rows, IEnumerable<string> columnKeys, string heading,
        bool showHeaders, string formatName = "Slack", IReadOnlyDictionary<string, string>? headerNames = null)
    {
        if (rows.Count == 0) return "";

        var cols    = Chosen(columnKeys);
        var headers = cols.Select(c => HeaderOf(c, headerNames)).ToArray();
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
