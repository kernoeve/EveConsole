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

// ── Rooms saved the newer way (XEP-0402) ───────────────────────────────────
{
    XElement Item(string id, XElement? room) => new(XmppClient.PubSub + "item", new XAttribute("id", id), room);
    var answer = new XElement(XmppClient.Client + "iq", new XElement(XmppClient.PubSub + "pubsub",
        new XElement(XmppClient.PubSub + "items", new XAttribute("node", XmppClient.Bookmarks2.NamespaceName),
            Item("ops@rooms.example", new XElement(XmppClient.Bookmarks2 + "conference", new XAttribute("name", "Operations"), new XAttribute("autojoin", "true"),
                new XElement(XmppClient.Bookmarks2 + "nick", "Pilot"), new XElement(XmppClient.Bookmarks2 + "password", "secret"))),
            Item("quiet@rooms.example/ignored", new XElement(XmppClient.Bookmarks2 + "conference")),
            Item("", new XElement(XmppClient.Bookmarks2 + "conference", new XAttribute("name", "no address"))),
            Item("other@rooms.example", new XElement(XmppClient.Bookmarks + "conference", new XAttribute("name", "the old kind, misplaced"))))));
    var saved = XmppClient.ParseNewBookmarks(answer);
    Check("new bookmarks: name, joining, nickname and password",
          saved.Count == 2 && saved[0] == new XmppBookmark("ops@rooms.example", "Operations", "Pilot", true, "secret"), string.Join(";", saved));
    Check("new bookmarks: a room by its bare address, not joining unless it says", saved[1] == new XmppBookmark("quiet@rooms.example", "", "", false, ""));
    Check("new bookmarks: none kept at all", XmppClient.ParseNewBookmarks(new XElement(XmppClient.Client + "iq")).Count == 0);

    // Written back: what is read again is what was meant, and nothing else in the older list is lost.
    var room = new XmppBookmark("ops@rooms.example", "Operations", "Pilot", true, "secret");
    var publish = XmppClient.NewBookmarkPublish(room, withOptions: true);
    var asRead = XmppClient.ParseNewBookmarks(new XElement(XmppClient.Client + "iq", new XElement(XmppClient.PubSub + "pubsub",
        new XElement(XmppClient.PubSub + "items", publish.Element(XmppClient.PubSub + "publish")!.Elements()))));
    Check("bookmark write: reads back as it was meant", asRead.SequenceEqual([room]), string.Join(";", asRead));
    var options = publish.Element(XmppClient.PubSub + "publish-options")?.Descendants(XmppClient.DataForm + "field")
        .ToDictionary(f => (string?)f.Attribute("var") ?? "", f => f.Element(XmppClient.DataForm + "value")?.Value ?? "");
    Check("bookmark write: kept, and readable only by this account", options is not null && options["pubsub#persist_items"] == "true" && options["pubsub#access_model"] == "whitelist");
    Check("bookmark write: without options for a server that will not take them", XmppClient.NewBookmarkPublish(room, withOptions: false).Element(XmppClient.PubSub + "publish-options") is null);
    Check("bookmark write: nothing written that is not known",
          XmppClient.NewBookmarkItem(new XmppBookmark("q@rooms.example", "", "", false, "")).ToString(SaveOptions.DisableFormatting).Contains("<nick") is false);

    var storage = new XElement(XmppClient.Bookmarks + "storage",
        new XElement(XmppClient.Bookmarks + "conference", new XAttribute("jid", "ops@rooms.example"), new XAttribute("autojoin", "true"), new XElement(XmppClient.Bookmarks + "nick", "Old")),
        new XElement(XmppClient.Bookmarks + "conference", new XAttribute("jid", "other@rooms.example"), new XAttribute("name", "Someone else's")),
        new XElement(XmppClient.Bookmarks + "url", new XAttribute("url", "https://example.test"), new XAttribute("name", "a link")));
    var left = XmppClient.WithOldBookmark(storage, room with { AutoJoin = false });
    var rooms = left.Elements(XmppClient.Bookmarks + "conference").ToList();
    Check("old bookmarks: the room changed in place, once",
          rooms.Count(c => (string?)c.Attribute("jid") == "ops@rooms.example") == 1
          && rooms.Single(c => (string?)c.Attribute("jid") == "ops@rooms.example") is var ops && (string?)ops.Attribute("autojoin") == "false" && ops.Element(XmppClient.Bookmarks + "nick")?.Value == "Pilot");
    Check("old bookmarks: other rooms and links are kept", rooms.Any(c => (string?)c.Attribute("jid") == "other@rooms.example") && left.Element(XmppClient.Bookmarks + "url") is not null);
    Check("old bookmarks: the list read is not changed itself", storage.Elements(XmppClient.Bookmarks + "conference").First().Element(XmppClient.Bookmarks + "nick")?.Value == "Old");
    Check("old bookmarks: none yet, one now", XmppClient.WithOldBookmark(null, room).Elements(XmppClient.Bookmarks + "conference").Count() == 1);
}

