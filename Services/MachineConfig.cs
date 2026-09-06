using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveConsole.Services;

/// <summary>
/// The little that a Windows service needs to know, kept where a service can read it.
///
/// <para>A service runs as LocalSystem, which is a different account with a different profile —
/// so it cannot see <c>%LOCALAPPDATA%\EveConsole\config.json</c>, and could not decrypt the
/// PostgreSQL password in it if it could: that is protected at CurrentUser scope, deliberately, and
/// only the account that saved it can open it.</para>
///
/// <para>⚠️ So the connection string is re-protected at MACHINE scope and written here. Say what
/// that costs, because it is a real reduction and an invisible one: a LocalMachine blob can be
/// decrypted by <em>any</em> process on this computer, not only by the service. It is the ordinary
/// bargain for service credentials — the alternatives are a plaintext password in the registry, or
/// asking somebody to type their Windows account password into a dialog — but it is weaker than
/// the user-scoped secret it is copied from, and nobody should discover that by reading the code.
/// </para>
///
/// <para>Written only while elevated, during service installation. The directory's default ACL
/// under ProgramData allows administrators to write and everyone to read, which matches what the
/// protection above already admits.</para>
/// </summary>
public static class MachineConfig
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EveConsole");

    public static string FilePath => Path.Combine(Folder, "service.json");

    private sealed class Data
    {
        /// <summary>Base64 of the DPAPI (LocalMachine) blob holding the whole connection string,
        /// password included.</summary>
        public string? Connection { get; set; }

        /// <summary>Newline-separated, and plain: a directory name is not a secret.</summary>
        public string? GameLogDirs { get; set; }
        public string? ChatLogDirs { get; set; }
    }

    /// <summary>
    /// The connection string for a service to use, or null when there is none to be had.
    ///
    /// <para>Null rather than throwing: the caller's next step reports "no database configured"
    /// far more usefully than a decryption failure would.</para>
    /// </summary>
    public static string? GetConnection()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var data = Read();
            if (data?.Connection is not { Length: > 0 } blob) return null;

            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.LocalMachine));
        }
        catch
        {
            // A blob written on another machine, or by an older build, cannot be opened here. The
            // service says it has no database, which is true and actionable.
            return null;
        }
    }

    public static string? GetGameLogDirs() => Read()?.GameLogDirs;
    public static string? GetChatLogDirs() => Read()?.ChatLogDirs;

    /// <summary>
    /// Which database the service is pointed at, in words fit for a settings page.
    ///
    /// <para>⚠️ Host and database only, never the password — this is displayed. The stored value is
    /// readable by anything on the machine as it is; there is no reason for the app to also put it
    /// on screen.</para>
    /// </summary>
    public static string? DescribeConnection()
    {
        var connection = GetConnection();
        if (connection is null) return null;

        try
        {
            var b = new Npgsql.NpgsqlConnectionStringBuilder(connection);
            return $"{b.Database} on {b.Host}";
        }
        catch { return "unreadable"; }
    }

    /// <summary>
    /// Whether the service would connect to the same place this client does.
    ///
    /// <para>⚠️ The whole string, password included, because a changed password matters as much as
    /// a changed host: the service would fail to connect, and <c>sc failure</c> would restart it
    /// into the same failure until somebody read the log. Both sides are produced by the same
    /// AppConfig call, so they are byte-identical while in step.</para>
    ///
    /// <para>True when there is nothing installed to disagree with.</para>
    /// </summary>
    public static bool MatchesConnection(string? current)
    {
        var stored = GetConnection();
        if (stored is null) return true;

        return string.Equals(stored, current, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes what the service will need. ⚠️ Requires elevation — ProgramData is not writable by
    /// an ordinary account — so this is called from the elevated half of installation, never from
    /// the running app.
    /// </summary>
    public static void Write(string connection, string? gameLogDirs, string? chatLogDirs)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The machine config exists for the Windows service.");

        Directory.CreateDirectory(Folder);

        var payload = new Data
        {
            Connection = Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(connection), null, DataProtectionScope.LocalMachine)),
            GameLogDirs = gameLogDirs,
            ChatLogDirs = chatLogDirs,
        };

        File.WriteAllText(FilePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Removes it, so uninstalling the service leaves no copy of the credential behind.</summary>
    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* best effort while elevated */ }
    }

    private static Data? Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath))
                : null;
        }
        catch { return null; }
    }
}
