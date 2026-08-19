using Microsoft.Data.Sqlite;

namespace EveConsole.Services;

/// <summary>What a relocation did, for the Database tab to report once the app is up.</summary>
public sealed record RelocationResult(bool Ran, bool Succeeded, string Message);

/// <summary>
/// Moves the database file to a new path.
///
/// <para><b>⚠️ Why this cannot happen while the app runs.</b> It used to: Move called
/// <c>File.Copy</c> and Rename called <c>File.Move</c>, both against a database the app had open,
/// with services writing to it. Two things went wrong and neither announced itself. The copy took
/// the main file only, so every transaction still sitting in the <c>-wal</c> was missing from it.
/// The move relocated the main file and left <c>-wal</c> and <c>-shm</c> behind at the old path,
/// stranding those same transactions where nothing would look for them. In both cases the app then
/// restarted onto the new file and the loss was invisible — the database opened cleanly, just
/// missing whatever had not been checkpointed.</para>
///
/// <para>So the button records the request and restarts, and the work happens here on the way back
/// up, before the DI container exists and therefore before anything can be writing. The WAL is
/// folded in first, which is the step whose absence caused the loss.</para>
///
/// <para><b>Move rather than copy-then-delete.</b> Within a volume a move is a rename: instant
/// whatever the file's size, and it cannot leave a stale second copy behind. Across volumes .NET
/// falls back to copying and removes the source only once that has succeeded — the same safety a
/// hand-written copy-verify-delete would provide, without the chance of getting the order wrong.
/// </para>
///
/// <para>Runs BEFORE <see cref="DatabaseShrinkService"/>, so that asking for both applies the
/// shrink to the database at its new home.</para>
/// </summary>
public static class DatabaseRelocationService
{
    /// <summary>What the last relocation did, for the Database settings tab to report.</summary>
    public static RelocationResult? LastResult { get; private set; }

    /// <summary>
    /// Performs a pending relocation, if one was requested. Call before anything opens the
    /// database. Never throws: a failed relocation must leave the app able to start on the
    /// database it already had.
    /// </summary>
    public static RelocationResult RunIfPending(Action<double, string>? progress = null)
    {
        var outcome = Run(progress);
        if (outcome.Ran) LastResult = outcome;
        return outcome;
    }

    private static RelocationResult Run(Action<double, string>? progress)
    {
        var target = AppConfig.GetPendingRelocation();
        if (target is null) return new RelocationResult(false, false, "");

        // Cleared first, not last. A relocation that crashes the machine must not leave an
        // operation that fails identically on every future launch, locking the user out.
        AppConfig.ClearPendingRelocation();

        var source = AppConfig.GetDbPath();

        try
        {
            if (!File.Exists(source))
                return new RelocationResult(true, false, "The database file could not be found.");

            if (File.Exists(target))
                return new RelocationResult(true, false,
                    $"A file already exists at {target}. The database was left where it was.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // ⚠️ The step whose absence lost data. Everything committed must be in the main file
            // before it goes anywhere.
            progress?.Invoke(5, "Preparing database…");
            SqliteMaintenance.Checkpoint(source);

            progress?.Invoke(15, "Moving database…");
            File.Move(source, target);

            // The old sidecars describe a database that is no longer at that path.
            SqliteMaintenance.DeleteSidecars(source);

            // Checked before the config is repointed, while putting it back is still trivial.
            progress?.Invoke(85, "Verifying…");
            if (!Verify(target, out var problem))
            {
                TryMoveBack(target, source);
                return new RelocationResult(true, false,
                    $"The moved database failed verification ({problem}) and was put back at {source}.");
            }

            progress?.Invoke(95, "Updating configuration…");
            AppConfig.SetDbPath(target);

            progress?.Invoke(100, "Database moved.");
            return new RelocationResult(true, true, $"Database moved to {target}.");
        }
        catch (Exception ex)
        {
            // The move either happened or it did not — File.Move does not leave a half-written
            // destination — so the database is at one of the two paths and the config still names
            // the one it started at.
            return new RelocationResult(true, false,
                $"The database could not be moved — it was left at {source}. ({ex.Message})");
        }
    }

    /// <summary>Undoes a move whose result could not be trusted. Best effort: if even this fails
    /// the message has to name both paths, because the file is at one of them.</summary>
    private static void TryMoveBack(string from, string to)
    {
        try { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to); } catch { }
    }

    /// <summary>
    /// Confirms the database at its new path opens and has a schema.
    ///
    /// <para>⚠️ Deliberately cheap. This was <c>PRAGMA quick_check</c>, which reads every page —
    /// a second full pass over the file, at the destination's speed. Moving 4 GB to a slower
    /// volume took about three minutes to copy and then another two to verify, most of a stall
    /// that looked like a hang. And it was guarding against a failure that does not occur:
    /// <see cref="File.Move(string,string)"/> either completes or throws, so a silently truncated
    /// destination is not on the table.</para>
    ///
    /// <para>What can actually go wrong is the file not arriving, arriving empty, or not being a
    /// database — all of which this catches by opening it and asking for the table count, which
    /// touches the header and the schema rather than the whole file.</para>
    /// </summary>
    private static bool Verify(string path, out string problem)
    {
        problem = "";
        try
        {
            using var conn = new SqliteConnection(SqliteMaintenance.ConnectionString(path));
            conn.Open();

            using var tables = conn.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table'";
            if (Convert.ToInt64(tables.ExecuteScalar() ?? 0L) == 0)
            {
                problem = "it contains no tables";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return false;
        }
        finally { SqliteConnection.ClearAllPools(); }
    }
}
