using System.Globalization;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>
/// Every intel setting as one reading of the database: how long each kind of news lasts, the
/// follow-up window, what makes a gate camp, whose standings colour the pilots, and the alarm
/// numbers set on Settings → Intel.
///
/// <para>Read as a whole, fresh, by whatever needs it (the snapshot every minute, the worker's
/// alarms), never from one PC's startup copy: the settings are shared, and a lifetime changed on
/// one PC must reach the others without a restart.</para>
/// </summary>
public sealed record IntelSettingsValues
{
    public TimeSpan Named        { get; init; } = TimeSpan.FromMinutes(IntelSettings.DefaultNamedMinutes);
    public TimeSpan Unnamed      { get; init; } = TimeSpan.FromMinutes(IntelSettings.DefaultUnnamedMinutes);
    public TimeSpan Clear        { get; init; } = TimeSpan.FromMinutes(IntelSettings.DefaultClearMinutes);
    public TimeSpan Camp         { get; init; } = TimeSpan.FromMinutes(IntelSettings.DefaultCampMinutes);
    public TimeSpan Cyno         { get; init; } = TimeSpan.FromMinutes(IntelSettings.DefaultCynoMinutes);
    public TimeSpan KillAttacker { get; init; } = TimeSpan.FromMinutes(IntelSettings.DefaultKillAttackerMinutes);
    public TimeSpan KillVictim   { get; init; } = TimeSpan.FromMinutes(IntelSettings.DefaultKillVictimMinutes);

    public TimeSpan FollowUpWindow { get; init; } = TimeSpan.FromSeconds(IntelThreads.DefaultWindowSeconds);

    /// <summary>How near a gate a kill must be to count towards a camp on it.</summary>
    public double   CampGateKm      { get; init; } = IntelSettings.DefaultCampGateKm;

    /// <summary>The longest gap between two kills of one camp.</summary>
    public TimeSpan CampWindow      { get; init; } = TimeSpan.FromMinutes(IntelSettings.DefaultCampWindowMinutes);

    /// <summary>Distinct pilots lost before kills on a gate are a camp.</summary>
    public int      CampMinVictims  { get; init; } = IntelSettings.DefaultCampMinVictims;

    /// <summary>The character whose standings colour every pilot, for everyone; 0 for each
    /// viewing character's own.</summary>
    public long     StandingsCharacterId { get; init; }

    /// <summary>Whether travel distances count our own jump bridges. Threat distance never does.</summary>
    public bool     UseBridges { get; init; }

    /// <summary>The kill-feed warning bar was dismissed (shared by every PC on the database).</summary>
    public bool     FeedWarningDismissed { get; init; }

    /// <summary>The intel alarms' shared wake cooldown, in seconds; 0 for none.</summary>

    /// <summary>How new news must be to wake anyone, and to light up a panel row.</summary>
    public int      FreshSeconds { get; init; } = IntelSettings.DefaultFreshSeconds;

    /// <summary>zKillboard's switch and scope, for the feed warning: read with the rest, because
    /// this PC's copy of them is whatever it loaded at startup.</summary>
    public bool     ZkbEnabled  { get; init; } = true;
    public ZkbScope ZkbScope    { get; init; } = ZkbScope.MineAndCorp;

    /// <summary>Whether any intel channel is set on Settings → Chat Logs: without one, a panel says why
    /// it is empty. True until read, so nothing says so before the first reading.</summary>
    public bool     HasIntelChannels { get; init; } = true;

    /// <summary>The chat channels set as intel on Settings → Chat Logs (Jabber and IRC rooms are not
    /// among them: they are marked on their own pages).</summary>
    public IReadOnlyList<string> IntelChannels { get; init; } = [];

    /// <summary>The longest any sighting, flag or clear lasts.</summary>
    public TimeSpan LongestLifetime =>
        new[] { Named, Unnamed, Clear, Camp, Cyno, KillAttacker, KillVictim }.Max();

    /// <summary>
    /// How far back the snapshot reads: an hour, or the longest lifetime and five minutes when that
    /// is longer. An hour because the tool's Feed shows up to one. A camp's kills can leave this window
    /// before the Camps tab lets the camp go (an hour after its last kill, faded once ended); the camp
    /// register holds those camps, so the window need not cover them.
    /// </summary>
    public TimeSpan Window
    {
        get
        {
            var longest = LongestLifetime + TimeSpan.FromMinutes(5);
            return longest > IntelSettings.MinimumWindow ? longest : IntelSettings.MinimumWindow;
        }
    }

    /// <summary>A flag's lifetime: a camp and bubbles last as long as a camp, a cyno as long as
    /// a cyno, and every other flag as long as an unnamed count.</summary>
    public TimeSpan FlagLifetime(Monitoring.IntelRules.IntelFlags flag) => flag switch
    {
        Monitoring.IntelRules.IntelFlags.Camp or Monitoring.IntelRules.IntelFlags.Bubbles => Camp,
        Monitoring.IntelRules.IntelFlags.Cyno                                            => Cyno,
        _                                                                                => Unnamed,
    };

