using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.Controls;
using EveConsole.Localization;
using EveConsole.Services.Fitting;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>One fit in the fitting tool: the ring, the module list and the fit's numbers.</summary>
public partial class FitTabView : UserControl
{
    public FitTabView()
    {
        InitializeComponent();
        Ring.SlotContextRequested += OnRingContext;
    }

    private FitTabViewModel? Vm => DataContext as FitTabViewModel;

    /// <summary>Right-click on the ring: what can be done to that module, as in the game.</summary>
    private void OnRingContext(FittingSlot slot, Point at)
    {
        if (Vm is not { } vm || slot.Tag is not FittingModuleRowVm { IsEmpty: false } row) return;
        vm.SelectedModule = row;

        var items = new List<MenuItem>();
        MenuItem Item(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => act();
            return mi;
        }
        if (row.CanToggle)
        {
            items.Add(row.IsOff
                ? Item(FittingText.MenuPutOnline, () => vm.SetState(row, ModuleState.Online))
                : Item(FittingText.MenuPutOffline, () => vm.SetState(row, ModuleState.Offline)));
            if (row.CanActivate)
                items.Add(row.State >= ModuleState.Active
                    ? Item(FittingText.MenuDeactivate, () => vm.SetState(row, ModuleState.Online))
                    : Item(FittingText.MenuActivate, () => vm.SetState(row, ModuleState.Active)));
            if (row.CanOverheat)
                items.Add(row.IsHeated
                    ? Item(FittingText.MenuStopOverheating, () => vm.SetState(row, ModuleState.Active))
                    : Item(FittingText.MenuOverheat, () => vm.SetState(row, ModuleState.Overheated)));
        }
        if (row.Charge is { } charge)
            items.Add(Item(string.Format(FittingText.MenuUnload, charge.DisplayName), () => row.Charge = null));
        items.Add(Item(FittingText.Remove, () => vm.Remove(row)));

        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(Ring);
    }

    /// <summary>The module last clicked is where a charge picked in the finder is loaded.</summary>
    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm && (sender as Control)?.DataContext is FittingModuleRowVm row && !row.IsEmpty)
            vm.SelectedModule = row;
    }
}
