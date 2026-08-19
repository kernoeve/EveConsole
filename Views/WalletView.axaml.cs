using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class WalletView : ReactiveUserControl<WalletViewModel>
{
    public WalletView()
    {
        InitializeComponent();
    }

    // Each row carries its own navigation; the button's DataContext is the row it sits in.
    private void OnOpenJournalOwner(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as WalletJournalRowVm)?.OpenOwner();

    private static WalletTransactionRowVm? Tx(object? sender)
        => (sender as Control)?.DataContext as WalletTransactionRowVm;

    private void OnOpenTxItem(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Tx(sender)?.OpenItem();
    private void OnOpenTxLocation(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Tx(sender)?.OpenLocation();
    private void OnOpenTxOwner(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Tx(sender)?.OpenOwner();
}
