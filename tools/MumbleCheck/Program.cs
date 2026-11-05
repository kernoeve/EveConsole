// The Mumble client's rules: its wire formats, what is refused, what each key may do, and what is kept where.
// Needs no server and no sound hardware: what a real server does is checked against a throwaway one by hand.
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Mumble;
using Microsoft.EntityFrameworkCore;

var checks = 0;
var failures = new List<string>();

void Check(string name, bool ok, string detail = "")
{
    checks++;
    if (!ok) failures.Add($"  FAIL  {name}{(detail.Length > 0 ? " | " + detail : "")}");
}

static string Hex(byte[] b) => Convert.ToHexString(b);

// The database is open: reading its bytes has to share it.
static string RawFile(string path)
{
    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(file, System.Text.Encoding.Latin1);
    return reader.ReadToEnd();
}

// ── Mumble's own number format, from its protocol document ───────────────────
{
    (long Value, string Bytes)[] vectors =
    [
        (0, "00"), (0x7F, "7F"),
        (0x80, "8080"), (0x3FFF, "BFFF"),
        (0x4000, "C04000"), (0x1FFFFF, "DFFFFF"),
        (0x200000, "E0200000"), (0xFFFFFFF, "EFFFFFFF"),
        (0x10000000, "F010000000"), (0xFFFFFFFFL, "F0FFFFFFFF"),
        (0x100000000L, "F40000000100000000"),
        (-1, "FC"), (-4, "FF"),
        (-5, "F804"),
    ];

    foreach (var (value, bytes) in vectors)
    {
        using var to = new MemoryStream();
        MumbleVarint.Write(to, value);
        Check($"varint: {value} writes as the document says", Hex(to.ToArray()) == bytes, Hex(to.ToArray()));

        var at = 0;
        var back = MumbleVarint.Read(to.ToArray(), ref at);
        Check($"varint: {value} reads back", back == value && at == to.Length, $"{back} at {at}");
    }

    var threw = false;
    try { var at = 0; MumbleVarint.Read(new byte[] { 0xC0 }, ref at); }
    catch (InvalidDataException) { threw = true; }
    Check("varint: cut short is refused, not read past the end", threw);
}

// ── Protobuf, by hand ─────────────────────────────────────────────────────────
{
    var bytes = new PbWriter().UInt(1, 300).Text(2, "héllo").Bool(3, true).UInt(4, 7).UInt(4, 8).Bytes(5, [1, 2, 3]).ToArray();
    Check("protobuf: a known encoding", Hex(bytes).StartsWith("08AC02", StringComparison.Ordinal), Hex(bytes));

    var m = new PbMessage(bytes);
    Check("protobuf: numbers", m.UInt(1) == 300);
    Check("protobuf: text is UTF-8", m.Text(2) == "héllo", m.Text(2) ?? "");
    Check("protobuf: true", m.Bool(3) == true);
    Check("protobuf: repeated fields keep every value", m.UInts(4).SequenceEqual([7UL, 8UL]));
    Check("protobuf: blobs", m.Bytes(5) is [1, 2, 3]);
    Check("protobuf: absent is absent, not zero", !m.Has(9) && m.UInt(9) is null && m.Text(9) is null);
}

