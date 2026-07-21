using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ProductionCalculatorWindow : ReactiveWindow<ProductionCalculatorViewModel>
{
    public ProductionCalculatorWindow() => InitializeComponent();
}
