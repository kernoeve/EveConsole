using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class AssetBrowserWindow : ReactiveWindow<AssetBrowserViewModel>
{
    public AssetBrowserWindow()
    {
        InitializeComponent();
    }
}
