using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class KillmailBrowserWindow : ReactiveWindow<KillmailBrowserViewModel>
{
    public KillmailBrowserWindow() => InitializeComponent();
}
