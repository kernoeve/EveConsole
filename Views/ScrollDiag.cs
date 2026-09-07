using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace EveConsole.Views;

/// <summary>
/// TEMPORARY. Records what the DataGrid actually believes about its own scroll extent, so the
/// upward-scroll fight can be diagnosed from numbers instead of from another theory.
///
/// <para>⚠️ Delete this file, its attribute in WorklistView.axaml and this note once the flicker
/// is settled. It reads private members by reflection and appends to a log on every wheel tick;
/// neither belongs in a shipped build.</para>
///
/// <para>Writes <c>%LOCALAPPDATA%\EveConsole\scrolldiag.log</c>. One line per wheel tick, taken
/// after layout has settled: the two estimates the grid prices unrealised rows with, the
/// scrollbar's maximum and value, the offset, and the realised rows with their real heights.
/// Stops itself after a few hundred lines so it cannot grow without bound.</para>
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

    private static int _lines;
    private const int Cap = 600;

    static ScrollDiag()
    {
        LabelProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.GetNewValue<string?>() is not { Length: > 0 } label) return;

            // Tunnel: the grid handles the wheel itself, so a bubbling handler never runs.
            grid.AddHandler(InputElement.PointerWheelChangedEvent,
                (_, args) => After(grid, label, args.Delta.Y),
                RoutingStrategies.Tunnel);
        });
    }

    private static void After(DataGrid grid, string label, double dy) =>
        Dispatcher.UIThread.Post(() => Write(grid, label, dy), DispatcherPriority.Background);

    private static object? Peek(object target, string name)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var type = target.GetType();
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.GetProperty(name, Any) is { } p) return p.GetValue(target);
            if (t.GetField(name, Any) is { } f) return f.GetValue(target);
        }
        return null;
    }

    private static string Num(object? v) =>
        v is double d ? d.ToString("F1", CultureInfo.InvariantCulture)
      : v?.ToString() ?? "?";

    private static void Write(DataGrid grid, string label, double dy)
    {
        if (_lines >= Cap) return;

        try
        {
            var vsb  = Peek(grid, "_vScrollBar");
            var disp = Peek(grid, "DisplayData");

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append(' ').Append(label)
              .Append(dy > 0 ? " UP  " : " DOWN")
              .Append(" rowEst=").Append(Num(Peek(grid, "RowHeightEstimate")))
              .Append(" detEst=").Append(Num(Peek(grid, "RowDetailsHeightEstimate")))
              .Append(" offset=").Append(Num(Peek(grid, "_verticalOffset")))
              .Append(" neg=").Append(Num(Peek(grid, "NegVerticalOffset")))
              .Append(" cells=").Append(Num(Peek(grid, "CellsEstimatedHeight")));

            if (vsb is not null)
                sb.Append(" sbMax=").Append(Num(Peek(vsb, "Maximum")))
                  .Append(" sbVal=").Append(Num(Peek(vsb, "Value")))
                  .Append(" sbView=").Append(Num(Peek(vsb, "ViewportSize")));

            if (disp is not null)
                sb.Append(" slots=").Append(Num(Peek(disp, "FirstScrollingSlot")))
                  .Append("..").Append(Num(Peek(disp, "LastScrollingSlot")))
                  .Append('/').Append(Num(Peek(disp, "NumDisplayedScrollingElements")));

            // What the realised rows really measure, open ones included. This is the truth the
            // two estimates above are standing in for.
            var rows = grid.GetVisualDescendants().OfType<DataGridRow>()
                           .Where(r => r.IsVisible)
                           .OrderBy(r => r.Index)
                           .Select(r => $"{r.Index}{(r.AreDetailsVisible ? "*" : "")}:{r.Bounds.Height:F0}");

            sb.Append(" | ").Append(string.Join(' ', rows));

            File.AppendAllText(Path, sb.Append(Environment.NewLine).ToString());
            _lines++;
        }
        catch
        {
            // A diagnostic must never be the thing that breaks the tool it is diagnosing.
            _lines = Cap;
        }
    }
}
