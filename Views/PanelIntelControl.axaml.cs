using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>Live intel on a panel, as a list of systems. See <see cref="PanelIntelVm"/>.</summary>
public partial class PanelIntelControl : UserControl
{
    public PanelIntelControl() => InitializeComponent();

    /// <summary>False when the list drops down from a single line, which starts and stops the view model itself.</summary>
    public bool ManagesLifetime { get; init; } = true;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ManagesLifetime) (DataContext as PanelIntelVm)?.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (ManagesLifetime) (DataContext as PanelIntelVm)?.Dispose();
    }

    /// <summary>A system's line opens and closes its details; a link, a pilot line or an event line under it does
    /// its own thing.</summary>
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && (v.FindAncestorOfType<Button>(includeSelf: true) is not null
                                     || v.FindAncestorOfType<IntelEventRow>(includeSelf: true) is not null
                                     || v.FindAncestorOfType<IntelHostileLine>(includeSelf: true) is not null)) return;
        if ((sender as Control)?.DataContext is IntelSystemLineVm row) row.Toggle();
    }

    /// <summary>The system's line: open its page, show it on the map, copy its name, open it in the Intel tool.</summary>
    private void OnRowContext(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Visual v && (v.FindAncestorOfType<IntelEventRow>(includeSelf: true) is not null
                                     || v.FindAncestorOfType<IntelHostileLine>(includeSelf: true) is not null)) return;
        if ((sender as Control)?.DataContext is not IntelSystemLineVm row) return;
        e.Handled = true;
        // A menu from a panel that never takes focus may not close when clicked away from.
        (TopLevel.GetTopLevel(this) as ClientPanelWindow)?.TakeFocus();
        var items = new List<Control> { Item(IntelText.MenuOpenSystem, row.OpenSystem) };
        if (row.CanShowOnMap) items.Add(Item(IntelText.MenuShowOnMap, row.ShowOnMap));
        items.Add(Item(IntelText.MenuCopySystem, () => _ = CopyAsync(row.SystemName)));
        if (row.CanOpenInTool) items.Add(Item(IntelText.MenuOpenInTool, row.OpenInTool));
        new ContextMenu { ItemsSource = items }.Open(sender as Control ?? this);
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
