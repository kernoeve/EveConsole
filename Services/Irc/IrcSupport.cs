namespace EveConsole.Services.Irc;

/// <summary>
/// What the server says about itself in its 005 lines (ISUPPORT): how it compares names, what a channel name starts with,
/// the ranks people can hold in a channel, and the limits it keeps.
/// </summary>
public sealed class IrcSupport
{
    public string Network { get; private set; } = "";

    /// <summary>The server's program and version from 004 (e.g. "ergo-2.19.1"): what its mode letters mean depends on it.</summary>
    public string Software { get; set; } = "";

    /// <summary>"rfc1459" (the default), "ascii" or "rfc1459-strict".</summary>
    public string CaseMapping { get; private set; } = "rfc1459";

    public string ChannelTypes { get; private set; } = "#&";

    /// <summary>Channel ranks from highest to lowest: the mode letter and the symbol shown before a name, e.g. (o, @).</summary>
    public IReadOnlyList<(char Mode, char Symbol)> Ranks { get; private set; } = [('o', '@'), ('v', '+')];

    /// <summary>Channel modes by kind (CHANMODES): A lists (ban), B always with a value, C with one when set, D flags.</summary>
    public string ModesA { get; private set; } = "beI";
    public string ModesB { get; private set; } = "k";
    public string ModesC { get; private set; } = "l";
    public string ModesD { get; private set; } = "imnpst";

    /// <summary>The most messages a CHATHISTORY request gives, 0 when the server keeps none to ask for.</summary>
    public int ChatHistory { get; private set; }

    /// <summary>The most names MONITOR watches, 0 when the server cannot.</summary>
    public int Monitor { get; private set; }

    /// <summary>
    /// Where the server takes files (draft/FILEHOST, or soju.im/FILEHOST as soju first named it): a web address, blank
    /// when it named none. ⚠️ The plain FILEHOST name is not read: the protocol forbids it until the draft is final.
    /// </summary>
    public string FileHost { get; private set; } = "";

    public int NickLength { get; private set; } = 30;
    public int TopicLength { get; private set; } = 390;
    public bool Utf8Only { get; private set; }

    /// <summary>Takes in one 005 line's tokens (the parameters between the nick and the trailing text).</summary>
    public void Apply(IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (token.StartsWith('-')) continue;   // a withdrawn token: the defaults are fine
            var eq = token.IndexOf('=');
            var key = (eq < 0 ? token : token[..eq]).ToUpperInvariant();
            var value = eq < 0 ? "" : IrcMessage.Unescape(token[(eq + 1)..].Replace("\\x20", " "));

            switch (key)
            {
                case "NETWORK": Network = value; break;
                case "CASEMAPPING": CaseMapping = value.ToLowerInvariant(); break;
                case "CHANTYPES": ChannelTypes = value; break;
                case "PREFIX": Ranks = ParsePrefix(value) ?? Ranks; break;
                case "CHANMODES":
                    var kinds = value.Split(',');
                    if (kinds.Length >= 4) { ModesA = kinds[0]; ModesB = kinds[1]; ModesC = kinds[2]; ModesD = kinds[3]; }
                    break;
                case "CHATHISTORY":
                case "DRAFT/CHATHISTORY":
                    ChatHistory = int.TryParse(value, out var most) && most > 0 ? most : Math.Max(ChatHistory, 100);
                    break;
                case "MONITOR": Monitor = int.TryParse(value, out var watch) ? watch : 100; break;
                case "NICKLEN": if (int.TryParse(value, out var n) && n > 0) NickLength = n; break;
                case "TOPICLEN": if (int.TryParse(value, out var t) && t > 0) TopicLength = t; break;
                case "UTF8ONLY": Utf8Only = true; break;
                case "DRAFT/FILEHOST": FileHost = value.Trim(); _fileHostIsDraft = true; break;
                case "SOJU.IM/FILEHOST": if (!_fileHostIsDraft) FileHost = value.Trim(); break;
            }
        }
    }

    /// <summary>The draft name was given, and is the one kept whatever order the two came in.</summary>
    private bool _fileHostIsDraft;

    private static IReadOnlyList<(char, char)>? ParsePrefix(string value)
    {
        // (ov)@+ : modes in brackets, symbols after, in the same order.
        if (value.Length < 2 || value[0] != '(') return value.Length == 0 ? [] : null;
        var close = value.IndexOf(')');
        if (close < 0) return null;
        var modes = value[1..close];
        var symbols = value[(close + 1)..];
        if (modes.Length != symbols.Length) return null;
        return [.. modes.Zip(symbols)];
    }

    public bool IsChannel(string target) => target.Length > 0 && ChannelTypes.Contains(target[0]);

    /// <summary>The rank a mode letter gives, 0 for the highest, or -1 when it is not a rank.</summary>
    public int RankOfMode(char mode)
    {
        for (var i = 0; i < Ranks.Count; i++) if (Ranks[i].Mode == mode) return i;
        return -1;
    }

    public int RankOfSymbol(char symbol)
    {
        for (var i = 0; i < Ranks.Count; i++) if (Ranks[i].Symbol == symbol) return i;
        return -1;
    }

    /// <summary>Whether a mode letter takes a value when set (+) or taken off (-), as CHANMODES and PREFIX say.</summary>
    public bool TakesValue(char mode, bool adding) =>
        RankOfMode(mode) >= 0 || ModesA.Contains(mode) || ModesB.Contains(mode) || (adding && ModesC.Contains(mode));

    /// <summary>A name as the server compares it.</summary>
    public string Fold(string name) => Fold(name, CaseMapping);

    public static string Fold(string name, string caseMapping)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= 'A' and <= 'Z') chars[i] = (char)(c + 32);
            else if (caseMapping != "ascii")
                chars[i] = c switch
                {
                    '[' => '{', ']' => '}', '\\' => '|',
                    '^' when caseMapping == "rfc1459" => '~',
                    _ => c,
                };
        }
        return new string(chars);
    }

    public bool Same(string a, string b) => Fold(a) == Fold(b);
}
