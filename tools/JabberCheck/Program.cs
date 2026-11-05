// The Jabber client's rules: the login's arithmetic, what is refused, and what counts as read.
// Needs no server: what a real one does is checked against a throwaway Openfire by hand.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Localization;
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
    Check("resource: this machine's own, stable", JabberService.MachineResource() == JabberService.MachineResource() && JabberService.MachineResource().StartsWith("EVEConsole-", StringComparison.Ordinal));
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

    var schema = EveConsole.Agent.AgentSchema.Build(factory.CreateDbContext());
    Check("agent: Jabber's tables are kept from the in-app agent",
          !schema.TableNames.Any(t => t.StartsWith("Jabber", StringComparison.OrdinalIgnoreCase)) && schema.TableNames.Count > 0);
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { File.Delete(dbPath); } catch (IOException) { /* a temp file, and the result is what matters */ }
}

Console.WriteLine($"Jabber check: {checks} check(s), {failures.Count} failure(s).");
foreach (var f in failures) Console.WriteLine(f);
return failures.Count == 0 ? 0 : 1;

sealed class Factory(DbContextOptions<AppDbContext> o) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(o);
}
