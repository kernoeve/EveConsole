using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.Controls;
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
                ? Item("Put online", () => vm.SetState(row, ModuleState.Online))
                : Item("Put offline", () => vm.SetState(row, ModuleState.Offline)));
            if (row.CanActivate)
                items.Add(row.State >= ModuleState.Active
                    ? Item("Deactivate", () => vm.SetState(row, ModuleState.Online))
                    : Item("Activate", () => vm.SetState(row, ModuleState.Active)));
            if (row.CanOverheat)
                items.Add(row.IsHeated
                    ? Item("Stop overheating", () => vm.SetState(row, ModuleState.Active))
                    : Item("Overheat", () => vm.SetState(row, ModuleState.Overheated)));
        }
        if (row.Charge is { } charge)
            items.Add(Item($"Unload {charge.Name}", () => row.Charge = null));
        items.Add(Item("Remove", () => vm.Remove(row)));

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