    /// <summary>A pilot's lifetime by what last placed them.</summary>
    public TimeSpan PilotLifetime(int lastSeenSource) => lastSeenSource switch
    {
        Models.IntelLastSeenSource.KillAttacker => KillAttacker,
        Models.IntelLastSeenSource.KillVictim   => KillVictim,
        _                                       => Named,
    };
}

/// <summary>
/// The intel settings' keys, defaults and bounds, and their one reader and writers.
///
/// <para>Shared in the database like every other preference. "No visual" is not here: it is a flag
/// on a sighting, not something with a lifetime of its own.</para>
/// </summary>
public sealed class IntelSettings(AppPreferencesService prefs)
{
    public const string KeyNamed        = IntelLastSeenWriter.KeyNamedLifetime;
    public const string KeyUnnamed      = "intel.life.unnamed_min";
    public const string KeyClear        = "intel.life.clear_min";
    public const string KeyCamp         = "intel.life.camp_min";
    public const string KeyCyno         = "intel.life.cyno_min";
    public const string KeyKillAttacker = "intel.life.kill_attacker_min";
    public const string KeyKillVictim   = "intel.life.kill_victim_min";
    public const string KeyFollowUp     = IntelThreads.KeyWindow;
    public const string KeyCampGateKm   = "intel.camp.gate_km";
    public const string KeyCampWindow   = "intel.camp.window_min";
    public const string KeyCampVictims  = "intel.camp.min_victims";
    public const string KeyStandings    = "intel.standings.character";
    public const string KeyBridges      = "intel.distance.bridges";
    public const string KeyFeedWarning  = "intel.zkb_warning_dismissed";
    public const string KeyFresh        = "intel.alert.fresh_s";

    public const int DefaultNamedMinutes        = IntelLastSeenWriter.DefaultNamedMinutes;
    public const int DefaultUnnamedMinutes      = 15;
    public const int DefaultClearMinutes        = 10;
    public const int DefaultCampMinutes         = 30;
    public const int DefaultCynoMinutes         = 10;
    public const int DefaultKillAttackerMinutes = 15;
    public const int DefaultKillVictimMinutes   = 5;
    public const int MinLifetimeMinutes         = IntelLastSeenWriter.MinNamedMinutes;
    public const int MaxLifetimeMinutes         = IntelLastSeenWriter.MaxNamedMinutes;

    public const double DefaultCampGateKm       = 150;
    public const double MinCampGateKm           = 20;
    public const double MaxCampGateKm           = 1000;
    public const int    DefaultCampWindowMinutes = 30;
    public const int    MinCampWindowMinutes     = 5;
    public const int    MaxCampWindowMinutes     = 120;
    public const int    DefaultCampMinVictims    = 2;
    public const int    MinCampMinVictims        = 2;
    public const int    MaxCampMinVictims        = 20;

    public const int DefaultFreshSeconds        = 120;
    public const int MinFreshSeconds            = 15;
    public const int MaxFreshSeconds            = 1800;

    /// <summary>The shortest the snapshot ever reads back: see <see cref="IntelSettingsValues.Window"/>.</summary>
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromMinutes(60);

    private static readonly string[] Keys =
    [
        KeyNamed, KeyUnnamed, KeyClear, KeyCamp, KeyCyno, KeyKillAttacker, KeyKillVictim, KeyFollowUp,
        KeyCampGateKm, KeyCampWindow, KeyCampVictims, KeyStandings, KeyBridges, KeyFeedWarning,
        KeyFresh, ZkillboardSettings.KeyEnabled, ZkillboardSettings.KeyScope,
        EveConsole.Monitoring.MonitoringSettings.KeyChatIntelChannels,
    ];

    /// <summary>Every intel setting as the database holds it now, in one query.</summary>
    public static async Task<IntelSettingsValues> ReadAsync(AppDbContext db, CancellationToken ct = default)
    {
        var rows = await db.AppPreferences.AsNoTracking()
            .Where(p => Keys.Contains(p.Key))
            .Select(p => new { p.Key, p.Value })
            .ToListAsync(ct);
        return From(rows.ToDictionary(r => r.Key, r => (string?)r.Value));
    }

