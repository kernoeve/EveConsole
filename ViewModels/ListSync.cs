using System.Collections.ObjectModel;

namespace EveConsole.ViewModels;

/// <summary>
/// Brings a list of choices in line with a fresh reading of them, changing only what changed.
/// </summary>
/// <remarks>
/// <para>⚠️ Never cleared and refilled. A ComboBox whose items are cleared loses its selection —
/// the box goes blank, and a two-way binding writes that null back — so a list refreshed because a
/// character was added must not drop the one the user had picked. An entry still wanted keeps its
/// own instance, which is what a selection points at; only entries gone are removed and only new
/// ones inserted, each where the fresh order puts it.</para>
/// </remarks>
internal static class ListSync
{
    public static void Sync<T, TKey>(IList<T> target, IReadOnlyList<T> wanted, Func<T, TKey> key)
        where TKey : notnull
    {
        var keep = new HashSet<TKey>(wanted.Select(key));
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(key(target[i]))) target.RemoveAt(i);

        for (var i = 0; i < wanted.Count; i++)
        {
            var k  = key(wanted[i]);
            var at = -1;
            for (var j = i; j < target.Count; j++)
                if (EqualityComparer<TKey>.Default.Equals(key(target[j]), k)) { at = j; break; }

            if (at == i) continue;
            if (at < 0) { target.Insert(i, wanted[i]); continue; }

            // Out of place: moved, not removed and added, so it stays the same item.
            if (target is ObservableCollection<T> oc) oc.Move(at, i);
            else { var item = target[at]; target.RemoveAt(at); target.Insert(i, item); }
        }
    }
}
