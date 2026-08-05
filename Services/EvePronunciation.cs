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

    public static string Expand(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? text
            : SystemNamePattern.Replace(text, m => Spell(m.Value));

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
