using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GridSim;

/// <summary>One row: a fixed body, and a drawer of a known number of fixed-height lines.</summary>
public sealed class Row(int index, double body, int lines, bool open)
{
    public const double LineHeight = 18;

    public string       Key    { get; } = $"r{index}";
    public string       Name   { get; } = $"Row {index}";
    public double       Body   { get; } = body;
    public int          Lines  { get; } = lines;
    public bool         IsOpen { get; } = open;
    public List<string> Items  { get; } = [.. Enumerable.Range(0, lines).Select(i => $"line {i}")];

    /// <summary>What this row really occupies, drawer included when it is open.</summary>
    public double DrawerHeight => Lines * LineHeight;

    public double TrueHeight => Body + (IsOpen ? Lines * LineHeight : 0);
}

public sealed record Step(
    double Offset, double Neg, int First, double SbMax, double SbVal,
    double RowEst, double DetEst, double TrueAbove, string Rows);

public sealed record Result(
    string Label, int Steps, bool Arrived, double ExtentErr, double Drift,
    int Stuck, int Clamped, int BadEst)
{
    public int Faults => (Arrived ? 0 : 1) + (ExtentErr > 200 ? 1 : 0) + (Drift > 200 ? 1 : 0)
                       + (Stuck > 0 ? 1 : 0) + (Clamped > 0 ? 1 : 0) + (BadEst > 0 ? 1 : 0);

    public override string ToString() =>
        $"  {Label,-20} steps={Steps,-4} arrived={Arrived,-5} extentErr={ExtentErr,7:F0} " +
        $"drift={Drift,7:F0} stuck={Stuck,-4} clamped={Clamped,-4} badEst={BadEst,-4} faults={Faults}";
}

public class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://gridsim/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"),
        });
    }
}

public static class Program
{
    private const double Viewport = 1000;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // ── Reflection into the grid's own arithmetic ────────────────────────────

    private static object? Peek(object target, string name)
    {
        for (var t = target.GetType(); t is not null; t = t.BaseType)
        {
            if (t.GetProperty(name, Any) is { } p) return p.GetValue(target);
            if (t.GetField(name, Any) is { } f) return f.GetValue(target);
        }
        return null;
    }

    private static void Poke(object target, string name, object value)
    {
        for (var t = target.GetType(); t is not null; t = t.BaseType)
        {
            if (t.GetProperty(name, Any) is { CanWrite: true } p) { p.SetValue(target, value); return; }
            if (t.GetField(name, Any) is { } f) { f.SetValue(target, value); return; }
        }
        throw new MissingMemberException(target.GetType().Name + "." + name);
    }

    private static double D(object? v) => v is double d ? d : 0;
    private static int    I(object? v) => v is int i ? i : 0;

    // ── The model ────────────────────────────────────────────────────────────

    // Shaped from the live log: fifty rows, bodies 32-55, four open with drawers of 1260, 810,
    // 1188 and 774. The SPREAD is the whole point, so it is reproduced rather than averaged away.
    private static List<Row> BuildRows()
    {
        var open = new Dictionary<int, int> { [0] = 70, [3] = 45, [7] = 66, [10] = 43 };
        var rows = new List<Row>();

        for (var i = 0; i < 50; i++)
        {
            double body = i % 3 == 0 ? 32 : i % 7 == 1 ? 55 : 43;
            rows.Add(new Row(i, body, open.GetValueOrDefault(i, 0), open.ContainsKey(i)));
        }
        return rows;
    }

