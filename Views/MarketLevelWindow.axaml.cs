using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class MarketLevelWindow : ReactiveWindow<MarketLevelViewModel>
{
    public MarketLevelWindow() => InitializeComponent();
}
