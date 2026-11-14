using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// One intel event, as the panel, its popup and the Intel tool show it. See <see cref="IntelEventRowVm"/>.
/// A system opens its page, a pilot theirs, a hull the Item Browser; right-click offers the map,
/// the system's name to copy, and the Intel tool. Nothing here reaches the game client.
/// </summary>
public partial class IntelEventRow : UserControl
{
    public IntelEventRow()
    {
        InitializeComponent();
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
    }

    private void OnOpenPicture(object? sender, RoutedEventArgs e)
    {
        var picture = (sender as Control)?.DataContext switch
        {
            IntelPictureVm p => p,
            IntelTokenVm t => t.Source,
            _ => null,
        };
        if (picture is not null && DataContext is IntelEventRowVm row) row.OpenPicture(picture);
        e.Handled = true;
    }

    /// <summary>A kill's line, clicked anywhere but on one of its links, opens the killmail.</summary>
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (OnLink(e.Source) || DataContext is not IntelEventRowVm { CanOpenKill: true } row) return;
        e.Handled = true;
        row.OpenKill();
    }

    /// <summary>Whether a click landed on a link (a system, a pilot, a hull), which does its own thing.</summary>
    internal static bool OnLink(object? source) =>
        source is Avalonia.Visual v && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(v, includeSelf: true) is not null;

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not IntelEventRowVm row) return;
        e.Handled = true;
        // A menu from a panel that never takes focus may not close when clicked away from.
        (TopLevel.GetTopLevel(this) as ClientPanelWindow)?.TakeFocus();

        var items = new List<Control>();
        if (row.CanOpenKill) items.Add(Item(IntelText.MenuOpenKill, row.OpenKill));
        if (row.CanShowOnMap) items.Add(Item(IntelText.MenuShowOnMap, row.ShowOnMap));
        items.Add(Item(IntelText.MenuCopySystem, () => _ = CopyAsync(row.SystemName)));
        if (row.CanOpenInTool) items.Add(Item(IntelText.MenuOpenInTool, row.OpenInTool));
        // On a pilot: "Always alert" and "Ignore" for them.
        var picked = (e.Source as Avalonia.StyledElement)?.DataContext switch
        {
            IntelPictureVm p => p,
            IntelTokenVm t => t.Source,
            _ => null,
        };
        if (picked is { IsPilot: true, Id: > 0 } pilot && row.Actions.Watch is { } watch)
        {
            items.Add(new Separator());
            items.Add(Item(IntelText.MenuOpenPilot, () => row.OpenPicture(pilot)));
            items.AddRange(IntelWatchMenu.ForPilot(watch, row.WatchSet, pilot.Id, pilot.Text));
        }
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    private static MenuItem Item(string header, Action act)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => act();
        return item;
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}
