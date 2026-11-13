using System.Globalization;
using System.Text;
using Avalonia;
using EveConsole.Localization;
using EveConsole.Services;

namespace EveConsole.ViewModels;

/// <summary>What the By period table splits each period into, under its period row.</summary>
public enum SalesSplit { None, Tag, MarketGroup, Item, Buyer, Owner }

/// <summary>What the for-profit share is a share of.</summary>
public enum ShareBasis { Cost, SaleIsk, Units }

public sealed record SalesSplitOption(string Label, SalesSplit Split) { public override string ToString() => Label; }
public sealed record ShareBasisOption(string Label, ShareBasis Basis) { public override string ToString() => Label; }

/// <summary>The By period tab's figures for the whole range, as the cards above its table show them.</summary>
public sealed record PeriodKpis(string Sales, string SalesSub, string ForProfit, string ForProfitSub,
                                string NotForProfit, string NotForProfitSub, string Share, string ShareSub)
{
    public static readonly PeriodKpis Empty = new("—", "", "—", "", "—", "", "—", "");
}

/// <summary>A market group the Sales Tracker is narrowed to.</summary>
public sealed record ItemGroupFilter(int MarketGroupId, string Name);

/// <summary>
/// One sale as the By period summary reads it.
///
/// <para>⚠️ The not-for-profit mark is the only thing here with a meaning: it decides which side of
/// every figure a sale lands on. The split keys are just text — a tag, a buyer's name — and are
/// counted, never interpreted.</para>
/// </summary>
internal readonly record struct PeriodSale(DateTime Day, bool NotForProfit, double Sale, double? Cost, IReadOnlyList<string> Keys, long Units = 1);

/// <summary>Units of one key (a market group, an item…) in one sale.</summary>
internal readonly record struct PeriodUnits(DateTime Day, bool NotForProfit, IReadOnlyList<(string Key, long Units)> Units);

/// <summary>The sums for one period, or one split of it, on both sides of the for-profit mark.</summary>
internal sealed class PeriodFigures
{
    public int    Count, FpCount, NfpCount, Uncosted;
    /// <summary>Units sold: what the screen counts. A sale of three hulls is three.</summary>
    public long   Units, FpUnits, NfpUnits;
    public double Sales, FpSales, NfpSales;
    /// <summary>Cost, and the sale ISK of the same sales: only sales with a cost basis. Profit is
    /// the one less the other, so a sale with no cost cannot add a profit that never existed.</summary>
    public double FpCost, FpCostedSales, NfpCost, NfpCostedSales;

    public double Cost => FpCost + NfpCost;

    public void Add(PeriodSale s)
    {
        Count++;
        Units += s.Units;
        Sales += s.Sale;
        if (s.Cost is null) Uncosted++;
        if (s.NotForProfit)
        {
            NfpCount++; NfpUnits += s.Units; NfpSales += s.Sale;
            if (s.Cost is double c) { NfpCost += c; NfpCostedSales += s.Sale; }
        }
        else
        {
            FpCount++; FpUnits += s.Units; FpSales += s.Sale;
            if (s.Cost is double c) { FpCost += c; FpCostedSales += s.Sale; }
        }
    }

    /// <summary>Profit on the for-profit sales with a cost, or null when none had one.</summary>
    public double? FpProfit => FpCost > 0 || FpCostedSales > 0 ? FpCostedSales - FpCost : null;

    /// <summary>That profit over its cost, as the Detail grid's Profit % is.</summary>
    public double? FpProfitPct => FpCost > 0 ? (FpCostedSales - FpCost) / FpCost : null;

    /// <summary>The for-profit part of the whole, 0 to 1, or null when there is no whole.</summary>
    public double? Share(ShareBasis basis) => basis switch
    {
        ShareBasis.Cost    => Cost > 0 ? FpCost / Cost : null,
        ShareBasis.SaleIsk => Sales > 0 ? FpSales / Sales : null,
        _                  => Units > 0 ? (double)FpUnits / Units : null,
    };
}

internal sealed record PeriodSplit(string Key, PeriodFigures Figures);
internal sealed record PeriodGroup(DateTime Start, PeriodFigures Figures, List<PeriodSplit> Splits);

