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
    // A heading's hashes at a line's start, space after them or not ("###Assets"); not "#1".
    [GeneratedRegex(@"^[ \t]*#{1,6}(?=[ \t]|\p{L})[ \t]*", RegexOptions.Multiline)] private static partial Regex Heading { get; }
    // ⚠️ And anywhere else a run of them stands as a word. A heading reaches the voice mid-line
    // whenever its line break is lost on the way, and was read out as "hash hash hash".
    [GeneratedRegex(@"(?<=\s)#{2,6}[ \t]*|(?<=\s)#[ \t]+")]                          private static partial Regex StrayHashes { get; }
    [GeneratedRegex(@"^\s*[>*\-+]\s", RegexOptions.Multiline)]                        private static partial Regex Bullet { get; }
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]                                        private static partial Regex Link { get; }
    // A rule between sections: ---, *** or ___, spaced or not.
    [GeneratedRegex(@"^[ \t]*([-*_])([ \t]*\1){2,}[ \t]*$", RegexOptions.Multiline)]  private static partial Regex Rule { get; }
    // A table: the row of dashes under its header, the bars at a row's ends, and those between cells.
    [GeneratedRegex(@"^[ \t]*\|?[ \t]*:?-{3,}:?[ \t]*(\|[ \t]*:?-{3,}:?[ \t]*)+\|?[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex TableDivider { get; }
    [GeneratedRegex(@"^[ \t]*\|[ \t]*|[ \t]*\|[ \t]*$", RegexOptions.Multiline)]      private static partial Regex TableEdge { get; }
    [GeneratedRegex(@"[ \t]*\|[ \t]*")]                                               private static partial Regex TableBar { get; }

    /// <summary>
    /// The text without its markdown: headings' hashes, emphasis marks, bullets, rules, table bars,
    /// code fences and link targets, which a voice would otherwise read out or trip over. The
    /// words stay; a table's cells are read as a list.
    /// </summary>
    public static string WithoutMarkdown(string text)
    {
        text = CodeBlock.Replace(text, " ");
        text = InlineCode.Replace(text, "$1");
        text = TableDivider.Replace(text, "");         // before emphasis, which would eat a "* * *"
        text = Rule.Replace(text, "");
        text = Bold.Replace(text, "$1");
        text = Italic.Replace(text, "$1");
        text = BoldUnderscore.Replace(text, "$1");
        text = ItalicUnderscore.Replace(text, "$1");   // not inside a name: snake_case stays
        text = Heading.Replace(text, "");
        text = StrayHashes.Replace(text, "");
        text = Bullet.Replace(text, "");
        text = Link.Replace(text, "$1");
        text = TableEdge.Replace(text, "");
        text = TableBar.Replace(text, ", ");
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
