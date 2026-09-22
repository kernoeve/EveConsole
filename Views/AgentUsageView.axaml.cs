using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class AgentUsageView : ReactiveUserControl<AgentUsageViewModel>
{
    public AgentUsageView()
    {
        InitializeComponent();
    }
}
