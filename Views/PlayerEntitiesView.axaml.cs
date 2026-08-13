using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class PlayerEntitiesView : ReactiveUserControl<PlayerEntitiesViewModel>
{
    public PlayerEntitiesView() => InitializeComponent();
}
