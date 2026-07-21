using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class WalletView : ReactiveUserControl<WalletViewModel>
{
    public WalletView()
    {
        InitializeComponent();
    }
}
