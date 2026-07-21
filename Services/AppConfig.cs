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

    private static string ConfigPath => Path.Combine(AppDataDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string DefaultDbPath => Path.Combine(AppDataDir, DbFileName);

    // ── Read ─────────────────────────────────────────────────────────────────

    public static string GetDbPath()       => Load().DbPath ?? DefaultDbPath;
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

    public static void SetWindowPosition(int x, int y)
    {
        var c = Load();
        c.WindowX = x;
        c.WindowY = y;
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
        Directory.CreateDirectory(AppDataDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(data, JsonOpts));
    }

    private sealed class ConfigData
    {
        [JsonPropertyName("dbPath")]  public string? DbPath  { get; set; }
        [JsonPropertyName("windowX")] public int?    WindowX { get; set; }
        [JsonPropertyName("windowY")] public int?    WindowY { get; set; }
    }
}
