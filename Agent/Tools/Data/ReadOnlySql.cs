using System.Text;
using System.Text.RegularExpressions;

namespace EveConsole.Agent.Tools.Data;

/// <summary>
/// The rules every agent-written SQL statement passes before the database sees it. Shared by
/// query_database and show_query, so a statement the one refuses the other refuses too.
/// </summary>
internal static class ReadOnlySql
{
    /// <summary>
    /// Why the statement may not run, or null when it may.
    ///
    /// <para>Only SELECT, or a CTE that begins WITH. Anything else is refused on the first word —
    /// a read-only tool that let one UPDATE through would not be read-only.</para>
    /// </summary>
    public static string? Reject(string sql, AgentSchema? schema)
    {
        var firstWord = sql.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                           .FirstOrDefault() ?? "";
        if (!firstWord.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            && !firstWord.Equals("WITH", StringComparison.OrdinalIgnoreCase))
            return "Only SELECT (or CTEs starting with WITH...SELECT) are permitted.";

        return ValidateTables(sql, schema);
    }

    /// <summary>
    /// Rejects a query naming a table that does not exist, and says which ones do.
    ///
    /// <para>⚠️ This exists because the failure it catches is silent. On SQLite an unknown
    /// double-quoted identifier is not an error — it is a string literal — so a guessed name comes
    /// back as rows full of the guess, which reads like data. The agent then answers confidently
    /// from nothing. Refusing up front, with the real names attached, turns that into one more
    /// round trip instead of a wrong answer.</para>
    ///
    /// <para>⚠️ Deliberately narrow. It only inspects what follows FROM and JOIN, and only rejects
    /// a name it is sure about — CTEs defined in the same statement are collected first and
    /// allowed. A validator that blocks working SQL would be worse than the fault it prevents, so
    /// anything it cannot classify is let through to the database to judge.</para>
    /// </summary>
    private static string? ValidateTables(string sql, AgentSchema? schema)
    {
        if (schema is null) return null;

        // Names introduced by this statement itself: WITH x AS (...), and any alias that follows.
        var cte = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(sql, @"(?:\bWITH\b|,)\s*""?([A-Za-z_][A-Za-z0-9_]*)""?\s+AS\s*\(",
                                          RegexOptions.IgnoreCase))
            cte.Add(m.Groups[1].Value);

        var unknown = new List<string>();

        foreach (Match m in Regex.Matches(sql, @"\b(?:FROM|JOIN)\s+""?([A-Za-z_][A-Za-z0-9_]*)""?",
                                          RegexOptions.IgnoreCase))
        {
            var name = m.Groups[1].Value;
            if (cte.Contains(name) || schema.Has(name)) continue;
            if (!unknown.Contains(name, StringComparer.OrdinalIgnoreCase)) unknown.Add(name);
        }

        if (unknown.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append("Query not run — no such table: ")
          .Append(string.Join(", ", unknown))
          .Append('.');

        foreach (var name in unknown)
        {
            var near = schema.Nearest(name, 5);
            if (near.Count > 0)
                sb.Append($" Closest to '{name}': {string.Join(", ", near)}.");
        }

        sb.Append(" Use describe_tables to confirm columns before retrying.");
        return sb.ToString();
    }
}
