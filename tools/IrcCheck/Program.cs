// The IRC client's rules: the wire format, the server's own rules, what is refused, and what is kept where, against a
// scripted server in this process. No network and no real server: a real one was tried by hand.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Irc;
using EveConsole.ViewModels;
using Microsoft.EntityFrameworkCore;

var checks = 0;
var failures = new List<string>();

void Check(string name, bool ok, string detail = "")
{
    checks++;
    if (!ok) failures.Add($"  FAIL  {name}{(detail.Length > 0 ? " | " + detail : "")}");
}

async Task<bool> Wait(Func<bool> done, int ms = 5000)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (!done() && DateTime.UtcNow < until) await Task.Delay(20);
    return done();
}

// ── The wire format ───────────────────────────────────────────────────────────
{
    var m = IrcMessage.Parse(@"@time=2026-11-09T10:00:00.000Z;msgid=abc;+draft/reply=x\sy\:z\\w :Bob!u@host PRIVMSG #fleet :hello there")!;
    Check("tags, prefix, command, params", m is { Command: "PRIVMSG", Prefix: "Bob!u@host", Nick: "Bob", UserHost: "u@host" } && m[0] == "#fleet" && m.Last == "hello there");
    Check("tag values unescaped", m.Tag("+draft/reply") == @"x y;z\w" && m.Tag("msgid") == "abc");
    Check("server time read", IrcClient.TimeOf(m) == new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));

    Check("trailing may be empty", IrcMessage.Parse("PRIVMSG #a :")! is { } e && e.Params.Count == 2 && e.Last == "");
    Check("a line with only a command", IrcMessage.Parse("PING")! is { Command: "PING", Params.Count: 0 });
    Check("numerics", IrcMessage.Parse(":srv 001 me :Welcome")! is { Command: "001" } w && w[0] == "me");
    Check("lower-case command upper-cased", IrcMessage.Parse("ping :x")!.Command == "PING");
    Check("not a line: junk gives null, not a throw",
        IrcMessage.Parse("") is null && IrcMessage.Parse("@") is null && IrcMessage.Parse(":prefixonly") is null
        && IrcMessage.Parse("@a=b") is null && IrcMessage.Parse("P*NG x") is null && IrcMessage.Parse(new string('x', 20000)) is null);
    Check("a lone trailing backslash in a tag is dropped", IrcMessage.Unescape(@"ab\") == "ab");
    Check("an unknown escape keeps the letter", IrcMessage.Unescape(@"\q") == "q");
    var many = IrcMessage.Parse("CMD " + string.Join(' ', Enumerable.Range(1, 30)))!;
    Check("at most 15 params, the rest in the last", many.Params.Count == 15 && many.Last.StartsWith("15 16"));
    var tagFlood = IrcMessage.Parse("@" + string.Join(';', Enumerable.Range(0, 500).Select(i => $"k{i}=v")) + " PING")!;
    Check("a tag flood is cut short", tagFlood.Tags.Count <= 64);

    var round = new IrcMessage("PRIVMSG", "#a", "say: \"hi\"") { Tags = new Dictionary<string, string> { ["+typing"] = "active", ["+x"] = "a b;c\\d" } }.Serialize();
    Check("written back", IrcMessage.Parse(round) is { } back && back.Last == "say: \"hi\"" && back.Tag("+x") == "a b;c\\d" && back.Tag("+typing") == "active", round);
    Check("a line break in words cannot start a command of its own",
        !new IrcMessage("PRIVMSG", "#a", "hi\r\nQUIT :bye").Serialize().Contains('\n')
        && !new IrcMessage("PRIVMSG", "#a\nJOIN #b", "x").Serialize().Contains('\n'));
    Check("a middle param with a space cannot split the line", IrcMessage.Parse(new IrcMessage("MODE", "#a b", "+o", "c").Serialize())!.Params.Count == 3);
    Check("an empty last param is written as ':'", new IrcMessage("AWAY", "").Serialize() == "AWAY :");
}

// ── The server's own rules ────────────────────────────────────────────────────
{
    var s = new IrcSupport();
    s.Apply(["PREFIX=(qaohv)~&@%+", "CHANTYPES=#&", "CHANMODES=Ibe,k,fl,CEMRUimnstu", "CASEMAPPING=rfc1459", "NETWORK=Test\\x20Net", "CHATHISTORY=1000", "MONITOR=100"]);
    Check("ranks in order", s.Ranks.Count == 5 && s.RankOfMode('q') == 0 && s.RankOfMode('v') == 4 && s.RankOfSymbol('@') == 2);
    Check("network name unescaped", s.Network == "Test Net", s.Network);
    Check("history and monitor", s.ChatHistory == 1000 && s.Monitor == 100);
    Check("which modes take a value", s.TakesValue('o', false) && s.TakesValue('b', false) && s.TakesValue('k', false) && s.TakesValue('l', true) && !s.TakesValue('l', false) && !s.TakesValue('m', true));
    Check("rfc1459 folding", s.Fold("Nick[]\\^") == "nick{}|~" && s.Same("#Fleet", "#fleet"));
    Check("ascii folding leaves brackets", IrcSupport.Fold("A[]", "ascii") == "a[]");
    Check("strict folding leaves ^", IrcSupport.Fold("A^", "rfc1459-strict") == "a^");
    s.Apply(["PREFIX=(ov"]);
    Check("a broken PREFIX keeps the last good one", s.Ranks.Count == 5);
    Check("channels by their first character", s.IsChannel("#a") && s.IsChannel("&a") && !s.IsChannel("bob") && !s.IsChannel(""));
}

// ── Text ──────────────────────────────────────────────────────────────────────
{
    Check("formatting codes taken out",
        IrcService.StripFormatting("\u0002bold\u0002 \u000304,01red\u0003 \u001Dit\u001D \u000fend") == "bold red it end",
        IrcService.StripFormatting("\u0002bold\u0002 \u000304,01red\u0003 \u001Dit\u001D \u000fend"));
    Check("a colour with no number, and a comma that is words", IrcService.StripFormatting("\u0003a, b") == "a, b" && IrcService.StripFormatting("\u00034,x") == ",x");
    Check("hex colours", IrcService.StripFormatting("\u0004FF0000red") == "red");
    Check("plain text untouched", IrcService.StripFormatting("plain, text.") == "plain, text.");

    Check("CTCP read", IrcClient.IsCtcp("\u0001ACTION waves\u0001", out var c, out var rest) && c == "ACTION" && rest == "waves");
    Check("not CTCP", !IrcClient.IsCtcp("ACTION", out _, out _));

    Check("commands for once connected", IrcService.CommandLine("/msg NickServ IDENTIFY x") is { Command: "PRIVMSG" } p && p[0] == "NickServ" && p.Last == "IDENTIFY x"
        && IrcService.CommandLine("MODE me +x") is { Command: "MODE" } && IrcService.CommandLine("  ") is null && IrcService.CommandLine("/msg onlyone") is null);

    Check("this machine", IrcClient.IsThisMachine("localhost") && IrcClient.IsThisMachine("127.0.0.1") && IrcClient.IsThisMachine("::1")
        && !IrcClient.IsThisMachine("irc.example.org") && !IrcClient.IsThisMachine("10.0.0.5"));

    var a = IrcService.DedupeKey("#a", "bob", "hi", null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var b = IrcService.DedupeKey("#a", "bob", "hi", null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var other = IrcService.DedupeKey("#a", "bob", "hi", null, new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero));
    Check("the same line at the same server time is the same", a == b && a != other);
    Check("an id wins", IrcService.DedupeKey("#a", "bob", "x", "id1", null) == IrcService.DedupeKey("#a", "carol", "y", "id1", DateTimeOffset.UtcNow));
    Check("without an id or a time, never a repeat", IrcService.DedupeKey("#a", "bob", "hi", null, null) != IrcService.DedupeKey("#a", "bob", "hi", null, null));

    var server = new IrcServer { Nickname = "Ann", AlertWords = "fleet, ping" };
    Check("alerts on the nickname and the words, as words",
        IrcService.ChannelAlerts(server, "Ann_", new IrcChannel(), "hey Ann_") && IrcService.ChannelAlerts(server, "Ann_", new IrcChannel(), "Fleet up")
        && !IrcService.ChannelAlerts(server, "Ann_", new IrcChannel(), "annual pinging")
        && IrcService.ChannelAlerts(server, "", new IrcChannel { AlertMode = JabberRoomAlert.All }, "anything")
        && !IrcService.ChannelAlerts(server, "Ann", new IrcChannel { AlertMode = JabberRoomAlert.None }, "Ann"));
}

// ── A scripted server: logging in ─────────────────────────────────────────────
{
    await using var fake = new FakeIrc();
    var client = new IrcClient(new IrcLogin("127.0.0.1", fake.Port, false, "Ann", LoginKind: IrcLoginKind.Sasl, Account: "ann", Password: "pw"));
    await client.ConnectAsync(CancellationToken.None);
    Check("logs in through CAP and SASL", client.Nick == "Ann" && client.LoggedInAs == "ann" && client.HasEcho && client.HasTags && client.HasChatHistory);
    var auth = fake.Got.FirstOrDefault(l => l.StartsWith("AUTHENTICATE ") && l != "AUTHENTICATE PLAIN");
    Check("SASL PLAIN as the protocol says", auth == "AUTHENTICATE " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ann\0ann\0pw")), auth ?? "none");
    Check("CAP END once, after SASL", fake.Got.Count(l => l == "CAP END") == 1 && fake.Got.IndexOf("CAP END") > fake.Got.FindIndex(l => l.StartsWith("AUTHENTICATE ") && l != "AUTHENTICATE PLAIN"));
    Check("the multi-line CAP LS is read whole", fake.Got.Any(l => l.StartsWith("CAP REQ") && l.Contains("echo-message") && l.Contains("sasl")));
}
{
    await using var fake = new FakeIrc { RefuseSasl = true };
    var client = new IrcClient(new IrcLogin("127.0.0.1", fake.Port, false, "Ann", LoginKind: IrcLoginKind.Sasl, Password: "wrong"));
    var failure = await Catch(() => client.ConnectAsync(CancellationToken.None));
    Check("a refused password is a login failure", failure is IrcException { Failure: IrcFailure.Login }, failure?.Message ?? "no failure");
}
{
    await using var fake = new FakeIrc { Taken = ["Ann", "Ann_"] };
    var client = new IrcClient(new IrcLogin("127.0.0.1", fake.Port, false, "Ann"));
    await client.ConnectAsync(CancellationToken.None);
    Check("a taken nickname moves to the next", client.Nick == "Ann__", client.Nick);
}
{
    await using var fake = new FakeIrc { NoCaps = true };
    var client = new IrcClient(new IrcLogin("127.0.0.1", fake.Port, false, "Ann"));
    await client.ConnectAsync(CancellationToken.None);
    Check("a server without capabilities is plain IRC", client.Nick == "Ann" && !client.HasEcho && !client.HasTags && !client.HasChatHistory);
}

// ── A login the server refuses whole, and a server gone quiet ────────────────
{
    await using var fake = new FakeIrc { NakFirstRequest = true };
    var client = new IrcClient(new IrcLogin("127.0.0.1", fake.Port, false, "Ann", LoginKind: IrcLoginKind.Sasl, Account: "ann", Password: "pw"));
    await client.ConnectAsync(CancellationToken.None);
    Check("a refused request asks for the login alone, and logs in", client.LoggedInAs == "ann" && fake.Got.Contains("CAP REQ sasl"));
}
{
    await using var fake = new FakeIrc();
    var client = new IrcClient(new IrcLogin("127.0.0.1", fake.Port, false, "Ann")) { QuietBeforePing = TimeSpan.FromMilliseconds(600), PingAnswerTime = TimeSpan.FromMilliseconds(600) };
    await client.ConnectAsync(CancellationToken.None);
    var run = client.RunAsync(CancellationToken.None);
    await Task.Delay(2500);
    Check("a quiet server that answers its PING is kept", !run.IsCompleted && fake.Got.Count(l => l.StartsWith("PING")) >= 2);
    fake.Silent = true;
    var failure = await Catch(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
    Check("a server that stops answering is taken for gone", failure is IrcException { Failure: IrcFailure.Network }, failure?.GetType().Name + " " + failure?.Message);
}
{
    await using var fake = new FakeIrc();
    var client = new IrcClient(new IrcLogin("127.0.0.1", fake.Port, false, "Ann"));
    await client.ConnectAsync(CancellationToken.None);
    var run = client.RunAsync(CancellationToken.None);
    var queued = Enumerable.Range(0, 30).Select(i => client.SayAsync("#a", "line " + i, false, "", CancellationToken.None)).ToList();
    await Task.Delay(300);
    fake.Push("PING :urgent");
    Check("the answer to a PING goes ahead of what waits its turn",
        await Wait(() => fake.Got.Contains("PONG urgent"), 1500) && fake.Got.Count(l => l.StartsWith("PRIVMSG #a :line")) < 30,
        $"{fake.Got.Count(l => l.StartsWith("PRIVMSG"))} sent before");

    var nicks = Enumerable.Range(0, 60).Select(i => "SomeoneWithALongName" + i).ToList();
    var support = typeof(IrcClient).GetProperty("Support")!.GetValue(client) as IrcSupport;
    support!.Apply(["MONITOR=100"]);
    await client.MonitorAsync(nicks, CancellationToken.None);
    await Task.Delay(1500);
    var monitorLines = fake.Got.Where(l => l.StartsWith("MONITOR + ")).ToList();
    Check("a long watch list goes in lines that fit, every name whole",
        monitorLines.Count > 1 && monitorLines.All(l => l.Length < 450)
        && monitorLines.SelectMany(l => l["MONITOR + ".Length..].TrimStart(':').Split(',')).Order().SequenceEqual(nicks.Order()),
        $"{monitorLines.Count} lines");
}
{
    // On a connection of its own: one with lines waiting their turn would time the waiting, not the cutting.
    await using var fake = new FakeIrc();
    var client = new IrcClient(new IrcLogin("127.0.0.1", fake.Port, false, "Ann"));
    await client.ConnectAsync(CancellationToken.None);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    await client.NoticeAsync("#a", new string('x', 1_000_000), CancellationToken.None);
    var took = watch.Elapsed;
    Check("a huge line is cut to fit quickly", took < TimeSpan.FromSeconds(2)
        && await Wait(() => fake.Got.Any(l => l.StartsWith("NOTICE #a ") && l.EndsWith("xxx") && Encoding.UTF8.GetByteCount(l) <= 510)), took.ToString());
}

// ── Splitting what is said ────────────────────────────────────────────────────
{
    await using var fake = new FakeIrc();
    var client = new IrcClient(new IrcLogin("127.0.0.1", fake.Port, false, "Ann"));
    await client.ConnectAsync(CancellationToken.None);
    var text = string.Concat(Enumerable.Range(0, 400).Select(i => i % 7 == 0 ? "🚀 " : "word" + i + " ")) + "\nsecond line";
    var pieces = client.Split("#a", text, false).ToList();
    Check("long text in pieces that fit, nothing lost",
        pieces.All(p => Encoding.UTF8.GetByteCount(p) <= 400)
        && string.Join(' ', pieces).Split(' ', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
        && pieces[^1] == "second line",
        $"{pieces.Count} pieces, longest {pieces.Max(p => Encoding.UTF8.GetByteCount(p))}");
    Check("no character cut in two", pieces.All(p => !p.Any(char.IsSurrogate) || p.Where(char.IsHighSurrogate).Count() == p.Where(char.IsLowSurrogate).Count()));
    var unbroken = client.Split("#a", new string('x', 1500), false).ToList();
    Check("a word longer than a line is cut", unbroken.Count >= 3 && string.Concat(unbroken).Length == 1500);
    Check("blank lines send nothing", !client.Split("#a", "\n\n  \n", false).Any());
}

// ── The service against the scripted server: what is kept ────────────────────
var profile = Path.Combine(Path.GetTempPath(), "irccheck-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(profile);
AppConfig.UseProfile(profile);
var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(profile, "check.db")}").Options;
var factory = new Factory(options);
try
{
    await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
    await using var fake = new FakeIrc();
    var irc = new IrcService(factory, new AppErrorLogger(null!));
    var stored = new ConcurrentQueue<IrcStoredMessage>();
    irc.MessageStored += m => stored.Enqueue(m);

    var session = await irc.SaveServerAsync(new IrcServer { Name = "Fake", Host = "127.0.0.1", Port = fake.Port, Tls = false, Nickname = "Ann" }, null);
    irc.Connect(session);
    Check("service connects", await Wait(() => session.State == IrcState.Connected), $"{session.State} {session.Detail}");
    await irc.JoinChannelAsync(session, "#fleet", "", null);
    Check("joined, people listed with ranks", await Wait(() => session.ChannelOf("#FLEET") is { Joined: true } c && c.Members.Count == 3
        && c.Members.Values.Any(m => m.Nick == "Bob" && m.Ranks == "ov")), string.Join(",", session.ChannelOf("#fleet")?.Members.Values.Select(m => m.Ranks + m.Nick) ?? []));

    // ⚠️ The history the join asks for holds an old JOIN of this person: taken as live, it emptied the people (fixed).
    // ⚠️ Waited for longer than most: the ask goes out at the sending pace (five lines, then one a second) behind the
    // join, the history targets and the channel's settings, some four seconds in, which a five-second wait missed.
    Check("history read, and its old JOIN leaves the people alone",
        await Wait(() => stored.Any(m => m.Body == "from history" && m.IsRead), 15000) && session.ChannelOf("#fleet")!.Members.Count == 3,
        $"{session.ChannelOf("#fleet")?.Members.Count} people; saved: " + string.Join(" | ", stored.Select(m => $"{m.Body}:{(m.IsRead ? "read" : "unread")}"))
        + "; sent: " + string.Join(" | ", fake.Got.ToArray()));

    fake.Push(":Bob!b@h PRIVMSG #fleet :Ann are you there");
    Check("a mention saved, unread, alerting", await Wait(() => stored.Any(m => m.Body == "Ann are you there" && m is { IsMention: true, IsAlert: true, IsRead: false })));
    fake.Push("@msgid=m1;time=2026-11-09T10:00:00.000Z :Bob!b@h PRIVMSG #fleet :once only");
    fake.Push("@msgid=m1;time=2026-11-09T10:00:00.000Z :Bob!b@h PRIVMSG #fleet :once only");
    await Task.Delay(400);
    Check("the same id saved once", stored.Count(m => m.Body == "once only") == 1);

    var said = await irc.SendAsync(session, "#fleet", "hello all");
    Check("my words come back from the server and are saved once, as mine", said == "" && await Wait(() => stored.Count(m => m.Body == "hello all" && m.IsOutgoing && m.MessageId.Length > 0) == 1));

    // ── Channel settings (modes) ──
    Check("a channel's settings are asked for on joining, and held", await Wait(() => session.ChannelOf("#fleet") is { ModesKnown: true } c
        && c.Modes.ContainsKey('n') && c.Modes.ContainsKey('t') && c.Modes.GetValueOrDefault('l') == "25"),
        string.Join(",", session.ChannelOf("#fleet")?.Modes.Select(x => x.Key + "=" + x.Value) ?? []));
    fake.Push(":Bob!b@h MODE #fleet +mk-t sesame");
    fake.Push(":Bob!b@h MODE #fleet +b-l *!*@spam.host");
    Check("settings follow what is changed, lists and ranks apart", await Wait(() => session.ChannelOf("#fleet") is { } c
        && c.Modes.ContainsKey('m') && !c.Modes.ContainsKey('t') && !c.Modes.ContainsKey('l') && !c.Modes.ContainsKey('b')),
        string.Join(",", session.ChannelOf("#fleet")?.Modes.Select(x => x.Key + "=" + x.Value) ?? []));
    Check("a key is held without its value: it is a password", session.ChannelOf("#fleet")!.Modes.GetValueOrDefault('k') == "");
    var before = session.RefusalCount;
    fake.Push(":fake.server 482 Ann #fleet :You're not a channel operator");
    fake.Push(":fake.server 482 Ann #fleet :You're not a channel operator");
    Check("the same refusal twice is two refusals, so both are shown", await Wait(() => session.RefusalCount == before + 2)
        && session.LastRefusal == "#fleet: You're not a channel operator", $"{session.RefusalCount - before} {session.LastRefusal}");
    var exceptions = await irc.ModeListAsync(session, "#fleet", 'e');
    Check("a channel's ban exceptions read", exceptions is [{ Mask: "*!*@friend.host", SetBy: "Bob" }], string.Join(",", exceptions.Select(e => e.Mask)));
    Check("an empty list is empty, and comes back", (await irc.ModeListAsync(session, "#fleet", 'I')).Count == 0);
    await irc.SetModesAsync(session, "#fleet", "+i-m", []);
    await irc.SetModesAsync(session, "#fleet", "+l", ["30"]);
    await irc.SetListEntryAsync(session, "#fleet", 'e', "*!*@good.host", add: true);
    Check("settings sent as MODE, values after the letters", await Wait(() => fake.Got.Contains("MODE #fleet +i-m") && fake.Got.Contains("MODE #fleet +l 30")
        && fake.Got.Contains("MODE #fleet +e *!*@good.host")), string.Join(" | ", fake.Got.Where(l => l.StartsWith("MODE"))));
    {
        var vm = new IrcChannelSettingsVm(irc, session, "#fleet", session.Support, new Dictionary<char, string> { ['n'] = "", ['k'] = "", ['l'] = "25" }, true);
        vm.Flags.First(f => f.Letter == 'i').IsOn = true;
        vm.Flags.First(f => f.Letter == 'n').IsOn = false;
        vm.Values.First(v => v.Letter == 'l').Value = "40";
        vm.KeyOn = false;
        var changes = vm.Changes();
        Check("the settings window sends only what changed: flags together, each value on its own, a key taken off with a stand-in",
            changes.Count == 3 && changes[0].Changes == "+i-n" && changes[0].Values.Count == 0 && changes[1].Changes == "+l" && changes[1].Values[0] == "40"
            && changes[2].Changes == "-k" && changes[2].Values[0] == "*",
            string.Join(" | ", changes.Select(c => c.Changes + " " + string.Join(",", c.Values))));
        vm.KeyOn = true;
        vm.NewKey = "two words";
        Check("a key with a space in it is refused before sending", vm.Problem() is { Length: > 0 });
    }
    Check("the server's program is read from 004", session.Support.Software == "ergo-2.19.1", session.Support.Software);

    // ── Every setting in words, by the server's program ──
    Check("server programs told apart by their 004 name",
        IrcModeMeanings.KindOf("ergo-2.19.1") == IrcServerKind.Ergo && IrcModeMeanings.KindOf("InspIRCd-4.2.0") == IrcServerKind.InspIrcd
        && IrcModeMeanings.KindOf("UnrealIRCd-6.1.7") == IrcServerKind.Unreal && IrcModeMeanings.KindOf("solanum-1.0-dev") == IrcServerKind.Solanum
        && IrcModeMeanings.KindOf("ircd-hybrid-8.2.43") == IrcServerKind.Hybrid && IrcModeMeanings.KindOf("ngircd-27") == IrcServerKind.NgIrcd
        && IrcModeMeanings.KindOf("someircd-1.0") == IrcServerKind.Unknown);
    Check("a letter means what its server says: u, r and z differ",
        IrcModeMeanings.Of('u', IrcServerKind.Ergo)?.Label == IrcText.ModeAuditorium && IrcModeMeanings.Of('u', IrcServerKind.Solanum) is null
        && IrcModeMeanings.Of('r', IrcServerKind.Solanum)?.Label == IrcText.ModeRegisteredOnly && IrcModeMeanings.Of('r', IrcServerKind.InspIrcd) is { ServerOnly: true }
        && IrcModeMeanings.Of('z', IrcServerKind.Solanum)?.Label == IrcText.ModeOpModerated && IrcModeMeanings.Of('z', IrcServerKind.Unreal)?.Label == IrcText.ModeTlsOnly);
    Check("on an unknown server only the letters every server shares are named",
        IrcModeMeanings.Of('n', IrcServerKind.Unknown) is not null && IrcModeMeanings.Of('C', IrcServerKind.Unknown) is null);
    var ergoLetters = "CEMRUimnstu";
    Check("every Ergo setting has words (its HELP CMODES)", ergoLetters.All(c => IrcModeMeanings.Of(c, IrcServerKind.Ergo) is not null)
        && IrcModeMeanings.Of('f', IrcServerKind.Ergo) is { Hint.Length: > 0 },
        new string([.. ergoLetters.Where(c => IrcModeMeanings.Of(c, IrcServerKind.Ergo) is null)]));
    {
        var ergo = new IrcSupport { Software = "ergo-2.19.1" };
        ergo.Apply(["CHANMODES=Ibe,k,fl,CEMRUXimnstu"]);
        var vm = new IrcChannelSettingsVm(irc, session, "#fleet", ergo, new Dictionary<char, string> { ['n'] = "", ['X'] = "", ['l'] = "9" }, true);
        Check("the window names every box; a letter it cannot name is not a box",
            vm.Flags.Count == 11 && vm.Flags.All(f => f.Label.Length > 2 && !f.Label.Contains("+" + f.Letter)) && vm.Flags.All(f => f.Letter != 'X')
            && vm.Values.Select(v => v.Letter).SequenceEqual(['f', 'l']) && vm.Values.All(v => v.Hint.Length > 0),
            string.Join(",", vm.Flags.Select(f => f.Letter + "=" + f.Label)));
        Check("and one set is shown as set, for those who know it", vm.AlsoSet.Contains("+X") && vm.HasAlsoSet, vm.AlsoSet);
        vm.OtherModes = " +X-U ";
        Check("the command line goes after the rest, as typed", vm.Problem() is null && vm.Changes() is [.., ("+X-U", [])],
            string.Join(" | ", vm.Changes().Select(c => c.Changes + " " + string.Join(",", c.Values))));
        vm.OtherModes = "+j 5:10";
        Check("with its values", vm.Changes() is [.., ("+j", ["5:10"])]);
        vm.OtherModes = "frobnicate";
        Check("something that is not a mode change is refused before sending", vm.Problem() == IrcText.ModeCommandBad && vm.Changes().All(c => c.Changes != "frobnicate"));
        vm.OtherModes = "";
        Check("an empty command line sends nothing", vm.Problem() is null && vm.Changes().Count == 0);

        var insp = new IrcSupport { Software = "InspIRCd-4.2.0" };
        insp.Apply(["CHANMODES=Ibe,k,Ll,PQimnrstz"]);
        var vmInsp = new IrcChannelSettingsVm(irc, session, "#fleet", insp, new Dictionary<char, string> { ['r'] = "" }, true);
        Check("one only the network sets is shown when set, and cannot be changed; when not set, not shown",
            vmInsp.Flags.FirstOrDefault(f => f.Letter == 'r') is { CanChange: false, IsOn: true } && vmInsp.Flags.All(f => f.Letter != 'P'),
            string.Join(",", vmInsp.Flags.Select(f => f.Letter + ":" + f.CanChange)));
        vmInsp.Flags.First(f => f.Letter == 'r').IsOn = false;
        Check("and is never sent", vmInsp.Changes().Count == 0);
    }

    // ── Taking a message back (REDACT) ──
    var changedSeen = 0;
    irc.MessagesChanged += (_, conversation) => { if (conversation == "#fleet") changedSeen++; };
    fake.Push("@msgid=r1;time=2026-11-09T11:00:00.000Z :Bob!b@h PRIVMSG #fleet :to be taken back");
    fake.Push("@msgid=r2;time=2026-11-09T11:00:01.000Z :Carol!c@h PRIVMSG #fleet :removed by an operator");
    await Wait(() => stored.Any(m => m.MessageId == "r2"));
    fake.Push(":Bob!b@h REDACT #fleet r1");
    fake.Push(":Bob!b@h REDACT #fleet r2 :off topic");
    async Task<IrcStoredMessage?> Saved(string id) => (await irc.LoadConversationAsync(session.Server.Id, "#fleet")).FirstOrDefault(m => m.MessageId == id);
    Check("taken back by who said it: words gone, place kept", await Wait(() => Saved("r1").Result is { Deleted: JabberDeleted.BySender, Body: "", IsRead: true }));
    Check("taken back by an operator, with why", await Wait(() => Saved("r2").Result is { Deleted: JabberDeleted.ByModerator, DeletedReason: "off topic" }));
    Check("the screens are told a conversation changed", changedSeen >= 2, changedSeen.ToString());
    var myLine = stored.First(m => m.Body == "hello all" && m.IsOutgoing);
    Check("this server lets messages be taken back", session.CanRedact);
    await irc.RedactAsync(session, "#fleet", myLine.MessageId);
    Check("taking one's own back sends REDACT, and the echo marks it", fake.Got.Contains("REDACT #fleet " + myLine.MessageId)
        && await Wait(() => Saved(myLine.MessageId).Result is { Deleted: JabberDeleted.BySender }));
    fake.Push("@batch=nosuch :Bob!b@h REDACT #fleet nosuchid");
    fake.Push(":Bob!b@h REDACT #fleet");

    // ── Several lines as one message (draft/multiline) ──
    fake.Push("@msgid=ml1;time=2026-11-09T11:01:00.000Z :Bob!b@h BATCH +mb draft/multiline #fleet");
    fake.Push("@batch=mb :Bob!b@h PRIVMSG #fleet :line one");
    fake.Push("@batch=mb :Bob!b@h PRIVMSG #fleet :line two");
    fake.Push("@batch=mb;draft/multiline-concat :Bob!b@h PRIVMSG #fleet : and on");
    fake.Push(":Bob!b@h BATCH -mb");
    Check("a message of several lines arrives as one, joined lines kept joined", await Wait(() => stored.Any(m => m is { MessageId: "ml1", Conversation: "#fleet", IsChannel: true } && m.Body == "line one\nline two and on")),
        string.Join(" | ", stored.Where(m => m.Body.Contains("line")).Select(m => m.MessageId + ":" + m.Body.Replace("\n", "/"))));
    Check("its lines are not saved one by one", !stored.Any(m => m.Body == "line one"));
    fake.Push(":fake.server BATCH +hm chathistory #fleet");
    fake.Push("@batch=hm;msgid=ml2;time=2026-11-08T08:00:00.000Z :Carol!c@h BATCH +in draft/multiline #fleet");
    fake.Push("@batch=in :Carol!c@h PRIVMSG #fleet :old a");
    fake.Push("@batch=in :Carol!c@h PRIVMSG #fleet :old b");
    fake.Push("@batch=hm :fake.server BATCH -in");
    fake.Push(":fake.server BATCH -hm");
    Check("one inside history is history", await Wait(() => stored.Any(m => m is { MessageId: "ml2", Conversation: "#fleet" } && m.Body == "old a\nold b" && !m.IsAlert)));
    var longLine = new string('x', 700);
    await irc.SendAsync(session, "#fleet", "first\n\nsecond\n" + longLine);
    // ⚠️ Waited for: sent is not yet read by the scripted server, and it was checked a moment too soon once in a while.
    Check("several lines sent as one batch, a long line in joined pieces", await Wait(() =>
        {
            lock (fake.Got)
                return fake.Got.Any(l => l.StartsWith("BATCH +") && l.EndsWith("draft/multiline #fleet")) && fake.Got.Any(l => l.Contains("draft/multiline-concat") && l.Contains("PRIVMSG #fleet"))
                       && fake.Got.Any(l => l.StartsWith("BATCH -"));
        }), string.Join(" | ", fake.Got.Where(l => l.Contains("BATCH") || l.Contains("batch=")).Take(8)));
    Check("and saved once, as written", await Wait(() => stored.Count(m => m.IsOutgoing && m.Conversation == "#fleet" && m.Body == "first\n\nsecond\n" + longLine) == 1),
        string.Join(" | ", stored.Where(m => m.IsOutgoing && m.Body.StartsWith("first")).Select(m => m.Body.Length + ":" + m.Body[..Math.Min(20, m.Body.Length)].Replace("\n", "/"))));
    // ⚠️ A long line of words, cut between them: the pieces are joined again without a break, so each keeps its space.
    var longWords = string.Join(' ', Enumerable.Range(1, 150).Select(i => "w" + i));
    await irc.SendAsync(session, "#fleet", "start\n" + longWords);
    Check("a long line among several comes back whole, no two words run together", await Wait(() => stored.Any(m => m.IsOutgoing && m.Body == "start\n" + longWords)),
        string.Join(" | ", stored.Where(m => m.IsOutgoing && m.Body.StartsWith("start")).Select(m => m.Body.Length.ToString())));
    var paced = System.Diagnostics.Stopwatch.StartNew();
    await irc.SendAsync(session, "#fleet", string.Join('\n', Enumerable.Range(1, 20).Select(i => "row " + i)));
    Check("a batch goes in one go, not a line a second: a server gives up on one left open", paced.Elapsed < TimeSpan.FromSeconds(4), paced.Elapsed.ToString());

    fake.Push(":Bob!b@h MODE #fleet -o+v Ann Ann");
    Check("modes applied", await Wait(() => session.ChannelOf("#fleet")!.Members.Values.Any(m => m.Nick == "Ann" && m.Ranks == "v")));
    fake.Push(":Bob!b@h NICK :Bobby");
    Check("a new nickname follows", await Wait(() => session.ChannelOf("#fleet")!.Members.Values.Any(m => m.Nick == "Bobby")));
    fake.Push(":Bobby!b@h PRIVMSG Ann :\u0001ACTION waves\u0001");
    Check("a direct action, filed under the sender", await Wait(() => stored.Any(m => !m.IsChannel && m.Conversation == "bobby" && m.Kind == IrcMessageKind.Action && m.Body == "waves" && m.IsAlert)));
    fake.Push(":NickServ!s@services NOTICE Ann :You are now identified");
    Check("a services notice is kept but does not alert", await Wait(() => stored.Any(m => m.Sender == "NickServ" && !m.IsAlert)));
    fake.Push(":server.fake NOTICE * :*** Looking up your hostname");
    fake.Push(":Bobby!b@h KICK #fleet Ann :out");
    Check("kicked: out of the channel, and why said", await Wait(() => session.ChannelOf("#fleet") is { Joined: false } c && c.Failure.Contains("out")));

    // Hostile lines: none of them may end the connection or the reading.
    foreach (var line in new[]
    {
        ":x 353", ":x 353 Ann", ":x 366", "MODE", ":x MODE #fleet", ":x MODE #fleet +o", "KICK", ":x KICK #fleet", "JOIN", ":Bob!b@h JOIN",
        ":x TOPIC", ":x 332", "PRIVMSG", ":Bob!b@h PRIVMSG", ":Bob!b@h PRIVMSG #nokept :x", "BATCH", "BATCH +", ":x 005 Ann PREFIX=(ov :are",
        "@batch=nosuch :Bob!b@h PRIVMSG #fleet :in a batch never opened", ":x CHATHISTORY TARGETS", "TAGMSG", ":Bob!b@h TAGMSG #fleet",
        "@+draft/react=" + new string('x', 500) + ";+draft/reply=m1 :Bob!b@h TAGMSG #fleet", ":x 730", ":x 731 Ann", "AWAY", ":x 322 Ann", ":x 323",
        ":x NICK", ":x INVITE", ":x 401", ":x FAIL", new string('A', 600),
    })
        fake.Push(line);
    fake.Push(":Bobby!b@h PRIVMSG Ann :still here");
    Check("hostile lines change nothing that matters: still connected and reading",
        await Wait(() => stored.Any(m => m.Body == "still here")) && session.State == IrcState.Connected, $"{session.State} {session.Detail}");

    // A missed direct message: the server lists who wrote (CHATHISTORY TARGETS, in a batch of its own), and it is fetched.
    Check("a direct message missed while away is fetched on connecting", await Wait(() => stored.Any(m => m.Body == "missed while away" && m.Conversation == "dave")));

    // A netsplit's QUITs come in a batch, and are live: people leave the list.
    fake.Push(":Carol!c@h JOIN #fleet");
    await irc.JoinChannelAsync(session, "#fleet", "", null);
    await Wait(() => session.ChannelOf("#fleet")?.Members.ContainsKey("carol") == true);
    fake.Push(":fake.server BATCH +ns netsplit a.server b.server");
    fake.Push("@batch=ns :Carol!c@h QUIT :a.server b.server");
    fake.Push(":fake.server BATCH -ns");
    Check("a netsplit's quits are taken live", await Wait(() => session.ChannelOf("#fleet")?.Members.ContainsKey("carol") == false),
        string.Join(",", session.ChannelOf("#fleet")?.Members.Keys.ToList() ?? []));

    // Reactions are the same however often they come, as history replays them.
    var reactLine = "@+draft/react=👍;+draft/reply=m1 :Bobby!b@h TAGMSG #fleet";
    fake.Push(reactLine); fake.Push(reactLine);
    fake.Push(":fake.server BATCH +hr chathistory #fleet");
    fake.Push("@batch=hr;" + reactLine[1..]);
    fake.Push("@batch=hr;" + reactLine[1..]);
    fake.Push(":fake.server BATCH -hr");
    await Task.Delay(500);
    var counts = await irc.ReactionsAsync(session.Server.Id, "#fleet", ["m1"], "Ann");
    Check("a reaction replayed stays one reaction", counts.GetValueOrDefault("m1") is [{ Emoji: "👍", Count: 1 }], string.Join(",", counts.GetValueOrDefault("m1")?.Select(c => c.Emoji + c.Count) ?? []));
    fake.Push("@+draft/unreact=👍;+draft/reply=m1 :Bobby!b@h TAGMSG #fleet");
    Check("an unreact takes it back", await Wait(() => irc.ReactionsAsync(session.Server.Id, "#fleet", ["m1"], "Ann").Result.Count == 0));
    var typingSeen = 0;
    irc.TypingChanged += (_, _, _, _) => typingSeen++;
    fake.Push(":fake.server BATCH +ht chathistory #fleet");
    fake.Push("@batch=ht;+typing=active :Bobby!b@h TAGMSG #fleet");
    fake.Push(":fake.server BATCH -ht");
    fake.Push(":Bobby!b@h PRIVMSG Ann :after the typing");
    await Wait(() => stored.Any(m => m.Body == "after the typing"));
    Check("typing from history is not news", typingSeen == 1, typingSeen.ToString());

    var bans = await irc.BansAsync(session, "#fleet");
    Check("a channel's bans read, with who set them", bans.Count == 2 && bans[0] is { Mask: "*!*@bad.host", SetBy: "Bob" } && bans[1].SetBy == "",
        string.Join(", ", bans.Select(b => b.Mask + "/" + b.SetBy)));

    var unread = await irc.UnreadAsync();
    Check("unread by conversation", unread.Any(u => u.Conversation == "#fleet" && u.Mentions == 1) && unread.Any(u => u.Conversation == "bobby" && u.Alerts >= 1));
    await irc.MarkReadAsync(session.Server.Id, "#fleet");
    Check("read clears", !(await irc.UnreadAsync()).Any(u => u.Conversation == "#fleet"));
    Check("and the server is told where it was read up to", await Wait(() => fake.Got.Any(l => l.StartsWith("MARKREAD #fleet timestamp="))),
        string.Join(" | ", fake.Got.Where(l => l.StartsWith("MARKREAD"))));

    // Read on another of the account's clients: the server says so, and it is read here.
    fake.Push("@time=2026-11-09T12:00:00.000Z;msgid=rm1 :Eve!e@h PRIVMSG Ann :read elsewhere");
    fake.Push("@time=2026-11-09T12:00:05.000Z;msgid=rm2 :Eve!e@h PRIVMSG Ann :not yet");
    await Wait(() => stored.Any(m => m.MessageId == "rm2"));
    fake.Push(":fake.server MARKREAD Eve timestamp=2026-11-09T12:00:00.000Z");
    Check("a read marker from the server reads up to it, no further", await Wait(() => irc.LoadConversationAsync(session.Server.Id, "eve").Result is var eve
        && eve.Any(m => m is { MessageId: "rm1", IsRead: true }) && eve.Any(m => m is { MessageId: "rm2", IsRead: false })));
    fake.Push(":fake.server MARKREAD Eve *");
    fake.Push(":fake.server MARKREAD Eve timestamp=not-a-time");

    {
        await using var throttling = new FakeIrc { ErrorOnFirstLogin = true };
        var again = await irc.SaveServerAsync(new IrcServer { Name = "Throttled", Host = "127.0.0.1", Port = throttling.Port, Tls = false, Nickname = "Ann" }, null);
        irc.Connect(again);
        Check("an ERROR while logging in is tried again, not given up on", await Wait(() => again.State == IrcState.Connected, 20000), $"{again.State} {again.Problem} {again.Detail}");
        await irc.DisconnectAsync(again);
    }

    fake.Drop();
    Check("a dropped connection is reconnected", await Wait(() => session.State is IrcState.Reconnecting or IrcState.Connecting, 3000)
        && await Wait(() => session.State == IrcState.Connected, 20000), $"{session.State} {session.Detail}");

    await irc.DisconnectAsync(session);
    Check("disconnected on asking, and stays so", session.State == IrcState.Offline && !await Wait(() => session.State != IrcState.Offline, 1500));

    // A channel left while not joined, and joined again later, stays joined: being left is not remembered for ever.
    await irc.JoinChannelAsync(session, "#later", "", null);
    await irc.LeaveChannelAsync(session, "#later");
    irc.Connect(session);
    await Wait(() => session.State == IrcState.Connected, 20000);
    await irc.JoinChannelAsync(session, "#later", "", null);
    Check("a channel left long ago can be joined again", await Wait(() => session.ChannelOf("#later") is { Joined: true })
        && !await Wait(() => { lock (fake.Got) return fake.Got.Any(l => l.StartsWith("PART #later")); }, 1500));
    await irc.DisconnectAsync(session);

    // ── Who someone is (WHOIS) ──
    irc.Connect(session);
    await Wait(() => session.State == IrcState.Connected, 20000);
    var bob = await irc.WhoisAsync(session, "Bob");
    Check("WHOIS: who someone is, from the lines the server sends", bob is { Found: true, RealName: "Bob Real", User: "b", Host: "bob.host", Account: "bobacct",
        Away: "at lunch", IsSecure: true, IdleSeconds: 300, SignedOnUnix: 1700000000, Server: "fake.server" } && bob.Channels.SequenceEqual(["@#fleet"]),
        bob is null ? "none" : $"{bob.RealName}|{bob.Account}|{bob.Away}|{bob.IdleSeconds}|{string.Join(",", bob.Channels)}");
    var nobody = await irc.WhoisAsync(session, "Nobody");
    Check("WHOIS: nobody of that name is said so, not waited out", nobody is { Found: false });
    Check("WHOIS: a refusal for it is not shown as one", !session.LastRefusal.Contains("Nobody"), session.LastRefusal);
    var typed = await irc.SendAsync(session, "#fleet", "/whois Bob");
    Check("WHOIS: typed, it answers in a line", typed.Contains("Bob Real") && typed.Contains("bobacct") && typed.Contains(IrcText.WhoisAway), typed);
    var inDm = await irc.SendAsync(session, "Bob", "/whois");
    Check("WHOIS: typed alone in a conversation, it asks about the other person", inDm.Contains("Bob Real"), inDm);
    Check("WHOIS: the rows say only what was said", bob is not null && IrcService.WhoisRows(bob).All(r => r.Value.Length > 0)
        && IrcService.WhoisRows(new IrcWhois { Nick = "x" }).Count == 1);
    Check("WHOIS: how long, as people say it", IrcService.Span(TimeSpan.FromSeconds(42)).Contains("42") && IrcService.Span(TimeSpan.FromMinutes(90)).Contains("30")
        && IrcService.Span(TimeSpan.FromDays(2.5)).Contains("12"));
    Check("WHOIS: a channel or a name with a space is not asked", await irc.WhoisAsync(session, "#fleet") is null && await irc.WhoisAsync(session, "a b") is null);
    await irc.DisconnectAsync(session);

    // ── Encryption the server asks for (STS) ──
    Check("STS: what a server offers, read", StsOffer.Parse("port=6697,duration=2592000,preload") is { Port: 6697 } o && o.Duration == TimeSpan.FromSeconds(2592000)
        && StsOffer.Parse("duration=0") is { Port: 0 } z && z.Duration == TimeSpan.Zero
        && StsOffer.Parse("") is null && StsOffer.Parse("preload") is null && StsOffer.Parse("port=x") is null
        && StsOffer.Parse("duration=99999999999")!.Duration == StsOffer.Longest);
    {
        await using var asking = new FakeIrc { ExtraCaps = "sts=port=6697,duration=3600" };
        await using var plain = new IrcClient(new IrcLogin("127.0.0.1", asking.Port, false, "Ann"));
        IrcException? told = null;
        try { await plain.ConnectAsync(CancellationToken.None); } catch (IrcException e) { told = e; }
        Check("STS: unencrypted, the server's ask is to go encrypted now, on its port", told is { Failure: IrcFailure.Upgrade, UpgradePort: 6697 }, told?.Message ?? "connected");
        Check("STS: and nothing more is sent first", !asking.Got.Any(l => l.StartsWith("CAP REQ")));

        // The service goes there at once, encrypted: a listener on that port sees it come.
        var tlsSide = new TcpListener(IPAddress.Loopback, 0);
        tlsSide.Start();
        var tlsPort = ((IPEndPoint)tlsSide.LocalEndpoint).Port;
        var arrived = tlsSide.AcceptTcpClientAsync();
        await using var upgrading = new FakeIrc { ExtraCaps = $"sts=port={tlsPort},duration=3600" };
        var stsSession = await irc.SaveServerAsync(new IrcServer { Name = "Sts", Host = "127.0.0.1", Port = upgrading.Port, Tls = false, Nickname = "Ann" }, null);
        irc.Connect(stsSession);
        Check("STS: the service connects again at once, encrypted, on the port named", await Task.WhenAny(arrived, Task.Delay(5000)) == arrived);
        await irc.DisconnectAsync(stsSession);
        (await arrived).Dispose();
        tlsSide.Stop();
        Check("STS: an ask from an unencrypted connection is never kept", irc.SessionOf(stsSession.Server.Id)!.Server.StsHost == "");

        var rule = new IrcServer { Host = "irc.example.org", StsHost = "irc.example.org", StsPort = 6697, StsUntil = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ") };
        Check("STS: a kept rule holds for its host until it ends", IrcService.StsPortOf(rule) == 6697
            && IrcService.StsPortOf(new IrcServer { Host = "irc.other.org", StsHost = rule.StsHost, StsPort = 6697, StsUntil = rule.StsUntil }) is null
            && IrcService.StsPortOf(new IrcServer { Host = rule.Host, StsHost = rule.StsHost, StsPort = 6697, StsUntil = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ssZ") }) is null);

        // The settings screen holds an older copy of the server: saving it must not undo what the connection kept.
        var stsId = stsSession.Server.Id;
        await using (var db = factory.CreateDbContext())
            await db.IrcServers.Where(s => s.Id == stsId).ExecuteUpdateAsync(u => u.SetProperty(s => s.StsHost, "127.0.0.1").SetProperty(s => s.StsPort, 6697)
                .SetProperty(s => s.StsUntil, rule.StsUntil).SetProperty(s => s.AvatarUrl, "https://example.org/a.png"));
        await irc.SaveServerAsync(new IrcServer { Id = stsId, Name = "Sts renamed", Host = "127.0.0.1", Port = upgrading.Port, Tls = false, Nickname = "Ann" }, null);
        await using (var db = factory.CreateDbContext())
        {
            var back = await db.IrcServers.AsNoTracking().FirstAsync(s => s.Id == stsId);
            Check("STS: saving the settings keeps the server's rule and the picture", back is { Name: "Sts renamed", StsHost: "127.0.0.1", StsPort: 6697, AvatarUrl: "https://example.org/a.png" },
                $"{back.Name} {back.StsHost} {back.StsPort} {back.AvatarUrl}");
        }
        await irc.DeleteServerAsync(stsId);
    }

    // ── Pictures (draft/metadata-2) ──
    {
        Check("pictures: only from the internet", !WebPictures.IsInternet(IPAddress.Parse("127.0.0.1")) && !WebPictures.IsInternet(IPAddress.Parse("10.1.2.3"))
            && !WebPictures.IsInternet(IPAddress.Parse("192.168.1.1")) && !WebPictures.IsInternet(IPAddress.Parse("172.20.0.1")) && !WebPictures.IsInternet(IPAddress.Parse("169.254.1.1"))
            && !WebPictures.IsInternet(IPAddress.Parse("::1")) && !WebPictures.IsInternet(IPAddress.Parse("fd00::1")) && !WebPictures.IsInternet(IPAddress.Parse("fe80::1"))
            && !WebPictures.IsInternet(IPAddress.Parse("::ffff:192.168.0.1"))
            && WebPictures.IsInternet(IPAddress.Parse("8.8.8.8")) && WebPictures.IsInternet(IPAddress.Parse("2606:4700::1111")) && WebPictures.IsInternet(IPAddress.Parse("172.32.0.1")));
        Check("pictures: what a link may be", IrcService.IsPictureLink("") && IrcService.IsPictureLink("https://example.org/a.png")
            && !IrcService.IsPictureLink("ftp://example.org/a.png") && !IrcService.IsPictureLink("https://example.org/a b.png") && !IrcService.IsPictureLink("not a link")
            && !IrcService.IsPictureLink("https://example.org/" + new string('a', 400)));
        Check("pictures: an EVE portrait's link", IrcService.EvePortraitLink(12345) == "https://images.evetech.net/characters/12345/portrait?size=128");

        await using var pictureHost = new PictureHost();
        var bobOne = $"http://127.0.0.1:{pictureHost.Port}/bob.png";
        var bobTwo = $"http://127.0.0.1:{pictureHost.Port}/bob2.png";
        var mine = $"http://127.0.0.1:{pictureHost.Port}/me.png";
        Check("pictures: never from this machine", await WebPictures.FetchAsync(bobOne, CancellationToken.None) is null && pictureHost.Served == 0);
        WebPictures.AllowThisMachine = true;
        Check("pictures: fetched where allowed", await WebPictures.FetchAsync(bobOne, CancellationToken.None) is { Length: > 0 });
        Check("pictures: one too big is not taken", await WebPictures.FetchAsync($"http://127.0.0.1:{pictureHost.Port}/huge.png", CancellationToken.None) is null);

        var logger = new AppErrorLogger(null!);
        var avatars = new AvatarService(factory, logger);
        var pictured = new IrcService(factory, logger, null, avatars);
        await using var meta = new FakeIrc { Pictures = true, BobPicture = bobOne };
        var ps = await pictured.SaveServerAsync(new IrcServer { Name = "Pics", Host = "127.0.0.1", Port = meta.Port, Tls = false, Nickname = "Ann" }, null);
        pictured.Connect(ps);
        Check("pictures: the server keeps them, and is asked to tell of changes", await Wait(() => ps.State == IrcState.Connected, 20000) && ps.HasPictures
            && await Wait(() => { lock (meta.Got) return meta.Got.Contains("METADATA * SUB avatar"); }));

        async Task<string?> Owner(string person)
        {
            await using var db = factory.CreateDbContext();
            return await db.ChatAvatarOwners.AsNoTracking().Where(o => o.Service == "irc" && o.ServerId == ps.Server.Id && o.Person == person).Select(o => o.Hash).FirstOrDefaultAsync();
        }
        async Task<bool> WaitOwner(string person, Func<string?, bool> ok)
        {
            var until = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < until) { if (ok(await Owner(person))) return true; await Task.Delay(50); }
            return ok(await Owner(person));
        }

        await pictured.JoinChannelAsync(ps, "#fleet", "", null);
        Check("pictures: someone's comes with joining, kept under both names the screens use",
            await WaitOwner("nick:bob", h => h is { Length: > 0 }) && await Owner("bob") == await Owner("nick:bob"));
        meta.Push(":Carol!c@h JOIN #fleet");
        Check("pictures: someone who joins later is asked about, as the server tells only the one joining",
            await Wait(() => { lock (meta.Got) return meta.Got.Contains("METADATA Carol GET avatar"); }, 8000));
        meta.Push(":Carol!c@h PART #fleet");
        meta.Push(":Carol!c@h JOIN #fleet");
        meta.Push(":Bob!b@h PART #fleet");
        meta.Push(":Bob!b@h JOIN #fleet");
        await Task.Delay(5000);
        Check("pictures: and only once, nor someone whose picture came already",
            meta.Got.Count(l => l == "METADATA Carol GET avatar") == 1 && !meta.Got.Contains("METADATA Bob GET avatar"));
        var first = await Owner("nick:bob");
        meta.Push($":fake.server METADATA Bob avatar * :{bobTwo}");
        Check("pictures: a new one replaces it", await WaitOwner("nick:bob", h => h is { Length: > 0 } && h != first));
        meta.Push(":fake.server METADATA Bob avatar *");
        Check("pictures: one taken away is forgotten", await WaitOwner("nick:bob", h => h is null) && await Owner("bob") is null);

        await pictured.SetAvatarUrlAsync(ps, mine);
        Check("pictures: this person's set on the server", await Wait(() => { lock (meta.Got) return meta.Got.Contains("METADATA * SET avatar " + mine); }),
            string.Join(" | ", meta.Got.Where(l => l.StartsWith("METADATA"))));
        Check("pictures: and shown here as theirs", await WaitOwner("me", h => h is { Length: > 0 }));
        await using (var db = factory.CreateDbContext())
            Check("pictures: and kept with the server", (await db.IrcServers.AsNoTracking().FirstAsync(s => s.Id == ps.Server.Id)).AvatarUrl == mine);
        var refusedLink = false;
        try { await pictured.SetAvatarUrlAsync(ps, "javascript:alert(1)"); } catch (ArgumentException) { refusedLink = true; }
        Check("pictures: a link that is not one is refused", refusedLink && ps.Server.AvatarUrl == mine);

        await pictured.ReconnectAsync(ps);
        Check("pictures: set again on every connection", await Wait(() => { lock (meta.Got) return meta.Got.Count(l => l == "METADATA * SET avatar " + mine) >= 2; }, 20000));
        await pictured.SetAvatarUrlAsync(ps, "");
        Check("pictures: taken away when cleared", await Wait(() => { lock (meta.Got) return meta.Got.Contains("METADATA * SET avatar"); }) && await WaitOwner("me", h => h is null));
        await pictured.DisconnectAsync(ps);
        WebPictures.AllowThisMachine = false;
    }

    // ── Under the worker lease: one process connects, every other client shows what it wrote and asks it ──────────
    {
        var logger = new AppErrorLogger(null!);
        var signals = new ClientSignals(logger);
        var spy = new RelaySpy();
        var relayFactory = new Factory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(profile, "relay.db")}").AddInterceptors(spy).Options);
        await using (var db = relayFactory.CreateDbContext()) await db.Database.EnsureCreatedAsync();

        var worker = new IrcService(relayFactory, logger, signals) { RelayOnSqlite = true, PickupWait = TimeSpan.FromSeconds(1) };
        var view   = new IrcService(relayFactory, logger, signals) { RelayOnSqlite = true, PickupWait = TimeSpan.FromSeconds(1) };

        var unreadableSignals = 0;
        signals.Received += p =>
        {
            // An older build's alarm path parses whatever it does not know, and logs an error if that fails.
            try { using var _ = System.Text.Json.JsonDocument.Parse(p); } catch (System.Text.Json.JsonException) { Interlocked.Increment(ref unreadableSignals); }
            worker.TryApplySignal(p);
            view.TryApplySignal(p);
        };
        signals.Start();

        await using var relayFake = new FakeIrc();
        await worker.StartAsync();
        await view.StartAsync();

        var seen = await view.SaveServerAsync(new IrcServer
        {
            Name = "Shared", Host = "127.0.0.1", Port = relayFake.Port, Tls = false, Nickname = "Ann",
            LoginKind = (int)IrcLoginKind.ServerPassword, AutoConnect = true,
        }, "login-secret");
        var id = seen.Server.Id;
        Check("relay: a login saved by a client is there for the worker", AppConfig.GetServicePassword(IrcService.PasswordReference(id)) == "login-secret");

        var refused = (await Catch(() => view.SendAsync(seen, "#fleet", "nobody home")))?.Message;
        Check("relay: nobody to ask, and it says so rather than wait for ever", refused?.Contains("not done", StringComparison.Ordinal) == true, refused ?? "none");
        await using (var db = relayFactory.CreateDbContext())
            Check("relay: and takes it back, so it is not sent minutes later", !await db.IrcCommands.AnyAsync(c => c.Kind == "send"));

        await worker.BecomeWorkerAsync();
        var held = worker.SessionOf(id);
        Check("relay: the worker has the server, and connects it by itself", held is not null && await Wait(() => held.State == IrcState.Connected), held?.State.ToString() ?? "none");
        Check("relay: the client shows it connected, by its nickname", await Wait(() => seen.State == IrcState.Connected && seen.Nick == "Ann"), $"{seen.State} {seen.Nick}");

        var shown = new ConcurrentQueue<IrcStoredMessage>();
        var sounds = new ConcurrentQueue<long>();
        var unreads = 0;
        view.MessageStored += m => shown.Enqueue(m);
        view.AlertArrived += m => sounds.Enqueue(m.Id);
        view.UnreadChanged += () => Interlocked.Increment(ref unreads);

        await view.JoinChannelAsync(seen, "#fleet", "chan-key", JabberRoomType.General);
        var kept = await relayFactory.CreateDbContext().IrcChannels.AsNoTracking().FirstAsync(c => c.ServerId == id);
        Check("relay: a channel's key is saved for the worker", AppConfig.GetServicePassword(IrcService.ChannelKeyReference(kept.Id)) == "chan-key");
        Check("relay: the worker joins it with the key", await Wait(() => { lock (relayFake.Got) return relayFake.Got.Contains("JOIN #fleet chan-key"); }));
        Check("relay: the client shows it joined, people and ranks", await Wait(() => seen.ChannelOf("#fleet") is { Joined: true } c
            && c.Members.Values.Any(m => m.Nick == "Bob" && m.Ranks == "ov")), string.Join(",", seen.ChannelOf("#fleet")?.Members.Values.Select(m => m.Ranks + m.Nick) ?? []));
        Check("relay: and the server's ranks, so an operator is known", IrcService.IsOperator(seen, "o") && !IrcService.IsOperator(seen, "v") && seen.IsChannelName("#x"));

        relayFake.Push(":Bob!b@h PRIVMSG #fleet :Ann are you there");
        Check("relay: a message the worker saves reaches the client", await Wait(() => shown.Any(m => m.Body == "Ann are you there")));
        Check("relay: with its sound, once", await Wait(() => sounds.Count == 1) && !await Wait(() => sounds.Count > 1, 600), sounds.Count.ToString());
        Check("relay: and its count", Volatile.Read(ref unreads) > 0);

        var sent = await view.SendAsync(seen, "#fleet", "from the client");
        Check("relay: a client's words go out through the worker, and come back saved", sent == "" && await Wait(() => shown.Any(m => m.Body == "from the client" && m.IsOutgoing)));
        var unknown = await view.SendAsync(seen, "#fleet", "/frobnicate now");
        Check("relay: what the worker says back is said here", unknown.Contains("/frobnicate", StringComparison.Ordinal), unknown);

        // ⚠️ A typed line can carry a password: it reaches the worker through the logins store, not the request row.
        await view.SendAsync(seen, "#fleet", "/msg NickServ IDENTIFY sekrit-pw");
        Check("relay: a typed password goes out through the worker", await Wait(() => { lock (relayFake.Got) return relayFake.Got.Any(l => l.Contains("IDENTIFY sekrit-pw")); }));
        await Task.Delay(500);
        await using (var db = relayFactory.CreateDbContext())
            Check("relay: and is not kept as a message", !await db.IrcMessages.AnyAsync(m => m.Body.Contains("sekrit-pw")));
        await view.SendAsync(seen, "#fleet", "/join #keyed chan-key2");
        Check("relay: /join with a key typed on a client joins with it", await Wait(() => { lock (relayFake.Got) return relayFake.Got.Contains("JOIN #keyed chan-key2"); }));

        var typing = new ConcurrentQueue<string>();
        view.TypingChanged += (_, conversation, nick, on) => { if (on) typing.Enqueue(conversation + "|" + nick); };
        relayFake.Push("@+typing=active :Bo|b!b@h TAGMSG #fleet");
        Check("relay: someone typing is shown, a nickname with a bar in it whole", await Wait(() => typing.Contains("#fleet|Bo|b")), string.Join(";", typing));
        await view.SendTypingAsync(seen, "#fleet", true);
        Check("relay: this person typing goes out through the worker", await Wait(() => { lock (relayFake.Got) return relayFake.Got.Any(l => l.Contains("+typing=active") && l.Contains("TAGMSG #fleet")); }));

        var reacted = 0;
        view.ReactionsChanged += (_, _) => Interlocked.Increment(ref reacted);
        await view.ReactAsync(seen, "#fleet", "#fleet", "", "👍");
        Check("relay: a reaction with no id to go to does nothing", Volatile.Read(ref reacted) == 0);
        relayFake.Push("@msgid=r1;time=2026-11-09T11:00:00.000Z :Bob!b@h PRIVMSG #fleet :react to me");
        await Wait(() => shown.Any(m => m.MessageId == "r1"));
        await view.ReactAsync(seen, "#fleet", "#fleet", "r1", "👍");
        Check("relay: a reaction goes out through the worker", await Wait(() => { lock (relayFake.Got) return relayFake.Got.Any(l => l.Contains("+draft/react=👍") && l.Contains("+draft/reply=r1")); }));
        Check("relay: and the client is told", await Wait(() => Volatile.Read(ref reacted) > 0));
        Check("relay: and shows it as this person's", (await view.ReactionsAsync(id, "#fleet", ["r1"], seen.Nick)).GetValueOrDefault("r1") is [{ Emoji: "👍", Mine: true }]);

        var workerUnreads = 0;
        worker.UnreadChanged += () => Interlocked.Increment(ref workerUnreads);
        await view.MarkReadAsync(id, "#fleet");
        Check("relay: reading on one client is seen by the others", await Wait(() => Volatile.Read(ref workerUnreads) > 0));

        var askedBans = await view.BansAsync(seen, "#fleet");
        Check("relay: a channel's bans, asked through the worker", askedBans.Count == 2 && askedBans[0].SetBy == "Bob", askedBans.Count.ToString());
        var relayRefusals = seen.RefusalCount;
        relayFake.Push(":fake.server 482 Ann #fleet :You're not a channel operator");
        Check("relay: a refusal reaches the client", await Wait(() => seen.RefusalCount > relayRefusals && seen.LastRefusal.Contains("not a channel operator")));
        relayRefusals = seen.RefusalCount;
        relayFake.Push(":fake.server 482 Ann #fleet :You're not a channel operator");
        Check("relay: and the same one again, again", await Wait(() => seen.RefusalCount > relayRefusals), $"{seen.RefusalCount} {relayRefusals}");
        var relayWhois = await view.WhoisAsync(seen, "Bob");
        Check("relay: who someone is, asked through the worker", relayWhois is { Found: true, RealName: "Bob Real" }, relayWhois?.RealName ?? "none");
        var listed = await view.ListChannelsAsync(seen);
        Check("relay: the server's channels, asked through the worker", listed.Any(l => l.Channel == "#ops" && l.People == 4 && l.Topic == "Ops chat"), listed.Count.ToString());

        // The new ones: settings, lists, taking back, read markers.
        Check("relay: the client shows the channel's settings, and that messages can be taken back", await Wait(() => seen.ChannelOf("#fleet") is { ModesKnown: true } c
            && c.Modes.GetValueOrDefault('l') == "25") && seen.CanRedact && seen.Support.ModesA.Contains('e') && seen.Support.Software == "ergo-2.19.1",
            string.Join(",", seen.ChannelOf("#fleet")?.Modes.Select(x => x.Key + "=" + x.Value) ?? []));
        await view.SetModesAsync(seen, "#fleet", "+k", ["relay-key"]);
        Check("relay: a key set through the worker", await Wait(() => { lock (relayFake.Got) return relayFake.Got.Contains("MODE #fleet +k relay-key"); }));
        relayFake.Push(":Ann!u@h MODE #fleet +k relay-key");
        Check("relay: the client sees a key, never its value", await Wait(() => seen.ChannelOf("#fleet")?.Modes.ContainsKey('k') == true) && seen.ChannelOf("#fleet")!.Modes['k'] == "");
        var relayExceptions = await view.ModeListAsync(seen, "#fleet", 'e');
        Check("relay: a list asked through the worker", relayExceptions is [{ Mask: "*!*@friend.host" }], relayExceptions.Count.ToString());
        var edited = 0;
        view.MessagesChanged += (_, _) => Interlocked.Increment(ref edited);
        await view.RedactAsync(seen, "#fleet", "r1");
        Check("relay: taking a message back goes through the worker, and the client is told", await Wait(() => { lock (relayFake.Got) return relayFake.Got.Contains("REDACT #fleet r1"); })
            && await Wait(() => Volatile.Read(ref edited) > 0));
        relayFake.Push("@msgid=mr1;time=2026-11-09T11:30:00.000Z :Bob!b@h PRIVMSG #fleet :read me");
        await Wait(() => shown.Any(m => m.MessageId == "mr1"));
        await view.MarkReadAsync(id, "#fleet");
        Check("relay: reading on a client tells the server, through the worker", await Wait(() => { lock (relayFake.Got) return relayFake.Got.Any(l => l.StartsWith("MARKREAD #fleet timestamp=2026-11-09T11:30:00")); }),
            string.Join(" | ", relayFake.Got.Where(l => l.StartsWith("MARKREAD"))));

        await view.SetRoomTypeAsync(seen, "#fleet", JabberRoomType.Intel);
        Check("relay: a channel's type reaches the connection", await Wait(() => held!.ChannelOf("#fleet")?.Channel.RoomType == JabberRoomType.Intel));
        await view.SetAlertModeAsync(seen, "#fleet", JabberRoomAlert.All);
        Check("relay: and its alert setting", await Wait(() => held!.ChannelOf("#fleet")?.Channel.AlertMode == JabberRoomAlert.All));

        await view.SetAwayAsync(seen, "lunch");
        Check("relay: away, through the worker, and shown", await Wait(() => seen.IsAway) && await Wait(() => { lock (relayFake.Got) return relayFake.Got.Any(l => l.StartsWith("AWAY") && l.Contains("lunch")); }));

        await view.DisconnectAsync(seen);
        Check("relay: a client can ask it to disconnect, and sees it", await Wait(() => held!.State == IrcState.Offline && seen.State == IrcState.Offline), $"{held!.State} {seen.State}");
        int gotAtReconnect;
        lock (relayFake.Got) gotAtReconnect = relayFake.Got.Count;
        view.Connect(seen);
        Check("relay: and to connect again", await Wait(() => held!.State == IrcState.Connected && seen.State == IrcState.Connected), $"{held!.State} {seen.State}");

        seen.Server.Name = "Renamed";
        await view.SaveServerAsync(seen.Server, null);
        Check("relay: a change to a server reaches the worker", await Wait(() => worker.SessionOf(id)?.Server.Name == "Renamed"));
        var other = await view.SaveServerAsync(new IrcServer { Name = "Second", Host = "127.0.0.1", Port = 1, Tls = false, Nickname = "Ann" }, null);
        Check("relay: a new server reaches the worker", await Wait(() => worker.SessionOf(other.Server.Id) is not null));
        await view.DeleteServerAsync(other.Server.Id);
        Check("relay: and removing one does", await Wait(() => worker.SessionOf(other.Server.Id) is null));

        // ⚠️ Left while still rejoining after the reconnect above: the JOIN on its way kept it again (fixed). Which comes
        // first is timing: the JOIN already sent is answered with a PART, or the JOIN not yet sent is never sent.
        await view.LeaveChannelAsync(seen, "#fleet");
        bool OutOfFleet()
        {
            lock (relayFake.Got)
            {
                var since = relayFake.Got.Skip(gotAtReconnect).ToList();
                return since.FindLastIndex(l => l.StartsWith("PART #fleet")) >= since.FindLastIndex(l => l.StartsWith("JOIN #fleet"));
            }
        }
        await Task.Delay(1500);
        Check("relay: leaving, through the worker", await Wait(() => seen.ChannelOf("#fleet") is null) && await Wait(OutOfFleet),
            $"{held!.ChannelOf("#fleet")?.Joined} {seen.ChannelOf("#fleet")?.Joined} | " + string.Join(" / ", relayFake.Got.Skip(gotAtReconnect).Where(l => l.Contains("#fleet"))));
        Check("relay: and stays left", !await Wait(() => held!.ChannelOf("#fleet") is not null, 2500)
            && !relayFactory.CreateDbContext().IrcChannels.AsNoTracking().Any(c => c.ServerId == id && c.Name == "#fleet" && !c.Left));

        Check("relay: something did go through the tables between them", spy.Written.Any(w => w.Contains("#fleet", StringComparison.Ordinal)));
        Check("relay: no login, channel key or typed password went through them",
            !spy.Written.Any(w => w.Contains("login-secret") || w.Contains("chan-key") || w.Contains("sekrit-pw") || w.Contains("relay-key")));
        Check("relay: every signal is something an older build can read", Volatile.Read(ref unreadableSignals) == 0);

        await worker.StandDownAsync();
        await using (var db = relayFactory.CreateDbContext())
            Check("relay: standing down leaves nothing saying it is connected", !await db.IrcLiveStates.AnyAsync());
        Check("relay: and the client shows it", await Wait(() => seen.State == IrcState.Offline), seen.State.ToString());

        await view.BecomeWorkerAsync();
        var mine = view.SessionOf(id)!;
        Check("relay: the next holder connects it itself", await Wait(() => mine.State == IrcState.Connected, 20000), mine.State.ToString());
        Check("relay: and the old one now shows that one's", await Wait(() => worker.SessionOf(id)?.State == IrcState.Connected), worker.SessionOf(id)?.State.ToString() ?? "none");
        var oldShown = new ConcurrentQueue<IrcStoredMessage>();
        worker.MessageStored += m => oldShown.Enqueue(m);
        relayFake.Push(":Bob!b@h PRIVMSG Ann :after the hand-over");
        Check("relay: the old one is told of what the new one saves", await Wait(() => oldShown.Any(m => m.Body == "after the hand-over")));

        await view.StopAsync();
        await worker.StopAsync();
        signals.Stop();
    }

    // ── Settings → Data Retention: IRC's old read messages go by their rule, unread ones stay ──────────────────────
    await using (var db = factory.CreateDbContext())
    {
        db.IrcMessages.AddRange(
            new IrcStoredMessage { ServerId = 9, Conversation = "dave", SentAt = "2000-01-01T00:00:00.000Z", IsRead = true, DedupeKey = "r-dm", MessageId = "x1" },
            new IrcStoredMessage { ServerId = 9, Conversation = "dave", SentAt = "2000-01-01T00:00:00.000Z", IsRead = false, DedupeKey = "r-dm-unread" },
            new IrcStoredMessage { ServerId = 9, Conversation = "#old", IsChannel = true, SentAt = "2000-01-01T00:00:00.000Z", IsRead = true, DedupeKey = "r-ch", MessageId = "x2" });
        db.IrcReactions.Add(new IrcReaction { ServerId = 9, Conversation = "#old", Target = "x2", Sender = "Bob", Emojis = "👍" });
        await db.SaveChangesAsync();
    }
    await DataRetentionService.PurgeChatAsync(factory, group: false, days: 30);
    await using (var db = factory.CreateDbContext())
    {
        var left = await db.IrcMessages.Where(m => m.ServerId == 9).Select(m => m.DedupeKey).OrderBy(k => k).ToListAsync();
        Check("retention: an old read direct message goes, unread stays, channels are the other rule's", left.SequenceEqual(["r-ch", "r-dm-unread"]), string.Join(",", left));
    }
    await DataRetentionService.PurgeChatAsync(factory, group: true, days: 30);
    await using (var db = factory.CreateDbContext())
    {
        Check("retention: the group rule takes old channel messages", !await db.IrcMessages.AnyAsync(m => m.DedupeKey == "r-ch"));
        Check("retention: and their reactions", !await db.IrcReactions.AnyAsync(r => r.ServerId == 9));
    }
}
finally
{
    try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

Console.WriteLine($"IRC check: {checks} check(s), {failures.Count} failure(s).");
foreach (var f in failures) Console.WriteLine(f);
return failures.Count == 0 ? 0 : 1;

static async Task<Exception?> Catch(Func<Task> act)
{
    try { await act(); return null; }
    catch (Exception e) { return e; }
}

sealed class Factory(DbContextOptions<AppDbContext> o) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(o);
}

/// <summary>Everything either side writes to the tables between them, as it is written: to show no secret goes there.</summary>
sealed class RelaySpy : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
{
    private readonly ConcurrentQueue<string> _written = new();

    public IEnumerable<string> Written => _written;

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
        Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        foreach (var command in eventData.Context!.ChangeTracker.Entries<IrcCommand>()) _written.Enqueue(command.Entity.Kind + " " + command.Entity.Args);
        foreach (var live in eventData.Context.ChangeTracker.Entries<IrcLiveState>()) _written.Enqueue(live.Entity.Json);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>
/// An IRC server in this process, scripted: logs in with CAP and SASL, answers JOIN with names and a history batch,
/// echoes messages back with ids and times, and passes on whatever a check pushes.
/// </summary>
sealed class FakeIrc : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private StreamWriter? _out;
    private TcpClient? _client;
    private int _msgid;

    public int Port { get; }
    public List<string> Got { get; } = [];
    public bool RefuseSasl { get; init; }
    public bool NoCaps { get; init; }
    public HashSet<string> Taken { get; init; } = [];

    /// <summary>Where each conversation was read up to, as MARKREAD set it.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> ReadMarks { get; } = new();

    /// <summary>The first connection is told ERROR while logging in, as a server throttling a quick reconnect does.</summary>
    public bool ErrorOnFirstLogin { get; init; }

    /// <summary>The first CAP REQ is refused whole, as a server that dislikes one capability in it does.</summary>
    public bool NakFirstRequest { get; init; }

    /// <summary>Never answers this client's PING: a connection that died without a word.</summary>
    public bool Silent { get; set; }

    /// <summary>More offered at CAP LS, as "sts=port=6697".</summary>
    public string ExtraCaps { get; init; } = "";

    /// <summary>Keeps people's pictures (draft/metadata-2), Bob's being this link.</summary>
    public bool Pictures { get; init; }
    public string BobPicture { get; set; } = "";

    private int _connections;
    private bool _nakked;

    public FakeIrc()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _client = client;
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    public void Push(string line)
    {
        lock (this) { try { _out?.Write(line + "\r\n"); _out?.Flush(); } catch (IOException) { } catch (ObjectDisposedException) { } }
    }

    /// <summary>The connection goes, as a network failing would take it.</summary>
    public void Drop() { try { _client?.Client.Shutdown(SocketShutdown.Both); _client?.Dispose(); } catch (Exception) { } }

    private async Task ServeAsync(TcpClient client)
    {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n" };
        lock (this) _out = writer;
        var connection = Interlocked.Increment(ref _connections);
        string nick = "", user = "";
        bool capping = false, welcomed = false, echo = false;

        void Send(string line) => Push(line);
        void Welcome()
        {
            if (welcomed || nick.Length == 0 || user.Length == 0 || capping) return;
            welcomed = true;
            Send($":fake.server 001 {nick} :Welcome");
            Send($":fake.server 004 {nick} fake.server ergo-2.19.1 iow beIiklmnst beIklov");
            // Logged in to an account, as a certificate would: what read markers are kept under.
            if (!NoCaps) Send($":fake.server 900 {nick} {nick}!u@h ann :You are now logged in as ann");
            Send($":fake.server 005 {nick} PREFIX=(ov)@+ CHANTYPES=# CASEMAPPING=rfc1459 NETWORK=Fake CHATHISTORY=100 MONITOR=10 CHANMODES=beI,k,l,imnst :are supported");
            Send($":fake.server 376 {nick} :End of MOTD");
        }

        while (true)
        {
            string? line;
            try { line = await reader.ReadLineAsync(); }
            catch (Exception) { return; }
            if (line is null) return;
            lock (Got) Got.Add(line);
            var m = EveConsole.Services.Irc.IrcMessage.Parse(line);
            if (m is null) continue;

            switch (m.Command)
            {
                case "CAP" when m[0] == "LS":
                    if (NoCaps) { Send($":fake.server 421 * CAP :Unknown command"); break; }
                    capping = true;
                    Send(":fake.server CAP * LS * :server-time message-tags echo-message batch");
                    Send(":fake.server CAP * LS :sasl=PLAIN draft/chathistory multi-prefix away-notify draft/message-redaction draft/read-marker draft/multiline=max-bytes=4096,max-lines=100"
                         + (Pictures ? " draft/metadata-2=before-connect,max-subs=100,max-keys=100" : "") + (ExtraCaps.Length > 0 ? " " + ExtraCaps : ""));
                    break;
                case "CAP" when m[0] == "REQ":
                    if (NakFirstRequest && !_nakked) { _nakked = true; Send($":fake.server CAP * NAK :{m.Last}"); break; }
                    echo = echo || m.Last.Contains("echo-message");
                    Send($":fake.server CAP * ACK :{m.Last}");
                    break;
                case "CAP" when m[0] == "END":
                    capping = false;
                    Welcome();
                    break;
                case "AUTHENTICATE" when m[0] == "PLAIN":
                    Send("AUTHENTICATE +");
                    break;
                case "AUTHENTICATE":
                    if (RefuseSasl) { Send($":fake.server 904 {nick} :SASL authentication failed"); break; }
                    Send($":fake.server 900 {nick} {nick}!u@h ann :You are now logged in as ann");
                    Send($":fake.server 903 {nick} :SASL authentication successful");
                    break;
                case "NICK":
                    if (Taken.Contains(m[0])) { Send($":fake.server 433 * {m[0]} :Nickname is already in use"); break; }
                    nick = m[0];
                    Welcome();
                    break;
                case "USER":
                    if (ErrorOnFirstLogin && connection == 1) { Send("ERROR :Closing Link: throttled, reconnecting too fast"); return; }
                    user = m[0];
                    if (NoCaps) capping = false;
                    Welcome();
                    break;
                case "PING":
                    if (!Silent) Send($":fake.server PONG fake.server :{m.Last}");
                    break;
                case "JOIN":
                    Send($":{nick}!u@h JOIN {m[0]}");
                    if (Pictures && BobPicture.Length > 0)
                    {
                        // As Ergo does: the pictures of who is there, in a batch, before the names.
                        Send($":fake.server BATCH +mj metadata {m[0]}");
                        Send($"@batch=mj :fake.server 761 * Bob avatar * {BobPicture}");
                        Send(":fake.server BATCH -mj");
                    }
                    Send($":fake.server 353 {nick} = {m[0]} :{nick} @+Bob!b@h Carol");
                    Send($":fake.server 366 {nick} {m[0]} :End of NAMES");
                    break;
                case "CHATHISTORY":
                    var target = m[1];
                    if (m[0] == "TARGETS")
                    {
                        Send(":fake.server BATCH +t draft/chathistory-targets");
                        Send("@batch=t :fake.server CHATHISTORY TARGETS Dave 2026-11-08T09:30:00.000Z");
                        Send(":fake.server BATCH -t");
                        break;
                    }
                    if (!target.StartsWith('#'))
                    {
                        Send($":fake.server BATCH +d1 chathistory {target}");
                        Send($"@batch=d1;time=2026-11-08T09:30:00.000Z;msgid=dm1 :{target}!d@h PRIVMSG {nick} :missed while away");
                        Send(":fake.server BATCH -d1");
                        break;
                    }
                    Send($":fake.server BATCH +h1 chathistory {target}");
                    Send($"@batch=h1;time=2026-11-08T09:00:00.000Z :{nick}!u@h JOIN {target}");
                    Send($"@batch=h1;time=2026-11-08T09:00:01.000Z;msgid=old1 :Carol!c@h PRIVMSG {target} :from history");
                    Send($"@batch=h1;time=2026-11-08T09:00:02.000Z :Carol!c@h PART {target}");
                    Send(":fake.server BATCH -h1");
                    break;
                case "LIST":
                    Send($":fake.server 321 {nick} Channel :Users  Name");
                    Send($":fake.server 322 {nick} #ops 4 :Ops chat");
                    Send($":fake.server 322 {nick} #fleet 3 :");
                    Send($":fake.server 323 {nick} :End of /LIST");
                    break;
                case "MODE" when m.Params.Count == 2 && m[1] == "+b":
                    Send($":fake.server 367 {nick} {m[0]} *!*@bad.host Bob!b@h 1700000000");
                    Send($":fake.server 367 {nick} {m[0]} Spammer!*@*");
                    Send($":fake.server 368 {nick} {m[0]} :End of channel ban list");
                    break;
                case "MODE" when m.Params.Count == 1 && m[0].StartsWith('#'):
                    Send($":fake.server 324 {nick} {m[0]} +ntl 25");
                    break;
                case "MODE" when m.Params.Count == 2 && m[1] == "+e":
                    Send($":fake.server 348 {nick} {m[0]} *!*@friend.host Bob!b@h 1700000000");
                    Send($":fake.server 349 {nick} {m[0]} :End of channel exception list");
                    break;
                case "MODE" when m.Params.Count == 2 && m[1] == "+I":
                    Send($":fake.server 347 {nick} {m[0]} :End of channel invite list");
                    break;
                case "REDACT" when m.Params.Count >= 2:
                    Send($":{nick}!u@h REDACT {m[0]} {m[1]}" + (m.Params.Count > 2 ? $" :{m[2]}" : ""));
                    break;
                case "MARKREAD":
                    if (m.Params.Count > 1) ReadMarks[m[0]] = m[1];
                    Send($":fake.server MARKREAD {m[0]} {ReadMarks.GetValueOrDefault(m[0], "*")}");
                    break;
                case "BATCH" when echo && m[0].StartsWith('+'):
                    Send($"@msgid=e{Interlocked.Increment(ref _msgid)};time={DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} :{nick}!u@h BATCH {m[0]} {m[1]} {m[2]}");
                    break;
                case "BATCH" when echo:
                    Send($":fake.server BATCH {m[0]}");
                    break;
                case "PRIVMSG" when echo && m.Tag("batch") is { } inBatch:
                    Send($"@batch={inBatch}{(m.Tag("draft/multiline-concat") is null ? "" : ";draft/multiline-concat")} :{nick}!u@h PRIVMSG {m[0]} :{m.Last}");
                    break;
                case "PRIVMSG" or "NOTICE" when echo:
                    Send($"@msgid=e{Interlocked.Increment(ref _msgid)};time={DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} :{nick}!u@h {m.Command} {m[0]} :{m.Last}");
                    break;
                case "WHOIS" when m[0] == "Bob":
                    Send($":fake.server 311 {nick} Bob b bob.host * :Bob Real");
                    Send($":fake.server 319 {nick} Bob :@#fleet");
                    Send($":fake.server 312 {nick} Bob fake.server :The fake server");
                    Send($":fake.server 301 {nick} Bob :at lunch");
                    Send($":fake.server 330 {nick} Bob bobacct :is logged in as");
                    Send($":fake.server 671 {nick} Bob :is using a secure connection");
                    Send($":fake.server 317 {nick} Bob 300 1700000000 :seconds idle, signon time");
                    Send($":fake.server 318 {nick} Bob :End of /WHOIS list");
                    break;
                case "WHOIS":
                    Send($":fake.server 401 {nick} {m[0]} :No such nick");
                    Send($":fake.server 318 {nick} {m[0]} :End of /WHOIS list");
                    break;
                case "METADATA" when Pictures && m.Params.Count >= 3 && m[1] == "SUB":
                    Send($":fake.server 770 {nick} {m[2]}");
                    break;
                case "METADATA" when Pictures && m.Params.Count >= 3 && m[1] == "SET":
                    Send(m.Params.Count > 3 ? $":fake.server 761 {nick} {nick} avatar * :{m[3]}" : $":fake.server 766 {nick} {nick} avatar :key not set");
                    break;
                case "METADATA" when Pictures && m.Params.Count >= 3 && m[1] == "GET":
                    Send(m[0] == "Bob" && BobPicture.Length > 0 ? $":fake.server 761 {nick} Bob avatar * :{BobPicture}" : $":fake.server 766 {nick} {m[0]} avatar :key not set");
                    break;
                case "QUIT":
                    Send("ERROR :Closing link");
                    return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Drop();
    }
}

/// <summary>Serves pictures over plain HTTP on this machine: two small ones of different colours, and one too big to be one.</summary>
sealed class PictureHost : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private int _served;

    public int Port { get; }
    public int Served => Volatile.Read(ref _served);

    public PictureHost()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    private static byte[] Png(SkiaSharp.SKColor colour)
    {
        using var bitmap = new SkiaSharp.SKBitmap(16, 16);
        bitmap.Erase(colour);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    var request = await reader.ReadLineAsync() ?? "";
                    while (await reader.ReadLineAsync() is { Length: > 0 }) { }
                    var path = request.Split(' ').ElementAtOrDefault(1) ?? "";
                    var body = path switch
                    {
                        "/bob.png" => Png(SkiaSharp.SKColors.Red),
                        "/bob2.png" => Png(SkiaSharp.SKColors.Blue),
                        "/me.png" => Png(SkiaSharp.SKColors.Green),
                        "/huge.png" => new byte[AvatarService.MostBytes + 10],
                        _ => [],
                    };
                    Interlocked.Increment(ref _served);
                    var head = body.Length > 0
                        ? $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(body);
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
    }
}
