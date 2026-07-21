using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EveConsole.Views;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow() => InitializeComponent();

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
