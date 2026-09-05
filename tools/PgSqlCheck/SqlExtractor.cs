using System.Text;
using System.Text.RegularExpressions;

namespace EveConsole.Tools.PgSqlCheck;

/// <summary>What was found at one place in the source, and whether it can be checked.</summary>
public sealed record Candidate(
    string File,
    int    Line,
    string Sql,
    int    Parameters,
    string? Unusable);

/// <summary>
/// Pulls hand-written SQL out of C# source.
///
/// <para>⚠️ This does not parse C#. It scans for string literals, joins the ones concatenated
/// together, and keeps those that look like a statement. That is enough because the SQL in this
/// application is written as literals rather than assembled by a query builder — but it means the
/// extractor is honest about its limits rather than silent: anything it cannot reconstruct is
/// reported as unusable with a reason, so the coverage figure means something.</para>
/// </summary>
public static class SqlExtractor
{
    /// <summary>
    /// Whether a literal is a statement rather than a sentence.
    ///
    /// <para>⚠️ The opening verb is not enough on its own. "Update check failed", "Select a
    /// character to continue" and "Delete this posting?" all begin with one, and the first
    /// version of this check handed them to PostgreSQL and reported the resulting syntax errors
    /// as findings — dozens of them, all noise. Requiring the clause that must accompany the
    /// verb (FROM after SELECT, SET after UPDATE, and so on) tells prose apart from SQL without
    /// needing to parse either.</para>
    /// </summary>
    private static bool IsStatement(string s)
    {
        var t = Regex.Replace(s, @"^\s*(--[^\n]*\n\s*)*", "");
        bool Has(string kw) => Regex.IsMatch(t, $@"\b{kw}\b", RegexOptions.IgnoreCase);

        // Every table and column in this codebase is written quoted. A literal with no quoted
        // identifier anywhere is prose that happens to open with a verb — "Delete this posting
        // from the store?" satisfies DELETE…FROM otherwise.
        if (!Regex.IsMatch(t, @"""[A-Za-z_][\w ]*""")) return false;

        if (Regex.IsMatch(t, @"^WITH\b", RegexOptions.IgnoreCase))
            return Regex.IsMatch(t, @"\bAS\s*\(", RegexOptions.IgnoreCase);
        if (Regex.IsMatch(t, @"^SELECT\b", RegexOptions.IgnoreCase))
            return Has("FROM");
        if (Regex.IsMatch(t, @"^INSERT\b", RegexOptions.IgnoreCase))
            return Has("INTO");
        if (Regex.IsMatch(t, @"^UPDATE\b", RegexOptions.IgnoreCase))
            return Has("SET");
        if (Regex.IsMatch(t, @"^DELETE\b", RegexOptions.IgnoreCase))
            return Has("FROM");
        return false;
    }

    /// <summary>
    /// A hole that carries SQL rather than a value, and so cannot become a parameter.
    ///
    /// <para>⚠️ The distinction matters: <c>{corpId}</c> is a value and becomes $1, but
    /// <c>{BuildWhere()}</c> is a clause. Substituting a parameter for a clause produces a
    /// syntax error that looks like a finding and is not one, which would poison the whole
    /// report. Anything matching this makes the statement unusable instead.</para>
    /// </summary>
    private static readonly Regex Fragment =
        new(@"\b(sql|where|clause|prefix|order|filter|join|exclusion|cond|tests|slots|columns|"
          + @"marketCol|table|Fn|RowId|AllTrue|CaseInsensitiveLike)\b|\(\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<Candidate> FromFile(string path)
    {
        var text  = File.ReadAllText(path);
        var found = new List<Candidate>();

        foreach (var (literal, offset, interpolated, truncated) in Literals(text))
        {
            if (!IsStatement(literal)) continue;

            var line = text.Take(offset).Count(c => c == '\n') + 1;
            var (sql, count, bad) = truncated
                ? (literal, 0, "completed by a variable this tool cannot see")
                : Parameterise(literal, interpolated);
            found.Add(new Candidate(path, line, sql, count, bad));
        }

        return found;
    }

    /// <summary>
    /// Every string literal in the file, with adjacent ones joined where the source concatenates
    /// them. Handles raw strings ("""…""", including the $ and $$ forms) and ordinary ones.
    /// </summary>
    private static IEnumerable<(string Text, int Offset, bool Interpolated, bool Truncated)> Literals(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            // Skip over line and block comments so a quote in prose does not open a literal.
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                var nl = s.IndexOf('\n', i);
                i = nl < 0 ? s.Length : nl;
                continue;
            }
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 1;
                continue;
            }

            var dollars = 0;
            var j = i;
            while (j < s.Length && s[j] == '$') { dollars++; j++; }
            if (j < s.Length && s[j] == '@') j++;              // verbatim, @" or $@"
            if (j >= s.Length || s[j] != '"') { if (dollars > 0) i = j - 1; continue; }

            var start = i;
            var body  = new StringBuilder();
            int after;

            // Raw string: three or more quotes, closed by the same number.
            var quotes = 0;
            var k = j;
            while (k < s.Length && s[k] == '"') { quotes++; k++; }

            if (quotes >= 3)
            {
                var close = s.IndexOf(new string('"', quotes), k, StringComparison.Ordinal);
                if (close < 0) { i = k; continue; }
                body.Append(s, k, close - k);
                after = close + quotes;
            }
            else
            {
                // Ordinary literal, honouring \" escapes.
                var p = j + 1;
                while (p < s.Length && s[p] != '"')
                {
                    if (s[p] == '\\' && p + 1 < s.Length)
                    {
                        body.Append(s[p + 1] switch { 'n' => '\n', 't' => '\t', var c => c });
                        p += 2;
                        continue;
                    }
                    if (s[p] == '\n') break;                   // unterminated: give up
                    body.Append(s[p]);
                    p++;
                }
                if (p >= s.Length || s[p] != '"') { i = j; continue; }
                after = p + 1;
            }

