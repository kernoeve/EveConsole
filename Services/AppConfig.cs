using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveConsole.Services;

/// <summary>
/// Persists machine-level config outside the database so it can be read before the DB
/// connection is opened (e.g. splash screen monitor, DB path).
/// Stored at %LocalAppData%\EveConsole\config.json.
/// </summary>
public static class AppConfig
{
    private const string AppFolder     = "EveConsole";
    private const string LegacyFolder  = "EveCortex";     // pre-rename data location
    private const string DbFileName    = "EveConsole.db";
    private const string LegacyDbFile  = "EveCortex.db";

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Public so services that keep their own files alongside the config (agent settings, TTS
    // models, etc.) share the single app data directory rather than hard-coding the folder name.
    public static string AppDataDir => Path.Combine(LocalAppData, AppFolder);

    /// <summary>
    /// A config.json sitting beside the executable, which takes precedence over the one in app
    /// data when it exists.
    ///
    /// <para>It lets two builds on one machine point at different databases: a development copy
    /// aimed at a server, and an ordinary install still on its SQLite file, without either
    /// disturbing the other's settings.</para>
    ///
    /// <para>⚠️ Only the CONFIG moves. Everything else that lives in app data — the agent's
    /// settings, voice models, sound cache — stays there, because those are the user's and
    /// not the installation's. A portable config is about which database this executable opens,
    /// not about making the whole app relocatable.</para>
    ///
    /// <para>⚠️ Presence is what selects it, so an installation directory that is not writable
    /// simply never has one. Nothing creates this file automatically; a person puts it there.</para>
    /// </summary>
    public static string PortableConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>True when settings are being read from beside the executable.</summary>
    public static bool UsingPortableConfig => File.Exists(PortableConfigPath);

    private static string ConfigPath =>
        UsingPortableConfig ? PortableConfigPath : Path.Combine(AppDataDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string DefaultDbPath => Path.Combine(AppDataDir, DbFileName);

    // ── Read ─────────────────────────────────────────────────────────────────

    public static string GetDbPath()       => Load().DbPath ?? DefaultDbPath;

    /// <summary>
    /// Which engine to open. SQLite unless the user has explicitly pointed the app at a server,
    /// so every existing installation keeps opening the file it always has.
    /// </summary>
    public static DbBackend GetDbBackend()
    {
        // A connection string in the environment is itself the instruction: nobody sets one and
        // means to go on using the file.
        if (EnvConnection is not null) return DbBackend.Postgres;

        return string.Equals(Load().DbBackend, "postgres", StringComparison.OrdinalIgnoreCase)
            ? DbBackend.Postgres
            : DbBackend.Sqlite;
    }

    /// <summary>
    /// A connection string supplied by the environment, which overrides config.json entirely.
    ///
    /// <para>Two reasons it exists. It lets a developer point a build at a test server without
    /// editing the config the running copy is using — re-pointing that file would send the
    /// real app somewhere else mid-session. And a poller running in a container has no config
    /// file to edit and no user to edit it; the environment is how such a thing is configured.
    /// The standalone poller is a planned split, so this is the shape it will need.</para>
    ///
    /// <para>⚠️ Env wins over file, never merges. A half-applied override — the server from
    /// one place and the credentials from another — is the kind of configuration that appears
    /// to work and connects somewhere nobody intended.</para>
    /// </summary>
    private static string? EnvConnection
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("EVECONSOLE_DB_CONNECTION");
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }

    /// <summary>
    /// The Postgres connection string, with the password put back.
    ///
    /// <para>The password is stored apart from the rest and protected by the platform — DPAPI
    /// on Windows, the desktop keyring on Linux — so config.json holds a server address and a
    /// user name that anybody can read and edit, and nothing that is worth stealing. See
    /// <see cref="SecretStore"/> for why that matters more since the config can live beside the
    /// executable.</para>
    ///
    /// <para>⚠️ A password that cannot be decrypted comes back as none at all, which is the
    /// expected outcome after the config is copied to another machine or another account. The app
    /// then asks for it rather than trying to connect with a blank one and reporting an
    /// authentication failure the user cannot act on.</para>
    /// </summary>
    public static string? GetPostgresConnection()
    {
        if (EnvConnection is { } fromEnv) return fromEnv;

        var c = Load();
        if (string.IsNullOrWhiteSpace(c.PostgresConnection)) return null;

        var password = SecretStore.Unprotect(c.PostgresPassword);
        if (string.IsNullOrEmpty(password)) return c.PostgresConnection;

        try
        {
            var b = new Npgsql.NpgsqlConnectionStringBuilder(c.PostgresConnection) { Password = password };
            return b.ConnectionString;
        }
        catch { return c.PostgresConnection; }
    }

    /// <summary>How the password is being held, for the settings screen to state.</summary>
    public static SecretProtection PostgresPasswordProtection =>
        SecretStore.IsProtected(Load().PostgresPassword)
            ? SecretStore.Available
            : SecretProtection.None;

    public static (int X, int Y)? GetWindowPosition()
    {
        var c = Load();
        if (c.WindowX is int x && c.WindowY is int y) return (x, y);
        return null;
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public static void SetDbPath(string path)
    {
        var c = Load();
        c.DbPath = path;
        Save(c);
    }

    /// <summary>
    /// Points the app at an engine. Takes effect on the next start, because the context factory
    /// is built from it once.
    ///
    /// <para>⚠️ The Postgres connection string is kept when switching back to SQLite rather
    /// than cleared. Somebody trying the file database again should not have to retype a server
    /// address and password to go back.</para>
    /// </summary>
    public static void SetDbBackend(DbBackend backend, string? postgresConnection = null)
    {
        var c = Load();
        c.DbBackend = backend == DbBackend.Postgres ? "postgres" : "sqlite";

        if (!string.IsNullOrWhiteSpace(postgresConnection))
        {
            // ⚠️ Split before storing, so the password never reaches the file even once. The
            // connection string kept here has it removed rather than blanked, so a reader cannot
            // tell a password-less server from one whose password is held elsewhere.
            try
            {
                var b = new Npgsql.NpgsqlConnectionStringBuilder(postgresConnection);
                var password = b.Password ?? "";
                b.Password = null;

                c.PostgresConnection = b.ConnectionString;
                c.PostgresPassword   = SecretStore.Protect(password, "postgres");
            }
            catch
            {
                c.PostgresConnection = postgresConnection;
                c.PostgresPassword   = null;
            }
        }

        Save(c);
    }

    public static void SetWindowPosition(int x, int y)
    {
        var c = Load();
        c.WindowX = x;
        c.WindowY = y;
        Save(c);
    }

    /// <summary>Where the main window was, and how big. Null on a fresh install.</summary>
    public static (int X, int Y, int Width, int Height, string State)? GetMainWindow()
    {
        var c = Load();
        return c.MainX is int x && c.MainY is int y
            ? (x, y, c.MainWidth ?? 0, c.MainHeight ?? 0, c.MainState ?? "Normal")
            : null;
    }

    /// <summary>
    /// Remembers the main window.
    ///
    /// <para>⚠️ Width and height of zero mean "do not change it", which is what a maximised
    /// window reports as its restore size in some cases. Writing those through would shrink the
    /// window to nothing on the next launch.</para>
    /// </summary>
    public static void SetMainWindow(int x, int y, int width, int height, string state)
    {
        var c = Load();
        c.MainX     = x;
        c.MainY     = y;
        c.MainState = state;

        if (width  > 200) c.MainWidth  = width;
        if (height > 100) c.MainHeight = height;

        Save(c);
    }

    /// <summary>
    /// Whether a database shrink was requested and has not run yet.
    ///
    /// <para>Kept here rather than in AppPreferences because the shrink happens before the
    /// database is opened — a flag living inside the file being rebuilt would be unreadable at
    /// exactly the moment it is needed.</para>
    /// </summary>
    /// <summary>
    /// The archive a restore should put back on the next start, or null.
    ///
    /// <para>Alongside the shrink and relocation flags because it is the same kind of request:
    /// something that cannot be done while the database is open, so it is recorded and performed
    /// before anything opens it.</para>
    /// </summary>
    public static string? GetRestorePending()
    {
        var v = Load().RestorePending;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public static void SetRestorePending(string? archivePath)
    {
        var c = Load();
        c.RestorePending = string.IsNullOrWhiteSpace(archivePath) ? null : archivePath;
        Save(c);
    }

    public static bool GetShrinkPending() => Load().ShrinkPending == true;

    public static void SetShrinkPending(bool pending)
    {
        var c = Load();
        c.ShrinkPending = pending ? true : null;   // absent rather than false — keeps the file tidy
        Save(c);
    }

    /// <summary>
    /// A database move requested by the user, to be performed at next startup.
    ///
    /// <para>Here rather than in AppPreferences for the same reason as the shrink flag: it is read
    /// before the database is opened, and what it describes is the database moving.</para>
    /// </summary>
    public static string? GetPendingRelocation()
    {
        var to = Load().RelocateTo;
        return string.IsNullOrWhiteSpace(to) ? null : to;
    }

    public static void SetPendingRelocation(string target)
    {
        var c = Load();
        c.RelocateTo = target;
        Save(c);
    }

    public static void ClearPendingRelocation()
    {
        var c = Load();
        c.RelocateTo = null;
        Save(c);
    }

    // ── One-time migration from the pre-rename "EveCortex" folder ─────────────

    /// <summary>
    /// Carries a pre-rename Eve Cortex install forward into the new EVE Console data folder.
    /// Runs once, at startup, before the DB is opened. Everything is COPIED, never moved: a still
    /// installed Eve Cortex keeps its own untouched database, so schema changes the new app makes
    /// can't break the old one. The database is copied to the new default path (fresh, isolated
    /// copy) regardless of where the old one lived — the new config then points at that copy.
    /// </summary>
    public static void MigrateLegacyDataIfNeeded()
    {
        try
        {
            // Already set up (fresh install or a prior migration) — do nothing.
            if (File.Exists(ConfigPath)) return;

            var legacyDir       = Path.Combine(LocalAppData, LegacyFolder);
            var legacyConfig    = Path.Combine(legacyDir, "config.json");
            var legacyDefaultDb = Path.Combine(legacyDir, LegacyDbFile);

            // Nothing to migrate unless the old app left a config or a default database behind.
            if (!File.Exists(legacyConfig) && !File.Exists(legacyDefaultDb)) return;

            Directory.CreateDirectory(AppDataDir);

            // Resolve where the old DB actually lived (explicit path if it was moved, else default).
            var old = File.Exists(legacyConfig)
                ? (TryLoad(legacyConfig) ?? new ConfigData())
                : new ConfigData();
            var sourceDb = old.DbPath ?? legacyDefaultDb;

            if (File.Exists(sourceDb) && !File.Exists(DefaultDbPath))
            {
                File.Copy(sourceDb, DefaultDbPath);
                // Carry the SQLite WAL/SHM sidecars too, in case the old app closed uncleanly.
                foreach (var ext in new[] { "-wal", "-shm" })
                    if (File.Exists(sourceDb + ext) && !File.Exists(DefaultDbPath + ext))
                        File.Copy(sourceDb + ext, DefaultDbPath + ext);
                old.DbPath = DefaultDbPath;   // point the new config at the copy
            }
            else
            {
                old.DbPath = null;            // no source DB — fall back to a fresh default
            }

            // Write the (adjusted) config into the new folder, then carry small settings files.
            Save(old);
            foreach (var file in new[] { "agent-settings.json", "aura-history.json" })
            {
                var src = Path.Combine(legacyDir, file);
                var dst = Path.Combine(AppDataDir, file);
                if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
            }
        }
        catch
        {
            // Migration is best-effort — never let it block startup. Worst case the user lands on
            // a fresh setup and can re-point their database from Settings → Database.
        }
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private static ConfigData Load() => TryLoad(ConfigPath) ?? new ConfigData();

    private static ConfigData? TryLoad(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var s = File.OpenRead(path);
                return JsonSerializer.Deserialize<ConfigData>(s, JsonOpts);
            }
        }
        catch { }
        return null;
    }

    private static void Save(ConfigData data)
    {
        // The directory of whichever file is in use — writing to app data while reading from
        // beside the executable would silently discard every change the user made.
        var path = ConfigPath;
        var dir  = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOpts));
    }