// ── Voice packets ─────────────────────────────────────────────────────────────
{
    byte[] opus = [0xDE, 0xAD, 0xBE, 0xEF];

    var sent = new MumbleVoicePacket(1, 0, 9, opus, Last: false).Encode();
    Check("voice out: Opus, the shout target, no session", Hex(sent) == "81" + "09" + "04" + "DEADBEEF", Hex(sent));

    var lastOne = new MumbleVoicePacket(0, 0, 200, opus, Last: true).Encode();
    Check("voice out: channel talk is target 0, and the last frame says so", Hex(lastOne) == "80" + "80C8" + "A004" + "DEADBEEF", Hex(lastOne));

    // As the server relays it: with who it came from.
    byte[] relayed = [0x80 | 2, 5, 9, 0x84, 0x00 | 0x04, ..opus];
    relayed[3] = 0xA0; // 0x2004: 4 bytes, and the last
    var got = MumbleVoicePacket.Decode(relayed);
    Check("voice in: read", got is { Target: 2, Session: 5, Sequence: 9, Last: true } && got.Value.Opus.SequenceEqual(opus), got?.ToString() ?? "null");

    Check("voice in: an old codec is not played", MumbleVoicePacket.Decode(new byte[] { 0x00, 1, 1, 1, 0 }) is null);
    Check("voice in: a ping is not played", MumbleVoicePacket.Decode(new byte[] { 0x20, 1, 1 }) is null);

    var threw = false;
    try { MumbleVoicePacket.Decode(new byte[] { 0x80, 5, 9, 0x20 }); }
    catch (InvalidDataException) { threw = true; }
    Check("voice in: longer than it is, refused", threw);

    // A hostile server can send anything: nothing but "malformed" may come of it, for the connection to handle.
    var random = new Random(12345);
    var wrong = "";
    for (var i = 0; i < 20_000 && wrong.Length == 0; i++)
    {
        var junk = new byte[random.Next(0, 40)];
        random.NextBytes(junk);
        if (junk.Length > 0 && i % 2 == 0) junk[0] = (byte)(0x80 | junk[0] & 0x1F);
        try { MumbleVoicePacket.Decode(junk); }
        catch (InvalidDataException) { /* as it should */ }
        catch (Exception e) { wrong = e.GetType().Name + " " + Hex(junk); }
    }
    Check("voice in: random bytes never do worse than malformed", wrong.Length == 0, wrong);

    wrong = "";
    for (var i = 0; i < 20_000 && wrong.Length == 0; i++)
    {
        var junk = new byte[random.Next(0, 60)];
        random.NextBytes(junk);
        try { _ = new PbMessage(junk); }
        catch (InvalidDataException) { /* as it should */ }
        catch (Exception e) { wrong = e.GetType().Name + " " + Hex(junk); }
    }
    Check("protobuf: random bytes never do worse than malformed", wrong.Length == 0, wrong);
}

// ── Voice encryption, against the published OCB vectors ───────────────────────
{
    using var aes = System.Security.Cryptography.Aes.Create();
    var key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    aes.Key = key;
    var tag = new byte[16];

    MumbleCrypt.OcbEncrypt(aes, [], [], key, tag, modifyOnAttack: false);
    Check("crypt: the published tag of nothing", Hex(tag) == "BF3108130773AD5EC70EC69E7875A7B0", Hex(tag));

    var source = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
    var crypted = new byte[40];
    MumbleCrypt.OcbEncrypt(aes, source, crypted, key, tag, modifyOnAttack: false);
    Check("crypt: the published 40 bytes", Hex(crypted) == "F75D6BC8B4DC8D66B836A2B08B32A6369F1CD3C5228D79FD6C267F5F6AA7B231C7DFB9D59951AE9C", Hex(crypted));

    var back = new byte[40];
    var backTag = new byte[16];
    MumbleCrypt.OcbDecrypt(aes, crypted, back, key, backTag);
    Check("crypt: and back", back.SequenceEqual(source) && backTag.SequenceEqual(tag));

    // Two ends, as a client and server would be: each one's outgoing nonce the other's incoming.
    using var a = new MumbleCrypt();
    using var z = new MumbleCrypt();
    var n1 = new byte[16]; n1[0] = 1; n1[1] = 0x55;
    var n2 = new byte[16]; n2[0] = 2; n2[1] = 0x66;
    a.SetKey(key, n1, n2);
    z.SetKey(key, n2, n1);

    foreach (var size in new[] { 0, 1, 15, 16, 17, 31, 32, 33, 48, 200, 1000 })
    {
        var words = new byte[size];
        new Random(size).NextBytes(words);
        var sent = a.Encrypt(words)!;
        Check($"crypt: {size} bytes are 4 longer on the wire", sent.Length == size + MumbleCrypt.Overhead);
        Check($"crypt: {size} bytes arrive", z.Decrypt(sent)?.SequenceEqual(words) == true);
    }

    var once = a.Encrypt(new byte[] { 1, 2, 3 })!;
    Check("crypt: the first time", z.Decrypt(once) is not null);
    Check("crypt: the same packet twice is a replay, refused", z.Decrypt(once) is null);

    var tampered = a.Encrypt(new byte[] { 1, 2, 3, 4, 5 })!;
    tampered[^1] ^= 1;
    Check("crypt: a changed packet is refused", z.Decrypt(tampered) is null);
    Check("crypt: and does not spoil what comes next", z.Decrypt(a.Encrypt(new byte[] { 9 })!) is [9]);

    var first = a.Encrypt(new byte[] { 1 })!;
    var second = a.Encrypt(new byte[] { 2 })!;
    Check("crypt: arriving out of order, the later one", z.Decrypt(second) is [2]);
    Check("crypt: arriving out of order, the earlier one", z.Decrypt(first) is [1]);
    Check("crypt: and carries on", z.Decrypt(a.Encrypt(new byte[] { 3 })!) is [3]);

    var seen = 0;
    for (var i = 0; i < 1000; i++)
        if (z.Decrypt(a.Encrypt(new byte[] { (byte)i })!) is [var v] && v == (byte)i) seen++;
    Check("crypt: past the one-byte nonce's wraparound, several times over", seen == 1000, $"{seen}");

    var skipped = 0;
    for (var i = 0; i < 300; i++)
    {
        var p = a.Encrypt(new byte[] { 7 })!;
        if (i % 3 != 0) continue;
        if (z.Decrypt(p) is [7]) skipped++;
    }
    Check("crypt: packets lost on the way do not break it", skipped == 100, $"{skipped}");
    Check("crypt: and it counts them", z.Lost > 0);

    using var other = new MumbleCrypt();
    other.SetKey(new byte[16], n2, n1);
    Check("crypt: the wrong key is refused", other.Decrypt(a.Encrypt(new byte[] { 1, 2 })!) is null);

    using var empty = new MumbleCrypt();
    Check("crypt: no key yet, no packets", empty.Encrypt(new byte[] { 1 }) is null && empty.Decrypt(new byte[8]) is null);

    var threw = false;
    try { empty.SetKey(new byte[3], n1, n2); }
    catch (InvalidDataException) { threw = true; }
    Check("crypt: a server's wrong-sized key is refused, not used", threw && !empty.IsReady);

    var noise = new Random(99);
    var wrong = "";
    for (var i = 0; i < 20_000 && wrong.Length == 0; i++)
    {
        var junk = new byte[noise.Next(0, 80)];
        noise.NextBytes(junk);
        try { if (z.Decrypt(junk) is not null) wrong = "accepted " + Hex(junk); }
        catch (Exception e) { wrong = e.GetType().Name + " " + Hex(junk); }
    }
    Check("crypt: random bytes are refused without trouble", wrong.Length == 0 || wrong.StartsWith("accepted", StringComparison.Ordinal) , wrong);
}