            // Join anything concatenated onto it: "a" + "b" + $"c"
            //
            // ⚠️ A "+" followed by anything that is not another literal means the statement is
            // completed by a variable this tool cannot see — "SELECT … FROM x " + where + "GROUP
            // BY …". Stopping there and checking what was gathered produces a statement missing
            // its tail, and PostgreSQL then reports a perfectly sound query as having no GROUP
            // BY. Truncated is not the same as unusable-looking, so it has to be tracked.
            var more = after;
            var truncated = false;
            while (true)
            {
                var m = more;
                while (m < s.Length && (char.IsWhiteSpace(s[m]))) m++;
                if (m >= s.Length || s[m] != '+') break;
                m++;
                while (m < s.Length && char.IsWhiteSpace(s[m])) m++;

                var d2 = 0;
                while (m < s.Length && s[m] == '$') { d2++; m++; }
                if (m < s.Length && s[m] == '@') m++;
                if (m >= s.Length || s[m] != '"') { truncated = true; break; }

                var q2 = 0; var n = m;
                while (n < s.Length && s[n] == '"') { q2++; n++; }

                if (q2 >= 3)
                {
                    var close = s.IndexOf(new string('"', q2), n, StringComparison.Ordinal);
                    if (close < 0) break;
                    body.Append(s, n, close - n);
                    more = close + q2;
                }
                else
                {
                    var p = m + 1;
                    while (p < s.Length && s[p] != '"')
                    {
                        if (s[p] == '\\' && p + 1 < s.Length)
                        {
                            body.Append(s[p + 1] switch { 'n' => '\n', 't' => '\t', var c => c });
                            p += 2; continue;
                        }
                        if (s[p] == '\n') break;
                        body.Append(s[p]); p++;
                    }
                    if (p >= s.Length || s[p] != '"') break;
                    more = p + 1;
                }
                dollars = Math.Max(dollars, d2);
            }

            yield return (body.ToString(), start, dollars > 0, truncated);
            i = more - 1;
        }
    }

    /// <summary>
    /// Turns every hole and named marker into a PostgreSQL positional parameter.
    ///
    /// <para>Parameters rather than literals so PostgreSQL infers each type from its context —
    /// <c>WHERE "Id" = $1</c> tells it bigint on its own. Substituting NULL instead would make
    /// half the comparisons ambiguous and report failures that are only artefacts of the
    /// substitution.</para>
    /// </summary>
    private static (string Sql, int Count, string? Unusable) Parameterise(string sql, bool interpolated)
    {
        var n = 0;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        string Next(string key)
        {
            if (map.TryGetValue(key, out var existing)) return existing;
            return map[key] = "$" + (++n);
        }

        var work = sql;

        // ⚠️ In an interpolated string a doubled brace is a literal one, so $"…{{{i}}}…" emits
        // "{" + the value + "}" — which is how this codebase writes EF's positional markers,
        // {0} and {1}. Park the doubled braces before touching the real holes, or the two
        // meanings get confused and the statement comes out with stray braces PostgreSQL then
        // reports as a syntax error that is entirely this tool's doing.
        if (interpolated)
            work = work.Replace("{{", "").Replace("}}", "");

        foreach (Match h in Regex.Matches(work, @"\{(?<x>[^{}]*)\}"))
        {
            var inner = h.Groups["x"].Value;
            if (inner.Length == 0 || int.TryParse(inner, out _)) continue;
            if (Fragment.IsMatch(inner))
                return (sql, 0, $"builds SQL from {{{Trim(inner)}}}");
        }

        work = Regex.Replace(work, @"\{[^{}]*\}", m => Next(m.Value));

        if (interpolated)
            work = work.Replace('', '{').Replace('', '}');

        // Now the restored braces wrap either a literal index — {0} — or the parameter a hole
        // became — {$1}. Both are one marker.
        work = Regex.Replace(work, @"\{\s*(?<p>\$\d+)\s*\}", m => m.Groups["p"].Value);
        work = Regex.Replace(work, @"\{\s*\d+\s*\}",         m => Next(m.Value));

        // @name markers, kept consistent so @id used twice is the same parameter.
        work = Regex.Replace(work, @"(?<![\w@:])@(?<n>[A-Za-z_]\w*)",
            m => Next("@" + m.Groups["n"].Value));

        if (work.Contains('{') || work.Contains('}'))
            return (sql, 0, "braces this tool could not resolve");

        // ⚠️ A parameter is only a parameter where a value belongs. These three placements say the
        // hole was carrying something else — a column name, a table alias, or text inside a
        // literal — and PostgreSQL's complaint would be about the substitution rather than about
        // the code. Reporting those as findings is how a checker earns its way into being
        // ignored, so they are declared unusable instead.
        if (Regex.IsMatch(work, @"'[^']*\$\d+[^']*'"))
            return (sql, 0, "a hole sits inside a string literal");
        if (Regex.IsMatch(work, @"[\w""]\s*\.\s*\$\d+|\$\d+\s*\."))
            return (sql, 0, "a hole is used as an identifier, not a value");
        if (Regex.IsMatch(work, @"\$\d+\s*\(|\bAS\s+\$\d+|\bFROM\s+\$\d+|\bJOIN\s+\$\d+",
                          RegexOptions.IgnoreCase))
            return (sql, 0, "a hole is used where SQL, not a value, belongs");

        return (work, n, null);
    }

    private static string Trim(string s) =>
        s.Length <= 32 ? s : s[..32] + "…";
}
