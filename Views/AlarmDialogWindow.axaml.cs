using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EveConsole.Views;

/// <summary>
/// The Dialog alarm action. Deliberately top-most and centred on the screen rather than the
/// owner — the point of this action is to be seen when EVE Console is behind the game client.
/// </summary>
public partial class AlarmDialogWindow : Window
{
    public AlarmDialogWindow(string title, string message)
    {
        InitializeComponent();
        Title           = string.IsNullOrWhiteSpace(title) ? "Alarm" : title;
        TitleText.Text  = Title;
        MessageText.Text = message;
    }

    private void OnDismiss(object? sender, RoutedEventArgs e) => Close();
}
