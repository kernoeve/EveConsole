using System.Globalization;

namespace EveConsole.Services.Pi;

/// <summary>
/// How far ahead the PI alerts and worklist tasks look, when a colony's data counts as too old to
/// trust, and how many days of input a haul brings — Settings → Industry, beside the tax defaults.
///
/// <para>Shared preferences (AppPreferences), like the tax defaults: they decide what every client
/// says needs doing, so two clients on one database must not disagree about it. Whether each alert
/// is raised at all is an alert setting (Settings → Alerts); these numbers are what the alerts and
/// the tasks both judge by, so a colony the Overview calls "stopping" is the same colony the
/// worklist asks to have restarted.</para>
/// </summary>
public sealed class PiSettings(AppPreferencesService prefs)
{
    public const string ExtractorLeadKey = "pi.lead.extractor_hours";
    public const string StorageLeadKey   = "pi.lead.storage_hours";
    public const string InputLeadKey     = "pi.lead.input_hours";
    public const string StaleDaysKey     = "pi.stale_days";
    public const string InputDaysKey     = "pi.input_days";

    // The defaults, and why.
    //
    // A day for extractors and storage: an extractor program is set for one to a few days, and a
    // day's warning is one evening's login. Two days for inputs: they mean a trip with cargo, not
    // a click on the planet, and the haul has to be bought or fetched first. A week before data is
    // stale: the forecast replays the colony from its snapshot, and past a week a processor that
    // stalled or a route that was changed in game has had time to make every figure wrong. A week
    // of input per haul matches that: a colony visited weekly never runs dry.
    public const int DefaultExtractorLeadHours = 24;
    public const int DefaultStorageLeadHours   = 24;
    public const int DefaultInputLeadHours     = 48;
    public const int DefaultStaleDays          = 7;
    public const int DefaultInputDays          = 7;

    /// <summary>A lead time never reaches past the forecast's horizon, which is where the engine
    /// stops knowing anything.</summary>
    public static readonly int MaxLeadHours = (int)PiEngine.DefaultHorizon.TotalHours;

    public int ExtractorLeadHours => Read(ExtractorLeadKey, DefaultExtractorLeadHours, 0, MaxLeadHours);
    public int StorageLeadHours   => Read(StorageLeadKey,   DefaultStorageLeadHours,   0, MaxLeadHours);
    public int InputLeadHours     => Read(InputLeadKey,     DefaultInputLeadHours,     0, MaxLeadHours);
    public int StaleDays          => Read(StaleDaysKey,     DefaultStaleDays,          1, 365);
    public int InputDays          => Read(InputDaysKey,     DefaultInputDays,          1, 60);

    public PiThresholds Thresholds => new(
        TimeSpan.FromHours(ExtractorLeadHours),
        TimeSpan.FromHours(StorageLeadHours),
        TimeSpan.FromHours(InputLeadHours),
        TimeSpan.FromDays(StaleDays),
        InputDays);

    public Task SetExtractorLeadHoursAsync(int hours) => Write(ExtractorLeadKey, Math.Clamp(hours, 0, MaxLeadHours));
    public Task SetStorageLeadHoursAsync(int hours)   => Write(StorageLeadKey,   Math.Clamp(hours, 0, MaxLeadHours));
    public Task SetInputLeadHoursAsync(int hours)     => Write(InputLeadKey,     Math.Clamp(hours, 0, MaxLeadHours));
    public Task SetStaleDaysAsync(int days)           => Write(StaleDaysKey,     Math.Clamp(days, 1, 365));
    public Task SetInputDaysAsync(int days)           => Write(InputDaysKey,     Math.Clamp(days, 1, 60));

    private int Read(string key, int fallback, int min, int max)
        => int.TryParse(prefs.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? v
            : fallback;

    private Task Write(string key, int value) => prefs.SetAsync(key, value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>The numbers a colony is judged by: see <see cref="PiSettings"/>.</summary>
/// <param name="InputDays">How many days of running an input haul brings.</param>
public sealed record PiThresholds(TimeSpan ExtractorLead, TimeSpan StorageLead, TimeSpan InputLead,
                                  TimeSpan StaleAfter, int InputDays)
{
    public static PiThresholds Default { get; } = new(
        TimeSpan.FromHours(PiSettings.DefaultExtractorLeadHours),
        TimeSpan.FromHours(PiSettings.DefaultStorageLeadHours),
        TimeSpan.FromHours(PiSettings.DefaultInputLeadHours),
        TimeSpan.FromDays(PiSettings.DefaultStaleDays),
        PiSettings.DefaultInputDays);
}
