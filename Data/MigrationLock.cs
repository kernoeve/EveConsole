using EveConsole.Services;
using Npgsql;

namespace EveConsole.Data;

/// <summary>
/// Serialises the schema work at startup across every client pointed at one database.
///
/// <para>⚠️ A different lock from the worker lease, and deliberately a different question. "Who
/// migrates" is not "who polls": the first client to start has to bring the schema up whether or
/// not it ends up doing the background work, and tying the two together would mean a client
/// starting while no worker exists either skips the migration or races one.</para>
///
/// <para>⚠️ Blocking, not <c>try</c>. A client that cannot take this lock must wait, because the
/// alternative is running against a schema another client is halfway through altering. The wait is
/// short: what it guards is a handful of DDL statements against tables that, on any database but a
/// brand-new one, already exist.</para>
///
/// <para>On SQLite this does nothing. <see cref="SingleInstance"/> holds an exclusive handle on a
/// file beside the database there, so there is no second process to serialise against.</para>
/// </summary>
public sealed class MigrationLock : IDisposable
{
    /// <summary>
    /// Neighbour of the lease's key, and necessarily distinct from it. Sharing one would make a
    /// client wait for the schema behind whichever client happened to be polling — which is to say
    /// forever, since the worker holds its lease for as long as it runs.
    /// </summary>
    private const long Key = 0x4556_4543_4F4E_5302;   // "EVECONS" + 2

    private NpgsqlConnection? _conn;

    private MigrationLock(NpgsqlConnection? conn) => _conn = conn;

    /// <summary>
    /// Waits for the right to change the schema, and holds it until disposed.
    ///
    /// <para>Never throws. A lock that cannot be taken — no server, bad credentials — returns an
    /// empty holder and lets startup proceed to fail on the schema work itself, where the error
    /// says what is actually wrong. Failing here would replace a clear message with a vague one.</para>
    /// </summary>
    public static MigrationLock Acquire()
    {
        if (!DbEngine.IsPostgres) return new MigrationLock(null);

        try
        {
            var cs = AppConfig.GetPostgresConnection();
            if (string.IsNullOrWhiteSpace(cs)) return new MigrationLock(null);

            // Unpooled, like the lease's: an advisory lock belongs to the session that took it, so
            // a connection returned to the pool would take the lock with it.
            var b = new NpgsqlConnectionStringBuilder(AppDb.PostgresConnectionString(cs)) { Pooling = false };
            var conn = new NpgsqlConnection(b.ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_advisory_lock(@k)";
            cmd.Parameters.AddWithValue("k", Key);
            cmd.ExecuteNonQuery();

            return new MigrationLock(conn);
        }
        catch
        {
            return new MigrationLock(null);
        }
    }

    public void Dispose()
    {
        if (_conn is null) return;

        // Closing the session releases the lock on its own; unlocking first is what keeps a
        // connection that lingers from holding the next client up.
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT pg_advisory_unlock(@k)";
            cmd.Parameters.AddWithValue("k", Key);
            cmd.ExecuteNonQuery();
        }
        catch { /* the dispose below does the same job */ }

        try { _conn.Dispose(); } catch { }
        _conn = null;
    }
}
