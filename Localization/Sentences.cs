namespace EveConsole.Localization;

/// <summary>
/// Sentences worded separately, put one after the other.
///
/// <para>A space after a Latin or Cyrillic full stop, and after Korean's (which is the Latin one);
/// none after a Chinese or Japanese one — 。！？ — where a space reads as a gap in the line.</para>
/// </summary>
public static class Sentences
{
    public static string Join(string first, string next)
    {
        if (first.Length == 0) return next;
        if (next.Length == 0) return first;
        return first[^1] is '。' or '！' or '？' ? first + next : first + " " + next;
    }
}
