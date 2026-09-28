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

    // ── Profiles ─────────────────────────────────────────────────────────────

    private static string? _profileDir;

    /// <summary>
    /// The named profile this process was started with, or null for the ordinary one.
    ///
    /// <para>A profile is a second app data directory and therefore a second everything: its own
    /// config.json, its own database, its own remembered UI state, agent settings, sounds and
    /// picture cache. It exists so a development build can be run against a database of its own
    /// while the installed copy goes on using the real one — which matters more than it sounds,
    /// because opening the real database with a newer build stamps its version and the installed
    /// copy then refuses to start.</para>
    ///
    /// <para>⚠️ Nothing is shared and nothing is copied in. A new profile starts empty, the way a
    /// fresh install does, and is set up from Settings like one.</para>
    /// </summary>
    public static string? ProfileName { get; private set; }

    /// <summary>
    /// Points this process at a profile. Called once from <c>Program.Main</c>, before anything
    /// has read a path, and never afterwards.
    ///
    /// <para><paramref name="nameOrPath"/> is a bare name — kept under <c>Profiles\</c> in the
    /// ordinary app data directory — or a path of its own, for a profile somewhere else entirely.</para>
    /// </summary>
    public static void UseProfile(string nameOrPath)
    {
        var value = nameOrPath.Trim().Trim('"');
        if (value.Length == 0) return;

        var looksLikePath = Path.IsPathRooted(value)
                         || value.Contains(Path.DirectorySeparatorChar)
                         || value.Contains(Path.AltDirectorySeparatorChar);

        var dir = looksLikePath
            ? Path.GetFullPath(value)
            : Path.Combine(LocalAppData, AppFolder, "Profiles", Sanitise(value));

        Directory.CreateDirectory(dir);
        _profileDir = dir;
        ProfileName = looksLikePath ? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) : value;
    }

    /// <summary>A name that is safe as one path segment. A profile is named by whoever types the
    /// switch, so it cannot be trusted to be one.</summary>
    private static string Sanitise(string name)
    {
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return clean.Trim('.', ' ') is { Length: > 0 } s ? s : "profile";
    }

    // Public so services that keep their own files alongside the config (agent settings, TTS
    // models, etc.) share the single app data directory rather than hard-coding the folder name.
    //
    // ⚠️ The profile's directory when there is one, so everything that keeps a file beside the
    // config follows it without knowing profiles exist.
    public static string AppDataDir => _profileDir ?? Path.Combine(LocalAppData, AppFolder);

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

    /// <summary>
    /// True when settings are being read from beside the executable.
    ///
    /// <para>⚠️ A profile wins over it. Running a development build from an IDE means running it
    /// out of its build directory, which is exactly where a portable config sits — so without
    /// this the switch that asks for a database of its own would be overruled by the file it is
    /// there to avoid.</para>
    /// </summary>
    public static bool UsingPortableConfig => _profileDir is null && File.Exists(PortableConfigPath);

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

        // A service is installed from a client already pointed at a server, and the installer
        // writes that connection alongside it. Nothing else would be worth running as a service.
        if (AppRuntime.IsService && MachineConfig.GetConnection() is not null) return DbBackend.Postgres;

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

        // ⚠️ A service looks here and stops. It runs as LocalSystem, so the config file below
        // belongs to a profile it has never seen and holds a password protected for an account it
        // is not — reading on past this point would find nothing and report the wrong reason.
        if (AppRuntime.IsService && MachineConfig.GetConnection() is { } fromMachine) return fromMachine;

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

    /// <summary>How wide the capsuleer last dragged the agent panel. Null if never dragged.</summary>
    public static int? GetAgentPanelWidth() => Load().AgentPanelWidth;

    public static void SetAgentPanelWidth(int width)
    {
        var c = Load();
        c.AgentPanelWidth = width;
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

    /// <summary>
    /// Whether this client stays quiet for the alarm actions that happen live — sound, dialog and
    /// the agent speaking.
    ///
    /// <para>⚠️ Deliberately NOT the Alert action. Muting silences what interrupts a person at
    /// this machine; it does not stop the worker recording what happened, so a muted client still
    /// has the whole history waiting when its owner looks. Silencing the record instead would
    /// make "quiet for an hour" mean "blind about that hour", which is not what anybody means.</para>
    ///
    /// <para>Local, and per client. The point is that one machine can be quiet while another is
    /// not, so this cannot live in the shared database with the alarms themselves.</para>
    /// </summary>
    public static bool GetAlarmsMuted() => Load().AlarmsMuted == true;

    public static bool GetShowTrayIcon() => Load().ShowTrayIcon == true;

    public static void SetShowTrayIcon(bool show)
    {
        var c = Load();
        c.ShowTrayIcon = show ? true : null;   // absent rather than false — keeps the file tidy
        Save(c);
    }

    public static void SetAlarmsMuted(bool muted)
    {
        var c = Load();
        c.AlarmsMuted = muted ? true : null;   // absent rather than false — keeps the file tidy
        Save(c);
    }

    /// <summary>
    /// How the Overview screen's sections are arranged, as the layout's own JSON.
    ///
    /// <para>⚠️ Local, and per client, for the same reason the window's size and position are: it
    /// is how one person's screen is laid out, not part of the data. With several clients on one
    /// PostgreSQL database it lived in the shared preference table, so rearranging the sections on
    /// one machine silently rearranged them on every other — including a laptop with room for far
    /// fewer columns than the desktop that made the change.</para>
    ///
    /// <para>Null means "never set here", which is not "set to nothing". That distinction is what
    /// lets a client seed itself once from the old shared preference, so nobody's screen changes
    /// for the upgrade.</para>
    /// </summary>
    public static string? GetOverviewLayout() => Load().OverviewLayout;

    public static void SetOverviewLayout(string? json)
    {
        var c = Load();
        c.OverviewLayout = string.IsNullOrWhiteSpace(json) ? null : json;
        Save(c);
    }

    // ── Cloudflare, for deploying store sites ─────────────────────────────────
    //
    // ⚠️ Per machine, and protected like the database password. The token opens the owner's
    // Cloudflare account; it belongs on the machine they deploy from, not in a database that other
    // clients read and that gets backed up and moved. Syncing a site needs no token.

    /// <summary>The token, or null when none is saved or it cannot be opened on this machine.</summary>
    public static string? GetCloudflareToken()
    {
        var t = SecretStore.Unprotect(Load().CloudflareToken);
        return string.IsNullOrEmpty(t) ? null : t;
    }

    public static bool HasCloudflareToken => !string.IsNullOrEmpty(Load().CloudflareToken);

    /// <summary>How the token is being held, for the screen to state.</summary>
    public static SecretProtection CloudflareTokenProtection =>
        SecretStore.IsProtected(Load().CloudflareToken) ? SecretStore.Available : SecretProtection.None;

    /// <summary>Saves a token, or removes it when given nothing.</summary>
    public static void SetCloudflareToken(string? token)
    {
        var c = Load();
        c.CloudflareToken = string.IsNullOrWhiteSpace(token) ? null : SecretStore.Protect(token.Trim(), "cloudflare");
        Save(c);
    }


    // ── UI state ──────────────────────────────────────────────────────────────
    //
    // A plain key/value bag for the small remembered-view settings: which overlay was showing,
    // which period was chosen, what was left collapsed. Prefer it over new typed fields — the point
    // is that adding a remembered control costs a key and nothing else. The typed members above
    // (the window's geometry, the Overview layout) predate it and are left alone.
    //
    // Read through the UiState class rather than these directly: it does the one-time seeding from
    // the shared preference the setting is moving out of.

    public static string? GetUiState(string key)
        => Load().UiState is { } bag && bag.TryGetValue(key, out var v) ? v : null;

    public static void SetUiState(string key, string? value)
    {
        var c   = Load();
        var bag = c.UiState ?? new Dictionary<string, string>(StringComparer.Ordinal);

        if (value is null) bag.Remove(key);
        else               bag[key] = value;

        c.UiState = bag.Count > 0 ? bag : null;   // absent rather than empty — keeps the file tidy
        Save(c);
    }

    // ── This machine's EVE log setup ──────────────────────────────────────────
    //
    // ⚠️ Null means "never set here", which is not the same as "set to nothing". The
    // difference is what lets a client seed itself once from the old shared preference and
    // never again — see MonitoringSettings. An empty string is a real answer: this machine
    // watches no directories.

    /// <summary>
    /// Log directories supplied by the environment, which override config.json entirely.
    ///
    /// <para>The same bargain as EVECONSOLE_DB_CONNECTION and for the same reason: a worker in a
    /// container has no settings window to be configured from, and the one thing it needs told is
    /// where the logs are mounted. Newline- or semicolon-separated, because a newline is awkward
    /// to put in a docker-compose value.</para>
    ///
    /// <para>⚠️ Env wins over file, never merges — a directory list assembled from two places is
    /// the kind of configuration that looks right and watches the wrong folder.</para>
    /// </summary>
    private static string? EnvDirs(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Replace(';', '\n');
    }

    public static string? GameLogDirsFromEnv => EnvDirs("EVECONSOLE_GAMELOG_DIRS");
    public static string? ChatLogDirsFromEnv => EnvDirs("EVECONSOLE_CHATLOG_DIRS");

    // ⚠️ A service reads the machine config, for the same reason it does for the connection: its
    // profile is not the one the desktop app saved anything into.
    public static string? GetGameLogDirs() =>
        GameLogDirsFromEnv ?? (AppRuntime.IsService ? MachineConfig.GetGameLogDirs() : Load().GameLogDirs);

    public static string? GetChatLogDirs() =>
        ChatLogDirsFromEnv ?? (AppRuntime.IsService ? MachineConfig.GetChatLogDirs() : Load().ChatLogDirs);

    public static void SetGameLogDirs(string? dirs) { var c = Load(); c.GameLogDirs = dirs ?? ""; Save(c); }
    public static void SetChatLogDirs(string? dirs) { var c = Load(); c.ChatLogDirs = dirs ?? ""; Save(c); }

    public static bool? GetGameLogEnabled() => Load().GameLogEnabled;
    public static bool? GetChatLogEnabled() => Load().ChatLogEnabled;

    public static void SetGameLogEnabled(bool on) { var c = Load(); c.GameLogEnabled = on; Save(c); }
    public static void SetChatLogEnabled(bool on) { var c = Load(); c.ChatLogEnabled = on; Save(c); }

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
            // ⚠️ Never into a profile. A profile is empty by definition and stays that way until
            // somebody sets it up; carrying a years-old install into a test database would be a
            // surprise, and a slow one — the old database is copied whole.
            if (_profileDir is not null) return;

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
                ? Read(legacyConfig).Data
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

    /// <summary>
    /// Raised when a write was refused because the file could not be read first — see
    /// <see cref="Save"/>. A delegate because this class runs before the container exists and
    /// knows nothing about logging; wired to the error log in <c>App.axaml.cs</c>.
    ///
    /// <para>⚠️ Refusing silently would be its own fault. A setting that does not stick, with
    /// nothing said, is the kind of thing that gets diagnosed twice.</para>
    /// </summary>
    public static Action<string>? WriteRefused { get; set; }

    /// <summary>What came of trying to read the config file.</summary>
    private enum ReadOutcome
    {
        /// <summary>Read and parsed.</summary>
        Loaded,
        /// <summary>Not there at all: a fresh install, and defaults are the right answer.</summary>
        Missing,
        /// <summary>There, but another process is holding it. Says nothing about the contents.</summary>
        Locked,
        /// <summary>There, and not JSON this app understands.</summary>
        Corrupt,
    }

    /// <summary>The backup <see cref="Save"/> leaves behind: the contents before the last write.</summary>
    private const string BackupSuffix = ".bak";

    // A locked file is almost always locked for a few milliseconds — the other process is
    // writing its own settings. Worth waiting for; not worth waiting long.
    private const int ReadAttempts = 4;
    private const int ReadRetryMs  = 60;

    /// <summary>
    /// Whether the last read ON THIS THREAD failed with the file held by somebody else.
    ///
    /// <para>⚠️ This is what stands between a momentary lock and a wiped config. Every setter here
    /// is read-modify-write — <c>var c = Load(); c.X = …; Save(c);</c> — so a read that quietly
    /// returned defaults produced an object with one field set and everything else null, and the
    /// write that followed put THAT on disk. Which is how a config holding a server address, a
    /// protected password and an API token became four keys and a window position, and the app
    /// that read it next fell back to SQLite and imported the whole SDE into a database nobody
    /// was using.</para>
    ///
    /// <para>Thread-static, and paired with the read rather than passed through twenty call
    /// sites: the two calls are always back to back on one thread, and a setter added later is
    /// covered without anybody remembering to cover it.</para>
    /// </summary>
    [ThreadStatic] private static bool _readWasLocked;

    private static ConfigData Load()
    {
        var (data, outcome) = Read(ConfigPath);
        _readWasLocked = outcome == ReadOutcome.Locked;

        // ⚠️ The backup is the answer when the file itself cannot give one. Falling back to
        // DEFAULTS here is what chose the wrong database engine: nothing was wrong with the
        // settings, they were merely unreadable for a moment, and "no settings" is a very
        // different statement from "could not read the settings".
        if (outcome is ReadOutcome.Locked or ReadOutcome.Corrupt)
        {
            var (backup, backupOutcome) = Read(ConfigPath + BackupSuffix);
            if (backupOutcome == ReadOutcome.Loaded) return backup;
        }

        return data;
    }

    /// <summary>Reads one config file, saying which of the four things happened.</summary>
    private static (ConfigData Data, ReadOutcome Outcome) Read(string path)
    {
        if (!File.Exists(path)) return (new ConfigData(), ReadOutcome.Missing);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // ⚠️ Sharing everything. This read must not be the reason another process cannot
                // write, and it is a few hundred bytes taken in one go — a torn read is not
                // possible here, because a write arrives as a whole file replaced at once.
                using var s = new FileStream(path, FileMode.Open, FileAccess.Read,
                                             FileShare.ReadWrite | FileShare.Delete);
                var data = JsonSerializer.Deserialize<ConfigData>(s, JsonOpts);
                return data is null
                    ? (new ConfigData(), ReadOutcome.Corrupt)   // the file says "null"
                    : (data, ReadOutcome.Loaded);
            }
            catch (JsonException)
            {
                return (new ConfigData(), ReadOutcome.Corrupt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts) return (new ConfigData(), ReadOutcome.Locked);
                Thread.Sleep(ReadRetryMs);
            }
            catch
            {
                return (new ConfigData(), ReadOutcome.Corrupt);
            }
        }
    }

    private static void Save(ConfigData data)
    {
        // ⚠️ Nothing is written when the read that produced this object failed. What is in hand
        // is not the user's settings with one thing changed, it is one thing and a great many
        // nulls, and writing it destroys everything the file held. The change is dropped instead
        // — the setting does not stick, which is a small fault, and the file survives, which is
        // the point. See _readWasLocked.
        if (_readWasLocked)
        {
            _readWasLocked = false;
            try { WriteRefused?.Invoke($"Settings were not saved: {ConfigPath} was in use by another process."); }
            catch { }
            return;
        }

        // The directory of whichever file is in use — writing to app data while reading from
        // beside the executable would silently discard every change the user made.
        var path = ConfigPath;
        var dir  = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(data, JsonOpts);

        // ⚠️ Written beside and swapped in, never written over. A file being overwritten in
        // place is briefly neither the old contents nor the new, and a reader that arrives in
        // that moment sees a truncated file — which is a corrupt config on somebody else's next
        // start. The swap also leaves the previous contents as .bak, which is what a read falls
        // back to when the file itself cannot be read.
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Replace(tmp, path, path + BackupSuffix, ignoreMetadataErrors: true);
            else                   File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            // ⚠️ No fallback to writing over the file directly. The usual reason the swap fails
            // is that somebody holds the target, which is the case this whole path exists to
            // avoid making worse.
            try { File.Delete(tmp); } catch { }
            try { WriteRefused?.Invoke($"Settings could not be saved to {path}: {ex.Message}"); }
            catch { }
        }
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
        // The Cloudflare API token, protected the same way and for the same reason.
        [JsonPropertyName("cloudflareToken")]    public string? CloudflareToken    { get; set; }

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
        [JsonPropertyName("agentPanelWidth")] public int? AgentPanelWidth { get; set; }
        [JsonPropertyName("alarmsMuted")]   public bool? AlarmsMuted   { get; set; }

        // How this client's Overview sections are arranged. Beside the window geometry above and
        // for the same reason: it describes this screen, not the data, and a rearrangement made on
        // a wide desktop should not follow the user onto a laptop.
        [JsonPropertyName("overviewLayout")] public string? OverviewLayout { get; set; }

        // The rest of the remembered view settings, by key. Same reasoning, no new field per
        // setting — see the UiState class.
        [JsonPropertyName("uiState")] public Dictionary<string, string>? UiState { get; set; }

        // ── This machine's EVE log setup ──────────────────────────────────────
        //
        // ⚠️ Local, not in the shared preferences where these used to live. They name
        // directories on a filesystem, and with several clients on one database no single
        // list can be right for all of them: a container reading /mnt/xyz/eve cannot be handed
        // C:\Users\Name\Documents\EVE\logs and asked to make anything of it. The enabled flags
        // come with them, because "this machine imports logs" is the same kind of fact.
        [JsonPropertyName("gameLogDirs")]    public string? GameLogDirs    { get; set; }
        [JsonPropertyName("chatLogDirs")]    public string? ChatLogDirs    { get; set; }
        [JsonPropertyName("gameLogEnabled")] public bool?   GameLogEnabled { get; set; }
        [JsonPropertyName("chatLogEnabled")] public bool?   ChatLogEnabled { get; set; }

        // Per client, like the alarm mute: whether THIS window puts an icon in the tray is a
        // fact about this desktop, not about the data.
        [JsonPropertyName("showTrayIcon")]  public bool? ShowTrayIcon { get; set; }

        [JsonPropertyName("restorePending")] public string? RestorePending { get; set; }
        [JsonPropertyName("relocateTo")]   public string? RelocateTo   { get; set; }
    }
}
