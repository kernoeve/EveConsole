using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Localization;

namespace EveConsole.Views;

/// <summary>
/// The one window a repeating alarm sound always brings with it: a button that acknowledges
/// the situation and stops the sound. Separate from the Dialog action, which the user may or
/// may not have chosen — a sound that will not stop until acknowledged must carry its own way
/// of being acknowledged. Top-most and centred, like the dialog, to be seen behind the game.
///
/// <para>Closed by the caller when the sound ends of itself — the situation ended, or was
/// acknowledged on another client — so it never outlives the noise it is for.</para>
/// </summary>
public partial class AlarmSoundWindow : Window
{
    private readonly Func<Task> _onAcknowledge;
    private bool _acknowledged;

    public AlarmSoundWindow(string alarmName, string summary, Func<Task> onAcknowledge)
    {
        InitializeComponent();
        Title            = string.Format(AlarmsText.SoundWindowTitle, alarmName);
        TitleText.Text   = alarmName;
        MessageText.Text = string.IsNullOrWhiteSpace(summary)
            ? AlarmsText.SoundRepeatsUntilAck
            : summary + "\n\n" + AlarmsText.SoundRepeatsUntilAck;
        _onAcknowledge   = onAcknowledge;
    }

    private void OnAcknowledge(object? sender, RoutedEventArgs e)
    {
        if (_acknowledged) return;
        _acknowledged = true;
        AcknowledgeButton.IsEnabled = false;
        _ = _onAcknowledge();
        Close();
    }
}
