using System.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// Keeps a grid's per-row detail Popups open on rows that are actually showing their item.
///
/// <para>⚠️ A DataGrid row that leaves the screen does not leave the tree, and the grid does not
/// always say so. A row scrolled out is recycled in place — <c>UnloadRow</c> keeps it among the
/// presenter's children, still bound to the item it last showed — and that at least raises
/// <c>UnloadingRow</c>. A collection Reset recycles every row through <c>ClearElements</c>, which
/// raises nothing; the rows not needed afterwards keep their item and their old position. And the
/// FOCUSED row is not recycled at all: <c>RemoveDisplayedElement</c> clips it to nothing and
/// leaves it where it was, still bound. None of these is measured or arranged again, so a Popup
/// anchored to one stays open at the row's old position while the item's new row opens another.
/// Filter the list and the same manifest hangs twice — under the row, and where the row used to
/// be, which was the row that was clicked to open it.</para>
///
/// <para>So a popup is open only when every one of these holds: the item is expanded; the row is
/// the one the grid loaded most recently for that item; the grid has not unloaded it since; the
/// row's index is where the grid's item list has the item now — which is what a surplus row from
/// a Reset fails, its index being where the item WAS; and the row is not the parked focused row,
/// told by its empty clip. Settled after every layout.</para>
/// </summary>
public sealed class RowDetailPopups
{
    private readonly Dictionary<DataGridRow, DataGrid>      _gridOf     = [];
    private readonly Dictionary<DataGridRow, Popup[]?>      _popups     = [];
    private readonly Dictionary<object, DataGridRow>        _lastLoaded = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DataGridRow>                   _unloaded   = [];

    /// <summary>The grid's <c>LoadingRow</c>: this row now shows its item, whatever showed it before.</summary>
    public void RowLoaded(DataGrid grid, DataGridRow row)
    {
        _gridOf[row] = grid;
        _popups.TryAdd(row, null);
        _unloaded.Remove(row);
        if (row.DataContext is { } item) _lastLoaded[item] = row;
    }

    /// <summary>The grid's <c>UnloadingRow</c>: scrolled out and recycled, still in the tree.</summary>
    public void RowUnloaded(DataGridRow row)
    {
        _unloaded.Add(row);
        foreach (var popup in PopupsOf(row))
            if (popup.IsOpen) popup.SetCurrentValue(Popup.IsOpenProperty, false);
    }

    /// <summary>Settles every popup against its row's state. Called after layout.</summary>
    public void Reconcile()
    {
        foreach (var row in _popups.Keys.ToList())
        {
            var wanted = Wanted(row);
            foreach (var popup in PopupsOf(row))
                if (popup.IsOpen != wanted) popup.SetCurrentValue(Popup.IsOpenProperty, wanted);
        }
    }

    private bool Wanted(DataGridRow row)
    {
        if (row.DataContext is not IExpandableRow { IsExpanded: true } item) return false;
        if (_unloaded.Contains(row) || IsParked(row)) return false;
        if (!_lastLoaded.TryGetValue(item, out var latest) || !ReferenceEquals(latest, row)) return false;

        // The item's place in the grid's own list, against the index the row was given. A row
        // the grid silently recycled on a Reset keeps the index it had, which is no longer where
        // the item is — or the item is gone from the list altogether.
        return _gridOf.TryGetValue(row, out var grid)
            && grid.ItemsSource is IList list
            && list.IndexOf(item) is var index && index >= 0
            && row.GetIndex() == index;
    }

    /// <summary>
    /// The focused row on its way off screen: not unloaded, clipped to an empty rectangle and left
    /// bound. The clip is cleared again when the grid displays it.
    /// </summary>
    private static bool IsParked(DataGridRow row)
        => row.Clip is RectangleGeometry { Rect: { Width: <= 0, Height: <= 0 } };

    /// <summary>
    /// The row's popups, found once the row has been laid out — the cell templates that hold them
    /// are not realised before that. A row without any is remembered as such.
    /// </summary>
    private Popup[] PopupsOf(DataGridRow row)
    {
        if (_popups.TryGetValue(row, out var known) && known is not null) return known;
        var found = row.GetVisualDescendants().OfType<Popup>().ToArray();
        if (found.Length > 0 || row.Bounds.Height > 0) _popups[row] = found;
        return found;
    }
}
