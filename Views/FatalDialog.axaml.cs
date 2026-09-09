using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EveConsole.Views;

/// <summary>
/// Says why the application is stopping, and offers nothing else.
///
/// <para>Distinct from <see cref="ConfirmDialog"/> on purpose. That one asks a question; this one
/// reports a decision already taken. A fault that ends the session must not be presented with a
/// pair of buttons, because the second one always reads as "carry on regardless" whatever it is
/// labelled.</para>
/// </summary>
public partial class FatalDialog : Window
{
    public FatalDialog(string title, string message)
    {
        InitializeComponent();
        TitleText.Text   = title;
        MessageText.Text = message;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
