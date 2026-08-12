using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class NpcEntitiesView : ReactiveUserControl<NpcEntitiesViewModel>
{
    public NpcEntitiesView()
    {
        InitializeComponent();
    }
}
