using System.Globalization;
using System.Text;
using EveConsole.Models;

namespace EveConsole.Monitoring;

/// <summary>
/// What a Jabber room or IRC channel marked Intel adds to the intel channels of the chat logs. Its messages go through
/// the same table and the same parser, so the map, the alarms and a re-parse need to know nothing about where a line came from.
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
    public static List<ChatMessage> ToChatLines(JabberRoom room, JabberStoredMessage message) =>
        !message.IsRoom ? [] : ToChatLines(ChannelOf(room), $"jabber:{room.AccountId}:{room.RoomJid}", message.Id, message.Sender, message.SentAt, message.Body);

    /// <summary>The name an IRC channel's reports go under.</summary>
    public static string ChannelOf(IrcChannel channel) => "IRC: " + channel.Name;

    /// <summary>An IRC channel's message as chat lines, as a Jabber room's: a relayed chat log line is read as what it was.</summary>
    public static List<ChatMessage> ToChatLines(IrcChannel channel, IrcStoredMessage message) =>
        !message.IsChannel || message.Kind == IrcMessageKind.Notice ? [] :
        ToChatLines(ChannelOf(channel), $"irc:{channel.ServerId}:{channel.Name.ToLowerInvariant()}", message.Id, message.Sender, message.SentAt, message.Body);

    private static List<ChatMessage> ToChatLines(string channelName, string sourceFile, long id, string messageSender, string sentAt, string body)
    {
        var lines = new List<ChatMessage>();
        if (messageSender.Length == 0) return lines;

        var number = 0L;
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MostLines))
        {
            number++;
            var (at, sender, text) = (sentAt, messageSender, line);

            if (ChatLogRules.TryParseMessage(line) is { } relayed)
            {
                if (relayed.IsSystem) continue;

                (sender, text) = (relayed.Sender, relayed.Text);
                if (relayed.Timestamp is { } said && Parse(sentAt) is { } arrived && (said - arrived).Duration() <= MostRelayDelay)
                    at = ChatLogRules.FormatTimestamp(said);
            }

            lines.Add(new ChatMessage
            {
                OccurredAt  = at,
                ChannelName = channelName,
                SenderName  = sender,
                Message     = text,
                SourceFile  = sourceFile,

                // ⚠️ Unique with the source, so room for each line of each message.
                LineNumber  = id * (MostLines + 1) + number,
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
///
/// <para>A copy is stored as <see cref="IntelReportKind.Copy"/>, pointing at the report it repeats (<see cref="CopyOf(IntelReport, IEnumerable{IntelReportCharacter})"/>
/// says which): the line is no chatter, and it adds nothing but that the report was heard in one more place.</para>
/// </summary>
public sealed class IntelDuplicates
{
    /// <summary>A hand relay takes a moment and clocks differ a little. Long enough for those, short enough that a
    /// pilot still there later is still news.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private sealed record Held(IntelReport Report, string Channel, DateTimeOffset At, string Words, string Facts, bool SaysSomething,
                               bool IsClear = false);

    /// <summary>
    /// Whether a clear repeats an earlier clear of the same system from another channel within <see cref="Window"/>:
    /// a relay of it, most likely. Such a clear is still stored (see <see cref="IsCopy(IntelReport, IEnumerable{long})"/>)
    /// but retires nothing of its own: stamped with the relay's later time, it would also retire every sighting
    /// called in between the clear and its relay.
    /// </summary>
    public bool RelaysClear(IntelReport report) =>
        report.Kind == IntelReportKind.Clear
        && IntelRooms.Parse(report.ReportedAt) is { } at
        && _bySystem.TryGetValue(report.SystemId, out var list)
        && list.Any(held => held.IsClear && Relays(report.ChannelName, at, held.Channel, held.At));

    /// <summary><see cref="RelaysClear"/> for two clears already read: the later one relays the earlier one.</summary>
    public static bool RelaysClear(IntelReport later, DateTimeOffset laterAt, IntelReport earlier, DateTimeOffset earlierAt) =>
        later.Kind == IntelReportKind.Clear && earlier.Kind == IntelReportKind.Clear && later.SystemId == earlier.SystemId
        && Relays(later.ChannelName, laterAt, earlier.ChannelName, earlierAt);

    private static bool Relays(string laterChannel, DateTimeOffset laterAt, string earlierChannel, DateTimeOffset earlierAt) =>
        !string.Equals(laterChannel, earlierChannel, StringComparison.OrdinalIgnoreCase)
        && earlierAt <= laterAt && laterAt - earlierAt <= Window;

    private readonly Dictionary<int, List<Held>> _bySystem = [];

    /// <summary>Remembers a report, so later ones can be measured against it.</summary>
    public void Add(IntelReport report, IEnumerable<IntelReportCharacter> pilots) =>
        Add(report, pilots.Select(p => (p.CharacterId, p.ShipTypeId)));

    /// <summary>Remembers a report whose pilots' hulls are not known.</summary>
    public void Add(IntelReport report, IEnumerable<long> pilots) =>
        Add(report, pilots.Select(id => (id, (int?)null)));

    private void Add(IntelReport report, IEnumerable<(long Id, int? Hull)> pilots)
    {
        // A copy is never held itself: a third channel's relay is a copy of the report, not of the copy, and a copy
        // stored names no pilots, so its facts would not match anyway.
        if (report.Kind == IntelReportKind.Copy || Read(report, pilots) is not { } held) return;

        if (!_bySystem.TryGetValue(report.SystemId, out var list)) _bySystem[report.SystemId] = list = [];
        list.Add(held);
    }

    /// <summary>Whether this report is already held from another channel.</summary>
    public bool IsCopy(IntelReport report, IEnumerable<IntelReportCharacter> pilots) => CopyOf(report, pilots) is not null;

    /// <summary>Whether this report, whose pilots' hulls are not known, is already held from another channel.</summary>
    public bool IsCopy(IntelReport report, IEnumerable<long> pilots) => CopyOf(report, pilots) is not null;

    /// <summary>The report held from another channel that this one repeats, the earliest when several do; null when none.</summary>
    public IntelReport? CopyOf(IntelReport report, IEnumerable<IntelReportCharacter> pilots) =>
        CopyOf(report, pilots.Select(p => (p.CharacterId, p.ShipTypeId)));

    /// <summary><see cref="CopyOf(IntelReport, IEnumerable{IntelReportCharacter})"/> for a report whose pilots' hulls are not known.</summary>
    public IntelReport? CopyOf(IntelReport report, IEnumerable<long> pilots) =>
        CopyOf(report, pilots.Select(id => (id, (int?)null)));

    private IntelReport? CopyOf(IntelReport report, IEnumerable<(long Id, int? Hull)> pilots)
    {
        // ⚠️ A clear is never a copy. It names nobody and counts nothing, so a second one doubles nothing; and every
        // clear of a system has the same facts, so "<system> clr" called in a second channel was dropped here, left
        // with no report, and shown in the Feed as chatter. A relayed one is stored but retires nothing of its own
        // (RelaysClear), and the Board and Feed read it as the clear it relays.
        if (report.Kind != IntelReportKind.Sighting) return null;

        if (Read(report, pilots) is not { } candidate || !_bySystem.TryGetValue(report.SystemId, out var list)) return null;

        // ⚠️ Only a sighting is ever the original: a sighting worded like a clear ("QZ-X77 clr" with a pilot named after
        // it) is not a copy of the clear, whatever the words.
        return list
            .Where(held => !held.IsClear
                           && !string.Equals(held.Channel, candidate.Channel, StringComparison.OrdinalIgnoreCase)
                           && (held.At - candidate.At).Duration() <= Window
                           && (held.Facts == candidate.Facts || SameWords(held, candidate)))
            .OrderBy(held => held.At)
            .Select(held => held.Report)
            .FirstOrDefault();
    }

    private static bool SameWords(Held a, Held b)
    {
        if (a.Words == b.Words) return true;

        var (longer, shorter) = a.Words.Length >= b.Words.Length ? (a, b) : (b, a);
        return shorter.SaysSomething && longer.Words.EndsWith(" " + shorter.Words, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a report says, to compare. ⚠️ Everything a follow-up line can add is in it: the heading and each pilot's
    /// hull as well as the counts and flags. A follow-up restates the report it adds to, so one that adds only a
    /// heading or a hull would otherwise look like a copy of that report and be dropped, when it came from another
    /// channel within <see cref="Window"/>; a relayed copy of the follow-up itself still matches it.
    /// </summary>
    private static Held? Read(IntelReport report, IEnumerable<(long Id, int? Hull)> pilots)
    {
        if (IntelRooms.Parse(report.ReportedAt) is not { } at) return null;

        var named = string.Join(',', pilots.OrderBy(p => p.Id).ThenBy(p => p.Hull)
            .Select(p => p.Hull is { } hull ? $"{p.Id}:{hull}" : p.Id.ToString(CultureInfo.InvariantCulture)));
        // The kind is in it too, so a clear can never match a sighting.
        var facts = $"{(int)report.Kind}|{named}|{report.PlayerCount}|{report.Flags}|{report.NoVisual}|{report.Gate}|{report.Ships}|{report.HeadingSystemId}";
        var says  = report.PlayerCount > 0 || report.Flags != 0 || report.NoVisual || !string.IsNullOrEmpty(report.Gate) || named.Length > 0
                 || report.HeadingSystemId is not null;
        return new Held(report, report.ChannelName, at, Words(report.Message), facts, says, report.Kind == IntelReportKind.Clear);
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
