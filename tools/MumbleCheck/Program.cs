// The Mumble client's rules: its wire formats, what is refused, what each key may do, and what is kept where.
// Needs no server and no sound hardware: what a real server does is checked against a throwaway one by hand.
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Mumble;
using EveConsole.ViewModels;
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

// ── Echo cancelling and noise suppression ──────────────────────────────────────────────
{
    const int Rate = MumbleMixer.SampleRate, Frame = MumbleEncoder.FrameSamples;

    // Something with speech's shape: a rising and falling buzz, in syllables, with pauses between them.
    static double[] SpeechLike(int seconds, int seed, double level)
    {
        var random = new Random(seed);
        var samples = new double[Rate * seconds];
        double phase = 0, pitch = 110 + seed * 17, smooth = 0;
        for (var at = 0; at < samples.Length;)
        {
            var syllable = random.Next(Rate / 10, Rate / 3);
            var target = 90 + random.NextDouble() * 90 + seed * 10;
            var loudness = level * (0.4 + random.NextDouble() * 0.6);
            for (var i = 0; i < syllable && at < samples.Length; i++, at++)
            {
                pitch += (target - pitch) * 0.0005;
                phase += 2 * Math.PI * pitch / Rate;
                var buzz = 0.0;
                for (var h = 1; h <= 12; h++) buzz += Math.Sin(phase * h + h * seed) / h;
                smooth += (random.NextDouble() * 2 - 1 - smooth) * 0.2;
                samples[at] = (buzz * 0.25 + smooth * 0.3) * loudness * Math.Sin(Math.PI * i / syllable);
            }
            at += random.Next(Rate / 20, Rate / 5);
        }
        return samples;
    }

    // A room: the sound arrives late, then again and again off the walls, fading.
    static double[] Room(double[] played, int delay, int seed, double level)
    {
        var random = new Random(seed);
        var echo = new double[played.Length];
        var taps = new (int At, double Gain)[40];
        for (var t = 0; t < taps.Length; t++)
            taps[t] = (delay + t * t * 3 + random.Next(30), level * Math.Exp(-t * 0.12) * (random.Next(2) == 0 ? 1 : -1) * (0.5 + random.NextDouble() * 0.5));
        foreach (var (at, gain) in taps)
            for (var i = at; i < played.Length; i++) echo[i] += played[i - at] * gain;
        return echo;
    }

    static double[] Run(MumbleVoiceCleanup cleanup, double[] played, double[] heard)
    {
        var mixed = new short[Frame];
        var frame = new short[Frame];
        var result = new double[heard.Length];
        for (var at = 0; at + Frame <= heard.Length; at += Frame)
        {
            for (var i = 0; i < Frame; i++) mixed[i] = (short)Math.Clamp(played[at + i] * 32768, short.MinValue, short.MaxValue);
            cleanup.Played(mixed);
            for (var i = 0; i < Frame; i++) frame[i] = (short)Math.Clamp(heard[at + i] * 32768, short.MinValue, short.MaxValue);
            cleanup.Process(frame);
            for (var i = 0; i < Frame; i++) result[at + i] = frame[i] / 32768.0;
        }
        return result;
    }

    static double Db(double[] samples, int from, int to)
    {
        double sum = 0;
        for (var i = from; i < to && i < samples.Length; i++) sum += samples[i] * samples[i];
        return 10 * Math.Log10(sum / Math.Max(1, to - from) + 1e-12);
    }

    static double[] Sum(double[] a, double[] b) => [.. a.Select((s, i) => s + b[i])];
    static double[] Silence(int seconds) => new double[Rate * seconds];

    // Echo alone, across the delays real sound devices give.
    foreach (var delay in new[] { Rate * 20 / 1000, Rate * 120 / 1000, Rate * 280 / 1000 })
    {
        var played = SpeechLike(12, 1, 0.5);
        var echo = Room(played, delay, 3, 0.4);
        var cleanup = new MumbleVoiceCleanup { CancelEcho = true };
        cleanup.StartTalk();
        var result = Run(cleanup, played, echo);
        var removed = Db(echo, Rate * 6, Rate * 12) - Db(result, Rate * 6, Rate * 12);
        Check($"echo: gone at {delay * 1000 / Rate} ms late", removed > 15, $"{removed:F1} dB removed");
    }

    // Talking over it: the talker comes through, and the echo does not.
    {
        var played = SpeechLike(14, 1, 0.5);
        var near = SpeechLike(14, 2, 0.35);
        for (var i = 0; i < Rate * 6; i++) near[i] = 0;   // it learns the room first
        var echo = Room(played, Rate * 90 / 1000, 3, 0.4);
        var cleanup = new MumbleVoiceCleanup { CancelEcho = true };
        cleanup.StartTalk();
        var result = Run(cleanup, played, Sum(echo, near));

        var lost = Db(near, Rate * 7, Rate * 14) - Db(result, Rate * 7, Rate * 14);
        Check("echo: someone talking over it still comes through", Math.Abs(lost) < 4, $"{lost:F1} dB changed");

        // How much of the echo is still in there, by how well what came out lines up with it: the voice does not.
        const int Late = MumbleEchoCanceller.BlockSize * 2;   // how far behind it comes out
        double together = 0, echoPower = 0, voiceTogether = 0, voicePower = 0;
        for (var i = Rate * 7; i < Rate * 14 - Late; i++)
        {
            together += result[i + Late] * echo[i];
            echoPower += echo[i] * echo[i];
            voiceTogether += result[i + Late] * near[i];
            voicePower += near[i] * near[i];
        }
        var echoDown = -20 * Math.Log10(Math.Abs(together / echoPower) + 1e-9);
        var voiceDown = -20 * Math.Log10(Math.Abs(voiceTogether / voicePower) + 1e-9);
        Check("echo: and what was over them is far less", echoDown > 15, $"echo down {echoDown:F1} dB");
        Check("echo: while the voice is barely touched", voiceDown < 4, $"voice down {voiceDown:F1} dB");
    }

    // The room changes, as turning around with a laptop does.
    {
        var played = SpeechLike(24, 1, 0.5);
        var one = Room(played, Rate * 60 / 1000, 3, 0.4);
        var two = Room(played, Rate * 150 / 1000, 5, 0.4);
        var heard = new double[played.Length];
        for (var i = 0; i < heard.Length; i++) heard[i] = i < Rate * 8 ? one[i] : two[i];
        var cleanup = new MumbleVoiceCleanup { CancelEcho = true };
        cleanup.StartTalk();
        var result = Run(cleanup, played, heard);
        var removed = Db(heard, Rate * 18, Rate * 24) - Db(result, Rate * 18, Rate * 24);
        Check("echo: follows a room that changes", removed > 20, $"{removed:F1} dB removed");
    }

    // Nothing played: the microphone goes through unharmed.
    {
        var near = SpeechLike(6, 2, 0.35);
        var cleanup = new MumbleVoiceCleanup { CancelEcho = true };
        cleanup.StartTalk();
        var result = Run(cleanup, Silence(6), near);
        var changed = Math.Abs(Db(near, Rate, Rate * 6) - Db(result, Rate, Rate * 6));
        Check("echo: with nothing playing, a voice is left alone", changed < 0.5, $"{changed:F2} dB changed");
    }

    // Noise: steady hiss and hum, with someone talking in it.
    {
        var near = SpeechLike(14, 2, 0.35);
        var noise = new double[near.Length];
        var random = new Random(9);
        for (var i = 0; i < noise.Length; i++) noise[i] = (random.NextDouble() * 2 - 1) * 0.03 + Math.Sin(2 * Math.PI * 60 * i / Rate) * 0.02;

        var cleanup = new MumbleVoiceCleanup { ReduceNoise = true };
        cleanup.StartTalk();
        var result = Run(cleanup, Silence(14), Sum(near, noise));

        // Measured in the pauses, where there is only the noise.
        double before = 0, after = 0; var pauses = 0;
        for (var i = Rate * 4; i < near.Length - 2048; i++)
        {
            if (Math.Abs(near[i]) > 1e-9) continue;
            var inPause = true;
            for (var k = i - 1100; k < i + 1100 && inPause; k += 50) inPause = Math.Abs(near[Math.Clamp(k, 0, near.Length - 1)]) < 1e-9;
            if (!inPause) continue;
            before += noise[i] * noise[i];
            after += result[i + MumbleEchoCanceller.BlockSize] * result[i + MumbleEchoCanceller.BlockSize];
            pauses++;
        }
        var quieter = 10 * Math.Log10(before / Math.Max(after, 1e-12));
        Check("noise: far quieter between words", pauses > 1000 && quieter > 10, $"{quieter:F1} dB over {pauses} samples");

        var voice = Db(Sum(near, noise), Rate * 4, Rate * 14) - Db(result, Rate * 4, Rate * 14);
        Check("noise: the voice survives", voice < 4, $"{voice:F1} dB less overall");
    }

    // Quick enough for the sound device's thread: well under the 20 ms each frame allows.
    {
        var played = SpeechLike(4, 1, 0.5);
        var cleanup = new MumbleVoiceCleanup { CancelEcho = true, ReduceNoise = true };
        cleanup.StartTalk();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Run(cleanup, played, Room(played, 2000, 3, 0.4));
        var share = clock.Elapsed.TotalSeconds / 4;
        Check("cleanup: takes a small share of the time it has", share < 0.25, $"{share:P1} of real time");
    }

    // Frames in and out stay the same size however blocks and frames fall against each other.
    {
        var cleanup = new MumbleVoiceCleanup { ReduceNoise = true };
        cleanup.StartTalk();
        var frame = new short[Frame];
        var ok = true;
        for (var i = 0; i < 2000; i++) { frame.AsSpan().Fill(1000); cleanup.Process(frame); if (frame.Length != Frame) ok = false; }
        Check("cleanup: runs on for as long as a press does", ok);
    }
}

