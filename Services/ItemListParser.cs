using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EveConsole.Services;

/// <summary>One line of a pasted list as the parser read it: a name, and how many.</summary>
public sealed record ParsedItemLine(string Name, long Quantity, string Source);

/// <summary>
/// Reads the lists players paste: an inventory or cargo hold copied out of the client, a
/// contract's items, a multibuy list, an EFT fit, a spreadsheet's rows, or lines typed by hand.
///
/// <para>Tolerant by design, since a paste from another tool brings columns nobody asked for.
/// A line with fields — split by tabs, commas, semicolons, pipes or runs of spaces, each field
/// quoted or not — names the item in its first field, and its count is the first later field
/// that is a whole number; every other field is ignored, and no number at all means one. A
/// line with no fields is read the ways people type: "Name 20", "Name x20", "20 x Name",
/// "20 Name" or just the name. A fit is one item per line, a module and its charge together
/// split by a comma, with drones and cargo as "Name x5".</para>
///
/// <para>Everything the parser does is decide the name and the count — whether the name is an
/// item is the appraisal's business, so a name that ends in a number stays whole here and the
/// quantity is only split off once the whole line has failed to match.</para>
/// </summary>
public static class ItemListParser
{
    // "Name x20", "Name x 20", "Name × 20", "Name * 20"
    private static readonly Regex QtyTrailingMarked = new(@"^\s*(.+?)\s*[x×*]\s*(\d[\d,.]*)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // "20 x Name", "20x Name", "20 Name"
    private static readonly Regex QtyLeading        = new(@"^\s*(\d[\d,.]*)\s*(?:[x×*]\s*)?(.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // "Name 20"
    private static readonly Regex QtyTrailingBare   = new(@"^\s*(.+?)\s+(\d[\d,.]*)\s*$", RegexOptions.Compiled);
    // "[Vexor, PvE fit]": an EFT fit's header names the hull.
    private static readonly Regex FitHeader         = new(@"^\s*\[\s*([^,\]]+?)\s*(?:,\s*(.*?))?\s*\]\s*$", RegexOptions.Compiled);
    // Two or more spaces: a table whose tabs became spaces on the way.
    private static readonly Regex SpaceRun          = new(@"[  ]{2,}", RegexOptions.Compiled);
    // A whole number, with or without thousands groups: "34", "1,000", "1.000", "1 234". Not
    // "0.48" or "3,051.00" — volumes and prices are never counts.
    private static readonly Regex WholeNumber       = new(@"^(?:\d{1,3}(?:[,.  ]\d{3})+|\d{1,15})$", RegexOptions.Compiled);
    private static readonly Regex Spaces            = new(@"\s+", RegexOptions.Compiled);

    /// <summary>What a header row's first field says, so the row is passed over.</summary>
    private static readonly HashSet<string> Headers = new(StringComparer.OrdinalIgnoreCase)
        { "name", "item", "type", "item name", "type name", "typename", "itemname", "item type" };

    /// <summary>
    /// Every line that could be read, in order. The caller resolves names; a line comes back
    /// with more than one reading when it could be read more than one way — whole, and split —
    /// so the resolver can take the reading that names an item.
    /// </summary>
    public static IReadOnlyList<ParsedCandidate> Parse(string text)
    {
        var out_ = new List<ParsedCandidate>();
        if (string.IsNullOrWhiteSpace(text)) return out_;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            if (line.TrimStart().StartsWith("//")) continue;   // a comment, as fit tools allow

            // A fit header: the hull, once.
            var header = FitHeader.Match(line);
            if (header.Success)
            {
                out_.Add(new ParsedCandidate(raw, [new ParsedItemLine(Clean(header.Groups[1].Value), 1, "fit")]));
                continue;
            }

            var (fields, delimiter) = Fields(line);
            if (fields is not null)
            {
                var readings = Delimited(line, fields, delimiter);
                if (readings.Count > 0) out_.Add(new ParsedCandidate(raw, readings));
                continue;
            }

            out_.Add(new ParsedCandidate(raw, Typed(line)));
        }

        return out_;
    }

    /// <summary>
    /// A line with fields: the name is the first field — or the first that is not a number, when
    /// the count leads — and the count is the first later field that is a whole number. The
    /// rest is whatever the tool it came from put there, and is ignored.
    /// </summary>
    private static List<ParsedItemLine> Delimited(string line, List<string> fields, char delimiter)
    {
        var readings = new List<ParsedItemLine>();
        var numeric  = fields.Select(f => TryQuantity(f, out var q) ? q : (long?)null).ToList();

        var nameAt = 0;
        if (numeric[0] is not null)
        {
            nameAt = -1;
            for (var i = 1; i < fields.Count; i++)
                if (fields[i].Length > 0 && numeric[i] is null) { nameAt = i; break; }
            if (nameAt < 0) return readings;
        }
        var name = Clean(fields[nameAt]);
        if (name.Length == 0) return readings;
        if (nameAt == 0 && fields.Count > 1 && Headers.Contains(name)) return readings;   // a header row

        var qty = nameAt > 0 ? numeric[0]!.Value : numeric.Skip(1).FirstOrDefault(n => n is not null) ?? 1;

        // A name that holds the delimiter itself is tried whole first; a tab never is one.
        var whole = delimiter == '\t' ? "" : Clean(line);
        if (whole.Length > 0 && whole != name) readings.Add(new ParsedItemLine(whole, 1, "name"));

        // A fit's "Module, Charge": two names and no number. Both apply, one each.
        if (delimiter == ',' && fields.Count == 2 && nameAt == 0 && numeric.All(n => n is null))
        {
            var charge = Clean(fields[1]);
            if (charge.Length > 0)
            {
                readings.Add(new ParsedItemLine(name,   1, "fit module"));
                readings.Add(new ParsedItemLine(charge, 1, "fit charge"));
            }
        }

        readings.Add(new ParsedItemLine(name, qty, "delimited"));
        return readings;
    }

    /// <summary>A line with no fields, read the ways people type it. The whole line first: a
    /// name that ends in a number, "Cap Booster 400", must not lose its number to the count.</summary>
    private static List<ParsedItemLine> Typed(string line)
    {
        var readings = new List<ParsedItemLine>();

        var whole = Clean(line);
        if (whole.Length > 0) readings.Add(new ParsedItemLine(whole, 1, "name"));

        var marked = QtyTrailingMarked.Match(line);
        if (marked.Success && TryQuantity(marked.Groups[2].Value, out var mq))
            readings.Add(new ParsedItemLine(Clean(marked.Groups[1].Value), mq, "name x qty"));

        var leading = QtyLeading.Match(line);
        if (leading.Success && TryQuantity(leading.Groups[1].Value, out var lq) && !char.IsDigit(leading.Groups[2].Value.TrimStart().FirstOrDefault()))
            readings.Add(new ParsedItemLine(Clean(leading.Groups[2].Value), lq, "qty name"));

        var bare = QtyTrailingBare.Match(line);
        if (bare.Success && TryQuantity(bare.Groups[2].Value, out var bq))
            readings.Add(new ParsedItemLine(Clean(bare.Groups[1].Value), bq, "name qty"));

        return readings;
    }

    /// <summary>The line's fields and what split them, or null for a line that is one field.
    /// A tab wins; then whichever of comma, semicolon and pipe appears most outside quotes; then
    /// a run of two or more spaces.</summary>
    private static (List<string>? Fields, char Delimiter) Fields(string line)
    {
        if (line.Contains('\t')) return (Split(line, '\t'), '\t');

        var best = '\0';
        var most = 0;
        foreach (var d in new[] { ',', ';', '|' })
        {
            var n = CountOutsideQuotes(line, d);
            if (n > most) { most = n; best = d; }
        }
        if (best != '\0') return (Split(line, best), best);

        var trimmed = line.Trim();
        if (SpaceRun.IsMatch(trimmed)) return (SpaceRun.Split(trimmed).Select(f => Unquote(f).Trim()).ToList(), ' ');

        return (null, '\0');
    }

    /// <summary>Fields split on <paramref name="d"/>, a field in double quotes kept whole with
    /// a doubled quote inside it read as one, each field trimmed.</summary>
    private static List<string> Split(string line, char d)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        var atStart = true;   // nothing but spaces so far in this field: a quote here opens it
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"' && atStart) { quoted = true; sb.Clear(); atStart = false; }
            else if (c == d) { fields.Add(sb.ToString().Trim()); sb.Clear(); atStart = true; }
            else { sb.Append(c); if (!char.IsWhiteSpace(c)) atStart = false; }
        }
        fields.Add(sb.ToString().Trim());
        return fields;
    }

    private static int CountOutsideQuotes(string line, char d)
    {
        var n = 0;
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == d && !quoted) n++;
        }
        return n;
    }

    /// <summary>A count as tools write one: a whole number, with or without thousands separators
    /// ("34", "1,000", "1.000", "1 234"), quoted or not. A volume or a price is not one.</summary>
    public static bool TryQuantity(string s, out long quantity)
    {
        quantity = 0;
        var t = Unquote(s).Trim();
        if (!WholeNumber.IsMatch(t)) return false;
        var digits = t.Replace(",", "").Replace(".", "").Replace(" ", "").Replace(" ", "");
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out quantity) && quantity > 0;
    }

    /// <summary>The field without the double quotes a spreadsheet put round it.</summary>
    private static string Unquote(string s)
    {
        var t = s.Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1].Replace("\"\"", "\"") : t;
    }

    /// <summary>The name with the decorations off: quotes; a trailing asterisk, which marks a
    /// blueprint copy in some copies; "(Copy)" or "(Original)", which says so in others; and
    /// any run of spaces, which a paste through another tool tends to bring.</summary>
    private static string Clean(string s)
    {
        var t = Unquote(s).Trim().TrimEnd('*').Trim();
        foreach (var suffix in new[] { " (Copy)", " (Original)" })
            if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) t = t[..^suffix.Length].TrimEnd();
        return Spaces.Replace(t, " ");
    }
}

/// <summary>A pasted line and the readings it allows, best first.</summary>
public sealed record ParsedCandidate(string Line, IReadOnlyList<ParsedItemLine> Readings);
