using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
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

    public string       Name   { get; } = $"Row {index}";
    public double       Body   { get; } = body;
    public int          Lines  { get; } = lines;
    public bool         IsOpen { get; } = open;
    public List<string> Items  { get; } = [.. Enumerable.Range(0, lines).Select(i => $"line {i}")];

    /// <summary>What this row really occupies, drawer included when it is open.</summary>
    public double TrueHeight => Body + (IsOpen ? Lines * LineHeight : 0);
}

public sealed record Step(
    double Offset, double Neg, int First, int Last, double SbMax, double SbVal,
    double RowEst, double DetEst, double TrueAbove, double Covered, string Broke);

public sealed record Result(
    string Label, int Steps, bool Arrived, double ExtentErr, double Drift,
    int Stuck, int Clamped, int BadEst, double MaxJump, double Ask, int Reverse,
    int Crashes, int Broken, int Blank)
{
    public int Faults =>
          (Arrived ? 0 : 1)
        + (ExtentErr > 200 ? 1 : 0)
        + (Drift > 200 ? 1 : 0)
        + (Stuck > 0 ? 1 : 0)
        + (Clamped > 0 ? 1 : 0)
        + (BadEst > 0 ? 1 : 0)
        + (MaxJump > Ask * 1.5 + 1 ? 1 : 0)
        + (Reverse > 0 ? 1 : 0)
        + (Crashes > 0 ? 1 : 0)
        + (Broken > 0 ? 1 : 0)
        + (Blank > 0 ? 1 : 0);

    public override string ToString() =>
        $"  {Label,-18} steps={Steps,-4} arrive={Arrived,-5} extent={ExtentErr,6:F0} drift={Drift,6:F0} " +
        $"stuck={Stuck,-3} rev={Reverse,-3} jump={MaxJump,5:F0} clamp={Clamped,-3} badEst={BadEst,-3} " +
        $"crash={Crashes,-3} broke={Broken,-3} blank={Blank,-3} FAULTS={Faults}";
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
    // 1188 and 774. The SPREAD is the point, so it is reproduced rather than averaged away.
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

    private static DataGrid BuildGrid(List<Row> rows, Mode mode)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns         = false,
            IsReadOnly                  = true,
            GridLinesVisibility         = DataGridGridLinesVisibility.None,
            HeadersVisibility           = DataGridHeadersVisibility.None,
            RowDetailsVisibilityMode    = mode == Mode.WhenSelected
                ? DataGridRowDetailsVisibilityMode.VisibleWhenSelected
                : DataGridRowDetailsVisibilityMode.Collapsed,
            AreRowDetailsFrozen         = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            ItemsSource                 = rows,
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

            // Every drawer the same height, scrolling inside. The one thing that removes the
            // variance the whole fault rests on -- at the cost of the look.
            if (mode != Mode.Uniform) return list;

            return new ScrollViewer { Height = 320, Content = list };
        });

        // Exactly what WorklistView does: the flag lives on the item, restored as rows realise.
        grid.LoadingRow += (_, e) =>
        {
            if (e.Row.DataContext is not Row r) return;

            // ⚠️ VisibleWhenSelected only skips the large-jump shortcut if the template is set AND
            // every row has an EXPLICIT visibility, or selection would open drawers on its own.
            // An unchanged assignment writes nothing, so it is toggled to force the entry.
            if (mode == Mode.WhenSelected) e.Row.AreDetailsVisible = !r.IsOpen;

            if (e.Row.AreDetailsVisible != r.IsOpen) e.Row.AreDetailsVisible = r.IsOpen;
        };

        return grid;
    }

    // ── The candidates ───────────────────────────────────────────────────────

    /// <summary>
    /// Stock, the shipped fix, the rejected one, and two that touch nothing but public API.
    /// ⚠️ Takeover is kept to stay rejected, NOT to be cleared: the rig cannot clear it. It scores
    /// zero here and it destroyed the real app. Anything that writes DisplayData is out on those
    /// grounds and no green line changes that.
    /// </summary>
    private enum Mode { Stock, Pin, Takeover, WhenSelected, Uniform }

    private sealed class Fix(DataGrid grid, List<Row> model, Mode mode)
    {
        private readonly Dictionary<int, double> _heights = [];
        private readonly Dictionary<int, double> _seen    = [];

        private double _meanBody = 22, _meanDrawer, _offset = -1;
        private bool   _busy;

        private double Height(int i) =>
            _heights.TryGetValue(i, out var h)
                ? h
                : _meanBody + (i < model.Count && model[i].IsOpen ? _meanDrawer : 0);

        private double Total()
        {
            var t = 0.0;
            for (var i = 0; i < model.Count; i++) t += Height(i);
            return t;
        }

        /// <summary>Runs from LayoutUpdated, exactly as the app wires it.</summary>
        public void Pass()
        {
            if (mode == Mode.Stock || _busy) return;

            _busy = true;
            try { Measure(); if (mode == Mode.Takeover) Place(); }
            finally { _busy = false; }
        }

        private void Measure()
        {
            var bodies  = new List<double>();
            var drawers = new List<double>();

            foreach (var row in grid.GetVisualDescendants().OfType<DataGridRow>())
            {
                if (row.Index < 0 || row.Bounds.Height <= 1) continue;

                var presenter = row.AreDetailsVisible
                    ? row.GetVisualDescendants().OfType<DataGridDetailsPresenter>().FirstOrDefault()
                    : null;

                var drawer = presenter is { Bounds.Height: > 1 } ? presenter.Bounds.Height : 0;
                if (drawer > 0) drawers.Add(drawer);

                var body = row.Bounds.Height - drawer;
                if (body > 1) bodies.Add(body);

                if (Math.Abs(row.DesiredSize.Height - row.Bounds.Height) >= 0.5) continue;

                if (_seen.TryGetValue(row.Index, out var once) && Math.Abs(once - row.Bounds.Height) < 0.5)
                    _heights[row.Index] = row.Bounds.Height;

                _seen[row.Index] = row.Bounds.Height;
            }

            if (bodies.Count == 0) return;

            _meanBody   = bodies.Average();
            _meanDrawer = drawers.Count > 0 ? drawers.Average() : 0;

            Poke(grid, "RowHeightEstimate", _meanBody);
            Poke(grid, "RowDetailsHeightEstimate", _meanDrawer);
            Poke(grid, "_lastEstimatedRow", int.MaxValue);
        }

        /// <summary>
        /// ⚠️ The REJECTED candidate, kept so the rig goes on proving it fails.
        ///
        /// It computes the position itself and overwrites the grid's landing, which reads well and
        /// scored clean on the old rig. On the real app it lost rows, locked the grid, and stopped
        /// the whole window laying out. Writing NegVerticalOffset and calling UpdateDisplayedRows
        /// from a layout callback relocates the displayed set from outside the pass that owns it.
        /// </summary>
        private void Place()
        {
            var cells  = D(Peek(grid, "CellsEstimatedHeight"));
            var max    = Math.Max(0, Total() - cells);
            var theirs = D(Peek(grid, "_verticalOffset"));

            var span   = _meanBody + _meanDrawer;
            var target = _offset < 0 || Math.Abs(_offset - theirs) > span ? theirs : _offset;

            target  = Math.Clamp(target, 0, max);
            _offset = target;

            if (Math.Abs(theirs - target) > 0.5) Poke(grid, "_verticalOffset", target);

            var slot = 0;
            var acc  = 0.0;
            while (slot < model.Count - 1 && acc + Height(slot) <= target) { acc += Height(slot); slot++; }

            var neg     = Math.Max(0, target - acc);
            var display = Peek(grid, "DisplayData")!;

            if (Math.Abs(D(Peek(grid, "NegVerticalOffset")) - neg) > 0.5)
                Poke(grid, "NegVerticalOffset", neg);

            if (I(Peek(display, "FirstScrollingSlot")) != slot)
                grid.GetType().GetMethod("UpdateDisplayedRows", Any)!.Invoke(grid, [slot, cells]);

            if (Peek(grid, "_vScrollBar") is not ScrollBar bar) return;

            if (Math.Abs(bar.Maximum - max) > 0.5)        bar.Maximum      = max;
            if (Math.Abs(bar.ViewportSize - cells) > 0.5) bar.ViewportSize = cells;

            var value = Math.Clamp(target, 0, max);
            if (Math.Abs(bar.Value - value) > 0.5) bar.Value = value;
        }

        public void Ask(double pixels)
        {
            if (mode != Mode.Takeover) return;
            if (_offset < 0) _offset = D(Peek(grid, "_verticalOffset"));

            var cells = D(Peek(grid, "CellsEstimatedHeight"));
            _offset = Math.Clamp(_offset + pixels, 0, Math.Max(0, Total() - cells));
        }
    }

    // ── Driving: real input, real layout ─────────────────────────────────────

    private static readonly List<string> Crashes = [];

    /// <summary>
    /// ⚠️ Runs the RENDER timer, not just dispatcher jobs.
    ///
    /// <para>The first version of this rig pumped a fixed number of RunJobs per step and passed a
    /// candidate that then corrupted the real grid — rows vanishing, the window ceasing to lay out
    /// at all. Dispatcher jobs alone never reproduce continuous layout or a re-entrant
    /// LayoutUpdated, which is exactly where writing to DisplayData from a layout callback comes
    /// apart. This drives the real cycle, and anything thrown inside it is recorded as a fault
    /// rather than allowed to end the run.</para>
    /// </summary>
    private static void Settle(int passes = 4)
    {
        for (var i = 0; i < passes; i++)
        {
            try
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }
            catch (Exception ex)
            {
                Crashes.Add(ex.GetBaseException().Message);
            }
        }
    }

    /// <summary>A real wheel event over the grid. Positive notches scroll up.</summary>
    private static void Wheel(Window window, double notches)
    {
        try { window.MouseWheel(new Point(450, 500), new Vector(0, notches)); }
        catch (Exception ex) { Crashes.Add(ex.GetBaseException().Message); }
    }

    /// <summary>A real press-drag-release on the scrollbar thumb.</summary>
    private static void DragThumb(Window window, DataGrid grid, double pixels)
    {
        if (Peek(grid, "_vScrollBar") is not ScrollBar bar) return;
        if (bar.GetVisualDescendants().OfType<Thumb>().FirstOrDefault() is not { } thumb) return;
        if (thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)
            is not { } at) return;

        // The thumb travels the track, not the content, so the pointer moves proportionally.
        var track  = Math.Max(1, bar.Bounds.Height - thumb.Bounds.Height);
        var span   = Math.Max(1, bar.Maximum);
        var moveBy = pixels / span * track;

        try
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseMove(new Point(at.X, at.Y + moveBy));
            window.MouseUp(new Point(at.X, at.Y + moveBy), MouseButton.Left);
        }
        catch (Exception ex) { Crashes.Add(ex.GetBaseException().Message); }
    }

    // ── Reading, and the invariants a live grid must keep ────────────────────

    private static double Height(List<Row> rows, int i) =>
        i >= 0 && i < rows.Count ? rows[i].TrueHeight : 0;

    private static Step Read(DataGrid grid, List<Row> rows)
    {
        var display = Peek(grid, "DisplayData")!;
        var bar     = Peek(grid, "_vScrollBar");
        var first   = I(Peek(display, "FirstScrollingSlot"));
        var last    = I(Peek(display, "LastScrollingSlot"));
        var neg     = D(Peek(grid, "NegVerticalOffset"));

        var above = 0.0;
        for (var i = 0; i < first && i < rows.Count; i++) above += rows[i].TrueHeight;

        // ⚠️ The checks the old rig had no way to fail. A grid that has lost its displayed set
        // still reports plausible offsets; what it cannot do is name a row for every slot it
        // claims to be showing, or cover the viewport with them.
        var broke   = "";
        var covered = 0.0;
        var get     = display.GetType().GetMethod("GetDisplayedElement", Any);

        if (first < 0 || last < first)
        {
            broke = "no displayed set";
        }
        else
        {
            for (var slot = first; slot <= last; slot++)
            {
                object? element = null;
                try { element = get?.Invoke(display, [slot]); }
                catch { broke = $"slot {slot} threw"; break; }

                if (element is not DataGridRow r) { broke = $"slot {slot} empty"; break; }
                if (r.Index != slot) { broke = $"slot {slot} holds row {r.Index}"; break; }
                covered += r.Bounds.Height;
            }
        }

        if (broke.Length == 0 && neg > Height(rows, first) + 0.5)
            broke = $"neg {neg:F0} past row {first} ({Height(rows, first):F0})";

        return new Step(
            D(Peek(grid, "_verticalOffset")), neg, first, last,
            bar is null ? 0 : D(Peek(bar, "Maximum")),
            bar is null ? 0 : D(Peek(bar, "Value")),
            D(Peek(grid, "RowHeightEstimate")), D(Peek(grid, "RowDetailsHeightEstimate")),
            above + neg, covered - neg, broke);
    }

    // ── A run ────────────────────────────────────────────────────────────────

    private static Result Run(string label, Mode mode, string input, double step, int burst, bool trace)
    {
        Crashes.Clear();

        var rows  = BuildRows();
        var truth = rows.Sum(r => r.TrueHeight);
        var grid  = BuildGrid(rows, mode);
        var fix   = new Fix(grid, rows, mode);

        var window = new Window { Width = 900, Height = Viewport, Content = grid };
        grid.LayoutUpdated += (_, _) => fix.Pass();

        window.Show();
        Settle(10);

        // Down to the bottom the way a person gets there.
        for (var i = 0; i < 300; i++) { Wheel(window, -1); fix.Ask(step); Settle(2); }

        var steps = new List<Step> { Read(grid, rows) };

        for (var i = 0; i < 400; i++)
        {
            // ⚠️ A burst is the case that broke the app: several inputs between layout passes,
            // which is simply holding the wheel down while the grid is busy.
            for (var b = 0; b < burst; b++)
            {
                if (input == "wheel") Wheel(window, 1);
                else                  DragThumb(window, grid, -step);
                fix.Ask(-step);
            }

            Settle(burst > 1 ? 4 : 2);

            var s = Read(grid, rows);
            steps.Add(s);
            if (s.First == 0 && s.Neg <= 0.5) break;
        }

        if (trace)
            foreach (var s in steps)
                Console.WriteLine($"      off={s.Offset,8:F0} neg={s.Neg,7:F0} slots={s.First,3}..{s.Last,-3} " +
                                  $"true={s.TrueAbove,8:F0} sbMax={s.SbMax,8:F0} rowEst={s.RowEst,7:F1} " +
                                  $"detEst={s.DetEst,7:F1} cover={s.Covered,7:F0} {s.Broke}");

        var ask     = step * burst;
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
            jump, ask, reverse,
            Crashes.Count,
            steps.Count(s => s.Broke.Length > 0),
            // A grid showing less than half a viewport of rows, anywhere but the very end.
            steps.Count(s => s.Covered < Viewport / 2 && s.First > 0));
    }

    public static int Main(string[] args)
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
            .SetupWithoutStarting();

        var trace = args.Contains("--trace");
        var truth = BuildRows().Sum(r => r.TrueHeight);

        Console.WriteLine($"true content = {truth:F0}px over 50 rows (4 open), viewport {Viewport:F0}px");
        Console.WriteLine($"a correct scrollbar maximum is {truth - Viewport:F0}");
        Console.WriteLine();

        var graded = 0;

        foreach (var (input, step, burst) in new[]
                 {
                     ("wheel", 50.0, 1),
                     ("wheel", 50.0, 8),      // held down, layout behind
                     ("drag",  50.0, 1),
                     ("drag", 600.0, 1),
                 })
        {
            Console.WriteLine($"{input} {step:F0}px x{burst}:");

            foreach (var (label, mode) in new[]
                     {
                         ("stock Avalonia", Mode.Stock),
                         ("estimates pinned", Mode.Pin),
                         ("+ takeover [REJ]", Mode.Takeover),
                         ("whenSelected", Mode.WhenSelected),
                         ("uniform drawers", Mode.Uniform),
                     })
            {
                var r = Run(label, mode, input, step, burst, trace);
                Console.WriteLine(r);

                // ⚠️ Only the SHIPPED candidate is graded. Stock is the baseline; the takeover is
                // the rejected one, kept here to stay rejected.
                if (mode == Mode.Pin) graded += r.Faults;
            }

            Console.WriteLine();
        }

        Console.WriteLine(graded == 0
            ? "CLEAN - the shipped candidate has no faults in any mode."
            : $"FAULTS = {graded} in the shipped candidate.");

        return graded == 0 ? 0 : 1;
    }
}
