using System;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace EveConsole.Localization;

/// <summary>
/// Reads back a number a person typed, or one this app showed: in the interface's number format
/// (CultureInfo.CurrentCulture, which follows the interface language — see
/// <see cref="Languages.ApplyAtStartup"/>), and, only when the text is no number at all in that
/// format, as English writes it, so "1,234.5" pasted from a website still reads.
///
/// <para>⚠️ Not by stripping commas, which is how this used to be done. That removed English's
/// thousands separator and nothing else: in a language that writes decimals with a comma, "1,5"
/// came back as 15, and a group separator that is a space — French and Russian group with U+202F or
/// U+00A0 — was left in, so a number the app had shown, an id among them, could not be read back.</para>
///
/// <para>⚠️ A group separator counts only where it could be one: between groups of the culture's
/// size. .NET's parser takes it anywhere, so "1234.5" in German, where "." groups, would read as
/// 12345, and "1,5" in English as 15. Refused in the interface's format, "1234.5" reads as English
/// writes it; "1,5" does not read at all, which beats a price ten times too high.</para>
///
/// <para>For numbers from elsewhere — stored settings, game logs, mail from other players — parse
/// with the culture they were written in, not with this.</para>
/// </summary>
public static class NumberText
{
    private const NumberStyles Decimal = NumberStyles.Number | NumberStyles.AllowExponent;
    private const NumberStyles Whole   = NumberStyles.Integer | NumberStyles.AllowThousands;

    public static bool TryParse(string? text, out double value)  => Read(text, Decimal, out value);
    public static bool TryParse(string? text, out decimal value) => Read(text, Decimal, out value);

    /// <summary>A whole number — an id, a count. "12.5" is not one.</summary>
    public static bool TryParse(string? text, out long value) => Read(text, Whole, out value);

    private static bool Read<T>(string? text, NumberStyles styles, out T value) where T : struct, INumberBase<T>
    {
        value = T.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var here  = CultureInfo.CurrentCulture;
        var local = InCulture(text, here.NumberFormat);
        if (GroupsFit(local, here.NumberFormat))
        {
            if (T.TryParse(local, styles, here, out value)) return true;
            // A number here, only not a whole one: "12,5" in German must not come back from the
            // English reading as 125.
            if (decimal.TryParse(local, Decimal, here, out _)) return false;
        }

        var english = AsWritten(text);
        return GroupsFit(english, NumberFormatInfo.InvariantInfo)
            && T.TryParse(english, styles, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The text with a typing habit or two undone: "_" digit separators dropped, and every
    /// kind of space taken out where the culture groups digits with a space — people type an
    /// ordinary one where the culture's is U+202F or U+00A0.</summary>
    private static string InCulture(string text, NumberFormatInfo format)
    {
        var s     = AsWritten(text);
        var group = format.NumberGroupSeparator;
        if (group.Length == 1 && IsSpace(group[0]))
            s = string.Concat(s.Where(c => !IsSpace(c)));
        return s;
    }

    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is ' ' or ' ';

    private static string AsWritten(string text) => text.Trim().Replace("_", "");

    /// <summary>Whether every group separator in the whole-number part sits between groups of the
    /// culture's size: counted from the right, as NumberGroupSizes gives them, with the leftmost
    /// group allowed to be shorter.</summary>
    private static bool GroupsFit(string s, NumberFormatInfo format)
    {
        var separator = format.NumberGroupSeparator;
        if (separator.Length == 0) return true;

        var end      = s.IndexOf(format.NumberDecimalSeparator, StringComparison.Ordinal);
        var exponent = s.IndexOfAny(['e', 'E']);
        if (exponent >= 0 && (end < 0 || exponent < end)) end = exponent;
        var whole = end < 0 ? s : s[..end];
        if (!whole.Contains(separator, StringComparison.Ordinal)) return true;

        var groups = whole.Split(separator);
        var sizes  = format.NumberGroupSizes;
        for (int i = groups.Length - 1, fromRight = 0; i >= 0; i--, fromRight++)
        {
            var size   = sizes.Length == 0 ? 3 : sizes[Math.Min(fromRight, sizes.Length - 1)];
            var digits = groups[i].Count(char.IsAsciiDigit);
            var fits   = i == 0 ? digits >= 1 && (size == 0 || digits <= size)
                                : size > 0 && digits == size;
            if (!fits) return false;
        }
        return true;
    }
}
