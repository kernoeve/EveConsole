using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class EsiExplorerWindow : ReactiveWindow<EsiExplorerViewModel>
{
    public EsiExplorerWindow()
    {
        InitializeComponent();
    }
}