// ── Pictures: what presence says, and what a vCard holds ──────────────────
{
    XElement Presence(params object[] inside) => new(XmppClient.Client + "presence", new XAttribute("from", "them@example/x"), inside);
    XElement Update(XElement? photo) => new(XmppClient.VCardUpdate + "x", photo);

    Check("picture: the hash a presence names", XmppClient.PhotoHashIn(Presence(Update(new XElement(XmppClient.VCardUpdate + "photo", " ABCDEF0123 ")))) == "abcdef0123");
    Check("picture: an empty one says there is none", XmppClient.PhotoHashIn(Presence(Update(new XElement(XmppClient.VCardUpdate + "photo")))) == "");
    Check("picture: not said, unknown", XmppClient.PhotoHashIn(Presence(Update(null))) is null && XmppClient.PhotoHashIn(Presence()) is null);
    Check("picture: not a hash, not used", XmppClient.PhotoHashIn(Presence(Update(new XElement(XmppClient.VCardUpdate + "photo", "../../etc")))) is null);

    var card = new XElement(XmppClient.Client + "iq", new XElement(XmppClient.VCard + "vCard",
        new XElement(XmppClient.VCard + "PHOTO", new XElement(XmppClient.VCard + "TYPE", "image/png"),
            new XElement(XmppClient.VCard + "BINVAL", "AQID\nBAU="))));
    Check("picture: from a vCard, its lines joined", XmppClient.ParseVCardPhoto(card) is [1, 2, 3, 4, 5]);
    Check("picture: a vCard with none", XmppClient.ParseVCardPhoto(new XElement(XmppClient.Client + "iq", new XElement(XmppClient.VCard + "vCard"))) is null);
    Check("picture: one that is not base64 is none", XmppClient.ParseVCardPhoto(new XElement(XmppClient.Client + "iq", new XElement(XmppClient.VCard + "vCard",
        new XElement(XmppClient.VCard + "PHOTO", new XElement(XmppClient.VCard + "BINVAL", "not base64!"))))) is null);

    Check("initials: the first letter, in capitals", AvatarService.Initial("  bob talkalot") == "B" && AvatarService.Initial("[TAG] carl") == "T" && AvatarService.Initial("--") == "?");
    Check("initials: the same colour for the same name, whatever its case", AvatarService.ColorIndex("Bob", 8) == AvatarService.ColorIndex(" bob ", 8));
    Check("pictures: filed under the Messages tool's names",
          JabberService.AvatarServiceName == EveConsole.Services.Messages.JabberDmSource.Name
          && EveConsole.Services.Mumble.MumbleService.AvatarServiceName == EveConsole.Services.Messages.MumbleDmSource.Name);
}

// ── Reactions: what is read and what is sent ──────────────────────────────
{
    XElement With(string? id, params string[] emojis) =>
        new(XmppClient.Client + "message", new XElement(XmppClient.Reactions + "reactions", id is null ? null : new XAttribute("id", id),
            emojis.Select(e => new XElement(XmppClient.Reactions + "reaction", e))));

    var read = XmppClient.ReactionsIn(With("m1", "👍", "🔥", "👍"));
    Check("reactions: the set, each once, in order", read is ("m1", ["👍", "🔥"]), read is { } r ? string.Join(",", r.Emojis) : "null");
    Check("reactions: an empty set takes them back", XmppClient.ReactionsIn(With("m1")) is ("m1", []));
    Check("reactions: none without what they react to", XmppClient.ReactionsIn(With(null, "👍")) is null && XmppClient.ReactionsIn(new XElement(XmppClient.Client + "message")) is null);
    Check("reactions: words, spaces and long strings are not emojis",
          XmppClient.ReactionsIn(With("m1", "hello", "👍 👍", string.Concat(Enumerable.Repeat("😀", 20)), "", "❤️")) is ("m1", ["❤️"]));
    Check("reactions: only so many", XmppClient.ReactionsIn(With("m1", [.. Enumerable.Range(0, 40).Select(i => char.ConvertFromUtf32(0x1F600 + i))]))?.Emojis.Count == XmppClient.MostReactions);

    var sent = XmppClient.ReactionsMessage("ops@rooms.example", "groupchat", "S1", ["👍", "🔥"]);
    Check("reactions: sent as the whole set, kept by the server",
          (string?)sent.Attribute("type") == "groupchat" && XmppClient.ReactionsIn(sent) is ("S1", ["👍", "🔥"]) && sent.Element(XmppClient.Hints + "store") is not null);
    var echoed = new XElement(sent);
    echoed.SetAttributeValue("from", "ops@rooms.example/alice");
    Check("reactions: what is sent is read back as a reaction, not a message",
          XmppClient.ParseMessage(echoed, XmppAddress.Parse("me@example/here")) is { ReactionsTo: "S1", Body.Length: 0 });
}

// ── Deletes: every form they come in ──────────────────────────────────────
{
    var self = XmppAddress.Parse("me@example/here");
    XElement Stanza(string from, string type, params object[] inside) =>
        new(XmppClient.Client + "message", new XAttribute("from", from), new XAttribute("to", "me@example/here"), new XAttribute("type", type),
            new XAttribute("id", "r1"), inside);

    var own = XmppClient.ParseMessage(Stanza("them@example/x", "chat",
        new XElement(XmppClient.Retract + "retract", new XAttribute("id", "m1")),
        new XElement(XmppClient.Client + "body", "/me retracted a previous message, but it's unsupported by your client.")), self);
    Check("delete: the sender's own, as sent now", own is { RetractId: "m1", RetractByModerator: false }, own?.ToString() ?? "null");

    var bare = XmppClient.ParseMessage(Stanza("them@example/x", "chat", new XElement(XmppClient.Retract + "retract", new XAttribute("id", "m2"))), self);
    Check("delete: with no words for older clients still counts", bare is { RetractId: "m2", Body.Length: 0 });

    var moderated = XmppClient.ParseMessage(Stanza("ops@rooms.example", "groupchat",
        new XElement(XmppClient.Retract + "retract", new XAttribute("id", "s1"),
            new XElement(XmppClient.Moderate + "moderated", new XAttribute("by", "ops@rooms.example/boss")),
            new XElement(XmppClient.Retract + "reason", "off topic"))), self);
    Check("delete: a moderator's, with the reason", moderated is { RetractId: "s1", RetractByModerator: true, RetractReason: "off topic" }, moderated?.ToString() ?? "null");

    var older = XmppClient.ParseMessage(Stanza("them@example/x", "chat",
        new XElement(XmppClient.Fasten + "apply-to", new XAttribute("id", "m3"), new XElement(XmppClient.OldRetract + "retract"))), self);
    Check("delete: the older form, the sender's", older is { RetractId: "m3", RetractByModerator: false });

    var olderModerated = XmppClient.ParseMessage(Stanza("ops@rooms.example", "groupchat",
        new XElement(XmppClient.Fasten + "apply-to", new XAttribute("id", "s2"),
            new XElement(XmppClient.OldModerate + "moderated", new XAttribute("by", "ops@rooms.example/boss"),
                new XElement(XmppClient.OldRetract + "retract"), new XElement(XmppClient.OldModerate + "reason", "spam")))), self);
    Check("delete: the older form, a moderator's", olderModerated is { RetractId: "s2", RetractByModerator: true, RetractReason: "spam" });

    Check("delete: an ordinary message is none", XmppClient.ParseMessage(Stanza("them@example/x", "chat", new XElement(XmppClient.Client + "body", "hi")), self) is { RetractId.Length: 0 });

    var roomSaid = XmppClient.ParseMessage(Stanza("ops@rooms.example/someone", "groupchat", new XElement(XmppClient.Client + "body", "hi"),
        new XElement(XmppClient.StanzaId + "stanza-id", new XAttribute("by", "someone@example"), new XAttribute("id", "forged")),
        new XElement(XmppClient.StanzaId + "stanza-id", new XAttribute("by", "ops@rooms.example"), new XAttribute("id", "real"))), self);
    Check("delete: a room message's id is the one the room gave, not one put in by its sender", roomSaid is { RoomStanzaId: "real" }, roomSaid?.RoomStanzaId ?? "null");
}

