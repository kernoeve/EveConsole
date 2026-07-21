using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class CorpActivityWindow : ReactiveWindow<CorpActivityViewModel>
{
    public CorpActivityWindow() => InitializeComponent();
}
