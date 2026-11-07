// The Jabber client's rules: the login's arithmetic, what is refused, and what counts as read.
// Needs no server: what a real one does is checked against a throwaway Openfire by hand.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Localization;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Jabber;
using EveConsole.ViewModels;
using Microsoft.EntityFrameworkCore;

var checks = 0;
var failures = new List<string>();

void Check(string name, bool ok, string detail = "")
{
    checks++;
    if (!ok) failures.Add($"  FAIL  {name}{(detail.Length > 0 ? " | " + detail : "")}");
}

// ── Addresses ─────────────────────────────────────────────────────────────────
{
    var a = XmppAddress.Parse("Name@Domain.COM/Some Resource");
    Check("address: local and domain lower, resource kept", a.Bare == "name@domain.com" && a.Resource == "Some Resource", a.Full);
    Check("address: a server alone", XmppAddress.Parse("conference.example").Bare == "conference.example");
    Check("address: nothing", XmppAddress.Parse(null).Bare.Length == 0);
    Check("address: a room nickname may hold a slash", XmppAddress.Parse("r@c.example/a/b").Resource == "a/b");
}

// ── The login, against the published test vectors ─────────────────────────────
{
    // RFC 5802, section 5.
    var sha1 = new XmppScram("SCRAM-SHA-1", "user", "pencil", "fyko+d2lbbFgONRv9qkxdawL");
    Check("scram: first message", sha1.ClientFirst() == "n,,n=user,r=fyko+d2lbbFgONRv9qkxdawL", sha1.ClientFirst());
    var final1 = sha1.ClientFinal("r=fyko+d2lbbFgONRv9qkxdawL3rfcNHYJY1ZVvWVs7j,s=QSXCR+Q6sek8bf92,i=4096");
    Check("scram sha-1: the RFC's proof", final1 == "c=biws,r=fyko+d2lbbFgONRv9qkxdawL3rfcNHYJY1ZVvWVs7j,p=v0X8v3Bz2T0CJGbJQyF0X+HI4Ts=", final1);
    Check("scram sha-1: the server's signature is checked", sha1.ServerIsGenuine("v=rmF9pqV8S7suAoZWja4dJRkFsKQ="));
    Check("scram: a wrong signature is refused", !sha1.ServerIsGenuine("v=AAAAAAAAAAAAAAAAAAAAAAAAAAA="));
    Check("scram: no signature is refused", !sha1.ServerIsGenuine(""));

    // RFC 7677, section 3.
    var sha256 = new XmppScram("SCRAM-SHA-256", "user", "pencil", "rOprNGfwEbeRWgbNEkqO");
    var final256 = sha256.ClientFinal("r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096");
    Check("scram sha-256: the RFC's proof", final256.EndsWith(",p=dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=", StringComparison.Ordinal), final256);
    Check("scram sha-256: the server's signature is checked", sha256.ServerIsGenuine("v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4="));

    bool Refused(string challenge)
    {
        try { new XmppScram("SCRAM-SHA-256", "user", "pencil", "abc").ClientFinal(challenge); return false; }
        catch (XmppException e) { return e.Failure == XmppFailure.Login; }
    }
    Check("scram: a nonce not extending ours is refused", Refused("r=zzz,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096"));
    Check("scram: a hostile iteration count is refused", Refused("r=abcd,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=2000000000"));
    Check("scram: bad salt is refused", Refused("r=abcd,s=!!!,i=4096"));
    Check("scram: a name with = and , is escaped", new XmppScram("SCRAM-SHA-1", "a=b,c", "p", "n").ClientFirst() == "n,,n=a=3Db=2Cc,r=n");
}

// ── Reading messages ──────────────────────────────────────────────────────────
{
    var self = XmppAddress.Parse("me@example/here");
    var client = XmppClient.Client;

    XElement Message(string from, string body, string type = "chat", params object[] extra) =>
        new(client + "message", new XAttribute("from", from), new XAttribute("to", self.Full), new XAttribute("type", type), new XAttribute("id", "m1"),
            new XElement(client + "body", body), extra);

    var plain = XmppClient.ParseMessage(Message("them@example/x", "hello"), self);
    Check("message: plain", plain is { Body: "hello", FromThisAccount: false, Sent: null } && plain.From.Bare == "them@example");

    XElement Carbon(string from, string kind, string innerFrom, string innerTo) =>
        new(client + "message", new XAttribute("from", from), new XAttribute("to", self.Full),
            new XElement(XmppClient.Carbons + kind, new XElement(XmppClient.Forward + "forwarded",
                new XElement(client + "message", new XAttribute("from", innerFrom), new XAttribute("to", innerTo), new XAttribute("type", "chat"),
                    new XElement(client + "body", "copied")))));

    var sent = XmppClient.ParseMessage(Carbon("me@example", "sent", "me@example/other", "them@example"), self);
    Check("carbon: what this account sent elsewhere", sent is { FromThisAccount: true, Body: "copied" } && sent.To.Bare == "them@example");
    var received = XmppClient.ParseMessage(Carbon("me@example", "received", "them@example/x", "me@example/other"), self);
    Check("carbon: what it received elsewhere, as theirs", received is { FromThisAccount: false } && received.From.Bare == "them@example");
    Check("carbon: from anyone else is refused, or they could put words in anyone's mouth",
          XmppClient.ParseMessage(Carbon("evil@example", "sent", "me@example/other", "them@example"), self) is null);

    var delayed = XmppClient.ParseMessage(Message("them@example/x", "old", "chat",
        new XElement(XmppClient.Delay + "delay", new XAttribute("stamp", "2026-11-05T01:02:03Z"))), self);
    Check("delay: when it was really sent", delayed?.Sent == new DateTimeOffset(2026, 11, 5, 1, 2, 3, TimeSpan.Zero), delayed?.Sent?.ToString() ?? "none");
    var oldDelay = XmppClient.ParseMessage(Message("them@example/x", "old", "chat",
        new XElement(XmppClient.OldDelay + "x", new XAttribute("stamp", "20261105T01:02:03"))), self);
    Check("delay: the old form too", oldDelay?.Sent == new DateTimeOffset(2026, 11, 5, 1, 2, 3, TimeSpan.Zero));
    Check("message: an error is not a message", XmppClient.ParseMessage(Message("them@example", "x", "error"), self) is null);

    var through = new XElement(client + "message", new XAttribute("from", "room@c.example"),
        new XElement(XmppClient.MucUser + "x",
            new XElement(XmppClient.MucUser + "invite", new XAttribute("from", "them@example/x"), new XElement(XmppClient.MucUser + "reason", "join us")),
            new XElement(XmppClient.MucUser + "password", "pw")));
    Check("invite: through the room", XmppClient.ParseInvite(through) == new XmppInvite("room@c.example", "them@example", "join us", "pw"));
    var direct = new XElement(client + "message", new XAttribute("from", "them@example/x"),
        new XElement(XmppClient.DirectInvite + "x", new XAttribute("jid", "room@c.example"), new XAttribute("reason", "r")));
    Check("invite: direct from the person", XmppClient.ParseInvite(direct) == new XmppInvite("room@c.example", "them@example", "r", ""));
    Check("invite: an ordinary message is not one", XmppClient.ParseInvite(Message("them@example", "hello")) is null);
    Check("correction: marked as one", XmppClient.ParseMessage(Message("them@example/x", "fixed", "chat",
        new XElement(XmppClient.Correct + "replace", new XAttribute("id", "m0"))), self)?.IsCorrection == true);
    Check("correction: a plain message is not one", plain?.IsCorrection == false);

    Check("clean: what XML cannot carry goes, the rest stays", XmppClient.Clean("a\u0001b\nc\td￾") == "ab\nc\td");
}