// ── Running a room: who may do what, and what is sent for it ──────────────
{
    XmppOccupant P(string nick, string role, string affiliation, string jid = "", bool self = false) =>
        new("r@rooms.example", nick, XmppShow.Online, role, affiliation, jid, self, false, "");

    var owner = P("me", "moderator", "owner", "me@example", self: true);
    var admin = P("me", "moderator", "admin", "me@example", self: true);
    var mod = P("me", "moderator", "none", "me@example", self: true);
    var plain = P("me", "participant", "member", "me@example", self: true);

    var guest = P("guest", "participant", "none", "guest@example");
    var hidden = P("hidden", "participant", "none");
    var visitor = P("quiet", "visitor", "none", "quiet@example");
    var member = P("member", "participant", "member", "member@example");
    var otherAdmin = P("boss", "moderator", "admin", "boss@example");
    var otherOwner = P("top", "moderator", "owner", "top@example");
    var memberMod = P("helper", "moderator", "member", "helper@example");

    Check("room rights: nothing over yourself", !JabberRoomRights.Over(owner, owner).Any && !JabberRoomRights.Over(owner, P("me", "moderator", "owner")).Any);
    Check("room rights: nothing without a place in the room", !JabberRoomRights.Over(null, guest).Any);
    Check("room rights: an ordinary member can do nothing to anyone", !JabberRoomRights.Over(plain, guest).Any && !JabberRoomRights.Over(plain, visitor).Any);

    var m = JabberRoomRights.Over(mod, guest);
    Check("room rights: a moderator kicks and takes voice, no more", m is { Kick: true, TakeVoice: true, GiveVoice: false, Ban: false, MakeMember: false, MakeModerator: false }, m.ToString());
    Check("room rights: a moderator gives a visitor voice", JabberRoomRights.Over(mod, visitor) is { GiveVoice: true, TakeVoice: false });
    Check("room rights: no moderator touches an admin or owner", !JabberRoomRights.Over(mod, otherAdmin).Kick && !JabberRoomRights.Over(admin, otherOwner).Kick && !JabberRoomRights.Over(owner, otherAdmin).Kick);

    var a = JabberRoomRights.Over(admin, guest);
    Check("room rights: an admin bans, gives membership and moderation", a is { Ban: true, MakeMember: true, MakeModerator: true, MakeAdmin: false }, a.ToString());
    Check("room rights: an admin cannot ban another admin or an owner", !JabberRoomRights.Over(admin, otherAdmin).Ban && !JabberRoomRights.Over(admin, otherOwner).Ban);
    Check("room rights: membership taken back, not given twice", JabberRoomRights.Over(admin, member) is { MakeMember: false, RemoveMember: true });
    Check("room rights: a member made moderator can be put back", JabberRoomRights.Over(admin, memberMod) is { RemoveModerator: true, MakeModerator: false });
    Check("room rights: an admin's moderation is theirs by right", !JabberRoomRights.Over(owner, otherAdmin).RemoveModerator);
    Check("room rights: only an owner makes and unmakes admins",
          JabberRoomRights.Over(owner, guest).MakeAdmin && JabberRoomRights.Over(owner, otherAdmin) is { RemoveAdmin: true, Ban: true } && !JabberRoomRights.Over(admin, guest).MakeAdmin);
    Check("room rights: no address shown, nothing that needs one",
          JabberRoomRights.Over(owner, hidden) is { Kick: true, Ban: false, MakeMember: false, MakeAdmin: false });

    Check("room rights: the room's own lines",
          JabberRoomRights.OfRoom(mod) is { ChangeTopic: true, Invite: true, LiftBan: false }
          && JabberRoomRights.OfRoom(plain) is { ChangeTopic: false, Invite: true, LiftBan: false }
          && JabberRoomRights.OfRoom(admin) is { LiftBan: true } && JabberRoomRights.OfRoom(null) is { Invite: false });

    var kick = XmppClient.RoleQuery("guest", "none", "spam");
    var item = kick.Element(XmppClient.MucAdmin + "item");
    Check("room admin: a kick is role none by nickname, with the reason",
          kick.Name == XmppClient.MucAdmin + "query" && (string?)item?.Attribute("nick") == "guest" && (string?)item?.Attribute("role") == "none"
          && item?.Element(XmppClient.MucAdmin + "reason")?.Value == "spam", kick.ToString());
    var ban = XmppClient.AffiliationQuery("Guest@Example/phone", "outcast", "");
    Check("room admin: a ban is of the bare address, no empty reason sent",
          (string?)ban.Element(XmppClient.MucAdmin + "item")?.Attribute("jid") == "guest@example"
          && (string?)ban.Element(XmppClient.MucAdmin + "item")?.Attribute("affiliation") == "outcast"
          && ban.Element(XmppClient.MucAdmin + "item")?.Element(XmppClient.MucAdmin + "reason") is null, ban.ToString());
    var topic = XmppClient.SubjectMessage("r@rooms.example", "Ops at 20:00");
    Check("room admin: a topic is a room message with a subject and no body",
          (string?)topic.Attribute("type") == "groupchat" && topic.Element(XmppClient.Client + "subject")?.Value == "Ops at 20:00" && topic.Element(XmppClient.Client + "body") is null);
    var invite = XmppClient.InviteMessage("r@rooms.example", "friend@example/pc", "come along");
    var through = invite.Element(XmppClient.MucUser + "x")?.Element(XmppClient.MucUser + "invite");
    Check("room admin: an invitation goes through the room, to the bare address",
          (string?)invite.Attribute("to") == "r@rooms.example" && (string?)through?.Attribute("to") == "friend@example"
          && through?.Element(XmppClient.MucUser + "reason")?.Value == "come along", invite.ToString());

    var answer = new XElement(XmppClient.Client + "iq", new XElement(XmppClient.MucAdmin + "query",
        new XElement(XmppClient.MucAdmin + "item", new XAttribute("affiliation", "outcast"), new XAttribute("jid", "spammer@example"),
            new XElement(XmppClient.MucAdmin + "reason", "spam")),
        new XElement(XmppClient.MucAdmin + "item", new XAttribute("affiliation", "outcast"), new XAttribute("jid", "other@example")),
        new XElement(XmppClient.MucAdmin + "item", new XAttribute("affiliation", "outcast"))));
    var bans = XmppClient.ParseBans(answer);
    Check("room admin: the ban list, with reasons, nothing without an address",
          bans.Count == 2 && bans[0] == new XmppBan("spammer@example", "spam") && bans[1] == new XmppBan("other@example", ""), string.Join(";", bans));
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

    // Reactions: kept per person per message, counted, and only for messages kept here.
    {
        var changed = new List<string>();
        service.ReactionsChanged += (_, c) => changed.Add(c);
        XmppMessage To(string who, string id, string body) =>
            new(XmppAddress.Parse($"{who}/x"), XmppAddress.Parse("me@example"), "chat", body, "", id, null, "", false);
        XmppMessage Reacting(string who, string target, params string[] emojis) =>
            To(who, "re" + Guid.NewGuid().ToString("N")[..6], "") with { ReactionsTo = target, Reactions = emojis };

        await service.StoreAsync(session, To("fan@example", "r1", "react to this"));
        Check("reactions: not a message of their own", await service.StoreAsync(session, Reacting("fan@example", "r1", "👍", "🔥")) is null);
        var mine = new XmppMessage(XmppAddress.Parse("me@example/other"), XmppAddress.Parse("fan@example"), "chat", "", "", "mr1", null, "", true)
            { ReactionsTo = "r1", Reactions = ["👍"] };
        await service.StoreAsync(session, mine);
        await service.StoreAsync(session, Reacting("fan@example", "nothing-here", "😂"));

        var counts = await service.ReactionsAsync(1, "fan@example", ["r1", "nothing-here"], "me@example");
        string Shown(Dictionary<string, List<JabberReactionCount>> c, string t) =>
            string.Join(" ", c.GetValueOrDefault(t, []).Select(x => $"{x.Emoji}{x.Count}{(x.Mine ? "*" : "")}").Order(StringComparer.Ordinal));
        Check("reactions: counted, this account's own marked", Shown(counts, "r1") == string.Join(" ", new[] { "👍2*", "🔥1" }.Order(StringComparer.Ordinal)), Shown(counts, "r1"));
        Check("reactions: to a message not kept here, not kept", !counts.ContainsKey("nothing-here"));
        Check("reactions: the screens are told", changed.Count(c => c == "fan@example") == 2, string.Join(",", changed));

        await service.StoreAsync(session, Reacting("fan@example", "r1", "😮"));
        Check("reactions: a new set replaces the old", Shown(await service.ReactionsAsync(1, "fan@example", ["r1"], "me@example"), "r1") == string.Join(" ", new[] { "👍1*", "😮1" }.Order(StringComparer.Ordinal)));
        await service.StoreAsync(session, Reacting("fan@example", "r1"));
        await service.StoreAsync(session, mine with { Reactions = [] });
        Check("reactions: an empty set takes them back", (await service.ReactionsAsync(1, "fan@example", ["r1"], "me@example")).Count == 0);

        const string Hall = "hall@rooms.example";
        session.Locked(s => s.RoomMap[Hall] = new JabberRoomState(new JabberRoom { AccountId = 1, RoomJid = Hall }) { Nick = "me", HistoryDone = true });
        XmppMessage Said(string nick, string id, string body, string stanza = "") =>
            new XmppMessage(XmppAddress.Parse($"{Hall}/{nick}"), XmppAddress.Parse("me@example"), "groupchat", body, "", id, null, stanza, false) { RoomStanzaId = stanza };
        await service.StoreAsync(session, Said("alice", "a9", "room words", "S9"));
        await service.StoreAsync(session, Said("bob", "b9", "") with { ReactionsTo = "S9", Reactions = ["🎉"] });
        await service.StoreAsync(session, Said("me", "m9", "") with { ReactionsTo = "S9", Reactions = ["🎉"] });
        await service.StoreAsync(session, Said("carl", "c9", "") with { ReactionsTo = "a9", Reactions = ["😂"] });
        var room = await service.ReactionsAsync(1, Hall, ["S9", "a9"], "me");
        Check("room reactions: by the room's id for the message, this account's own by its nickname", Shown(room, "S9") == "🎉2*", Shown(room, "S9"));
        Check("room reactions: not by the sender's own id", !room.ContainsKey("a9"));

        await using (var db = factory.CreateDbContext())
            await db.JabberMessages.Where(m => m.Conversation == Hall || m.Conversation == "fan@example").ExecuteDeleteAsync();
        await service.PurgeOldAsync();
        await using (var db = factory.CreateDbContext())
            Check("reactions: go with the messages they react to", !await db.JabberReactions.AnyAsync(r => r.Conversation == Hall));
        session.Locked(s => s.RoomMap.Remove(Hall));
    }

    // Deletes: honoured only from whoever may, and the message keeps its place.
    {
        XmppMessage Said(string who, string id, string body) =>
            new(XmppAddress.Parse($"{who}/x"), XmppAddress.Parse("me@example"), "chat", body, "", id, null, "", false);
        XmppMessage Deletes(string who, string id) => Said(who, "d" + id, "") with { RetractId = id };

        await service.StoreAsync(session, Said("del@example", "k1", "keep this"));
        await service.StoreAsync(session, Said("del@example", "k2", "regret this"));
        var mine = new XmppMessage(XmppAddress.Parse("me@example/other"), XmppAddress.Parse("del@example"), "chat", "my words", "", "k3", null, "", true);
        await service.StoreAsync(session, mine);
        await service.StoreAsync(session, Deletes("del@example", "k3"));
        var editedBefore = edited.Count;
        Check("delete: is not a message of its own", await service.StoreAsync(session, Deletes("del@example", "k2")) is null);
        await service.StoreAsync(session, Deletes("someone@example", "k1"));
        await service.StoreAsync(session, Said("someone@example", "dk", "") with { RetractId = "k1", RetractByModerator = true });

        await using (var db = factory.CreateDbContext())
        {
            var rows = await db.JabberMessages.Where(m => m.Conversation == "del@example").OrderBy(m => m.Id).ToListAsync();
            Check("delete: they cannot delete what this account said to them", rows.Count == 3 && rows[2] is { Body: "my words", Deleted: JabberDeleted.No, IsOutgoing: true });
            await service.StoreAsync(session, mine with { Id = "dk3", Body = "", RetractId = "k3" });
            await using (var again = factory.CreateDbContext())
                Check("delete: but this account can, from its other client", await again.JabberMessages.AnyAsync(m => m.MessageId == "k3" && m.Deleted == JabberDeleted.BySender && m.Body == ""));
            Check("delete: the sender's own goes, keeping its place", rows.Count == 3 && rows[1] is { Body: "", Deleted: JabberDeleted.BySender, IsRead: true, IsAlert: false }, string.Join(";", rows.Select(r => $"{r.Body}/{r.Deleted}")));
            Check("delete: nobody else can delete theirs, nor claim to moderate a person", rows[0] is { Body: "keep this", Deleted: JabberDeleted.No });
            Check("delete: the screen is told", edited.Count == editedBefore + 2, $"{edited.Count - editedBefore}");
        }

        const string Ops = "ops@rooms.example";
        session.Locked(s => s.RoomMap[Ops] = new JabberRoomState(new JabberRoom { AccountId = 1, RoomJid = Ops }) { Nick = "me", HistoryDone = true });
        XmppMessage InRoom(string nick, string id, string body, string stanza = "") =>
            new XmppMessage(XmppAddress.Parse(nick.Length > 0 ? $"{Ops}/{nick}" : Ops), XmppAddress.Parse("me@example"), "groupchat", body, "", id, null, stanza, false) { RoomStanzaId = stanza };

        await service.StoreAsync(session, InRoom("alice", "a1", "first", "S1"));
        await service.StoreAsync(session, InRoom("alice", "a2", "second", "S2"));
        await service.StoreAsync(session, InRoom("bob", "b1", "third", "S3"));

        await service.StoreAsync(session, InRoom("bob", "x1", "") with { RetractId = "S1" });
        await service.StoreAsync(session, InRoom("alice", "x2", "") with { RetractId = "S2" });
        await service.StoreAsync(session, InRoom("mallory", "x3", "") with { RetractId = "S3", RetractByModerator = true });
        await service.StoreAsync(session, InRoom("", "x4", "") with { RetractId = "S3", RetractByModerator = true, RetractReason = "off topic" });
        await service.StoreAsync(session, InRoom("", "x5", "") with { RetractId = "S1" });

        await using (var db = factory.CreateDbContext())
        {
            var rows = await db.JabberMessages.Where(m => m.Conversation == Ops).OrderBy(m => m.Id).ToListAsync();
            string Shown(JabberStoredMessage m) => $"{m.Sender}:{m.Body}:{m.Deleted}:{m.DeletedReason}";
            Check("room delete: only their own, by the room's id for it", rows.Count == 3 && rows[1] is { Body: "", Deleted: JabberDeleted.BySender } && rows[0].Body == "first",
                  string.Join(" | ", rows.Select(Shown)));
            Check("room delete: a moderator's only from the room itself, with the reason",
                  rows[2] is { Body: "", Deleted: JabberDeleted.ByModerator, DeletedReason: "off topic" }, Shown(rows[2]));
            Check("room delete: the room deleting as if it were the sender changes nothing", rows[0] is { Deleted: JabberDeleted.No });
            await db.JabberMessages.Where(m => m.Conversation == Ops || m.Conversation == "del@example").ExecuteDeleteAsync();
        }
        session.Locked(s => s.RoomMap.Remove(Ops));

        Check("deleted line: says so, keeps nothing that was said",
              new GroupLineVm(1, "a", "", "", "", true, null, JabberDeleted.ByModerator, "spam") is { IsDeleted: true, IsMention: false, Links.Count: 0 } line
              && line.Body == string.Format(JabberText.MessageRemovedReason, "spam")
              && new GroupLineVm(2, "a", "", "", "", false, null, JabberDeleted.BySender).Body == JabberText.MessageDeleted);
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

    // ── Pictures in the database: fetched only when new ──────────────────────
    {
        var pictures = new AvatarService(factory, new AppErrorLogger(null!));
        var fetches = 0;
        Func<Task<byte[]?>> Fetch(byte[]? bytes) => () => { fetches++; return Task.FromResult(bytes); };
        async Task<List<(string Person, string Hash)>> Owners() { await using var db = factory.CreateDbContext(); return [.. (await db.ChatAvatarOwners.ToListAsync()).Select(o => (o.Person, o.Hash))]; }
        async Task<int> Pictures() { await using var db = factory.CreateDbContext(); return await db.ChatAvatars.CountAsync(); }

        var png = Png();
        await pictures.NoteAsync("jabber", 1, ["r@rooms.example/Bob", "bob@example"], "h1", Fetch(png));
        var owners = await Owners();
        Check("pictures: fetched, kept once, under every name they go by",
              fetches == 1 && await Pictures() == 1 && owners.Count == 2 && owners.All(o => o.Hash == owners[0].Hash), string.Join(";", owners));

        await pictures.NoteAsync("jabber", 1, ["r@rooms.example/Bob", "bob@example"], "h1", Fetch(png));
        Check("pictures: the same hash again is not fetched again", fetches == 1);

        await pictures.NoteAsync("jabber", 1, ["other@example"], null, Fetch(png));
        await pictures.NoteAsync("jabber", 1, ["other@example"], null, Fetch(png));
        Check("pictures: with no hash said, asked once", fetches == 2 && await Pictures() == 1 && (await Owners()).Count == 3);

        await pictures.NoteAsync("jabber", 1, ["junk@example"], "h9", Fetch(System.Text.Encoding.UTF8.GetBytes("not a picture")));
        Check("pictures: what is not a picture is not kept", (await Owners()).All(o => o.Person != "junk@example"));

        await pictures.NoteAsync("jabber", 1, ["r@rooms.example/Bob", "bob@example", "other@example"], "", Fetch(png));
        Check("pictures: none any more, forgotten, and the picture with them", (await Owners()).Count == 0 && await Pictures() == 0);
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

// ── Styling (XEP-0393): the marks kept, styled only where the protocol says ──
{
    string Shape(string text) => string.Join("|", MessageStyling.Parse(text).Select(s => $"{s.Text}:{(int)s.Style}"));
    const int B = (int)TextStyle.Bold, I = (int)TextStyle.Italic, D = (int)TextStyle.Directive, M = (int)TextStyle.Mono, Q = (int)TextStyle.Quote;

    Check("styling: bold, its marks kept and faint", Shape("a *b* c") == $"a :0|*:{B | D}|b:{B}|*:{B | D}| c:0", Shape("a *b* c"));
    Check("styling: within another", Shape("*_x_*") == $"*:{B | D}|_:{B | I | D}|x:{B | I}|_:{B | I | D}|*:{B | D}", Shape("*_x_*"));
    Check("styling: none inside words or sums", Shape("snake_case_name and 2*3*4") == "snake_case_name and 2*3*4:0", Shape("snake_case_name and 2*3*4"));
    Check("styling: none with a space inside the mark, or never closed", Shape("* not* and *open") == "* not* and *open:0");
    Check("styling: not across lines", Shape("*a\nb*") == "*a\nb*:0", Shape("*a\nb*"));
    Check("styling: nothing styled inside code", Shape("`*x*`") == $"`:{M | D}|*x*:{M}|`:{M | D}", Shape("`*x*`"));
    Check("styling: a quote", Shape("> said *it*") == $"> :{Q | D}|said :{Q}|*:{Q | B | D}|it:{Q | B}|*:{Q | B | D}", Shape("> said *it*"));
    Check("styling: a block of code", Shape("```\n*x*\n```\nafter") == $"```\n:{M | D}|*x*\n:{M}|```\n:{M | D}|after:0", Shape("```\n*x*\n```\nafter"));
    Check("styling: every character kept, in order", string.Concat(MessageStyling.Parse("x *y* _z_ `w`\n> q\n```\nc").Select(s => s.Text)) == "x *y* _z_ `w`\n> q\n```\nc");
    Check("styling: plain text is passed by cheaply", !MessageStyling.MayHaveStyling("just words") && MessageStyling.MayHaveStyling("> q"));
}

// ── Replies (XEP-0461), corrections, and what goes out with a message ─────
{
    var self = XmppAddress.Parse("me@example/here");
    XElement From(XElement sent, string from)
    {
        var copy = new XElement(sent);
        copy.SetAttributeValue("from", from);
        copy.SetAttributeValue("to", "me@example/here");
        return copy;
    }

    var quote = XmppClient.QuoteFor("Al 🚀", "first line\r\nsecond\nthird");
    Check("reply: the quote names who, and two lines of theirs", quote == "> Al 🚀:\n> first line\n> second\n", quote);

    var reply = XmppClient.OutgoingMessage("id1", "bob@example", "chat", "my answer 👍", new XmppSendExtras(ReplyTo: "al@example", ReplyId: "m9", ReplyQuote: quote));
    var fallback = reply.Element(XmppClient.Fallback + "fallback")?.Element(XmppClient.Fallback + "body");
    Check("reply: says what it answers", reply.Element(XmppClient.ReplyNs + "reply") is { } r && (string?)r.Attribute("id") == "m9" && (string?)r.Attribute("to") == "al@example");
    Check("reply: the quote marked as only for older clients, counted in characters not halves",
          (string?)fallback?.Attribute("start") == "0" && (string?)fallback?.Attribute("end") == quote.EnumerateRunes().Count().ToString(), fallback?.ToString() ?? "none");
    Check("reply: older clients see the quote, then the words", reply.Element(XmppClient.Client + "body")?.Value == quote + "my answer 👍");

    var read = XmppClient.ParseMessage(From(reply, "bob@example/phone"), self);
    Check("reply: read back without the quote", read is { ReplyId: "m9", ReplyTo: "al@example", Body: "my answer 👍" }, read?.Body ?? "null");
    Check("reply: what was quoted kept, for when what it answers is not here", read?.ReplyQuote == "Al 🚀: first line second", read?.ReplyQuote ?? "null");

    var otherFallback = XmppClient.OutgoingMessage("id1b", "bob@example", "chat", "text");
    otherFallback.Add(new XElement(XmppClient.Fallback + "fallback", new XAttribute("for", "urn:something:else"),
        new XElement(XmppClient.Fallback + "body", new XAttribute("start", 0), new XAttribute("end", 2))));
    Check("reply: another kind of fallback is not taken out", XmppClient.ParseMessage(From(otherFallback, "bob@example/phone"), self)?.Body == "text");

    var plain = XmppClient.OutgoingMessage("id2", "bob@example", "chat", "hello");
    Check("send: its own id kept by the server, and seen asked for, with a person",
          (string?)plain.Element(XmppClient.StanzaId + "origin-id")?.Attribute("id") == "id2" && plain.Element(XmppClient.Markers + "markable") is not null);
    var inRoom = XmppClient.OutgoingMessage("id3", "ops@rooms.example", "groupchat", "hello");
    Check("send: seen is never asked for in a room", inRoom.Element(XmppClient.Markers + "markable") is null && inRoom.Element(XmppClient.ChatStates + "active") is null);
    Check("send: a reply with no quote has no fallback",
          XmppClient.OutgoingMessage("id4", "b@x", "chat", "hi", new XmppSendExtras(ReplyTo: "b@x", ReplyId: "m1")) is var bare
          && bare.Element(XmppClient.Fallback + "fallback") is null && bare.Element(XmppClient.Client + "body")?.Value == "hi");

    var fix = XmppClient.OutgoingMessage("id5", "bob@example", "chat", "fixed", new XmppSendExtras(ReplaceId: "id2"));
    Check("correction: names what it corrects", XmppClient.ParseMessage(From(fix, "bob@example/phone"), self) is { IsCorrection: true, ReplaceId: "id2", Body: "fixed" });
    Check("send: what nobody asked for is not added", fix.Element(XmppClient.ReplyNs + "reply") is null && plain.Element(XmppClient.Correct + "replace") is null);
}

// ── Seen (XEP-0333), the room's lasting ids (XEP-0421), and being put out ──
{
    var self = XmppAddress.Parse("me@example/here");

    var marker = XmppClient.DisplayedMarker("bob@example", "id7");
    marker.SetAttributeValue("from", "bob@example/phone");
    var seen = XmppClient.ParseMessage(marker, self);
    Check("seen: read as a marker, not a message", seen is { DisplayedId: "id7", Body.Length: 0 }, seen?.ToString() ?? "null");
    Check("seen: kept by the server for their other clients", marker.Element(XmppClient.Hints + "store") is not null && (string?)marker.Attribute("type") == "chat");

    var asked = XmppClient.OutgoingMessage("id8", "me@example", "chat", "hi");
    asked.SetAttributeValue("from", "bob@example/phone");
    Check("seen: a message asking for it says so", XmppClient.ParseMessage(asked, self) is { Markable: true });

    var withId = new XElement(XmppClient.Client + "message", new XAttribute("from", "ops@rooms.example/al"), new XAttribute("type", "groupchat"),
        new XElement(XmppClient.Client + "body", "hi"), new XElement(XmppClient.OccupantIdNs + "occupant-id", new XAttribute("id", "occ-1")));
    Check("room ids: read", XmppClient.ParseMessage(withId, self) is { OccupantId: "occ-1" });

    JabberStoredMessage Said(string sender, string occupant) => new() { Sender = sender, OccupantId = occupant };
    Check("room ids: the same id is the same person, whatever the name", JabberService.SamePerson(Said("al", "o1"), Said("al_away", "o1")));
    Check("room ids: a different id is someone else, even with the name", !JabberService.SamePerson(Said("al", "o1"), Said("al", "o2")));
    Check("room ids: without them, by name", JabberService.SamePerson(Said("al", ""), Said("al", "o2")) && !JabberService.SamePerson(Said("al", ""), Said("bo", "")));

    XmppOccupant Out(params string[] codes) =>
        new("r@rooms.example", "me", XmppShow.Offline, "none", "none", "", true, true, "") { Codes = codes, Actor = "boss", Reason = "spam" };
    Check("put out: kicked, banned, no longer a member, room closed",
          JabberService.RemovedBecause(Out("110", "307")) == XmppRoomFailure.Kicked && JabberService.RemovedBecause(Out("110", "301")) == XmppRoomFailure.Banned
          && JabberService.RemovedBecause(Out("321")) == XmppRoomFailure.NotAMember && JabberService.RemovedBecause(Out("332")) == XmppRoomFailure.Closed);
    Check("put out: leaving of its own accord is not", JabberService.RemovedBecause(Out("110")) is null);
    Check("put out: by whom and why", JabberService.RemovedText(Out("307")) is var text && text.Contains("boss", StringComparison.Ordinal) && text.EndsWith(": spam", StringComparison.Ordinal), JabberService.RemovedText(Out("307")));
    Check("put out: nothing said, nothing shown", JabberService.RemovedText(Out("307") with { Actor = "", Reason = "" }) == "");

    var retract = XmppClient.RetractionMessage("ops@rooms.example", "groupchat", "S1", "own1", "gone");
    Check("delete own: both forms, the room's id and its own", (string?)retract.Element(XmppClient.Retract + "retract")?.Attribute("id") == "S1"
          && (string?)retract.Element(XmppClient.Fasten + "apply-to")?.Attribute("id") == "own1" && retract.Element(XmppClient.Client + "body")?.Value == "gone");
    retract.SetAttributeValue("from", "ops@rooms.example/me");
    Check("delete own: read back as the delete it is", XmppClient.ParseMessage(retract, self) is { RetractId: "S1", RetractByModerator: false });
}

// ── What a reply shows above it ───────────────────────────────────────────
{
    var lines = new[] { (Id: "m1", Who: "Al", Text: "hello\nthere"), (Id: "m2", Who: "Bo", Text: new string('x', 300)) };
    string Show(string id, string quote) => GroupChatViewModel.ReplyTextFor(id, quote, lines, l => l.Id, l => l.Who, l => l.Text);

    Check("reply shown: from the message itself, on one line", Show("m1", "ignored") == "↩ Al: hello there", Show("m1", "ignored"));
    Check("reply shown: else what was quoted", Show("gone", "Al: old words") == "↩ Al: old words");
    Check("reply shown: else says it is not here", Show("gone", "") == "↩ " + JabberText.ReplyUnknown);
    Check("reply shown: none for a message that answers nothing", Show("", "quote") == "");
    Check("reply shown: a long one cut short", Show("m2", "") is var cut && cut.EndsWith('…') && cut.Length == 2 + 120 + 1, Show("m2", "").Length.ToString());
}

// ── Stream management (XEP-0198), against a scripted server ───────────────
{
    Check("stream: counts go round", XmppConnection.After(1, 0) && !XmppConnection.After(0, 0) && XmppConnection.After(0, uint.MaxValue) && !XmppConnection.After(uint.MaxValue, 0));

    using var cert = EveConsole.Services.Mumble.MumbleClient.NewClientCertificate("example");
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var login = new XmppLogin("me@example", "pw", "127.0.0.1", port, "r", XmppConnection.Fingerprint(cert), DirectTls: true);
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    var ct = stop.Token;

    const string Header = "<?xml version='1.0'?><stream:stream xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' id='s' version='1.0' from='example'>";
    const string SmNs = "urn:xmpp:sm:3";
    string Bind(string jid) => $"<iq type='result' id='bind'><bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'><jid>{jid}</jid></bind></iq>";
    XElement Out(string id, string body) => new(XmppClient.Client + "message", new XAttribute("to", "bob@example"), new XAttribute("type", "chat"),
        new XAttribute("id", id), new XElement(XmppClient.Client + "body", body));

    var heard = new Dictionary<string, string>();
    var resent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    // One connection of the server's: logged in and offering stream management, then waiting on what it is told.
    async Task<(Func<string, Task<string>> Until, Func<string, Task> Seen, Func<string, Task> Send, Func<Task> Closed)> LoggedInAsync(TcpClient tcp)
    {
        var tls = new System.Net.Security.SslStream(tcp.GetStream());
        await tls.AuthenticateAsServerAsync(cert);
        var text = new StringBuilder();
        var used = 0;
        var buffer = new byte[8192];

        async Task More()
        {
            var n = await tls.ReadAsync(buffer, ct);
            if (n == 0) throw new IOException("closed");
            text.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }
        async Task<string> Until(string mark)
        {
            while (text.ToString().IndexOf(mark, used, StringComparison.Ordinal) is var at && at < 0) await More();
            var all = text.ToString();
            var end = all.IndexOf(mark, used, StringComparison.Ordinal) + mark.Length;
            var part = all[used..end];
            used = end;
            return part;
        }
        async Task Seen(string mark) { while (!text.ToString().Contains(mark, StringComparison.Ordinal)) await More(); }
        async Task Send(string s) { await tls.WriteAsync(Encoding.UTF8.GetBytes(s), ct); await tls.FlushAsync(ct); }
        async Task Closed() { try { while (true) await More(); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { } }

        await Until("streams'>");
        await Send(Header + "<stream:features><mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'><mechanism>PLAIN</mechanism></mechanisms></stream:features>");
        await Until("</auth>");
        await Send("<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'/>");
        await Until("streams'>");
        await Send(Header + $"<stream:features><bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'/><sm xmlns='{SmNs}'/></stream:features>");
        return (Until, Seen, Send, Closed);
    }

    var server = Task.Run(async () =>
    {
        // First: a session that counts. A message in with a request to say so; two out, of which only the first is confirmed.
        using (var tcp = await listener.AcceptTcpClientAsync(ct))
        {
            var (until, seen, send, closed) = await LoggedInAsync(tcp);
            await until("</iq>");
            await send(Bind("me@example/r1"));
            heard["enable"] = await until("/>");
            await send($"<enabled xmlns='{SmNs}' id='S1' resume='true'/>");
            await send($"<message from='al@example/x' to='me@example/r1' type='chat' id='in1'><body>hi</body></message><r xmlns='{SmNs}'/>");
            await seen("<a h=\"1\"");
            await seen("two</body></message><r xmlns='urn:xmpp:sm:3'/>");
            await send($"<a xmlns='{SmNs}' h='1'/><message from='al@example/x' to='me@example/r1' type='chat' id='in2'><body>again</body></message>");
            await closed();
        }

        // Second: the dropped stream picked up, the server having had only the first of the two.
        using (var tcp = await listener.AcceptTcpClientAsync(ct))
        {
            var (until, seen, send, closed) = await LoggedInAsync(tcp);
            await until("<resume");
            heard["resume"] = await until("/>");
            await send($"<resumed xmlns='{SmNs}' previd='S1' h='1'/>");
            await seen("two</body>");
            resent.TrySetResult(true);
            await closed();
        }

        // Third: too late, the server no longer has it. A new session, and a server that will not count this time.
        using (var tcp = await listener.AcceptTcpClientAsync(ct))
        {
            var (until, _, send, closed) = await LoggedInAsync(tcp);
            await until("<resume");
            await send($"<failed xmlns='{SmNs}' h='1'/>");
            await until("</iq>");
            await send(Bind("me@example/r2"));
            await until("<enable");
            await send($"<failed xmlns='{SmNs}'/>");
            await closed();
        }
    }, ct);

    try
    {
        var first = new XmppConnection(login);
        await first.ConnectAsync(ct);
        var bound = first.Bound;
        Check("stream: counting once the server agrees, keeping it if dropped", first.Counting && heard.GetValueOrDefault("enable", "").Contains("resume=\"true\"", StringComparison.Ordinal), heard.GetValueOrDefault("enable", ""));

        var in1 = await first.ReadStanzaAsync(ct);
        var waiting = first.ReadStanzaAsync(ct);
        await first.WriteAsync(Out("out1", "one"), ct);
        await first.WriteAsync(Out("out2", "two"), ct);
        var in2 = await waiting.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Check("stream: its own messages are not handed on", (string?)in1?.Attribute("id") == "in1" && (string?)in2?.Attribute("id") == "in2", $"{in1} / {in2}");

        var kept = first.Resumable;
        Check("stream: what picking it up takes", kept is { Id: "S1", Inbound: 2, Outbound: 2 } && kept.Unacked.Count == 1 && kept.Unacked[0].Stanza.Element(XmppClient.Client + "body")?.Value == "two",
              kept is null ? "null" : $"{kept.Id} in {kept.Inbound} out {kept.Outbound} unacked {kept.Unacked.Count}");
        first.Abandon();

        var second = new XmppConnection(login, kept);
        await second.ConnectAsync(ct);
        Check("stream: picked up with what came in counted", heard.GetValueOrDefault("resume", "") is var asked && asked.Contains("previd=\"S1\"", StringComparison.Ordinal) && asked.Contains("h=\"2\"", StringComparison.Ordinal), heard.GetValueOrDefault("resume", ""));
        Check("stream: picked up as it was, the same address", second.Resumed && second.Counting && second.Bound == bound, second.Bound.Full);
        Check("stream: what the server never had sent again", await resent.Task.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Check("stream: sent again, and counted again", second.Resumable is { Outbound: 2 } again && again.Unacked.Count == 1);
        second.Abandon();

        var third = new XmppConnection(login, kept);
        await third.ConnectAsync(ct);
        var unsent = third.TakeUnsent();
        Check("stream: too late, a new session", !third.Resumed && third.Bound.Full == "me@example/r2" && !third.Counting && third.Resumable is null, third.Bound.Full);
        Check("stream: what was never confirmed is handed back to send again, once",
              unsent.Count == 1 && unsent[0].Element(XmppClient.Client + "body")?.Value == "two" && third.TakeUnsent().Count == 0, unsent.Count.ToString());
        await third.DisposeAsync();

        await server.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
    catch (Exception e)
    {
        Check("stream: the scripted server ran to the end", false, e.GetType().Name + ": " + e.Message);
    }
    finally
    {
        listener.Stop();
    }
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
