using Avalonia.Controls;
using Avalonia.Interactivity;
using EveCortex.Models;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class OverviewCustomizeWindow : Window
{
    public OverviewCustomizeWindow()
    {
        InitializeComponent();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnApply(object? sender, RoutedEventArgs e)
        => Close((DataContext as OverviewCustomizeViewModel)?.Build());
}
