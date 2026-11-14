using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// One hostile pilot in a system's details, on the panel, its popup and the Intel tool's Board. See
/// <see cref="IntelHostilePilotVm"/>. A name opens its page; right-click offers the pilot and the watch list.
/// Nothing here reaches a game client.
/// </summary>
public partial class IntelHostileLine : UserControl
{
    /// <summary>The pictures' size: about 20 on the panel, 24 on the Board.</summary>
    public static readonly StyledProperty<double> PictureSizeProperty =
        AvaloniaProperty.Register<IntelHostileLine, double>(nameof(PictureSize), 20);

    public double PictureSize
    {
        get => GetValue(PictureSizeProperty);
        set => SetValue(PictureSizeProperty, value);
    }

    public IntelHostileLine()
    {
        InitializeComponent();
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
    }

    private void OnOpenPicture(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is IntelPictureVm picture && DataContext is IntelHostilePilotVm pilot)
            pilot.Open(picture);
        e.Handled = true;
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not IntelHostilePilotVm pilot) return;
        e.Handled = true;
        // A menu from a panel that never takes focus may not close when clicked away from.
        (TopLevel.GetTopLevel(this) as ClientPanelWindow)?.TakeFocus();
        var open = new MenuItem { Header = IntelText.MenuOpenPilot };
        open.Click += (_, _) => pilot.Open(pilot.Pilot);
        var items = new List<Control> { open };
        if (pilot.Actions.Watch is { } watch) items.AddRange(IntelWatchMenu.ForPilot(watch, pilot.WatchSet, pilot.CharacterId, pilot.Name));
        new ContextMenu { ItemsSource = items }.Open(this);
    }
}
