using System.Globalization;
using Avalonia.Collections;
using EveConsole.Localization;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// A results list narrowed to the items whose name contains what was typed — Industry and Trade
/// Opportunities. Filters the view the grid shows, as it is typed; the results themselves, and
/// anything worked out from all of them, are untouched, so nothing has to be calculated again.
///
/// <para>Held by the tool's view model, which outlives the tab, and not remembered between
/// sessions: a filter left in from last time would read as items gone missing.</para>
/// </summary>
public sealed class ItemNameFilter<T> : ReactiveObject
{
    private readonly DataGridCollectionView _view;
    private readonly IReadOnlyCollection<T> _all;

    public ItemNameFilter(DataGridCollectionView view, IReadOnlyCollection<T> all, Func<T, string> name)
    {
        _view = view;
        _all  = all;
        view.Filter = o => o is T row && ItemNameFilter.Matches(name(row), _text);
    }

    private string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            if (_text == (value ?? "")) return;
            this.RaiseAndSetIfChanged(ref _text, value ?? "");
            _view.Refresh();
            Update();
        }
    }

    private string _shown = "";
    /// <summary>"Showing 12 of 840" while a filter is typed; empty otherwise.</summary>
    public string Shown { get => _shown; private set => this.RaiseAndSetIfChanged(ref _shown, value); }

    /// <summary>To be called when the results change, so the count follows them.</summary>
    public void Update()
        => Shown = _text.Trim().Length == 0 || _all.Count == 0
            ? ""
            : string.Format(CommonText.ItemNameFilterShowing, _view.Count, _all.Count);
}

public static class ItemNameFilter
{
    /// <summary>The name contains the filter: any case, accents optional ("Ö" matches "o").</summary>
    public static bool Matches(string name, string filter)
    {
        var f = filter.Trim();
        return f.Length == 0
            || CultureInfo.CurrentCulture.CompareInfo.IndexOf(name, f,
                   CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }
}
