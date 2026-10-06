using EveConsole.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Data;

/// <summary>
/// Which build a database was last brought up by — the version startup compares against its own
/// before it touches the schema, and asks about before it upgrades one.
/// </summary>
/// <remarks>
/// <para>On PostgreSQL that is the worker lease's row (<see cref="WorkerLease.ReadStatusAsync"/>),
/// written by whichever build takes the background processing, which is the build that brings the
/// schema up.</para>
///
/// <para>On SQLite it is the file's own <c>PRAGMA user_version</c>: an integer SQLite keeps in the
/// header for exactly this, read without any table and written without any schema. Nothing set it
/// before this class, so every existing file reads 0 — "older than tracking", which startup treats
/// as an older version to ask about.</para>
/// </remarks>
public static class DatabaseVersion
{
    /// <summary>What a SQLite file says about itself.</summary>
    /// <param name="Fresh">No database yet, or one with no tables: a first start, nothing to ask.</param>
    /// <param name="Version">The build that last brought it up; null when it never said.</param>
    public sealed record SqliteState(bool Fresh, string? Version);

    /// <summary>
    /// Reads a SQLite file's recorded version, without writing anything: opened read-only, so a
    /// user who declines the upgrade leaves the file exactly as it was. Null when it cannot be read
    /// — the start that follows will meet whatever is wrong with it and say so itself.
    /// </summary>
    public static SqliteState? ReadSqlite(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0) return new SqliteState(true, null);

            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();

            using var tables = conn.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'";
            if (Convert.ToInt64(tables.ExecuteScalar()) == 0) return new SqliteState(true, null);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            var stamp = Convert.ToInt32(cmd.ExecuteScalar());
            return new SqliteState(false, Decode(stamp));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Records this build's version in the SQLite file, once its schema is brought up.</summary>
    public static void StampSqlite(AppDbContext db)
    {
        if (Encode(AppVersion.Number) is not int stamp) return;
        // A pragma takes no parameters; the value is our own integer. Built in a variable so the
        // raw-SQL analyzers see a string, not an interpolation at the call.
        var sql = "PRAGMA user_version = " + stamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        db.Database.ExecuteSqlRaw(sql);
    }

    /// <summary>"0.9.17" → 9017: major × 1,000,000 + minor × 1,000 + patch. Null for anything that
    /// is not three numbers each under 1,000 — then nothing is recorded rather than a wrong number.</summary>
    internal static int? Encode(string version)
    {
        var parts = version.Split('.');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch)) return null;
        if (major is < 0 or > 999 || minor is < 0 or > 999 || patch is < 0 or > 999) return null;
        return major * 1_000_000 + minor * 1_000 + patch;
    }

    /// <summary>The other way: 9017 → "0.9.17"; 0, never set, → null.</summary>
    internal static string? Decode(int stamp) =>
        stamp <= 0 ? null : $"{stamp / 1_000_000}.{stamp / 1_000 % 1_000}.{stamp % 1_000}";
}
