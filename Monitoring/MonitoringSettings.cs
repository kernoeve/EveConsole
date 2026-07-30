using EveConsole.Services;

namespace EveConsole.Monitoring;

/// <summary>
/// Typed view over the game log importer's preference keys, wrapping
/// AppPreferencesService the same way SlackService wraps it for its own keys.
///
/// ESI session polling has no settings here — it runs as ordinary endpoints in
/// EsiPollingService, so its intervals live in the existing Timers settings.
/// </summary>
public sealed class MonitoringSettings(AppPreferencesService prefs)
{
    public const string KeyGameLogEnabled   = "gamelog.enabled";
    public const string KeyGameLogDirs      = "gamelog.dirs";
    public const string KeyFirstImportDays  = "gamelog.first_import_days";
    public const string KeyStoreUnmatched   = "gamelog.store_unmatched";
    public const string KeyScanSeconds      = "gamelog.scan_seconds";
    public const string KeyHistoryImported  = "gamelog.history_imported";

    public const string KeyChatEnabled       = "chatlog.enabled";
    public const string KeyChatChannels      = "chatlog.channels";
    public const string KeyChatDiscovered    = "chatlog.discovered_channels";
    public const string KeyChatHistoryDays   = "chatlog.history_days";
    public const string KeyChatDirs          = "chatlog.dirs";

    /// <summary>
    /// ON by default. The auto-detected local folder starts being read as soon as the
    /// app runs, so new activity is captured without the user having to find a setting
    /// first. Only picks up activity from that point onward — anything already in the
    /// logs needs an explicit history import, which is never automatic.
    /// </summary>
    public bool GameLogEnabled
    {
        get => prefs.GetBool(KeyGameLogEnabled, true);
        set => _ = prefs.SetBoolAsync(KeyGameLogEnabled, value);
    }

    /// <summary>
    /// Directories to import from, newline-separated. May include UNC paths
    /// (<c>\\HOST\share\Gamelogs</c>) — that is how EVE clients on other machines are
    /// covered, with nothing installed on them.
    ///
    /// Empty means "use the auto-detected local folder".
    /// </summary>
    public IReadOnlyList<string> GameLogDirectories
    {
        get => (prefs.Get(KeyGameLogDirs) ?? "")
               .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        set => _ = prefs.SetAsync(KeyGameLogDirs, value.Count == 0 ? null : string.Join('\n', value));
    }

    /// <summary>
    /// Day window most recently chosen for a history import. Only a remembered UI
    /// value — the import itself is explicit and user-initiated, so this never causes
    /// anything to happen on its own.
    /// </summary>
    public int HistoryImportDays
    {
        get => Math.Clamp((int)prefs.GetLong(KeyFirstImportDays, 30), 0, 3650);
        set => _ = prefs.SetLongAsync(KeyFirstImportDays, Math.Clamp(value, 0, 3650));
    }

    /// <summary>Set once a history import has completed, so the first-run prompt stops
    /// being offered.</summary>
    public bool HistoryImported
    {
        get => prefs.GetBool(KeyHistoryImported, false);
        set => _ = prefs.SetBoolAsync(KeyHistoryImported, value);
    }

    /// <summary>
    /// Store lines no rule matched, as Kind = "unmatched".
    ///
    /// ON by default, and that default is deliberate. EVE's log format is undocumented
    /// and changes between client versions, and the rule set can only ever cover
    /// activity someone has actually done — an entire channel can be missing simply
    /// because that playstyle wasn't sampled. (Exactly this happened: (mining) and
    /// (bounty) lines were absent from a 900-file sample and only turned up across the
    /// full archive.) Keeping unmatched lines makes coverage gaps visible from real
    /// play instead of depending on someone thinking to look.
    /// </summary>
    public bool StoreUnmatched
    {
        get => prefs.GetBool(KeyStoreUnmatched, true);
        set => _ = prefs.SetBoolAsync(KeyStoreUnmatched, value);
    }

    public int ScanSeconds
    {
        get => Math.Clamp((int)prefs.GetLong(KeyScanSeconds, 5), 2, 300);
        set => _ = prefs.SetLongAsync(KeyScanSeconds, Math.Clamp(value, 2, 300));
    }

