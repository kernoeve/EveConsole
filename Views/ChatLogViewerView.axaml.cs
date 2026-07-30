using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ChatLogViewerView : ReactiveUserControl<ChatLogViewerViewModel>
{
    public ChatLogViewerView()
    {
        InitializeComponent();
    }
}