// ── Encoder into mixer: a tone in, the same tone out ──────────────────────────
{
    var encoder = new MumbleEncoder(MumbleEncoder.BitrateFor(72_000));
    var phase = 0.0;
    short[] Frame()
    {
        var s = new short[MumbleEncoder.FrameSamples];
        for (var i = 0; i < s.Length; i++) { s[i] = (short)(Math.Sin(phase) * 16000); phase += 2 * Math.PI * 440 / MumbleMixer.SampleRate; }
        return s;
    }

    // ⚠️ One in, one out, as a live conversation goes: the mixer drops what piles up, to keep the delay short.
    (double Rms, int Read) Play(MumbleMixer mixer, int server, uint session, int frames, double gain = 1.0)
    {
        var collected = new List<short>();
        var read = 0;
        var buffer = new short[MumbleMixer.SamplesPer10Ms];
        for (var f = 0; f < frames; f++)
        {
            mixer.Receive(server, encoder.Encode(Frame(), 0, last: false) with { Session = session }, gain);
            if (mixer.ReadMix(buffer) > 0) read++;
            collected.AddRange(buffer);
        }

        // Past the codec's own start.
        var tail = collected.Skip(collected.Count / 2).Select(v => (double)v * v).ToList();
        return (tail.Count == 0 ? 0 : Math.Sqrt(tail.Average()), read);
    }

    var mixer = new MumbleMixer();
    var heard = Play(mixer, 1, 5, 60);
    var expected = 16000 / Math.Sqrt(2);
    Check("mixer: what went in comes out, about as loud", Math.Abs(heard.Rms - expected) < expected * 0.15, $"{heard.Rms:N0} vs {expected:N0}");
    Check("mixer: says who is talking", mixer.TalkingNow().Contains((1, 5)));

    var quietMixer = new MumbleMixer();
    var half = Play(quietMixer, 1, 5, 60, gain: 0.5);
    Check("mixer: one person's volume", Math.Abs(half.Rms - expected / 2) < expected * 0.1, $"{half.Rms:N0}");

    var lowMixer = new MumbleMixer { Volume = 0.25 };
    var low = Play(lowMixer, 1, 5, 60);
    Check("mixer: the speaker volume", Math.Abs(low.Rms - expected / 4) < expected * 0.1, $"{low.Rms:N0}");

    var two = new MumbleMixer();
    Play(two, 1, 5, 10);
    Play(two, 2, 5, 10);
    two.RemoveServer(1);
    Check("mixer: the same session on two servers is two people, and one server leaving takes only its own",
          !two.TalkingNow().Contains((1, 5)));

    var loud = new MumbleMixer { Volume = 10 };
    var buffer = new short[MumbleMixer.SamplesPer10Ms];
    for (var f = 0; f < 30; f++) loud.Receive(1, encoder.Encode(Frame(), 0, false) with { Session = 1 });
    var wrapped = false;
    var previous = (short)0;
    for (var f = 0; f < 30; f++)
    {
        loud.ReadMix(buffer);
        foreach (var v in buffer) { if (Math.Abs(v - previous) > 40000) wrapped = true; previous = v; }
    }
    Check("mixer: too loud clips, and never wraps around into noise", !wrapped);

    var junkMixer = new MumbleMixer();
    var survived = true;
    try { junkMixer.Receive(1, new MumbleVoicePacket(0, 9, 1, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF], false)); }
    catch (Exception) { survived = false; }
    Check("mixer: voice that does not decode costs only that frame", survived);

    Check("encoder: stays within what the server allows", MumbleEncoder.BitrateFor(40_000) <= 40_000 && MumbleEncoder.BitrateFor(0) > 0, $"{MumbleEncoder.BitrateFor(40_000)}");
}

