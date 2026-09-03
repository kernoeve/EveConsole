namespace EveConsole.Services;

/// <summary>Which database engine the app is pointed at.</summary>
public enum DbBackend
{
    /// <summary>A file on this machine. The default, and what every install used before 1.0.</summary>
    Sqlite,

    /// <summary>A server the user runs and points the app at.</summary>
    Postgres,
}

/// <summary>
/// The engine in use, for the places where SQL genuinely has to differ.
///
/// <para>⚠️ Resolved once and cached rather than read from config per call. The backend cannot
/// change while the process runs — the context factory is built from it at startup, so switching
/// needs a restart — and a query asking "am I on Postgres?" must never get an answer that
/// disagrees with the connection it is about to run on.</para>
///
/// <para>Prefer LINQ over asking this. Every use is a place where two dialects have to be kept
/// in step by hand, which is the kind of duplication that goes quietly stale; reach for it when
/// the statement cannot be expressed through EF, not to save writing a query.</para>
/// </summary>
public static class DbEngine
{
    private static DbBackend? _current;

    public static DbBackend Current => _current ??= AppConfig.GetDbBackend();

    public static bool IsSqlite   => Current == DbBackend.Sqlite;
    public static bool IsPostgres => Current == DbBackend.Postgres;

    /// <summary>What to call it in the UI and in log lines.</summary>
    public static string DisplayName => IsPostgres ? "PostgreSQL" : "SQLite";

    /// <summary>
    /// Pins the backend for a process that builds its own context rather than reading config —
    /// the fresh-install checker and the schema-script tool under <c>tools/</c>.
    /// </summary>
    public static void Pin(DbBackend backend) => _current = backend;
}
