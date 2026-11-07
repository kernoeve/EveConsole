using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;

namespace EveConsole.Services.Jabber;

/// <summary>Someone's availability, as the protocol's five words and the absence of a presence.</summary>
public enum XmppShow { Offline, Online, Chat, Away, ExtendedAway, DoNotDisturb }

/// <summary>A message as it arrived, with the wrapping of carbons and delays already taken off.</summary>
/// <param name="Sent">When it was really sent, for one that waited on the server. Null means just now.</param>
/// <param name="ServerId">The server's own id for it, where it gives one, to tell a repeat from a new one.</param>
/// <param name="FromThisAccount">Written by this account in another client, and copied here.</param>
public sealed record XmppMessage(
    XmppAddress From, XmppAddress To, string Type, string Body, string Subject, string Id,
    DateTimeOffset? Sent, string ServerId, bool FromThisAccount)
{
    /// <summary>The sender corrected their last message: this replaces it rather than following it.</summary>
    public bool IsCorrection { get; init; }

    /// <summary>Which of their messages a correction replaces.</summary>
    public string ReplaceId { get; init; } = "";

    /// <summary>Came out of the server's archive, under this id, rather than being delivered.</summary>
    public string ArchiveId { get; init; } = "";

    /// <summary>Its date has already been judged, on arrival: it was held back until a room's catch-up finished.</summary>
    public bool DateJudged { get; init; }

    /// <summary>The room's own id for it, from the room itself. What a moderator's removal names.</summary>
    public string RoomStanzaId { get; init; } = "";

    /// <summary>Not a message but a delete of one: the id it names. Blank for an ordinary message.</summary>
    public string RetractId { get; init; } = "";

    /// <summary>The delete is a room moderator's, not the sender's own.</summary>
    public bool RetractByModerator { get; init; }

    public string RetractReason { get; init; } = "";
}

/// <summary>How far an archive query got.</summary>
/// <param name="Complete">The archive had nothing more to give.</param>
/// <param name="Newest">The date of the last message it gave, if any.</param>
public sealed record XmppArchiveResult(bool Complete, DateTimeOffset? Newest);

/// <summary>Where this server takes files, and the largest it says it will.</summary>
public sealed record XmppUploadService(string Jid, long MaxBytes);

/// <summary>Where to put a file, what to send with it, and the address others fetch it from.</summary>
public sealed record XmppUploadSlot(string PutUrl, string GetUrl, IReadOnlyList<KeyValuePair<string, string>> Headers);

public sealed record XmppRosterItem(string Jid, string Name, string Subscription, bool AskedByUs, IReadOnlyList<string> Groups);

public sealed record XmppPresence(XmppAddress From, XmppShow Show, string Status);

/// <summary>An invitation into a room.</summary>
public sealed record XmppInvite(string Room, string From, string Reason, string Password);

/// <summary>Someone in a room, as of their latest presence there.</summary>
/// <param name="Self">The server says this presence is about this client's own nickname.</param>
public sealed record XmppOccupant(
    string Room, string Nick, XmppShow Show, string Role, string Affiliation, string RealJid, bool Self, bool Left, string Status);

public enum XmppRoomFailure { NickInUse, PasswordRequired, Banned, NotAMember, NotFound, Other }

/// <summary>A room on a conference service, as found by browsing it.</summary>
public sealed record XmppRoomInfo(string Jid, string Name);

/// <summary>What a room says about itself when asked. Each piece is there only if the server chose to say it.</summary>
public sealed record XmppRoomDetails(string Jid, string Name, string Description, string Subject, int? People,
                                     bool NeedsPassword, bool MembersOnly, bool Moderated, bool Temporary);

/// <summary>A room the account has saved on its server.</summary>
public sealed record XmppBookmark(string Jid, string Name, string Nick, bool AutoJoin, string Password);

/// <summary>
/// A logged-in Jabber session: messages, contacts, presence and rooms, over one <see cref="XmppConnection"/>.
///
/// <para>Runs until the connection ends, then is done: a new one is made to reconnect. Keeping no state
/// across connections means a reconnect cannot act on anything the server has forgotten.</para>
///
/// <para>⚠️ Events fire on the reading thread, in the order the server sent things. Anything slow done
/// inside one holds up everything behind it, including the keep-alive's answers.</para>
/// </summary>
public sealed partial class XmppClient(XmppConnection connection) : IAsyncDisposable
{
    public static readonly XNamespace Client    = XmppConnection.Client;
    public static readonly XNamespace Roster    = "jabber:iq:roster";
    public static readonly XNamespace Ping      = "urn:xmpp:ping";
    public static readonly XNamespace DiscoInfo = "http://jabber.org/protocol/disco#info";
    public static readonly XNamespace DiscoItems = "http://jabber.org/protocol/disco#items";
    public static readonly XNamespace Muc       = "http://jabber.org/protocol/muc";
    public static readonly XNamespace MucUser   = "http://jabber.org/protocol/muc#user";
    public static readonly XNamespace MucOwner  = "http://jabber.org/protocol/muc#owner";
    public static readonly XNamespace DataForm  = "jabber:x:data";
    public static readonly XNamespace Carbons   = "urn:xmpp:carbons:2";
    public static readonly XNamespace Forward   = "urn:xmpp:forward:0";
    public static readonly XNamespace ChatStates = "http://jabber.org/protocol/chatstates";
    public static readonly XNamespace Mam       = "urn:xmpp:mam:2";
    public static readonly XNamespace Rsm       = "http://jabber.org/protocol/rsm";
    public static readonly XNamespace Upload    = "urn:xmpp:http:upload:0";
    public static readonly XNamespace Oob       = "jabber:x:oob";
    public static readonly XNamespace Hints     = "urn:xmpp:hints";
    public static readonly XNamespace Delay     = "urn:xmpp:delay";
    public static readonly XNamespace OldDelay  = "jabber:x:delay";
    public static readonly XNamespace StanzaId  = "urn:xmpp:sid:0";
    public static readonly XNamespace Private   = "jabber:iq:private";
    public static readonly XNamespace Bookmarks = "storage:bookmarks";
    public static readonly XNamespace Bookmarks2 = "urn:xmpp:bookmarks:1";
    public static readonly XNamespace PubSub    = "http://jabber.org/protocol/pubsub";
    public static readonly XNamespace Version   = "jabber:iq:version";
    public static readonly XNamespace Stanzas   = "urn:ietf:params:xml:ns:xmpp-stanzas";
    public static readonly XNamespace Receipts  = "urn:xmpp:receipts";
    public static readonly XNamespace Correct   = "urn:xmpp:message-correct:0";
    public static readonly XNamespace DirectInvite = "jabber:x:conference";
    public static readonly XNamespace Retract   = "urn:xmpp:message-retract:1";
    public static readonly XNamespace OldRetract = "urn:xmpp:message-retract:0";
    public static readonly XNamespace Moderate  = "urn:xmpp:message-moderate:1";
    public static readonly XNamespace OldModerate = "urn:xmpp:message-moderate:0";
    public static readonly XNamespace Fasten    = "urn:xmpp:fasten:0";

