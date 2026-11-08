namespace EveConsole.Services;

[Flags]
public enum TextStyle
{
    None      = 0,
    Bold      = 1,
    Italic    = 2,
    Strike    = 4,
    Mono      = 8,
    Quote     = 16,

    /// <summary>The marks themselves, such as the asterisks around bold: shown, but faintly.</summary>
    Directive = 32,
}

public readonly record struct StyledSpan(string Text, TextStyle Style);

/// <summary>
/// Message styling (XEP-0393), the plain-text marks every Jabber client agrees on: *bold*, _italic_, ~struck~, `code`,
/// a block of code between lines of ```, and > at the start of a line for a quote.
///
/// <para>The marks stay in the text, shown faintly, as the protocol asks: a message reads the same to anyone whose
/// client styles nothing. A mark only counts where the protocol says: an opening one at the start of a line, after a
/// space or after another opening mark, and not followed by a space; a closing one not after a space, on the same line.
/// Nothing is styled inside code.</para>
/// </summary>
public static class MessageStyling
{
    private const string Marks = "*_~`";

    /// <summary>Whether there is anything to style at all, so plain text stays plain cheaply.</summary>
    public static bool MayHaveStyling(string text) => text.IndexOfAny(['*', '_', '~', '`', '>']) >= 0;

    public static List<StyledSpan> Parse(string text)
    {
        var spans = new List<StyledSpan>();
        var lines = text.Split('\n');
        var inBlock = false;

        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n];
            var end = n < lines.Length - 1 ? "\n" : "";

            // A block of code: from a line starting ``` to the next such line, or the end.
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                spans.Add(new StyledSpan(line + end, TextStyle.Mono | TextStyle.Directive));
                inBlock = !inBlock;
                continue;
            }
            if (inBlock)
            {
                spans.Add(new StyledSpan(line + end, TextStyle.Mono));
                continue;
            }

            if (line.StartsWith('>'))
            {
                var marker = line.StartsWith("> ", StringComparison.Ordinal) ? 2 : 1;
                spans.Add(new StyledSpan(line[..marker], TextStyle.Quote | TextStyle.Directive));
                AddSpans(spans, line[marker..], TextStyle.Quote);
            }
            else AddSpans(spans, line, TextStyle.None);

            if (end.Length > 0) spans.Add(new StyledSpan(end, TextStyle.None));
        }

        return Merge(spans);
    }

    /// <summary>The spans of one line, styled within whatever it already is.</summary>
    private static void AddSpans(List<StyledSpan> spans, string line, TextStyle outer)
    {
        var start = 0;
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (Marks.Contains(c) && OpensAt(line, i) && ClosingFor(line, i) is { } close)
            {
                if (i > start) spans.Add(new StyledSpan(line[start..i], outer));

                var style = outer | StyleOf(c);
                spans.Add(new StyledSpan(c.ToString(), style | TextStyle.Directive));
                if (c == '`') spans.Add(new StyledSpan(line[(i + 1)..close], style));
                else AddSpans(spans, line[(i + 1)..close], style);
                spans.Add(new StyledSpan(c.ToString(), style | TextStyle.Directive));

                i = close + 1;
                start = i;
                continue;
            }
            i++;
        }
        if (start < line.Length) spans.Add(new StyledSpan(line[start..], outer));
    }

    /// <summary>
    /// An opening mark: not followed by a space, and at the start (of the line, or of what another opening mark began,
    /// as that is passed in on its own) or after a space. So snake_case and 2*3*4 stay as they are.
    /// </summary>
    private static bool OpensAt(string line, int i) =>
        i + 1 < line.Length && !char.IsWhiteSpace(line[i + 1]) && (i == 0 || char.IsWhiteSpace(line[i - 1]));

    /// <summary>Where the same mark closes it on this line, after at least one character, not after a space. Null for nowhere.</summary>
    private static int? ClosingFor(string line, int open)
    {
        var c = line[open];
        for (var j = open + 2; j < line.Length; j++)
            if (line[j] == c && !char.IsWhiteSpace(line[j - 1])) return j;
        return null;
    }

    private static TextStyle StyleOf(char mark) => mark switch
    {
        '*' => TextStyle.Bold,
        '_' => TextStyle.Italic,
        '~' => TextStyle.Strike,
        _   => TextStyle.Mono,
    };

    private static List<StyledSpan> Merge(List<StyledSpan> spans)
    {
        var merged = new List<StyledSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (span.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].Style == span.Style) merged[^1] = new StyledSpan(merged[^1].Text + span.Text, span.Style);
            else merged.Add(span);
        }
        return merged;
    }
}
