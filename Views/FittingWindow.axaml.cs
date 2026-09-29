using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class FittingWindow : ReactiveWindow<FittingViewModel>
{
    public FittingWindow() => InitializeComponent();
}
