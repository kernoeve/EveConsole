using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ItemBrowserWindow : ReactiveWindow<ItemBrowserViewModel>
{
    public ItemBrowserWindow()
    {
        InitializeComponent();
    }
}
