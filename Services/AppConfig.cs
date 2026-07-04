using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveCortex.Services;

/// <summary>
/// Persists machine-level config outside the database so it can be read before the DB
/// connection is opened (e.g. splash screen monitor, DB path).
/// Stored at %LocalAppData%\EveCortex\config.json.
/// </summary>
public static class AppConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EveCortex", "config.json");

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string DefaultDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EveCortex", "EveCortex.db");

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

    // ── Internal ──────────────────────────────────────────────────────────────

    private static ConfigData Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                using var s = File.OpenRead(ConfigPath);
                return JsonSerializer.Deserialize<ConfigData>(s, JsonOpts) ?? new ConfigData();
            }
        }
        catch { }
        return new ConfigData();
    }

    private static void Save(ConfigData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(data, JsonOpts));
    }

    private sealed class ConfigData
    {
        [JsonPropertyName("dbPath")]  public string? DbPath  { get; set; }
        [JsonPropertyName("windowX")] public int?    WindowX { get; set; }
        [JsonPropertyName("windowY")] public int?    WindowY { get; set; }
    }
}
