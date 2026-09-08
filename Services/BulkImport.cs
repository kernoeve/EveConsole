using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Thrown when an import ran to completion but emptied a table that previously held data, so it
/// was rolled back.
/// </summary>
public class ImportVerificationException(string source, IReadOnlyList<string> lost)
    : Exception(BuildMessage(source, lost))
{
    public string Source { get; } = source;
    public IReadOnlyList<string> LostTables { get; } = lost;

    private static string BuildMessage(string source, IReadOnlyList<string> lost) =>
        $"The {source} data was read without error, but {lost.Count} table(s) that held data " +
        $"beforehand came back empty, which is what a changed file format looks like: " +
        $"{string.Join(" ", lost)} " +
        $"The import was rolled back and your existing {source} data has NOT been changed.";
}

/// <summary>
/// Does a wipe-and-refill import still have everything it started with?
/// </summary>
/// <remarks>
/// ⚠️ This exists because an import that stores nothing says nothing. The SDE import once aborted
/// on a duplicate key one stage in, and because the whole thing is a wipe followed by a refill,
/// the ten tables belonging to later stages stayed empty — type materials among them, which is
/// reprocessing — with nothing anywhere reporting it. Counting before and after is the cheapest
/// thing that would have caught it.
///
/// <para>Shared by the SDE and Hoboleaks imports so the policy below is written once. Both are the
/// same shape of job: wipe a set of prefixed tables, refill them from a download.</para>
/// </remarks>
public static class BulkImport
{
    /// <summary>
    /// Every table in the model whose name starts with <paramref name="prefix"/>, except the ones
    /// named in <paramref name="keep"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Derived from the model rather than hand-listed, because a hand-list is one line that
    /// gets forgotten. When SdeIndustryModifierSources was added to the SDE import and not added
    /// to its wipe list, the next import inserted its rows on top of the previous run's, violated
    /// the primary key and threw — costing ten tables. Sixty more SDE tables are planned; a list
    /// that has to be edited twice per table would have gone wrong again.
    ///
    /// <para>The keep set is for tables carrying the import's own metadata, which is upserted
    /// rather than rewritten and must survive the wipe that empties everything else.</para>
    /// </remarks>
    public static List<string> TablesFor(AppDbContext db, string prefix, params string[] keep)
    {
        var kept = new HashSet<string>(keep, StringComparer.Ordinal);

        return db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(n => n is not null && n.StartsWith(prefix, StringComparison.Ordinal) && !kept.Contains(n))
            .Select(n => n!)
            // A table name cannot be a parameter, so it is interpolated wherever these are used.
            // They come from our own compiled model and can come from nowhere else; this says so
            // in code rather than only in a comment.
            .Where(n => n.All(char.IsLetterOrDigit))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Row counts for the given tables, as they stand at this moment.</summary>
    /// <remarks>
    /// One statement per table rather than a single UNION, so a table the schema step has not
    /// created yet costs one missing entry instead of blinding the whole sweep. Measured at a few
    /// hundred milliseconds for all 42 SDE tables — including ones of 646k and 476k rows — against
    /// an import measured in minutes, and it runs twice.
    /// </remarks>
    public static async Task<Dictionary<string, long>> CountAsync(AppDbContext db,
        IEnumerable<string> tables, CancellationToken ct)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var table in tables)
        {
            try
            {
                counts[table] = await db.Database
                    .SqlQueryRaw<long>($"SELECT CAST(COUNT(*) AS BIGINT) AS \"Value\" FROM \"{table}\"")
                    .SingleAsync(ct);
            }
            catch
            {
                // Absent or unreadable. Left OUT of the dictionary rather than recorded as zero:
                // the caller reports that as "could not be counted", which is the opposite claim
                // to "empty" and must not be mistaken for it.
            }
        }

        return counts;
    }

    /// <summary>Empties the given tables, immediately before an import refills them.</summary>
    /// <remarks>
    /// No foreign key is configured between any of these, so no delete order is required. If one
    /// is ever added this needs a topological sort, not a hand-written order that the next new
    /// table silently falls out of.
    /// </remarks>
    public static async Task ClearAsync(AppDbContext db, IReadOnlyList<string> tables,
        CancellationToken ct)
    {
        foreach (var table in tables)
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{table}\"", ct);
    }

    /// <summary>
    /// Compares counts taken after an import against the ones taken before the wipe.
    /// </summary>
    /// <returns>
    /// <c>Lost</c> — tables that held rows before and hold none now, which is what a file whose
    /// format changed underneath us looks like, and is treated as a failed import.
    /// <c>Warnings</c> — tables merely worth a look, reported without rejecting the run.
    /// </returns>
    /// <remarks>
    /// ⚠️ Only a fall to ZERO rejects an import. A table merely shrinking is ordinary — CCP retires
    /// ships, merges groups, drops certificates — and any threshold on "how much smaller is too
    /// much" is a guess that would one day block a legitimate update with no way past it. Falling
    /// to nothing from something is not a guess: no release empties a table this app requires, so
    /// it means the parse has stopped matching the file.
    ///
    /// <para>A first import compares against all zeros, so nothing can be "lost" and a fresh
    /// install cannot trip this.</para>
    /// </remarks>
    public static (List<string> Lost, List<string> Warnings) Compare(
        IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> after)
    {
        var lost     = new List<string>();
        var warnings = new List<string>();

        foreach (var (table, was) in before.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!after.TryGetValue(table, out var now))
            {
                warnings.Add($"{table}: could not be counted after the import.");
                continue;
            }

            if (now == 0 && was > 0)
                lost.Add($"{table}: held {was:N0} row(s) before this import and holds none now.");
            else if (now == 0)
                warnings.Add($"{table}: imported empty, and was empty beforehand too.");
            else if (was > 0 && now < was / 2)
                warnings.Add($"{table}: {now:N0} row(s), down from {was:N0} — under half what it held.");
        }

        foreach (var table in after.Keys.Except(before.Keys, StringComparer.Ordinal)
                                        .OrderBy(t => t, StringComparer.Ordinal))
            if (after[table] == 0)
                warnings.Add($"{table}: new table, imported empty.");

        return (lost, warnings);
    }
}
