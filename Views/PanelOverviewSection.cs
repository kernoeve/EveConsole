using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// One of the Overview's sections on a panel — orders, sales, notifications, news, killmails — as
/// the Overview shows it, with its title and links. An Overview of its own that shows only this
/// section, so it reads only what the section needs, refreshing as the Overview does.
///
/// <para>Without the section's own card, and with nothing behind it: the panel it's on is its
/// background. A Card panel gives it the Overview's look, and any other panel style its own.</para>
/// </summary>
public sealed class PanelOverviewSection : Panel
{
    private readonly MainWindowViewModel _main;
    private readonly string _section;
    private readonly CharacterView _view = new() { ShowsToolbar = false };
    private OverviewViewModel? _overview;

    public PanelOverviewSection(MainWindowViewModel main, string section)
    {
        _main = main;
        _section = section;
        WithoutOwnCard(_view);
        Children.Add(_view);

        // Clicked into, its grids can be scrolled with the keyboard and its menus close when clicked away from.
        AddHandler(PointerPressedEvent, (_, _) => (TopLevel.GetTopLevel(this) as ClientPanelWindow)?.TakeFocus(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// An Overview with its sections' cards taken off, and its page colour, so what's round it shows:
    /// on a panel, the panel is the card. Their titles, links and contents stay as they are.
    ///
    /// <para>⚠️ Set above a binding's priority, not as a plain value. Those colours are theme
    /// bindings: a value set over one, even with the binding cleared first, lasted only until the view
    /// went on screen, when the binding looked its colour up again and put it back.</para>
    /// </summary>
    public static void WithoutOwnCard(CharacterView view)
    {
        Transparent(view, TemplatedControl.BackgroundProperty);
        foreach (var box in view.GetLogicalDescendants().OfType<Border>())
        {
            if (box.Name?.StartsWith("Section", StringComparison.Ordinal) != true) continue;
            Transparent(box, Border.BackgroundProperty);
            Transparent(box, Border.BorderBrushProperty);
            box.SetValue(Border.BorderThicknessProperty, default(Thickness), BindingPriority.Animation);
            box.SetValue(Decorator.PaddingProperty, default(Thickness), BindingPriority.Animation);
        }

        // Not the Overview's note about whose data it shows: that's about the whole page, and on a
        // panel holding one section it's only in the way.
        foreach (var note in view.GetLogicalDescendants().OfType<Border>().Where(b => b.Child is TextBlock { Text: var text } && text == Localization.OverviewText.OverviewDataReflectsCharactersNote))
            note.SetValue(Visual.IsVisibleProperty, false, BindingPriority.Animation);
    }

    private static void Transparent(AvaloniaObject on, StyledProperty<IBrush?> property) =>
        on.SetValue(property, Brushes.Transparent, BindingPriority.Animation);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _overview ??= _main.NewSectionOverview(_section);
        _view.DataContext = _overview;
    }

    /// <summary>Gone with the panel: nothing keeps refreshing it, as with a container's Overview when its tab closes.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _view.DataContext = null;
        _overview?.Dispose();
        _overview = null;
    }
}
