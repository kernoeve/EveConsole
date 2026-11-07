using System.Text;
using System.Text.RegularExpressions;

namespace EveConsole.Services.Jabber;

/// <summary>A code and the picture it stands for.</summary>
public sealed record EmoteRef(string Code, string Hash, int Width, int Height, bool Animated);

/// <summary>One picture in the picker, with every code a pack gives it: the first is the one put in a message.</summary>
public sealed record EmotePickItem(EmoteRef Emote, IReadOnlyList<string> Codes, string Pack);

/// <summary>A piece of a message: plain text, or an emote standing in for its code.</summary>
public readonly record struct EmotePiece(string Text, EmoteRef? Emote);

/// <summary>
/// The emotes one Jabber server's messages are read with: the codes of every pack that server uses, the pack listed
/// first winning where two have the same code.
///
/// <para>One object per server for as long as the app runs, filled again in place when its packs change, so whatever
/// holds it only has to listen to <see cref="Changed"/>.</para>
///
/// <para>Two kinds of code. One between colons, such as <c>:wave:</c>, is found anywhere, even against another: that
/// is how they are typed, several in a row. Any other, such as <c>:)</c>, only counts on its own, between spaces or at
/// either end, so a <c>:/</c> inside a word or an address is left alone. Nothing is ever found inside a web address.</para>
/// </summary>
public sealed partial class EmoteSet(int accountId)
{
    /// <summary>A set with nothing in it, for a conversation that is not on a Jabber server.</summary>
    public static readonly EmoteSet None = new(0);

    private const int LongestColonCode = 64;

    private Dictionary<string, EmoteRef> _colon = new(StringComparer.OrdinalIgnoreCase);
    private List<EmoteRef> _other = [];
    private List<EmoteRef> _colonSorted = [];
    private IReadOnlyList<EmotePickItem> _pictures = [];

    public int AccountId => accountId;

    public bool IsEmpty => _colon.Count == 0 && _other.Count == 0;

    /// <summary>One for each picture, in the order the server's packs list them.</summary>
    public IReadOnlyList<EmotePickItem> Pictures => _pictures;

    /// <summary>Raised on the thread that filled it, after the set holds the new codes.</summary>
    public event Action? Changed;

    /// <summary>Takes on the server's packs: the codes in order of preference, and the pictures for the picker.</summary>
    public void Fill(IEnumerable<EmoteRef> codesBestFirst, IReadOnlyList<EmotePickItem> pictures)
    {
        var colon = new Dictionary<string, EmoteRef>(StringComparer.OrdinalIgnoreCase);
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var other = new List<EmoteRef>();

        foreach (var emote in codesBestFirst)
        {
            if (IsColonCode(emote.Code))
            {
                // ⚠️ Case is ignored, so :Wave: finds :wave:. The first spelling seen wins either way.
                colon.TryAdd(emote.Code, emote);
            }
            else if (exact.Add(emote.Code)) other.Add(emote);
        }

        _colon = colon;
        _other = [.. other.OrderByDescending(e => e.Code.Length)];
        _colonSorted = [.. colon.Values.OrderBy(e => e.Code, StringComparer.OrdinalIgnoreCase)];
        _pictures = pictures;
        Changed?.Invoke();
    }

    public static bool IsColonCode(string code) =>
        code.Length >= 3 && code[0] == ':' && code[^1] == ':' && !code.AsSpan(1, code.Length - 2).ContainsAny(": \t\r\n");

    public EmoteRef? Find(string code) =>
        _colon.TryGetValue(code, out var emote) ? emote : _other.FirstOrDefault(e => e.Code == code);

    /// <summary>The message cut into text and emotes. A single piece of text when there are none in it.</summary>
    public IReadOnlyList<EmotePiece> Split(string text)
    {
        if (IsEmpty || text.Length == 0) return [new EmotePiece(text, null)];

        var pieces = new List<EmotePiece>();
        var plain = new StringBuilder();
        var links = UrlPattern().Matches(text);
        var link = 0;

        void Flush()
        {
            if (plain.Length == 0) return;
            pieces.Add(new EmotePiece(plain.ToString(), null));
            plain.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            while (link < links.Count && links[link].Index + links[link].Length <= i) link++;
            if (link < links.Count && links[link].Index == i)
            {
                plain.Append(links[link].Value);
                i += links[link].Length;
                continue;
            }

            if (text[i] == ':' && _colon.Count > 0)
            {
                var end = text.IndexOf(':', i + 1);
                if (end > i + 1 && end - i < LongestColonCode
                    && _colon.TryGetValue(text[i..(end + 1)], out var emote) && IsColonCode(text[i..(end + 1)]))
                {
                    Flush();
                    pieces.Add(new EmotePiece(text[i..(end + 1)], emote));
                    i = end + 1;
                    continue;
                }
            }

            if (_other.Count > 0 && (i == 0 || char.IsWhiteSpace(text[i - 1])) && OtherAt(text, i) is { } face)
            {
                Flush();
                pieces.Add(new EmotePiece(face.Code, face));
                i += face.Code.Length;
                continue;
            }

            plain.Append(text[i]);
            i++;
        }

        Flush();
        return pieces;
    }

    private EmoteRef? OtherAt(string text, int at)
    {
        foreach (var emote in _other)
        {
            var end = at + emote.Code.Length;
            if (end <= text.Length && string.CompareOrdinal(text, at, emote.Code, 0, emote.Code.Length) == 0
                && (end == text.Length || char.IsWhiteSpace(text[end])))
                return emote;
        }
        return null;
    }

    /// <summary>The colon codes that start with what has been typed so far, such as <c>:wa</c>, in alphabetical order.</summary>
    public IReadOnlyList<EmoteRef> StartingWith(string typed, int most)
    {
        if (typed.Length < 2 || typed[0] != ':') return [];

        var found = new List<EmoteRef>();
        foreach (var emote in _colonSorted)
        {
            if (!emote.Code.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) continue;
            found.Add(emote);
            if (found.Count == most) break;
        }
        return found;
    }

    /// <summary>
    /// The colon code being typed just before the caret, such as <c>:wa</c>, and where it starts: once a colon and at
    /// least two more characters are in, at the start, after a space, or right after another code. Null otherwise.
    /// </summary>
    public static (int Start, string Typed)? TypedCode(string text, int caret)
    {
        if (caret <= 0 || caret > text.Length) return null;

        var i = caret - 1;
        while (i >= 0 && text[i] != ':' && !char.IsWhiteSpace(text[i])) i--;
        if (i < 0 || text[i] != ':' || caret - i < 3) return null;
        if (i > 0 && !char.IsWhiteSpace(text[i - 1]) && text[i - 1] != ':') return null;

        return (i, text[i..caret]);
    }

    /// <summary>The picker's pictures with a code containing the words typed, case ignored. All of them for none.</summary>
    public IReadOnlyList<EmotePickItem> Search(string words)
    {
        var term = words.Trim();
        if (term.Length == 0) return _pictures;
        return [.. _pictures.Where(p => p.Codes.Any(c => c.Contains(term, StringComparison.OrdinalIgnoreCase)))];
    }

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
