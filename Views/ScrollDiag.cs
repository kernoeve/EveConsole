using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace EveConsole.Views;

/// <summary>
/// TEMPORARY. Records what the DataGrid believes about its own scroll extent, and how long each
/// step costs, so the upward-scroll fight can be diagnosed from numbers instead of theories.
///
/// <para>⚠️ Delete this file and its attribute in WorklistView.axaml once the flicker is settled.
/// It reads private members by reflection and writes a log; neither belongs in a shipped build.</para>
///
/// <para>⚠️ Logs on LayoutUpdated, NOT on the wheel. The first version logged from a Background
/// post per wheel event, which is starved exactly when the grid is busy — so the slow steps, the
/// ones worth seeing, were the ones it missed. Every layout pass gets a line now, deduped when
/// nothing moved.</para>
///
/// <para>Two candidates are separated by this: <c>pending</c> is the height a single scroll call
/// is about to apply, and above twice the viewport Avalonia abandons the exact walk for an
/// estimate that prices every row at RowHeightEstimate alone — which ignores drawers entirely in
/// Collapsed mode. <c>ms</c> and <c>built</c> are the other one: time since the last change, and
/// how many drawers have been constructed, since a seventy-line drawer rebuilt on recycling is
/// enough to stall the thread on its own.</para>
/// </summary>
public sealed class ScrollDiag
{
    private ScrollDiag() { }

    public static readonly AttachedProperty<string?> LabelProperty =
        AvaloniaProperty.RegisterAttached<ScrollDiag, DataGrid, string?>("Label");

    public static void SetLabel(DataGrid grid, string? value) => grid.SetValue(LabelProperty, value);
    public static string? GetLabel(DataGrid grid) => grid.GetValue(LabelProperty);

    private static readonly string Path =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EveConsole", "scrolldiag.log");

    private const int Cap = 4000;

    private static readonly List<string> Pending = [];
    private static readonly Stopwatch    Clock   = Stopwatch.StartNew();

    private static int    _lines;
    private static string _last  = "";
    private static long   _lastMs;
    private static int    _built;

    static ScrollDiag()
    {
        LabelProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.GetNewValue<string?>() is not { Length: > 0 } label) return;

            grid.LoadingRowDetails += (_, _) => _built++;
            grid.LayoutUpdated     += (_, _) => Write(grid, label);
        });
    }

    private static object? Peek(object target, string name)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        for (var t = target.GetType(); t is not null; t = t.BaseType)
        {
            if (t.GetProperty(name, Any) is { } p) return p.GetValue(target);
            if (t.GetField(name, Any) is { } f) return f.GetValue(target);
        }
        return null;
    }

    private static string Num(object? v) =>
        v is double d ? d.ToString("F1", CultureInfo.InvariantCulture) : v?.ToString() ?? "?";

    private static void Write(DataGrid grid, string label)
    {
        if (_lines >= Cap) return;

        try
        {
            var vsb  = Peek(grid, "_vScrollBar");
            var disp = Peek(grid, "DisplayData");

            var sb = new StringBuilder();
            sb.Append(label)
              .Append(" rowEst=").Append(Num(Peek(grid, "RowHeightEstimate")))
              .Append(" detEst=").Append(Num(Peek(grid, "RowDetailsHeightEstimate")))
              .Append(" offset=").Append(Num(Peek(grid, "_verticalOffset")))
              .Append(" neg=").Append(Num(Peek(grid, "NegVerticalOffset")))
              .Append(" cells=").Append(Num(Peek(grid, "CellsEstimatedHeight")));

            // The height ONE scroll call is about to apply. Past twice the viewport Avalonia
            // stops walking rows and estimates instead.
            if (disp is not null)
                sb.Append(" pending=").Append(Num(Peek(disp, "PendingVerticalScrollHeight")));

            if (vsb is not null)
                sb.Append(" sbMax=").Append(Num(Peek(vsb, "Maximum")))
                  .Append(" sbVal=").Append(Num(Peek(vsb, "Value")));

            if (disp is not null)
                sb.Append(" slots=").Append(Num(Peek(disp, "FirstScrollingSlot")))
                  .Append("..").Append(Num(Peek(disp, "LastScrollingSlot")));

            sb.Append(" built=").Append(_built);

            var rows = grid.GetVisualDescendants().OfType<DataGridRow>()
                           .Where(r => r.IsVisible)
                           .OrderBy(r => r.Index)
                           .Select(r => $"{r.Index}{(r.AreDetailsVisible ? "*" : "")}:{r.Bounds.Height:F0}");

            sb.Append(" | ").Append(string.Join(' ', rows));

            var line = sb.ToString();
            if (line == _last) return;          // Layout ran; nothing moved.

            var now = Clock.ElapsedMilliseconds;
            Pending.Add($"{DateTime.Now:HH:mm:ss.fff} ms={now - _lastMs,-5} {line}");
            _lastMs = now;
            _last   = line;
            _lines++;

            // Batched, so the log is not itself the stall being measured.
            if (Pending.Count < 25) return;

            File.AppendAllLines(Path, Pending);
            Pending.Clear();
        }
        catch
        {
            // A diagnostic must never be the thing that breaks the tool it is diagnosing.
            _lines = Cap;
        }
    }
}