    // ── Chat logs ────────────────────────────────────────────────────────────
    //
    // Two independent gates, both of which must be satisfied before a single message
    // is stored: the feature must be enabled AND the channel must be on the allowlist.
    // Chat contains other people's words, including private conversations — which show
    // up as "Private Chat (2)", "Private Chat (3)" etc. Those are generic slot names
    // reused for different people over time, so one of them can span DMs with many
    // different players. "On" alone is never enough.

    /// <summary>OFF by default. Unlike game logs, this stores message content.</summary>
    public bool ChatEnabled
    {
        get => prefs.GetBool(KeyChatEnabled, false);
        set => _ = prefs.SetBoolAsync(KeyChatEnabled, value);
    }

    /// <summary>Channels the user has explicitly chosen to keep, newline-separated.
    /// EMPTY BY DEFAULT — an empty list stores nothing, even when enabled. Opting in
    /// is always a positive act of naming a channel.</summary>
    public IReadOnlyList<string> ChatChannels
    {
        get => (prefs.Get(KeyChatChannels) ?? "")
               .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        set => _ = prefs.SetAsync(KeyChatChannels, value.Count == 0 ? null : string.Join('\n', value));
    }

    /// <summary>Channel names found by the last discovery scan. Cached because
    /// enumerating a chat log folder is slow — tens of thousands of files, often on
    /// OneDrive — so it is an explicit user action rather than something done on open.</summary>
    public IReadOnlyList<string> ChatDiscoveredChannels
    {
        get => (prefs.Get(KeyChatDiscovered) ?? "")
               .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        set => _ = prefs.SetAsync(KeyChatDiscovered, value.Count == 0 ? null : string.Join('\n', value));
    }

    public int ChatHistoryDays
    {
        get => Math.Clamp((int)prefs.GetLong(KeyChatHistoryDays, 7), 0, 3650);
        set => _ = prefs.SetLongAsync(KeyChatHistoryDays, Math.Clamp(value, 0, 3650));
    }

    /// <summary>Chat log folders. Empty means the auto-detected local one.</summary>
    public IReadOnlyList<string> ChatDirectories
    {
        get => (prefs.Get(KeyChatDirs) ?? "")
               .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        set => _ = prefs.SetAsync(KeyChatDirs, value.Count == 0 ? null : string.Join('\n', value));
    }

    public IReadOnlyList<string> ResolveChatDirectories()
    {
        var configured = ChatDirectories;
        if (configured.Count > 0) return configured;

        var auto = DefaultChatLogDirectory();
        return auto is null ? [] : [auto];
    }

    /// <summary>Sibling of the Gamelogs folder.</summary>
    public static string? DefaultChatLogDirectory() => FindLogSubfolder("Chatlogs");

    /// <summary>Configured directories, or the auto-detected default when none are set.</summary>
    public IReadOnlyList<string> ResolveDirectories()
    {
        var configured = GameLogDirectories;
        if (configured.Count > 0) return configured;

        var auto = DefaultGameLogDirectory();
        return auto is null ? [] : [auto];
    }

    /// <summary>
    /// The usual local location. Null when it cannot be found — most often because
    /// Documents is redirected (OneDrive, corporate policy), which is the single most
    /// common reason the importer finds nothing. The settings tab shows the resolved
    /// path so this is visible rather than mysterious.
    /// </summary>
    public static string? DefaultGameLogDirectory() => FindLogSubfolder("Gamelogs");

    /// <summary>Locate a folder under EVE's logs directory, trying the usual places.
    /// Documents redirection (OneDrive, corporate policy) is the single most common
    /// reason nothing is found, so the OneDrive path is tried explicitly.</summary>
    private static string? FindLogSubfolder(string subfolder)
    {
        var candidates = new List<string>();

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(docs))
            candidates.Add(Path.Combine(docs, "EVE", "logs", subfolder));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            candidates.Add(Path.Combine(profile, "OneDrive", "Documents", "EVE", "logs", subfolder));
            candidates.Add(Path.Combine(profile, "Documents", "EVE", "logs", subfolder));
        }

        foreach (var c in candidates)
        {
            try { if (Directory.Exists(c)) return c; }
            catch { /* unreachable path — keep looking */ }
        }

        return null;
    }
}
