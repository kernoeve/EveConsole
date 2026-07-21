using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class CharacterViewerWindow : ReactiveWindow<CharacterViewerViewModel>
{
    public CharacterViewerWindow()
    {
        InitializeComponent();
    }
}
