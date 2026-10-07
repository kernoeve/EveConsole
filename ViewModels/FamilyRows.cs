using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Collections;

namespace EveConsole.ViewModels;

/// <summary>
/// A grid row that opens into rows of its own beneath it — its detail, drawn in the grid rather
/// than floated over it.
///
/// <para>⚠️ Why rows, and why every one the same height: a DataGrid prices the rows it has not
/// drawn from those it has, and any spread in row height turns scrolling up into a fight — the
/// view flickers backwards and the scrollbar resizes. RowDetails did it, and so does a drawer
/// inside the cell, capped or not; detail lines as rows of one height score clean
/// (tools/gridsim). The view pins each grid's row height — see WorklistView.PinRowHeight.</para>
///
/// <para>⚠️ A child row carries its PARENT's data, so every column sorts and groups it with the
/// parent; <see cref="FamilyOrder"/> then <see cref="ChildOrder"/>, kept last in the sort, keep
/// the family together and in order. The view hides every cell of a child row except those in
/// columns marked to show child content (CellStyleClasses "child").</para>
/// </summary>
public interface IFamilyRow
{
    /// <summary>One of another row's detail rows.</summary>
    bool IsChild { get; }

    /// <summary>The parent's place in the list, shared by its children, so the family sorts as one.</summary>
    int FamilyOrder { get; }

    /// <summary>0 on the parent; its children 1, 2, … in the order they read.</summary>
    int ChildOrder { get; }
}

public static class FamilyRows
{
    public const string FamilyOrder = nameof(IFamilyRow.FamilyOrder);
    public const string ChildOrder  = nameof(IFamilyRow.ChildOrder);

    /// <summary>
    /// Keeps <paramref name="head"/> first in the view's sort (the group key, where there is one)
    /// and the family keys last, whatever a header click puts between them.
    ///
    /// <para>A header click replaces the sort wholesale, which raises this again; the guard stops
    /// the edits made here from re-entering.</para>
    /// </summary>
    public static void Pin(DataGridCollectionView view, string? head, ref bool busy)
    {
        if (busy) return;
        var sorts = view.SortDescriptions;

        var headOk = head is null || (sorts.Count > 0 && sorts[0].PropertyPath == head);
        var tailOk = sorts.Count >= 2
                     && sorts[^2].PropertyPath == FamilyOrder
                     && sorts[^1].PropertyPath == ChildOrder;
        if (headOk && tailOk) return;

        busy = true;
        try
        {
            if (!headOk) sorts.Insert(0, DataGridSortDescription.FromPath(head!));

            for (var i = sorts.Count - 1; i >= 0; i--)
                if (sorts[i].PropertyPath is FamilyOrder or ChildOrder)
                    sorts.RemoveAt(i);
            sorts.Add(DataGridSortDescription.FromPath(FamilyOrder, ListSortDirection.Ascending));
            sorts.Add(DataGridSortDescription.FromPath(ChildOrder,  ListSortDirection.Ascending));
        }
        finally { busy = false; }
    }

    /// <summary>
    /// A paragraph as its sentences, a row each — a paragraph in one row would be cut off. Split
    /// on a full stop, question or exclamation mark followed by a space, and after a full-width
    /// stop, which takes none; so "1.5" mid-number stays whole, and Japanese and Chinese split
    /// where they should.
    /// </summary>
    public static IEnumerable<string> Sentences(string text)
    {
        foreach (var s in System.Text.RegularExpressions.Regex.Split(text.Trim(), @"(?<=[.!?])\s+|(?<=[。！？])"))
            if (s.Trim() is { Length: > 0 } sentence) yield return sentence;
    }

    /// <summary>The rows, each followed by its children when it is open.</summary>
    public static IEnumerable<T> WithChildren<T>(IEnumerable<T> rows, System.Func<T, bool> open,
                                                 System.Func<T, IReadOnlyList<T>> children)
    {
        foreach (var row in rows)
        {
            yield return row;
            if (!open(row)) continue;
            foreach (var child in children(row)) yield return child;
        }
    }

    /// <summary>
    /// Puts a row's children into the list under it, or takes them out.
    ///
    /// <para>⚠️ One at a time rather than a Reset. A Reset recycles every row and the reader who
    /// opened something halfway down would be thrown back to the top. Children are tens of rows.</para>
    /// </summary>
    public static void Toggle<T>(IList<T> rows, T row, bool open, IReadOnlyList<T> children)
    {
        var at = rows.IndexOf(row);
        if (at < 0 || children.Count == 0) return;

        if (open)
        {
            if (rows.Contains(children[0])) return;
            for (var i = 0; i < children.Count; i++) rows.Insert(at + 1 + i, children[i]);
        }
        else
        {
            foreach (var child in children) rows.Remove(child);
        }
    }
}
