using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class AssetBrowserWindow : ReactiveWindow<AssetBrowserViewModel>
{
    public AssetBrowserWindow()
    {
        InitializeComponent();
    }
}
