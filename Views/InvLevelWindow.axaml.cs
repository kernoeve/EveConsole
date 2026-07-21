using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class InvLevelWindow : ReactiveWindow<InvLevelViewModel>
{
    public InvLevelWindow() => InitializeComponent();
}
