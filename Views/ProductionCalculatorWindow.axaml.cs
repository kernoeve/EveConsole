using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class ProductionCalculatorWindow : ReactiveWindow<ProductionCalculatorViewModel>
{
    public ProductionCalculatorWindow() => InitializeComponent();
}
