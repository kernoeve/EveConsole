using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using EveConsole.ViewModels;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EveConsole.Localization;

namespace EveConsole.Views;

public partial class ItemBrowserView : ReactiveUserControl<ItemBrowserViewModel>
{
    public ItemBrowserView()
    {
        InitializeComponent();
        SetupTreeTemplates();
        WireConditionalTabFallback();
    }

    /// <summary>
    /// Several detail tabs only exist for some items — LP Store, Required For, Price
    /// History. Selecting one and then picking an item that lacks it leaves the selection
    /// on a hidden tab, which reads as an empty pane with no tab highlighted. Fall back to
    /// the first tab still on screen, which is Description.
    ///
    /// Driven off each tab's own IsVisible rather than off any single view-model flag, so
    /// a conditional tab added later is covered without touching this.
    /// </summary>
    private void WireConditionalTabFallback()
    {
        var tabs = this.FindControl<TabControl>("ItemDetailTabs");
        if (tabs is null) return;

        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            var captured = tab;
            captured.GetObservable(Visual.IsVisibleProperty).Subscribe(visible =>
            {
                if (visible || !ReferenceEquals(tabs.SelectedItem, captured)) return;

                // Deferred: this fires mid-way through applying the new item's bindings,
                // and a selection set during that pass gets overwritten by the rest of it.
                Dispatcher.UIThread.Post(() =>
                {
                    if (captured.IsVisible || !ReferenceEquals(tabs.SelectedItem, captured)) return;
                    tabs.SelectedItem = tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.IsVisible);
                });
            });
        }
    }

    /// <summary>A market order's location — NPC station or player structure, decided by which
    /// lookup named it rather than by the id.</summary>
    private void OnOpenLpCorp(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as LpOfferVm)?.OpenCorporation();

    private void OnOpenOrderLocation(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as OrderRowVm)?.OpenLocation();

    // The Assets tab's three links: where the stack is, who holds it, and the system.
    private void OnOpenAssetLocation(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as ItemAssetRowVm)?.OpenLocation();

    private void OnOpenAssetOwner(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as ItemAssetRowVm)?.OpenOwner();

    private void OnOpenAssetSystem(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as ItemAssetRowVm)?.OpenSystem();

    private void OnDetailTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Only the detail tabs' own selection: SelectionChanged bubbles up from every list inside
        // them — the history's Chart/Grid tabs, its period and region pickers — and each of those
        // reloaded the history again (the region picker already reloads it; the period filters
        // what is loaded).
        if (!ReferenceEquals(e.Source, sender)) return;

        // ⚠️ By the tab itself, not its header: the header is in the interface language.
        if (sender is TabControl tc &&
            ReferenceEquals(tc.SelectedItem, PriceHistoryTab) &&
            DataContext is ItemBrowserViewModel vm)
        {
            _ = vm.LoadPriceHistoryAsync();
        }
    }

    /// <summary>A tree line in a theme colour, followed when the theme changes. ⚠️ Not a fixed
    /// colour: the tree's grey and gold were the dark theme's, and faded out on Light.</summary>
    private static TextBlock Line(string text, string brushKey, FontWeight weight = FontWeight.Normal)
    {
        var line = new TextBlock { Text = text, FontSize = 12, FontWeight = weight, Padding = new Avalonia.Thickness(2, 1) };
        line.Bind(TextBlock.ForegroundProperty, line.GetResourceObservable(brushKey));
        return line;
    }

    private void SetupTreeTemplates()
    {
        var tree = this.FindControl<TreeView>("ItemTree")!;

        tree.DataTemplates.Add(new FuncTreeDataTemplate<MarketGroupNode>(
            (node, _) => Line(node.Name, "TextSecondaryBrush"),
            node => node.Children
        ));

        tree.DataTemplates.Add(new FuncTreeDataTemplate<TypeNode>(
            (node, _) => Line(node.Name, "AccentBrush"),
            _ => Array.Empty<object>()
        ));

        this.FindControl<TreeView>("QuickbarTree")!.DataTemplates.Add(new FuncTreeDataTemplate<QuickbarNodeVm>(
            (node, _) => node.IsFolder ? Line(node.Name, "TextSecondaryBrush", FontWeight.SemiBold)
                                       : Line(node.Name, "AccentBrush"),
            node => node.Children
        ));
    }

    // ── Right-click: the quickbar and standing buy orders ───────────────────

    private ItemBrowserViewModel? Vm => DataContext as ItemBrowserViewModel;

    private static object? Under(ContextRequestedEventArgs e)
        => (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext
        ?? (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext;

    private static MenuItem Item(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>"Add to Quickbar", straight to the top level while there are no folders, or to
    /// a folder picked from its submenu; "Remove from Quickbar" for one already on it.</summary>
    private MenuItem QuickbarItem(ItemBrowserViewModel vm, int typeId)
    {
        if (vm.QuickbarHas(typeId))
            return Item(AssetsText.MenuRemoveFromQuickbar, () => _ = vm.RemoveFromQuickbarAsync(typeId));
        var places = vm.QuickbarDestinations();
        if (places.Count == 1) return Item(AssetsText.MenuAddToQuickbar, () => _ = vm.AddToQuickbarAsync(typeId));
        return new MenuItem
        {
            Header      = AssetsText.MenuAddToQuickbar,
            ItemsSource = places.Select(p => Item(p.Label, () => _ = vm.AddToQuickbarAsync(typeId, p.FolderId))).ToList(),
        };
    }

    private void OpenItemMenu(Control at, int typeId, IEnumerable<Control> extra)
    {
        if (Vm is not { } vm) return;
        var items = new List<Control>(extra) { QuickbarItem(vm, typeId) };
        if (vm.AddToStandingBuyOrders is { } add)
        {
            items.Add(new Separator());
            items.Add(Item(MarketText.MenuAddToStandingBuyOrders, () => add(typeId)));
        }
        new ContextMenu { ItemsSource = items }.Open(at);
    }

    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Under(e) is not TypeNode node || e.Source is not Control at) return;
        OpenItemMenu(at, node.TypeId, []);
        e.Handled = true;
    }

    private void OnResultsContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Under(e) is not TypeSearchResult result || e.Source is not Control at) return;
        OpenItemMenu(at, result.TypeId, []);
        e.Handled = true;
    }

    /// <summary>On an item: move it, take it off; on a folder: a folder inside it, rename, move,
    /// delete; on the empty space: a new folder.</summary>
    private void OnQuickbarContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is not { } vm || e.Source is not Control at) return;
        var node = Under(e) as QuickbarNodeVm;
        var items = new List<Control>();

        MenuItem MoveTo(IReadOnlyList<(string? FolderId, string Label)> places, Func<string?, Task> move) => new()
        {
            Header      = AssetsText.MenuMoveToFolder,
            ItemsSource = places.Select(p => Item(p.Label, () => _ = move(p.FolderId))).ToList(),
        };

        if (node is { TypeId: int typeId })
        {
            items.Add(MoveTo(vm.QuickbarDestinations(), into => vm.MoveQuickbarItemAsync(typeId, into)));
            items.Add(Item(AssetsText.MenuRemoveFromQuickbar, () => _ = vm.RemoveFromQuickbarAsync(typeId)));
            if (vm.AddToStandingBuyOrders is { } add)
            {
                items.Add(new Separator());
                items.Add(Item(MarketText.MenuAddToStandingBuyOrders, () => add(typeId)));
            }
        }
        else if (node is { FolderId: { } folderId })
        {
            items.Add(Item(AssetsText.MenuNewFolder, () => _ = NewFolderAsync(vm, folderId)));
            items.Add(Item(AssetsText.MenuRenameFolder, () => _ = RenameFolderAsync(vm, folderId, node.Name)));
            items.Add(MoveTo(vm.QuickbarDestinations(folderId), into => vm.MoveQuickbarFolderAsync(folderId, into)));
            items.Add(new Separator());
            items.Add(Item(AssetsText.MenuDeleteFolder, () => _ = vm.DeleteQuickbarFolderAsync(folderId)));
        }
        else
        {
            items.Add(Item(AssetsText.MenuNewFolder, () => _ = NewFolderAsync(vm, null)));
        }

        new ContextMenu { ItemsSource = items }.Open(at);
        e.Handled = true;
    }

    private async Task<string?> AskFolderNameAsync(string title, string initial = "")
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;
        var typed = await new TextPromptDialog(title, AssetsText.LabelFolderName, AssetsText.HintFolderName, initial)
            .ShowDialog<string?>(owner);
        return string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();
    }

    private async Task NewFolderAsync(ItemBrowserViewModel vm, string? parentId)
    {
        if (await AskFolderNameAsync(AssetsText.TitleNewFolder) is { } name)
            await vm.NewQuickbarFolderAsync(name, parentId);
    }

    private async Task RenameFolderAsync(ItemBrowserViewModel vm, string folderId, string current)
    {
        if (await AskFolderNameAsync(AssetsText.TitleRenameFolder, current) is { } name)
            await vm.RenameQuickbarFolderAsync(folderId, name);
    }
}
