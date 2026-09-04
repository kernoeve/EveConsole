using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EveConsole.Views;

public partial class ConfirmDialog : Window
{
    /// <summary>
    /// What the user must type before Yes becomes available, or null for an ordinary yes/no.
    ///
    /// <para>⚠️ For destructive actions only, and only where the phrase names the thing being
    /// destroyed. Asking somebody to type "yes" teaches them to type "yes"; asking them to type
    /// the name of the database makes them read which database it is, which is the mistake worth
    /// catching — restoring the right file over the wrong server.</para>
    /// </summary>
    private readonly string? _required;

    public ConfirmDialog(string message, string? requiredPhrase = null)
    {
        InitializeComponent();
        MessageText.Text = message;
        _required        = requiredPhrase;

        if (_required is null) return;

        ConfirmPanel.IsVisible = true;
        ConfirmPrompt.Text     = $"Type {_required} to confirm:";
        YesButton.IsEnabled    = false;
        ConfirmBox.TextChanged += (_, _) =>
            YesButton.IsEnabled =
                string.Equals(ConfirmBox.Text?.Trim(), _required, StringComparison.Ordinal);
    }

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);
    private void OnNo(object? sender, RoutedEventArgs e)  => Close(false);
}
