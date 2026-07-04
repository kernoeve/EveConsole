using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class MarketLevelWindow : ReactiveWindow<MarketLevelViewModel>
{
    public MarketLevelWindow() => InitializeComponent();
}
