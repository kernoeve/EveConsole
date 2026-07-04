using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class NetWorthWindow : ReactiveWindow<NetWorthViewModel>
{
    public NetWorthWindow() => InitializeComponent();
}
