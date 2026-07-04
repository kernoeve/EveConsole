using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class ItemBrowserWindow : ReactiveWindow<ItemBrowserViewModel>
{
    public ItemBrowserWindow()
    {
        InitializeComponent();
    }
}
