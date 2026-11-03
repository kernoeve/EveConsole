using Avalonia;
using EveConsole.Services;

namespace EveConsole.ViewModels;

/// <summary>A piece of a tooltip line: plain text, or an item drawn with its picture.</summary>
public sealed record DetailRun(string Text, int TypeId = 0)
{
    public bool IsItem => TypeId > 0;
}

/// <summary>One line of a tooltip: its pieces, and how far it sits in.</summary>
public sealed record DetailLine(IReadOnlyList<DetailRun> Runs, int Level = 0, bool GapBefore = false)
{
    public Thickness Margin => new(Level * 18, GapBefore ? 8 : 0, 0, 0);
}

/// <summary>
/// A task's reason, laid out to be read rather than as one paragraph.
///
/// <para>Each sentence on a line of its own. A sentence that opens with a heading — "For
/// Hypnagogic Neurolink Enhancer (1,819 run(s)): need 70,840; 50,284 on hand…" — is the heading
/// with its parts under it, indented, one to a line where the sentence lists them with ";". Every
/// item it names is drawn with its picture before the name.</para>
///
/// <para>⚠️ Built from the sentence the generator wrote, not from new wording: the text in every
/// language stays exactly what it was, only broken and indented. A sentence with no heading or
/// list in it is one line, as before.</para>
/// </summary>
public static class DetailText
{
    /// <param name="paragraphs">Separate paragraphs (the reason, a note, the snooze), a gap between.</param>
    /// <param name="known">Items the task itself knows, as the screen names them — found in any
    /// language, where the name index needs spaces between words.</param>
    public static IReadOnlyList<DetailLine> Build(IEnumerable<string> paragraphs, IEnumerable<(int TypeId, string Shown)> known)
    {
        var names = known.Where(k => k.TypeId > 0 && k.Shown.Length > 2)
                         .DistinctBy(k => k.Shown)
                         .OrderByDescending(k => k.Shown.Length)
                         .ToList();
        var lines = new List<DetailLine>();
        var first = true;

        foreach (var paragraph in paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var gap = !first;
            first = false;
            foreach (var sentence in FamilyRows.Sentences(paragraph))
            {
                foreach (var (text, level) in Split(sentence))
                {
                    lines.Add(new DetailLine(Runs(text, names), level, gap));
                    gap = false;
                }
            }
        }
        return lines;
    }

    /// <summary>A sentence as its heading and the parts under it.</summary>
    private static IEnumerable<(string Text, int Level)> Split(string sentence)
    {
        // A heading: up to the first colon, when something follows it. Not a colon inside a
        // number or a time ("12:30"), which has no space after it.
        var colon = sentence.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) colon = sentence.IndexOf('：');
        if (colon <= 0 || colon > 120 || colon >= sentence.Length - 2)
        {
            foreach (var part in Parts(sentence)) yield return (part, 0);
            yield break;
        }

        var head = sentence[..(colon + 1)].Trim();
        var rest = sentence[(colon + 1)..].Trim();
        var parts = Parts(rest).ToList();

        // A short tail stays on the heading's line: "Releases 1 stopped job(s): X." reads as one.
        if (parts.Count == 1 && rest.Length <= 60) { yield return ($"{head} {rest}", 0); yield break; }

        yield return (head, 0);
        foreach (var part in parts) yield return (part, 1);
    }

    /// <summary>A list's parts, split at its semicolons, and a long part's conclusion after a
    /// dash ("… and 16,250 on order — short 5,119.") on a line of its own.</summary>
    private static IEnumerable<string> Parts(string text) =>
        text.Split(["; ", "；"], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(p => p.Length > 60 && p.LastIndexOf(" — ", StringComparison.Ordinal) is var d and > 0
                ? new[] { p[..d], p[(d + 1)..] }
                : [p])
            .Select(p => p.Trim()).Where(p => p.Length > 0);

    /// <summary>The line as text and items, each item where its name stands.</summary>
    private static List<DetailRun> Runs(string text, List<(int TypeId, string Shown)> known)
    {
        // The task's own items, found in any language, and whatever else the index finds — the
        // longest name winning where two overlap: "Compressed Lime Mykoserocin" is one item, not
        // "Compressed" and the row's own "Lime Mykoserocin".
        var found = new List<(int Start, int Length, int TypeId)>(TypeNameIndex.Find(text));
        foreach (var (typeId, shown) in known)
            for (var at = text.IndexOf(shown, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(shown, at + shown.Length, StringComparison.Ordinal))
                found.Add((at, shown.Length, typeId));

        var hits = new List<(int Start, int Length, int TypeId)>();
        foreach (var h in found.OrderByDescending(h => h.Length).ThenBy(h => h.Start))
            if (!hits.Any(x => h.Start < x.Start + x.Length && x.Start < h.Start + h.Length))
                hits.Add(h);

        var runs = new List<DetailRun>();
        var pos = 0;
        foreach (var (start, length, typeId) in hits.OrderBy(h => h.Start))
        {
            if (start > pos) runs.Add(new DetailRun(text[pos..start]));
            runs.Add(new DetailRun(text.Substring(start, length), typeId));
            pos = start + length;
        }
        if (pos < text.Length) runs.Add(new DetailRun(text[pos..]));
        return runs;
    }
}
