using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Does every format string from the resources get the values it needs?
///
/// <para>⚠️ <c>string.Format(SalesText.X, a)</c> where X is "{0} of {1}" throws FormatException
/// the moment its screen shows it — in every language, but only there, so neither the build nor
/// a look around the app meets it. This finds each <c>string.Format</c> and <c>Plurals.Format</c>
/// in the C# whose format is an entry, and counts the values given against the placeholders the
/// English uses. A named placeholder ("{alarm}") is the app's own template token and cannot be
/// filled by string.Format at all.</para>
///
/// <para>Reads the code as text, not through the compiler: comments and the words of string
/// literals are blanked (holes of interpolated strings stay, since they are code), then calls are
/// split into their top-level arguments.</para>
/// </summary>
static class FormatUses
{
    static readonly Regex Call = new(@"\b(?:string|String)\.Format\s*\(|\bPlurals\.Format\s*\(", RegexOptions.Compiled);
    static readonly Regex EntryRef = new(@"\b(\w+Text)\.(\w+)\b", RegexOptions.Compiled);
    static readonly Regex PluralFamily = new(@"nameof\(\s*(\w+Text)\.(\w+)Other\s*\)", RegexOptions.Compiled);
    static readonly string[] PluralForms = ["Zero", "One", "Two", "Few", "Many", "Other"];

    /// <returns>How many uses were checked.</returns>
    public static int Check(string root, IReadOnlyDictionary<string, string> english, List<string> errors)
    {
        var checkedUses = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("tools/") || rel.Contains("/bin/") || rel.Contains("/obj/")) continue;

