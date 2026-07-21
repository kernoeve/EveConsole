using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class NetWorthWindow : ReactiveWindow<NetWorthViewModel>
{
    public NetWorthWindow() => InitializeComponent();
}
