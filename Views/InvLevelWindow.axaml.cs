using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class InvLevelWindow : ReactiveWindow<InvLevelViewModel>
{
    public InvLevelWindow() => InitializeComponent();
}
