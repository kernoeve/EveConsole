using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class WalletWindow : ReactiveWindow<WalletViewModel>
{
    public WalletWindow() => InitializeComponent();
}