// ── What calls for attention ──────────────────────────────────────────────────
{
    Check("word: alone", JabberService.ContainsWord("is alice here?", "Alice"));
    Check("word: at the edges", JabberService.ContainsWord("alice", "alice"));
    Check("word: not inside another", !JabberService.ContainsWord("malice aforethought", "alice"));
    Check("word: found after a false start", JabberService.ContainsWord("malice alice", "alice"));

    var account = new JabberAccount { Id = 7, Address = "me@example", AlertWords = "fleet, cta" };
    var room = new JabberRoom { RoomJid = "r@c.example", AlertMode = JabberRoomAlert.Mentions };
    var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    XmppMessage Dm(string body, DateTimeOffset? sent = null) => new(XmppAddress.Parse("bot@example/x"), XmppAddress.Parse("me@example"), "chat", body, "", "i1", sent, "", false);
    XmppMessage InRoom(string nick, string body, DateTimeOffset? sent = null) => new(XmppAddress.Parse($"r@c.example/{nick}"), XmppAddress.Parse("me@example"), "groupchat", body, "", "i2", sent, "", false);

    var live = JabberService.ToStored(account, null, null, Dm("ping"), now);
    Check("dm: unread and an alert", live is { IsRead: false, IsAlert: true, IsOutgoing: false, IsRoom: false, Conversation: "bot@example" });
    var waited = JabberService.ToStored(account, null, null, Dm("ping", now.AddHours(-3)), now);
    Check("dm: one that waited on the server is what they came back for", waited is { IsRead: false, IsAlert: true });
    Check("dm: carries when it was really sent", waited.SentAt == "2026-11-05T09:00:00Z", waited.SentAt);
    var normal = new XmppMessage(XmppAddress.Parse("bot@example"), XmppAddress.Parse("me@example"), "headline", "broadcast", "", "i", null, "", false);
    Check("dm: a bot's headline or normal message counts as direct", JabberService.ToStored(account, null, null, normal, now) is { IsRoom: false, IsAlert: true });

    var mine = JabberService.ToStored(account, null, null, new XmppMessage(XmppAddress.Parse("me@example/other"), XmppAddress.Parse("them@example"), "chat", "hi", "", "i", null, "", true), now);
    Check("dm: what I said elsewhere is in their conversation, already read", mine is { Conversation: "them@example", IsOutgoing: true, IsRead: true, IsAlert: false });

    Check("room: talk is unread, no alert", JabberService.ToStored(account, "Me", room, InRoom("Them", "hello"), now) is { IsRead: false, IsAlert: false, IsRoom: true, Sender: "Them" });
    Check("room: my nickname alerts", JabberService.ToStored(account, "Me", room, InRoom("Them", "hey me!"), now).IsAlert);
    Check("room: a keyword alerts", JabberService.ToStored(account, "Me", room, InRoom("Them", "FLEET up"), now).IsAlert);
    Check("room: my own echo is read, not an alert", JabberService.ToStored(account, "Me", room, InRoom("Me", "hey me"), now) is { IsOutgoing: true, IsRead: true, IsAlert: false });
    Check("room: replayed history is read, not an alert", JabberService.ToStored(account, "Me", room, InRoom("Them", "hey me", now.AddHours(-1)), now) is { IsRead: true, IsAlert: false });

    var all = new JabberRoom { AlertMode = JabberRoomAlert.All };
    var none = new JabberRoom { AlertMode = JabberRoomAlert.None };
    Check("room: set to everything", JabberService.ToStored(account, "Me", all, InRoom("Them", "x"), now).IsAlert);
    Check("room: set to nothing, even naming me", !JabberService.ToStored(account, "Me", none, InRoom("Them", "hey me"), now).IsAlert);

    var pm = JabberService.ToStored(account, null, null, new XmppMessage(XmppAddress.Parse("r@c.example/Them"), XmppAddress.Parse("me@example"), "chat", "psst", "", "i", null, "", false), now, privateInRoom: true);
    Check("room: a private message is its own conversation, and an alert", pm is { Conversation: "r@c.example/Them", IsRoom: false, IsAlert: true, Sender: "Them" });

    Check("nick: the room's, then the account's, then the login's",
          JabberService.NicknameFor(new JabberAccount { Address = "me@x", Nickname = "A" }, new JabberRoom { Nickname = "R" }) == "R"
          && JabberService.NicknameFor(new JabberAccount { Address = "me@x", Nickname = "A" }, null) == "A"
          && JabberService.NicknameFor(new JabberAccount { Address = "me@x" }, null) == "me");

    var id1 = new XmppMessage(XmppAddress.Parse("a@x"), XmppAddress.Parse("b@x"), "chat", "same", "", "id", null, "srv", false);
    var id2 = id1 with { Id = "other" };
    Check("dedupe: the server's id wins", JabberService.DedupeKey("c", "s", id1) == JabberService.DedupeKey("c", "s", id2));
    var noServer = id1 with { ServerId = "" };
    Check("dedupe: then the sender's", JabberService.DedupeKey("c", "s", noServer) != JabberService.DedupeKey("c", "other", noServer));
    var bare = id1 with { ServerId = "", Id = "", Sent = now };
    Check("dedupe: then the words and time", JabberService.DedupeKey("c", "s", bare) == JabberService.DedupeKey("c", "s", bare));
    var unstamped = id1 with { ServerId = "", Id = "" };
    Check("dedupe: two with nothing to go on are not one", JabberService.DedupeKey("c", "s", unstamped) != JabberService.DedupeKey("c", "s", unstamped));
    Check("resource: the same on every PC, so a new worker replaces a dead one's session", JabberService.WorkerResource == "EVEConsole");
}

// ── Where to connect ──────────────────────────────────────────────────────────
{
    var a = new XmppEndpoint("a.example", 5222, false);
    var b = new XmppEndpoint("b.example", 5223, true);
    var ordered = XmppDns.Order("d.example", [(20, 0, a), (10, 0, b)]);
    Check("dns: lowest priority first, then the domain itself", ordered.SequenceEqual([b, a, new XmppEndpoint("d.example", 5222, false)]));
    Check("dns: heavier first within a priority", XmppDns.Order("d", [(10, 1, a), (10, 9, b)])[0] == b);
    Check("dns: no records, the domain on the usual port", XmppDns.Order("d", []).SequenceEqual([new XmppEndpoint("d", 5222, false)]));
    Check("dns: the domain is not listed twice", XmppDns.Order("d", [(1, 1, new XmppEndpoint("d", 5222, false))]).Count == 1);
}

// ── A server offering no encryption gets no credentials ───────────────────────
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var seen = new StringBuilder();

    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync();
        var stream = peer.GetStream();
        var buffer = new byte[4096];
        var n = await stream.ReadAsync(buffer);
        seen.Append(Encoding.UTF8.GetString(buffer, 0, n));

        // Features with no starttls, and PLAIN on offer: the bait.
        var answer = "<?xml version='1.0'?><stream:stream xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' id='1' version='1.0'>"
                   + "<stream:features><mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'><mechanism>PLAIN</mechanism></mechanisms></stream:features>";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(answer));

        try
        {
            while ((n = await stream.ReadAsync(buffer)) > 0) seen.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }
        catch (IOException) { /* the client went, as it should */ }
    });

    XmppFailure? failure = null;
    await using (var c = new XmppConnection(new XmppLogin("me@example", "secret-password", "127.0.0.1", port)))
    {
        try { await c.ConnectAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token); }
        catch (XmppException e) { failure = e.Failure; }
    }
    await server.WaitAsync(TimeSpan.FromSeconds(5));
    listener.Stop();

    Check("no encryption: refused, and not after a hang", failure == XmppFailure.NoEncryption, failure?.ToString() ?? "none");
    Check("no encryption: nothing of the login was sent", !seen.ToString().Contains("auth", StringComparison.Ordinal)
          && !seen.ToString().Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("\0me\0secret-password")), StringComparison.Ordinal), seen.ToString());
}

// ── Nothing listening is a failure to retry, not one that needs the person ────
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();

    XmppFailure? failure = null;
    await using (var c = new XmppConnection(new XmppLogin("me@example", "x", "127.0.0.1", port)))
    {
        try { await c.ConnectAsync(CancellationToken.None); }
        catch (XmppException e) { failure = e.Failure; }
    }
    Check("network: nothing there", failure == XmppFailure.Network, failure?.ToString() ?? "none");

    Check("stream error: replaced is its own kind", XmppConnection.StreamError(new XElement(XmppConnection.Streams + "error",
        new XElement(XNamespace.Get("urn:ietf:params:xml:ns:xmpp-streams") + "conflict"))).Failure == XmppFailure.Replaced);
}

// ── Saving, reading and forgetting ────────────────────────────────────────────
// ── Who an answer is from ─────────────────────────────────────────────────────
{
    var me = XmppAddress.Parse("me@example/home");

    Check("answers: from who was asked", XmppClient.AnswerIsFrom("conference.example", "conference.example", me));
    Check("answers: from anyone else, ignored", !XmppClient.AnswerIsFrom("conference.example", "stranger@example/x", me));
    Check("answers: a stranger cannot answer what was asked of the server", !XmppClient.AnswerIsFrom(null, "stranger@example/x", me));
    Check("answers: the server, about this account, in any of its forms",
          XmppClient.AnswerIsFrom(null, null, me) && XmppClient.AnswerIsFrom(null, "me@example", me) && XmppClient.AnswerIsFrom(null, "me@example/home", me) && XmppClient.AnswerIsFrom(null, "example", me));
    Check("answers: no name given is fine only from this account's own side", XmppClient.AnswerIsFrom("example", null, me) && XmppClient.AnswerIsFrom("me@example", null, me) && !XmppClient.AnswerIsFrom("conference.example", null, me));
    Check("answers: letter case does not matter", XmppClient.AnswerIsFrom("Conference.Example", "conference.example", me));
    Check("answers: another resource of who was asked is someone else", !XmppClient.AnswerIsFrom("them@example/a", "them@example/b", me));
}

