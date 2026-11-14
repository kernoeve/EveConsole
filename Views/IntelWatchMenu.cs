using Avalonia.Controls;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services.Intel;

namespace EveConsole.Views;

/// <summary>
/// "Always alert" and "Ignore" for one pilot, in any intel view's right-click menu: ticked as the watch list
/// has them, and choosing the ticked one takes the pilot off the list. Written off the UI thread; every view
/// shows the change once the snapshot has read the list again, at once on this PC.
/// </summary>
public static class IntelWatchMenu
{
    public static IEnumerable<Control> ForPilot(IntelWatchList list, IntelWatchSet set, long characterId, string name)
    {
        if (characterId <= 0) yield break;
        var mode = set.ModeOf(IntelWatchKind.Pilot, characterId);
        yield return new Separator();
        yield return Item(IntelText.MenuWatchAlways, mode == IntelWatchMode.Always,
            () => mode == IntelWatchMode.Always ? list.RemoveAsync(IntelWatchKind.Pilot, characterId)
                                                : list.SetAsync(IntelWatchKind.Pilot, characterId, name, IntelWatchMode.Always));
        yield return Item(IntelText.MenuWatchIgnore, mode == IntelWatchMode.Ignore,
            () => mode == IntelWatchMode.Ignore ? list.RemoveAsync(IntelWatchKind.Pilot, characterId)
                                                : list.SetAsync(IntelWatchKind.Pilot, characterId, name, IntelWatchMode.Ignore));
    }

    private static MenuItem Item(string header, bool on, Func<Task> change)
    {
        var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
        item.Click += (_, _) => _ = Task.Run(async () =>
        {
            // A change that cannot be written is not made: the next menu shows the ticks as they still are,
            // and the Watch list tab says why when it is the one writing.
            try { await change(); }
            catch { }
        });
        return item;
    }
}