            var src  = File.ReadAllText(file);
            var code = CodeOnly(src);
            foreach (Match call in Call.Matches(code))
            {
                var open = call.Index + call.Length - 1;
                var args = ArgumentsAt(code, open);
                if (args.Count == 0) continue;
                var line = 1 + src.AsSpan(0, call.Index).Count('\n');

                if (call.Value.StartsWith("Plurals", StringComparison.Ordinal))
                {
                    // Plurals.Format(X.ResourceManager, nameof(X.KOther), n, more…): {0} is n.
                    if (args.Count < 3 || PluralFamily.Match(args[1].Text) is not { Success: true } fam) continue;
                    var given = args.Count - 2;
                    foreach (var form in PluralForms)
                    {
                        if (!english.TryGetValue($"{fam.Groups[1].Value}.{fam.Groups[2].Value}{form}", out var v)) continue;
                        checkedUses++;
                        if (Need(v) is { } n && n.Count > given)
                            errors.Add($"{rel}:{line}: {fam.Groups[1].Value}.{fam.Groups[2].Value}{form} needs {n.Count} value(s), gets {given}: \"{v}\"");
                    }
                    continue;
                }

                // string.Format([provider,] format, values…)
                var fi = args.Count > 1 && Regex.IsMatch(args[0].Text, @"Culture|IFormatProvider|[Pp]rovider") ? 1 : 0;
                var format = args[fi];
                var givenValues = args.Count - fi - 1;
                // One array passed as the params: its length can't be read here.
                var oneArray = givenValues == 1 && Regex.IsMatch(args[fi + 1].Text, @"^new\s*(object\??\s*)?\[\]|\.ToArray\(\)$");

                foreach (Match r in EntryRef.Matches(format.Text))
                {
                    if (!english.TryGetValue($"{r.Groups[1].Value}.{r.Groups[2].Value}", out var v)) continue;
                    checkedUses++;
                    if (Need(v) is not { } need) continue;   // the resx check reports bad braces
                    if (need.Named.Count > 0)
                        errors.Add($"{rel}:{line}: {r.Value} has {{{string.Join("},{", need.Named)}}}, which string.Format cannot fill: \"{v}\"");
                    else if (!oneArray && need.Count > givenValues)
                        errors.Add($"{rel}:{line}: {r.Value} needs {need.Count} value(s), gets {givenValues}: \"{v}\"");
                }
            }
        }
        return checkedUses;
    }

    /// <summary>How many values a format needs (its highest index + 1), and its named tokens;
    /// null when the braces do not parse.</summary>
    static (int Count, List<string> Named)? Need(string s)
    {
        var count = 0;
        var named = new List<string>();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { i++; continue; }
                var end = s.IndexOf('}', i);
                if (end < 0) return null;
                var body = s[(i + 1)..end];
                var m = Regex.Match(body, @"^\s*(\d+)\s*(,\s*-?\d+\s*)?(:[^{}]*)?$");
                if (m.Success) count = Math.Max(count, int.Parse(m.Groups[1].Value) + 1);
                else if (Regex.IsMatch(body, @"^[A-Za-z_]\w*$")) named.Add(body);
                else return null;
                i = end;
            }
            else if (s[i] == '}')
            {
                if (i + 1 < s.Length && s[i + 1] == '}') { i++; continue; }
                return null;
            }
        }
        return (count, named);
    }

    // ── Reading C# as text ───────────────────────────────────────────────────────────────────

    sealed record Argument(int Start, int End, string Text);

    /// <summary>The top-level arguments of the call whose "(" is at <paramref name="open"/>, in
    /// code where literals are already blanked.</summary>
    static List<Argument> ArgumentsAt(string code, int open)
    {
        var args = new List<Argument>();
        int depth = 0, start = open + 1;
        for (var j = open + 1; j < code.Length; j++)
        {
            var c = code[j];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    var last = code[start..j].Trim();
                    if (last.Length > 0) args.Add(new(start, j, last));
                    return args;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(new(start, j, code[start..j].Trim()));
                start = j + 1;
            }
        }
        return args;
    }

    /// <summary>The source with comments, character literals and the words of string literals
    /// replaced by spaces (newlines kept), so every offset still points at the same place. The
    /// holes of an interpolated string are code and stay.</summary>
    static string CodeOnly(string s)
    {
        var sb = new StringBuilder(s.Length);
        var j = 0;
        while (j < s.Length)
        {
            var c = s[j];
            if (c == '/' && j + 1 < s.Length && s[j + 1] == '/')
            {
                var e = s.IndexOf('\n', j); var k = e < 0 ? s.Length : e;
                Blank(sb, s, j, k); j = k; continue;
            }
            if (c == '/' && j + 1 < s.Length && s[j + 1] == '*')
            {
                var e = s.IndexOf("*/", j + 2, StringComparison.Ordinal); var k = e < 0 ? s.Length : e + 2;
                Blank(sb, s, j, k); j = k; continue;
            }
            if (IsLiteralStart(s, j)) { j = MaskLiteral(sb, s, j); continue; }
            if (c == '\'') { var k = SkipChar(s, j); Blank(sb, s, j, k); j = k; continue; }
            sb.Append(c); j++;
        }
        return sb.ToString();
    }

    static bool IsLiteralStart(string s, int j) =>
        s[j] == '"' || ((s[j] == '$' || s[j] == '@') && Regex.IsMatch(s.AsSpan(j, Math.Min(4, s.Length - j)), "^[$@]+\""));

    static void Blank(StringBuilder sb, string s, int from, int to)
    {
        for (var i = from; i < to; i++) sb.Append(s[i] == '\n' ? '\n' : ' ');
    }

    static int SkipChar(string s, int j)
    {
        j++;
        while (j < s.Length && s[j] != '\'') { if (s[j] == '\\') j++; j++; }
        return Math.Min(j + 1, s.Length);
    }

    /// <summary>Appends the masked literal starting at <paramref name="i"/>; returns the index just past it.</summary>
    static int MaskLiteral(StringBuilder sb, string s, int i)
    {
        var m = Regex.Match(s.Substring(i, Math.Min(12, s.Length - i)), "^(\\$+@?|@\\$+|@)?(\"\"\"+|\")");
        var prefix = m.Groups[1].Value;
        var quotes = m.Groups[2].Value;
        var interpolated = prefix.Contains('$');
        var verbatim     = prefix.Contains('@');
        var j = i + prefix.Length + quotes.Length;
        Blank(sb, s, i, j);

        if (quotes.Length >= 3)   // raw strings (SQL, prompts): masked whole
        {
            var end = s.IndexOf(quotes, j, StringComparison.Ordinal);
            var k = end < 0 ? s.Length : end + quotes.Length;
            Blank(sb, s, j, k);
            return k;
        }
        while (j < s.Length)
        {
            var c = s[j];
            if (verbatim && c == '"' && j + 1 < s.Length && s[j + 1] == '"') { sb.Append("  "); j += 2; continue; }
            if (!verbatim && c == '\\') { var k = Math.Min(j + 2, s.Length); Blank(sb, s, j, k); j = k; continue; }
            if (c == '"') { sb.Append(' '); return j + 1; }
            if (interpolated && c == '{')
            {
                if (j + 1 < s.Length && s[j + 1] == '{') { sb.Append("  "); j += 2; continue; }
                sb.Append(' '); j++;
                var depth = 0;
                while (j < s.Length)
                {
                    var h = s[j];
                    if (IsLiteralStart(s, j)) { j = MaskLiteral(sb, s, j); continue; }
                    if (h == '\'') { var k = SkipChar(s, j); Blank(sb, s, j, k); j = k; continue; }
                    if (h is '(' or '[' or '{') depth++;
                    else if (h is ')' or ']') depth--;
                    else if (h == '}') { if (depth == 0) { sb.Append(' '); j++; break; } depth--; }
                    sb.Append(h); j++;
                }
                continue;
            }
            sb.Append(c == '\n' ? '\n' : ' '); j++;
        }
        return j;
    }
}
