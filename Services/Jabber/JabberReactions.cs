using System.Globalization;
using System.Xml.Linq;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Jabber;

/// <summary>One emoji under a message: how many chose it, who, and whether this account is among them.</summary>
public sealed record JabberReactionCount(string Emoji, int Count, IReadOnlyList<string> Who, bool Mine);

public sealed partial class XmppClient
{
    public static readonly XNamespace Reactions = "urn:xmpp:reactions:0";

    /// <summary>The most a person's reactions to one message can hold, and the longest one emoji can be.</summary>
    public const int MostReactions = 20;
    public const int LongestReaction = 32;

    /// <summary>
    /// What a reactions message (XEP-0444) is about and what it holds: the id of the message reacted to, and the
    /// sender's whole set of reactions to it now, an empty one taking them all back. Null for any other message.
    /// ⚠️ Each one checked: no spaces, nothing too long, none twice, and only so many.
    /// </summary>
    public static (string Target, List<string> Emojis)? ReactionsIn(XElement inner)
    {
        if (inner.Element(Reactions + "reactions") is not { } element || (string?)element.Attribute("id") is not { Length: > 0 } target) return null;

        var emojis = element.Elements(Reactions + "reaction")
            .Select(r => r.Value.Trim())
            .Where(IsReaction)
            .Distinct(StringComparer.Ordinal)
            .Take(MostReactions)
            .ToList();
        return (target, emojis);
    }

    /// <summary>One emoji: short, and with no spaces or letters of a word in it.</summary>
    public static bool IsReaction(string text) =>
        text.Length is > 0 and <= LongestReaction
        && !text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c < 0x80 && char.IsLetterOrDigit(c));

    /// <summary>A person's reactions to one message, as their whole set. Kept by the server, so a catch-up brings them too.</summary>
    public static XElement ReactionsMessage(string to, string type, string target, IEnumerable<string> emojis) =>
        new(Client + "message", new XAttribute("to", to), new XAttribute("type", type), new XAttribute("id", NewId()),
            new XElement(Reactions + "reactions", new XAttribute("id", target),
                emojis.Select(e => new XElement(Reactions + "reaction", e))),
            new XElement(Hints + "store"));

    public Task SendReactionsAsync(string to, bool isRoom, string target, IEnumerable<string> emojis, CancellationToken ct) =>
        connection.WriteAsync(ReactionsMessage(to, isRoom ? "groupchat" : "chat", target, emojis), ct);
}

/// <summary>Reactions: saved as they come, read for what is on screen, and sent as the person's whole set.</summary>
public sealed partial class JabberService
{
    /// <summary>Someone's reactions in a conversation changed: the account and the conversation.</summary>
    public event Action<int, string>? ReactionsChanged;

    /// <summary>
    /// Takes on a person's reactions to one message, replacing what they had. Only for a message that is saved here, by
    /// the room's id for it in a room or its own id with a person, so nothing piles up for messages long gone.
    /// </summary>
    private async Task ApplyReactionsAsync(AppDbContext db, int accountId, string conversation, bool isRoom, string sender,
        string target, IReadOnlyList<string> emojis, CancellationToken ct, string? senderName = null)
    {
        var recent = db.JabberMessages
            .Where(m => m.AccountId == accountId && m.Conversation == conversation)
            .OrderByDescending(m => m.Id).Take(2000);
        var known = isRoom ? await recent.AnyAsync(m => m.StanzaId == target, ct) : await recent.AnyAsync(m => m.MessageId == target, ct);
        if (!known) return;

        var row = await db.JabberReactions.FirstOrDefaultAsync(r => r.AccountId == accountId && r.Conversation == conversation && r.Target == target && r.Sender == sender, ct);
        var text = string.Join(' ', emojis);
        if (emojis.Count == 0)
        {
            if (row is null) return;
            db.JabberReactions.Remove(row);
        }
        else
        {
            if (row?.Emojis == text) return;
            if (row is null) db.JabberReactions.Add(row = new JabberReaction { AccountId = accountId, Conversation = conversation, Target = target, Sender = sender });
            row.Emojis = text;
            row.SenderName = senderName ?? sender;
            row.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }
        await db.SaveChangesAsync(ct);

        ReactionsChanged?.Invoke(accountId, conversation);
        Tell($"react|{accountId}|{conversation}");
    }

    /// <summary>
    /// The reactions to some messages of a conversation, counted: by the message's id, each emoji once with how many
    /// chose it and who, in the order they were first chosen.
    /// </summary>
    /// <param name="me">Who this account is there, to say which are its own: its nickname in a room, its address with a person.</param>
    public async Task<Dictionary<string, List<JabberReactionCount>>> ReactionsAsync(int accountId, string conversation, IReadOnlyCollection<string> targets,
        string me, CancellationToken ct = default)
    {
        if (targets.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wanted = targets.ToList();
        var rows = await db.JabberReactions.AsNoTracking()
            .Where(r => r.AccountId == accountId && r.Conversation == conversation && wanted.Contains(r.Target))
            .OrderBy(r => r.UpdatedAt).ToListAsync(ct);

        return Count(rows, me);
    }

    public static Dictionary<string, List<JabberReactionCount>> Count(IEnumerable<JabberReaction> rows, string me) =>
        rows.GroupBy(r => r.Target).ToDictionary(g => g.Key, g =>
            g.SelectMany(r => r.Emojis.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                 .Select(e => (Emoji: e, r.Sender, Name: r.SenderName.Length > 0 ? r.SenderName : r.Sender)))
             .GroupBy(x => x.Emoji)
             .Select(e => new JabberReactionCount(e.Key, e.Count(), [.. e.Select(x => x.Name)], e.Any(x => x.Sender == me)))
             .OrderByDescending(c => c.Count)   // most chosen first; equal ones as first chosen, the sort keeping their order
             .ToList());

    /// <summary>
    /// Sends this account's whole set of reactions to one message. Done where the connection is. With a person, saved here
    /// at once: nothing comes back to say it went. In a room, the room's own copy back to everyone saves it.
    /// </summary>
    public async Task SendReactionsAsync(JabberSession session, string conversation, bool isRoom, string target, IReadOnlyList<string> emojis, CancellationToken ct = default)
    {
        var kept = emojis.Where(XmppClient.IsReaction).Distinct(StringComparer.Ordinal).Take(XmppClient.MostReactions).ToList();
        if (!ActsHere)
        {
            await AskWorkerAsync(session.Account.Id, "react", new CommandArgs(conversation, IsRoom: isRoom, Text: target, Name: string.Join(' ', kept)), ct);
            return;
        }

        var client = session.Client ?? throw new XmppException(XmppFailure.Network, "Not connected.");
        await client.SendReactionsAsync(conversation, isRoom, target, kept, ct);

        if (!isRoom)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await ApplyReactionsAsync(db, session.Account.Id, conversation, false, XmppAddress.Parse(session.Account.Address).Bare, target, kept, ct);
        }
    }

    /// <summary>Reactions to messages no longer kept. Part of what clears old messages away.</summary>
    private static Task DeleteOrphanReactionsAsync(AppDbContext db, CancellationToken ct) =>
        db.JabberReactions.Where(r => !db.JabberMessages.Any(m => m.AccountId == r.AccountId && m.Conversation == r.Conversation
                                                                && (m.StanzaId == r.Target || m.MessageId == r.Target)))
          .ExecuteDeleteAsync(ct);
}
