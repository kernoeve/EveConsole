using System.Text;
using EveConsole.Localization;

namespace EveConsole.Services.Worklist;

/// <summary>
/// An item named in a task's reason or note, marked where the name is written.
///
/// <para>⚠️ Marked by whoever writes the name, because only they know it is an item. Finding items
/// afterwards by matching names against the finished sentence cannot work: "XQ1-Z2 - Capital Ships
/// &amp; Research" is a structure with a skillbook's name inside it, and it got the skillbook's
/// picture. What is not marked is never drawn as an item, whatever it looks like.</para>
///
/// <para>The marks are private-use characters around the type id and the name. They only ever
/// reach <see cref="WorklistItem.MarkedDetail"/> and <see cref="WorklistItem.MarkedBlockedBy"/>;
/// <see cref="WorklistItem.Detail"/> and <see cref="WorklistItem.BlockedBy"/> read without them, so
/// everything but the tooltip sees plain text.</para>
/// </summary>
public static class ItemRef
{
    private const char Open = '', Mid = '', Close = '';

    /// <summary>The item as the screen names it, with its suffix if it is a print, marked.</summary>
    public static string Of(int typeId, string english, PrintKind print = PrintKind.None) =>
        Mark(typeId, PrintLabel.Name(typeId, english, print), print);

    /// <summary>A name already worded for its sentence, marked: drawn as this print without its
    /// suffix, where the sentence says "copy" itself.</summary>
    public static string Mark(int typeId, string shown, PrintKind print = PrintKind.None) =>
        typeId > 0 ? $"{Open}{typeId}:{(int)print}{Mid}{shown}{Close}" : shown;

    /// <summary>The text as it reads, marks gone and names kept.</summary>
    public static string Plain(string text)
    {
        if (text.IndexOf(Open) < 0) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var (piece, _, _) in Parse(text)) sb.Append(piece);
        return sb.ToString();
    }

    /// <summary>The text in order: plain pieces with a type id of 0, and each marked item.</summary>
    public static IEnumerable<(string Text, int TypeId, PrintKind Print)> Parse(string text)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var open = text.IndexOf(Open, pos);
            var mid  = open < 0 ? -1 : text.IndexOf(Mid, open);
            var end  = mid  < 0 ? -1 : text.IndexOf(Close, mid);
            if (end < 0) { yield return (text[pos..], 0, PrintKind.None); yield break; }

            if (open > pos) yield return (text[pos..open], 0, PrintKind.None);

            var head = text[(open + 1)..mid].Split(':');
            yield return (text[(mid + 1)..end], int.Parse(head[0]), (PrintKind)int.Parse(head[1]));
            pos = end + 1;
        }
    }
}
