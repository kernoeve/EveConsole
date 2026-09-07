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
    /// <summary>
    /// The engine's name for the physical address of a row.
    ///
    /// <para>⚠️ Safe ONLY within a single statement, which is all the callers want: they
    /// delete or sample an arbitrary N rows and need a handle on each. SQLite's rowid is stable
    /// and can be stored; PostgreSQL's ctid is not — it moves when the row is updated and
    /// when the table is vacuumed — so nothing may keep one or compare it across
    /// statements.</para>
    /// </summary>
    public static string RowId => DbEngine.IsPostgres ? "ctid" : "rowid";

    /// <summary>The scalar "smaller of these two" function — not the aggregate.</summary>
    ///
    /// <remarks>⚠️ SQLite overloads MIN: one argument is the aggregate, two or more is the
    /// scalar. PostgreSQL keeps them apart, reserving MIN for the aggregate and calling the scalar
    /// LEAST, so a two-argument MIN there reports "function min(integer, integer) does not
    /// exist".</remarks>
    public static string LeastFn    => DbEngine.IsPostgres ? "LEAST"    : "MIN";

    /// <summary>The scalar "larger of these two" function — not the aggregate.</summary>
    public static string GreatestFn => DbEngine.IsPostgres ? "GREATEST" : "MAX";

    /// <summary>
    /// Makes LIKE mean on PostgreSQL what it has always meant on SQLite.
    ///
    /// <para>⚠️ This application was written against SQLite, whose LIKE ignores case for
    /// ASCII. PostgreSQL's LIKE does not, and ILIKE is its case-insensitive form. Every LIKE here
    /// is a person searching for a name — an item, a character, a system — so the
    /// SQLite behaviour is the intended one, and the PostgreSQL behaviour is a regression that
    /// reports NO ERROR: the search simply stops finding things, which is how it was noticed.</para>
    ///
    /// <para>Done centrally rather than at each of the 47 call sites for two reasons. Most of
    /// them are <c>const</c> strings, which cannot call a method at all. And EF generates LIKE
    /// itself from <c>Contains</c>, so a call-site sweep would fix the hand-written half and
    /// leave the generated half behaving differently — the worst of both.</para>
    ///
    /// <para>The scan skips single-quoted literals, so a search term containing the word is left
    /// alone, and it will not touch an identifier such as <c>"Like"</c> or a LIKE that is already
    /// an ILIKE, which makes it safe to apply twice.</para>
    /// </summary>
    public static string CaseInsensitiveLike(string sql)
    {
        if (!DbEngine.IsPostgres || sql.Length == 0) return sql;
        if (sql.IndexOf("LIKE", StringComparison.OrdinalIgnoreCase) < 0) return sql;

        var sb = new System.Text.StringBuilder(sql.Length + 16);
        var inLiteral = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];

            if (inLiteral)
            {
                sb.Append(c);
                // A doubled quote inside a literal closes and reopens it, which this treats as
                // two transitions and lands in the same place.
                if (c == '\'') inLiteral = false;
                continue;
            }

            // ⚠️ Comments are copied over whole, without looking inside them. An apostrophe in
            // prose — "Which hop's flag names the corp hangar division" — is not the start of a
            // string literal, but this read it as one, went into literal mode and never came out,
            // so every LIKE after that comment was left alone. That is exactly what happened to
            // the assets query: one apostrophe near the top of its CTE, and every text filter in
            // the tool stayed case-sensitive while the rest of the application was fine.
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var eol = sql.IndexOf('\n', i);
                if (eol < 0) { sb.Append(sql, i, sql.Length - i); break; }
                sb.Append(sql, i, eol - i + 1);
                i = eol;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) { sb.Append(sql, i, sql.Length - i); break; }
                sb.Append(sql, i, end + 2 - i);
                i = end + 1;
                continue;
            }

            if (c == '\'') { inLiteral = true; sb.Append(c); continue; }

            if ((c is 'L' or 'l')
                && i + 4 <= sql.Length
                && string.Compare(sql, i, "LIKE", 0, 4, StringComparison.OrdinalIgnoreCase) == 0
                && (i == 0 || !IsWordChar(sql[i - 1]))
                && (i + 4 == sql.Length || !IsWordChar(sql[i + 4])))
            {
                sb.Append("ILIKE");
                i += 3;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();

        // The quote counts as a word character so a quoted identifier is never rewritten, and
        // the I of an existing ILIKE stops it matching a second time.
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '"';
    }

    /// <summary>
    /// "True only if true in every row of the group", for a boolean column.
    ///
    /// <para>⚠️ MIN does this on SQLite, where a boolean is an integer and MIN over 0 and 1
    /// says exactly that. PostgreSQL has a real boolean type and no min(boolean) at all; bool_and
    /// is its name for the same thing, and it returns a boolean, which is what an entity property
    /// expects to read back.</para>
    /// </summary>
    public static string AllTrue(string column) =>
        DbEngine.IsPostgres ? $"bool_and({column})" : $"MIN({column})";

    /// <summary>
    /// Whether a save failed because a unique index already held those rows.
    ///
    /// <para>⚠️ Not an error everywhere it happens. Several clients can now be pointed at one
    /// database, and two of them tailing the same EVE log folder will parse the same lines and
    /// try to write the same rows — the unique index on (SourceFile, LineNumber) is what makes
    /// that safe rather than duplicating. The loser has nothing to fix and nothing to report: the
    /// winner committed exactly the rows it was going to, and both read on from the new cursor
    /// next pass. Logging it would fill the error log with the sound of the design working.</para>
    ///
    /// <para>Told apart by SQLSTATE on the server (23505 is unique_violation) and by result code
    /// on SQLite (19 is SQLITE_CONSTRAINT), because the message text differs between the two and
    /// matching on it would be a guess.</para>
    /// </summary>
    public static bool IsUniqueViolation(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: "23505" }) return true;
            if (e is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }) return true;
        }
        return false;
    }

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
        cmd.CommandText = AppDb.CaseInsensitiveLike(sql);
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
