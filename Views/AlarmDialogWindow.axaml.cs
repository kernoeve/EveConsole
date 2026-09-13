using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EveConsole.Views;

/// <summary>
/// The Dialog alarm action. Deliberately top-most and centred on the screen rather than the
/// owner — the point of this action is to be seen when EVE Console is behind the game client.
/// </summary>
public partial class AlarmDialogWindow : Window
{
    private readonly Func<Task>? _onAcknowledge;

    /// <param name="button">The button's label when pressing it means something — "I'm awake".</param>
    /// <param name="onAcknowledge">Run when it is pressed: the acknowledgement of a staged alarm.</param>
    public AlarmDialogWindow(string title, string message, string? button = null, Func<Task>? onAcknowledge = null)
    {
        InitializeComponent();
        Title            = string.IsNullOrWhiteSpace(title) ? "Alarm" : title;
        TitleText.Text   = Title;
        MessageText.Text = message;
        _onAcknowledge   = onAcknowledge;
        if (!string.IsNullOrWhiteSpace(button)) DismissButton.Content = button;
    }

    private void OnDismiss(object? sender, RoutedEventArgs e)
    {
        if (_onAcknowledge is { } ack) _ = ack();
        Close();
    }
}
