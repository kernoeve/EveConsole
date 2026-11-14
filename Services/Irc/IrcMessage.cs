using System.Text;

namespace EveConsole.Services.Irc;

/// <summary>
/// One IRC line: its tags (IRCv3 message-tags), who sent it, the command, and its parameters. Parsed from and written back
/// to the wire format, <c>@tags :prefix COMMAND params :trailing</c>.
/// </summary>
/// <remarks>
/// ⚠️ A line is untrusted text from a server, or from whoever it relays. Parsing never throws for content: a line that
/// cannot be read gives null, a tag that cannot be unescaped is taken as written, and every limit (tags, parameters,
/// length) is applied here so nothing behind this has to.
/// </remarks>
public sealed class IrcMessage
{
    /// <summary>Longest line read, tags included (IRCv3 allows 8191 for tags plus 512 for the rest).</summary>
    public const int MaxLineLength = 8191 + 512;

    /// <summary>Longest line written, without tags, CR LF included: what every server accepts.</summary>
    public const int MaxOutgoingBytes = 512;

    private const int MaxParams = 15;

    public IReadOnlyDictionary<string, string> Tags { get; init; } = Empty;

    /// <summary>Who sent it: a server name or <c>nick!user@host</c>. Blank when the line had none.</summary>
    public string Prefix { get; init; } = "";

    /// <summary>Upper case: a word (PRIVMSG) or three digits (001).</summary>
    public string Command { get; init; } = "";

    public IReadOnlyList<string> Params { get; init; } = [];

    /// <summary>
    /// The kind of batch this line came in ("chathistory"...), worked out by <see cref="IrcClient"/> as it read the line.
    /// Blank for a line not in one.
    /// </summary>
    /// <remarks>
    /// ⚠️ Set on the reading thread, before the line is handed on. Lines are handled later, off that thread, by when
    /// the batch's end has been read and the batch forgotten: an old JOIN from history was then taken as live, and
    /// emptied the channel's list of people.
    /// </remarks>
    public string Batch { get; internal set; } = "";

    private static readonly Dictionary<string, string> Empty = [];

    public IrcMessage() { }

    public IrcMessage(string command, params string[] parameters)
    {
        Command = command;
        Params = parameters;
    }

    /// <summary>The parameter at this place, or blank.</summary>
    public string this[int index] => index >= 0 && index < Params.Count ? Params[index] : "";

    /// <summary>The last parameter, which is usually the words.</summary>
    public string Last => Params.Count > 0 ? Params[^1] : "";

    /// <summary>The nickname part of the prefix: up to the first '!' or '@'.</summary>
    public string Nick
    {
        get
        {
            var end = Prefix.IndexOfAny(['!', '@']);
            return end < 0 ? Prefix : Prefix[..end];
        }
    }

    /// <summary>The user@host part of the prefix, or blank.</summary>
    public string UserHost
    {
        get
        {
            var bang = Prefix.IndexOf('!');
            return bang < 0 ? "" : Prefix[(bang + 1)..];
        }
    }

    public string? Tag(string name) => Tags.TryGetValue(name, out var v) ? v : null;

    /// <summary>Reads one line, without its CR LF. Null when it is not an IRC line at all.</summary>
    public static IrcMessage? Parse(string? line)
    {
        if (string.IsNullOrEmpty(line) || line.Length > MaxLineLength) return null;

        var pos = 0;
        var tags = Empty;

        if (line[0] == '@')
        {
            var end = line.IndexOf(' ');
            if (end < 0) return null;
            tags = ParseTags(line[1..end]);
            pos = SkipSpaces(line, end);
        }

        var prefix = "";
        if (pos < line.Length && line[pos] == ':')
        {
            var end = line.IndexOf(' ', pos);
            if (end < 0) return null;
            prefix = line[(pos + 1)..end];
            pos = SkipSpaces(line, end);
        }

        var cmdEnd = line.IndexOf(' ', pos);
        var command = (cmdEnd < 0 ? line[pos..] : line[pos..cmdEnd]).ToUpperInvariant();
        if (command.Length == 0 || !command.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9')) return null;
        pos = cmdEnd < 0 ? line.Length : SkipSpaces(line, cmdEnd);

        var parameters = new List<string>();
        while (pos < line.Length)
        {
            if (line[pos] == ':' || parameters.Count == MaxParams - 1)
            {
                parameters.Add(line[pos] == ':' ? line[(pos + 1)..] : line[pos..]);
                break;
            }
            var end = line.IndexOf(' ', pos);
            if (end < 0) { parameters.Add(line[pos..]); break; }
            parameters.Add(line[pos..end]);
            pos = SkipSpaces(line, end);
        }

        return new IrcMessage { Tags = tags, Prefix = prefix, Command = command, Params = parameters };
    }

    private static int SkipSpaces(string line, int at)
    {
        while (at < line.Length && line[at] == ' ') at++;
        return at;
    }

    private static Dictionary<string, string> ParseTags(string raw)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            if (key.Length == 0 || tags.Count >= 64) continue;
            // A repeated key: the last one counts, as the spec says.
            tags[key] = eq < 0 ? "" : Unescape(part[(eq + 1)..]);
        }
        return tags;
    }

    /// <summary>A tag value as written on the wire, back to its text.</summary>
    public static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0) return value;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { sb.Append(value[i]); continue; }
            if (++i >= value.Length) break;   // a lone trailing backslash is dropped
            sb.Append(value[i] switch { ':' => ';', 's' => ' ', 'r' => '\r', 'n' => '\n', var other => other });
        }
        return sb.ToString();
    }

    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
            sb.Append(c switch { ';' => "\\:", ' ' => "\\s", '\\' => "\\\\", '\r' => "\\r", '\n' => "\\n", '\0' => "", _ => c.ToString() });
        return sb.ToString();
    }

    /// <summary>
    /// The line to send, without CR LF. ⚠️ Line breaks in a parameter would end the line early and let the rest be read
    /// as a command of its own, so they are replaced by spaces here, whoever built the message.
    /// </summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        if (Tags.Count > 0)
        {
            sb.Append('@');
            sb.AppendJoin(';', Tags.Select(t => t.Value.Length == 0 ? Clean(t.Key) : $"{Clean(t.Key)}={Escape(t.Value)}"));
            sb.Append(' ');
        }
        if (Prefix.Length > 0) sb.Append(':').Append(Clean(Prefix)).Append(' ');
        sb.Append(Command);

        for (var i = 0; i < Params.Count; i++)
        {
            var p = Clean(Params[i]);
            var last = i == Params.Count - 1;
            sb.Append(' ');
            if (last && (p.Length == 0 || p.Contains(' ') || p[0] == ':')) sb.Append(':');
            else if (!last && (p.Length == 0 || p.Contains(' ') || p[0] == ':')) p = p.Replace(' ', '_').TrimStart(':') is { Length: > 0 } s ? s : "*";
            sb.Append(p);
        }
        return sb.ToString();
    }

    private static string Clean(string s) =>
        s.IndexOfAny(['\r', '\n', '\0']) < 0 ? s : s.Replace('\r', ' ').Replace('\n', ' ').Replace("\0", "");

    public override string ToString() => Serialize();
}