    private static readonly TimeSpan IqTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (string? To, TaskCompletionSource<XElement> Answer)> _pending = new();
    private CancellationTokenSource? _run;

    public XmppAddress Self => connection.Bound;

    public event Action<XmppMessage>? MessageReceived;

    /// <summary>What someone's client says about them: true while they are typing.</summary>
    public event Action<XmppAddress, bool>? TypingChanged;
    public event Action<XmppPresence>? PresenceChanged;
    public event Action<XmppOccupant>? OccupantChanged;
    public event Action<XmppRosterItem>? RosterChanged;

    /// <summary>Someone asked to see this account's presence.</summary>
    public event Action<string>? SubscriptionRequested;

    /// <summary>A join failed: the room, why, and the room's own words if any.</summary>
    public event Action<string, XmppRoomFailure, string>? RoomJoinFailed;

    public event Action<string, string>? RoomSubjectChanged;

    public event Action<XmppInvite>? InviteReceived;

    /// <summary>
    /// Reads until the connection ends. Returns normally when cancelled or closed by the server, and
    /// throws <see cref="XmppException"/> when the server ended it for a reason someone should see.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _run = run;

        var pinger = PingLoopAsync(run.Token);
        try
        {
            while (!run.Token.IsCancellationRequested)
            {
                var stanza = await connection.ReadStanzaAsync(run.Token);
                if (stanza is null) break;
                Dispatch(stanza);
            }
        }
        finally
        {
            await run.CancelAsync();
            foreach (var (_, waiting) in _pending) waiting.Answer.TrySetCanceled(CancellationToken.None);
            _pending.Clear();
            await Task.WhenAny(pinger);
        }
    }

    /// <summary>
    /// Proves the link both ways once a minute. ⚠️ A write alone can succeed into a dead connection for
    /// a long time, so an unanswered ping is what notices, and it closes the session to force a reconnect.
    /// </summary>
    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PingEvery, ct);

                // ⚠️ Inside the loop: refusing a ping is still an answer, and the checking has to carry on after one.
                try { await QueryAsync("get", Self.Domain, new XElement(Ping + "ping"), ct); }
                catch (XmppException e) when (e.Failure == XmppFailure.Protocol) { /* it answered */ }
            }
        }
        catch (OperationCanceledException) { /* the session is ending */ }
        catch (Exception e) when (e is XmppException or TimeoutException)
        {
            await connection.DisposeAsync();
        }
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    private void Dispatch(XElement stanza)
    {
        if (stanza.Name == XmppConnection.Streams + "error") throw XmppConnection.StreamError(stanza);

        if (stanza.Name == Client + "iq") HandleIq(stanza);
        else if (stanza.Name == Client + "message") HandleMessage(stanza);
        else if (stanza.Name == Client + "presence") HandlePresence(stanza);
    }

    private void HandleIq(XElement iq)
    {
        var type = (string?)iq.Attribute("type");
        var id   = (string?)iq.Attribute("id") ?? "";

        if (type is "result" or "error")
        {
            // ⚠️ Only from who was asked. Anyone who can see this account's address can send it stanzas, and an answer
            // taken from them would be believed: bookmarks that join rooms, a contact list, somewhere to upload files.
            if (_pending.TryGetValue(id, out var waiting) && AnswerIsFrom(waiting.To, (string?)iq.Attribute("from"), Self)
                && _pending.TryRemove(id, out _))
                waiting.Answer.TrySetResult(iq);
            return;
        }

        // Only our own server may tell us who is on our contact list. Anyone else saying so is ignored.
        var from = (string?)iq.Attribute("from");
        if (iq.Element(Roster + "query") is { } push && type == "set"
            && (from is null || from == Self.Bare || from == Self.Domain))
        {
            foreach (var item in push.Elements(Roster + "item")) RosterChanged?.Invoke(ParseRosterItem(item));
            Reply(iq);
            return;
        }

        if (iq.Element(Ping + "ping") is not null) { Reply(iq); return; }

        if (iq.Element(DiscoInfo + "query") is not null && type == "get")
        {
            Reply(iq, new XElement(DiscoInfo + "query",
                new XElement(DiscoInfo + "identity", new XAttribute("category", "client"), new XAttribute("type", "pc"), new XAttribute("name", "EVE Console")),
                new XElement(DiscoInfo + "feature", new XAttribute("var", DiscoInfo.NamespaceName)),
                new XElement(DiscoInfo + "feature", new XAttribute("var", Ping.NamespaceName)),
                new XElement(DiscoInfo + "feature", new XAttribute("var", Carbons.NamespaceName)),
                new XElement(DiscoInfo + "feature", new XAttribute("var", Receipts.NamespaceName)),
                new XElement(DiscoInfo + "feature", new XAttribute("var", Correct.NamespaceName)),
                new XElement(DiscoInfo + "feature", new XAttribute("var", DirectInvite.NamespaceName))));
            return;
        }

        if (iq.Element(Version + "query") is not null && type == "get")
        {
            Reply(iq, new XElement(Version + "query", new XElement(Version + "name", "EVE Console")));
            return;
        }

        // Anything else asked of a client gets the polite no the protocol requires.
        if (type is "get" or "set") Send(new XElement(Client + "iq",
            new XAttribute("type", "error"), new XAttribute("id", id), from is null ? null : new XAttribute("to", from),
            new XElement(Client + "error", new XAttribute("type", "cancel"), new XElement(Stanzas + "service-unavailable"))));
    }

    private void Reply(XElement iq, XElement? payload = null)
    {
        var from = (string?)iq.Attribute("from");
        Send(new XElement(Client + "iq",
            new XAttribute("type", "result"), new XAttribute("id", (string?)iq.Attribute("id") ?? ""),
            from is null ? null : new XAttribute("to", from), payload));
    }

    private void HandleMessage(XElement stanza)
    {
        if (stanza.Element(Mam + "result") is not null)
        {
            var archived = _archiveRoom is { } room
                ? ParseRoomArchived(stanza, room, _archiveQuery)
                : ParseArchived(stanza, Self, _archiveQuery);

            if (archived is not null)
            {
                _archiveNewest = archived.Sent ?? _archiveNewest;
                MessageReceived?.Invoke(archived);
            }
            return;
        }

        if (ParseInvite(stanza) is { } invite)
        {
            InviteReceived?.Invoke(invite);
            return;
        }

        var message = ParseMessage(stanza, Self);
        if (message is null) return;

        // A sender asking to hear it arrived gets told, unless this is only a copy of someone's own.
        if (stanza.Element(Receipts + "request") is not null && !message.FromThisAccount
            && message.Type != "groupchat" && message.Id.Length > 0 && stanza.Attribute("from") is { } sender)
            Send(new XElement(Client + "message", new XAttribute("to", sender.Value),
                new XElement(Receipts + "received", new XAttribute("id", message.Id))));

        var type = (string?)stanza.Attribute("type") ?? "normal";

        // ⚠️ Live ones only. A carbon's sits inside what it carries, so it never reads here, and a stored one
        // is about a moment long gone.
        if (type != "groupchat" && !message.FromThisAccount && message.Sent is null
            && stanza.Elements().FirstOrDefault(e => e.Name.Namespace == ChatStates) is { } state)
            TypingChanged?.Invoke(message.From, state.Name.LocalName == "composing");

        if (type == "groupchat" && stanza.Element(Client + "subject") is { } subject && message.Body.Length == 0)
        {
            RoomSubjectChanged?.Invoke(message.From.Bare, subject.Value);
            return;
        }

        // A delete usually carries words for clients that cannot do one, but need not.
        if (message.Body.Length > 0 || message.RetractId.Length > 0) MessageReceived?.Invoke(message);
    }

    /// <summary>
    /// What a delete names, if this is one: the sender's own (XEP-0424), or a room moderator's (XEP-0425), in the current
    /// form or the older one still in use, which wrapped it in apply-to. Who may delete what is decided where it is saved.
    /// </summary>
    public static (string Id, bool Moderated, string Reason)? RetractionIn(XElement inner)
    {
        if (inner.Element(Retract + "retract") is { } retract && (string?)retract.Attribute("id") is { Length: > 0 } id)
            return (id, retract.Element(Moderate + "moderated") is not null,
                    retract.Element(Retract + "reason")?.Value ?? retract.Element(Moderate + "reason")?.Value ?? "");

        if (inner.Element(Fasten + "apply-to") is { } apply && (string?)apply.Attribute("id") is { Length: > 0 } old)
        {
            if (apply.Element(OldModerate + "moderated") is { } moderated && moderated.Element(OldRetract + "retract") is not null)
                return (old, true, moderated.Element(OldModerate + "reason")?.Value ?? "");
            if (apply.Element(OldRetract + "retract") is not null) return (old, false, "");
        }
        return null;
    }

    private static XmppMessage WithRetraction(XmppMessage message, XElement inner) =>
        RetractionIn(inner) is (string id, bool moderated, string reason)
            ? message with { RetractId = id, RetractByModerator = moderated, RetractReason = reason }
            : message;

    /// <summary>The id a room gave a message, only when the room itself says so: anyone in it could put one in.</summary>
    private static string RoomStanzaIdIn(XElement inner, string room) =>
        (string?)inner.Elements(StanzaId + "stanza-id")
            .FirstOrDefault(e => XmppAddress.Parse((string?)e.Attribute("by")).Bare == room)?.Attribute("id") ?? "";

    /// <summary>An invitation, in either of the forms servers and clients send, or null.</summary>
    public static XmppInvite? ParseInvite(XElement stanza)
    {
        if ((string?)stanza.Attribute("type") == "error") return null;

        // Through the room, which says who sent it.
        if (stanza.Element(MucUser + "x") is { } user && user.Element(MucUser + "invite") is { } through)
            return new XmppInvite(
                XmppAddress.Parse((string?)stanza.Attribute("from")).Bare,
                XmppAddress.Parse((string?)through.Attribute("from")).Bare,
                through.Element(MucUser + "reason")?.Value ?? "",
                user.Element(MucUser + "password")?.Value ?? "");

        // Directly from the person.
        if (stanza.Element(DirectInvite + "x") is { } direct && (string?)direct.Attribute("jid") is { Length: > 0 } room)
            return new XmppInvite(
                XmppAddress.Parse(room).Bare,
                XmppAddress.Parse((string?)stanza.Attribute("from")).Bare,
                (string?)direct.Attribute("reason") ?? "",
                (string?)direct.Attribute("password") ?? "");

        return null;
    }

    /// <summary>
    /// A message stanza as what it means, with a carbon copy unwrapped. Null for an error or for one
    /// that claims to be a copy from anyone but this account's own server.
    /// </summary>
    public static XmppMessage? ParseMessage(XElement stanza, XmppAddress self)
    {
        if ((string?)stanza.Attribute("type") == "error") return null;

        var outerFrom = XmppAddress.Parse((string?)stanza.Attribute("from"));
        var wrapper   = stanza.Element(Carbons + "received") ?? stanza.Element(Carbons + "sent");
        var inner     = stanza;
        var mine      = false;

        if (wrapper is not null)
        {
            // ⚠️ Only from this account itself. Anyone can send a stanza dressed as a carbon, and taken at
            // face value it would put words in another person's mouth, or in this account's.
            if (outerFrom.Bare != self.Bare) return null;

            inner = wrapper.Element(Forward + "forwarded")?.Element(Client + "message");
            if (inner is null) return null;

            mine = wrapper.Name == Carbons + "sent";
        }

        var delay = stanza.Element(Delay + "delay") ?? inner.Element(Delay + "delay")
                 ?? wrapper?.Element(Forward + "forwarded")?.Element(Delay + "delay");
        DateTimeOffset? sent = null;
        if (delay is not null && DateTimeOffset.TryParse((string?)delay.Attribute("stamp"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp)) sent = stamp;
        else if ((inner.Element(OldDelay + "x") ?? stanza.Element(OldDelay + "x")) is { } old
                 && DateTimeOffset.TryParseExact((string?)old.Attribute("stamp"), "yyyyMMdd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var oldStamp)) sent = oldStamp;

        var from = XmppAddress.Parse((string?)inner.Attribute("from"));
        var type = (string?)inner.Attribute("type") ?? "normal";

        return WithRetraction(new XmppMessage(
            from,
            XmppAddress.Parse((string?)inner.Attribute("to")),
            type,
            inner.Element(Client + "body")?.Value ?? "",
            inner.Element(Client + "subject")?.Value ?? "",
            (string?)inner.Attribute("id") ?? "",
            sent,
            (string?)inner.Element(StanzaId + "stanza-id")?.Attribute("id") ?? "",
            mine)
        {
            IsCorrection = inner.Element(Correct + "replace") is not null,
            ReplaceId = (string?)inner.Element(Correct + "replace")?.Attribute("id") ?? "",
            RoomStanzaId = type == "groupchat" ? RoomStanzaIdIn(inner, from.Bare) : "",
        }, inner);
    }

    private void HandlePresence(XElement stanza)
    {
        var from = XmppAddress.Parse((string?)stanza.Attribute("from"));
        var type = (string?)stanza.Attribute("type") ?? "";

        if (type == "subscribe") { SubscriptionRequested?.Invoke(from.Bare); return; }
        if (type is "subscribed" or "unsubscribe" or "unsubscribed" or "probe") return;

        var user = stanza.Element(MucUser + "x");

        if (type == "error")
        {
            if (stanza.Element(Muc + "x") is not null || user is not null || from.Resource.Length > 0)
            {
                var error = stanza.Element(Client + "error");
                RoomJoinFailed?.Invoke(from.Bare, ParseRoomFailure(error),
                    error?.Element(Stanzas + "text")?.Value ?? "");
            }
            return;
        }

        var show   = type == "unavailable" ? XmppShow.Offline : ParseShow(stanza.Element(Client + "show")?.Value);
        var status = stanza.Element(Client + "status")?.Value ?? "";

        if (user is not null)
        {
            var item = user.Element(MucUser + "item");
            var codes = user.Elements(MucUser + "status").Select(s => (string?)s.Attribute("code")).ToHashSet();

            // ⚠️ A room this join just created stays locked to everyone else until its owner says how
            // it should be set up. An empty form takes the server's defaults, as an "instant" room.
            if (codes.Contains("110") && codes.Contains("201"))
                Send(new XElement(Client + "iq", new XAttribute("type", "set"), new XAttribute("to", from.Bare),
                    new XAttribute("id", NewId()),
                    new XElement(MucOwner + "query", new XElement(DataForm + "x", new XAttribute("type", "submit")))));

            OccupantChanged?.Invoke(new XmppOccupant(
                from.Bare, from.Resource, show,
                (string?)item?.Attribute("role") ?? "", (string?)item?.Attribute("affiliation") ?? "",
                XmppAddress.Parse((string?)item?.Attribute("jid")).Bare,
                codes.Contains("110"), type == "unavailable", status));
            return;
        }

        PresenceChanged?.Invoke(new XmppPresence(from, show, status));
    }

    public static XmppShow ParseShow(string? show) => show?.Trim() switch
    {
        "chat" => XmppShow.Chat,
        "away" => XmppShow.Away,
        "xa"   => XmppShow.ExtendedAway,
        "dnd"  => XmppShow.DoNotDisturb,
        _      => XmppShow.Online,
    };

    private static XmppRoomFailure ParseRoomFailure(XElement? error)
    {
        if (error is null) return XmppRoomFailure.Other;
        if (error.Element(Stanzas + "conflict") is not null) return XmppRoomFailure.NickInUse;
        if (error.Element(Stanzas + "not-authorized") is not null) return XmppRoomFailure.PasswordRequired;
        if (error.Element(Stanzas + "forbidden") is not null) return XmppRoomFailure.Banned;
        if (error.Element(Stanzas + "registration-required") is not null) return XmppRoomFailure.NotAMember;
        if (error.Element(Stanzas + "item-not-found") is not null) return XmppRoomFailure.NotFound;
        return XmppRoomFailure.Other;
    }

    private static XmppRosterItem ParseRosterItem(XElement item) => new(
        XmppAddress.Parse((string?)item.Attribute("jid")).Bare,
        (string?)item.Attribute("name") ?? "",
        (string?)item.Attribute("subscription") ?? "none",
        (string?)item.Attribute("ask") == "subscribe",
        [.. item.Elements(Roster + "group").Select(g => g.Value)]);

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>Fire and forget, for answers the reading thread gives. A failure ends the read loop anyway.</summary>
    private void Send(XElement stanza) =>
        _ = connection.WriteAsync(stanza, _run?.Token ?? CancellationToken.None)
                      .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>Asks and waits for the answer. An error answer comes back as <see cref="XmppFailure.Protocol"/>.</summary>
    public async Task<XElement> QueryAsync(string type, string? to, XElement payload, CancellationToken ct)
    {
        var id     = NewId();
        var answer = new TaskCompletionSource<XElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (to, answer);

        try
        {
            await connection.WriteAsync(new XElement(Client + "iq",
                new XAttribute("type", type), new XAttribute("id", id),
                to is null ? null : new XAttribute("to", to), payload), ct);

            var result = await answer.Task.WaitAsync(IqTimeout, ct);

            if ((string?)result.Attribute("type") == "error")
            {
                var why = result.Element(Client + "error")?.Elements()
                                .FirstOrDefault(e => e.Name.Namespace == Stanzas && e.Name.LocalName != "text")?.Name.LocalName;
                throw new XmppException(XmppFailure.Protocol, $"The server refused: {why ?? "unknown"}.");
            }
            return result;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Unguessable as well as unique: what answers a question is matched on this.</summary>
    private static string NewId() => "ec" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Whether a reply came from who was asked. Asking nobody in particular means this account's own server, which
    /// may leave out who it is, or give the account's address or its own.
    /// </summary>
    public static bool AnswerIsFrom(string? asked, string? from, XmppAddress self)
    {
        var who = from is null ? (XmppAddress?)null : XmppAddress.Parse(from);

        if (asked is null) return who is null || who.Value.Bare == self.Bare || who.Value.Bare == self.Domain;

        var target = XmppAddress.Parse(asked);
        if (who is null) return target.Bare == self.Bare || target.Bare == self.Domain;

        return who.Value.Full == target.Full;
    }

    /// <summary>What a person or a service says it can do. Empty when it does not answer.</summary>
    public async Task<HashSet<string>> FeaturesOfAsync(string to, CancellationToken ct)
    {
        try
        {
            var answer = await QueryAsync("get", to, new XElement(DiscoInfo + "query"), ct);
            return answer.Element(DiscoInfo + "query")?.Elements(DiscoInfo + "feature")
                         .Select(f => (string?)f.Attribute("var") ?? "").ToHashSet() ?? [];
        }
        catch (Exception e) when (e is XmppException { Failure: XmppFailure.Protocol } or TimeoutException) { return []; }
    }

    /// <summary>Asks a room about itself. The details are its identity, its features, and a form of whatever else it offers.</summary>
    public async Task<XmppRoomDetails> RoomDetailsAsync(string room, CancellationToken ct)
    {
        var answer = await QueryAsync("get", room, new XElement(DiscoInfo + "query"), ct);
        return ParseRoomDetails(room, answer.Element(DiscoInfo + "query"));
    }

    public static XmppRoomDetails ParseRoomDetails(string room, XElement? query)
    {
        var features = query?.Elements(DiscoInfo + "feature").Select(f => (string?)f.Attribute("var") ?? "").ToHashSet() ?? [];
        var fields = query?.Elements(DataForm + "x").Elements(DataForm + "field")
                          .GroupBy(f => (string?)f.Attribute("var") ?? "")
                          .ToDictionary(g => g.Key, g => (string?)g.First().Element(DataForm + "value") ?? "") ?? [];

        return new XmppRoomDetails(
            room,
            (string?)query?.Element(DiscoInfo + "identity")?.Attribute("name") ?? "",
            fields.GetValueOrDefault("muc#roominfo_description", ""),
            fields.GetValueOrDefault("muc#roominfo_subject", ""),
            int.TryParse(fields.GetValueOrDefault("muc#roominfo_occupants"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var people) ? people : null,
            features.Contains("muc_passwordprotected"),
            features.Contains("muc_membersonly"),
            features.Contains("muc_moderated"),
            features.Contains("muc_temporary"));
    }

    public async Task<IReadOnlyList<XmppRoomInfo>> ItemsOfAsync(string to, CancellationToken ct)
    {
        var answer = await QueryAsync("get", to, new XElement(DiscoItems + "query"), ct);
        return [.. (answer.Element(DiscoItems + "query")?.Elements(DiscoItems + "item") ?? [])
            .Select(i => new XmppRoomInfo((string?)i.Attribute("jid") ?? "", (string?)i.Attribute("name") ?? ""))
            .Where(i => i.Jid.Length > 0)];
    }

    /// <summary>
    /// The server's group-chat services, found by asking rather than assumed: a group's conference
    /// service has its own name, which no one should have to type or know.
    /// </summary>
    public async Task<IReadOnlyList<string>> ConferenceServicesAsync(CancellationToken ct)
    {
        var found = new List<string>();
        foreach (var item in await ItemsOfAsync(Self.Domain, ct))
            if ((await FeaturesOfAsync(item.Jid, ct)).Contains(Muc.NamespaceName)) found.Add(item.Jid);
        return found;
    }

    public async Task<IReadOnlyList<XmppRosterItem>> GetRosterAsync(CancellationToken ct)
    {
        var answer = await QueryAsync("get", null, new XElement(Roster + "query"), ct);
        return [.. (answer.Element(Roster + "query")?.Elements(Roster + "item") ?? []).Select(ParseRosterItem)];
    }

    public Task AddContactAsync(string jid, string name, CancellationToken ct) =>
        Task.WhenAll(
            QueryAsync("set", null, new XElement(Roster + "query", new XElement(Roster + "item",
                new XAttribute("jid", jid), name.Length > 0 ? new XAttribute("name", name) : null)), ct),
            connection.WriteAsync(new XElement(Client + "presence", new XAttribute("to", jid), new XAttribute("type", "subscribe")), ct));

    public Task RemoveContactAsync(string jid, CancellationToken ct) =>
        QueryAsync("set", null, new XElement(Roster + "query", new XElement(Roster + "item",
            new XAttribute("jid", jid), new XAttribute("subscription", "remove"))), ct);

    /// <summary>Answers someone's request to see this account's presence. A yes also asks for theirs.</summary>
    public async Task AnswerSubscriptionAsync(string jid, bool accept, CancellationToken ct)
    {
        await connection.WriteAsync(new XElement(Client + "presence", new XAttribute("to", jid),
            new XAttribute("type", accept ? "subscribed" : "unsubscribed")), ct);
        if (accept)
            await connection.WriteAsync(new XElement(Client + "presence", new XAttribute("to", jid), new XAttribute("type", "subscribe")), ct);
    }

    /// <summary>Tells contacts this client is here. Stored messages from the server arrive after this.</summary>
    private XmppShow _show = XmppShow.Online;
    private string _status = "";

    /// <summary>Tells everyone subscribed how this account stands. Rooms are not among them: see <see cref="SetRoomPresenceAsync"/>.</summary>
    public Task SetPresenceAsync(XmppShow show, string status, CancellationToken ct)
    {
        _show = show;
        _status = status;
        return connection.WriteAsync(WithPresence(new XElement(Client + "presence")), ct);
    }

    /// <summary>The same for one room this account is in, as a server does not pass a status on to rooms by itself.</summary>
    public Task SetRoomPresenceAsync(string room, string nick, CancellationToken ct) =>
        connection.WriteAsync(WithPresence(new XElement(Client + "presence", new XAttribute("to", $"{room}/{nick}"))), ct);

    private XElement WithPresence(XElement presence)
    {
        var (show, status) = (_show, _status);
        var word = show switch
        {
            XmppShow.Chat         => "chat",
            XmppShow.Away         => "away",
            XmppShow.ExtendedAway => "xa",
            XmppShow.DoNotDisturb => "dnd",
            _                     => null,
        };
        if (word is not null) presence.Add(new XElement(Client + "show", word));
        if (status.Length > 0) presence.Add(new XElement(Client + "status", Clean(status)));
        return presence;
    }

    /// <summary>Copies of what this account writes and receives in its other clients, if the server can.</summary>
    public async Task EnableCarbonsAsync(CancellationToken ct)
    {
        if ((await FeaturesOfAsync(Self.Domain, ct)).Contains(Carbons.NamespaceName))
            await QueryAsync("set", null, new XElement(Carbons + "enable"), ct);
    }

    /// <summary>Sends a direct message, or a room message for type "groupchat". Returns its id.</summary>
    public async Task<string> SendMessageAsync(string to, string type, string body, CancellationToken ct, string? fileUrl = null)
    {
        // ⚠️ Unique for good, not for this connection: a counter starts again on the next one, and the same id
        // to the same person reads as a repeat of what was already saved, so the new message would vanish.
        var id = "ec" + Guid.NewGuid().ToString("N");
        var message = new XElement(Client + "message",
            new XAttribute("to", to), new XAttribute("type", type), new XAttribute("id", id),
            new XElement(Client + "body", Clean(body)));

        // Says this client understands typing notices, so one that does can start sending them.
        if (type == "chat") message.Add(new XElement(ChatStates + "active"));

        // Tells a client that shows pictures that the words are a file's address, not something someone wrote.
        if (fileUrl is not null) message.Add(new XElement(Oob + "x", new XElement(Oob + "url", fileUrl)));

        await connection.WriteAsync(message, ct);
        return id;
    }

    // ── The server's archive ──────────────────────────────────────────────────

    private volatile string? _archiveQuery;
    private volatile string? _archiveRoom;
    private DateTimeOffset? _archiveNewest;

    // One at a time: an answer is matched to the one question being asked.
    private readonly SemaphoreSlim _archiveTurn = new(1, 1);

    /// <summary>
    /// One message out of this account's own archive, or null for anything else.
    ///
    /// <para>⚠️ Only from this account's own server and only in answer to a question asked now. Anyone can send a
    /// stanza dressed as an archive result, and taken at face value it would put words in anyone's mouth, dated
    /// whenever the forger likes.</para>
    /// </summary>
    public static XmppMessage? ParseArchived(XElement stanza, XmppAddress self, string? expectedQuery)
    {
        var result = stanza.Element(Mam + "result");
        if (result is null || expectedQuery is null || (string?)result.Attribute("queryid") != expectedQuery) return null;

        if ((string?)stanza.Attribute("from") is { } from && XmppAddress.Parse(from).Bare != self.Bare) return null;

        var forwarded = result.Element(Forward + "forwarded");
        var inner     = forwarded?.Element(Client + "message");
        if (forwarded is null || inner is null) return null;

        // A room's messages come from the room's own archive.
        var type = (string?)inner.Attribute("type") ?? "normal";
        if (type is "groupchat" or "error") return null;

        var body = inner.Element(Client + "body")?.Value ?? "";
        if (body.Length == 0 && RetractionIn(inner) is null) return null;

        var sender = XmppAddress.Parse((string?)inner.Attribute("from"));

        return ArchivedMessage(result, forwarded, inner, sender, type, body, sender.Bare == self.Bare);
    }

    private static XmppMessage ArchivedMessage(XElement result, XElement forwarded, XElement inner, XmppAddress sender, string type, string body, bool mine)
    {
        DateTimeOffset? sent = null;
        if (DateTimeOffset.TryParse((string?)forwarded.Element(Delay + "delay")?.Attribute("stamp"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp)) sent = stamp;

        return WithRetraction(new XmppMessage(
            sender,
            XmppAddress.Parse((string?)inner.Attribute("to")),
            type, body,
            inner.Element(Client + "subject")?.Value ?? "",
            (string?)inner.Attribute("id") ?? "",
            sent,
            (string?)inner.Element(StanzaId + "stanza-id")?.Attribute("id") ?? "",
            mine)
        {
            ArchiveId = (string?)result.Attribute("id") ?? "",
            IsCorrection = inner.Element(Correct + "replace") is not null,
            ReplaceId = (string?)inner.Element(Correct + "replace")?.Attribute("id") ?? "",

            // A room's archive is the room speaking, and its id for each message is the one its moderators name.
            RoomStanzaId = type == "groupchat" ? (string?)result.Attribute("id") ?? "" : "",
        }, inner);
    }

    /// <summary>
    /// One message out of a room's archive, or null for anything else.
    ///
    /// <para>⚠️ Only from the room itself, never one of the people in it, and only in answer to a question asked now.
    /// Everyone in a room can send from room/nickname, so a stanza from one of those could otherwise put words in
    /// anyone's mouth, dated whenever they like.</para>
    /// </summary>
    public static XmppMessage? ParseRoomArchived(XElement stanza, string room, string? expectedQuery)
    {
        var result = stanza.Element(Mam + "result");
        if (result is null || expectedQuery is null || (string?)result.Attribute("queryid") != expectedQuery) return null;

        var from = XmppAddress.Parse((string?)stanza.Attribute("from"));
        if (from.Bare != room || from.Resource.Length > 0) return null;

        var forwarded = result.Element(Forward + "forwarded");
        var inner     = forwarded?.Element(Client + "message");
        if (forwarded is null || inner is null) return null;

        if ((string?)inner.Attribute("type") != "groupchat") return null;

        var sender = XmppAddress.Parse((string?)inner.Attribute("from"));
        if (sender.Bare != room) return null;

        var body = inner.Element(Client + "body")?.Value ?? "";
        return body.Length == 0 && RetractionIn(inner) is null ? null : ArchivedMessage(result, forwarded, inner, sender, "groupchat", body, mine: false);
    }

    /// <summary>Whether this account's server keeps an archive to ask.</summary>
    public async Task<bool> HasArchiveAsync(CancellationToken ct) =>
        (await FeaturesOfAsync(Self.Bare, ct)).Contains(Mam.NamespaceName);

    /// <summary>
    /// Whether a room says it keeps an archive. ⚠️ Not long to wait: rooms are joined at login, and a room on a far
    /// server that never answers must not hold that up.
    /// </summary>
    public async Task<bool> RoomHasArchiveAsync(string room, CancellationToken ct)
    {
        try { return (await FeaturesOfAsync(room, ct).WaitAsync(TimeSpan.FromSeconds(5), ct)).Contains(Mam.NamespaceName); }
        catch (TimeoutException) { return false; }
    }

    /// <summary>
    /// Asks for everything since a moment, from this account's archive or from a room's. It arrives as ordinary
    /// messages, before this returns.
    /// </summary>
    public async Task<XmppArchiveResult> QueryArchiveAsync(DateTimeOffset since, CancellationToken ct, string? room = null)
    {
        const int PageSize = 100, MostPages = 20;

        await _archiveTurn.WaitAsync(ct);

        var query = "ec" + Guid.NewGuid().ToString("N");
        _archiveNewest = null;
        _archiveRoom = room;
        _archiveQuery = query;
        try
        {
            string? after = null;
            for (var page = 0; page < MostPages; page++)
            {
                var answer = await QueryAsync("set", room, new XElement(Mam + "query",
                    new XAttribute("queryid", query),
                    new XElement(DataForm + "x", new XAttribute("type", "submit"),
                        new XElement(DataForm + "field", new XAttribute("var", "FORM_TYPE"), new XAttribute("type", "hidden"),
                            new XElement(DataForm + "value", Mam.NamespaceName)),
                        new XElement(DataForm + "field", new XAttribute("var", "start"),
                            new XElement(DataForm + "value", since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)))),
                    new XElement(Rsm + "set",
                        new XElement(Rsm + "max", PageSize),
                        after is null ? null : new XElement(Rsm + "after", after))), ct);

                var fin = answer.Element(Mam + "fin");
                if (fin is null || (string?)fin.Attribute("complete") == "true") return new XmppArchiveResult(true, _archiveNewest);

                after = fin.Element(Rsm + "set")?.Element(Rsm + "last")?.Value;
                if (string.IsNullOrEmpty(after)) return new XmppArchiveResult(true, _archiveNewest);
            }
            return new XmppArchiveResult(false, _archiveNewest);
        }
        finally
        {
            _archiveQuery = null;
            _archiveRoom = null;
            _archiveTurn.Release();
        }
    }

    // ── Files ─────────────────────────────────────────────────────────────────

    /// <summary>What a service's own description says about taking files, or null if it does not.</summary>
    public static XmppUploadService? ParseUploadService(string jid, XElement info)
    {
        var query = info.Element(DiscoInfo + "query");
        if (query is null || !query.Elements(DiscoInfo + "feature").Any(f => (string?)f.Attribute("var") == Upload.NamespaceName)) return null;

        long most = 0;
        foreach (var form in query.Elements(DataForm + "x"))
        {
            var fields = form.Elements(DataForm + "field").ToList();
            if (fields.All(f => f.Element(DataForm + "value")?.Value != Upload.NamespaceName)) continue;

            var size = fields.FirstOrDefault(f => (string?)f.Attribute("var") == "max-file-size")?.Element(DataForm + "value")?.Value;
            if (long.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0) most = parsed;
        }
        return new XmppUploadService(jid, most);
    }

    /// <summary>The first service on this server that takes files, or null.</summary>
    public async Task<XmppUploadService?> FindUploadServiceAsync(CancellationToken ct)
    {
        foreach (var item in await ItemsOfAsync(Self.Domain, ct))
        {
            try
            {
                var info = await QueryAsync("get", item.Jid, new XElement(DiscoInfo + "query"), ct);
                if (ParseUploadService(item.Jid, info) is { } service) return service;
            }
            catch (Exception e) when (e is XmppException { Failure: XmppFailure.Protocol } or TimeoutException) { /* a service with nothing to say about itself */ }
        }
        return null;
    }

    /// <summary>Where to put a file, from the answer to asking for somewhere. Null if the answer does not say.</summary>
    public static XmppUploadSlot? ParseUploadSlot(XElement answer)
    {
        var slot = answer.Element(Upload + "slot");
        var put  = slot?.Element(Upload + "put");
        var get  = (string?)slot?.Element(Upload + "get")?.Attribute("url");
        var url  = (string?)put?.Attribute("url");
        if (put is null || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(get)) return null;

        // ⚠️ Only the three the protocol allows: anything else is a server telling this client what to send.
        string[] allowed = ["Authorization", "Cookie", "Expires"];
        var headers = put.Elements(Upload + "header")
            .Select(h => new KeyValuePair<string, string>((string?)h.Attribute("name") ?? "", h.Value))
            .Where(h => allowed.Contains(h.Key, StringComparer.OrdinalIgnoreCase) && !h.Value.Contains('\n') && !h.Value.Contains('\r'))
            .ToList();

        return new XmppUploadSlot(url, get, headers);
    }

    public async Task<XmppUploadSlot> RequestUploadSlotAsync(string service, string fileName, long size, string contentType, CancellationToken ct)
    {
        var answer = await QueryAsync("get", service, new XElement(Upload + "request",
            new XAttribute("filename", fileName), new XAttribute("size", size), new XAttribute("content-type", contentType)), ct);

        return ParseUploadSlot(answer) ?? throw new XmppException(XmppFailure.Protocol, "The server gave nowhere to put the file.");
    }

    /// <summary>Tells someone this account has started or stopped typing to them. Never stored for later.</summary>
    public Task SendTypingAsync(string to, bool typing, CancellationToken ct) =>
        connection.WriteAsync(new XElement(Client + "message",
            new XAttribute("to", to), new XAttribute("type", "chat"),
            new XElement(ChatStates + (typing ? "composing" : "paused")),
            new XElement(Hints + "no-store")), ct);

    /// <summary>Enters a room. History since a moment asks only for what has not been seen.</summary>
    public Task JoinRoomAsync(string room, string nick, string password, DateTimeOffset? since, CancellationToken ct)
    {
        var x = new XElement(Muc + "x");
        if (password.Length > 0) x.Add(new XElement(Muc + "password", password));
        x.Add(since is { } s
            ? new XElement(Muc + "history", new XAttribute("since", s.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)))
            : new XElement(Muc + "history", new XAttribute("maxstanzas", "50")));

        // With the status, as a room would otherwise show everyone as plainly online until it next changed.
        return connection.WriteAsync(WithPresence(new XElement(Client + "presence", new XAttribute("to", $"{room}/{nick}"), x)), ct);
    }

    public Task LeaveRoomAsync(string room, string nick, CancellationToken ct) =>
        connection.WriteAsync(new XElement(Client + "presence", new XAttribute("to", $"{room}/{nick}"), new XAttribute("type", "unavailable")), ct);

    /// <summary>The rooms saved on the server. Empty when it keeps none.</summary>
    public async Task<IReadOnlyList<XmppBookmark>> GetBookmarksAsync(CancellationToken ct)
    {
        try
        {
            var answer = await QueryAsync("get", null,
                new XElement(Private + "query", new XElement(Bookmarks + "storage")), ct);

            return [.. (answer.Element(Private + "query")?.Element(Bookmarks + "storage")?.Elements(Bookmarks + "conference") ?? [])
                .Select(c => new XmppBookmark(
                    XmppAddress.Parse((string?)c.Attribute("jid")).Bare,
                    (string?)c.Attribute("name") ?? "",
                    c.Element(Bookmarks + "nick")?.Value ?? "",
                    (string?)c.Attribute("autojoin") is "true" or "1",
                    c.Element(Bookmarks + "password")?.Value ?? ""))
                .Where(b => b.Jid.Length > 0)];
        }
        catch (Exception e) when (e is XmppException { Failure: XmppFailure.Protocol } or TimeoutException) { return []; }
    }

    /// <summary>
    /// The rooms saved the newer way (XEP-0402): one item each in the account's own store, under the room's address.
    /// Empty when the server keeps none this way. Newer clients save only here, older ones only in the private store.
    /// </summary>
    public async Task<IReadOnlyList<XmppBookmark>> GetNewBookmarksAsync(CancellationToken ct)
    {
        try
        {
            var answer = await QueryAsync("get", null,
                new XElement(PubSub + "pubsub", new XElement(PubSub + "items", new XAttribute("node", Bookmarks2.NamespaceName))), ct);
            return ParseNewBookmarks(answer);
        }
        catch (Exception e) when (e is XmppException { Failure: XmppFailure.Protocol } or TimeoutException) { return []; }
    }

    public const string BookmarksInStepFeature = "urn:xmpp:bookmarks:1#compat";

    /// <summary>A room as the newer store keeps it: an item under the room's address. Only what is known is written.</summary>
    public static XElement NewBookmarkItem(XmppBookmark b) =>
        new(PubSub + "item", new XAttribute("id", b.Jid),
            new XElement(Bookmarks2 + "conference",
                b.Name.Length > 0 ? new XAttribute("name", b.Name) : null,
                new XAttribute("autojoin", b.AutoJoin ? "true" : "false"),
                b.Nick.Length > 0 ? new XElement(Bookmarks2 + "nick", b.Nick) : null,
                b.Password.Length > 0 ? new XElement(Bookmarks2 + "password", b.Password) : null));

    /// <summary>
    /// The publish for one room. ⚠️ With the options the protocol asks for, so the list is kept and only this account
    /// can read it: rooms' passwords are in it. A server that will not take options is asked without them.
    /// </summary>
    public static XElement NewBookmarkPublish(XmppBookmark b, bool withOptions)
    {
        var pubsub = new XElement(PubSub + "pubsub",
            new XElement(PubSub + "publish", new XAttribute("node", Bookmarks2.NamespaceName), NewBookmarkItem(b)));
        if (withOptions)
            pubsub.Add(new XElement(PubSub + "publish-options", new XElement(DataForm + "x", new XAttribute("type", "submit"),
                Field("FORM_TYPE", "http://jabber.org/protocol/pubsub#publish-options", hidden: true),
                Field("pubsub#persist_items", "true"),
                Field("pubsub#max_items", "max"),
                Field("pubsub#send_last_published_item", "never"),
                Field("pubsub#access_model", "whitelist"))));
        return pubsub;

        static XElement Field(string name, string value, bool hidden = false) =>
            new(DataForm + "field", new XAttribute("var", name), hidden ? new XAttribute("type", "hidden") : null, new XElement(DataForm + "value", value));
    }

    /// <summary>A room as the older private store keeps it.</summary>
    public static XElement OldBookmarkElement(XmppBookmark b) =>
        new(Bookmarks + "conference", new XAttribute("jid", b.Jid),
            b.Name.Length > 0 ? new XAttribute("name", b.Name) : null,
            new XAttribute("autojoin", b.AutoJoin ? "true" : "false"),
            b.Nick.Length > 0 ? new XElement(Bookmarks + "nick", b.Nick) : null,
            b.Password.Length > 0 ? new XElement(Bookmarks + "password", b.Password) : null);

    /// <summary>
    /// The older store with one room put in or changed. ⚠️ The store is one list written whole, so everything else in it
    /// (other rooms, saved links, what other clients added) is carried over as it was.
    /// </summary>
    public static XElement WithOldBookmark(XElement? storage, XmppBookmark b)
    {
        var copy = storage is null ? new XElement(Bookmarks + "storage") : new XElement(storage);
        copy.Elements(Bookmarks + "conference").Where(c => XmppAddress.Parse((string?)c.Attribute("jid")).Bare == b.Jid).Remove();
        copy.Add(OldBookmarkElement(b));
        return copy;
    }

    public async Task PublishNewBookmarkAsync(XmppBookmark b, CancellationToken ct)
    {
        try { await QueryAsync("set", null, NewBookmarkPublish(b, withOptions: true), ct); }
        catch (XmppException e) when (e.Failure == XmppFailure.Protocol) { await QueryAsync("set", null, NewBookmarkPublish(b, withOptions: false), ct); }
    }

    public async Task SaveOldBookmarkAsync(XmppBookmark b, CancellationToken ct)
    {
        XElement? storage;
        try
        {
            var answer = await QueryAsync("get", null, new XElement(Private + "query", new XElement(Bookmarks + "storage")), ct);
            storage = answer.Element(Private + "query")?.Element(Bookmarks + "storage");
        }
        catch (XmppException e) when (e.Failure == XmppFailure.Protocol) { storage = null; }

        await QueryAsync("set", null, new XElement(Private + "query", WithOldBookmark(storage, b)), ct);
    }

    public static List<XmppBookmark> ParseNewBookmarks(XElement answer) =>
        [.. (answer.Element(PubSub + "pubsub")?.Element(PubSub + "items")?.Elements(PubSub + "item") ?? [])
            .Select(item => (Jid: XmppAddress.Parse((string?)item.Attribute("id")).Bare, Room: item.Element(Bookmarks2 + "conference")))
            .Where(x => x.Jid.Length > 0 && x.Room is not null)
            .Select(x => new XmppBookmark(
                x.Jid,
                (string?)x.Room!.Attribute("name") ?? "",
                x.Room.Element(Bookmarks2 + "nick")?.Value ?? "",
                (string?)x.Room.Attribute("autojoin") is "true" or "1",
                x.Room.Element(Bookmarks2 + "password")?.Value ?? ""))];

    /// <summary>Removes what XML cannot carry. ⚠️ One stray control character would otherwise throw on send.</summary>
    public static string Clean(string text) =>
        new([.. text.Where(c => c is '\t' or '\n' or '\r' || c >= ' ' && c != '￾' && c != '￿')]);

    public async ValueTask DisposeAsync()
    {
        try { if (_run is { IsCancellationRequested: false }) await _run.CancelAsync(); }
        catch (ObjectDisposedException) { /* already over */ }
        await connection.DisposeAsync();
    }
}
