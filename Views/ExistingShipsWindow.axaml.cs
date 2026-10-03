using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>The fitting tool's Existing ships picker; closes with the ship chosen, or null.</summary>
public partial class ExistingShipsWindow : Window
{
    private ExistingShipsViewModel? Vm => DataContext as ExistingShipsViewModel;

    public ExistingShipsWindow() => InitializeComponent();

    public ExistingShipsWindow(ExistingShipsViewModel vm) : this()
    {
        DataContext = vm;
        // Ready to type into: the filter is the way through a long list.
        Opened += (_, _) => Filter.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOpenClick(object? sender, RoutedEventArgs e) => Open();

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e) => Open();

    private void Open()
    {
        if (Vm?.Selected is { } row) Close(row.Ship);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(null); e.Handled = true; return; }
        base.OnKeyDown(e);
    }
}