    private sealed class ConfigData
    {
        [JsonPropertyName("dbPath")]  public string? DbPath  { get; set; }

        // Which engine, and how to reach it when that engine is a server. Absent on every
        // database written before Postgres support, which reads back as SQLite.
        [JsonPropertyName("dbBackend")]          public string? DbBackend          { get; set; }
        [JsonPropertyName("postgresConnection")] public string? PostgresConnection { get; set; }

        // ⚠️ Kept apart from the connection string and protected by the platform. A value with
        // no "dpapi:" or "libsecret:" prefix was written before this existed, or on a machine
        // that could not protect it; either way it is read as-is and protected the next time the
        // user saves.
        [JsonPropertyName("postgresPassword")]   public string? PostgresPassword   { get; set; }
        [JsonPropertyName("windowX")] public int?    WindowX { get; set; }
        [JsonPropertyName("windowY")] public int?    WindowY { get; set; }

        // The main window, kept beside the splash's position rather than in the database. It is
        // per-installation UI state, not the user's data, and a file is something somebody can
        // open and fix when a window ends up on a monitor that no longer exists.
        [JsonPropertyName("mainX")]      public int?    MainX      { get; set; }
        [JsonPropertyName("mainY")]      public int?    MainY      { get; set; }
        [JsonPropertyName("mainWidth")]  public int?    MainWidth  { get; set; }
        [JsonPropertyName("mainHeight")] public int?    MainHeight { get; set; }
        [JsonPropertyName("mainState")]  public string? MainState  { get; set; }
        [JsonPropertyName("shrinkPending")] public bool? ShrinkPending { get; set; }
        [JsonPropertyName("restorePending")] public string? RestorePending { get; set; }
        [JsonPropertyName("relocateTo")]   public string? RelocateTo   { get; set; }
    }
}
