using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EveConsole.Services;

/// <summary>
/// Persists machine-level config outside the database so it can be read before the DB
/// connection is opened (e.g. splash screen monitor, DB path).
/// Stored at %LocalAppData%\EVE Console Data\config.json on Windows, and
/// ~/.local/share/EveConsole/config.json on Linux.
/// </summary>
public static class AppConfig
{
    private const string AppFolder     = "EveConsole";     // Windows: the install folder. Elsewhere: the data folder
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
            : Path.Combine(BaseDir, "Profiles", Sanitise(value));

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
    public static string AppDataDir => _profileDir ?? BaseDir;

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

    // ── The data out of the install folder (Windows) ─────────────────────────────

    /// <summary>
    /// Where the data lives on Windows: beside the install folder, never in it.
    ///
    /// <para>⚠️ Velopack installs into %LocalAppData%\EveConsole and treats that folder as its own.
    /// Its installer, run over an existing install, moves the whole folder aside and deletes it
    /// once the new install succeeds, and an uninstall deletes it outright. The data used to live
    /// in that same folder — config.json with a server address and password, SQLite databases,
    /// backups, profiles — so reinstalling wiped it. Velopack has no option to keep files, and
    /// installing somewhere else would mean a new package id, which Velopack treats as another
    /// app. So the data moves instead: once, by the installed copy, on its first start.</para>
    /// </summary>
    private const string WindowsDataFolder = "EVE Console Data";

    /// <summary>Left in the install folder once the data has gone, for a person looking there.</summary>
    internal const string MovedNoteName = "Your EVE Console data has moved.txt";

    private static string? _baseDir;

    /// <summary>What the move out of the install folder could not do, for the error log (App
    /// writes it once there is one). Null when there was nothing to move, or it moved.</summary>
    public static string? DataMoveProblem { get; private set; }

    private static string InstallDir => Path.Combine(LocalAppData, AppFolder);
    private static string DataDir    => OperatingSystem.IsWindows()
        ? Path.Combine(LocalAppData, WindowsDataFolder)
        : Path.Combine(LocalAppData, AppFolder);

    /// <summary>The ordinary data directory, before any profile. Settled by
    /// <see cref="MoveDataOutOfInstallFolder"/> at start; a process that never calls it (a tool, a
    /// harness) looks without moving anything.</summary>
    private static string BaseDir => _baseDir ??= ResolveDataFolder(InstallDir, DataDir, move: false).BaseDir;

