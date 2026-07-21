using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class KillmailBrowserWindow : ReactiveWindow<KillmailBrowserViewModel>
{
    public KillmailBrowserWindow() => InitializeComponent();
}
