using System;
using System.Collections.ObjectModel;
using System.Linq;
using EveCortex.Models;
using ReactiveUI;

namespace EveCortex.ViewModels;

// Backs the "Customize Overview" dialog: an editable copy of an OverviewLayout.
// Numeric fields are decimal so they bind directly to NumericUpDown.Value.
public class OverviewCustomizeViewModel : ReactiveObject
{
    private decimal _rows;
    public decimal Rows { get => _rows; set => this.RaiseAndSetIfChanged(ref _rows, value); }

    private decimal _cols;
    public decimal Cols { get => _cols; set => this.RaiseAndSetIfChanged(ref _cols, value); }

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
                Row     = p?.Row ?? 1,
                Col     = p?.Col ?? 1,
                RowSpan = p?.RowSpan ?? 1,
                ColSpan = p?.ColSpan ?? 1,
            });
        }
    }

    public OverviewLayout Build() => new()
    {
        Rows = Math.Max(1, (int)Rows),
        Cols = Math.Max(1, (int)Cols),
        Sections = Sections.Select(s => new OverviewPlacement
        {
            Key     = s.Key,
            Enabled = s.Enabled,
            Row     = Math.Max(1, (int)s.Row),
            Col     = Math.Max(1, (int)s.Col),
            RowSpan = Math.Max(1, (int)s.RowSpan),
            ColSpan = Math.Max(1, (int)s.ColSpan),
        }).ToList(),
    };
}

public class OverviewSectionEditVm : ReactiveObject
{
    public string Key   { get; init; } = "";
    public string Title { get; init; } = "";

    private bool _enabled;
    public bool Enabled { get => _enabled; set => this.RaiseAndSetIfChanged(ref _enabled, value); }

    private decimal _row = 1, _col = 1, _rowSpan = 1, _colSpan = 1;
    public decimal Row     { get => _row;     set => this.RaiseAndSetIfChanged(ref _row, value); }
    public decimal Col     { get => _col;     set => this.RaiseAndSetIfChanged(ref _col, value); }
    public decimal RowSpan { get => _rowSpan; set => this.RaiseAndSetIfChanged(ref _rowSpan, value); }
    public decimal ColSpan { get => _colSpan; set => this.RaiseAndSetIfChanged(ref _colSpan, value); }
}
