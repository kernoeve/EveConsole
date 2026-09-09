using Npgsql;

namespace EveConsole.ViewModels;

/// <summary>
/// The parts of a PostgreSQL connection, kept apart from the single string the app stores.
///
/// <para>Asking for host, port, database, user and password separately rather than for a
/// connection string means somebody who has never written one can still fill it in, and it is
/// what lets the password be shown in a masked box instead of sitting in the middle of a line of
/// text the user is expected to edit by hand.</para>
/// </summary>
public sealed class PostgresSettings
{
    public string Host     { get; set; } = "";
    public int    Port     { get; set; } = 5432;
    public string Database { get; set; } = "eveconsole";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>
    /// ⚠️ A timeout is set deliberately. The default is long enough that a typo in the host looks
    /// to the user like the app has hung rather than like a wrong address.
    /// </summary>
    public string ToConnectionString() => new NpgsqlConnectionStringBuilder
    {
        Host     = Host.Trim(),
        Port     = Port,
        Database = Database.Trim(),
        Username = Username.Trim(),
        Password = Password,
        Timeout  = 15,
    }.ConnectionString;

    /// <summary>Reads back a stored connection string so the boxes show what is in use.</summary>
    public static PostgresSettings FromConnectionString(string? cs)
    {
        var s = new PostgresSettings();
        if (string.IsNullOrWhiteSpace(cs)) return s;

        try
        {
            var b = new NpgsqlConnectionStringBuilder(cs);
            s.Host     = b.Host ?? "";
            s.Port     = b.Port;
            s.Database = b.Database ?? "";
            s.Username = b.Username ?? "";
            s.Password = b.Password ?? "";
        }
        catch
        {
            // A connection string we cannot parse is left for the user to retype rather than
            // half-loaded into the boxes, which would look like it had been understood.
        }
        return s;
    }
}