    private static DataGrid BuildGrid(List<Row> rows, bool fixedDrawer)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns      = false,
            IsReadOnly               = true,
            GridLinesVisibility      = DataGridGridLinesVisibility.None,
            HeadersVisibility        = DataGridHeadersVisibility.None,
            RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.Collapsed,
            AreRowDetailsFrozen      = false,
            ItemsSource              = rows,
        };

        grid.Columns.Add(new DataGridTemplateColumn
        {
            Width        = new DataGridLength(1, DataGridLengthUnitType.Star),
            CellTemplate = new FuncDataTemplate<Row>((_, _) =>
            {
                var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                text[!TextBlock.TextProperty] = new Binding(nameof(Row.Name));

                var border = new Border { Child = text };
                border[!Layoutable.HeightProperty] = new Binding(nameof(Row.Body));
                return border;
            }),
        });

        // A drawer is N stacked lines, so its height is N * LineHeight exactly.
        grid.RowDetailsTemplate = new FuncDataTemplate<Row>((_, _) =>
        {
            var list = new ItemsControl
            {
                ItemTemplate = new FuncDataTemplate<string>((_, _) =>
                    new Border { Height = Row.LineHeight, Child = new TextBlock() }),
            };
            list[!ItemsControl.ItemsSourceProperty] = new Binding(nameof(Row.Items));
            if (fixedDrawer) list[!Layoutable.HeightProperty] = new Binding(nameof(Row.DrawerHeight));
            return list;
        });

        // Exactly what WorklistView does: the flag lives on the item, restored as rows realise.
        grid.LoadingRow += (_, e) =>
        {
            if (e.Row.DataContext is Row r && e.Row.AreDetailsVisible != r.IsOpen)
                e.Row.AreDetailsVisible = r.IsOpen;
        };

        return grid;
    }

    // ── The fix under test ───────────────────────────────────────────────────

    private sealed class Fix(DataGrid grid)
    {
        public bool Pin      { get; init; }
        public bool FixStale { get; init; }

        public static int Pushed;

        private readonly Dictionary<string, double> _drawers = [];
        private readonly HashSet<DataGridRow>       _hooked  = [];

        /// <summary>
        /// A recycled row reports the PREVIOUS item's drawer height until an ARRANGE pass has run:
        /// DataGridDetailsPresenter.MeasureOverride returns ContentHeight verbatim, and ContentHeight
        /// is only refreshed from DataGridRow.ArrangeOverride. The scroll walk measures rows
        /// synchronously, so it lands on a height belonging to a different row.
        /// </summary>
        public void OnRowLoading(DataGridRow row)
        {
            if (!FixStale) return;

            if (_hooked.Add(row)) row.DataContextChanged += (_, _) => Push(row);
            Push(row);
        }

        private void Push(DataGridRow row)
        {
            if (row.DataContext is not Row item) return;

            // The grid restores this in its own LoadingRow handler, which runs AFTER DataContextChanged,
            // so at this point the flag still belongs to the row that was here before.
            if (row.AreDetailsVisible != item.IsOpen) row.AreDetailsVisible = item.IsOpen;
            if (!item.IsOpen) return;

            var presenter = row.GetVisualDescendants().OfType<DataGridDetailsPresenter>().FirstOrDefault();
            if (presenter is null) return;

            double want;
            if (_drawers.TryGetValue(item.Key, out var known))
            {
                want = known;
            }
            else if (presenter.GetVisualChildren().OfType<Control>().FirstOrDefault() is { } content)
            {
                content.Measure(Size.Infinity);
                want = content.DesiredSize.Height;
            }
            else return;

            if (Math.Abs(presenter.ContentHeight - want) < 0.5) return;

            presenter.ContentHeight = want;

            // The scroll walk reads DesiredSize, which only a Measure refreshes -- InvalidateMeasure
            // merely schedules one, and the walk is synchronous.
            row.InvalidateMeasure();
            presenter.InvalidateMeasure();
            row.Measure(Size.Infinity);

            // ⚠️ The row caches the same figure privately and ArrangeOverride writes it back over
            // ContentHeight, so both have to move or the next arrange undoes this.
            Poke(row, "_detailsDesiredHeight", want);
            Pushed++;
        }

        public void Apply()
        {
            var bodies  = new List<double>();
            var drawers = new List<double>();

            foreach (var row in grid.GetVisualDescendants().OfType<DataGridRow>())
            {
                if (row.DataContext is not Row item || row.Bounds.Height <= 1) continue;

                var presenter = row.AreDetailsVisible
                    ? row.GetVisualDescendants().OfType<DataGridDetailsPresenter>().FirstOrDefault()
                    : null;

                var drawer = presenter is { Bounds.Height: > 1 } ? presenter.Bounds.Height : 0;
                if (drawer > 0) { drawers.Add(drawer); _drawers[item.Key] = drawer; }

                var body = row.Bounds.Height - drawer;
                if (body > 1) bodies.Add(body);
            }

            if (!Pin || bodies.Count == 0) return;

            Poke(grid, "RowHeightEstimate", bodies.Average());
            Poke(grid, "RowDetailsHeightEstimate", drawers.Count > 0 ? drawers.Average() : 0d);
            Poke(grid, "_lastEstimatedRow", int.MaxValue);
        }
    }

    // ── Driving and measuring ────────────────────────────────────────────────

    private static void Pump(Fix fix, int passes = 3)
    {
        for (var i = 0; i < passes; i++)
        {
            Dispatcher.UIThread.RunJobs();
            fix.Apply();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Step Read(DataGrid grid, List<Row> rows)
    {
        var display = Peek(grid, "DisplayData")!;
        var bar     = Peek(grid, "_vScrollBar");
        var first   = I(Peek(display, "FirstScrollingSlot"));
        var neg     = D(Peek(grid, "NegVerticalOffset"));

        var above = 0.0;
        for (var i = 0; i < first && i < rows.Count; i++) above += rows[i].TrueHeight;

        return new Step(
            D(Peek(grid, "_verticalOffset")), neg, first,
            bar is null ? 0 : D(Peek(bar, "Maximum")),
            bar is null ? 0 : D(Peek(bar, "Value")),
            D(Peek(grid, "RowHeightEstimate")), D(Peek(grid, "RowDetailsHeightEstimate")),
            above + neg,
            string.Join(" ", grid.GetVisualDescendants().OfType<DataGridRow>()
                .Where(r => r.IsVisible).OrderBy(r => r.Index)
                .Select(r => r.Index + (r.AreDetailsVisible ? "*" : "") + ":" + r.Bounds.Height.ToString("F0"))));
    }

    /// <summary>Positive scrolls up: UpdateScroll turns delta.Y into -delta.Y of offset.</summary>
    private static void Wheel(DataGrid grid, double pixels) =>
        typeof(DataGrid).GetMethod("UpdateScroll", Any)!.Invoke(grid, [new Vector(0, pixels)]);

    private static void Drag(DataGrid grid, double delta)
    {
        var bar = Peek(grid, "_vScrollBar");
        if (bar is null) return;

        var target = Math.Clamp(D(Peek(bar, "Value")) + delta, 0, D(Peek(bar, "Maximum")));
        Poke(bar, "Value", target);
        typeof(DataGrid).GetMethod("ProcessVerticalScroll", Any)!
            .Invoke(grid, [ScrollEventType.ThumbTrack]);
    }

    private static Result Run(string label, bool pin, bool stale, string mode, double step, bool trace)
    {
        var rows  = BuildRows();
        var truth = rows.Sum(r => r.TrueHeight);
        var grid  = BuildGrid(rows, stale);
        var fix   = new Fix(grid) { Pin = pin, FixStale = stale };
        grid.LoadingRow += (_, e) => fix.OnRowLoading(e.Row);

        var window = new Window { Width = 900, Height = Viewport, Content = grid };
        window.Show();
        Pump(fix, 8);

        // Down to the bottom the way a person gets there.
        for (var i = 0; i < 500; i++) { Wheel(grid, -step); Pump(fix, 2); }

        var steps = new List<Step> { Read(grid, rows) };

        for (var i = 0; i < 500; i++)
        {
            if (mode == "wheel") Wheel(grid, step); else Drag(grid, -step);
            Pump(fix, 2);

            var s = Read(grid, rows);
            steps.Add(s);
            if (s.First == 0 && s.Neg <= 0.5) break;
        }

        if (trace)
            foreach (var s in steps)
                Console.WriteLine($"      off={s.Offset,8:F0} neg={s.Neg,7:F0} first={s.First,3} " +
                                  $"true={s.TrueAbove,8:F0} sbMax={s.SbMax,8:F0} sbVal={s.SbVal,8:F0} " +
                                  $"rowEst={s.RowEst,7:F1} detEst={s.DetEst,7:F1} | {s.Rows}");

        var stuck = 0;
        for (var i = 1; i < steps.Count; i++)
            if (Math.Abs(steps[i].TrueAbove - steps[i - 1].TrueAbove) < 1 &&
                Math.Abs(steps[i].Offset - steps[i - 1].Offset) > 1) stuck++;

        window.Close();

        return new Result(
            label, steps.Count,
            steps[^1].First == 0 && steps[^1].Neg <= 0.5,
            steps.Max(s => Math.Abs(s.SbMax + Viewport - truth)),
            steps.Max(s => Math.Abs(s.Offset - s.TrueAbove)),
            stuck,
            steps.Count(s => Math.Abs(s.SbVal - s.Offset) > 1),
            steps.Count(s => s.RowEst < 1));
    }

    public static int Main(string[] args)
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
            .SetupWithoutStarting();

        var trace = args.Contains("--trace");
        var truth = BuildRows().Sum(r => r.TrueHeight);

        Console.WriteLine($"true content = {truth:F0}px over 50 rows (4 open), viewport {Viewport:F0}px");
        Console.WriteLine($"so a correct scrollbar maximum is {truth - Viewport:F0}");
        Console.WriteLine();

        var faults = 0;
        foreach (var (mode, step) in new[] { ("wheel", 50.0), ("drag", 50.0), ("drag", 600.0) })
        {
            Console.WriteLine($"{mode} {step:F0}px:");
            foreach (var (label, pin, stale) in new[] { ("stock Avalonia", false, false), ("estimates pinned", true, false), ("pinned + bound drawer", true, true), ("bound drawer only", false, true) })
            {
                var r = Run(label, pin, stale, mode, step, trace);
                Console.WriteLine(r);
                faults += r.Faults;
            }
            Console.WriteLine();
        }

        Console.WriteLine("stale pushes = " + Fix.Pushed);
        Console.WriteLine("total faults = " + faults);
        return faults == 0 ? 0 : 1;
    }
}
