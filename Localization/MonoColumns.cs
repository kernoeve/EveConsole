using System.Globalization;
using System.Text;

namespace EveConsole.Localization;

/// <summary>
/// The columns a text takes in a monospace font, for tables lined up with spaces: Slack code
/// blocks, the Top 10 and Monthly Summary exports, the game log's detail pane.
///
/// <para>⚠️ Not <c>string.Length</c>, and not <c>{0,-16}</c> alignment, which both count
/// characters: a Chinese, Japanese or Korean character takes two columns, so a translated label
/// padded by its length pushes every column after it out of line. A combining mark takes none,
/// and a character outside the Basic Multilingual Plane is one text element, not two.</para>
/// </summary>
public static class MonoColumns
{
    public static int Width(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var width = 0;
        foreach (var rune in text.EnumerateRunes()) width += Of(rune);
        return width;
    }

    /// <summary>The text with spaces after it, to <paramref name="columns"/> in all.</summary>
    public static string PadRight(string? text, int columns) =>
        (text ?? "") + new string(' ', Math.Max(0, columns - Width(text)));

    /// <summary>The text with spaces before it, to <paramref name="columns"/> in all.</summary>
    public static string PadLeft(string? text, int columns) =>
        new string(' ', Math.Max(0, columns - Width(text))) + (text ?? "");

    private static int Of(Rune rune)
    {
        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.Format:
                return 0;
        }
        var c = rune.Value;
        return c is >= 0x1100 and <= 0x115F      // Hangul initial jamo
                 or >= 0x2E80 and <= 0x303E      // CJK radicals, symbols and punctuation
                 or >= 0x3041 and <= 0x33FF      // kana, bopomofo, Hangul compatibility jamo, CJK letters
                 or >= 0x3400 and <= 0x4DBF      // CJK extension A
                 or >= 0x4E00 and <= 0x9FFF      // CJK ideographs
                 or >= 0xA000 and <= 0xA4CF      // Yi
                 or >= 0xA960 and <= 0xA97F      // Hangul jamo extended A
                 or >= 0xAC00 and <= 0xD7A3      // Hangul syllables
                 or >= 0xF900 and <= 0xFAFF      // CJK compatibility ideographs
                 or >= 0xFE10 and <= 0xFE19      // vertical forms
                 or >= 0xFE30 and <= 0xFE6F      // CJK compatibility and small forms
                 or >= 0xFF00 and <= 0xFF60      // full-width forms
                 or >= 0xFFE0 and <= 0xFFE6      // full-width signs
                 or >= 0x1F300 and <= 0x1FAFF    // emoji, drawn two wide
                 or >= 0x20000 and <= 0x3FFFD    // CJK extensions B onwards
            ? 2 : 1;
    }
}
