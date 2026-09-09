namespace EveConsole.Services;

/// <summary>
/// Where remembered UI state lives: which view was last chosen, what was left collapsed, how a
/// screen is arranged.
///
/// <para>⚠️ The local config file, never the database, and this is the rule for anything new of the
/// same kind. The preference table is a table IN the database, so with several clients sharing one
/// PostgreSQL server every remembered control became a shared one — collapsing a group on the
/// desktop collapsed it on the laptop, choosing a map overlay on one machine changed it on the
/// other, and declining an update on a machine that had it hid the notice from a machine that
/// did not. None of that is the user's data; it is how one screen was left.</para>
///
/// <para>The test is simple: would two people, or one person at two machines, reasonably want
/// different answers? Then it belongs here. Settings ABOUT the data — what to build, what to
/// price, what to poll — stay in the shared table where every client sees the same thing.</para>
///
/// <para>Adding a remembered control costs a key and nothing else; there is no field to declare.
/// The window's geometry and the Overview layout have typed fields of their own in
/// <see cref="AppConfig"/> because they predate this, not because they are different.</para>
/// </summary>
public static class UiState
{
    // Keys are namespaced by screen, as they were in the table they came from. Keeping the same
    // strings is what lets the seeding below find the value it is replacing.
    public const string UpdateAutoCheck      = "update.auto_check";
    public const string UpdateDeclined       = "update.declined_version";
    public const string UniverseOverlay      = "universe.overlay";
    public const string OverviewPeriodHours  = "overview.period_hours";
    public const string MarketSource         = "itembrowser.market_source";
    public const string CollapsedGroups      = "invlevels.collapsed_groups";
    public const string CollapsedCollections = "invlevels.collapsed_collections";
    public const string StructuresShowUnknown = "structures.show_unknown";
    public const string Theme                 = "ui.theme";

    /// <summary>
    /// This client's value for <paramref name="key"/>.
    ///
    /// <para>Pass <paramref name="shared"/> to take the old preference-table value once, when
    /// nothing has been saved locally, so an installation upgrading into this keeps what it had.
    /// ⚠️ Seeded into the file on the way past rather than left as a standing fallback: falling
    /// back for ever would mean a client that never touches the control goes on picking up whatever
    /// ANOTHER machine last saved, which is the behaviour being fixed, only later.</para>
    ///
    /// <para>Omit it where carrying the old value forward is not wanted — a declined update version
    /// is one, since it was recorded against a build the client is about to leave behind.</para>
    ///
    /// <para>The shared row itself is left in place. Other clients on the same database may still
    /// be on a build that reads it.</para>
    /// </summary>
    public static string? Get(string key, AppPreferencesService? shared = null)
    {
        if (AppConfig.GetUiState(key) is { Length: > 0 } local) return local;

        if (shared?.Get(key) is not { Length: > 0 } seed) return null;

        try { AppConfig.SetUiState(key, seed); }
        catch { /* config not writable; fall back again next run rather than failing the screen */ }

        return seed;
    }

    public static long GetLong(string key, long defaultValue, AppPreferencesService? shared = null)
        => long.TryParse(Get(key, shared), out var n) ? n : defaultValue;

    public static bool GetBool(string key, bool defaultValue, AppPreferencesService? shared = null)
        => Get(key, shared) is { } v
            ? v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            : defaultValue;

    /// <summary>
    /// Remembers a value for this client only.
    ///
    /// <para>⚠️ Synchronous, unlike the preference table it replaces. That is the point: it is a
    /// file write, so the callers that used to fire an un-awaited SetAsync and hope are no longer
    /// racing anything — including a control set moments before the window closes.</para>
    /// </summary>
    public static void Set(string key, string? value) => AppConfig.SetUiState(key, value);

    public static void SetLong(string key, long? value)
        => AppConfig.SetUiState(key, value?.ToString());

    public static void SetBool(string key, bool value)
        => AppConfig.SetUiState(key, value ? "1" : "0");
}
