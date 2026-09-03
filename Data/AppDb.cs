using System.Data.Common;
using EveConsole.Services;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace EveConsole.Data;

/// <summary>
/// A connection to whichever database the app is configured for, for the code that talks ADO
/// rather than going through EF.
///
/// <para>Roughly forty places opened a <c>SqliteConnection</c> built from a connection string
/// handed down at construction. That is a provider choice baked into every one of them, and on a
/// server it fails before any SQL runs at all — "Connection string keyword 'host' is not
/// supported" — because a PostgreSQL connection string was being handed to the SQLite driver.
/// The queries themselves are portable; only the object that carries them was not.</para>
///
/// <para>⚠️ Not for the maintenance services. Shrink, relocate, size and the integrity check
/// operate on a FILE — VACUUM, page counts, copying it aside — and stay on SqliteConnection
/// deliberately. They are meaningless against a server and are hidden there, rather than being
/// made to compile against a connection that cannot do what they need.</para>
/// </summary>
public static class AppDb
{
    /// <summary>A closed connection of the right type. The caller opens it, as before.</summary>
    public static DbConnection Connect() =>
        DbEngine.IsPostgres
            ? new NpgsqlConnection(AppConfig.GetPostgresConnection())
            : new SqliteConnection(SqliteMaintenance.ConnectionString(AppConfig.GetDbPath()));

    /// <summary>The connection string for whichever engine is configured.</summary>
    public static string ConnectionString =>
        DbEngine.IsPostgres
            ? AppConfig.GetPostgresConnection() ?? ""
            : SqliteMaintenance.ConnectionString(AppConfig.GetDbPath());
}

public static class DbCommandExtensions
{
    /// <summary>
    /// A command on this connection, replacing <c>new SqliteCommand(sql, conn)</c> — which names
    /// the provider in its constructor and so cannot be handed a server connection.
    /// </summary>
    public static DbCommand Command(this DbConnection conn, string sql, DbTransaction? tx = null)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null) cmd.Transaction = tx;
        return cmd;
    }

    /// <summary>
    /// The equivalent of SqliteCommand's AddWithValue, for a command typed as the provider-neutral
    /// <see cref="DbCommand"/>.
    ///
    /// <para>⚠️ It lives on the command rather than on Parameters because a
    /// <see cref="DbParameterCollection"/> cannot create a parameter — only the command knows what
    /// kind its provider wants. Both engines accept the same <c>@name</c> placeholder syntax, so
    /// the SQL around it needs no change.</para>
    /// </summary>
    public static DbCommand AddWithValue(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return cmd;
    }
}
