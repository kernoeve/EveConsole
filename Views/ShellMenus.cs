using Avalonia.Controls;
using EveConsole.Services;

namespace EveConsole.Views;

/// <summary>
/// The theme and UI scale menus, for the main window's bar and for the same controls on a panel.
/// Built fresh each time one opens, because the tick has to show what is on now: Settings can
/// change either too, and a menu made once would go on ticking whatever was on when it was made.
/// </summary>
internal static class ShellMenus
{
    /// <param name="editorOwner">The window the theme editor belongs to; null for none in particular.</param>
    public static MenuFlyout Theme(PlacementMode placement, Window? editorOwner)
    {
        var menu = new MenuFlyout { Placement = placement };
        foreach (var choice in ThemeService.All)
        {
            var item = new MenuItem
            {
                Header     = choice.Name,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked  = choice.Key == ThemeService.Current,
            };

            // Captured, not read off the sender: a MenuItem's Click gives back the item, and
            // recovering the key from its header would break the moment one was renamed.
            var key = choice.Key;
            item.Click += (_, _) => ThemeService.Apply(key);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = EveConsole.Localization.SettingsText.EditThemesButton };
        edit.Click += (_, _) => ThemeEditorWindow.Open(editorOwner);
        menu.Items.Add(edit);
        return menu;
    }

    public static MenuFlyout UiScale(PlacementMode placement)
    {
        var menu = new MenuFlyout { Placement = placement };
        foreach (var choice in UiScaleChoice.All)
        {
            var item = new MenuItem
            {
                Header     = choice.Name,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked  = Math.Abs(choice.Scale - UiScaleService.Scale) < 0.001,
            };
            var scale = choice.Scale;
            item.Click += (_, _) => UiScaleService.Apply(scale);
            menu.Items.Add(item);
        }
        return menu;
    }
}
