using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EveCortex.Views;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow() => InitializeComponent();

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
