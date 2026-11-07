using System.Text.Json;

namespace EveConsole.Services;

/// <summary>
/// The saved chat and voice logins and client certificates on SQLite, in a file of their own beside the settings. On
/// PostgreSQL they are in the database (<see cref="SharedLogins"/>), and what was here moves there.
///
/// <para>⚠️ Not in config.json. Every build reads that whole file and writes it back, keeping only the fields it knows,
/// so one older build run once against the same folder took every saved password and certificate with it, and the next
/// connection made a new certificate and offered no password. Nothing but this class touches this file.</para>
///
/// <para>Holds what <see cref="SecretStore.Protect"/> returned, never a plain password where the platform has a store.
/// Read afresh each time: there are few entries, they are read rarely, and another client may have changed them.</para>
/// </summary>
internal static class ServiceSecretsFile
{
    private const string BackupSuffix = ".bak";
    private const int ReadAttempts = 5;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(AppConfig.AppDataDir, "service-secrets.json");

    /// <summary>
    /// Everything saved, or the backup when the file itself cannot be read.
    ///
    /// <para>⚠️ Throws rather than answer "nothing is saved" when neither can be read. Nothing saved means offering no
    /// password and making a new certificate, and writing that back would destroy what the file may still hold.</para>
    /// </summary>
    public static Dictionary<string, string> Read()
    {
        var path = FilePath;
        var backup = path + BackupSuffix;
        if (!File.Exists(path) && !File.Exists(backup)) return [];

        if (TryRead(path) is { } data) return data;
        if (TryRead(backup) is { } kept) return kept;

        throw new IOException($"Saved logins could not be read from {path}.");
    }

    /// <summary>Swapped in whole, never written over, leaving the previous contents as the backup. Throws if it fails, as the caller has to know.</summary>
    public static void Write(Dictionary<string, string> secrets)
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(secrets, JsonOpts));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            if (File.Exists(path)) File.Replace(tmp, path, path + BackupSuffix, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* the failure that matters is the one going up */ }
            throw;
        }
    }

    /// <summary>Removes the file and its backup, once what they held is safely somewhere else.</summary>
    public static void Delete()
    {
        File.Delete(FilePath);
        File.Delete(FilePath + BackupSuffix);
    }

    /// <summary>The file's contents, or null if it is not there or cannot be read. A moment's lock by another client is waited out.</summary>
    private static Dictionary<string, string>? TryRead(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(stream, JsonOpts);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts) return null;
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
