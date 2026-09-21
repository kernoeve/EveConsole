using System.Globalization;
using System.Text.RegularExpressions;

namespace EveConsole.Services;

/// <summary>One line of a pasted list as the parser read it: a name, and how many.</summary>
public sealed record ParsedItemLine(string Name, long Quantity, string Source);

/// <summary>
/// Reads the lists players paste: an inventory or cargo hold copied out of the client, a
/// contract's items, a multibuy list, an EFT fit, or lines typed by hand.
///
/// <para>The client's copies are tab-separated with the name first and the quantity second; a
/// hand-typed line is "Name 20", "Name x20", "20 x Name" or "20 Name"; a fit is one item per
/// line, a module and its charge on one line split by a comma, with drones and cargo as
/// "Name x5". Everything the parser does is decide the name and the count — whether the name is
/// an item is the appraisal's business, so a name that ends in a number stays whole here and
/// the quantity is only split off once the whole line has failed to match.</para>
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

    /// <summary>
    /// Every line that could be read, in order. The caller resolves names; a line that ends in
    /// a number comes back twice when it could be read either way — whole, and split — so the
    /// resolver can take the reading that names an item.
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

            // The client's own copy: tab-separated, name first, quantity second (blank means one).
            if (line.Contains('\t'))
            {
                var fields = line.Split('\t');
                var name = Clean(fields[0]);
                if (name.Length == 0) continue;
                var qty = fields.Length > 1 && TryQuantity(fields[1], out var q) ? q : 1;
                out_.Add(new ParsedCandidate(raw, [new ParsedItemLine(name, qty, "tab")]));
                continue;
            }

            var readings = new List<ParsedItemLine>();

            var marked = QtyTrailingMarked.Match(line);
            if (marked.Success && TryQuantity(marked.Groups[2].Value, out var mq))
                readings.Add(new ParsedItemLine(Clean(marked.Groups[1].Value), mq, "name x qty"));

            var leading = QtyLeading.Match(line);
            if (leading.Success && TryQuantity(leading.Groups[1].Value, out var lq) && !char.IsDigit(leading.Groups[2].Value.TrimStart().FirstOrDefault()))
                readings.Add(new ParsedItemLine(Clean(leading.Groups[2].Value), lq, "qty name"));

            var bare = QtyTrailingBare.Match(line);
            if (bare.Success && TryQuantity(bare.Groups[2].Value, out var bq))
                readings.Add(new ParsedItemLine(Clean(bare.Groups[1].Value), bq, "name qty"));

            // A fit line "Module, Charge": both, one each. Read last, so a name that happens to
            // hold a comma is tried whole first.
            var whole = Clean(line);
            if (whole.Length > 0) readings.Insert(0, new ParsedItemLine(whole, 1, "name"));
            if (line.Contains(',') && !line.Any(char.IsDigit))
            {
                var parts = line.Split(',', 2);
                var module = Clean(parts[0]);
                var charge = Clean(parts[1]);
                if (module.Length > 0 && charge.Length > 0)
                    readings.Add(new ParsedItemLine(module, 1, "fit module"));
                if (module.Length > 0 && charge.Length > 0)
                    readings.Add(new ParsedItemLine(charge, 1, "fit charge"));
            }

            out_.Add(new ParsedCandidate(raw, readings));
        }

        return out_;
    }

    /// <summary>A quantity as the client writes it: digits with thousands separators, and nothing else.</summary>
    public static bool TryQuantity(string s, out long quantity)
    {
        quantity = 0;
        var digits = s.Replace(",", "").Replace(".", "").Replace(" ", "").Trim();
        if (digits.Length == 0 || digits.Length > 15 || !digits.All(char.IsDigit)) return false;
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out quantity) && quantity > 0;
    }

    /// <summary>The name with the client's decorations off: a trailing asterisk marks a blueprint
    /// copy in some copies, and "(Copy)" or "(Original)" says which in others.</summary>
    private static string Clean(string s)
    {
        var t = s.Trim().TrimEnd('*').Trim();
        foreach (var suffix in new[] { " (Copy)", " (Original)" })
            if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) t = t[..^suffix.Length].TrimEnd();
        return t;
    }
}

/// <summary>A pasted line and the readings it allows, best first.</summary>
public sealed record ParsedCandidate(string Line, IReadOnlyList<ParsedItemLine> Readings);
