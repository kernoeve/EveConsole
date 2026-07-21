using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class MarketViewerView : ReactiveUserControl<MarketViewerViewModel>
{
    public MarketViewerView()
    {
        InitializeComponent();
    }
}