// ── Each key belongs to one server ────────────────────────────────────────────
{
    var a = new MumbleServer { Id = 1, Name = "One", WhisperKey = 0x7C, ShoutKey = 0x7D };
    var b = new MumbleServer { Id = 2, Name = "Two", WhisperKey = 0x7E, ShoutKey = 0x7F };

    Check("keys: different keys are fine", MumbleService.KeyClash(b, [a, b]) is null);
    Check("keys: a server does not clash with itself", MumbleService.KeyClash(a, [a]) is null);
    Check("talk: shout is plain talk, as the standard client's Push to Talk", MumbleService.TargetOf(MumbleTalk.Shout) == 0);
    Check("talk: whisper uses a saved target", MumbleService.TargetOf(MumbleTalk.Channel) != 0);
    Check("talk: whisper's target is the channel alone, no links or subchannels",
          MumbleClient.ChannelTargetMessage(1, 7).SequenceEqual(new PbWriter().UInt(1, 1).Bytes(2, new PbWriter().UInt(2, 7).ToArray()).ToArray()));
    Check("keys: the same talk key on another server", MumbleService.KeyClash(new MumbleServer { Id = 2, WhisperKey = 0x7C }, [a]) == "One");
    Check("keys: my shout on their talk", MumbleService.KeyClash(new MumbleServer { Id = 2, ShoutKey = 0x7C }, [a]) == "One");
    Check("keys: my talk on their shout", MumbleService.KeyClash(new MumbleServer { Id = 2, WhisperKey = 0x7D }, [a]) == "One");
    Check("keys: one server's two keys must differ too", MumbleService.KeyClash(new MumbleServer { Id = 3, Name = "Three", WhisperKey = 0x7C, ShoutKey = 0x7C }, []) == "Three");
    Check("keys: no key clashes with no key", MumbleService.KeyClash(new MumbleServer { Id = 2 }, [new MumbleServer { Id = 1 }]) is null);
    Check("keys: a server with no name is named by its host", MumbleService.KeyClash(new MumbleServer { Id = 2, WhisperKey = 5 }, [new MumbleServer { Id = 1, Host = "voice.example", WhisperKey = 5 }]) == "voice.example");
}

