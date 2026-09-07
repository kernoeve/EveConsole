using System.Collections.Generic;
using System.Linq;

namespace EveConsole.ViewModels;

/// <summary>
/// A grid row that can be opened to show more underneath it.
///
/// <para>⚠️ The open/closed flag lives on the ITEM, never on the DataGridRow: the grid recycles
/// rows as you scroll, so a flag held on the row would follow whichever item landed in it next.
/// <see cref="ExpandKey"/> is the other half of that — every row is a rebuilt object after a
/// refresh, so reference identity cannot say whether this is the row somebody opened.</para>
/// </summary>
public interface IExpandableRow
{
    /// <summary>Identifies the row across a rebuild — stable for the same underlying work.</summary>
    string ExpandKey { get; }

    bool IsExpanded { get; set; }
}

public static class RowExpansion
{
    /// <summary>
    /// Carries what was open forward onto a freshly built set of rows.
    ///
    /// <para>⚠️ A refresh replaces every row object, so without this the timer closes whatever
    /// the reader had opened, several minutes into reading it. Matching is by key rather than by
    /// position: the rebuild re-sorts, and reopening whatever now sits at the same index would
    /// open the wrong rows.</para>
    /// </summary>
    public static void Carry<T>(IEnumerable<T>? before, IEnumerable<T> after) where T : IExpandableRow
    {
        if (before is null) return;

        var open = before.Where(r => r.IsExpanded).Select(r => r.ExpandKey).ToHashSet();
        if (open.Count == 0) return;

        foreach (var row in after)
            if (open.Contains(row.ExpandKey)) row.IsExpanded = true;
    }
}
