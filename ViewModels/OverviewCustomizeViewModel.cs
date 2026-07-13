using System;
using System.Collections.ObjectModel;
using System.Linq;
using EveCortex.Models;
using ReactiveUI;

namespace EveCortex.ViewModels;

// Backs the "Customize Overview" dialog. Rows/Cols are decimal so they bind directly to
// NumericUpDown; section placements are edited visually on a grid canvas (see the window
// code-behind), so they are plain ints here plus a Fits() validator for bounds/overlap.
public class OverviewCustomizeViewModel : ReactiveObject
{
    private decimal _rows;
    public decimal Rows { get => _rows; set => this.RaiseAndSetIfChanged(ref _rows, value); }

    private decimal _cols;
    public decimal Cols { get => _cols; set => this.RaiseAndSetIfChanged(ref _cols, value); }

    public int RowCount => Math.Max(1, (int)_rows);
    public int ColCount => Math.Max(1, (int)_cols);

    public ObservableCollection<OverviewSectionEditVm> Sections { get; } = [];

    public OverviewCustomizeViewModel(OverviewLayout layout)
    {
        _rows = layout.Rows;
        _cols = layout.Cols;
        foreach (var (key, title) in OverviewLayout.KnownSections)
        {
            var p = layout.Sections.FirstOrDefault(s => s.Key == key);
            Sections.Add(new OverviewSectionEditVm
            {
                Key     = key,
                Title   = title,
                Enabled = p?.Enabled ?? false,
                Row     = Math.Max(1, p?.Row ?? 1),
                Col     = Math.Max(1, p?.Col ?? 1),
                RowSpan = Math.Max(1, p?.RowSpan ?? 1),
                ColSpan = Math.Max(1, p?.ColSpan ?? 1),
            });
        }
    }

    // True if the given rectangle is fully inside the grid and doesn't overlap any other
    // enabled section. `self` is excluded from the overlap check (it's the one being moved).
    public bool Fits(OverviewSectionEditVm? self, int row, int col, int rowSpan, int colSpan)
    {
        if (row < 1 || col < 1 || rowSpan < 1 || colSpan < 1) return false;
        if (row + rowSpan - 1 > RowCount) return false;
        if (col + colSpan - 1 > ColCount) return false;

        foreach (var s in Sections)
        {
            if (!s.Enabled || ReferenceEquals(s, self)) continue;
            bool separate = col + colSpan - 1 < s.Col
                         || s.Col + s.ColSpan - 1 < col
                         || row + rowSpan - 1 < s.Row
                         || s.Row + s.RowSpan - 1 < row;
            if (!separate) return false;
        }
        return true;
    }

    // Clamp every placed section back inside the grid after a rows/cols change. Overlaps that
    // survive clamping are left for the user to fix by dragging.
    public void ClampToBounds()
    {
        foreach (var s in Sections.Where(s => s.Enabled))
        {
            s.ColSpan = Math.Clamp(s.ColSpan, 1, ColCount);
            s.RowSpan = Math.Clamp(s.RowSpan, 1, RowCount);
            s.Col     = Math.Clamp(s.Col, 1, ColCount - s.ColSpan + 1);
            s.Row     = Math.Clamp(s.Row, 1, RowCount - s.RowSpan + 1);
        }
    }

    // Find the first free 1×1 cell (row-major), or null if the grid is full.
    public (int Row, int Col)? FirstFreeCell()
    {
        for (int r = 1; r <= RowCount; r++)
            for (int c = 1; c <= ColCount; c++)
                if (Fits(null, r, c, 1, 1))
                    return (r, c);
        return null;
    }

    public OverviewLayout Build() => new()
    {
        Rows = RowCount,
        Cols = ColCount,
        Sections = Sections.Select(s => new OverviewPlacement
        {
            Key     = s.Key,
            Enabled = s.Enabled,
            Row     = s.Row,
            Col     = s.Col,
            RowSpan = s.RowSpan,
            ColSpan = s.ColSpan,
        }).ToList(),
    };
}

public class OverviewSectionEditVm : ReactiveObject
{
    public string Key   { get; init; } = "";
    public string Title { get; init; } = "";

    private bool _enabled;
    public bool Enabled { get => _enabled; set => this.RaiseAndSetIfChanged(ref _enabled, value); }

    private int _row = 1, _col = 1, _rowSpan = 1, _colSpan = 1;
    public int Row     { get => _row;     set => this.RaiseAndSetIfChanged(ref _row, value); }
    public int Col     { get => _col;     set => this.RaiseAndSetIfChanged(ref _col, value); }
    public int RowSpan { get => _rowSpan; set => this.RaiseAndSetIfChanged(ref _rowSpan, value); }
    public int ColSpan { get => _colSpan; set => this.RaiseAndSetIfChanged(ref _colSpan, value); }
}
