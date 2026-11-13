// Checks the Sales Tracker's By period summary and which trades count as sales at all: periods
// and their splits, the for-profit and not-for-profit sides, the share on each basis, the units
// table, the text copy, and a trade between the user's own wallets left out. Constructed sales
// only: no database. A wrong answer here pays a builder on a for-profit share that never was.
using System.Globalization;
using EveConsole.Localization;
using EveConsole.ViewModels;

CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

var failures = 0;
var passes = 0;

void Check(bool ok, string what)
{
    if (ok) passes++;
    else { failures++; Console.WriteLine("FAILED: " + what); }
}

void Same<T>(T actual, T wanted, string what) => Check(Equals(actual, wanted), $"{what}: got {actual}, wanted {wanted}");
void Near(double? actual, double wanted, string what) =>
    Check(actual is double a && Math.Abs(a - wanted) < 1e-9, $"{what}: got {actual}, wanted {wanted}");

static DateTime Month(DateTime d) => new(d.Year, d.Month, 1);
static DateTime Next(DateTime d)  => d.AddMonths(1);

var aug = new DateTime(2026, 8, 4);
var sep = new DateTime(2026, 9, 12);
var nov = new DateTime(2026, 11, 2);

// ---- Periods and splits ----
{
    PeriodSale Sale(DateTime day, bool nfp, double sale, double? cost, params string[] tags) => new(day, nfp, sale, cost, tags);
    var sales = new List<PeriodSale>
    {
        Sale(aug, false, 110, 100, "Alpha"),
        Sale(aug, true,   50,  49, "Alpha", "Beta"),   // two tags: under both, once in August
        Sale(aug, false,  30,  20),                    // no tag
        Sale(aug, false,  40, null),                   // no cost basis
        Sale(nov, true,   10,  10, "Beta"),
    };
    var groups = SalesPeriods.Build(sales, Month, Next, "No tag");

    Same(groups.Count, 4, "August to November, the two empty months listed");
    Same(groups[1].Figures.Count, 0, "September is there, and empty");

    var a = groups[0].Figures;
    Same(a.Count, 4, "August counts the two-tag sale once");
    Near(a.Sales, 230, "August's sale ISK, the uncosted sale too");
    Same(a.Uncosted, 1, "one August sale had no cost");
    Near(a.Cost, 169, "cost only from sales that have one");
    Same(a.FpCount, 3, "three for profit");
    Same(a.NfpCount, 1, "one not for profit");
    Near(a.FpProfit, 20, "profit from the costed for-profit sales only: 110-100 + 30-20");
    Near(a.FpProfitPct, 20.0 / 120, "profit over its cost");
    Near(a.Share(ShareBasis.Cost), 120.0 / 169, "share on cost");
    Near(a.Share(ShareBasis.SaleIsk), 180.0 / 230, "share on sale ISK");
    Near(a.Share(ShareBasis.Units), 3.0 / 4, "share on units, one each");

    var keys = groups[0].Splits.Select(s => s.Key).ToList();
    Same(string.Join("|", keys), "Alpha|Beta|No tag", "largest first, no tag last");
    var alpha = groups[0].Splits.Single(s => s.Key == "Alpha").Figures;
    var beta  = groups[0].Splits.Single(s => s.Key == "Beta").Figures;
    Same(alpha.Count, 2, "the two-tag sale is under Alpha");
    Same(beta.Count, 1, "and under Beta");
    Check(groups[0].Splits.Sum(s => s.Figures.Count) > a.Count, "splits may add to more than their period");

    var total = SalesPeriods.Total(sales);
    Same(total.Count, 5, "the total counts every sale once");
    Same(total.NfpCount, 2, "and both not-for-profit ones");

    var none = new PeriodFigures();
    Check(none.Share(ShareBasis.Cost) is null, "no share of nothing");
    Check(none.FpProfit is null && none.FpProfitPct is null, "no profit without a costed sale");
}

// ---- Units, not sales ----
{
    // One contract of three dreads and one of a single carrier: four units, two sales.
    var f = SalesPeriods.Total(
    [
        new PeriodSale(sep, false, 9, 8, [], Units: 3),
        new PeriodSale(sep, true,  3, 3, [], Units: 1),
    ]);
    Same(f.Units, 4L, "a contract of three counts three");
    Same(f.FpUnits, 3L, "on its side");
    Same(f.NfpUnits, 1L, "and one on the other");
    Same(f.Count, 2, "still two sales underneath");
    Near(f.Share(ShareBasis.Units), 3.0 / 4, "the units share counts hulls, not contracts");
    var row = new PeriodRowVm("2026-09", "2026-09", f, ShareBasis.Units);
    Same(row.Units, "4", "the table shows units");
}