// ── The server's archive: only ever this account's own ──────────────────────
{
    var self = XmppAddress.Parse("me@example/home");

    XElement Archived(string? outerFrom, string queryId, string innerFrom, string type = "chat", string body = "hello", string? innerId = "m1") =>
        new(XmppClient.Client + "message",
            outerFrom is null ? null : new XAttribute("from", outerFrom),
            new XElement(XmppClient.Mam + "result", new XAttribute("queryid", queryId), new XAttribute("id", "arc-1"),
                new XElement(XmppClient.Forward + "forwarded",
                    new XElement(XmppClient.Delay + "delay", new XAttribute("stamp", "2026-01-02T03:04:05Z")),
                    new XElement(XmppClient.Client + "message",
                        new XAttribute("from", innerFrom), new XAttribute("to", "me@example"), new XAttribute("type", type),
                        innerId is null ? null : new XAttribute("id", innerId),
                        new XElement(XmppClient.Client + "body", body)))));

    var good = XmppClient.ParseArchived(Archived("me@example", "q1", "friend@example/phone"), self, "q1");
    Check("archive: read", good is { Body: "hello", ArchiveId: "arc-1", Id: "m1", FromThisAccount: false } && good.From.Bare == "friend@example");
    Check("archive: dated when it was said", good?.Sent == new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), good?.Sent?.ToString() ?? "");
    Check("archive: no 'from' means the server itself", XmppClient.ParseArchived(Archived(null, "q1", "friend@example"), self, "q1") is not null);
    Check("archive: what this account said elsewhere is its own", XmppClient.ParseArchived(Archived("me@example", "q1", "me@example/phone"), self, "q1")?.FromThisAccount == true);

    Check("archive: from anyone else, refused", XmppClient.ParseArchived(Archived("stranger@example", "q1", "friend@example"), self, "q1") is null);
    Check("archive: not in answer to the question, refused", XmppClient.ParseArchived(Archived("me@example", "other", "friend@example"), self, "q1") is null);
    Check("archive: with no question asked, refused", XmppClient.ParseArchived(Archived("me@example", "q1", "friend@example"), self, null) is null);
    Check("archive: room messages have their own history", XmppClient.ParseArchived(Archived("me@example", "q1", "room@c.example/nick", "groupchat"), self, "q1") is null);
    Check("archive: nothing to say, nothing kept", XmppClient.ParseArchived(Archived("me@example", "q1", "friend@example", body: ""), self, "q1") is null);

    const string Room = "room@c.example";
    var said = XmppClient.ParseRoomArchived(Archived(Room, "q1", Room + "/Them", "groupchat"), Room, "q1");
    Check("room archive: read", said is { Body: "hello", ArchiveId: "arc-1", Id: "m1", Type: "groupchat", FromThisAccount: false } && said.From.Resource == "Them");
    Check("room archive: dated when it was said", said?.Sent == new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
    Check("room archive: from someone in the room, refused", XmppClient.ParseRoomArchived(Archived(Room + "/Them", "q1", Room + "/Other", "groupchat"), Room, "q1") is null);
    Check("room archive: from another room, refused", XmppClient.ParseRoomArchived(Archived("other@c.example", "q1", "other@c.example/Them", "groupchat"), Room, "q1") is null);
    Check("room archive: with no sender at all, refused", XmppClient.ParseRoomArchived(Archived(null, "q1", Room + "/Them", "groupchat"), Room, "q1") is null);
    Check("room archive: naming someone in another room, refused", XmppClient.ParseRoomArchived(Archived(Room, "q1", "other@c.example/Them", "groupchat"), Room, "q1") is null);
    Check("room archive: a private message inside it, refused", XmppClient.ParseRoomArchived(Archived(Room, "q1", Room + "/Them", "chat"), Room, "q1") is null);
    Check("room archive: not in answer to the question, refused", XmppClient.ParseRoomArchived(Archived(Room, "other", Room + "/Them", "groupchat"), Room, "q1") is null);
    Check("room archive: with no question asked, refused", XmppClient.ParseRoomArchived(Archived(Room, "q1", Room + "/Them", "groupchat"), Room, null) is null);
    Check("room archive: nothing to say, nothing kept", XmppClient.ParseRoomArchived(Archived(Room, "q1", Room + "/Them", "groupchat", body: ""), Room, "q1") is null);

    var account = new JabberAccount { Id = 1, Address = "me@example" };
    var kept = new JabberRoom { RoomJid = Room };
    var missed = new XmppMessage(XmppAddress.Parse(Room + "/Them"), self, "groupchat", "hello", "", "m1", DateTimeOffset.UtcNow.AddHours(-2), "", false) { ArchiveId = "arc-1" };
    Check("room archive: what was missed is unread, as it would have been", JabberService.ToStored(account, "Me", kept, missed, DateTimeOffset.UtcNow) is { IsRead: false, IsAlert: false, IsRoom: true });
    Check("room archive: naming me is an alert", JabberService.ToStored(account, "Me", kept, missed with { Body = "hey me" }, DateTimeOffset.UtcNow) is { IsRead: false, IsAlert: true });
    Check("room archive: what I said elsewhere is mine, and read", JabberService.ToStored(account, "Them", kept, missed, DateTimeOffset.UtcNow) is { IsRead: true, IsOutgoing: true });
    Check("room archive: a replay is still only history", JabberService.ToStored(account, "Me", kept, missed with { ArchiveId = "", Body = "hey me" }, DateTimeOffset.UtcNow) is { IsRead: true, IsAlert: false });

    // ── Group Chat: mentions, room details, and who is a person in a room ───────────────────
    {
        var named = new JabberAccount { Id = 1, Address = "me@example.test", Nickname = "Pilot" };
        var inRoom = new XmppMessage(XmppAddress.Parse("room@rooms.example.test/Someone"), XmppAddress.Parse("me@example.test"), "groupchat", "RoomNick, over here", "", "id1", null, "", false);
        Check("mentions: the name in this room", JabberService.ToStored(named, "RoomNick", null, inRoom, DateTimeOffset.UtcNow).IsMention);
        Check("mentions: the usual name too", JabberService.NamesThem(named, "RoomNick", "pilot: over here"));
        Check("mentions: not inside another word", !JabberService.NamesThem(named, "Al", "already there, Pilots"));
        Check("mentions: nobody names themselves", !JabberService.ToStored(named, "Someone", null, inRoom with { Body = "Someone here" }, DateTimeOffset.UtcNow).IsMention);
        Check("mentions: a direct message is not one", !JabberService.ToStored(named, null, null, inRoom with { Type = "chat", Body = "Pilot" }, DateTimeOffset.UtcNow).IsMention);

        var details = XmppClient.ParseRoomDetails("room@rooms.example.test", System.Xml.Linq.XElement.Parse(
            """
            <query xmlns='http://jabber.org/protocol/disco#info'>
              <identity category='conference' name='The Room' type='text'/>
              <feature var='muc_passwordprotected'/><feature var='muc_membersonly'/><feature var='muc_moderated'/><feature var='muc_temporary'/>
              <x xmlns='jabber:x:data' type='result'>
                <field var='FORM_TYPE' type='hidden'><value>http://jabber.org/protocol/muc#roominfo</value></field>
                <field var='muc#roominfo_description'><value>What it is for</value></field>
                <field var='muc#roominfo_subject'><value>Today's topic</value></field>
                <field var='muc#roominfo_occupants'><value>12</value></field>
              </x>
            </query>
            """));
        Check("details: all of it", details is { Name: "The Room", Description: "What it is for", Subject: "Today's topic", People: 12, NeedsPassword: true, MembersOnly: true, Moderated: true, Temporary: true });

        var bare = XmppClient.ParseRoomDetails("room@rooms.example.test", System.Xml.Linq.XElement.Parse("<query xmlns='http://jabber.org/protocol/disco#info'><field>12</field></query>"));
        Check("details: a room saying nothing is still a room", bare is { Name: "", Description: "", People: null, NeedsPassword: false });
        Check("details: nothing at all", XmppClient.ParseRoomDetails("r@s", null).People is null);

        string[] services = ["rooms.example.test"];
        Check("person in a room: a room kept", JabberService.IsSomeoneInARoom(XmppAddress.Parse("kept@elsewhere.test/Nick"), true, []));
        Check("person in a room: a room since left, by its service", JabberService.IsSomeoneInARoom(XmppAddress.Parse("old@ROOMS.example.test/Nick"), false, services));
        Check("person in a room: not the room itself", !JabberService.IsSomeoneInARoom(XmppAddress.Parse("old@rooms.example.test"), false, services));
        Check("person in a room: not someone's phone", !JabberService.IsSomeoneInARoom(XmppAddress.Parse("them@example.test/phone"), false, services));
    }

    // ── Times in the chat tools: EVE time, always with the date ──────────────────────────
    {
        var evening = new DateTimeOffset(2026, 11, 5, 23, 30, 0, TimeSpan.FromHours(-7));
        Check("chat time: EVE time, across midnight", ChatTime.Of(evening) == "2026-11-06 06:30");
        Check("chat time: today has its date too", ChatTime.Of(DateTimeOffset.UtcNow).StartsWith(DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd"), StringComparison.Ordinal));
        Check("chat time: as saved", ChatTime.Of("2026-11-06T01:29:53Z") == "2026-11-06 01:29");
        Check("chat time: nothing readable, nothing shown", ChatTime.Of("not a time") == "");
        Check("chat time: a retry's clock", ChatTime.Clock(evening) == "06:30:00");
    }

    // ── The list of rooms to join, by name or by how many are in them ─────────────────────
    {
        GroupRoomChoiceVm Listed(string name, int? people)
        {
            var room = new GroupRoomChoiceVm(new XmppRoomInfo($"{name}@rooms.example.test", name), false);
            room.Apply(new XmppRoomDetails(room.Jid, name, "", "", people, false, false, false, false));
            return room;
        }

        GroupRoomChoiceVm[] rooms = [Listed("Bravo", 2), Listed("alpha", 10), Listed("Charlie", 100), Listed("Delta", null), Listed("Echo", 10), Listed("Foxtrot", 0)];
        string Order(int sort) => string.Join(",", GroupRoomChoiceVm.Sorted(rooms, sort).Select(r => r.Name));

        Check("room sort: by name, any capitals", Order(GroupRoomChoiceVm.SortByName) == "alpha,Bravo,Charlie,Delta,Echo,Foxtrot");
        Check("room sort: by number, not its digits", Order(GroupRoomChoiceVm.SortByPeople) == "Charlie,alpha,Echo,Bravo,Foxtrot,Delta");
    }

    // ── Rooms marked Intel: what reaches the parser, and a report held from another source ──
    {
        var room = new JabberRoom { AccountId = 3, RoomJid = "ops@rooms.example.test", Name = "Operations" };
        JabberStoredMessage Said(string body, long id = 7, bool isRoom = true) => new() { Id = id, IsRoom = isRoom, Sender = "Scout", Body = body, SentAt = "2026-11-05T18:00:30Z" };

        var plain = IntelRooms.ToChatLines(room, Said("QZ-X77 Sampler")).Single();
        Check("intel rooms: a line as said", plain is { ChannelName: "Jabber: Operations", SenderName: "Scout", Message: "QZ-X77 Sampler", OccurredAt: "2026-11-05T18:00:30Z" });
        Check("intel rooms: named from the address without a name", IntelRooms.ChannelOf(new JabberRoom { RoomJid = "ops@rooms.example.test" }) == "Jabber: ops");

        var relayed = IntelRooms.ToChatLines(room, Said("[ 2026.11.05 18:00:12 ] Tester > QZ-X77 Sampler")).Single();
        Check("intel rooms: a relayed chat line is who said it, when", relayed is { SenderName: "Tester", Message: "QZ-X77 Sampler", OccurredAt: "2026-11-05T18:00:12Z" });

        var stale = IntelRooms.ToChatLines(room, Said("[ 2026.11.01 18:00:12 ] Tester > QZ-X77 Sampler")).Single();
        Check("intel rooms: a time too far off is not believed", stale.OccurredAt == "2026-11-05T18:00:30Z");
        Check("intel rooms: the game's own lines are not reports", IntelRooms.ToChatLines(room, Said("[ 2026.11.05 18:00:12 ] EVE System > Channel MOTD")).Count == 0);

        var several = IntelRooms.ToChatLines(room, Said("QZ-X77 Sampler\n\nXQ1-Z2 Tester"));
        Check("intel rooms: each line of a message", several.Count == 2 && several[1].Message == "XQ1-Z2 Tester" && several[0].LineNumber != several[1].LineNumber);
        Check("intel rooms: lines of other messages stay apart", IntelRooms.ToChatLines(room, Said("a", id: 8)).Single().LineNumber != IntelRooms.ToChatLines(room, Said("a")).Single().LineNumber);
        Check("intel rooms: not private words", IntelRooms.ToChatLines(room, Said("QZ-X77", isRoom: false)).Count == 0);

        IntelReport Report(string channel, string at, string message, int count = 1, int flags = 0, int system = 9001) =>
            new() { ChannelName = channel, ReportedAt = at, SystemId = system, Message = message, PlayerCount = count, Flags = flags };
        long[] pilot = [100], other = [200];
        bool CopyOf(IntelReport held, long[] heldPilots, IntelReport next, long[] nextPilots)
        {
            var seen = new IntelDuplicates();
            seen.Add(held, heldPilots);
            return seen.IsCopy(next, nextPilots);
        }

        var chat = Report("Chat", "2026-11-05T18:00:00Z", "QZ-X77 Sampler");
        Check("copies: relayed word for word", CopyOf(chat, pilot, Report("Jabber: Ops", "2026-11-05T18:00:20Z", "QZ-X77 Sampler"), pilot));
        Check("copies: whichever arrived first", CopyOf(Report("Jabber: Ops", "2026-11-05T18:00:20Z", "QZ-X77 Sampler"), pilot, chat, pilot));
        Check("copies: other capitals and spacing", CopyOf(chat, pilot, Report("Jabber: Ops", "2026-11-05T18:00:20Z", "qz-x77   sampler"), pilot));
        Check("copies: a relay's label in front, however it parsed", CopyOf(chat, pilot, Report("Jabber: Ops", "2026-11-05T18:00:20Z", "[Intel] Tester: QZ-X77 Sampler", 2), [100, 300]));
        Check("copies: the same facts in other words", CopyOf(chat, pilot, Report("Jabber: Ops", "2026-11-05T18:00:20Z", "Sampler is in QZ-X77"), pilot));
        Check("copies: not within one channel", !CopyOf(chat, pilot, Report("Chat", "2026-11-05T18:00:20Z", "QZ-X77 Sampler"), pilot));
        Check("copies: not long after", !CopyOf(chat, pilot, Report("Jabber: Ops", "2026-11-05T18:02:01Z", "QZ-X77 Sampler"), pilot));
        Check("copies: right at the limit", CopyOf(chat, pilot, Report("Jabber: Ops", "2026-11-05T18:02:00Z", "QZ-X77 Sampler"), pilot));
        Check("copies: not another system", !CopyOf(chat, pilot, Report("Jabber: Ops", "2026-11-05T18:00:20Z", "QZ-X77 Sampler", system: 9002), pilot));
        Check("copies: not another pilot", !CopyOf(chat, pilot, Report("Jabber: Ops", "2026-11-05T18:00:20Z", "QZ-X77 Tester"), other));
        Check("copies: not when it says more", !CopyOf(chat, pilot, Report("Jabber: Ops", "2026-11-05T18:00:20Z", "QZ-X77 Sampler +5", 6), pilot));
        Check("copies: not when it adds bubbles", !CopyOf(chat, pilot, Report("Jabber: Ops", "2026-11-05T18:00:20Z", "QZ-X77 Sampler bubbles", 1, 4), pilot));
        Check("copies: a bare system is not the fuller report", !CopyOf(Report("Chat", "2026-11-05T18:00:00Z", "QZ-X77", 0), [], Report("Jabber: Ops", "2026-11-05T18:00:20Z", "Sampler QZ-X77"), pilot));
        Check("copies: nor the other way", !CopyOf(chat, pilot, Report("Jabber: Ops", "2026-11-05T18:00:20Z", "QZ-X77", 0), []));
        Check("copies: a time that cannot be read is no match", !CopyOf(chat, pilot, Report("Jabber: Ops", "", "QZ-X77 Sampler"), pilot));
    }

    Check("lines: nothing said until asked", JabberViewModel.RoomArchiveLine(null, false).Length == 0 && JabberViewModel.AccountArchiveLine(null, 0, 0).Length == 0);
    Check("lines: a room that keeps one", JabberViewModel.RoomArchiveLine(true, false) == JabberText.ArchiveRoomYes);
    Check("lines: a room that does not", JabberViewModel.RoomArchiveLine(false, false) == JabberText.ArchiveRoomNo);
    Check("lines: refused outranks having one", JabberViewModel.RoomArchiveLine(true, true) == JabberText.ArchiveRoomRefused);
    Check("lines: the server's, with the rooms counted", JabberViewModel.AccountArchiveLine(true, 2, 5) is { } both && both.StartsWith(JabberText.ArchiveDirectYes, StringComparison.Ordinal) && both.Contains('2') && both.Contains('5'));
    Check("lines: no rooms, no count", JabberViewModel.AccountArchiveLine(false, 0, 0) == JabberText.ArchiveDirectNo);
}

// ── Repeats ───────────────────────────────────────────────────────────────────
{
    XmppMessage Msg(string id, string body, string server = "") =>
        new(XmppAddress.Parse("bot@example"), XmppAddress.Parse("me@example"), "chat", body, "", id, null, server, false);

    var key = JabberService.DedupeKey("bot@example", "bot@example", Msg("7", "Fleet up"));
    Check("repeat: the same id and words is the same message", key == JabberService.DedupeKey("bot@example", "bot@example", Msg("7", "Fleet up")));
    Check("repeat: a bot's restarted counter is a new message", key != JabberService.DedupeKey("bot@example", "bot@example", Msg("7", "Fleet down")));
    Check("repeat: the server's own id decides when it gave one",
          JabberService.DedupeKey("c", "s", Msg("1", "a", "srv")) == JabberService.DedupeKey("c", "s", Msg("2", "b", "srv")));
}

// ── Files ─────────────────────────────────────────────────────────────────────
{
    XElement Info(params XElement[] extra) => new(XmppClient.Client + "iq", new XElement(XmppClient.DiscoInfo + "query",
        new XElement(XmppClient.DiscoInfo + "feature", new XAttribute("var", XmppClient.Upload.NamespaceName)), extra));

    XElement Form(string size) => new(XmppClient.DataForm + "x",
        new XElement(XmppClient.DataForm + "field", new XAttribute("var", "FORM_TYPE"), new XElement(XmppClient.DataForm + "value", XmppClient.Upload.NamespaceName)),
        new XElement(XmppClient.DataForm + "field", new XAttribute("var", "max-file-size"), new XElement(XmppClient.DataForm + "value", size)));

    Check("files: the service and its limit", XmppClient.ParseUploadService("up.example", Info(Form("5000"))) is { Jid: "up.example", MaxBytes: 5000 });
    Check("files: no limit given is no limit known", XmppClient.ParseUploadService("up.example", Info())?.MaxBytes == 0);
    Check("files: nonsense for a limit is no limit known", XmppClient.ParseUploadService("up.example", Info(Form("lots")))?.MaxBytes == 0);
    Check("files: a service that does not take them", XmppClient.ParseUploadService("c.example",
        new XElement(XmppClient.Client + "iq", new XElement(XmppClient.DiscoInfo + "query"))) is null);

    var slot = XmppClient.ParseUploadSlot(new XElement(XmppClient.Client + "iq", new XElement(XmppClient.Upload + "slot",
        new XElement(XmppClient.Upload + "put", new XAttribute("url", "https://up.example/put"),
            new XElement(XmppClient.Upload + "header", new XAttribute("name", "Authorization"), "Basic abc"),
            new XElement(XmppClient.Upload + "header", new XAttribute("name", "Host"), "elsewhere.example"),
            new XElement(XmppClient.Upload + "header", new XAttribute("name", "Cookie"), "a=1\r\nX-Injected: 1")),
        new XElement(XmppClient.Upload + "get", new XAttribute("url", "https://up.example/get")))));
    Check("files: where to put it and fetch it", slot is { PutUrl: "https://up.example/put", GetUrl: "https://up.example/get" });
    Check("files: only the headers the protocol allows", slot?.Headers.Count == 1 && slot.Headers[0].Key == "Authorization", string.Join(",", slot?.Headers.Select(h => h.Key) ?? []));
    Check("files: an answer with nowhere to put it", XmppClient.ParseUploadSlot(new XElement(XmppClient.Client + "iq")) is null);

    Check("files: encrypted is fine", JabberService.IsSafeUploadUrl("https://up.example/x"));
    Check("files: plain to another machine is refused", !JabberService.IsSafeUploadUrl("http://up.example/x"));
    Check("files: plain to this machine, for testing", JabberService.IsSafeUploadUrl("http://127.0.0.1:7070/x"));
    Check("files: anything else is refused", !JabberService.IsSafeUploadUrl("file:///c:/windows/x") && !JabberService.IsSafeUploadUrl("not a url"));

    Check("files: sizes in plain words", EveConsole.ViewModels.JabberViewModel.SizeText(1_572_864) == 1.5.ToString("N1") + " MB" && EveConsole.ViewModels.JabberViewModel.SizeText(10) == "1 KB");
    Check("files: a type from the name", EveConsole.ViewModels.JabberViewModel.ContentTypeOf("MAP.PNG") == "image/png" && EveConsole.ViewModels.JabberViewModel.ContentTypeOf("x.unknown") == "application/octet-stream");
}

// ── Emote packs: the theme file, what is typed, and what is found in a message ───
{
    var theme = EmotePackReader.ParseTheme(
        "Name=Test pack\r\nDescription=For the check\r\nIcon=wave.png\r\nAuthor=Nobody\r\n\r\n# a comment\r\n" +
        "[default]\r\nwave.png :wave: :hi:\r\n! hidden.gif :hidden:\r\n!tight.gif :tight:\r\nlonely.png\r\nother.png :wave:\r\n" +
        "[AIM]\r\naim.png :aim:\r\n[XMPP]\r\nxmpp.png :xmpp:\r\n");
    Check("emote theme: the header", theme is { Name: "Test pack", Description: "For the check", Icon: "wave.png", Author: "Nobody" }, theme.ToString());
    var codes = theme.Lines.SelectMany(l => l.Codes.Select(c => $"{l.File}={c}")).ToList();
    Check("emote theme: every code of a line, hidden ones too, other networks left out",
          string.Join(",", codes) == "wave.png=:wave:,wave.png=:hi:,hidden.gif=:hidden:,tight.gif=:tight:,xmpp.png=:xmpp:", string.Join(",", codes));
}
{
    Check("emote typing: from the start", EmoteSet.TypedCode(":si", 3) == (0, ":si"));
    Check("emote typing: after a space", EmoteSet.TypedCode("hi :si", 6) == (3, ":si"));
    Check("emote typing: right after another code", EmoteSet.TypedCode(":siren::sk", 10) == (7, ":sk"));
    Check("emote typing: not inside a word", EmoteSet.TypedCode("hi:si", 5) is null);
    Check("emote typing: not before two letters", EmoteSet.TypedCode("hi :s", 5) is null);
    Check("emote typing: not once the code is closed", EmoteSet.TypedCode("a :siren:", 9) is null);
    Check("emote typing: not in an address", EmoteSet.TypedCode("see http://x.example", 20) is null);
    Check("emote typing: only up to the caret", EmoteSet.TypedCode(":siren and more", 4) == (0, ":sir"));

    var set = new EmoteSet(1);
    set.Fill([
        new EmoteRef(":wave:", "a", 20, 20, false), new EmoteRef(":siren:", "b", 20, 20, true), new EmoteRef(":)", "c", 16, 16, false),
        new EmoteRef(":wave:", "z", 20, 20, false), new EmoteRef(":sirenspin:", "d", 20, 20, true),
    ], [new EmotePickItem(new EmoteRef(":wave:", "a", 20, 20, false), [":wave:"], "Test pack")]);

    string Shown(string text) => string.Join("|", set.Split(text).Select(p => p.Emote is { } e ? $"[{e.Hash}]" : p.Text));
    Check("emote split: a code between words", Shown("hi :wave: there") == "hi |[a]| there", Shown("hi :wave: there"));
    Check("emote split: two against each other", Shown(":wave::siren:") == "[a]|[b]", Shown(":wave::siren:"));
    Check("emote split: case does not matter for a colon code", Shown(":WAVE:") == "[a]", Shown(":WAVE:"));
    Check("emote split: the first pack wins a code", set.Find(":wave:")?.Hash == "a");
    Check("emote split: an unknown code stays text", Shown("a :nope: b") == "a :nope: b", Shown("a :nope: b"));
    Check("emote split: never inside a web address", Shown("see https://x.example/:wave:/y ok") == "see https://x.example/:wave:/y ok", Shown("see https://x.example/:wave:/y ok"));
    Check("emote split: after an address, found again", Shown("https://x.example :wave:") == "https://x.example |[a]", Shown("https://x.example :wave:"));
    Check("emote split: a face on its own", Shown("ok :) then") == "ok |[c]| then" && Shown(":)") == "[c]", Shown("ok :) then"));
    Check("emote split: a face inside a word is left", Shown("a:)b") == "a:)b" && Shown(":)x") == ":)x", Shown("a:)b"));
    Check("emote split: a colon that opens nothing", Shown("time 12:30 and :") == "time 12:30 and :", Shown("time 12:30 and :"));
    Check("emote split: no packs, no change", EmoteSet.None.Split("hi :wave:") is [{ Text: "hi :wave:", Emote: null }]);

    var starting = set.StartingWith(":si", 8).Select(e => e.Code).ToList();
    Check("emote list: codes starting with what is typed, in order", string.Join(",", starting) == ":siren:,:sirenspin:", string.Join(",", starting));
    Check("emote list: as many as asked", set.StartingWith(":si", 1).Count == 1);
    Check("emote list: faces are not offered", set.StartingWith(":)", 8).Count == 0);
    Check("emote picker: search on any part of a code", set.Search("AV").Count == 1 && set.Search("nothing").Count == 0 && set.Search(" ").Count == 1);
}

// A real pack on disk: a picture, a moving one, one that is not a picture, one missing and one reaching outside.
static byte[] Png()
{
    using var bitmap = new SkiaSharp.SKBitmap(4, 4);
    bitmap.Erase(SkiaSharp.SKColors.Orange);
    using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
    return image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
}

// One pixel, two frames of 100 ms: red, then blue.
static byte[] Gif() =>
[
    (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 0, 1, 0, 0x80, 0, 0,
    0xFF, 0, 0, 0, 0, 0xFF,
    0x21, 0xFF, 0x0B, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0', 3, 1, 0, 0, 0,
    0x21, 0xF9, 4, 0, 10, 0, 0, 0, 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0,
    0x21, 0xF9, 4, 0, 10, 0, 0, 0, 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x4C, 0x01, 0,
    0x3B,
];

static string ZipPack(string folder, string name, string themeText, bool withPictures = true)
{
    var path = Path.Combine(folder, name);
    using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
    void Put(string entry, byte[] bytes) { using var s = zip.CreateEntry(entry).Open(); s.Write(bytes); }
    if (themeText.Length > 0) Put("pack/theme", Encoding.UTF8.GetBytes(themeText));
    if (withPictures)
    {
        Put("pack/still.png", Png());
        Put("pack/moving.gif", Gif());
        Put("pack/broken.gif", Encoding.UTF8.GetBytes("not a picture"));
        Put("evil.png", Png());
    }
    return path;
}

var emoteDir = Path.Combine(Path.GetTempPath(), $"jabbercheck-emotes-{Guid.NewGuid():N}");
Directory.CreateDirectory(emoteDir);
const string PackTheme = "Name=Check pack\nIcon=still.png\n[default]\nstill.png :still: :wave:\nmoving.gif :moving:\nbroken.gif :broken:\ngone.png :gone:\n../evil.png :evil:\n";
const string OtherTheme = "Name=Check pack no menu\n[default]\n! moving.gif :wave:\n! still.png :still:\n";
var packZip = ZipPack(emoteDir, "pack.zip", PackTheme);
var otherZip = ZipPack(emoteDir, "other.zip", OtherTheme);
{
    var pack = EmotePackReader.Read(packZip);
    Check("emote pack: the pictures that are pictures", pack.Pictures.Keys.Order().SequenceEqual(["moving.gif", "still.png"]), string.Join(",", pack.Pictures.Keys));
    Check("emote pack: a file not there is missing", pack.Missing.SequenceEqual(["gone.png"]), string.Join(",", pack.Missing));
    Check("emote pack: not a picture, or outside the pack, is not used", pack.Unusable.Order().SequenceEqual(["../evil.png", "broken.gif"]), string.Join(",", pack.Unusable));
    Check("emote pack: a GIF's frames are counted", pack.Info["moving.gif"].Frames == 2 && pack.Info["still.png"] is { Width: 4, Height: 4, Frames: 1 });

    var unpacked = Path.Combine(emoteDir, "unpacked");
    System.IO.Compression.ZipFile.ExtractToDirectory(packZip, unpacked);
    var fromFolder = EmotePackReader.Read(Path.Combine(unpacked, "pack"));
    var fromTheme = EmotePackReader.Read(Path.Combine(unpacked, "pack", "theme"));
    Check("emote pack: an unpacked folder reads the same", fromFolder.Pictures.Count == 2 && fromFolder.Missing.Count == 1 && fromFolder.Unusable.Count == 2);
    Check("emote pack: and so does its theme file", fromTheme.Pictures.Count == 2 && fromTheme.Theme.Name == "Check pack");
    Check("emote pack: the folder above is found from one level up", EmotePackReader.Read(unpacked).Pictures.Count == 2);

    var notZip = Path.Combine(emoteDir, "not.zip");
    File.WriteAllText(notZip, "this is not a zip");
    Check("emote pack: not a zip says so", Throws(() => EmotePackReader.Read(notZip)) == JabberText.EmoteNotAPack);
    Check("emote pack: a zip with no theme says so", Throws(() => EmotePackReader.Read(ZipPack(emoteDir, "bare.zip", ""))) == JabberText.EmoteNoTheme);
    Check("emote pack: a picture too big for an emote is refused",
          EmotePackReader.Probe(Png()) is not null && EmotePackReader.Probe(Encoding.UTF8.GetBytes("text")) is null);
}

static string? Throws(Action act)
{
    try { act(); return null; }
    catch (InvalidDataException e) { return e.Message; }
}

var dbPath = Path.Combine(Path.GetTempPath(), $"jabbercheck-{Guid.NewGuid():N}.db");
try
{
    var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").Options;
    var factory = new Factory(options);
    await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();

    var service = new JabberService(factory, new AppErrorLogger(null!));
    var session = new JabberSession(new JabberAccount { Id = 1, Address = "me@example" });

    await using (var db = factory.CreateDbContext())
    {
        db.JabberAccounts.Add(session.Account);
        await db.SaveChangesAsync();
    }

    var stored = new List<JabberStoredMessage>();
    var alerts = 0;
    var unreadChanges = 0;
    service.MessageStored += stored.Add;
    service.AlertArrived += _ => alerts++;
    service.UnreadChanged += () => unreadChanges++;

    XmppMessage From(string who, string id, string body = "hi") =>
        new(XmppAddress.Parse($"{who}/x"), XmppAddress.Parse("me@example"), "chat", body, "", id, null, "", false);

    var first = await service.StoreAsync(session, From("bot@example", "a"));
    Check("store: saved and announced", first is not null && stored.Count == 1 && alerts == 1 && unreadChanges == 1);
    var repeat = await service.StoreAsync(session, From("bot@example", "a"));
    Check("store: the same one again is nothing", repeat is null && stored.Count == 1 && alerts == 1);
    await service.StoreAsync(session, From("bot@example", "b"));
    await service.StoreAsync(session, From("other@example", "c"));

    Check("unread: counted for the alert", await service.UnreadAlertsAsync() == 3);

    var fixedOne = From("other@example", "d", "corrected") with { IsCorrection = true, ReplaceId = "c" };
    var edited = new List<string>();
    service.ConversationEdited += (_, c) => edited.Add(c);
    await service.StoreAsync(session, fixedOne);
    await using (var db = factory.CreateDbContext())
    {
        var rows = await db.JabberMessages.Where(m => m.Conversation == "other@example").ToListAsync();
        Check("correction: changes their last message, adding none", rows.Count == 1 && rows[0].Body == "corrected" && edited.SequenceEqual(["other@example"]), string.Join(",", rows.Select(r => r.Body)));
    }
    await service.StoreAsync(session, From("new@example", "e", "nothing to correct") with { IsCorrection = true, ReplaceId = "z" });
    await using (var db = factory.CreateDbContext())
    {
        Check("correction: with nothing of theirs saved, it stands as a message", await db.JabberMessages.AnyAsync(m => m.Conversation == "new@example"));
        await db.JabberMessages.Where(m => m.Conversation == "new@example").ExecuteDeleteAsync();
    }
    var unread = await service.UnreadAsync();
    Check("unread: by conversation", unread.Single(u => u.Conversation == "bot@example").Count == 2);

    var page = await service.LoadConversationAsync(1, "bot@example");
    Check("history: oldest first", page.Count == 2 && page[0].Id < page[1].Id);
    var earlier = await service.LoadConversationAsync(1, "bot@example", page[1].Id, 10);
    Check("history: paged back", earlier.Count == 1 && earlier[0].Id == page[0].Id);
    var conversations = await service.ConversationsAsync(1);
    Check("history: newest conversation first", conversations[0].Conversation == "other@example" && conversations.Count == 2);

    await service.MarkReadAsync(1, "bot@example");
    Check("read: clears that conversation's alerts", await service.UnreadAlertsAsync() == 1);
    var before = unreadChanges;
    await service.MarkReadAsync(1, "bot@example");
    Check("read: nothing changed, nothing announced", unreadChanges == before);

    // Old read messages go. Unread ones stay, however old.
    await using (var db = factory.CreateDbContext())
    {
        db.JabberMessages.Add(new JabberStoredMessage { AccountId = 1, Conversation = "old@example", SentAt = "2000-01-01T00:00:00Z", IsRead = true, DedupeKey = "old-read" });
        db.JabberMessages.Add(new JabberStoredMessage { AccountId = 1, Conversation = "old@example", SentAt = "2000-01-01T00:00:00Z", IsRead = false, DedupeKey = "old-unread" });
        await db.SaveChangesAsync();
    }
    await service.PurgeOldAsync();
    await using (var db = factory.CreateDbContext())
    {
        var left = await db.JabberMessages.Where(m => m.Conversation == "old@example").Select(m => m.DedupeKey).ToListAsync();
        Check("purge: old read goes, old unread stays", left.SequenceEqual(["old-unread"]), string.Join(",", left));
        Check("purge: recent stays", await db.JabberMessages.CountAsync(m => m.Conversation == "bot@example") == 2);
    }

    await using (var db = factory.CreateDbContext())
    {
        db.AppPreferences.Add(new AppPreference { Key = JabberService.HistoryDaysPref, Value = "0" });
        db.JabberMessages.Add(new JabberStoredMessage { AccountId = 1, Conversation = "old@example", SentAt = "2000-01-01T00:00:00Z", IsRead = true, DedupeKey = "old-kept" });
        await db.SaveChangesAsync();
    }
    await service.PurgeOldAsync();
    await using (var db = factory.CreateDbContext())
        Check("purge: zero keeps everything", await db.JabberMessages.AnyAsync(m => m.DedupeKey == "old-kept"));

    // What the archive holds is often already here, known by whatever it carried when delivered.
    {
        XmppMessage FromArchive(string archiveId, string id, string body, string server = "", DateTimeOffset? sent = null) =>
            new(XmppAddress.Parse("arc@example"), XmppAddress.Parse("me@example"), "chat", body, "", id, sent, server, false) { ArchiveId = archiveId };
        XmppMessage Live(string id, string body, string server = "", DateTimeOffset? sent = null) =>
            new(XmppAddress.Parse("arc@example"), XmppAddress.Parse("me@example"), "chat", body, "", id, sent, server, false);

        await service.StoreAsync(session, Live("L1", "delivered with an id"));
        Check("archive store: delivered with an id is known by it", await service.StoreAsync(session, FromArchive("A1", "L1", "delivered with an id")) is null);

        await service.StoreAsync(session, Live("L2", "delivered with the server's id", "S2"));
        Check("archive store: delivered with the server's id is known by it", await service.StoreAsync(session, FromArchive("S2", "L2", "delivered with the server's id")) is null);

        var moment = DateTimeOffset.UtcNow.AddMinutes(-30);
        await service.StoreAsync(session, Live("", "a bot with no ids", sent: moment));
        Check("archive store: no id, same words at about the same moment", await service.StoreAsync(session, FromArchive("A3", "", "a bot with no ids", sent: moment.AddSeconds(20))) is null);
        Check("archive store: no id, same words hours apart is another message", await service.StoreAsync(session, FromArchive("A4", "", "a bot with no ids", sent: moment.AddHours(-3))) is not null);

        var fresh = await service.StoreAsync(session, FromArchive("A5", "L5", "missed while away"));
        Check("archive store: what was missed is unread and alerts", fresh is { IsRead: false, IsAlert: true, IsOutgoing: false });
        Check("archive store: asking twice keeps one", await service.StoreAsync(session, FromArchive("A5", "L5", "missed while away")) is null);

        await using var cleanup = factory.CreateDbContext();
        await cleanup.JabberMessages.Where(m => m.Conversation == "arc@example").ExecuteDeleteAsync();
    }

    // Rules a hostile member or a strange clock could otherwise get round.
    {
        var outsider = new XmppMessage(XmppAddress.Parse("notjoined@c.example/Nick"), XmppAddress.Parse("me@example"), "groupchat", "me!", "", "g1", null, "", false);
        Check("rooms: talk from a room this account is not in has nowhere to go", await service.StoreAsync(session, outsider) is null);

        var future = await service.StoreAsync(session, From("clock@example", "f1", "from the future") with { Sent = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        Check("dates: one far ahead is taken as now", future is not null && string.CompareOrdinal(future.SentAt, "2090") < 0, future?.SentAt ?? "");

        await service.StoreAsync(session, From("fix@example", "orig", "before"));
        var wrongId = await service.StoreAsync(session, From("fix@example", "x1", "not a fix of that") with { IsCorrection = true, ReplaceId = "other" });
        await using (var db = factory.CreateDbContext())
            Check("correction: naming another message changes nothing, and stands alone", wrongId is not null && await db.JabberMessages.AnyAsync(m => m.Conversation == "fix@example" && m.Body == "before"));

        var replayed = await service.StoreAsync(session, From("fix@example", "x2", "old correction") with { IsCorrection = true, ReplaceId = "gone", Sent = DateTimeOffset.UtcNow.AddHours(-3) });
        Check("correction: an old one in replayed history is not a new message", replayed is null);

        await using (var db = factory.CreateDbContext())
            await db.JabberMessages.Where(m => m.Conversation == "fix@example" || m.Conversation == "clock@example").ExecuteDeleteAsync();

        var pm = JabberService.ToStored(session.Account, null, null,
            new XmppMessage(XmppAddress.Parse("me@example/home"), XmppAddress.Parse("r@c.example/Nick"), "chat", "psst", "", "p1", null, "", true),
            DateTimeOffset.UtcNow, privateInRoom: true);
        Check("rooms: a private word I send stays with that person", pm.Conversation == "r@c.example/Nick" && pm.IsOutgoing, pm.Conversation);
    }

    // ── One connection for every client ───────────────────────────────────────
    // Two services over one file, as a worker and a client over one server. ⚠️ The real thing runs on PostgreSQL, whose
    // notifications this has in place of: what it proves is what each side does with them, not that they arrive.
    {
        var relayProfile = Path.Combine(Path.GetTempPath(), "jabbercheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(relayProfile);
        AppConfig.UseProfile(relayProfile);
        DbEngine.Pin(DbBackend.Sqlite);

        await using (var db = factory.CreateDbContext())
        {
            await db.JabberAccounts.ExecuteUpdateAsync(u => u.SetProperty(a => a.AutoConnect, false));
            await db.JabberMessages.ExecuteDeleteAsync();
        }

        // What the Background Processes tab shows: from what is saved, so the same on any client.
        {
            await using (var seed = factory.CreateDbContext())
            {
                var hour = DateTimeOffset.UtcNow.AddHours(-1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                seed.JabberMessages.AddRange(
                    new JabberStoredMessage { AccountId = 77, Conversation = "busy@rooms.example", IsRoom = true, SentAt = "2000-01-01T00:00:00Z", Sender = "a", DedupeKey = "activity-1" },
                    new JabberStoredMessage { AccountId = 77, Conversation = "busy@rooms.example", IsRoom = true, SentAt = hour, Sender = "a", DedupeKey = "activity-2" },
                    new JabberStoredMessage { AccountId = 77, Conversation = "busy@rooms.example", IsRoom = true, SentAt = hour, Sender = "b", DedupeKey = "activity-3" },
                    new JabberStoredMessage { AccountId = 77, Conversation = "quiet@rooms.example", IsRoom = true, SentAt = "2000-01-02T00:00:00Z", Sender = "a", DedupeKey = "activity-4" },
                    new JabberStoredMessage { AccountId = 77, Conversation = "bob@example", SentAt = hour, Sender = "bob@example", DedupeKey = "activity-5" });
                await seed.SaveChangesAsync();
            }

            var activity = await new JabberService(factory, new AppErrorLogger(null!)).ActivityAsync();
            var busy = activity.Single(a => a.AccountId == 77 && a.Conversation == "busy@rooms.example");
            var quiet = activity.Single(a => a.AccountId == 77 && a.Conversation == "quiet@rooms.example");
            Check("activity: the last message of a room, and the last day's only", busy is { IsRoom: true, LastDay: 2 } && busy.LastAt.CompareTo("2001") > 0);
            Check("activity: a quiet room still says when it last spoke", quiet is { LastDay: 0, LastAt: "2000-01-02T00:00:00Z" });
            Check("activity: direct messages apart from rooms", activity.Single(a => a.AccountId == 77 && a.Conversation == "bob@example") is { IsRoom: false, LastDay: 1 });

            await using var clean = factory.CreateDbContext();
            await clean.JabberMessages.Where(m => m.AccountId == 77).ExecuteDeleteAsync();
        }

        var logger = new AppErrorLogger(null!);
        var signals = new ClientSignals(logger);

        // Everything either side writes to the tables between them, as it is written.
        var spy = new RelaySpy();
        var relayFactory = new Factory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").AddInterceptors(spy).Options);

        var worker = new JabberService(relayFactory, logger, signals) { RelayOnSqlite = true, PickupWait = TimeSpan.FromSeconds(1) };
        var view   = new JabberService(relayFactory, logger, signals) { RelayOnSqlite = true, PickupWait = TimeSpan.FromSeconds(1) };

        var unreadableSignals = 0;
        signals.Received += p =>
        {
            // An older build's alarm path parses whatever it does not know, and logs an error if that fails.
            try { using var _ = System.Text.Json.JsonDocument.Parse(p); } catch (System.Text.Json.JsonException) { Interlocked.Increment(ref unreadableSignals); }
            worker.TryApplySignal(p);
            view.TryApplySignal(p);
        };

        static async Task<bool> Soon(Func<bool> what)
        {
            for (var i = 0; i < 100; i++)
            {
                if (what()) return true;
                await Task.Delay(100);
            }
            return what();
        }

        static async Task<string?> FailureOf(Func<Task> act)
        {
            try { await act(); return null; }
            catch (XmppException e) { return e.Message; }
        }

        await view.StartAsync();
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        // Saved by a client that is not connected, with nobody holding the lease yet.
        var seen = await view.SaveAccountAsync(new JabberAccount { Address = "relay@127.0.0.1", Host = "127.0.0.1", Port = closedPort, AutoConnect = false, Nickname = "Relay" }, "login-secret");
        var relayId = seen.Account.Id;
        Check("relay: a login saved by a client is there for the worker", AppConfig.GetServicePassword(JabberService.PasswordReference(relayId)) == "login-secret");

        var refused = await FailureOf(() => view.SendAsync(seen, "them@example", false, "into the void"));
        Check("relay: nobody to ask, and it says so rather than wait for ever", refused?.Contains("not done", StringComparison.Ordinal) == true, refused ?? "none");
        await using (var db = factory.CreateDbContext())
            Check("relay: and takes it back, so it is not sent minutes later", !await db.JabberCommands.AnyAsync(c => c.Kind == "send"));

        await worker.BecomeWorkerAsync();
        var held = worker.SessionOf(relayId);
        Check("relay: the worker has the account", held is not null);

        var notConnected = await FailureOf(() => view.SendAsync(seen, "them@example", false, "hello"));
        Check("relay: an answer that fails there fails here, in the same words", notConnected == "Not connected.", notConnected ?? "none");

        view.Connect(seen);
        Check("relay: a client asks, and the worker connects", await Soon(() => held!.State is JabberState.Connecting or JabberState.Reconnecting), held!.State.ToString());
        Check("relay: and the client shows how that went", await Soon(() => seen.State is JabberState.Connecting or JabberState.Reconnecting), seen.State.ToString());

        await view.DisconnectAsync(seen);
        Check("relay: and it can ask it to stop", await Soon(() => held!.State == JabberState.Offline && seen.State == JabberState.Offline), $"{held!.State} {seen.State}");

        // What is saved reaches the client, once each, with the sound the worker decided on.
        var shown = new List<JabberStoredMessage>();
        var sounds = new List<long>();
        var unreads = 0;
        view.MessageStored += m => { lock (shown) shown.Add(m); };
        view.AlertArrived += m => { lock (sounds) sounds.Add(m.Id); };
        view.UnreadChanged += () => Interlocked.Increment(ref unreads);

        XmppMessage ToRelay(string who, string id, string body = "hi") =>
            new(XmppAddress.Parse($"{who}/x"), XmppAddress.Parse("relay@127.0.0.1"), "chat", body, "", id, null, "", false);

        var dm = await worker.StoreAsync(held!, ToRelay("bot@example", "r1", "a broadcast"));
        Check("relay: a saved message reaches the client", await Soon(() => { lock (shown) return shown.Any(m => m.Id == dm!.Id); }));
        Check("relay: with its sound", await Soon(() => { lock (sounds) return sounds.SequenceEqual([dm!.Id]); }), string.Join(",", sounds));
        Check("relay: and its count", await Soon(() => Volatile.Read(ref unreads) > 0));

        await using (var db = factory.CreateDbContext())
            await db.JabberAccounts.Where(a => a.Id == relayId).ExecuteUpdateAsync(u => u.SetProperty(a => a.AutoConnect, false));

        var burst = new List<long>();
        for (var i = 0; i < 120; i++)
            burst.Add((await worker.StoreAsync(held!, ToRelay($"p{i % 7}@example", $"b{i}", $"line {i}")))!.Id);
        Check("relay: a burst, all of it", await Soon(() => { lock (shown) return burst.All(id => shown.Any(m => m.Id == id)); }));
        List<long> order;
        lock (shown) order = [.. shown.Select(m => m.Id)];
        Check("relay: each once, in order", order.Distinct().Count() == order.Count && order.SequenceEqual(order.Order()));

        var corrections = 0;
        view.ConversationEdited += (_, _) => Interlocked.Increment(ref corrections);
        await worker.StoreAsync(held!, ToRelay("bot@example", "r2", "first words"));
        await worker.StoreAsync(held!, ToRelay("bot@example", "r3", "better words") with { IsCorrection = true, ReplaceId = "r2" });
        Check("relay: a correction reaches the client", await Soon(() => Volatile.Read(ref corrections) == 1));

        var workerUnreads = 0;
        worker.UnreadChanged += () => Interlocked.Increment(ref workerUnreads);
        await view.MarkReadAsync(relayId, "bot@example");
        Check("relay: reading on one client is seen by the others", await Soon(() => Volatile.Read(ref workerUnreads) > 0));

        // Rooms: the room and its password are saved first, so the password is never in what is asked.
        var room = await view.JoinRoomAsync(seen, "room@c.example", "A Room", "Nick", "room-secret");
        Check("relay: a room's password is there for the worker", AppConfig.GetServicePassword(JabberService.RoomPasswordReference(room.Id)) == "room-secret");
        Check("relay: the worker has the room", await Soon(() => held!.Rooms.Any(r => r.Room.RoomJid == "room@c.example")));
        await Task.Delay(1500);
        Check("relay: and it is still shown once the worker has had its say", seen.Rooms.Any(r => r.Room.Name == "A Room"));

        var intelChanges = 0;
        view.IntelRoomsChanged += () => Interlocked.Increment(ref intelChanges);
        await view.SetRoomTypeAsync(seen, "room@c.example", JabberRoomType.Intel);
        Check("relay: a room's type reaches the connection", await Soon(() => held!.Rooms.Any(r => r.Room.RoomJid == "room@c.example" && r.Room.RoomType == JabberRoomType.Intel)));
        Check("relay: and tells the clients", await Soon(() => Volatile.Read(ref intelChanges) > 0));
        await view.SetRoomAlertAsync(seen, "room@c.example", JabberRoomAlert.All);
        Check("relay: and its alert setting", await Soon(() => held!.Rooms.Any(r => r.Room.RoomJid == "room@c.example" && r.Room.AlertMode == JabberRoomAlert.All)));

        await view.SetPresenceAsync(seen, XmppShow.Away, "back soon");
        Check("relay: a status chosen on a client reaches the connection", await Soon(() => held!.Show == XmppShow.Away && held.Status == "back soon"));

        // Accounts.
        var added = await view.SaveAccountAsync(new JabberAccount { Address = "more@127.0.0.1", Host = "127.0.0.1", Port = closedPort, AutoConnect = false }, "more-secret");
        Check("relay: a new account reaches the worker", await Soon(() => worker.SessionOf(added.Account.Id) is not null));
        added.Account.Name = "Renamed";
        await view.SaveAccountAsync(added.Account, null);
        Check("relay: a change to one does", await Soon(() => worker.SessionOf(added.Account.Id)?.Account.Name == "Renamed"));
        await view.DeleteAccountAsync(added.Account.Id);
        Check("relay: and removing one does", await Soon(() => worker.SessionOf(added.Account.Id) is null));

        // ⚠️ As written, not what is left in the file: a request is deleted once answered, and its place soon reused.
        await Task.Delay(1500);
        Check("relay: something did go through the tables between them", spy.Written.Any(w => w.Contains("room@c.example", StringComparison.Ordinal)));
        Check("relay: no login or room password went through them",
              !spy.Written.Any(w => w.Contains("room-secret", StringComparison.Ordinal) || w.Contains("login-secret", StringComparison.Ordinal)));
        Check("relay: every signal is something an older build can read", Volatile.Read(ref unreadableSignals) == 0);

        // The lease moves.
        await worker.StandDownAsync();
        await using (var db = factory.CreateDbContext())
            Check("relay: standing down leaves nothing saying it is connected", !await db.JabberLiveStates.AnyAsync());
        Check("relay: and the client shows it", await Soon(() => seen.State == JabberState.Offline && seen.Rooms.Count == 0), $"{seen.State} {seen.Rooms.Count}");

        var shownBefore = shown.Count;
        await view.BecomeWorkerAsync();
        var nowHere = await FailureOf(() => view.SendAsync(view.SessionOf(relayId)!, "them@example", false, "hello"));
        Check("relay: the next holder does it itself", nowHere == "Not connected.", nowHere ?? "none");
        await view.SetPresenceAsync(view.SessionOf(relayId)!, XmppShow.DoNotDisturb, "");
        Check("relay: and the old one now asks it", await Soon(() => worker.SessionOf(relayId)?.Show == XmppShow.DoNotDisturb), worker.SessionOf(relayId)?.Show.ToString() ?? "none");
        var askedNow = await FailureOf(() => worker.SendAsync(worker.SessionOf(relayId)!, "them@example", false, "hello"));
        Check("relay: and its requests go to the new one", askedNow == "Not connected.", askedNow ?? "none");

        var newRow = await view.StoreAsync(view.SessionOf(relayId)!, ToRelay("bot@example", "n1", "under new management"));
        var oldSaw = new List<long>();
        worker.MessageStored += m => { lock (oldSaw) oldSaw.Add(m.Id); };
        var more = await view.StoreAsync(view.SessionOf(relayId)!, ToRelay("bot@example", "n2", "and more"));
        Check("relay: the old one is told of what the new one saves", await Soon(() => { lock (oldSaw) return oldSaw.Contains(more!.Id); }));
        Check("relay: and the new one does not tell itself twice", shown.Count == shownBefore + 2, $"{shown.Count} from {shownBefore}");
        _ = newRow;

        await view.StopAsync();
        await worker.StopAsync();
        try { Directory.Delete(relayProfile, recursive: true); } catch (IOException) { /* a temp folder */ }
    }

    // ── Emote packs in the database: each server its own ────────────────────
    {
        var emotes = new EmoteService(factory, new AppErrorLogger(null!));
        async Task<int> Count<T>(Func<AppDbContext, IQueryable<T>> of) { await using var db = factory.CreateDbContext(); return await of(db).CountAsync(); }

        var loaded = await emotes.ImportAsync(packZip, 1);
        Check("emote db: a pack loaded for a server", loaded is { Name: "Check pack", Codes: 3, Pictures: 2, Missing: 1, Unusable: 2, Replaced: false }, loaded.ToString());
        Check("emote db: that server reads with it", emotes.For(1).Find(":moving:") is { Animated: true } && emotes.For(1).Find(":wave:") is { Width: 4 });
        Check("emote db: another server does not", emotes.For(2).IsEmpty);
        Check("emote db: one picture for each, not for each code", emotes.For(1).Pictures.Count == 2 && emotes.For(1).Pictures.Any(p => p.Codes.SequenceEqual([":still:", ":wave:"])));

        var again = await emotes.ImportAsync(packZip, 1);
        Check("emote db: loading it again replaces it", again.Replaced && again.PackId == loaded.PackId && await Count(db => db.EmotePacks) == 1 && await Count(db => db.EmoteCodes) == 3);

        var other = await emotes.ImportAsync(otherZip, 2);
        Check("emote db: a second pack with the same pictures keeps one copy of each", await Count(db => db.EmoteImages) == 2 && other.PackId != loaded.PackId);
        Check("emote db: and its hidden lines are read", emotes.For(2).Find(":wave:") is { Animated: true });

        await emotes.AddToAsync(2, loaded.PackId);
        Check("emote db: a loaded pack given to a second server, after its own", emotes.For(2).Find(":wave:") is { Animated: true } && emotes.For(2).Find(":moving:") is not null);
        await emotes.MoveAsync(2, loaded.PackId, -1);
        Check("emote db: moved up, its code wins", emotes.For(2).Find(":wave:") is { Animated: false });
        Check("emote db: the loaded server is untouched", (await emotes.PackIdsOfAsync(1)).SequenceEqual([loaded.PackId]) && emotes.For(1).Find(":wave:") is { Animated: false });

        var packs = await emotes.PacksAsync();
        Check("emote db: the list says which servers have each", packs.First(p => p.Id == loaded.PackId).AccountIds.Order().SequenceEqual([1, 2]) && packs.First(p => p.Id == loaded.PackId).Pictures == 2);

        Check("emote db: taken off one server, kept for the other", !await emotes.RemoveFromAsync(1, loaded.PackId) && emotes.For(1).IsEmpty && emotes.For(2).Find(":moving:") is not null);
        Check("emote db: taken off the last, it is deleted", await emotes.RemoveFromAsync(2, loaded.PackId) && await Count(db => db.EmotePacks) == 1);
        Check("emote db: and pictures another pack has are kept", await Count(db => db.EmoteImages) == 2);

        // What deleting a server does to its packs. ⚠️ Not by DeleteAccountAsync itself: that also clears a saved password.
        await using (var db = factory.CreateDbContext())
        {
            await db.JabberEmotePacks.Where(l => l.AccountId == 2).ExecuteDeleteAsync();
            await EmoteService.DeleteUnusedAsync(db);
        }
        Check("emote db: a server deleted takes its packs and pictures with it",
              await Count(db => db.EmotePacks) == 0 && await Count(db => db.EmoteCodes) == 0 && await Count(db => db.EmoteImages) == 0 && await Count(db => db.JabberEmotePacks) == 0);

        var bad = Path.Combine(emoteDir, "nothing.zip");
        ZipPack(emoteDir, "nothing.zip", "Name=Empty\n[default]\ngone.png :gone:\n", withPictures: false);
        Check("emote db: a pack with no usable picture loads nothing", await FailsAsync(() => emotes.ImportAsync(bad, 1)) == JabberText.EmoteNoPictures && await Count(db => db.EmotePacks) == 0);
    }

    var schema = EveConsole.Agent.AgentSchema.Build(factory.CreateDbContext());
    Check("agent: Jabber's tables are kept from the in-app agent",
          !schema.TableNames.Any(t => t.StartsWith("Jabber", StringComparison.OrdinalIgnoreCase)) && schema.TableNames.Count > 0);
    Check("agent: and so are the emote packs", !schema.TableNames.Any(t => t.StartsWith("Emote", StringComparison.OrdinalIgnoreCase)));
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { File.Delete(dbPath); } catch (IOException) { /* a temp file, and the result is what matters */ }
    try { Directory.Delete(emoteDir, recursive: true); } catch (IOException) { /* the same */ }
}

static async Task<string?> FailsAsync(Func<Task> act)
{
    try { await act(); return null; }
    catch (InvalidDataException e) { return e.Message; }
}

Console.WriteLine($"Jabber check: {checks} check(s), {failures.Count} failure(s).");
foreach (var f in failures) Console.WriteLine(f);
return failures.Count == 0 ? 0 : 1;

sealed class Factory(DbContextOptions<AppDbContext> o) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(o);
}

sealed class RelaySpy : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _written = new();

    public IEnumerable<string> Written => _written;

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
        Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        foreach (var command in eventData.Context!.ChangeTracker.Entries<JabberCommand>()) _written.Enqueue(command.Entity.Args);
        foreach (var live in eventData.Context.ChangeTracker.Entries<JabberLiveState>()) _written.Enqueue(live.Entity.Json);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
