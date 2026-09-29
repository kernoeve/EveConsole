using System.Data.Common;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Alarms;

/// <summary>
/// Names typed in one of the game client's other languages, matched to what they name: an alarm
/// set up by someone who plays in German or Chinese names its ships, places and items as their
/// client does.
///
/// <para>The checks match the English first, which is what the editor stores; this is the second
/// look, for a name the English does not know — typed before the editor translated names, given
/// by the agent, or written by hand. By the SDE's names in the other languages
/// (<see cref="SdeName"/>): exactly, or with case folded where the database folds it (PostgreSQL
/// in every script, SQLite in A–Z only).</para>
/// </summary>
public static class OtherLanguageNames
{
    /// <summary>The ids, by kind, of what the names name in the client's other languages. One
    /// query for all the names and kinds, and none when there are no names.</summary>
    public static async Task<Dictionary<SdeNameKind, HashSet<long>>> IdsAsync(
        AppDbContext db, IEnumerable<string> names, CancellationToken ct, params SdeNameKind[] kinds)
    {
        var found = kinds.ToDictionary(k => k, _ => new HashSet<long>());
        var exact = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToList();
        if (exact.Count == 0 || kinds.Length == 0) return found;

        var folded = exact.Select(n => n.ToLowerInvariant()).Distinct().ToList();
        foreach (var row in await db.SdeNames.AsNoTracking()
                     .Where(n => kinds.Contains(n.Kind) && (exact.Contains(n.Name) || folded.Contains(n.Name.ToLower())))
                     .Select(n => new { n.Kind, n.Id })
                     .ToListAsync(ct))
            found[row.Kind].Add(row.Id);
        return found;
    }

    /// <summary>The id of what a name names in the client's other languages, the lowest when two
    /// share the name, or null. For a check that reads through its own connection.</summary>
    public static async Task<long?> IdAsync(DbConnection conn, SdeNameKind kind, string name, CancellationToken ct)
    {
        var text = name.Trim();
        if (text.Length == 0) return null;

        await using var cmd = conn.Command(
            """SELECT "Id" FROM "SdeNames" WHERE "Kind" = @k AND ("Name" = @n OR lower("Name") = @folded) ORDER BY "Id" LIMIT 1""");
        cmd.AddWithValue("@k", (int)kind);
        cmd.AddWithValue("@n", text);
        cmd.AddWithValue("@folded", text.ToLowerInvariant());
        return await cmd.ExecuteScalarAsync(ct) is { } v and not DBNull ? Convert.ToInt64(v) : null;
    }
}
