using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class WalletWindow : ReactiveWindow<WalletViewModel>
{
    public WalletWindow() => InitializeComponent();
}
