using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class MarketViewerView : ReactiveUserControl<MarketViewerViewModel>
{
    public MarketViewerView()
    {
        InitializeComponent();
    }
}
