using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Localization;

namespace EveConsole.Views;

/// <summary>
/// The one window a situation waiting to be acknowledged brings with it: a button that
/// acknowledges it. A repeating sound always brings one — a sound that will not stop until
/// acknowledged must carry its own way of being acknowledged — and a wake-up call brings one
/// from its first stage, whatever that stage does, so it can be answered without a word to the
/// agent. Separate from the Dialog action, which the user may or may not have chosen. Top-most
/// and centred, like the dialog, to be seen behind the game.
///
/// <para>One per situation, kept through its stages: a later stage brings it to the front, and
/// the stage whose sound starts says so on it. Closed by the caller when the situation ends of
/// itself — over, or acknowledged on another client — so it never outlives what it is for.</para>
/// </summary>
public partial class AlarmSoundWindow : Window
{
    private readonly string     _alarmName;
    private readonly Func<Task> _onAcknowledge;
    private bool _acknowledged;

    public AlarmSoundWindow(string alarmName, string summary, bool sounding, Func<Task> onAcknowledge)
    {
        InitializeComponent();
        _alarmName     = alarmName;
        _onAcknowledge = onAcknowledge;
        TitleText.Text = alarmName;
        Update(summary, sounding);
    }

    /// <summary>The stage's own summary, and whether the sound is repeating yet: the title, the
    /// line and the button follow.</summary>
    public void Update(string summary, bool sounding)
    {
        Title = string.Format(sounding ? AlarmsText.SoundWindowTitle : AlarmsText.WakeWindowTitle, _alarmName);
        var line = sounding ? AlarmsText.SoundRepeatsUntilAck : AlarmsText.WakeAnswerHere;
        MessageText.Text = string.IsNullOrWhiteSpace(summary) ? line : summary + "\n\n" + line;
        AcknowledgeButton.Content = sounding ? AlarmsText.AcknowledgeStopTheSound : AlarmsText.ButtonImAwake;
        IconText.Text = sounding ? "\U0001F50A" : "\u23F0";
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
