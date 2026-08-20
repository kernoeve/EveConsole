using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class SystemPageView : UserControl
{
    public SystemPageView() => InitializeComponent();

    /// <summary>
    /// Opens the double-clicked kill in the Killmail Browser.
    ///
    /// <para>Code-behind rather than a command because the gesture belongs to the row as a whole,
    /// and the row's own DataContext is the killmail — there is nothing for a binding to route
    /// through that a handler does not already have.</para>
    /// </summary>
    private void OnKillRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: KillmailListRowVm row })
            EntityNavigator.Instance.Killmail(row.KillMailId);
    }

    // ── Header links ──────────────────────────────────────────────────────────
    //
    // These sit outside any row template, so they read the page's own view model rather than a
    // DataContext.
    private SystemPageViewModel? Vm => DataContext as SystemPageViewModel;

    private void OnOpenRegion(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm?.OpenRegion();
    private void OnOpenConstellation(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm?.OpenConstellation();
    private void OnOpenPirates(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm?.OpenPirates();

    // ── Row links ─────────────────────────────────────────────────────────────
    private void OnOpenSovType(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as SovStructureVm)?.OpenType();

    private void OnOpenSovOwner(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as SovStructureVm)?.OpenOwner();

    private void OnOpenGateSystem(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as GateVm)?.OpenSystem();
}
