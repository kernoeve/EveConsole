using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class FitSelectorWindow : Window
{
    private FitSelectorViewModel Vm => (FitSelectorViewModel)DataContext!;

    public FitSelectorWindow()
    {
        InitializeComponent();
    }

    public FitSelectorWindow(FitSelectorViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (!Vm.CanConfirm) return;
        Close(new FitSelectorResult(Vm.SelectedNode!.Entry!.Data, Vm.SelectedGroup!.GroupId));
    }
}
