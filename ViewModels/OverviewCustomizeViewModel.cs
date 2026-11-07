using System;
using System.Collections.ObjectModel;
using System.Linq;
using EveConsole.Models;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

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

/// <summary>One layout in the editor: its name, and its grid as it's being edited.</summary>
public sealed class OverviewLayoutEntryVm : ReactiveObject
{
    public OverviewLayoutEntryVm(string? was, string name, OverviewLayout layout)
    {
        Was = was;
        _name = name;
        Editor = new OverviewCustomizeViewModel(layout);
    }

    /// <summary>The key it was saved under, or null for one made in this session.</summary>
    public string? Was { get; }

    /// <summary>Default keeps its name, can't be deleted, and is what older versions of the app show.</summary>
    public bool IsDefault => Was is not null && EveConsole.Services.OverviewLayoutStore.IsDefault(Was);

    private string _name;
    public string Name
    {
        get => IsDefault ? EveConsole.Localization.OverviewText.LayoutDefault : _name;
        set { if (!IsDefault) this.RaiseAndSetIfChanged(ref _name, value); }
    }

    public OverviewCustomizeViewModel Editor { get; }

    public override string ToString() => Name;
}

/// <summary>
/// The layout editor's layouts: the one being edited, picked from a dropdown, and Add and Delete.
/// Nothing is saved until Apply, which saves them all together; Cancel leaves everything as it was.
/// </summary>
public sealed class OverviewLayoutsEditVm : ReactiveObject
{
    public ObservableCollection<OverviewLayoutEntryVm> Layouts { get; } = [];

    /// <param name="showing">The key of the layout the Overview it was opened from is showing, which it starts on.</param>
    /// <param name="current">That Overview's layout as it stands.</param>
    public OverviewLayoutsEditVm(EveConsole.Services.OverviewLayoutStore store, string showing, OverviewLayout current)
    {
        foreach (var choice in store.Choices)
        {
            var mine = string.Equals(choice.Key, showing, StringComparison.OrdinalIgnoreCase);
            Layouts.Add(new OverviewLayoutEntryVm(choice.Key, choice.Label, mine ? current : store.Get(choice.Key)));
        }
        _selected = Layouts.FirstOrDefault(l => string.Equals(l.Was, showing, StringComparison.OrdinalIgnoreCase)) ?? Layouts[0];
        AddCommand = ReactiveCommand.Create(Add);
        DeleteCommand = ReactiveCommand.Create(Delete, this.WhenAnyValue(x => x.Selected, s => s is { IsDefault: false }));
    }

    private OverviewLayoutEntryVm _selected;
    public OverviewLayoutEntryVm Selected
    {
        get => _selected;
        set { if (value is not null) this.RaiseAndSetIfChanged(ref _selected, value); }
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> AddCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> DeleteCommand { get; }

    /// <summary>A new layout, starting as a copy of the one being edited.</summary>
    private void Add()
    {
        var taken = Layouts.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = Layouts.Count;
        string name;
        do name = string.Format(System.Globalization.CultureInfo.CurrentCulture, EveConsole.Localization.OverviewText.LayoutNumbered, n++);
        while (taken.Contains(name));
        var entry = new OverviewLayoutEntryVm(null, name, _selected.Editor.Build());
        Layouts.Add(entry);
        Selected = entry;
    }

    private void Delete()
    {
        if (_selected.IsDefault) return;
        var i = Layouts.IndexOf(_selected);
        Layouts.Remove(_selected);
        Selected = Layouts[Math.Clamp(i - 1, 0, Layouts.Count - 1)];
    }

    /// <summary>Everything as it now is, with the one being edited as the one to show.</summary>
    public OverviewLayoutEdits Build() =>
        new([.. Layouts.Select(l => (l.Was, l.IsDefault ? EveConsole.Services.OverviewLayoutStore.DefaultKey : l.Name, l.Editor.Build()))],
            Layouts.IndexOf(_selected));
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