// ── Voice activation ───────────────────────────────────────────────────────────────────
{
    var gate = new MumbleVoiceGate();
    Check("voice: starts closed", !gate.IsOpen);
    Check("voice: a quiet room stays closed", !gate.Update(-60, -40));
    Check("voice: a voice opens it at once", gate.Update(-30, -40));
    for (var i = 0; i < MumbleVoiceGate.HoldFrames - 1; i++) gate.Update(-60, -40);
    Check("voice: held open over the gap between words", gate.IsOpen);
    Check("voice: and closes after", !gate.Update(-60, -40));
    Check("voice: exactly at the level counts", gate.Update(-40, -40));
    gate.Close();
    Check("voice: closes when told", !gate.IsOpen);
    Check("voice: level of silence", MumbleVoiceGate.LevelOf(new short[960]) == -96);
    var full = new short[960];
    full.AsSpan().Fill(short.MaxValue);
    Check("voice: level at full scale", Math.Abs(MumbleVoiceGate.LevelOf(full)) < 0.01);
}

// ── What is asked of a server about someone else ───────────────────────────────────────
{
    var mute = new PbMessage(MumbleClient.UserStateMessage(7, m => m.Bool(6, true)));
    Check("admin: a server mute names the person, and nothing else", mute.UInt(1) == 7 && mute.Bool(6) == true && mute.Bool(7) is null && mute.UInt(5) is null);

    var move = new PbMessage(MumbleClient.UserStateMessage(7, m => m.UInt(5, 0)));
    Check("admin: moving to the root says so", move.UInt(5) == 0);

    var kick = new PbMessage(MumbleClient.RemoveUserMessage(7, "Because", ban: false));
    Check("admin: a kick is no ban", kick.UInt(1) == 7 && kick.Text(3) == "Because" && kick.Bool(4) is null);

    var ban = new PbMessage(MumbleClient.RemoveUserMessage(7, "", ban: true));
    Check("admin: a ban says so, and a blank reason is left out", ban.Bool(4) == true && ban.Text(3) is null);

    Check("admin: the bits are the published ones", (uint)MumblePermission.MuteDeafen == 0x10 && (uint)MumblePermission.Move == 0x20 && (uint)MumblePermission.Listen == 0x800
        && (uint)MumblePermission.Kick == 0x10000 && (uint)MumblePermission.Ban == 0x20000 && (uint)MumblePermission.SelfRegister == 0x80000);
}