/// <summary>Units of one key across the periods: for profit and not, period by period.</summary>
internal sealed record UnitLine(string Key, long[] Fp, long[] Nfp)
{
    public long Total => Fp.Sum() + Nfp.Sum();
}

/// <summary>
/// The By period summary's arithmetic, kept apart from the screen so it can be checked on its own
/// (tools/SalesSummaryCheck).
/// </summary>
internal static class SalesPeriods
{
    /// <summary>
    /// Sums the sales by period, and each period by split key.
    ///
    /// <para>⚠️ A sale with two keys (two tags) is under both splits but once in its period:
    /// the splits of a period can add up to more than it, and the period is the true figure.</para>
    ///
    /// <para>Every period from the first sale's to the last is listed, an empty one with nothing
    /// in it, so a quiet month shows as one rather than vanishing from a month-over-month read.</para>
    /// </summary>
    public static List<PeriodGroup> Build(IEnumerable<PeriodSale> sales, Func<DateTime, DateTime> bucket,
                                          Func<DateTime, DateTime> step, string noKey)
    {
        var byPeriod = sales.GroupBy(s => bucket(s.Day)).ToDictionary(g => g.Key, g => g.ToList());
        var groups = new List<PeriodGroup>();
        if (byPeriod.Count == 0) return groups;

        for (var d = byPeriod.Keys.Min(); d <= byPeriod.Keys.Max(); d = step(d))
        {
            var total  = new PeriodFigures();
            var splits = new Dictionary<string, PeriodFigures>(StringComparer.Ordinal);
            foreach (var s in byPeriod.GetValueOrDefault(d) ?? [])
            {
                total.Add(s);
                var keys = s.Keys.Count > 0 ? s.Keys : [noKey];
                foreach (var k in keys.Distinct(StringComparer.Ordinal))
                {
                    if (!splits.TryGetValue(k, out var f)) splits[k] = f = new PeriodFigures();
                    f.Add(s);
                }
            }
            groups.Add(new PeriodGroup(d, total, Ordered(splits, noKey, f => f.Sales)
                .Select(kv => new PeriodSplit(kv.Key, kv.Value)).ToList()));
        }
        return groups;
    }

    /// <summary>The whole range as one set of figures: every sale once.</summary>
    public static PeriodFigures Total(IEnumerable<PeriodSale> sales)
    {
        var f = new PeriodFigures();
        foreach (var s in sales) f.Add(s);
        return f;
    }

    /// <summary>
    /// Units of each key, period by period: the rows of the units table, the largest first.
    /// <paramref name="periods"/> are the period starts the columns stand for.
    /// </summary>
    public static List<UnitLine> Units(IEnumerable<PeriodUnits> sales, IReadOnlyList<DateTime> periods,
                                       Func<DateTime, DateTime> bucket, string noKey)
    {
        var column = periods.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var lines  = new Dictionary<string, UnitLine>(StringComparer.Ordinal);
        foreach (var s in sales)
        {
            if (!column.TryGetValue(bucket(s.Day), out var col)) continue;
            foreach (var (key, units) in s.Units)
            {
                var k = key.Length > 0 ? key : noKey;
                if (!lines.TryGetValue(k, out var line))
                    lines[k] = line = new UnitLine(k, new long[periods.Count], new long[periods.Count]);
                (s.NotForProfit ? line.Nfp : line.Fp)[col] += units;
            }
        }
        return Ordered(lines, noKey, l => l.Total).Select(kv => kv.Value).ToList();
    }