// ── Text arrives as HTML and is shown as words ────────────────────────────────
{
    Check("text: tags go", MumbleService.PlainText("<p>Fleet <b>up</b></p>") == "Fleet up");
    Check("text: entities are read", MumbleService.PlainText("a &lt;b&gt; &amp; c") == "a <b> & c");
    Check("text: line breaks stay", MumbleService.PlainText("one<br>two<br/>three<BR />four") == "one\ntwo\nthree\nfour");
    Check("text: scripts are only words at most", !MumbleService.PlainText("<script>x()</script>").Contains('<'));
    Check("text: a picture does not fill the screen with its data", MumbleService.PlainText("""<img src="data:image/png;base64,AAAA"/>""").Length == 0);
}

// ── Certificates: nothing passes unless it is the one accepted ────────────────
{
    using var serverCert = MumbleClient.NewClientCertificate("voice.test");
    var fingerprintOf = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(serverCert.RawData));

    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;

    var heardPassword = false;
    using var stop = new CancellationTokenSource();
    var serving = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (OperationCanceledException) { return; }

            _ = Task.Run(async () =>
            {
                using var _1 = tcp;
                try
                {
                    await using var tls = new SslStream(tcp.GetStream());
                    await tls.AuthenticateAsServerAsync(serverCert);
                    var buffer = new byte[4096];
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    while (await tls.ReadAsync(buffer, timeout.Token) is var n and > 0)
                        if (System.Text.Encoding.UTF8.GetString(buffer, 0, n).Contains("secret-password", StringComparison.Ordinal))
                            heardPassword = true;
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
                {
                    // A client refusing the certificate hangs up, which is the point.
                }
            });
        }
    });

    async Task<MumbleException?> Try(string trusted)
    {
        await using var client = new MumbleClient(new MumbleLogin("127.0.0.1", port, "Ann", "secret-password", null, trusted, []));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await client.ConnectAsync(limit.Token); return null; }
        catch (MumbleException e) { return e; }
        catch (OperationCanceledException) { return null; }
    }

    var first = await Try("");
    Check("certificate: an unknown one stops the login", first?.Failure == MumbleFailure.Certificate, first?.Message ?? "no failure");
    Check("certificate: and shows what to compare", string.Equals(first?.CertificateFingerprint, fingerprintOf, StringComparison.OrdinalIgnoreCase), first?.CertificateFingerprint ?? "");
    Check("certificate: and names who it says it is", first?.CertificateSubject?.Contains("voice.test", StringComparison.Ordinal) == true, first?.CertificateSubject ?? "");

    var changed = await Try(new string('A', 64));
    Check("certificate: one other than the accepted one is refused", changed?.Failure == MumbleFailure.Certificate, changed?.Message ?? "no failure");

    await Task.Delay(200);
    Check("certificate: no password went to a server not trusted", !heardPassword);

    var accepted = await Try(fingerprintOf);
    Check("certificate: the accepted one passes, whatever else happens next", accepted?.Failure != MumbleFailure.Certificate, accepted?.Message ?? "");

    await stop.CancelAsync();
    listener.Stop();
    try { await serving; } catch (OperationCanceledException) { /* stopping it */ }
}

