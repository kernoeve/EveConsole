using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;
using Avalonia.Interactivity;

namespace EveConsole.Views;

public partial class ItemBrowserView : ReactiveUserControl<ItemBrowserViewModel>
{
    public ItemBrowserView()
    {
        InitializeComponent();
        SetupTreeTemplates();
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