    /// <summary>The values from raw preference text: unset or unreadable ones take their default,
    /// and every number is held to its bounds.</summary>
    public static IntelSettingsValues From(IReadOnlyDictionary<string, string?> raw)
    {
        long Long(string key, long fallback, long min, long max) =>
            raw.TryGetValue(key, out var t) && long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? Math.Clamp(n, min, max) : fallback;
        double Real(string key, double fallback, double min, double max) =>
            raw.TryGetValue(key, out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && double.IsFinite(n) ? Math.Clamp(n, min, max) : fallback;
        bool Bool(string key, bool fallback) =>
            raw.TryGetValue(key, out var t) && t is not null ? t == "1" || t.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;
        TimeSpan Life(string key, int fallback) =>
            TimeSpan.FromMinutes(Long(key, fallback, MinLifetimeMinutes, MaxLifetimeMinutes));

        var channels = (raw.GetValueOrDefault(EveConsole.Monitoring.MonitoringSettings.KeyChatIntelChannels) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new IntelSettingsValues
        {
            Named        = Life(KeyNamed,        DefaultNamedMinutes),
            Unnamed      = Life(KeyUnnamed,      DefaultUnnamedMinutes),
            Clear        = Life(KeyClear,        DefaultClearMinutes),
            Camp         = Life(KeyCamp,         DefaultCampMinutes),
            Cyno         = Life(KeyCyno,         DefaultCynoMinutes),
            KillAttacker = Life(KeyKillAttacker, DefaultKillAttackerMinutes),
            KillVictim   = Life(KeyKillVictim,   DefaultKillVictimMinutes),
            FollowUpWindow = TimeSpan.FromSeconds(Long(KeyFollowUp, IntelThreads.DefaultWindowSeconds,
                                                       IntelThreads.MinWindowSeconds, IntelThreads.MaxWindowSeconds)),
            CampGateKm     = Real(KeyCampGateKm, DefaultCampGateKm, MinCampGateKm, MaxCampGateKm),
            CampWindow     = TimeSpan.FromMinutes(Long(KeyCampWindow, DefaultCampWindowMinutes, MinCampWindowMinutes, MaxCampWindowMinutes)),
            CampMinVictims = (int)Long(KeyCampVictims, DefaultCampMinVictims, MinCampMinVictims, MaxCampMinVictims),
            StandingsCharacterId = Long(KeyStandings, 0, 0, long.MaxValue),
            UseBridges           = Bool(KeyBridges, false),
            FeedWarningDismissed = Bool(KeyFeedWarning, false),
            FreshSeconds         = (int)Long(KeyFresh, DefaultFreshSeconds, MinFreshSeconds, MaxFreshSeconds),
            ZkbEnabled = Bool(ZkillboardSettings.KeyEnabled, true),
            ZkbScope   = raw.TryGetValue(ZkillboardSettings.KeyScope, out var scope) && scope == "All" ? ZkbScope.All : ZkbScope.MineAndCorp,
            HasIntelChannels = channels.Length > 0,
            IntelChannels    = channels,
        };
    }

    /// <summary>The values as this PC loaded them: for the Settings page, which then writes each
    /// change through.</summary>
    public IntelSettingsValues Cached => From(Keys.ToDictionary(k => k, k => prefs.Get(k)));

    public Task SetMinutesAsync(string key, int minutes) =>
        prefs.SetLongAsync(key, Math.Clamp(minutes, MinLifetimeMinutes, MaxLifetimeMinutes));

    public Task SetFollowUpSecondsAsync(int seconds) =>
        prefs.SetLongAsync(KeyFollowUp, Math.Clamp(seconds, IntelThreads.MinWindowSeconds, IntelThreads.MaxWindowSeconds));

    public Task SetCampGateKmAsync(double km) =>
        prefs.SetAsync(KeyCampGateKm, Math.Clamp(km, MinCampGateKm, MaxCampGateKm).ToString(CultureInfo.InvariantCulture));

    public Task SetCampWindowMinutesAsync(int minutes) =>
        prefs.SetLongAsync(KeyCampWindow, Math.Clamp(minutes, MinCampWindowMinutes, MaxCampWindowMinutes));

    public Task SetCampMinVictimsAsync(int victims) =>
        prefs.SetLongAsync(KeyCampVictims, Math.Clamp(victims, MinCampMinVictims, MaxCampMinVictims));

    public Task SetStandingsCharacterAsync(long characterId) =>
        prefs.SetLongAsync(KeyStandings, Math.Max(0, characterId));

    public Task SetUseBridgesAsync(bool on) => prefs.SetBoolAsync(KeyBridges, on);

    public Task SetFeedWarningDismissedAsync(bool dismissed) => prefs.SetBoolAsync(KeyFeedWarning, dismissed);


    public Task SetFreshSecondsAsync(int seconds) =>
        prefs.SetLongAsync(KeyFresh, Math.Clamp(seconds, MinFreshSeconds, MaxFreshSeconds));
}

/// <summary>What the Intel tool's kill-feed warning needs: whether the full kill feed is off, and
/// whether the warning was dismissed.</summary>
/// <param name="FullFeed">zKillboard is on with every kill in New Eden (scope All).</param>
public sealed record IntelFeedStatus(bool Enabled, ZkbScope Scope, bool FullFeed, bool Dismissed)
{
    /// <summary>The bar shows when the full feed is off and nobody has dismissed it.</summary>
    public bool ShowWarning => !FullFeed && !Dismissed;

    public static IntelFeedStatus From(IntelSettingsValues s) =>
        new(s.ZkbEnabled, s.ZkbScope, s.ZkbEnabled && s.ZkbScope == ZkbScope.All, s.FeedWarningDismissed);
}
