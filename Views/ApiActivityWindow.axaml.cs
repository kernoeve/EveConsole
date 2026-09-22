using Avalonia.Controls;

namespace EveConsole.Views;

/// <summary>The Background Processes view in a window of its own, for the tray menu. Everything
/// it does, it does as <see cref="BackgroundProcessesView"/>.</summary>
public partial class ApiActivityWindow : Window
{
    public ApiActivityWindow()
    {
        InitializeComponent();
    }
}
