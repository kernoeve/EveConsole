using System.Text;
using System.Xml.Linq;

namespace EveConsole.Services.Jabber;

/// <summary>What else a message sent says: that it corrects an earlier one, or that it answers one.</summary>
/// <param name="ReplaceId">The id of this account's message it corrects (XEP-0308).</param>
/// <param name="ReplyTo">Who wrote what it answers: room/nickname in a room, their address with a person.</param>
/// <param name="ReplyId">What it answers: the room's id for it in a room, its own id with a person (XEP-0461).</param>
/// <param name="ReplyQuote">The words it answers and who wrote them, quoted ahead of the text for clients that cannot show a reply.</param>
public sealed record XmppSendExtras(string ReplaceId = "", string ReplyTo = "", string ReplyId = "", string ReplyQuote = "");

public sealed partial class XmppClient
{
    public static readonly XNamespace ReplyNs  = "urn:xmpp:reply:0";
    public static readonly XNamespace Fallback = "urn:xmpp:fallback:0";
    public static readonly XNamespace Markers  = "urn:xmpp:chat-markers:0";
    public static readonly XNamespace OccupantIdNs = "urn:xmpp:occupant-id:0";

    /// <summary>The longest a reply's quote of what it answers is, for those who see it as text.</summary>
    private const int LongestQuote = 200;

    /// <summary>
    /// A reply's quote, as older clients show it (XEP-0461's fallback): who wrote it, then up to two lines of it, each
    /// marked as quoted. The text follows on the next line.
    /// </summary>
    public static string QuoteFor(string who, string words)
    {
        var lines = words.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(2).ToList();
        var quoted = string.Join("\n", lines.Select(l => "> " + (l.Length > LongestQuote ? l[..LongestQuote] + "…" : l)));
        return $"> {who}:\n{quoted}\n";
    }

    /// <summary>The message stanza for words sent, with what else it says. ⚠️ Its id is new for good, not per connection.</summary>
    public static XElement OutgoingMessage(string id, string to, string type, string body, XmppSendExtras? extras = null, string? fileUrl = null)
    {
        var quote = extras is { ReplyId.Length: > 0 } ? extras.ReplyQuote : "";
        var text = quote + Clean(body);

        var message = new XElement(Client + "message",
            new XAttribute("to", to), new XAttribute("type", type), new XAttribute("id", id),
            new XElement(Client + "body", text),

            // The same id again, kept by the server whoever relays it: what a correction, delete or reply names.
            new XElement(StanzaId + "origin-id", new XAttribute("id", id)));

        // Says this client understands typing notices, so one that does can start sending them; and asks to be told
        // when it has been seen, as people's other clients do with each other.
        if (type == "chat")
        {
            message.Add(new XElement(ChatStates + "active"));
            message.Add(new XElement(Markers + "markable"));
        }

        if (extras is { ReplaceId.Length: > 0 })
            message.Add(new XElement(Correct + "replace", new XAttribute("id", extras.ReplaceId)));

        if (extras is { ReplyId.Length: > 0 })
        {
            message.Add(new XElement(ReplyNs + "reply", new XAttribute("to", extras.ReplyTo), new XAttribute("id", extras.ReplyId)));
            if (quote.Length > 0)
                message.Add(new XElement(Fallback + "fallback", new XAttribute("for", ReplyNs.NamespaceName),
                    new XElement(Fallback + "body", new XAttribute("start", 0), new XAttribute("end", Runes(quote)))));
        }

        // Tells a client that shows pictures that the words are a file's address, not something someone wrote.
        if (fileUrl is not null) message.Add(new XElement(Oob + "x", new XElement(Oob + "url", fileUrl)));
        return message;
    }

    private static int Runes(string text) => text.EnumerateRunes().Count();

