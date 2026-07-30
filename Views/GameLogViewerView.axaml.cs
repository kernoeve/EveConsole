using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class GameLogViewerView : ReactiveUserControl<GameLogViewerViewModel>
{
    public GameLogViewerView()
    {
        InitializeComponent();
    }
}
