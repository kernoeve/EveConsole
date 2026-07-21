using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

public partial class SdeUpdateDialog : Window
{
    public SdeUpdateDialog()
    {
        InitializeComponent();
    }

    private void OnUpdateNow(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SdeViewModel vm) return;

        PromptPanel.IsVisible   = false;
        ProgressPanel.IsVisible = true;

        vm.WhenAnyValue(x => x.IsBusy)
            .Skip(1)
            .Where(busy => !busy)
            .Take(1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                ProgressPanel.IsVisible = false;
                DonePanel.IsVisible     = true;
            });

        vm.RefreshSdeCommand.Execute().Subscribe();
    }

    private void OnHoboImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SdeViewModel vm) return;

        HoboImportButton.IsVisible    = false;
        HoboProgressPanel.IsVisible   = true;

        vm.WhenAnyValue(x => x.HoboIsBusy)
            .Skip(1)
            .Where(busy => !busy)
            .Take(1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                HoboProgressPanel.IsVisible = false;
                HoboImportButton.IsVisible  = true;
            });

        vm.RefreshHoboCommand.Execute().Subscribe();
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Close();
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
