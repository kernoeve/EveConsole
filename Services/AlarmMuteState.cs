namespace EveConsole.Services;

/// <summary>
/// Whether this machine stays quiet for alarms — held in one place, so every control that shows or
/// changes it agrees.
///
/// <para>⚠️ A shared object rather than each view model keeping its own copy. There are now three
/// ways to reach this — the beacon's right-click menu, the button on the Alarms tool, and whatever
/// comes next — and a second copy would mean muting from one of them while another still shows
/// "on", with the beacon un-struck and the operator believing they are silent when they are not.
/// That is a worse failure than having no toggle at all.</para>
///
/// <para>The value itself lives in the local config file, because muting is a fact about this
/// machine and must not follow the database to another client.</para>
/// </summary>
public sealed class AlarmMuteState
{
    private bool _muted = AppConfig.GetAlarmsMuted();

    /// <summary>Raised after any change, from whichever control made it.</summary>
    public event Action? Changed;

    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value) return;
            _muted = value;
            AppConfig.SetAlarmsMuted(value);
            Changed?.Invoke();
        }
    }

    public void Toggle() => Muted = !Muted;

    /// <summary>The action a click would take, for a menu item or a button that toggles.</summary>
    public string ToggleText => _muted ? "Unmute Alarms" : "Mute Alarms";
}
