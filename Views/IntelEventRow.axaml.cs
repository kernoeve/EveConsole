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
        if ((sender as Control)?.DataContext is IntelPictureVm picture && DataContext is IntelEventRowVm row)
            row.OpenPicture(picture);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not IntelEventRowVm row) return;
        e.Handled = true;
        // A menu from a panel that never takes focus may not close when clicked away from.
        (TopLevel.GetTopLevel(this) as ClientPanelWindow)?.TakeFocus();

        var items = new List<Control>();
        if (row.CanShowOnMap) items.Add(Item(IntelText.MenuShowOnMap, row.ShowOnMap));
        items.Add(Item(IntelText.MenuCopySystem, () => _ = CopyAsync(row.SystemName)));
        if (row.CanOpenInTool) items.Add(Item(IntelText.MenuOpenInTool, row.OpenInTool));
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
