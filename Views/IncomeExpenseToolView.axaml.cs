using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class IncomeExpenseToolView : UserControl
{
    public IncomeExpenseToolView()
    {
        InitializeComponent();
    }

    private void OnOpenOwner(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as WalletJournalRowVm)?.OpenOwner();
}
