using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class CorpActivityWindow : ReactiveWindow<CorpActivityViewModel>
{
    public CorpActivityWindow() => InitializeComponent();
}
