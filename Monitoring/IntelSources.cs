using System.Globalization;
using System.Text;
using EveConsole.Models;

namespace EveConsole.Monitoring;

/// <summary>
/// What a Jabber room marked Intel adds to the intel channels of the chat logs. Its messages go through the same
/// table and the same parser, so the map, the alarms and a re-parse need to know nothing about where a line came from.
/// </summary>
public static class IntelRooms
{
    /// <summary>A line relayed with its own time keeps it, unless it is too far from when it arrived to be believed.</summary>
    private static readonly TimeSpan MostRelayDelay = TimeSpan.FromHours(1);

    /// <summary>How many lines of one message are read: a relay can send several at once.</summary>
    private const int MostLines = 100;

    /// <summary>The name its reports go under.</summary>
    public static string ChannelOf(JabberRoom room) =>
        "Jabber: " + (room.Name.Length > 0 ? room.Name : room.RoomJid.Split('@')[0]);

    /// <summary>
    /// A room message as chat lines, one per line of it. A line that is a chat log line relayed whole is read as what
    /// it was: who really said it and when, or the relay would be taken for the reporter and its name for a pilot.
    /// </summary>
    public static List<ChatMessage> ToChatLines(JabberRoom room, JabberStoredMessage message)
    {
        var lines = new List<ChatMessage>();
        if (!message.IsRoom || message.Sender.Length == 0) return lines;

        var number = 0L;
        foreach (var line in message.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MostLines))
        {
            number++;
            var (at, sender, text) = (message.SentAt, message.Sender, line);

            if (ChatLogRules.TryParseMessage(line) is { } relayed)
            {
                if (relayed.IsSystem) continue;

                (sender, text) = (relayed.Sender, relayed.Text);
                if (relayed.Timestamp is { } said && Parse(message.SentAt) is { } arrived && (said - arrived).Duration() <= MostRelayDelay)
                    at = ChatLogRules.FormatTimestamp(said);
            }

            lines.Add(new ChatMessage
            {
                OccurredAt  = at,
                ChannelName = ChannelOf(room),
                SenderName  = sender,
                Message     = text,
                SourceFile  = $"jabber:{room.AccountId}:{room.RoomJid}",

                // ⚠️ Unique with the source, so room for each line of each message.
                LineNumber  = (long)message.Id * (MostLines + 1) + number,
            });
        }

        return lines;
    }

    internal static DateTimeOffset? Parse(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
}

/// <summary>
/// Finds a report that is already held from another source, as when someone relays a chat channel into a Jabber room.
/// Either copy can arrive first, and with its own time, so this looks both ways in time.
///
/// <para>The same report is the same system, close in time, from another channel, and either the same words or the
/// same facts. Words are matched from the end of the longer line, since a relay puts its label or its reporter's name
/// in front. A shorter line has to say something beyond the system: "QZ-X77" is not the same report as "Sampler QZ-X77".</para>
///
/// <para>Within one channel nothing is a copy: two people calling the same pilot there is how a pilot stays on the map.</para>
/// </summary>
public sealed class IntelDuplicates
{
    /// <summary>A hand relay takes a moment and clocks differ a little. Long enough for those, short enough that a
    /// pilot still there later is still news.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private sealed record Held(string Channel, DateTimeOffset At, string Words, string Facts, bool SaysSomething);

    private readonly Dictionary<int, List<Held>> _bySystem = [];

    /// <summary>Remembers a report, so later ones can be measured against it.</summary>
    public void Add(IntelReport report, IEnumerable<long> pilots)
    {
        if (Read(report, pilots) is not { } held) return;

        if (!_bySystem.TryGetValue(report.SystemId, out var list)) _bySystem[report.SystemId] = list = [];
        list.Add(held);
    }

    /// <summary>Whether this report is already held from another channel.</summary>
    public bool IsCopy(IntelReport report, IEnumerable<long> pilots)
    {
        if (Read(report, pilots) is not { } candidate || !_bySystem.TryGetValue(report.SystemId, out var list)) return false;

        return list.Any(held =>
            !string.Equals(held.Channel, candidate.Channel, StringComparison.OrdinalIgnoreCase)
            && (held.At - candidate.At).Duration() <= Window
            && (held.Facts == candidate.Facts || SameWords(held, candidate)));
    }

    private static bool SameWords(Held a, Held b)
    {
        if (a.Words == b.Words) return true;

        var (longer, shorter) = a.Words.Length >= b.Words.Length ? (a, b) : (b, a);
        return shorter.SaysSomething && longer.Words.EndsWith(" " + shorter.Words, StringComparison.Ordinal);
    }

    private static Held? Read(IntelReport report, IEnumerable<long> pilots)
    {
        if (IntelRooms.Parse(report.ReportedAt) is not { } at) return null;

        var named = string.Join(',', pilots.Order());
        var facts = $"{named}|{report.PlayerCount}|{report.Flags}|{report.NoVisual}|{report.Gate}|{report.Ships}";
        var says  = report.PlayerCount > 0 || report.Flags != 0 || report.NoVisual || !string.IsNullOrEmpty(report.Gate) || named.Length > 0;
        return new Held(report.ChannelName, at, Words(report.Message), facts, says);
    }

    /// <summary>Letters and digits only, in lower case and single spaces: a copy may lose its capitals and its spacing.</summary>
    internal static string Words(string text)
    {
        var words = new StringBuilder(text.Length);
        foreach (var c in text)
            if (char.IsLetterOrDigit(c)) words.Append(char.ToLowerInvariant(c));
            else if (words.Length > 0 && words[^1] != ' ') words.Append(' ');

        return words.ToString().TrimEnd();
    }
}
