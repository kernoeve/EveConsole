using System.Data.Common;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
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
            ? new NpgsqlConnection(PostgresConnectionString(AppConfig.GetPostgresConnection() ?? ""))
            : new SqliteConnection(SqliteMaintenance.ConnectionString(AppConfig.GetDbPath()));

    /// <summary>
    /// SQLite's bulk-import tuning, and deliberately nothing at all on a server.
    ///
    /// <para>⚠️ PRAGMA is not SQL PostgreSQL parses — it fails the statement with "syntax
    /// error at or near PRAGMA" — and there is nothing to translate it into. These settings
    /// trade durability for speed inside one process against one file; the equivalent on a
    /// server is configuration its administrator owns, not something a client should be
    /// reaching for mid-import.</para>
    ///
    /// <para>journal_mode and busy_timeout are already set per connection by
    /// DisableForeignKeysInterceptor; synchronous, cache_size and temp_store are not, and they
    /// matter once inserts are batched rather than done a row at a time.</para>
    /// </summary>
    public static async Task TuneForBulkImportAsync(
        Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade db, CancellationToken ct)
    {
        if (!DbEngine.IsSqlite) return;

        await db.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL",   ct);
        await db.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL", ct);
        await db.ExecuteSqlRawAsync("PRAGMA cache_size=20000",   ct);
        await db.ExecuteSqlRawAsync("PRAGMA temp_store=MEMORY",  ct);
    }

    /// <summary>
    /// A parameter of the type the configured provider wants.
    ///
    /// <para>⚠️ A <c>SqliteParameter</c> handed to EF is passed straight through to the
    /// command, so on a server Npgsql is given an object it cannot use. Unlike a wrong
    /// connection string this does not announce itself as a configuration problem: the query
    /// simply fails on a screen nobody was looking at.</para>
    ///
    /// <para>Both providers take the same <c>@name</c> placeholders, so only the object changes.
    /// Dates still reach PostgresParameterInterceptor, these being EF commands, so UTC
    /// normalisation is handled there rather than repeated here.</para>
    /// </summary>
    public static DbParameter Param(string name, object? value) =>
        DbEngine.IsPostgres
            ? new NpgsqlParameter(name, value ?? DBNull.Value)
            : new SqliteParameter(name, value ?? DBNull.Value);

    /// <summary>The connection string for whichever engine is configured.</summary>
    public static string ConnectionString =>
        DbEngine.IsPostgres
            ? PostgresConnectionString(AppConfig.GetPostgresConnection() ?? "")
            : SqliteMaintenance.ConnectionString(AppConfig.GetDbPath());

    /// <summary>
    /// The user's connection string, plus the session settings this app's SQL depends on.
    ///
    /// <para>SQLite stores a DateTimeOffset as text and every row in these databases carries
    /// <c>+00:00</c>, so the first characters of it are a UTC date. PostgreSQL stores an instant
    /// and renders it on demand, in the session's time zone and DateStyle. Left at the server's
    /// defaults the same query groups by the server's idea of a month, or formats the date as
    /// <c>09/04/2026</c> — a substring of which is a different answer that still looks like a
    /// date.</para>
    ///
    /// <para>⚠️ In the connection string rather than an interceptor, because
    /// <see cref="Connect"/> hands out a plain ADO connection EF never sees. An interceptor would
    /// have pinned the session for EF's queries and quietly left the rest on the server's
    /// defaults — the worst shape for a bug of this kind, with most of the app agreeing and a
    /// few screens not.</para>
    ///
    /// <para>A connection string that already sets Options is left alone: somebody who went to
    /// that trouble meant it.</para>
    /// </summary>
    public static string PostgresConnectionString(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return configured;
        try
        {
            var b = new NpgsqlConnectionStringBuilder(configured);
            if (string.IsNullOrWhiteSpace(b.Options))
                b.Options = "-c timezone=UTC -c datestyle=ISO,MDY";
            return b.ConnectionString;
        }
        catch { return configured; }
    }
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

        // ⚠️ Dates are normalised to UTC here as well as in PostgresParameterInterceptor,
        // because these commands never reach it: a connection from AppDb.Connect() is plain ADO
        // and EF, which owns the interceptor, is not in the path at all. The two choke points
        // together are what covers every query in the app — miss either and Npgsql rejects
        // the write with "only offset 0 (UTC) is supported".
        p.Value = value switch
        {
            null                                              => DBNull.Value,
            DateTimeOffset dto when dto.Offset != TimeSpan.Zero => dto.ToUniversalTime(),
            DateTime dt when dt.Kind == DateTimeKind.Local     => dt.ToUniversalTime(),
            _                                                 => value,
        };

        cmd.Parameters.Add(p);
        return cmd;
    }
}
