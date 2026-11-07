using System.Globalization;
using EveConsole.Services;
using EveConsole.Services.Worklist;
using EveConsole.ViewModels;

DbEngine.Pin(DbBackend.Sqlite);

var checks = 0;
void Check(string name, string expected, IEnumerable<WorklistRowVm> rows)
{
    var actual = WorklistViewModel.FormatBuyList(rows);
    if (actual != expected)
        throw new Exception($"{name}: expected [{expected}], got [{actual}]");
    checks++;
}

WorklistRowVm Row(int typeId, string name, long quantity, long station = 1,
    WorklistKind kind = WorklistKind.Buy, string? tag = null, PrintKind print = PrintKind.None) =>
    new(new WorklistItem
    {
        Key = $"buy:{station}:{typeId}", Source = "check", Title = name,
        TypeId = typeId, TypeName = name, Quantity = quantity, LocationId = station,
        Kind = kind, TitleTag = tag, Print = print, Detail = "Required for production."
    }, 0);

var trit = Row(34, "Tritanium", 150_000);
var moreTrit = Row(34, "Tritanium", 42_000, station: 2);
var pyerite = Row(35, "Pyerite", 8_500);

Check("empty", "", []);
Check("combine stations and sort names",
    "Pyerite\t8500" + Environment.NewLine + "Tritanium\t192000", [trit, pyerite, moreTrit]);
Check("use only supplied filtered rows", "Tritanium\t150000", [trit]);
Check("skip non-purchases and invalid quantities", "", [
    Row(34, "Tritanium", 12, kind: WorklistKind.Haul),
    Row(34, "Tritanium", 12, kind: WorklistKind.Job),
    Row(34, "Tritanium", 0), Row(34, "Tritanium", -1),
    Row(0, "Unknown", 1), Row(34, " ", 1)
]);
Check("skip blueprint runs including reaction formulas", "", [
    Row(100, "Blueprint", 20, tag: "BPO/BPC"),
    Row(101, "Formula", 20, tag: "BPO/BPC"),
    Row(102, "Copy", 20, print: PrintKind.Copy)
]);
if (trit.Children.Count == 0) throw new Exception("Expanded-row fixture has no children");
Check("expanded details do not double count", "Tritanium\t150000", [trit, .. trit.Children]);
Check("64-bit totals", "Tritanium\t4294967294",
    [Row(34, "Tritanium", int.MaxValue), Row(34, "Tritanium", int.MaxValue)]);

var culture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
    Check("culture-independent multibuy text", "Tritanium\t150000", [trit]);
}
finally { CultureInfo.CurrentCulture = culture; }

Console.WriteLine($"PASS: {checks} buy-list checks");
