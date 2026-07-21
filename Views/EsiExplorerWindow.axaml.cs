using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class EsiExplorerWindow : ReactiveWindow<EsiExplorerViewModel>
{
    public EsiExplorerWindow()
    {
        InitializeComponent();
    }
}
