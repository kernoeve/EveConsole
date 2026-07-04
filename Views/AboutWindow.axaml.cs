using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Reflection;

namespace EveCortex.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
