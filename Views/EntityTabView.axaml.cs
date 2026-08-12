using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class EntityTabView : ReactiveUserControl<EntityTabViewModel>
{
    public EntityTabView() => InitializeComponent();
}