// ── A hostile server, against the real client ─────────────────────────────────
{
    // ⚠️ One packet of this used to overflow the stack, which nothing can catch and which ends the whole app. If this
    // comes back, this check does not fail so much as vanish, and that is the failure.
    var threw = false;
    try { var at = 0; MumbleVarint.Read(Enumerable.Repeat((byte)0xF8, 1_000_000).ToArray(), ref at); }
    catch (InvalidDataException) { threw = true; }
    Check("hostile: a megabyte of negation is refused, not recursed", threw);

    threw = false;
    try { MumbleVoicePacket.Decode(new byte[] { 0x80 }.Concat(Enumerable.Repeat((byte)0xF8, 1_000_000)).ToArray()); }
    catch (InvalidDataException) { threw = true; }
    Check("hostile: the same inside a voice packet", threw);

    using var cert = MumbleClient.NewClientCertificate("hostile.test");
    var trusted = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.RawData));
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;

    static byte[] Frame(MumbleMessage type, byte[] body)
    {
        var f = new byte[6 + body.Length];
        f[0] = (byte)((int)type >> 8); f[1] = (byte)type;
        f[2] = (byte)(body.Length >> 24); f[3] = (byte)(body.Length >> 16); f[4] = (byte)(body.Length >> 8); f[5] = (byte)body.Length;
        body.CopyTo(f, 6);
        return f;
    }

    var finished = new TaskCompletionSource();
    var serving = Task.Run(async () =>
    {
        using var tcp = await listener.AcceptTcpClientAsync();
        await using var tls = new SslStream(tcp.GetStream());
        await tls.AuthenticateAsServerAsync(cert);

        // Channels that try to go round in a circle.
        await tls.WriteAsync(Frame(MumbleMessage.ChannelState, new PbWriter().UInt(1, 0).Text(3, "Top").ToArray()));
        await tls.WriteAsync(Frame(MumbleMessage.ChannelState, new PbWriter().UInt(1, 1).UInt(2, 0).Text(3, "A").ToArray()));
        await tls.WriteAsync(Frame(MumbleMessage.ChannelState, new PbWriter().UInt(1, 2).UInt(2, 1).Text(3, "B").ToArray()));
        await tls.WriteAsync(Frame(MumbleMessage.ChannelState, new PbWriter().UInt(1, 1).UInt(2, 2).ToArray()));
        await tls.WriteAsync(Frame(MumbleMessage.ChannelState, new PbWriter().UInt(1, 3).UInt(2, 3).Text(3, "Self").ToArray()));
        await tls.WriteAsync(Frame(MumbleMessage.UserState, new PbWriter().UInt(1, 5).Text(3, "Ann").UInt(5, 2).ToArray()));

        // Links: a chain that closes on itself, one to a channel that does not exist, and one taken away again.
        await tls.WriteAsync(Frame(MumbleMessage.ChannelState, new PbWriter().UInt(1, 10).UInt(2, 0).Text(3, "L1").UInt(6, 11).UInt(6, 99).ToArray()));
        await tls.WriteAsync(Frame(MumbleMessage.ChannelState, new PbWriter().UInt(1, 11).UInt(2, 0).Text(3, "L2").UInt(6, 12).ToArray()));
        await tls.WriteAsync(Frame(MumbleMessage.ChannelState, new PbWriter().UInt(1, 12).UInt(2, 0).Text(3, "L3").UInt(6, 10).ToArray()));
        await tls.WriteAsync(Frame(MumbleMessage.ChannelState, new PbWriter().UInt(1, 13).UInt(2, 0).Text(3, "Lone").UInt(6, 10).ToArray()));
        await tls.WriteAsync(Frame(MumbleMessage.ChannelState, new PbWriter().UInt(1, 13).UInt(7, 10).ToArray()));

        // A voice packet of nothing but negations: the connection should not even notice.
        await tls.WriteAsync(Frame(MumbleMessage.UdpTunnel, new byte[] { 0x80 }.Concat(Enumerable.Repeat((byte)0xF8, 1_000_000)).ToArray()));

        // Keys of the wrong size: no UDP, and no trouble.
        await tls.WriteAsync(Frame(MumbleMessage.CryptSetup, new PbWriter().Bytes(1, new byte[3]).Bytes(2, new byte[3]).Bytes(3, new byte[3]).ToArray()));

        await tls.WriteAsync(Frame(MumbleMessage.ServerSync, new PbWriter().UInt(1, 5).ToArray()));
        await finished.Task;
    });

    await using (var client = new MumbleClient(new MumbleLogin("127.0.0.1", port, "Ann", "", null, trusted, [])))
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(limit.Token);

        var a = client.ChannelOf(1);
        var self = client.ChannelOf(3);
        Check("hostile: a channel cannot be moved inside its own child", a?.Parent == 0, a?.Parent?.ToString() ?? "");
        Check("hostile: a channel cannot be its own parent", self?.Parent is null, self?.Parent?.ToString() ?? "");
        Check("hostile: walking the tree ends", MumbleService.PathOf(client, 2) == "A/B", MumbleService.PathOf(client, 2));
        Check("hostile: finding a channel ends", MumbleService.FindChannel(client, "A/B/C/D") is null);
        Check("reach: a link of a link counts, and the walk ends", client.LinkedChannels(10).Order().SequenceEqual(new uint[] { 11, 12 }), string.Join(",", client.LinkedChannels(10)));
        Check("reach: from anywhere along the chain, never itself", client.LinkedChannels(12).Order().SequenceEqual(new uint[] { 10, 11 }), string.Join(",", client.LinkedChannels(12)));
        Check("reach: a link taken away is gone", client.LinkedChannels(13).Count == 0, string.Join(",", client.LinkedChannels(13)));
        Check("reach: a channel below another is not reached by being below it", client.LinkedChannels(0).Count == 0, string.Join(",", client.LinkedChannels(0)));
        Check("reach: an unknown channel reaches nothing", client.LinkedChannels(999).Count == 0);
        Check("hostile: still connected after all that", client.Me?.Name == "Ann");
        Check("hostile: wrong-sized keys mean no UDP, not a failure", !client.IsUsingUdp);
    }
    finished.TrySetResult();
    listener.Stop();
    try { await serving; } catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { /* the client hung up, which ends it */ }

    var flood = "<" + new string('<', 4_000_000);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var kept = MumbleService.PlainText(flood);
    Check("hostile: megabytes of '<' take no time", clock.ElapsedMilliseconds < 2000 && kept.Length > 0, $"{clock.ElapsedMilliseconds} ms");
    Check("text: an unclosed '<' stays as words", MumbleService.PlainText("a < b") == "a < b");
}

