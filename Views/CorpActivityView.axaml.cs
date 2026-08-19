using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.Models;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class CorpActivityView : UserControl
{
    public CorpActivityView()
    {
        InitializeComponent();
        ExportTop10Button.Click += (_, _) =>
        {
            if (DataContext is not CorpActivityViewModel vm) return;
            var text = vm.BuildTop10Export();
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        };

        ExportTop10NoIskButton.Click += (_, _) =>
        {
            if (DataContext is not CorpActivityViewModel vm) return;
            var text = vm.BuildTop10ExportNoIsk();
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        };

        PostTop10SlackButton.Click += (_, _) =>
        {
            if (DataContext is not CorpActivityViewModel vm) return;
            _ = vm.PostTop10ToSlackAsync(includeIsk: false);
        };

        ExportSummaryButton.Click += (_, _) =>
        {
            if (DataContext is not CorpActivityViewModel vm) return;
            var text = vm.BuildMonthlySummaryExport();
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        };

        PostSummarySlackButton.Click += (_, _) =>
        {
            if (DataContext is not CorpActivityViewModel vm) return;
            _ = vm.PostMonthlySummaryToSlackAsync();
        };

        Kill24hList.DoubleTapped += OnKill24hDoubleTapped;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is not CorpActivityViewModel vm) return;

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CorpActivityViewModel.ShowProjectDetailPanel))
                UpdateProjectsGridRows(vm.ShowProjectDetailPanel);
        };
        UpdateProjectsGridRows(vm.ShowProjectDetailPanel);

        vm.ShowStandingProjectDialog = async (existing) =>
        {
            var dialog = new StandingProjectDialog(vm.Service, existing);
            return await dialog.ShowDialog<CorpStandingProject?>(GetWindow());
        };

        vm.ConfirmSlackRepost = async (message) =>
        {
            var dlg = new ConfirmDialog(message);
            return await dlg.ShowDialog<bool>(GetWindow());
        };

        vm.ConfirmDelete = async () =>
        {
            var dlg = new ConfirmDialog("Are you sure you want to delete this standing project?");
            return await dlg.ShowDialog<bool>(GetWindow());
        };
    }

    // Click rather than Command: the row already carries the navigation itself, and reaching it
    // through the button's own DataContext avoids another ICommand per row.
    private void OnOpenProjectItem(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as StandingProjectRowVm)?.OpenItem();

    private void OnOpenProjectLocation(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as StandingProjectRowVm)?.OpenLocation();

    // ── Row links ─────────────────────────────────────────────────────────────
    //
    // One handler per kind of link rather than per grid, matched on the row type. Half a dozen
    // lists across this view show a character name, and they are backed by six different row
    // types that share no base class; a handler each would be six near-identical methods, while
    // a switch here keeps every "open the pilot" link on one line of code.
    private void OnOpenRowCharacter(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        switch ((sender as Control)?.DataContext)
        {
            case Activity24hPlayerRowVm r: r.OpenCharacter(); break;
            case CorpTopPlayerRowVm     r: r.OpenCharacter(); break;
            case CorpKillCharRowVm      r: r.OpenCharacter(); break;
            case MiningLedgerRowVm      r: r.OpenCharacter(); break;
            case ProjectContributorVm   r: r.OpenCharacter(); break;
        }
    }

    /// <summary>Wallet counterparties — a donor or a tax payer can be a corporation as easily as
    /// a pilot, so the row decides the kind from the id.</summary>
    private void OnOpenRowParty(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as WalletDetailRowVm)?.OpenParty();

    private void OnOpenRowEntity(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as TaxPayerRowVm)?.OpenEntity();

    /// <summary>The ore on a mining row, in the Item Browser.</summary>
    private void OnOpenRowType(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as MiningLedgerRowVm)?.OpenType();

    private void OnOpenProjectCreator(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as CorpProjectRowVm)?.OpenCreator();

    // ── Killmail rows ─────────────────────────────────────────────────────────
    private void OnOpenKillVictim(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Kill(sender)?.OpenVictim();
    private void OnOpenKillVictimCorp(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Kill(sender)?.OpenVictimCorp();
    private void OnOpenKillVictimAlliance(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Kill(sender)?.OpenVictimAlliance();
    private void OnOpenKillFb(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Kill(sender)?.OpenFb();
    private void OnOpenKillFbCorp(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Kill(sender)?.OpenFbCorp();
    private void OnOpenKillFbAlliance(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Kill(sender)?.OpenFbAlliance();
    private void OnOpenKillShip(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Kill(sender)?.OpenShip();
    private void OnOpenKillSystem(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Kill(sender)?.OpenSystem();
    private void OnOpenKillRegion(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Kill(sender)?.OpenRegion();

    private static Activity24hKillRowVm? Kill(object? sender)
        => (sender as Control)?.DataContext as Activity24hKillRowVm;

    /// <summary>
    /// Double-click a killmail row to open it in the Killmail tool.
    ///
    /// <para>Walks up from whatever was actually tapped to find the row's own view model, rather
    /// than reading the list's SelectedItem: the row is full of links now, and a double-click that
    /// lands on one of those names must still open the kill.</para>
    /// </summary>
    private void OnKillRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not CorpActivityViewModel vm) return;

        for (var c = e.Source as Control; c is not null; c = c.Parent as Control)
            if (c.DataContext is Activity24hKillRowVm row)
            {
                vm.RequestOpenKillmail?.Invoke(row.KillMailId);
                return;
            }
    }

    // ⚠️ Shares OnKillRowDoubleTapped rather than reading Kill24hList.SelectedItem. Now that the
    // rows carry links, a double-click can land on a Button, which does not select the row it
    // sits in — so the selection was the wrong row, or the previous one, or none at all.
    private void OnKill24hDoubleTapped(object? sender, TappedEventArgs e)
        => OnKillRowDoubleTapped(sender, e);

    private void UpdateProjectsGridRows(bool showDetail)
    {
        ProjectsOuterGrid.RowDefinitions[1].Height = showDetail ? new GridLength(4)       : GridLength.Auto;
        ProjectsOuterGrid.RowDefinitions[2].Height = showDetail ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }

    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;
}