    /// <summary>
    /// The words of a message without what is there only for clients that cannot show a reply (XEP-0461 and 0428): the
    /// ranges it marks as fallback for a reply, counted in characters as the protocol counts them, taken out. Also what
    /// they quoted, as the best that can be shown of what is answered when that is not kept here.
    /// </summary>
    public static (string Body, string Quote) WithoutReplyFallback(XElement inner, string body)
    {
        var ranges = inner.Elements(Fallback + "fallback")
            .Where(f => (string?)f.Attribute("for") == ReplyNs.NamespaceName)
            .SelectMany(f => f.Elements(Fallback + "body"))
            .Select(b => (Start: (int?)b.Attribute("start") ?? 0, End: (int?)b.Attribute("end") ?? 0))
            .Where(r => r.Start >= 0 && r.End > r.Start)
            .OrderBy(r => r.Start).ToList();
        if (ranges.Count == 0) return (body, "");

        var runes = body.EnumerateRunes().ToList();
        var kept = new StringBuilder();
        var quoted = new StringBuilder();
        for (var i = 0; i < runes.Count; i++)
            (ranges.Any(r => i >= r.Start && i < r.End) ? quoted : kept).Append(runes[i].ToString());

        var quote = string.Join(" ", quoted.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('>').Trim()).Where(l => l.Length > 0));
        return (kept.ToString().TrimStart('\n'), quote);
    }

    /// <summary>
    /// Asks that one of this account's messages be taken back (XEP-0424), in the current form and the older one still
    /// in use, with words for clients that can do neither.
    /// </summary>
    /// <param name="target">What the current form names: the room's id for it in a room, its own id with a person.</param>
    /// <param name="ownId">Its own id, which the older form names.</param>
    public static XElement RetractionMessage(string to, string type, string target, string ownId, string words) =>
        new(Client + "message", new XAttribute("to", to), new XAttribute("type", type), new XAttribute("id", NewId()),
            new XElement(Retract + "retract", new XAttribute("id", target)),
            new XElement(Fasten + "apply-to", new XAttribute("id", ownId), new XElement(OldRetract + "retract")),
            new XElement(Fallback + "fallback", new XAttribute("for", Retract.NamespaceName)),
            new XElement(Client + "body", words),
            new XElement(Hints + "store"));

    public Task SendRetractionAsync(string to, bool isRoom, string target, string ownId, string words, CancellationToken ct) =>
        connection.WriteAsync(RetractionMessage(to, isRoom ? "groupchat" : "chat", target, ownId, words), ct);

    /// <summary>Tells someone their messages up to this one have been seen (XEP-0333).</summary>
    public static XElement DisplayedMarker(string to, string id) =>
        new(Client + "message", new XAttribute("to", to), new XAttribute("type", "chat"), new XAttribute("id", NewId()),
            new XElement(Markers + "displayed", new XAttribute("id", id)),
            new XElement(Hints + "store"));

    public Task SendDisplayedAsync(string to, string id, CancellationToken ct) => connection.WriteAsync(DisplayedMarker(to, id), ct);

    /// <summary>Reads what a message says beyond its words: a reply, a marker, whether it asks for one, who sent it in a room.</summary>
    private static XmppMessage WithExtras(XmppMessage message, XElement inner)
    {
        if (inner.Element(ReplyNs + "reply") is { } reply && (string?)reply.Attribute("id") is { Length: > 0 } replyId)
        {
            var (body, quote) = WithoutReplyFallback(inner, message.Body);
            message = message with { Body = body, ReplyId = replyId, ReplyTo = (string?)reply.Attribute("to") ?? "", ReplyQuote = quote };
        }

        if (inner.Element(Markers + "displayed") is { } displayed && (string?)displayed.Attribute("id") is { Length: > 0 } seen)
            message = message with { DisplayedId = seen };

        return message with
        {
            Markable = inner.Element(Markers + "markable") is not null,
            OccupantId = (string?)inner.Element(OccupantIdNs + "occupant-id")?.Attribute("id") ?? "",
            OriginId = (string?)inner.Element(StanzaId + "origin-id")?.Attribute("id") ?? "",
        };
    }
}
