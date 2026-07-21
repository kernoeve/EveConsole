using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ContractsView : ReactiveUserControl<ContractsViewModel>
{
    public ContractsView()
    {
        InitializeComponent();
    }
}
