using System.Text;
using System.Text.RegularExpressions;

namespace EveConsole.Services;

/// <summary>
/// Rewrites EVE system names into how capsuleers actually say them, for text on its way to
/// text-to-speech.
///
/// <para>Null-security system names are not words. "C-FD0D" is said "C tac F D zero D" — every
/// character spoken individually, and the hyphen spoken as "tac". Left alone, a speech engine
/// either tries to pronounce it as a word or reads the hyphen as "dash", and either way the one
/// piece of information that matters in an intel alert — which system — is the part that does
/// not survive.</para>
///
/// <para>Applied on the speech path only, so what is written on screen stays "C-FD0D".</para>
/// </summary>
public static partial class EvePronunciation
{
    /// <summary>
    /// The shape of a null-sec name: one to five upper-case alphanumerics, a hyphen, then one
    /// to five more. Deliberately not requiring a digit — plenty of real systems have none
    /// (Y-ORBJ, M-OEE8's neighbours) — and deliberately case-sensitive, so ordinary hyphenated
    /// prose is left alone.
    /// </summary>
    [GeneratedRegex(@"\b[A-Z0-9]{1,5}-[A-Z0-9]{1,5}\b")]
    private static partial Regex SystemNamePattern { get; }

    private static readonly string[] Digits =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    /// <summary>
    /// Words a speech engine says wrong because they look like something they are not.
    ///
    /// <para>EVE and ISK are the same problem in reverse: both are ordinary words that happen to
    /// be written in capitals, so an engine reads them as initialisms — "E V E", "I S K".
    /// Lower-casing is the whole fix.</para>
    ///
    /// <para>⚠️ The empire names are respelled phonetically rather than corrected, because there
    /// is nothing to correct — the engine is guessing at an invented proper noun and guessing
    /// plausibly. On the speech path the only lever is the text itself. Stress matters more than
    /// the vowels: Gallente is "guh-LEN-tay", not "GAL-ent".</para>
    /// </summary>
    private static readonly (string Written, string Spoken)[] Words =
    [
        // Ordinary words wearing capitals.
        ("EVE",       "Eve"),
        ("ISK",       "isk"),

        // The four empires. ⚠️ Amarrian before Amarr — the alternation below is ordered, so the
        // shorter name would otherwise match first and leave "-ian" stranded.
        ("Amarrian",  "uh-MAR-ee-an"),
        ("Amarr",     "uh-MAR"),
        ("Caldari",   "kal-DAR-ee"),
        ("Gallente",  "guh-LEN-tay"),
        ("Minmatar",  "MIN-muh-tar"),
    ];

    /// <summary>
    /// ⚠️ Whole words only. Without the boundaries "ISK" matches inside "RISK" and "EVE" inside
    /// "SEVEN", and the correction becomes the defect.
    /// </summary>
    [GeneratedRegex(@"\b(EVE|ISK|Amarrian|Amarr|Caldari|Gallente|Minmatar)\b")]
    private static partial Regex WordPattern { get; }

    public static string Expand(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // ⚠️ System names first, words second. Spell() emits single letters separated by spaces,
        // so running it over the word pass's output could re-match letters it had just produced.
        var spelled = SystemNamePattern.Replace(text, m => Spell(m.Value));

        return WordPattern.Replace(spelled, m =>
        {
            foreach (var (written, spoken) in Words)
                if (m.Value.Equals(written, StringComparison.Ordinal))
                    return spoken;
            return m.Value;
        });
    }

    /// <summary>"C-FD0D" → "C tac F D zero D".</summary>
    private static string Spell(string name)
    {
        var sb = new StringBuilder(name.Length * 3);

        foreach (var c in name)
        {
            if (sb.Length > 0) sb.Append(' ');

            if (c == '-')                sb.Append("tac");
            else if (char.IsAsciiDigit(c)) sb.Append(Digits[c - '0']);
            else                         sb.Append(c);
        }

        return sb.ToString();
    }
}
