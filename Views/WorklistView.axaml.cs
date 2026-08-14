using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class WorklistView : ReactiveUserControl<WorklistViewModel>
{
    public WorklistView()
    {
        InitializeComponent();
    }
}
