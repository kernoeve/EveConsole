using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class CharacterViewerView : ReactiveUserControl<CharacterViewerViewModel>
{
    public CharacterViewerView()
    {
        InitializeComponent();
    }
}
