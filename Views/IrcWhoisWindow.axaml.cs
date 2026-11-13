using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>Who someone on IRC is, as the server says.</summary>
public partial class IrcWhoisWindow : Window
{
    public IrcWhoisWindow() => InitializeComponent();

    /// <summary>Opens one, owned by the window it was asked from.</summary>
    public static IrcWhoisWindow ShowFor(IrcWhoisVm vm, Window host)
    {
        var window = new IrcWhoisWindow { DataContext = vm };
        window.Show(host);
        return window;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
