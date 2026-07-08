using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class OrderTrackerView : ReactiveUserControl<OrderTrackerViewModel>
{
    public OrderTrackerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not OrderTrackerViewModel vm) return;
            vm.ShowOrderDialog = async initial =>
            {
                if (TopLevel.GetTopLevel(this) is not Window owner) return null;
                var dialog = new OrderEditDialog(vm.SearchTypesAsync, initial);
                return await dialog.ShowDialog<OrderDialogResult?>(owner);
            };
        };
    }
}
