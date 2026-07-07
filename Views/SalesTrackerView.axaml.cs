using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class SalesTrackerView : ReactiveUserControl<SalesTrackerViewModel>
{
    public SalesTrackerView()
    {
        InitializeComponent();
    }
}
