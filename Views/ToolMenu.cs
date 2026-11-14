using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// The left navigation's sections as a menu, a submenu for each. What a tool does when picked is
/// the caller's; Shift held while clicking asks for another copy of it.
/// </summary>
public static class ToolMenu
{
    /// <param name="isOpen">Whether a tool gets the dot that says it's open.</param>
    /// <param name="open">Opens a tool: its id, and whether Shift was held (another copy).</param>
    public static ContextMenu Build(MainWindowViewModel main, Func<NavItem, bool> isOpen, Action<string, bool> open)
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var group in main.NavGroups)
        {
            var section = new MenuItem { Header = group.Title };
            foreach (var item in group.Items)
            {
                var entry = new MenuItem
                {
                    Header = item.Title,
                    Icon = isOpen(item) ? new Ellipse { Width = 5, Height = 5, [!Shape.FillProperty] = new DynamicResourceExtension("AccentBrush") } : null,
                };
                var toolId = item.ToolId;
                var shift = ShiftWatch.On(entry);
                entry.Click += (_, _) => open(toolId, shift.Take());
                section.Items.Add(entry);
            }
            menu.Items.Add(section);
        }
        return menu;
    }
}

/// <summary>
/// Whether Shift was held for the click on a control. A click's own event doesn't say, so the
/// press and the release that make it are watched, before anything else handles them. A click
/// from the keyboard counts as without.
/// </summary>
public sealed class ShiftWatch
{
    private bool _held;

    public static ShiftWatch On(InputElement control)
    {
        var watch = new ShiftWatch();
        control.AddHandler(InputElement.PointerPressedEvent, (_, e) => watch._held = e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        control.AddHandler(InputElement.PointerReleasedEvent, (_, e) => watch._held |= e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        control.AddHandler(InputElement.KeyDownEvent, (_, _) => watch._held = false,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        return watch;
    }

    /// <summary>Whether Shift was held, then forgets it for the next click.</summary>
    public bool Take()
    {
        var held = _held;
        _held = false;
        return held;
    }
}
