using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class TradeOpportunitiesWindow : ReactiveWindow<TradeOpportunitiesViewModel>
{
    public TradeOpportunitiesWindow() => InitializeComponent();
}