    /// <summary>
    /// Moves the data out of Velopack's install folder, once. Called first thing in
    /// <c>Program.Main</c>, before a profile, a lock file or a setting has been touched.
    ///
    /// <para>⚠️ Only the INSTALLED copy moves anything — the one running from the install folder's
    /// own <c>current</c> directory. A development build run beside an older installed copy would
    /// otherwise carry the installed copy's data out from under it, and that copy would start as a
    /// fresh install. Any other build just uses whichever folder holds the data.</para>
    /// </summary>
    public static void MoveDataOutOfInstallFolder()
    {
        if (!OperatingSystem.IsWindows()) return;

        var install     = InstallDir;
        var fromInstall = AppContext.BaseDirectory.StartsWith(
            Path.Combine(install, "current") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        (_baseDir, DataMoveProblem) = ResolveDataFolder(install, DataDir, move: fromInstall);
    }

    /// <summary>
    /// Which folder holds the data — moving it out of <paramref name="installDir"/> first when
    /// <paramref name="move"/> allows. Takes both folders so it can be tried on scratch ones.
    ///
    /// <para>All or nothing: a file held open means another copy of the app is running on this
    /// data, and nothing moves; a folder that will not move puts back whatever already had, so the
    /// data is never split between two places. The old folder is used for the run either way, and
    /// the next start tries again.</para>
    /// </summary>
    internal static (string BaseDir, string? Problem) ResolveDataFolder(string installDir, string dataDir, bool move)
    {
        // One folder for both — Linux, where the AppImage lives somewhere else entirely.
        if (string.Equals(Path.GetFullPath(installDir).TrimEnd('\\', '/'),
                          Path.GetFullPath(dataDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return (dataDir, null);

        try
        {
            var ours = Directory.Exists(installDir)
                ? Directory.EnumerateFileSystemEntries(installDir).Where(e => !StaysInInstallFolder(e)).ToList()
                : [];
            if (ours.Count == 0) return (dataDir, null);   // a fresh machine, or moved already

            if (File.Exists(Path.Combine(dataDir, "config.json")))
                return (dataDir, $"{dataDir} is in use, and the install folder {installDir} still holds " +
                                 $"{Names(ours)}, which were left where they are.");

            if (!move) return (installDir, null);

            string Dest(string e) => Path.Combine(dataDir, Path.GetFileName(e));

            if (ours.FirstOrDefault(e => File.Exists(Dest(e)) || Directory.Exists(Dest(e))) is { } clash)
                return (installDir, $"Not moved: {Dest(clash)} is already there.");

            foreach (var f in ours.Where(File.Exists))
            {
                try { using var _ = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None); }
                catch (IOException ex)
                {
                    return (installDir, $"Not moved this time: {Path.GetFileName(f)} is in use, probably by " +
                                        $"another copy of the app ({ex.Message}).");
                }
            }

            // Folders first: one with a file held open inside cannot be renamed, and finding that
            // out before any file has gone leaves less to put back.
            var moved = new List<(string From, string To)>();
            try
            {
                Directory.CreateDirectory(dataDir);
                foreach (var e in ours.OrderBy(File.Exists))
                {
                    var d = Dest(e);
                    if (Directory.Exists(e)) Directory.Move(e, d); else File.Move(e, d);
                    moved.Add((e, d));
                }
            }
            catch (Exception ex)
            {
                foreach (var (from, to) in Enumerable.Reverse(moved))
                    try { if (Directory.Exists(to)) Directory.Move(to, from); else File.Move(to, from); } catch { }
                return (installDir, $"Not moved this time, and put back as it was: {ex.Message}");
            }

            RebaseSettings(installDir, dataDir);
            try
            {
                File.WriteAllText(Path.Combine(installDir, MovedNoteName),
                    $"EVE Console keeps its settings and data in{Environment.NewLine}{Environment.NewLine}" +
                    $"    {dataDir}{Environment.NewLine}{Environment.NewLine}" +
                    $"This folder holds only the program, which installing and updating replace as a whole.{Environment.NewLine}");
            }
            catch { /* a courtesy, not part of the move */ }

            return (dataDir, null);
        }
        catch (Exception ex)
        {
            // Unforeseen: stay where the data was last known to be.
            return (Directory.Exists(installDir) ? installDir : dataDir, $"Not moved: {ex.Message}");
        }
    }

    /// <summary>What belongs to Velopack, or to the move itself, and never leaves the install folder.</summary>
    private static bool StaysInInstallFolder(string path)
    {
        var name = Path.GetFileName(path);
        if (Directory.Exists(path))
            return name.Equals("current", StringComparison.OrdinalIgnoreCase)
                || name.Equals("packages", StringComparison.OrdinalIgnoreCase);

        return name.StartsWith('.')
            || name.Equals(MovedNoteName, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)      // Update.exe, and the launcher stub
            || name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("velopack", StringComparison.OrdinalIgnoreCase);
    }

    private static string Names(IEnumerable<string> paths) =>
        string.Join(", ", paths.Select(Path.GetFileName).Take(8));

    /// <summary>
    /// Points a setting that named a place inside the old folder at the same place in the new one:
    /// a database at the default location, a profile's own database. ⚠️ A path anywhere else — a
    /// database the user moved elsewhere — is theirs, and is left exactly as it is.
    /// </summary>
    private static void RebaseSettings(string installDir, string dataDir)
    {
        var files = new List<string> { Path.Combine(dataDir, "config.json"), Path.Combine(dataDir, "config.json" + BackupSuffix) };
        var profiles = Path.Combine(dataDir, "Profiles");
        if (Directory.Exists(profiles))
            foreach (var p in Directory.EnumerateDirectories(profiles))
                files.AddRange([Path.Combine(p, "config.json"), Path.Combine(p, "config.json" + BackupSuffix)]);

        foreach (var f in files.Where(File.Exists))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(f)) is { } node && Rebase(node, installDir, dataDir))
                    File.WriteAllText(f, node.ToJsonString(JsonOpts));
            }
            catch { /* left as it was: whatever it names can still be changed from Settings */ }
        }
    }

    private static bool Rebase(JsonNode node, string from, string to)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, child) in o.ToList())
                {
                    if (child is JsonValue v && v.TryGetValue<string>(out var s) && Inside(s, from) is { } rest)
                    { o[key] = to + rest; changed = true; }
                    else if (child is not null) changed |= Rebase(child, from, to);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && Inside(s, from) is { } rest)
                    { a[i] = to + rest; changed = true; }
                    else if (a[i] is { } child) changed |= Rebase(child, from, to);
                }
                break;
        }
        return changed;
    }

    /// <summary>What follows <paramref name="dir"/> in <paramref name="path"/>, separator
    /// included, when the path lies inside it; otherwise null.</summary>
    private static string? Inside(string path, string dir)
    {
        var d = dir.TrimEnd('\\', '/');
        if (path.Equals(d, StringComparison.OrdinalIgnoreCase)) return "";
        return path.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase)
            ? path[d.Length..]
            : null;
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