    /// <summary>Largest first, then by name; the "none" key always last.</summary>
    private static IEnumerable<KeyValuePair<string, T>> Ordered<T>(Dictionary<string, T> items, string noKey, Func<T, double> size) =>
        items.OrderBy(kv => kv.Key == noKey ? 1 : 0)
             .ThenByDescending(kv => size(kv.Value))
             .ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase);

    // ── Words and numbers ────────────────────────────────────────────────────

    /// <summary>A period's name: the month for a month, the first day for a week or a day.</summary>
    public static string PeriodName(DateTime start, string grain, DateTime today)
    {
        var name = grain == "m" ? start.ToString("yyyy-MM", CultureInfo.InvariantCulture)
                                : start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = grain switch { "m" => start.AddMonths(1), "w" => start.AddDays(7), _ => start.AddDays(1) };
        return today >= start && today < end && grain != "d" ? $"{name} ({SalesText.PeriodToDate})" : name;
    }

    public static string Count(long n) => n == 0 ? "—" : n.ToString("N0");
    public static string Pct(double? v) => v is double d ? (d * 100).ToString("N1") + "%" : "—";
    public static string Isk(double v)  => MarketFmt.Isk(v);

    /// <summary>A units cell: the total, then for profit · not for profit.</summary>
    public static string UnitCell(long fp, long nfp) => fp + nfp == 0 ? "—" : $"{fp + nfp:N0} ({fp:N0}·{nfp:N0})";

    // ── Copy as text ─────────────────────────────────────────────────────────

    /// <summary>
    /// The summary as plain text in fixed columns, for a code block in a chat: both tables, the
    /// period table with its splits indented under each period.
    /// </summary>
    public static string Text(string title, string settings, IReadOnlyList<PeriodRowVm> rows,
                              IReadOnlyList<string> unitPeriods, IReadOnlyList<UnitRowVm> units, string unitsTitle)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine(settings);
        sb.AppendLine();

        // The screen's columns, in its order.
        string[] head =
        [
            SalesText.ColPeriod, SalesText.ColUnits, SalesText.ColSaleIsk, SalesText.ColFpShare,
            SalesText.ColForProfit, SalesText.ColSaleIsk, SalesText.ColCost, SalesText.ColProfit, SalesText.ColProfit2,
            SalesText.ColNotForProfit, SalesText.ColSaleIsk,
        ];
        var table = rows.Select(r => new[]
        {
            (r.IsSplit ? "  " : "") + r.Label, r.Units, r.SaleIsk, r.Share,
            r.FpUnits, r.FpSaleIsk, r.FpCost, r.FpProfit, r.FpProfitPct,
            r.NfpUnits, r.NfpSaleIsk,
        }).ToList();
        Columns(sb, head, table, bars: [4, 9]);   // before For profit, and before Not for profit

        if (units.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(unitsTitle);
            var uhead = new[] { "" }.Concat(unitPeriods).Append(SalesText.Total).ToArray();
            Columns(sb, uhead, units.Select(u => new[] { u.Key }.Concat(u.Cells).Append(u.Total).ToArray()).ToList(), bars: []);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The first column left-aligned, the rest right-aligned, a bar before each column
    /// in <paramref name="bars"/> (the start of a group).</summary>
    private static void Columns(StringBuilder sb, string[] head, List<string[]> rows, int[] bars)
    {
        var width = head.Select((h, i) => rows.Select(r => r[i].Length).Append(h.Length).Max()).ToArray();
        void Line(string[] cells)
        {
            var line = new StringBuilder(cells[0].PadRight(width[0]));
            for (var i = 1; i < cells.Length; i++)
                line.Append(bars.Contains(i) ? " │ " : "  ").Append(cells[i].PadLeft(width[i]));
            sb.AppendLine(line.ToString().TrimEnd());
        }
        Line(head);
        foreach (var r in rows) Line(r);
    }

    // ── CSV ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The period table as CSV, one line per period and per split, with raw numbers (ISK
    /// unrounded, shares as fractions) so a spreadsheet can work with them; then the units.
    /// </summary>
    public static string Csv(IReadOnlyList<PeriodRowVm> rows, IReadOnlyList<string> unitPeriods,
                             IReadOnlyList<UnitRowVm> units, string unitsTitle)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[]
        {
            SalesText.ColPeriod, SalesText.CsvSplit, SalesText.ColUnits, SalesText.ColSaleIsk, SalesText.ColCost,
            SalesText.ColFpShare, SalesText.ColForProfit, SalesText.CsvForProfitIsk, SalesText.CsvForProfitCost,
            SalesText.ColProfit, SalesText.ColProfit2, SalesText.ColNotForProfit, SalesText.CsvNotForProfitIsk,
            SalesText.CsvNotForProfitCost, SalesText.CsvNoCost,
        }.Select(Field)));
        foreach (var r in rows)
        {
            var f = r.Figures;
            sb.AppendLine(string.Join(",", new[]
            {
                Field(r.Period), Field(r.IsSplit ? r.Label : ""), f.Units.ToString(inv), Num(f.Sales), Num(f.Cost),
                f.Share(r.Basis) is double s ? s.ToString("0.####", inv) : "", f.FpUnits.ToString(inv), Num(f.FpSales), Num(f.FpCost),
                f.FpProfit is double p ? Num(p) : "", f.FpProfitPct is double pp ? pp.ToString("0.####", inv) : "",
                f.NfpUnits.ToString(inv), Num(f.NfpSales), Num(f.NfpCost), f.Uncosted.ToString(inv),
            }));
        }

        if (units.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Field(unitsTitle));
            sb.AppendLine(string.Join(",", new[] { Field(SalesText.CsvCountBy) }
                .Concat(unitPeriods.SelectMany(p => new[] { Field(p + " " + SalesText.ColForProfit), Field(p + " " + SalesText.ColNotForProfit) }))));
            foreach (var u in units)
                sb.AppendLine(string.Join(",", new[] { Field(u.Key) }
                    .Concat(u.Line.Fp.Zip(u.Line.Nfp).SelectMany(x => new[] { x.First.ToString(inv), x.Second.ToString(inv) }))));
        }
        return sb.ToString();

        static string Num(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>A CSV field, quoted when it holds a comma, a quote or a line break.</summary>
    private static string Field(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>One line of the By period table: a period, a split of one, or the total.</summary>
public sealed class PeriodRowVm
{
    internal PeriodFigures Figures { get; }
    internal ShareBasis    Basis   { get; }

    /// <summary>The period this line belongs to; a split repeats its period's.</summary>
    public string Period { get; }
    public string Label  { get; }
    public bool   IsPeriod { get; }
    public bool   IsSplit  { get; }
    public bool   IsTotal  { get; }
    public Thickness Indent => IsSplit ? new Thickness(16, 0, 0, 0) : default;

    public string Units    { get; }
    public string SaleIsk  { get; }
    public string Share    { get; }
    /// <summary>The share bar's width, out of <see cref="BarWidth"/>.</summary>
    public double ShareBar { get; }
    public const double BarWidth = 60;

    public string FpUnits     { get; }
    public string FpSaleIsk   { get; }
    public string FpCost      { get; }
    public string FpProfit    { get; }
    public string FpProfitPct { get; }
    public string NfpUnits    { get; }
    public string NfpSaleIsk  { get; }

    internal PeriodRowVm(string period, string label, PeriodFigures f, ShareBasis basis, bool isSplit = false, bool isTotal = false)
    {
        Figures = f; Basis = basis;
        Period = period; Label = label;
        IsSplit = isSplit; IsTotal = isTotal; IsPeriod = !isSplit && !isTotal;

        Units   = SalesPeriods.Count(f.Units);
        SaleIsk = f.Count == 0 ? "—" : SalesPeriods.Isk(f.Sales);
        var share = f.Share(basis);
        Share    = SalesPeriods.Pct(share);
        ShareBar = (share ?? 0) * BarWidth;

        FpUnits     = SalesPeriods.Count(f.FpUnits);
        FpSaleIsk   = f.FpCount == 0 ? "—" : SalesPeriods.Isk(f.FpSales);
        FpCost      = f.FpCount == 0 ? "—" : SalesPeriods.Isk(f.FpCost);
        FpProfit    = f.FpProfit is double p ? SalesPeriods.Isk(p) : "—";
        FpProfitPct = SalesPeriods.Pct(f.FpProfitPct);
        NfpUnits    = SalesPeriods.Count(f.NfpUnits);
        NfpSaleIsk  = f.NfpCount == 0 ? "—" : SalesPeriods.Isk(f.NfpSales);
    }
}

/// <summary>One line of the units table: a market group, an item…, its units period by period.</summary>
public sealed class UnitRowVm
{
    internal UnitLine Line { get; }
    public string Key { get; }
    /// <summary>One cell per period, in the columns' order.</summary>
    public IReadOnlyList<string> Cells { get; }
    public string Total { get; }

    internal UnitRowVm(UnitLine line)
    {
        Line  = line;
        Key   = line.Key;
        Cells = line.Fp.Zip(line.Nfp, SalesPeriods.UnitCell).ToList();
        Total = SalesPeriods.UnitCell(line.Fp.Sum(), line.Nfp.Sum());
    }
}
