using Avalonia.Controls;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

/// <summary>
/// A window a tab was dragged out into. It holds tabs of its own, split or not, and takes more
/// dragged in from the main window or any other; named for the tool it is showing. Closing it
/// closes its tools. Opened and closed by <see cref="MainWindow"/> as workspaces come and go.
/// </summary>
public partial class ToolHostWindow : Window
{
    public TabWorkspace Workspace { get; }

    public ToolHostWindow() : this(new TabWorkspace(isMain: false)) { }   // the designer's

    public ToolHostWindow(TabWorkspace workspace)
    {
        Workspace = workspace;
        InitializeComponent();
        Tabs.DataContext = workspace;
        workspace.WhenAnyValue(w => w.Title).Subscribe(t => Title = t.Length > 0 ? $"{t} — EVE Console" : "EVE Console");
    }

    internal WorkspaceView View => Tabs;
}