// ---- Period names ----
{
    var today = new DateTime(2026, 11, 10);
    Same(SalesPeriods.PeriodName(new DateTime(2026, 10, 1), "m", today), "2026-10", "a finished month");
    Same(SalesPeriods.PeriodName(new DateTime(2026, 11, 1), "m", today), "2026-11 (to date)", "the month under way");
    Same(SalesPeriods.PeriodName(new DateTime(2026, 11, 9), "w", today), "2026-11-09 (to date)", "the week under way, by its Monday");
    Same(SalesPeriods.PeriodName(today, "d", today), "2026-11-10", "a day is never part way");
}

// ---- Units ----
{
    var periods = new List<DateTime> { Month(aug), Month(sep) };
    var units = SalesPeriods.Units(
    [
        new PeriodUnits(aug, false, [("Carriers", 2), ("Dreadnoughts", 1)]),
        new PeriodUnits(aug, true,  [("Carriers", 1)]),
        new PeriodUnits(sep, false, [("Dreadnoughts", 3)]),
        new PeriodUnits(sep, true,  [("", 1)]),
        new PeriodUnits(nov, false, [("Titans", 9)]),   // outside the columns: not counted
    ], periods, Month, "—");

    Same(string.Join("|", units.Select(u => u.Key)), "Dreadnoughts|Carriers|—", "most units first, the unnamed last");
    var carriers = units.Single(u => u.Key == "Carriers");
    Same(carriers.Fp[0], 2L, "two carriers for profit in August");
    Same(carriers.Nfp[0], 1L, "one not for profit");
    Same(SalesPeriods.UnitCell(2, 1), "3 (2·1)", "a cell: the total, then each side");
    Same(SalesPeriods.UnitCell(0, 0), "—", "nothing sold is a dash");
    Check(units.All(u => u.Key != "Titans"), "a sale outside the periods shown is left out");
}

// ---- The text copy ----
{
    var f = SalesPeriods.Total([new PeriodSale(aug, false, 1_500_000_000, 1_000_000_000, [])]);
    var rows = new List<PeriodRowVm>
    {
        new("2026-08", "2026-08", f, ShareBasis.Cost),
        new("2026-08", "No tag", f, ShareBasis.Cost, isSplit: true),
        new(SalesText.Total, SalesText.Total, f, ShareBasis.Cost, isTotal: true),
    };
    var text  = SalesPeriods.Text("Title", "Settings", rows, [], [], "Units");
    var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
    Same(lines[0], "Title", "the title first");
    Check(lines.Any(l => l.StartsWith("  No tag")), "a split is indented under its period");
    var head = lines.First(l => l.StartsWith(SalesText.ColPeriod));
    var data = lines.First(l => l.StartsWith("2026-08"));
    Same(data.Length, head.Length, "the columns line up");
    Check(data.Contains("100.0%"), "the share in its column: " + data);
    Check(data.Contains("50.0%"), "profit over cost: " + data);

    var csv = SalesPeriods.Csv(rows, [], [], "Units").Split('\n');
    Check(csv[1].StartsWith("2026-08,,1,1500000000,1000000000,1,"), "the CSV carries raw numbers: " + csv[1]);
    Check(csv[2].StartsWith("2026-08,No tag,"), "a split names its period and itself: " + csv[2]);
}

// ---- Not a sale: the user paying themselves ----
{
    var ours = new HashSet<long> { 1, 2, 10 };   // two characters and a personal corporation
    Check(SalesQuery.NotASale(1, 10, ours), "a character selling to its own personal corporation");
    Check(SalesQuery.NotASale(10, 2, ours), "a personal corporation selling to another character of the user's");
    Check(SalesQuery.NotASale(50, 50, ours), "a corporation selling to itself");
    Check(!SalesQuery.NotASale(50, 1, ours), "a corporation that is not personal selling to the user is a sale");
    Check(!SalesQuery.NotASale(10, 50, ours), "and the other way round");
    Check(!SalesQuery.NotASale(1, 999, ours), "a stranger buying is a sale");
    Check(!SalesQuery.NotASale(1, 0, ours), "no buyer recorded is left as it was");
}

Console.WriteLine(failures == 0
    ? $"PASSED: sales summary check: all {passes} passed."
    : $"FAILED: sales summary check: {failures} failed, {passes} passed.");
return failures == 0 ? 0 : 1;
