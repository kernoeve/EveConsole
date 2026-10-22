using System.Collections.ObjectModel;
using System.Reactive.Linq;
using EveConsole.Localization;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// A list of tick boxes shown as one dropdown: the button names what is ticked, the panel under
/// it holds an All box and the boxes themselves. See <c>Views/MultiChoiceDropDown</c>.
/// </summary>
public sealed class MultiChoice : ReactiveObject
{
    /// <summary>More ticked than this and the button counts them instead of naming them.</summary>
    private const int NamedAtMost = 3;

    public MultiChoice(IEnumerable<CategoryChoice> items)
    {
        foreach (var item in items)
        {
            Items.Add(item);
            item.WhenAnyValue(i => i.Selected).Skip(1).Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(AllSelected));
                this.RaisePropertyChanged(nameof(Summary));
                SelectionChanged?.Invoke();
            });
        }
    }

    public ObservableCollection<CategoryChoice> Items { get; } = [];

    /// <summary>Any box ticked or unticked, All included.</summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// True with every box ticked, false with none, null (the box half-filled) with some.
    /// Setting it ticks or clears them all.
    /// </summary>
    public bool? AllSelected
    {
        get => Items.Count > 0 && Items.All(i => i.Selected) ? true : Items.Any(i => i.Selected) ? null : false;
        set
        {
            // A click on a half-filled All box ticks everything.
            var on = value ?? true;
            foreach (var i in Items) i.Selected = on;
        }
    }

    /// <summary>What the button says: All, None, All but the one left out, the boxes ticked, or how many.</summary>
    public string Summary
    {
        get
        {
            var on = Items.Where(i => i.Selected).ToList();
            if (on.Count == 0)           return AlarmsText.None;
            if (on.Count == Items.Count) return AlarmsText.All;
            if (on.Count == Items.Count - 1 && Items.Count > 2)
                return string.Format(CommonText.AllBut, Items.First(i => !i.Selected).Title);
            return on.Count <= NamedAtMost
                ? string.Join(", ", on.Select(i => i.Title))
                : string.Format(CommonText.CountSelected, on.Count);
        }
    }

    /// <summary>The keys ticked, in the list's order.</summary>
    public IEnumerable<string> SelectedKeys => Items.Where(i => i.Selected).Select(i => i.Key);
}
