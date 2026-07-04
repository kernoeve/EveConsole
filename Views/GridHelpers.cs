using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using EveCortex.ViewModels;

namespace EveCortex.Views;

// Fires on the UI thread approximately every second.  One shared timer for all subscribers.
internal static class ClockService
{
    private static readonly System.Timers.Timer _timer;

    public static event Action? Tick;

    static ClockService()
    {
        _timer = new System.Timers.Timer(1000) { AutoReset = true };
        _timer.Elapsed += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Tick?.Invoke());
        _timer.Start();
    }
}

// Shared time-remaining calculation used by SelectableCell and the detail panel.
internal static class TimeRemainingHelper
{
    public static string Compute(GridRow row)
    {
        var status = row["Status"].ToLowerInvariant();
        if (status is "delivered" or "cancelled" or "reverted") return "";
        if (status == "ready") return "Ready";

        var raw = row["End Date Raw"];
        if (string.IsNullOrEmpty(raw)) return row["Time Remaining"];

        if (!DateTimeOffset.TryParse(raw, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var end))
            return row["Time Remaining"];

        var rem = end.ToUniversalTime() - DateTimeOffset.UtcNow;
        if (rem <= TimeSpan.Zero) return "Ready";

        if (rem.TotalDays >= 1)  return $"{(int)rem.TotalDays}d {rem.Hours}h {rem.Minutes}m";
        if (rem.TotalHours >= 1) return $"{(int)rem.TotalHours}h {rem.Minutes}m";
        if (rem.TotalMinutes >= 1) return $"{(int)rem.TotalMinutes}m {rem.Seconds}s";
        return $"{rem.Seconds}s";
    }
}

// Tracks a rectangular cell selection (r0..r1, c0..c1) for one DataGrid.
internal sealed class CellSelectionService
{
    public event Action? Changed;

    private DataGrid?                 _grid;
    private int                       _r0, _r1, _c0, _c1;
    private Dictionary<GridRow, int>? _rowIdx;
    private Dictionary<string, int>?  _colIdx;
    private int                       _rowCount;

    public void Set(DataGrid grid,
                    List<GridRow> rows, List<string> cols,
                    int r0, int r1, int c0, int c1)
    {
        bool rebuild = _grid != grid || _rowIdx is null || _colIdx is null
                    || _rowCount != rows.Count;
        _grid     = grid;
        _r0 = r0; _r1 = r1; _c0 = c0; _c1 = c1;
        _rowCount = rows.Count;

        if (rebuild)
        {
            _rowIdx = new(rows.Count);
            for (int i = 0; i < rows.Count; i++) _rowIdx[rows[i]] = i;
            _colIdx = new(cols.Count);
            for (int i = 0; i < cols.Count; i++) _colIdx[cols[i]] = i;
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        _grid = null;
        Changed?.Invoke();
    }

    public bool IsSelected(DataGrid grid, GridRow? row, string col)
    {
        if (_grid != grid || _rowIdx is null || _colIdx is null || row is null) return false;
        if (!_rowIdx.TryGetValue(row, out int r) || r < _r0 || r > _r1) return false;
        if (!_colIdx.TryGetValue(col, out int c) || c < _c0 || c > _c1) return false;
        return true;
    }
}

// Data cell.  "Time Remaining" cells also subscribe to ClockService and
// recompute their value live every second without mutating the GridRow.
internal sealed class SelectableCell : Border
{
    private static readonly IBrush SelectionBrush =
        new SolidColorBrush(Color.FromArgb(120, 51, 153, 255));

    private readonly DataGrid             _grid;
    private readonly string               _col;
    private readonly CellSelectionService _svc;
    private readonly TextBlock            _tb;
    private readonly bool                 _isTimeRemaining;

    public string ColName => _col;

    public SelectableCell(DataGrid grid, string col, CellSelectionService svc)
    {
        _grid            = grid;
        _col             = col;
        _svc             = svc;
        _isTimeRemaining = col == "Time Remaining";
        _tb = new TextBlock
        {
            Padding           = new Thickness(6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize          = 11,
        };
        Child = _tb;
        DataContextChanged += (_, _) => Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _svc.Changed += Refresh;
        if (_isTimeRemaining) ClockService.Tick += Refresh;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _svc.Changed -= Refresh;
        if (_isTimeRemaining) ClockService.Tick -= Refresh;
        base.OnDetachedFromVisualTree(e);
    }

    private void Refresh()
    {
        var row = DataContext as GridRow;
        _tb.Text = _isTimeRemaining && row is not null
            ? TimeRemainingHelper.Compute(row)
            : row?[_col] ?? "";
        Background = _svc.IsSelected(_grid, row, _col) ? SelectionBrush : null;
    }
}
