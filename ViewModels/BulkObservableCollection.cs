using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EveConsole.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> whose contents can be replaced wholesale, raising one
/// change notification instead of one per item.
///
/// <para><b>⚠️ Why this is not a micro-optimisation.</b> The usual <c>Clear()</c> then
/// <c>foreach Add</c> raises N+1 notifications, and anything subscribed to the collection runs
/// N+1 times. When a subscriber rebuilds other bound collections — which is exactly what the
/// Overview does with the worklist — the cost is not N, it is N². Measured: a worklist refresh
/// held the UI thread for ten seconds with three full garbage collections inside it, every six
/// minutes, because each of the rows added to one collection re-ran a handler that refilled four
/// more, item by item, each of those bound to a panel that laid itself out again.</para>
///
/// <para>A single Reset lets a subscriber do its work once and lets a control rebuild once.</para>
///
/// <para><b>Still a UI-thread type.</b> This makes the notification cheap; it does not make the
/// collection thread-safe. Build the list off-thread, then call <see cref="ResetTo"/> on the UI
/// thread — which is the shape that was wanted anyway, because the expensive part is producing
/// the items, not storing them.</para>
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection() { }
    public BulkObservableCollection(IEnumerable<T> items) : base(items) { }

    /// <summary>
    /// Update in place where the order of the rows has not changed, so a control keeps its scroll
    /// position and redraws only the rows that actually differ.
    ///
    /// <para>⚠️ A refresh that resets the collection makes the grid rebuild every row, which the
    /// user sees as the list blanking and coming back — on a timer, repeatedly, for a list that
    /// mostly did not change. Rows are rebuilt objects each time, so reference identity cannot
    /// tell a changed row from an identical one; <paramref name="signature"/> is what does.</para>
    ///
    /// <para>Falls back to <see cref="ResetTo"/> the moment the sequence of keys differs. Rows
    /// appearing, disappearing or reordering is exactly when a control has to rebuild anyway,
    /// and a diff that handled it would cost more to get right than it saves.</para>
    /// </summary>
    public void SyncTo<TKey>(
        IReadOnlyList<T> items, Func<T, TKey> key, Func<T, string> signature)
    {
        if (items.Count != Count)
        {
            ResetTo(items);
            return;
        }

        var comparer = EqualityComparer<TKey>.Default;

        for (var i = 0; i < items.Count; i++)
            if (!comparer.Equals(key(Items[i]), key(items[i])))
            {
                ResetTo(items);
                return;
            }

        // Same rows, same order. Replace only the ones that read differently: each SetItem
        // raises a single Replace, which redraws one row and leaves the rest untouched.
        for (var i = 0; i < items.Count; i++)
            if (signature(Items[i]) != signature(items[i]))
                SetItem(i, items[i]);
    }

    /// <summary>
    /// Replace everything with <paramref name="items"/>, notifying once.
    ///
    /// <para>⚠️ Reset rather than a diff, deliberately. A diff would preserve selection, but the
    /// rows here are rebuilt objects each refresh, so nothing would match and every "kept" row
    /// would be replaced anyway — a diff would pay to discover that. Callers that need selection
    /// preserved capture it around the call, as they already had to with Clear().</para>
    /// </summary>
    public void ResetTo(IEnumerable<T> items)
    {
        // Items is the underlying list: mutating it raises nothing, which is the whole point.
        Items.Clear();
        foreach (var item in items) Items.Add(item);

        // Both property notifications matter. A binding on Count updates from the first; an
        // ItemsSource binding needs the indexer notification to re-read, and omitting it leaves
        // controls showing the old contents after a Reset.
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
