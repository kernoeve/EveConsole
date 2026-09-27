using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EveConsole.Services;

/// <summary>
/// What a voice is given, as against what the chat shows: the same words, made ready to be
/// said. Applied once, on the way into the speech queue, so every engine gets the same text.
///
/// <para>⚠️ Once, centrally, because doing it per engine had drifted: Kokoro, Piper and the
/// OpenAI-style voices each stripped markdown with their own copy of the same patterns, and
/// ElevenLabs had none — a "### Purpose" heading went to it as written.</para>
/// </summary>
public static partial class SpeechText
{
    /// <summary>Markdown and emoji out, then EVE's names into the form they are said.</summary>
    public static string Prepare(string text) =>
        EvePronunciation.Expand(WithoutEmoji(WithoutMarkdown(text ?? "")));

    [GeneratedRegex(@"```[\s\S]*?```")]                          private static partial Regex CodeBlock { get; }
    [GeneratedRegex(@"`([^`]+)`")]                                private static partial Regex InlineCode { get; }
    [GeneratedRegex(@"\*\*([^*]+)\*\*")]                          private static partial Regex Bold { get; }
    [GeneratedRegex(@"\*([^*]+)\*")]                              private static partial Regex Italic { get; }
    [GeneratedRegex(@"__([^_]+)__")]                              private static partial Regex BoldUnderscore { get; }
    [GeneratedRegex(@"(?<!\w)_([^_]+)_(?!\w)")]                   private static partial Regex ItalicUnderscore { get; }
    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.Multiline)]       private static partial Regex Heading { get; }
    [GeneratedRegex(@"^\s*[>*\-+]\s", RegexOptions.Multiline)]    private static partial Regex Bullet { get; }
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]                    private static partial Regex Link { get; }

    /// <summary>
    /// The text without its markdown: a heading's hashes, emphasis marks, bullets, code fences and
    /// link targets, which a voice would otherwise read out or trip over. The words stay.
    /// </summary>
    public static string WithoutMarkdown(string text)
    {
        text = CodeBlock.Replace(text, " ");
        text = InlineCode.Replace(text, "$1");
        text = Bold.Replace(text, "$1");
        text = Italic.Replace(text, "$1");
        text = BoldUnderscore.Replace(text, "$1");
        text = ItalicUnderscore.Replace(text, "$1");   // not inside a name: snake_case stays
        text = Heading.Replace(text, "");
        text = Bullet.Replace(text, "");
        text = Link.Replace(text, "$1");
        return text;
    }

    [GeneratedRegex(@"[ \t]{2,}")]            private static partial Regex Spaces { get; }
    [GeneratedRegex(@"[ \t]+([.,!?;:])")]     private static partial Regex SpaceBeforeMark { get; }

    /// <summary>
    /// The text without emoji. A local model scatters them through its answers — a rocket to sign
    /// off, a chart beside a heading — and a voice either reads their names aloud or stumbles on
    /// them. They stay in the chat, where they are harmless.
    ///
    /// <para>Everything that makes one up goes: the pictograph itself, and the invisible parts
    /// that join or colour it — the zero-width joiner of a family, the variation selector that
    /// asks for colour, a skin tone, a keycap's enclosing mark, a flag's letters and tags. The
    /// degree sign stays: it is a word, "25°".</para>
    /// </summary>
    public static string WithoutEmoji(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var kept    = new StringBuilder(text.Length);
        var removed = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsEmojiPart(rune)) { removed = true; continue; }
            kept.Append(rune.ToString());
        }
        if (!removed) return text;

        // The gap an emoji leaves: "done 📊." is "done ." without it, and should be "done."
        var result = Spaces.Replace(kept.ToString(), " ");
        return SpaceBeforeMark.Replace(result, "$1").Trim();
    }

    private static bool IsEmojiPart(Rune rune)
    {
        var v = rune.Value;
        if (v == 0x00B0) return false;                                  // °, a word
        if (v is 0x200D or 0xFE0E or 0xFE0F or 0x20E3) return true;     // joiner, selectors, keycap
        if (v is >= 0x1F3FB and <= 0x1F3FF) return true;                // skin tones
        if (v is >= 0xE0020 and <= 0xE007F) return true;                // a subdivision flag's tags
        return Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol;   // pictographs, dingbats, flags' letters
    }
}
