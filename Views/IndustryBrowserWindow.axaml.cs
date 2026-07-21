using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class IndustryBrowserWindow : ReactiveWindow<IndustryBrowserViewModel>
{
    public IndustryBrowserWindow()
    {
        InitializeComponent();
    }
}
