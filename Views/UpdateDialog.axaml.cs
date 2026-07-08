using Avalonia.Controls;
using Avalonia.Interactivity;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class UpdateDialog : Window
{
    public UpdateDialog()
    {
        InitializeComponent();
    }

    private void OnUpdateNow(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not UpdateViewModel vm) return;
        PromptPanel.IsVisible   = false;
        ProgressPanel.IsVisible = true;
        // Downloads then restarts the process; this window won't return.
        vm.InstallUpdateCommand.Execute().Subscribe();
    }

    private void OnNotNow(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UpdateViewModel vm) vm.DeclineCurrent();
        Close();
    }
}
