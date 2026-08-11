using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using EveConsole.ViewModels;
using Avalonia.Interactivity;

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

    private void OnDetailTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is TabControl tc &&
            tc.SelectedItem is TabItem { Header: "Price History" } &&
            DataContext is ItemBrowserViewModel vm)
        {
            _ = vm.LoadPriceHistoryAsync();
        }
    }

    private void SetupTreeTemplates()
    {
        var tree    = this.FindControl<TreeView>("ItemTree")!;
        var groupFg = new SolidColorBrush(Color.Parse("#999aaa"));
        var typeFg  = new SolidColorBrush(Color.Parse("#c8a84b"));

        tree.DataTemplates.Add(new FuncTreeDataTemplate<MarketGroupNode>(
            (node, _) => new TextBlock
            {
                Text       = node.Name,
                Foreground = groupFg,
                FontSize   = 12,
                Padding    = new Avalonia.Thickness(2, 1)
            },
            node => node.Children
        ));

        tree.DataTemplates.Add(new FuncTreeDataTemplate<TypeNode>(
            (node, _) => new TextBlock
            {
                Text       = node.Name,
                Foreground = typeFg,
                FontSize   = 12,
                Padding    = new Avalonia.Thickness(2, 1)
            },
            _ => Array.Empty<object>()
        ));
    }
}
