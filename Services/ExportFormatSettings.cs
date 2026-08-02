namespace EveConsole.Services;

/// <summary>
/// The clipboard/post markup format, shared by every screen that offers the choice —
/// sale postings, the Corp Activity Top 10 and the Monthly Summary.
///
/// Deliberately one setting rather than one per screen: a user who posts to Discord picks
/// Discord once, and having each dropdown remember its own value would mean setting the
/// same thing three times and, worse, silently posting one screen in the wrong format.
/// </summary>
public sealed class ExportFormatSettings(AppPreferencesService prefs)
{
    public const string Key     = "ui.export_format";
    public const string Default = "Plain Text";

    public string Format
    {
        get
        {
            var stored = prefs.Get(Key);
            return string.IsNullOrWhiteSpace(stored) ? Default : stored;
        }
        set => _ = prefs.SetAsync(Key, string.IsNullOrWhiteSpace(value) ? Default : value);
    }
}
