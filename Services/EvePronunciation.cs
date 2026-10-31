using System.Text;
using System.Text.RegularExpressions;

namespace EveConsole.Services;

/// <summary>
/// Rewrites EVE system names into how capsuleers actually say them, for text on its way to
/// text-to-speech.
///
/// <para>Null-security system names are not words. "QZ-X77" is said "Q Z tac X seven seven" — every
/// character spoken individually, and the hyphen spoken as "tac". Left alone, a speech engine
/// either tries to pronounce it as a word or reads the hyphen as "dash", and either way the one
/// piece of information that matters in an intel alert — which system — is the part that does
/// not survive.</para>
///
/// <para>Applied on the speech path only, so what is written on screen stays "QZ-X77".</para>
/// </summary>
public static partial class EvePronunciation
{
    /// <summary>
    /// The shape of a null-sec name: one to five upper-case alphanumerics, a hyphen, then one
    /// to five more. Deliberately not requiring a digit — plenty of real systems have none — and
    /// deliberately case-sensitive, so ordinary hyphenated
    /// prose is left alone.
    ///
    /// <para>And a wormhole's "J123456", which is otherwise read as a number: "J one hundred and
    /// twenty-three thousand…". Named systems — high and low sec, Thera — are words, and left to
    /// the voice.</para>
    /// </summary>
    [GeneratedRegex(@"\b[A-Z0-9]{1,5}-[A-Z0-9]{1,5}\b|\bJ[0-9]{6}\b")]
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
    /// is nothing to correct — the engine is guessing at an invented proper noun. On the speech
    /// path the only lever is the text itself. Left alone the local voice says "GAL-ent",
    /// "MIN-matter" and "AM-ar".</para>
    ///
    /// <para>⚠️ Lower case, all of it. The respellings were written "guh-LEN-tay" to show the
    /// stress, and the local voice reads a capitalised syllable as letters: "kal D A R ee",
    /// "guh L E N tay", "M I N muh tar". Each was checked against the phonemes Kokoro's
    /// espeak front end makes of it. Caldari needs nothing — it already comes out "kal-DAR-ee".</para>
    /// </summary>
    private static readonly (string Written, string Spoken)[] Words =
    [
        // Ordinary words wearing capitals.
        ("EVE",       "Eve"),
        ("ISK",       "isk"),

        // The empires. ⚠️ Amarrian before Amarr — the alternation below is ordered, so the
        // shorter name would otherwise match first and leave "-ian" stranded.
        ("Amarrian",  "amaarian"),       // am-AR-ee-an
        ("Amarr",     "a-mar"),          // uh-MAR
        ("Gallente",  "guh-len-tay"),
        ("Minmatar",  "min-muh-tar"),
    ];

    /// <summary>
    /// ⚠️ Whole words only. Without the boundaries "ISK" matches inside "RISK" and "EVE" inside
    /// "SEVEN", and the correction becomes the defect.
    /// </summary>
    [GeneratedRegex(@"\b(EVE|ISK|Amarrian|Amarr|Gallente|Minmatar)\b")]
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

    /// <summary>"QZ-X77" → "Q Z tac X seven seven".</summary>
    private static string Spell(string name)
    {
        var sb = new StringBuilder(name.Length * 3);

        foreach (var c in name)
        {
            if (sb.Length > 0) sb.Append(' ');

            // ⚠️ "A" alone is the article to a voice — "uh" — and it ran into the letters round
            // it: "UALX-3" was heard as "U uh L X". "eigh" is the letter's sound written out.
            if (c == '-')                sb.Append("tac");
            else if (char.IsAsciiDigit(c)) sb.Append(Digits[c - '0']);
            else if (c == 'A')           sb.Append("eigh");
            else                         sb.Append(c);
        }

        return sb.ToString();
    }
}
