using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class CharacterViewerWindow : ReactiveWindow<CharacterViewerViewModel>
{
    public CharacterViewerWindow()
    {
        InitializeComponent();
    }
}
