using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class IndustryBrowserWindow : ReactiveWindow<IndustryBrowserViewModel>
{
    public IndustryBrowserWindow()
    {
        InitializeComponent();
    }
}
