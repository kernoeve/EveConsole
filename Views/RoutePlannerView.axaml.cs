using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class RoutePlannerView : UserControl
{
    public RoutePlannerView() => InitializeComponent();

    /// <summary>
    /// Set destination: who is logged in is read at the click, so the choice follows logins and
    /// logouts. Nobody, or one: straight away. More: a drop-down of All online, then each name.
    /// </summary>
    private async void OnSetDestinationClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RouteTabViewModel vm || sender is not Button button) return;
        var online = await vm.OnlineCharactersAsync();
        if (online.Count <= 1)
        {
            await vm.SendToAsync(online);
            return;
        }

        var items = new List<object>();
        var all = new MenuItem { Header = MapText.RouteAllOnline };
        all.Click += async (_, _) => await vm.SendToAsync(online);
        items.Add(all);
        items.Add(new Separator());
        foreach (var c in online)
        {
            var item = new MenuItem { Header = c.Label };
            item.Click += async (_, _) => await vm.SendToAsync([c]);
            items.Add(item);
        }
        new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedRight }.ShowAt(button);
    }

    /// <summary>
    /// A right-click on a step of the route: put its system on the avoid list, or take it off —
    /// whichever it is not. Built when asked, so the words match the list as it is now.
    /// </summary>
    private void OnStepContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source) return;
        var row = source.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        if (row?.DataContext is not RouteStepRowVm step) return;

        var avoided = RouteAvoidList.Contains(step.Step.SystemId);
        var item = new MenuItem
        {
            Header = string.Format(avoided ? MapText.AvoidRemove : MapText.AvoidAdd, step.Label),
        };
        item.Click += (_, _) => RouteAvoidList.Toggle(step.Step.SystemId);
        new ContextMenu { ItemsSource = new[] { item } }.Open(row);
        e.Handled = true;
    }
}