// ── What is kept, and where ───────────────────────────────────────────────────
var profile = Path.Combine(Path.GetTempPath(), "mumblecheck-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(profile);
AppConfig.UseProfile(profile);

var dbPath = Path.Combine(profile, "check.db");
var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").Options;
var factory = new Factory(options);

try
{
    await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();

    var service = new MumbleService(factory, new AppErrorLogger(null!));
    service.StartWithoutDevices();

    var session = await service.SaveServerAsync(new MumbleServer { Name = "Local", Host = "voice.example", Username = "Ann", WhisperKey = 0x7C, ShoutKey = 0x7D }, "kept-secret");
    var id = session.Server.Id;
    Check("save: given an id", id > 0);
    Check("save: not connected until asked", session.State == MumbleState.Offline);

    var config = File.ReadAllText(Path.Combine(profile, "config.json"));
    Check("password: not in the config file in the clear", !config.Contains("kept-secret", StringComparison.Ordinal) && config.Contains(MumbleService.PasswordReference(id), StringComparison.Ordinal));
    Check("password: read back", AppConfig.GetServicePassword(MumbleService.PasswordReference(id)) == "kept-secret");
    Check("password: not in the database", !RawFile(dbPath).Contains("kept-secret", StringComparison.Ordinal));

    await service.SaveServerAsync(session.Server, null);
    Check("password: saving without one keeps it", AppConfig.GetServicePassword(MumbleService.PasswordReference(id)) == "kept-secret");

    var user = new MumbleUser { Session = 3, Name = "Bob", Hash = "abc" };
    await service.SetUserVolumeAsync(session, user, 1.5);
    Check("volume: remembered by who they are, not by session", session.VolumeOf(user) == 1.5 && session.VolumeOf(new MumbleUser { Session = 99, Name = "Bob", Hash = "abc" }) == 1.5);
    Check("volume: nobody else's", session.VolumeOf(new MumbleUser { Session = 3, Name = "Bob", Hash = "other" }) == 1.0);
    await using (var db = factory.CreateDbContext())
        Check("volume: saved", await db.MumbleUserVolumes.AnyAsync(v => v.ServerId == id && v.UserKey == "abc" && v.Volume == 1.5));

    await service.DisconnectAsync(session);

    await service.DeleteServerAsync(id);
    await using (var db = factory.CreateDbContext())
    {
        Check("delete: the server goes", !await db.MumbleServers.AnyAsync());
        Check("delete: its volumes go with it", !await db.MumbleUserVolumes.AnyAsync());
    }
    Check("delete: its password goes", string.IsNullOrEmpty(AppConfig.GetServicePassword(MumbleService.PasswordReference(id))));
    Check("delete: and it is out of the list", service.Sessions.Count == 0);

    var schema = EveConsole.Agent.AgentSchema.Build(factory.CreateDbContext());
    Check("agent: Mumble's tables are kept from the in-app agent",
          !schema.TableNames.Any(t => t.StartsWith("Mumble", StringComparison.OrdinalIgnoreCase)) && schema.TableNames.Count > 0);
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(profile, recursive: true); } catch (IOException) { /* a temp folder, and the result is what matters */ }
}

Console.WriteLine($"Mumble check: {checks} check(s), {failures.Count} failure(s).");
foreach (var f in failures) Console.WriteLine(f);
return failures.Count == 0 ? 0 : 1;

sealed class Factory(DbContextOptions<AppDbContext> o) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(o);
}
