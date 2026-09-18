using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EveConsole.Views;

/// <summary>
/// "This link opens a website in your browser — open it?" with the address shown and a
/// "don't ask me again" box. Closes with true to open, false otherwise; <see cref="DontAsk"/>
/// says whether the box was ticked, which the caller records once and never asks about again.
/// </summary>
public partial class ExternalLinkDialog : Window
{
    public bool DontAsk => DontAskBox.IsChecked == true;

    public ExternalLinkDialog(string url)
    {
        InitializeComponent();
        UrlText.Text = url;
    }

    private void OnOpen(object? sender, RoutedEventArgs e)   => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
