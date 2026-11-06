using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>One open battle. A live report changes its lists while it is on the screen.</summary>
public partial class BattleSessionView : UserControl
{
    public BattleSessionView() => InitializeComponent();

    /// <summary>Grids watching their list for a change about to happen. A live report changes its
    /// lists on its own, so this is what stands between a clicked row and a blank line: the grid
    /// keeps the focused row out of its recycling, and a change arriving meanwhile leaves that
    /// hidden row standing in a slot on screen. Focus leaving the grid lets it go.</summary>
    private readonly HashSet<(DataGrid, IAnnouncesChanges)> _watched = [];

    private void OnRowLoading(object? sender, DataGridRowEventArgs e)
    {
        // By grid and list together: this view is shared between battles, and a grid can be handed
        // another battle's list.
        if (sender is not DataGrid grid) return;
        var list = grid.ItemsSource as IAnnouncesChanges
                ?? (grid.ItemsSource as Avalonia.Collections.DataGridCollectionView)?.SourceCollection as IAnnouncesChanges;
        if (list is null || !_watched.Add((grid, list))) return;
        list.Changing += (_, _) =>
        {
            if (grid.IsKeyboardFocusWithin) TopLevel.GetTopLevel(grid)?.FocusManager?.ClearFocus();
        };
    }

    private static T? RowItem<T>(RoutedEventArgs e) where T : class =>
        (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault()?.DataContext as T;

    /// <summary>A double-click on the timeline is the killmail, as on the pilots. The names on it
    /// are links of their own.</summary>
    private void OnKillDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!OnAButton(e)) RowItem<BattleKillVm>(e)?.Open();
    }

    /// <summary>A double-click is the kill, where the line has one, and nothing where it has not.</summary>
    private void OnPilotDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (OnAButton(e) || RowItem<BattlePilotVm>(e) is not { HasLoss: true } pilot) return;
        if (e.Source is Control source) OpenLosses(pilot, source);
    }

    /// <summary>A click on a pilot with several hulls lists them. Not on their name or a link.</summary>
    private void OnPilotTapped(object? sender, TappedEventArgs e)
    {
        if (OnAButton(e) || RowItem<BattlePilotVm>(e) is not { } pilot) return;
        if (sender is DataGrid { DataContext: BattleTeamVm team }) team.Toggle(pilot);
    }

    private void OnLostClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BattlePilotVm pilot } button) OpenLosses(pilot, button);
    }

    /// <summary>Opens the kill, or with several lost in that hull, asks which.</summary>
    private static void OpenLosses(BattlePilotVm pilot, Control anchor)
    {
        var losses = pilot.Losses();
        if (losses.Count == 1) { EntityNavigator.Instance.Killmail(losses[0].KillMailId); return; }
        if (losses.Count == 0) return;

        var menu = new MenuFlyout();
        foreach (var kill in losses)
        {
            var item = new MenuItem { Header = kill.Time.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ·  " + BattleFormat.Isk(kill.Value) };
            item.Click += (_, _) => EntityNavigator.Instance.Killmail(kill.KillMailId);
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }

    private static bool OnAButton(RoutedEventArgs e) =>
        (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true;

    /// <summary>A click on an alliance's row opens its corporations. Not on its arrows, which have
    /// their own work to do.</summary>
    private void OnSideTapped(object? sender, TappedEventArgs e)
    {
        if (OnAButton(e) || RowItem<BattleSideVm>(e) is not { } side) return;
        if (sender is DataGrid { DataContext: BattleTeamVm team }) team.Toggle(side);
    }

    /// <summary>A click opens a hull into who flew it, or an alliance into its corporations.</summary>
    private void OnShipTapped(object? sender, TappedEventArgs e)
    {
        if (OnAButton(e) || RowItem<BattleShipVm>(e) is not { } ship) return;
        if (sender is DataGrid { DataContext: BattleTeamVm team }) team.Toggle(ship);
    }

    private void OnSideDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!OnAButton(e)) RowItem<BattleSideVm>(e)?.OpenDetails();
    }
}
