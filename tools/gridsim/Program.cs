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
    int Stuck, int Clamped, int BadEst, double MaxJump, double Ask, int Reverse)
{
    public int Faults => (Arrived ? 0 : 1) + (ExtentErr > 200 ? 1 : 0) + (Drift > 200 ? 1 : 0)
                       + (Stuck > 0 ? 1 : 0) + (Clamped > 0 ? 1 : 0) + (BadEst > 0 ? 1 : 0)
                       + (MaxJump > Ask * 1.5 + 1 ? 1 : 0) + (Reverse > 0 ? 1 : 0);

    public override string ToString() =>
        $"  {Label,-20} steps={Steps,-4} arrived={Arrived,-5} extentErr={ExtentErr,7:F0} " +
        $"drift={Drift,7:F0} stuck={Stuck,-3} clamp={Clamped,-3} badEst={BadEst,-3} jump={MaxJump,6:F0} rev={Reverse,-3} faults={Faults}";
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

    private sealed class Fix(DataGrid grid, List<Row> model)
    {
        public bool Pin       { get; init; }
        public bool Reconcile { get; init; }

        private readonly Dictionary<int, double> _heights = [];

        /// <summary>Heights seen once. A height is only trusted after two passes agree on it.</summary>
        private readonly Dictionary<int, double> _seen = [];
        private double _meanBody = 22, _meanDrawer;

        /// <summary>
        /// Our own offset, moved only by what was asked for.
        ///
        /// ⚠️ Not read back from the grid. Its _verticalOffset is overwritten by its own walk --
        /// reaching the first row sets it to NegVerticalOffset outright -- so a 50px ask that the
        /// walk mishandled comes back as a 111px move, and reading it means inheriting the error
        /// this exists to avoid.
        /// </summary>
        private double _offset = -1;

        public void OnRowLoading(DataGridRow row) { }

        /// <summary>What a scroll asked for, recorded before the grid gets to interpret it.</summary>
        public void Request(double pixels)
        {
            if (!Reconcile) return;
            if (_offset < 0) _offset = D(Peek(grid, "_verticalOffset"));

            var cells = D(Peek(grid, "CellsEstimatedHeight"));
            _offset = Math.Clamp(_offset + pixels, 0, Math.Max(0, Total() - cells));


        }

        /// <summary>Best height for a row: measured where it has been seen, averaged where not.</summary>
        private double Height(int index) =>
            _heights.TryGetValue(index, out var h)
                ? h
                : _meanBody + (index < model.Count && model[index].IsOpen ? _meanDrawer : 0);

        private double Total()
        {
            var total = 0.0;
            for (var i = 0; i < model.Count; i++) total += Height(i);
            return total;
        }

        /// <summary>
        /// Scrolls by a number of pixels, positioning the view directly.
        ///
        /// ⚠️ This replaces Avalonia's own conversion from pixels to a position rather than
        /// correcting it afterwards. That conversion walks rows and measures each one on the way
        /// past, and a row measured while it is OFF screen reports the height of whichever row was
        /// recycled into it -- slot 3 read 1231, row 7's height, when it is 842. Correcting the
        /// landing after the fact leaves a visible two-step wobble as the error damps out; not
        /// making it in the first place leaves nothing to damp.
        /// </summary>
        public void ScrollBy(double pixels)
        {
            var cells = D(Peek(grid, "CellsEstimatedHeight"));
            var max   = Math.Max(0, Total() - cells);
            var at    = Math.Clamp(D(Peek(grid, "_verticalOffset")) + pixels, 0, max);

            Poke(grid, "_verticalOffset", at);
            Place(at);
        }

        /// <summary>Puts the view where a given offset says it should be.</summary>
        private void Place(double target)
        {
            // ⚠️ Clamped, and written back. The grid accumulates the offset from whatever each scroll
            // asked for and lets it run past the end -- it sat 43px beyond the maximum after a drag
            // to the bottom, which is a scrollbar value that disagrees with the offset for ever.
            var cells0 = D(Peek(grid, "CellsEstimatedHeight"));
            var max0   = Math.Max(0, Total() - cells0);

            target  = Math.Clamp(target, 0, max0);
            _offset = target;
            if (Math.Abs(D(Peek(grid, "_verticalOffset")) - target) > 0.5)
                Poke(grid, "_verticalOffset", target);

            var slot = 0;
            var acc  = 0.0;
            while (slot < model.Count - 1 && acc + Height(slot) <= target) { acc += Height(slot); slot++; }

            var neg     = Math.Max(0, target - acc);
            var display = Peek(grid, "DisplayData")!;

            Poke(grid, "NegVerticalOffset", neg);

            // ⚠️ Only when the FIRST ROW changes, and defensively. UpdateDisplayedRows expects to be
            // called from inside a layout pass; asking it to rebuild the displayed set from outside
            // one can leave DisplayData inconsistent with its own list. A part-scroll within the
            // same row needs no rebuild anyway -- the arrange picks it up from NegVerticalOffset.
            if (I(Peek(display, "FirstScrollingSlot")) != slot)
            {
                try
                {
                    grid.GetType().GetMethod("UpdateDisplayedRows", Any)!
                        .Invoke(grid, [slot, D(Peek(grid, "CellsEstimatedHeight"))]);
                }
                catch { /* the layout pass will sort it out */ }
            }

            Bar(target);
        }

        /// <summary>The extent, summed from measured heights rather than multiplied out from two
        /// scalars over rows the grid has never seen.</summary>
        private void Bar(double at)
        {
            if (Peek(grid, "_vScrollBar") is not { } bar) return;

            var cells = D(Peek(grid, "CellsEstimatedHeight"));
            var max   = Math.Max(0, Total() - cells);

            Poke(bar, "Maximum", max);
            Poke(bar, "ViewportSize", cells);
            Poke(bar, "Value", Math.Clamp(at, 0, max));
        }

        public void Apply()
        {
            var bodies  = new List<double>();
            var drawers = new List<double>();

            foreach (var row in grid.GetVisualDescendants().OfType<DataGridRow>())
            {
                if (row.DataContext is not Row || row.Bounds.Height <= 1 || row.Index < 0) continue;

                var presenter = row.AreDetailsVisible
                    ? row.GetVisualDescendants().OfType<DataGridDetailsPresenter>().FirstOrDefault()
                    : null;

                var drawer = presenter is { Bounds.Height: > 1 } ? presenter.Bounds.Height : 0;
                if (drawer > 0) drawers.Add(drawer);

                var body = row.Bounds.Height - drawer;
                if (body > 1) bodies.Add(body);

                // ⚠️ A height is only recorded once TWO passes agree on it, and only while the row is
                // settled -- its arranged bounds and its measured size matching. A recycled row can
                // be consistently wrong for a pass, reporting the height of whichever row was in it
                // before, and one bad sample poisons the table for good: row 0 went into it at 1231,
                // row 7 height, against its real 1292, and every later step across that boundary
                // moved 111px for a 50px ask.
                if (Math.Abs(row.DesiredSize.Height - row.Bounds.Height) >= 0.5) continue;

                var h = row.Bounds.Height;
                if (_seen.TryGetValue(row.Index, out var once) && Math.Abs(once - h) < 0.5)
                    _heights[row.Index] = h;

                _seen[row.Index] = h;
            }

            if (bodies.Count == 0) return;

            _meanBody   = bodies.Average();
            _meanDrawer = drawers.Count > 0 ? drawers.Average() : 0;

            if (Pin)
            {
                Poke(grid, "RowHeightEstimate", _meanBody);
                Poke(grid, "RowDetailsHeightEstimate", _meanDrawer);
                Poke(grid, "_lastEstimatedRow", int.MaxValue);
            }

            if (!Reconcile) return;

            var mine = _offset;
            var theirs = D(Peek(grid, "_verticalOffset"));

            // ⚠️ Ours only while the two are CLOSE. The grid loses a little of the offset whenever
            // its walk reaches the first row -- it assigns NegVerticalOffset outright -- so a 50px
            // ask came back as a 111px move. But it also relocates wholesale (snapping to the
            // bottom, resetting to the top), and overriding those would mean moving the displayed
            // set a long way from outside a layout pass, which corrupts DisplayData. So small
            // disagreements are ours to correct and large ones are the grid relocating: resync.
            var span = _meanBody + _meanDrawer;
            if (mine < 0 || Math.Abs(mine - theirs) > span) mine = theirs;

            Place(mine);
        }
    }

    // ── Driving and measuring ────────────────────────────────────────────────

    // ⚠️ Ends with Apply, not with a layout pass. The grid rewrites the scrollbar maximum from
    // its own arithmetic on every layout, so a pass that finishes with RunJobs leaves the wrong
    // figure standing.
    private static void Pump(Fix fix, int passes = 3)
    {
        for (var i = 0; i < passes; i++)
        {
            Dispatcher.UIThread.RunJobs();
            fix.Apply();
        }
    }

    /// <summary>
    /// The elements the grid is ACTUALLY displaying, read out of DisplayData rather than off the
    /// visual tree: recycled rows linger in the tree holding stale Index values, and reading those
    /// hides the very discrepancy being looked for. Desired size is what the scroll walk uses;
    /// bounds are what was drawn. They should agree.
    /// </summary>
    private static string Displayed(DataGrid grid)
    {
        var display = Peek(grid, "DisplayData")!;
        var first   = I(Peek(display, "FirstScrollingSlot"));
        var last    = I(Peek(display, "LastScrollingSlot"));
        if (first < 0 || last < first) return "";

        var get   = display.GetType().GetMethod("GetDisplayedElement", Any);
        var parts = new List<string>();

        for (var slot = first; slot <= last; slot++)
        {
            if (get?.Invoke(display, [slot]) is not Control c) continue;

            var open  = c is DataGridRow { AreDetailsVisible: true } ? "*" : "";
            var want  = c.DesiredSize.Height;
            var have  = c.Bounds.Height;
            var flag  = Math.Abs(want - have) > 1 ? "!" : "";
            parts.Add($"{slot}{open}:{want:F0}/{have:F0}{flag}");
        }
        return string.Join(" ", parts);
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
            Displayed(grid));
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
        var grid  = BuildGrid(rows, false);
        var fix   = new Fix(grid, rows) { Pin = pin, Reconcile = stale };
        grid.LoadingRow += (_, e) => fix.OnRowLoading(e.Row);

        var window = new Window { Width = 900, Height = Viewport, Content = grid };
        window.Show();
        Pump(fix, 8);

        // Down to the bottom the way a person gets there.
        for (var i = 0; i < 500; i++) { Wheel(grid, -step); fix.Request(step); Pump(fix, 2); }

        var steps = new List<Step> { Read(grid, rows) };

        for (var i = 0; i < 500; i++)
        {
            if (mode == "wheel") Wheel(grid, step); else Drag(grid, -step);
            fix.Request(-step);
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

        // A step that moves the view the WRONG WAY is what a person sees as the flicker.
        var jump    = 0.0;
        var reverse = 0;
        for (var i = 1; i < steps.Count - 1; i++)
        {
            var moved = steps[i].TrueAbove - steps[i - 1].TrueAbove;
            jump = Math.Max(jump, Math.Abs(moved));
            if (moved > 0.5) reverse++;
        }

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
            steps.Count(s => s.RowEst < 1),
            jump, step, reverse);
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
            foreach (var (label, pin, stale) in new[] { ("stock Avalonia", false, false), ("estimates pinned", true, false), ("pinned + reconciled", true, true), ("reconciled only", false, true) })
            {
                var r = Run(label, pin, stale, mode, step, trace);
                Console.WriteLine(r);

                // Only the candidate is graded. The other three are controls and are SUPPOSED to
                // fail -- they are what the fix is being measured against.
                if (pin && stale) faults += r.Faults;
            }
            Console.WriteLine();
        }


        Console.WriteLine(faults == 0
            ? "CLEAN - the candidate has no faults in any mode."
            : "FAULTS = " + faults + " in the candidate.");
        return faults == 0 ? 0 : 1;
    }
}
