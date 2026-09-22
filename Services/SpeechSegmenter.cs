using System.Text;

namespace EveConsole.Services;

/// <summary>
/// Decides how much of a part-written answer can be spoken yet.
///
/// <para>The agent writes a sentence, calls a tool, thinks, and writes more. Waiting for the whole
/// answer means silence through all of that followed by a wall of text read at once, so speech
/// takes finished sentences as they appear and holds the unfinished tail back.</para>
///
/// <para>Its own class because it is a pure function with edge cases worth testing — one of them
/// audible enough that getting it wrong is obvious to a listener and invisible in a diff.</para>
/// </summary>
public static class SpeechSegmenter
{
    /// <summary>Below this a fragment is held back rather than spoken alone.</summary>
    private const int MinimumUtterance = 12;

    /// <summary>
    /// Removes and returns the finished sentences at the front of <paramref name="pending"/>, or
    /// null when there is nothing worth speaking yet.
    /// </summary>
    /// <param name="flush">
    /// True once the stream has ended, when what remains has no closing punctuation and never will.
    /// </param>
    public static string? Take(StringBuilder pending, bool flush)
    {
        if (pending.Length == 0) return null;

        if (flush)
        {
            var rest = pending.ToString().Trim();
            pending.Clear();
            return rest.Length > 0 ? rest : null;
        }

        // ⚠️ Scans only the pending tail, never the whole answer. Rebuilding the full response on
        // every delta to hunt for a full stop is the quadratic cost that was deliberately taken
        // out of the display path; doing it here would put it straight back.
        var text = pending.ToString();
        var cut  = -1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n') { cut = i; continue; }
            if (c != '.' && c != '!' && c != '?') continue;

            // ⚠️ A full stop only ends a sentence when whitespace or the end follows. Without
            // this "382.9B" is three sentences and an ISK figure is read out in pieces — and
            // abbreviations like "vs." or a trailing "etc." break the same way.
            if (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])) cut = i;
        }

        if (cut < 0) return null;

        var ready = text[..(cut + 1)].Trim();
        pending.Remove(0, cut + 1);

        // A bare "Right." lands as a stutter between longer utterances, so short fragments wait
        // and are spoken with whatever follows them.
        if (ready.Length < MinimumUtterance)
        {
            pending.Insert(0, ready + " ");
            return null;
        }

        return ready;
    }
}
