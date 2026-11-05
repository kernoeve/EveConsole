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

    /// <summary>Came out of the server's archive, under this id, rather than being delivered.</summary>
    public string ArchiveId { get; init; } = "";
}

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
public sealed class XmppClient(XmppConnection connection) : IAsyncDisposable
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
    public static readonly XNamespace Version   = "jabber:iq:version";
    public static readonly XNamespace Stanzas   = "urn:ietf:params:xml:ns:xmpp-stanzas";
    public static readonly XNamespace Receipts  = "urn:xmpp:receipts";
    public static readonly XNamespace Correct   = "urn:xmpp:message-correct:0";
    public static readonly XNamespace DirectInvite = "jabber:x:conference";

    private static readonly TimeSpan IqTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<XElement>> _pending = new();
    private long _nextId;
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
            foreach (var (_, waiting) in _pending) waiting.TrySetCanceled(CancellationToken.None);
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
                var answer = new XElement(Ping + "ping");
                await QueryAsync("get", Self.Domain, answer, ct);
            }
        }
        catch (OperationCanceledException) { /* the session is ending */ }
        catch (XmppException e) when (e.Failure == XmppFailure.Protocol) { /* refused the ping, but it answered */ }
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
            if (_pending.TryRemove(id, out var waiting)) waiting.TrySetResult(iq);
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
            if (ParseArchived(stanza, Self, _archiveQuery) is { } archived) MessageReceived?.Invoke(archived);
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

        if (message.Body.Length > 0) MessageReceived?.Invoke(message);
    }

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

        return new XmppMessage(
            XmppAddress.Parse((string?)inner.Attribute("from")),
            XmppAddress.Parse((string?)inner.Attribute("to")),
            (string?)inner.Attribute("type") ?? "normal",
            inner.Element(Client + "body")?.Value ?? "",
            inner.Element(Client + "subject")?.Value ?? "",
            (string?)inner.Attribute("id") ?? "",
            sent,
            (string?)inner.Element(StanzaId + "stanza-id")?.Attribute("id") ?? "",
            mine)
        {
            IsCorrection = inner.Element(Correct + "replace") is not null,
        };
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
                    new XAttribute("id", "ec" + Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture)),
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
        var id     = "ec" + Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        var answer = new TaskCompletionSource<XElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;

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
    public Task SetPresenceAsync(XmppShow show, string status, CancellationToken ct)
    {
        var presence = new XElement(Client + "presence");
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
        return connection.WriteAsync(presence, ct);
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

        // Rooms have their own history, asked for on joining.
        var type = (string?)inner.Attribute("type") ?? "normal";
        if (type is "groupchat" or "error") return null;

        var body = inner.Element(Client + "body")?.Value ?? "";
        if (body.Length == 0) return null;

        DateTimeOffset? sent = null;
        if (DateTimeOffset.TryParse((string?)forwarded.Element(Delay + "delay")?.Attribute("stamp"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp)) sent = stamp;

        var sender = XmppAddress.Parse((string?)inner.Attribute("from"));

        return new XmppMessage(
            sender,
            XmppAddress.Parse((string?)inner.Attribute("to")),
            type, body,
            inner.Element(Client + "subject")?.Value ?? "",
            (string?)inner.Attribute("id") ?? "",
            sent,
            (string?)inner.Element(StanzaId + "stanza-id")?.Attribute("id") ?? "",
            sender.Bare == self.Bare)
        {
            ArchiveId = (string?)result.Attribute("id") ?? "",
        };
    }

    /// <summary>Whether this account's server keeps an archive to ask.</summary>
    public async Task<bool> HasArchiveAsync(CancellationToken ct) =>
        (await FeaturesOfAsync(Self.Bare, ct)).Contains(Mam.NamespaceName);

    /// <summary>Asks for everything since a moment. It arrives as ordinary messages, before this returns.</summary>
    public async Task QueryArchiveAsync(DateTimeOffset since, CancellationToken ct)
    {
        const int PageSize = 100, MostPages = 20;

        var query = "ec" + Guid.NewGuid().ToString("N");
        _archiveQuery = query;
        try
        {
            string? after = null;
            for (var page = 0; page < MostPages; page++)
            {
                var answer = await QueryAsync("set", null, new XElement(Mam + "query",
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
                if (fin is null || (string?)fin.Attribute("complete") == "true") return;

                after = fin.Element(Rsm + "set")?.Element(Rsm + "last")?.Value;
                if (string.IsNullOrEmpty(after)) return;
            }
        }
        finally { _archiveQuery = null; }
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
            catch (XmppException e) when (e.Failure == XmppFailure.Protocol) { /* a service with nothing to say about itself */ }
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

        return connection.WriteAsync(new XElement(Client + "presence", new XAttribute("to", $"{room}/{nick}"), x), ct);
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
