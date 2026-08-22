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
