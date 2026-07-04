using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class WalletView : ReactiveUserControl<WalletViewModel>
{
    public WalletView()
    {
        InitializeComponent();
    }
}