// ── Which channels are open in the list ─────────────────────────────────────────────────
{
    // Root: Fleets (Alpha (Main, Dps), Bravo), Social (Lounge)
    (uint Id, uint? Parent)[] tree = [(0, null), (1, 0), (2, 1), (3, 2), (4, 2), (5, 1), (6, 0), (7, 6)];
    const uint Root = 0, Fleets = 1, Alpha = 2, Main = 3, Dps = 4, Bravo = 5, Social = 6, Lounge = 7;

    var t = new MumbleChannelTree();
    t.Update(tree, [], null);
    Check("channels: nobody there, all closed", !t.IsOpen(Root) && !t.IsOpen(Fleets) && !t.IsOpen(Main));

    t.Update(tree, [Main], null);
    Check("channels: someone arrives, the way to them opens", t.IsOpen(Root) && t.IsOpen(Fleets) && t.IsOpen(Alpha) && t.IsOpen(Main));
    Check("channels: and nothing else", !t.IsOpen(Dps) && !t.IsOpen(Bravo) && !t.IsOpen(Social) && !t.IsOpen(Lounge));

    t.Update(tree, [Main, Lounge], null);
    t.Update(tree, [Lounge], null);
    Check("channels: they leave, what only they kept open closes", !t.IsOpen(Fleets) && !t.IsOpen(Alpha) && !t.IsOpen(Main));
    Check("channels: what others keep open stays", t.IsOpen(Root) && t.IsOpen(Social));

    t.Toggle(Fleets);
    Check("channels: an empty one opens by hand", t.IsOpen(Fleets));
    t.Update(tree, [Lounge], null);
    Check("channels: and stays open while nothing changes", t.IsOpen(Fleets));
    t.Toggle(Fleets);
    Check("channels: and closes by hand", !t.IsOpen(Fleets));

    t.Toggle(Social);
    Check("channels: one with people closes by hand", !t.IsOpen(Social));
    t.Update(tree, [Lounge, Lounge], null);
    Check("channels: another arriving does not reopen it", !t.IsOpen(Social));
    t.Update(tree, [], null);
    t.Update(tree, [Lounge], null);
    Check("channels: once it has emptied, the next arrival opens it", t.IsOpen(Social));

    t.Update(tree, [], null);
    t.Toggle(Root);
    t.Toggle(Fleets);
    t.Toggle(Alpha);
    Check("channels: opened by hand with nobody there", t.IsOpen(Alpha));
    t.Update(tree, [Main], null);
    Check("channels: someone arriving takes over", t.IsOpen(Alpha));
    t.Update(tree, [], null);
    Check("channels: and leaving closes what they were in", !t.IsOpen(Root) && !t.IsOpen(Fleets) && !t.IsOpen(Alpha));

    t.Update(tree, [Main, Lounge], null);
    t.Toggle(Dps);
    Check("channels: an empty one under people, opened by hand", t.IsOpen(Dps));
    t.Update(tree, [Lounge], null);
    Check("channels: closes with what it was under", !t.IsOpen(Dps));

    t.Update(tree, [Lounge], Lounge);
    t.Toggle(Social);
    t.Update(tree, [Lounge], Lounge);
    Check("channels: closed by hand over the person using this", !t.IsOpen(Social));
    t.Update(tree, [Lounge, Main], Main);
    t.Toggle(Fleets);
    t.Update(tree, [Lounge, Main], Lounge);
    Check("channels: their own move always opens the way to them", t.IsOpen(Social));

    (uint Id, uint? Parent)[] loop = [(10, 11), (11, 10)];
    var odd = new MumbleChannelTree();
    odd.Update(loop, [10, 99], 10);
    odd.Toggle(11);
    odd.Update(loop, [], null);
    Check("channels: a server's circular channels end", !odd.IsOpen(10));
}

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
    Check("any key: every one has a name and its own code", GlobalHotkeyService.AnyKeys.All(k => k.Name.Length > 0 && k.WinVk != 0) && GlobalHotkeyService.AnyKeys.Select(k => k.WinVk).Distinct().Count() == GlobalHotkeyService.AnyKeys.Count);
    Check("any key: letters, with the system's own codes", GlobalHotkeyService.VkOf(Avalonia.Input.Key.T) == 0x54 && GlobalHotkeyService.VkName(0x54) == "T" && GlobalHotkeyService.VkToX11Keysym(0x54) == 0x74 && GlobalHotkeyService.VkToMacKeyCode(0x54) == 0x11);
    Check("any key: the keys on a normal keyboard", new[] { Avalonia.Input.Key.F1, Avalonia.Input.Key.F12, Avalonia.Input.Key.Space, Avalonia.Input.Key.LeftCtrl, Avalonia.Input.Key.RightAlt, Avalonia.Input.Key.CapsLock, Avalonia.Input.Key.OemTilde, Avalonia.Input.Key.NumPad5, Avalonia.Input.Key.D7 }.All(k => GlobalHotkeyService.VkOf(k) != 0));
    Check("any key: F1 and F24 where Windows has them", GlobalHotkeyService.VkOf(Avalonia.Input.Key.F1) == 0x70 && GlobalHotkeyService.VkOf(Avalonia.Input.Key.F24) == 0x87 && GlobalHotkeyService.VkName(0x7C) == "F13");
    Check("any key: Escape is kept for cancelling", GlobalHotkeyService.VkOf(Avalonia.Input.Key.Escape) == 0);
    Check("any key: nothing pressed is nothing", GlobalHotkeyService.VkOf(Avalonia.Input.Key.None) == 0);
    Check("mouse: the back button", GlobalHotkeyService.MouseButton(0x020B, 1u << 16) == (GlobalHotkeyService.VkMouseBack, true) && GlobalHotkeyService.MouseButton(0x020C, 1u << 16) == (GlobalHotkeyService.VkMouseBack, false));
    Check("mouse: the forward button", GlobalHotkeyService.MouseButton(0x020B, 2u << 16) == (GlobalHotkeyService.VkMouseForward, true));
    Check("mouse: the middle button", GlobalHotkeyService.MouseButton(0x0207, 0) == (GlobalHotkeyService.VkMouseMiddle, true) && GlobalHotkeyService.MouseButton(0x0208, 0) == (GlobalHotkeyService.VkMouseMiddle, false));
    Check("mouse: moving and the left button are not talking", GlobalHotkeyService.MouseButton(0x0200, 0) is null && GlobalHotkeyService.MouseButton(0x0201, 0) is null);
    // Speech-like: bursts of tone with pauses, at a given level, returning what the listener's level would be.
    static (double SpeechDb, double PeakDb, double FinalGain) Levelled(double db, double seconds, bool pop = false, double background = -74)
    {
        var leveler = new MumbleVoiceLeveler();
        leveler.StartTalk();
        var amplitude = Math.Pow(10, db / 20) * 32768 * Math.Sqrt(2);
        var hiss = Math.Pow(10, background / 20) * 32768;
        var random = new Random(7);
        double sum = 0, peak = 0;
        var counted = 0;
        var frames = (int)(seconds * 50);
        for (var f = 0; f < frames; f++)
        {
            var speaking = f % 50 < 38;
            var frame = new short[MumbleEncoder.FrameSamples];
            for (var i = 0; i < frame.Length; i++)
            {
                var v = (speaking ? Math.Sin(2 * Math.PI * 300 * (f * frame.Length + i) / 48000) * amplitude : 0) + (random.NextDouble() - 0.5) * hiss * 2;
                if (pop && f == 1) v = 30000 * (i % 2 == 0 ? 1 : -1);
                frame[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            }
            leveler.Process(frame);
            foreach (var s in frame) peak = Math.Max(peak, Math.Abs((double)s));
            if (speaking && f >= frames * 3 / 4) { foreach (var s in frame) sum += (double)s * s; counted += frame.Length; }
        }
        return (counted == 0 ? -120 : 10 * Math.Log10(sum / counted / (32768.0 * 32768.0)), 20 * Math.Log10(Math.Max(peak, 1) / 32768), leveler.GainDb);
    }

    // A long quiet stretch, then someone raising their voice: the moment that came through too hot.
    static (double LoudestFrameDb, double PeakDb) Raised()
    {
        var leveler = new MumbleVoiceLeveler();
        leveler.StartTalk();
        double loudest = -120, peak = 0;
        for (var f = 0; f < 300; f++)
        {
            var amplitude = Math.Pow(10, (f < 200 ? -42 : -15) / 20.0) * 32768 * Math.Sqrt(2);
            var frame = new short[MumbleEncoder.FrameSamples];
            for (var i = 0; i < frame.Length; i++) frame[i] = (short)Math.Clamp(Math.Sin(2 * Math.PI * 300 * (f * frame.Length + i) / 48000) * amplitude, short.MinValue, short.MaxValue);
            leveler.Process(frame);
            double sum = 0;
            foreach (var s in frame) { sum += (double)s * s; peak = Math.Max(peak, Math.Abs((double)s)); }
            loudest = Math.Max(loudest, 10 * Math.Log10(Math.Max(sum / frame.Length, 1) / (32768.0 * 32768.0)));
        }
        return (loudest, 20 * Math.Log10(peak / 32768));
    }

    var raised = Raised();
    Check("level: a sudden loud word stays below clipping, with room for Opus", raised.PeakDb <= -1.4, $"peak {raised.PeakDb:0.0}");
    Check("level: and is turned down at once", raised.LoudestFrameDb < -5, $"loudest frame {raised.LoudestFrameDb:0.0}");

    static double PauseDb(double seconds)
    {
        var leveler = new MumbleVoiceLeveler();
        leveler.StartTalk();
        var random = new Random(3);
        var amplitude = Math.Pow(10, -38 / 20.0) * 32768 * Math.Sqrt(2);
        var hiss = Math.Pow(10, -60 / 20.0) * 32768;
        double last = 0;
        for (var f = 0; f < seconds * 50; f++)
        {
            var speaking = f < 150;
            var frame = new short[MumbleEncoder.FrameSamples];
            for (var i = 0; i < frame.Length; i++) frame[i] = (short)((speaking ? Math.Sin(2 * Math.PI * 300 * (f * frame.Length + i) / 48000) * amplitude : 0) + (random.NextDouble() - 0.5) * hiss);
            leveler.Process(frame);
            double sum = 0;
            foreach (var s in frame) sum += (double)s * s;
            last = 10 * Math.Log10(Math.Max(sum / frame.Length, 0.01) / (32768.0 * 32768.0));
        }
        return last;
    }

    var pauseLevel = PauseDb(5);
    Check("level: the pauses between words are quieter than the voice raises them", pauseLevel < -60, $"{pauseLevel:0.0} dBFS");

    var quietVoice = Levelled(-38, 8);
    Check("level: a quiet voice comes up to the target", Math.Abs(quietVoice.SpeechDb - MumbleVoiceLeveler.TargetDb) < 2, $"{quietVoice.SpeechDb:0.0} dBFS, gain {quietVoice.FinalGain:0.0}");
    var loudVoice = Levelled(-12, 8);
    Check("level: a loud voice comes down to it", Math.Abs(loudVoice.SpeechDb - MumbleVoiceLeveler.TargetDb) < 2.5, $"{loudVoice.SpeechDb:0.0} dBFS, gain {loudVoice.FinalGain:0.0}");
    var veryQuiet = Levelled(-70 + 12, 12, background: -90);
    Check("level: never raises by more than the standard client's most", veryQuiet.FinalGain <= MumbleVoiceLeveler.MostRaiseDb && veryQuiet.FinalGain > 25, $"gain {veryQuiet.FinalGain:0.0}");
    Check("level: silence is not turned up to find something", Levelled(-120, 6).FinalGain == 0, $"gain {Levelled(-120, 6).FinalGain:0.0}");
    var clipping = Levelled(-1, 3);
    Check("level: never clips", clipping.PeakDb < -0.1, $"peak {clipping.PeakDb:0.00}");
    var popped = Levelled(-38, 8, pop: true);
    Check("level: the click at the start of a press is not sent", popped.PeakDb < quietVoice.PeakDb + 1, $"{popped.PeakDb:0.0} against {quietVoice.PeakDb:0.0}");
    Check("level: and does not throw off the level", Math.Abs(popped.SpeechDb - quietVoice.SpeechDb) < 0.5, $"{popped.SpeechDb:0.0} against {quietVoice.SpeechDb:0.0}");
    {
        var key = new byte[16]; var one = new byte[16]; var two = new byte[16];
        new Random(1).NextBytes(key); new Random(2).NextBytes(one); new Random(3).NextBytes(two);
        using var left = new MumbleCrypt();
        using var right = new MumbleCrypt();
        left.SetKey(key, one, two);
        right.SetKey(key, two, one);
        var payload = new byte[110];
        int firstByteOne = 0, unreadable = 0;
        for (var i = 0; i < 2000; i++)
        {
            new Random(i).NextBytes(payload);
            var packet = left.Encrypt(payload)!;
            if (packet[0] == 1) firstByteOne++;
            if (right.Decrypt(packet) is not { } back || !back.SequenceEqual(payload)) unreadable++;
        }
        Check("udp: no packet starts with the byte a server can take for a ping", firstByteOne == 0, $"{firstByteOne} did");
        Check("udp: skipping it costs nothing at the far end", unreadable == 0, $"{unreadable} unreadable of 2000");
    }

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
