using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Localization;
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
        var entry = Vm.SelectedNode!.Entry!;
        Close(new FitSelectorResult(entry.Data, Vm.SelectedGroup?.GroupId ?? 0, entry));
    }

    /// <summary>Deletes the EVE Console fit chosen, after asking.</summary>
    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (!Vm.CanDeleteSelected || Vm.SelectedNode?.Entry is not { } entry) return;
        var ok = await new ConfirmDialog(string.Format(FittingText.ConfirmDeleteSaved, entry.Data.Name), title: FittingText.TitleDeleteSaved)
            .ShowDialog<bool>(this);
        if (ok) await Vm.DeleteSelectedAsync();
    }
}
