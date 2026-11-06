using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class BattleFinderView : UserControl
{
    public BattleFinderView() => InitializeComponent();

    private void OnFightDoubleTapped(object? sender, TappedEventArgs e)
    {
        var fight = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault()?.DataContext as BattleFightVm;
        fight?.OpenCommand.Execute().Subscribe();
    }
}
