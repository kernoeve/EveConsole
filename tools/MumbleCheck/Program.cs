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

    Check("echo: its native library loads here", MumbleEchoCanceller.Available, MumbleEchoCanceller.WhyNot?.Message ?? "");

    // Echo alone, across the delays real sound devices give.
    foreach (var delay in new[] { Rate * 40 / 1000, Rate * 120 / 1000, Rate * 280 / 1000 })
    {
        var played = SpeechLike(12, 1, 0.5);
        var echo = Room(played, delay, 3, 0.4);
        var cleanup = new MumbleVoiceCleanup { CancelEcho = true };
        cleanup.StartTalk();
        var result = Run(cleanup, played, echo);
        var removed = Db(echo, Rate * 4, Rate * 12) - Db(result, Rate * 4, Rate * 12);
        Check($"echo: gone at {delay * 1000 / Rate} ms late", removed > 25, $"{removed:F1} dB removed");
    }

    // ⚠️ Speakers and microphone as real devices give them, two streams at their own pace. A check that hands over one of
    // each in turn, which no sound device does, passed while a real room got almost nothing. Measured on a real machine:
    // speakers 10 ms at a time, a USB microphone 50 ms at a time, 40 to 56 ms after being opened.
    static double[] Streams(MumbleVoiceCleanup cleanup, double[] played, double[] heard, int micBurst = 2400, int micOpensAfter = 2400, double micFaster = 1, int pressEvery = 0)
    {
        const int Chunk = MumbleMixer.SamplesPer10Ms;
        var result = new double[heard.Length];
        var mixed = new short[Chunk];
        var frame = new short[Frame];
        var heardUpTo = micOpensAfter;
        var nextBurst = micOpensAfter + micBurst;
        var nextPress = pressEvery;
        for (var t = 0; t + Chunk <= played.Length; t += Chunk)
        {
            for (var i = 0; i < Chunk; i++) mixed[i] = (short)Math.Clamp(played[t + i] * 32768, short.MinValue, short.MaxValue);
            cleanup.Played(mixed);

            if (pressEvery > 0 && t >= nextPress)
            {
                cleanup.NewPress();
                nextPress += pressEvery;
            }

            if (t + Chunk < nextBurst) continue;
            nextBurst += micBurst;
            while (micOpensAfter + (heardUpTo - micOpensAfter + Frame) / micFaster <= t + Chunk && heardUpTo + Frame <= heard.Length)
            {
                for (var i = 0; i < Frame; i++) frame[i] = (short)Math.Clamp(heard[heardUpTo + i] * 32768, short.MinValue, short.MaxValue);
                cleanup.Process(frame);
                for (var i = 0; i < Frame; i++) result[heardUpTo + i] = frame[i] / 32768.0;
                heardUpTo += Frame;
            }
        }
        return result;
    }

    static double WorstStretch(double[] heard, double[] result, int from, int to)
    {
        var worst = double.MaxValue;
        for (var s = from; s + 2 <= to; s += 2) worst = Math.Min(worst, Db(heard, Rate * s, Rate * (s + 2)) - Db(result, Rate * s, Rate * (s + 2)));
        return worst;
    }

    {
        var played = SpeechLike(30, 1, 0.5);
        var echo = Room(played, Rate * 90 / 1000, 3, 0.4);

        var cleanup = new MumbleVoiceCleanup { CancelEcho = true };
        cleanup.StartTalk();
        var worst = WorstStretch(echo, Streams(cleanup, played, echo), 6, 30);
        Check("echo: gone with real devices' bursts, and stays gone", worst > 25, $"worst stretch {worst:F1} dB removed");

        // A press changes nothing: what was learned while nobody was talking is there for the first word, and the next.
        var pressed = new MumbleVoiceCleanup { CancelEcho = true };
        pressed.StartTalk();
        var worstPressed = WorstStretch(echo, Streams(pressed, played, echo, pressEvery: Rate * 3 + 1234), 6, 30);
        Check("echo: pressing the key does not disturb it", worstPressed > 25, $"worst stretch {worstPressed:F1} dB removed");

        // A microphone a whole second late to open: what it hears must be set against what was playing then, not when asked.
        var late = new MumbleVoiceCleanup { CancelEcho = true };
        late.StartTalk();
        var lateResult = Streams(late, played, echo, micOpensAfter: Rate);
        var worstLate = WorstStretch(echo, lateResult, 8, 30);
        Check("echo: finds its place with a microphone slow to open", worstLate > 25, $"worst stretch {worstLate:F1} dB removed");

        // Learned quickly: the reason for using this one.
        var fresh = new MumbleVoiceCleanup { CancelEcho = true };
        fresh.StartTalk();
        var soon = Streams(fresh, played, echo);
        var early = Db(echo, Rate * 3, Rate * 5) - Db(soon, Rate * 3, Rate * 5);
        Check("echo: down within seconds of starting", early > 20, $"{early:F1} dB removed by 3 to 5 s");
    }

    // ⚠️ A long quiet in what is played, with the microphone open throughout, as it now always is. Our own filter's fading
    // echo went to not-a-number here and silenced the microphone for good.
    {
        var voice = SpeechLike(10, 2, 0.25);
        var sound = SpeechLike(10, 1, 0.5);
        var heard = Room(sound, Rate * 90 / 1000, 3, 0.4);
        var played = new double[Rate * 80];
        var mic = new double[Rate * 80];
        sound.CopyTo(played, 0);
        heard.CopyTo(mic, 0);
        for (var i = 0; i < voice.Length; i++) mic[Rate * 70 + i] += voice[i];

        var cleanup = new MumbleVoiceCleanup { CancelEcho = true };
        cleanup.StartTalk();
        var result = Streams(cleanup, played, mic);
        var spoken = Db(result, Rate * 71, Rate * 80);
        Check("echo: a voice still gets through after a long quiet", Math.Abs(spoken - Db(voice, Rate, voice.Length)) < 3, $"{spoken:F1} dBFS against {Db(voice, Rate, voice.Length):F1}");
    }

    // Two clocks a little apart, as two USB devices are: the speakers' stream and the microphone's slowly part.
    foreach (var ppm in new[] { 10.0, -10.0, 60.0, -60.0 })
    {
        var played = SpeechLike(90, 1, 0.5);
        var echo = Room(played, Rate * 90 / 1000, 3, 0.4);
        var rate = 1 + ppm / 1e6;
        var heard = new double[echo.Length];
        for (var i = 0; i < heard.Length; i++)
        {
            var at = i / rate;
            var whole = (int)at;
            var part = at - whole;
            heard[i] = whole + 1 < echo.Length ? echo[whole] * (1 - part) + echo[whole + 1] * part : 0;
        }
        var cleanup = new MumbleVoiceCleanup { CancelEcho = true };
        cleanup.StartTalk();
        var result = Streams(cleanup, played, heard, micFaster: rate);
        var removed = Db(heard, Rate * 70, Rate * 88) - Db(result, Rate * 70, Rate * 88);
        Check($"echo: stays down with clocks {ppm:F0} ppm apart", removed > 15, $"{removed:F1} dB removed");
    }

    // Talking over it: the talker comes through.
    {
        var played = SpeechLike(14, 1, 0.5);
        var near = SpeechLike(14, 2, 0.35);
        for (var i = 0; i < Rate * 6; i++) near[i] = 0;   // it learns the room first
        var echo = Room(played, Rate * 90 / 1000, 3, 0.15);
        var cleanup = new MumbleVoiceCleanup { CancelEcho = true };
        cleanup.StartTalk();
        var result = Run(cleanup, played, Sum(echo, near));
        var lost = Db(near, Rate * 7, Rate * 14) - Db(result, Rate * 7, Rate * 14);
        Check("echo: someone talking over it still comes through", Math.Abs(lost) < 6, $"{lost:F1} dB changed");
    }

    // ⚠️ With levelling after it, as in real use: what is left must not be taken for a quiet voice and raised.
    {
        var played = SpeechLike(30, 1, 0.5);
        var echo = Room(played, Rate * 90 / 1000, 3, 0.4);

        static double[] Chain(double[] far, double[] heard)
        {
            var cleanup = new MumbleVoiceCleanup { CancelEcho = true };
            var leveler = new MumbleVoiceLeveler();
            cleanup.StartTalk();
            leveler.StartTalk();
            var mixed = new short[Frame];
            var frame = new short[Frame];
            var result = new double[heard.Length];
            for (var at = 0; at + Frame <= heard.Length; at += Frame)
            {
                for (var i = 0; i < Frame; i++) mixed[i] = (short)Math.Clamp(far[at + i] * 32768, short.MinValue, short.MaxValue);
                cleanup.Played(mixed);
                for (var i = 0; i < Frame; i++) frame[i] = (short)Math.Clamp(heard[at + i] * 32768, short.MinValue, short.MaxValue);
                cleanup.Process(frame);
                leveler.Process(frame, cleanup.LastFrameWasEcho);
                for (var i = 0; i < Frame; i++) result[at + i] = frame[i] / 32768.0;
            }
            return result;
        }

        var worst = WorstStretch(echo, Chain(played, echo), 8, 30);
        Check("levelling: does not raise what echo cancelling left", worst > 25, $"worst stretch {worst:F1} dB under the echo");

        var near = SpeechLike(30, 2, 0.2);
        for (var i = 0; i < Rate * 15; i++) near[i] = 0;
        var quietRoom = Room(played, Rate * 90 / 1000, 3, 0.1);
        var over = Chain(played, Sum(quietRoom, near));
        var alone = Chain(Silence(30), near);
        var lost = Db(alone, Rate * 18, Rate * 30) - Db(over, Rate * 18, Rate * 30);
        Check("levelling: someone talking over the echo is still levelled", Math.Abs(lost) < 5, $"{lost:F1} dB against the voice alone");

        var quiet = SpeechLike(10, 2, 0.03);
        var raised = Chain(Silence(10), quiet);
        var up = Db(raised, Rate * 4, Rate * 10) - Db(quiet, Rate * 4, Rate * 10);
        Check("levelling: a quiet voice with nothing playing is still raised", up > 6, $"{up:F1} dB");
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
        Check("echo: with nothing playing, a voice is left alone", changed < 2, $"{changed:F2} dB changed");
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
            after += result[i + MumbleNoiseSuppressor.BlockSize] * result[i + MumbleNoiseSuppressor.BlockSize];
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
    for (var i = 0; i < 15; i++) gate.Update(-60, -40);   // 300 ms
    Check("voice: held open over the gap between words", gate.IsOpen);
    for (var i = 0; i < 10; i++) gate.Update(-60, -40);   // 500 ms
    Check("voice: and closes after", !gate.IsOpen);
    Check("voice: exactly at the level counts", gate.Update(-40, -40));
    gate.Close();
    Check("voice: closes when told", !gate.IsOpen);
    Check("voice: level of silence", MumbleVoiceGate.LevelOf(new short[960]) == -96);
    var full = new short[960];
    full.AsSpan().Fill(short.MaxValue);
    Check("voice: level at full scale", Math.Abs(MumbleVoiceGate.LevelOf(full)) < 0.01);
}

// ── How someone is talking, by what each packet of their voice says ─────────────────────
{
    Check("talk: plain talk is Shout, blue in the standard client", MumbleService.TalkOf(0) == MumbleTalk.Shout);
    Check("talk: to a channel target is Channel, yellow", MumbleService.TalkOf(1) == MumbleTalk.Channel);
    Check("talk: to this client alone is Direct, purple", MumbleService.TalkOf(2) == MumbleTalk.Direct);

    var mixerOf = new MumbleMixer();
    var opus = new MumbleEncoder(40_000);
    var quiet = new short[MumbleEncoder.FrameSamples];
    var made = opus.Encode(quiet, 0, last: false);
    mixerOf.Receive(1, new MumbleVoicePacket(0, 5, 0, made.Opus, false));
    Check("talk: remembered from the packet", mixerOf.TargetOf(1, 5) == 0);
    mixerOf.Receive(1, new MumbleVoicePacket(1, 5, 2, made.Opus, false));
    Check("talk: and follows them changing key", mixerOf.TargetOf(1, 5) == 1);
    Check("talk: nobody known is plain", mixerOf.TargetOf(1, 99) == 0);

    var wire = new MumbleVoicePacket(1, 0, 0, made.Opus, false).Encode();
    var withSession = new byte[wire.Length + 1];
    withSession[0] = wire[0];
    withSession[1] = 5;
    wire.AsSpan(1).CopyTo(withSession.AsSpan(2));
    Check("talk: read off the wire", MumbleVoicePacket.Decode(withSession) is { Target: 1, Session: 5 });
}

// ── What is asked of a server about someone else ───────────────────────────────────────
{
    var mute = new PbMessage(MumbleClient.UserStateMessage(7, m => m.Bool(6, true)));
    Check("admin: a server mute names the person, and nothing else", mute.UInt(1) == 7 && mute.Bool(6) == true && mute.Bool(7) is null && mute.UInt(5) is null);

    var move = new PbMessage(MumbleClient.UserStateMessage(7, m => m.UInt(5, 0)));
    Check("admin: moving to the root says so", move.UInt(5) == 0);

    var kick = new PbMessage(MumbleClient.RemoveUserMessage(7, "Because", ban: false));
    Check("admin: a kick is no ban", kick.UInt(1) == 7 && kick.Text(3) == "Because" && kick.Bool(4) is null);

    // A ban comes as the server wrote it and goes back unchanged, as anyone left off the list is unbanned.
    var entry = new PbWriter().Bytes(1, System.Net.IPAddress.Parse("192.0.2.9").MapToIPv6().GetAddressBytes()).UInt(2, 128).Text(3, "Tester").Text(4, "abc").Text(5, "Because").Text(6, "2026-11-06T01:00:00").UInt(7, 0).ToArray();
    var read = MumbleBan.Read(entry);
    Check("bans: read", read is { Name: "Tester", Reason: "Because", Address: "192.0.2.9", DurationSeconds: 0 }, read.Address);
    var sent = new PbMessage(MumbleClient.BanListMessage([read, read]));
    Check("bans: go back exactly as they came", sent.AllBytes(1).Count == 2 && sent.AllBytes(1)[0].SequenceEqual(entry));
    Check("bans: sending is not asking", sent.Bool(2) == false);
    Check("bans: an empty list says nothing but that", new PbMessage(MumbleClient.BanListMessage([])).AllBytes(1).Count == 0);

    // A tone of its own, heard by this listener alone, with nobody talking and no sound going out.
    var cueMixer = new MumbleMixer();
    var cueBuffer = new short[480];
    cueMixer.PlayCue(starting: true);
    cueMixer.ReadMix(cueBuffer);
    Check("tone: plays with nobody talking", cueBuffer.Any(s => s != 0));
    for (var i = 0; i < 10; i++) cueMixer.ReadMix(cueBuffer);
    Check("tone: and ends", cueBuffer.All(s => s == 0));

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

    // ── The jitter buffer: a made-up network, on a clock of its own ──
    {
        var jitterEncoder = new MumbleEncoder(40_000);
        var made = new List<MumbleVoicePacket>();
        for (var f = 0; f < 100; f++)
            made.Add(jitterEncoder.Encode(Frame(), 0, last: false) with { Session = 3 });

        // What came out over time: each 10 ms, whether anyone was heard, and the sound. Sent every 20 ms unless told otherwise.
        (List<bool> Heard, List<short> Sound) Run(IEnumerable<(double At, MumbleVoicePacket Packet)> arrivals, double forMs, MumbleMixer? using_ = null)
        {
            var clock = 0.0;
            var m = using_ ?? new MumbleMixer();
            m.Clock = () => clock;
            var pending = new Queue<(double At, MumbleVoicePacket Packet)>(arrivals.OrderBy(a => a.At));
            var heard = new List<bool>();
            var sound = new List<short>();
            var chunk = new short[MumbleMixer.SamplesPer10Ms];
            for (; clock < forMs; clock += 10)
            {
                while (pending.Count > 0 && pending.Peek().At <= clock) m.Receive(1, pending.Dequeue().Packet);
                heard.Add(m.ReadMix(chunk) > 0);
                sound.AddRange(chunk);
            }
            return (heard, sound);
        }

        IEnumerable<(double, MumbleVoicePacket)> Steady(IEnumerable<MumbleVoicePacket> packets) =>
            packets.Select((p, i) => (20.0 * i, p));

        // How many stretches of silence fall between the first sound and the last.
        int Holes(List<bool> heard)
        {
            var first = heard.IndexOf(true);
            var last = heard.LastIndexOf(true);
            if (first < 0) return 0;
            var holes = 0;
            for (var i = first + 1; i <= last; i++)
                if (!heard[i] && heard[i - 1]) holes++;
            return holes;
        }

        var ten = 100 * 2;

        var steady = Run(Steady(made), 3000);
        Check("jitter: a clean connection has no holes", Holes(steady.Heard) == 0);
        Check("jitter: and nothing goes missing", steady.Heard.Count(h => h) >= ten - 10, steady.Heard.Count(h => h).ToString());
        Check("jitter: starting takes no longer than it used to", steady.Heard.IndexOf(true) <= 7, steady.Heard.IndexOf(true).ToString());

        // Two swapped on the way: what comes out is exactly what would have, with the same arrival time for both.
        var swapped = made.Select((p, i) => (At: 20.0 * i, Packet: p)).ToList();
        (swapped[40], swapped[41]) = ((swapped[41].At, made[40]), (swapped[40].At, made[41]));
        var inOrder = Run(swapped.Select((x, i) => (20.0 * i, made[i])), 3000);
        var reordered = Run(swapped.OrderBy(x => x.Packet.Sequence == made[41].Sequence ? 20.0 * 40 : x.Packet.Sequence == made[40].Sequence ? 20.0 * 41 : x.At), 3000);
        Check("jitter: packets arriving out of order play in order", inOrder.Sound.SequenceEqual(reordered.Sound));

        // One never arrives.
        var lostOne = Run(Steady(made).Where((_, i) => i != 50).Select((x, i) => (20.0 * (i >= 50 ? i + 1 : i), x.Item2)), 3000);
        Check("jitter: a lost packet leaves no hole", Holes(lostOne.Heard) == 0);
        Check("jitter: and takes no time out of what they said",
              Math.Abs(lostOne.Heard.Count(h => h) - steady.Heard.Count(h => h)) <= 1, $"{lostOne.Heard.Count(h => h)} vs {steady.Heard.Count(h => h)}");

        // ⚠️ The guess has to be sound, not silence, or the hole is only hidden from the count above.
        var missingPart = lostOne.Sound.Skip(steady.Heard.IndexOf(true) * MumbleMixer.SamplesPer10Ms + 50 * MumbleEncoder.FrameSamples).Take(MumbleEncoder.FrameSamples).Select(v => (double)v * v).ToList();
        Check("jitter: the guess is sound", Math.Sqrt(missingPart.Average()) > 2000, Math.Sqrt(missingPart.Average()).ToString("N0"));

        // Several in a row, then they carry on.
        var lostSome = Run(Steady(made).Where((_, i) => i is < 30 or > 32).Select(x => (x.Item1, x.Item2)), 3000);
        Check("jitter: a few lost in a row leave no hole", Holes(lostSome.Heard) == 0);
        Check("jitter: and take no time out either", Math.Abs(lostSome.Heard.Count(h => h) - steady.Heard.Count(h => h)) <= 1, lostSome.Heard.Count(h => h).ToString());

        // A long stretch lost, and then they carry on. The guess stops rather than droning on.
        var outage = Run(Steady(made).Where((_, i) => i is < 30 or > 60).Select(x => (x.Item1, x.Item2)), 3000);
        Check("jitter: a long loss doesn't get guessed at", outage.Heard.Count(h => h) < steady.Heard.Count(h => h) - 40, outage.Heard.Count(h => h).ToString());
        Check("jitter: and they come back after it", outage.Heard.LastIndexOf(true) > 200, outage.Heard.LastIndexOf(true).ToString());

        // Nothing more arrives, and no terminator either: they stop by themselves, with only a few frames guessed.
        var cutOff = Run(Steady(made.Take(50)), 3000);
        Check("jitter: when a connection stops, the sound stops soon after", cutOff.Heard.Count(h => h) <= 100 + 3 * 2 + 2, cutOff.Heard.Count(h => h).ToString());

        // Let go properly: nothing guessed after the last frame.
        var tail = made.Take(49).Append(made[49] with { Last = true });
        var letGo = Run(Steady(tail), 3000);
        Check("jitter: letting go adds no sound they didn't make", letGo.Heard.Count(h => h) == 100, letGo.Heard.Count(h => h).ToString());

        // A very short press is under the wait, and still plays.
        var blip = Run(Steady(new[] { made[0] with { Last = true } }), 500);
        Check("jitter: one short frame still plays", blip.Heard.Count(h => h) == 2, blip.Heard.Count(h => h).ToString());

        // The next press counts from zero, as a client may after letting go.
        var again = new List<(double, MumbleVoicePacket)>(Steady(tail));
        again.AddRange(made.Take(29).Append(made[29] with { Last = true }).Select((p, i) => (3000 + 20.0 * i, p)));
        var pressTwice = Run(again, 5000);
        Check("jitter: a new press may start counting again", pressTwice.Heard.Count(h => h) == 160, pressTwice.Heard.Count(h => h).ToString());

        // The next press carries on counting, a moment later: the pause isn't a loss to guess at.
        var carryOn = new List<(double, MumbleVoicePacket)>(Steady(made.Take(49).Append(made[49] with { Last = true })));
        carryOn.AddRange(made.Skip(58).Take(29).Append(made[87] with { Last = true }).Select((p, i) => (20.0 * 58 + 20.0 * i, p)));
        var carriedOn = Run(carryOn, 5000);
        Check("jitter: a pause between presses isn't guessed at", carriedOn.Heard.Count(h => h) == 160, carriedOn.Heard.Count(h => h).ToString());

        // The terminator overtakes the last frames: they still play.
        var overtaken = Steady(made.Take(50)).ToList();
        overtaken.Add((19.0 * 49, made[49] with { Last = true, Opus = [] }));
        var overtake = Run(overtaken, 3000);
        Check("jitter: a terminator ahead of the last frames doesn't cut them off", overtake.Heard.Count(h => h) >= 94, overtake.Heard.Count(h => h).ToString());

        // A packet whose place has gone by.
        var stale = Steady(made.Take(60)).ToList();
        stale.Add((20.0 * 58, made[5]));
        var late = Run(stale, 3000);
        Check("jitter: a packet too late to use isn't played", Math.Abs(late.Heard.Count(h => h) - Run(Steady(made.Take(60)), 3000).Heard.Count(h => h)) == 0);

        // Same packet twice.
        var doubled = Steady(made.Take(60)).Concat(Steady(made.Take(60)).Select(x => (x.Item1 + 3, x.Item2)));
        var twice = Run(doubled, 3000);
        Check("jitter: a packet arriving twice plays once", Math.Abs(twice.Heard.Count(h => h) - Run(Steady(made.Take(60)), 3000).Heard.Count(h => h)) <= 2, twice.Heard.Count(h => h).ToString());

        // Uneven by up to 80 ms: the old fixed 60 ms ran dry. It may run dry while learning, and then it has to stop.
        var random = new Random(4);
        var unevenMade = new List<MumbleVoicePacket>();
        for (var f = 0; f < 600; f++) unevenMade.Add(jitterEncoder.Encode(Frame(), 0, last: false) with { Session = 3 });
        var unevenMixer = new MumbleMixer();
        var uneven = Run(unevenMade.Select((p, i) => (20.0 * i + random.NextDouble() * (i < 100 ? 5 : 120), p)), 13_000, unevenMixer);
        Check("jitter: a longer wait means packets stop coming too late", unevenMixer.LatePackets <= 20, $"{unevenMixer.LatePackets} of 600 too late");
        Check("jitter: and few have to be guessed", unevenMixer.GuessedFrames <= 25, $"{unevenMixer.GuessedFrames} guessed");
        var unevenHoles = Holes(uneven.Heard);

        // The wait is learned once. Each press after the first has it from its first frame, and loses nothing to learning.
        var pressesRandom = new Random(9);
        var presses = new List<(double, MumbleVoicePacket)>();
        var pressAt = 0.0;
        var pressStart = new List<double>();
        for (var press = 0; press < 12; press++)
        {
            pressStart.Add(pressAt);
            var length = press == 0 ? 300 : 40;
            for (var f = 0; f < length; f++)
                presses.Add((pressAt + 20.0 * f + pressesRandom.NextDouble() * 120, jitterEncoder.Encode(Frame(), 0, last: f == length - 1) with { Session = 3 }));
            pressAt += 20.0 * length + 1000;
        }
        var pressMixer = new MumbleMixer();
        var firstPress = presses.Where(p => p.Item1 < pressStart[1]).ToList();
        Run(firstPress, pressStart[1], pressMixer);
        var lateAfterFirst = pressMixer.LatePackets;
        var guessedAfterFirst = pressMixer.GuessedFrames;
        var pressClock = pressStart[1];
        pressMixer.Clock = () => pressClock;
        var pressChunk = new short[MumbleMixer.SamplesPer10Ms];
        var pressPending = new Queue<(double, MumbleVoicePacket)>(presses.Where(p => p.Item1 >= pressStart[1]).OrderBy(p => p.Item1));
        for (; pressClock < pressAt; pressClock += 10)
        {
            while (pressPending.Count > 0 && pressPending.Peek().Item1 <= pressClock) pressMixer.Receive(1, pressPending.Dequeue().Item2);
            pressMixer.ReadMix(pressChunk);
        }
        var laterLate = pressMixer.LatePackets - lateAfterFirst;
        var laterGuessed = pressMixer.GuessedFrames - guessedAfterFirst;
        Check("jitter: later presses start with the wait already learned", laterLate + laterGuessed <= 60, $"{laterLate} too late, {laterGuessed} guessed, of 440");
        var learned = Holes([.. uneven.Heard.Skip(500)]);
        Check("jitter: an uneven connection gets a longer wait", learned == 0, $"{unevenHoles} in all, {learned} after the first 5 s");
        Check("jitter: uneven costs no words", uneven.Heard.Count(h => h) >= 600 * 2 - 40, uneven.Heard.Count(h => h).ToString());

        // A burst all at once, after a stall: dropped down to the limit, not kept as a delay.
        var flood = Run(unevenMade.Take(200).Select(p => (0.0, p)), 6000);
        Check("jitter: a flood is cut to the longest it may fall behind", flood.Heard.Count(h => h) <= 46, flood.Heard.Count(h => h).ToString());

        // A stall partway, then steady: the delay it left comes back down, without waiting for them to let go.
        var longTalk = new List<MumbleVoicePacket>();
        for (var f = 0; f < 800; f++) longTalk.Add(jitterEncoder.Encode(Frame(), 0, last: f == 799) with { Session = 3 });
        var stalled = Run(longTalk.Select((p, i) => (i is >= 100 and < 110 ? 20.0 * 110 : 20.0 * i, p)), 18_000);
        var lastHeardAt = stalled.Heard.LastIndexOf(true) * 10;
        Check("jitter: the delay after a stall comes back down", lastHeardAt <= 800 * 20 + 100, $"{lastHeardAt} ms, last sent at {800 * 20}");

        // Volume still applies to what's waiting, and to a guess.
        var halfMixer = new MumbleMixer();
        var clock2 = 0.0;
        halfMixer.Clock = () => clock2;
        var chunk2 = new short[MumbleMixer.SamplesPer10Ms];
        var halfSound = new List<short>();
        for (var f = 0; f < 200; f++, clock2 += 10)
        {
            if (f % 2 == 0 && f / 2 < 100 && f / 2 != 60) halfMixer.Receive(1, made[f / 2], 0.5);
            halfMixer.ReadMix(chunk2);
            halfSound.AddRange(chunk2);
        }
        var halfRms = Math.Sqrt(halfSound.Skip(halfSound.Count / 2).Take(halfSound.Count / 4).Select(v => (double)v * v).Average());
        Check("jitter: someone's volume still applies, guess included", halfRms is > 4000 and < 7000, halfRms.ToString("N0"));

        var damaged = new MumbleMixer { Clock = () => 0 };
        var junkAndGood = new short[MumbleMixer.SamplesPer10Ms];
        var damagedOk = true;
        try
        {
            damaged.Receive(1, new MumbleVoicePacket(0, 9, 0, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF], false));
            for (var f = 1; f < 8; f++) damaged.Receive(1, made[f] with { Session = 9, Sequence = f * 2 });
            for (var f = 0; f < 20; f++) damaged.ReadMix(junkAndGood);
        }
        catch (Exception) { damagedOk = false; }
        Check("jitter: a damaged packet costs only itself when its turn comes", damagedOk);
    }

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

// ── Logins on a shared database: the sealing, which needs no server ───────────
{
    var sealedLogin = SharedLogins.Seal("mumble-1", "a-login", "database-password");
    Check("shared login: not in the clear", !sealedLogin.Contains("a-login", StringComparison.Ordinal)
          && !sealedLogin.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("a-login")), StringComparison.Ordinal));
    Check("shared login: opens with the same database password, as on another PC", SharedLogins.Open("mumble-1", sealedLogin, "database-password") == "a-login");
    Check("shared login: not with another", SharedLogins.Open("mumble-1", sealedLogin, "another-password") is null);
    Check("shared login: not with no password", SharedLogins.Open("mumble-1", sealedLogin, "") is null);
    Check("shared login: not copied onto another row", SharedLogins.Open("mumble-2", sealedLogin, "database-password") is null);
    Check("shared login: sealed differently each time", SharedLogins.Seal("mumble-1", "a-login", "database-password") != sealedLogin);

    var parts = sealedLogin.Split('.');
    var flipped = Convert.FromBase64String(parts[3]);
    flipped[0] ^= 1;
    parts[3] = Convert.ToBase64String(flipped);
    Check("shared login: a changed value does not open", SharedLogins.Open("mumble-1", string.Join('.', parts), "database-password") is null);
    Check("shared login: nor does something else in the column", SharedLogins.Open("mumble-1", "dpapi:abc", "database-password") is null && SharedLogins.Open("mumble-1", "v1.!.!.!.!", "database-password") is null);
    Check("shared login: long ones fit, as a certificate is", SharedLogins.Open("c", SharedLogins.Seal("c", new string('x', 20_000), "p"), "p")?.Length == 20_000);

    Check("agent: the shared logins are refused however it is asked",
          EveConsole.Agent.Tools.Data.ReadOnlySql.Reject($"SELECT * FROM (TABLE \"{SharedLogins.Table}\") t", null) is not null
          && EveConsole.Agent.Tools.Data.ReadOnlySql.Reject($"select 1 from {SharedLogins.Table.ToLowerInvariant()}", null) is not null);
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

    var reference = MumbleService.PasswordReference(id);
    var configPath = Path.Combine(profile, "config.json");
    var secretsPath = Path.Combine(profile, "service-secrets.json");
    string ReadConfig() => File.Exists(configPath) ? File.ReadAllText(configPath) : "";
    Check("password: not in the settings at all", !ReadConfig().Contains("kept-secret", StringComparison.Ordinal) && !ReadConfig().Contains(reference, StringComparison.Ordinal));
    Check("password: in its own file, not in the clear", File.ReadAllText(secretsPath).Contains(reference, StringComparison.Ordinal) && (!OperatingSystem.IsWindows() || !File.ReadAllText(secretsPath).Contains("kept-secret", StringComparison.Ordinal)));
    Check("password: read back", AppConfig.GetServicePassword(reference) == "kept-secret");

    // ⚠️ What cost the owner their saved logins: a build that does not know the field writes the settings back without it.
    var settingsWere = ReadConfig();
    File.WriteAllText(configPath, "{}");
    Check("password: survives a build rewriting the settings", AppConfig.GetServicePassword(reference) == "kept-secret");

    // Saved by an earlier build, in the settings.
    File.WriteAllText(configPath, $$"""{ "servicePasswords": { "moved-one": "moved-value", "{{reference}}": "stale" } }""");
    Check("password: moved out of the settings", AppConfig.GetServicePassword("moved-one") == "moved-value");
    Check("password: what the file has wins over what is moved", AppConfig.GetServicePassword(reference) == "kept-secret");
    Check("password: gone from the settings once moved", !ReadConfig().Contains("moved-one", StringComparison.Ordinal) && !ReadConfig().Contains("stale", StringComparison.Ordinal));
    Check("password: moved for good", File.ReadAllText(secretsPath).Contains("moved-one", StringComparison.Ordinal));
    AppConfig.SetServicePassword("moved-one", null);
    Check("password: removing takes it from the file", !AppConfig.HasServicePassword("moved-one"));

    // ⚠️ Unreadable is not the same as nothing saved: that would offer no password, make a new certificate and write that over.
    var secretsWere = File.ReadAllText(secretsPath);
    File.WriteAllText(secretsPath, "{ not json");
    Check("password: a damaged file falls back to its backup", AppConfig.GetServicePassword(reference) == "kept-secret");
    var backupPath = secretsPath + ".bak";
    var backupWas = File.ReadAllText(backupPath);
    File.WriteAllText(backupPath, "{ not json");
    bool Refused(Action act) { try { act(); return false; } catch (IOException) { return true; } }
    Check("password: neither readable, reading says so instead of saying none", Refused(() => AppConfig.GetServicePassword(reference)) && Refused(() => AppConfig.HasServicePassword(reference)));
    Check("password: neither readable, saving refuses", Refused(() => AppConfig.SetServicePassword("another", "value")));
    Check("password: and nothing was written over", File.ReadAllText(secretsPath) == "{ not json");
    File.WriteAllText(secretsPath, secretsWere);
    File.WriteAllText(backupPath, backupWas);
    File.WriteAllText(configPath, settingsWere);

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

    // Mute comes back after a deafen, however it went in.
    var muting = await service.SaveServerAsync(new MumbleServer { Name = "Muting", Host = "voice.example", Username = "Ann" }, null);
    async Task<(bool Mute, bool Deaf)> Stored()
    {
        await using var db = factory.CreateDbContext();
        var row = await db.MumbleServers.AsNoTracking().SingleAsync(s => s.Id == muting.Server.Id);
        return (row.SelfMuted, row.SelfDeafened);
    }
    await service.ToggleDeafenAsync(muting);
    Check("deafen: from unmuted, nothing goes out", muting.Server.SelfDeafened && muting.Server.Silent);
    await service.ToggleDeafenAsync(muting);
    Check("deafen: taken off, it talks again", !muting.Server.SelfDeafened && !muting.Server.Silent);

    await service.ToggleMuteAsync(muting);
    Check("mute: muted", muting.Server is { SelfMuted: true, SelfDeafened: false });
    await service.ToggleDeafenAsync(muting);
    Check("deafen: from muted, deafened", muting.Server.SelfDeafened);
    Check("deafen: and what was chosen is saved as it was", await Stored() == (true, true));
    await service.ToggleDeafenAsync(muting);
    Check("deafen: taken off, still muted", muting.Server is { SelfMuted: true, SelfDeafened: false, Silent: true });
    Check("deafen: and saved so", await Stored() == (true, false));

    await service.ToggleMuteAsync(muting);
    await service.ToggleDeafenAsync(muting);
    Check("deafen: unmuted first, its own mute stays off while deafened", await Stored() == (false, true));
    var reread = new MumbleService(factory, new AppErrorLogger(null!));
    await reread.LoadServersAsync();
    await reread.ToggleDeafenAsync(reread.SessionOf(muting.Server.Id)!);
    Check("deafen: comes back right after a restart while deafened", await Stored() == (false, false));

    await service.SetMuteAsync(muting, true, true);
    await service.ToggleMuteAsync(muting);
    Check("mute: clicked while deafened means talk, as in the standard client", muting.Server is { SelfMuted: false, SelfDeafened: false });
    await service.DeleteServerAsync(muting.Server.Id);

    Check("listening: only those in the channel, and not deafened", MumbleService.ListeningIn(
    [
        new MumbleUser { Session = 1, Name = "In", ChannelId = 5 },
        new MumbleUser { Session = 2, Name = "Also", ChannelId = 5 },
        new MumbleUser { Session = 3, Name = "SelfDeaf", ChannelId = 5, SelfDeaf = true },
        new MumbleUser { Session = 4, Name = "ServerDeaf", ChannelId = 5, Deaf = true },
        new MumbleUser { Session = 5, Name = "Elsewhere", ChannelId = 6 },
    ], 5) == 2);

    // Each new service below is a client starting, and an emptied config file is a different PC.
    var settingsThen = ReadConfig();
    File.WriteAllText(configPath, "{}");
    var ticked = (await service.SaveServerAsync(new MumbleServer { Name = "Ticked", Host = "127.0.0.1", Port = 1, Username = "Ann", AutoConnect = true }, null)).Server.Id;
    var plain  = (await service.SaveServerAsync(new MumbleServer { Name = "Plain", Host = "127.0.0.1", Port = 1, Username = "Ann" }, null)).Server.Id;

    // ⚠️ Connecting happens on its own task, so give it time to leave Offline, or to not leave it: both answers need the wait.
    async Task<MumbleService> StartClient() { var c = new MumbleService(factory, new AppErrorLogger(null!)); await c.LoadServersAsync(); await Task.Delay(500); return c; }
    async Task<bool> SharedTick() { await using var db = factory.CreateDbContext(); return await db.MumbleServers.AnyAsync(s => s.AutoConnect); }

    var firstPc = await StartClient();
    Check("auto-connect: the first PC keeps a tick from when it was shared", MumbleService.AutoConnectsHere(ticked));
    Check("auto-connect: and connects", firstPc.SessionOf(ticked)!.State != MumbleState.Offline);
    Check("auto-connect: an unticked server stays off", !MumbleService.AutoConnectsHere(plain) && firstPc.SessionOf(plain)!.State == MumbleState.Offline);
    Check("auto-connect: the tick leaves the database", !await SharedTick());

    var firstConfig = ReadConfig();
    await firstPc.SaveServerAsync(firstPc.SessionOf(ticked)!.Server, null);
    Check("auto-connect: saving the server does not put it back", !await SharedTick());

    File.WriteAllText(configPath, "{}");
    var secondPc = await StartClient();
    Check("auto-connect: a second PC starting does not connect and throw the first off", secondPc.SessionOf(ticked)!.State == MumbleState.Offline);
    Check("auto-connect: and shows it unticked", !MumbleService.AutoConnectsHere(ticked));

    MumbleService.SetAutoConnectHere(plain, true);
    var secondAgain = await StartClient();
    Check("auto-connect: ticked on that PC, it connects there", secondAgain.SessionOf(plain)!.State != MumbleState.Offline);
    Check("auto-connect: and not to the one it never ticked", secondAgain.SessionOf(ticked)!.State == MumbleState.Offline);
    Check("auto-connect: nothing shared was changed by it", !await SharedTick());

    File.WriteAllText(configPath, firstConfig);
    var firstAgain = await StartClient();
    Check("auto-connect: the first PC is untouched by that", firstAgain.SessionOf(ticked)!.State != MumbleState.Offline && firstAgain.SessionOf(plain)!.State == MumbleState.Offline);

    MumbleService.SetAutoConnectHere(ticked, false);
    Check("auto-connect: unticking takes it away", !MumbleService.AutoConnectsHere(ticked));
    MumbleService.SetAutoConnectHere(ticked, true);

    foreach (var client in new[] { firstPc, secondPc, secondAgain, firstAgain })
        foreach (var s in client.Sessions.ToList()) await client.DisconnectAsync(s);
    await service.DeleteServerAsync(ticked);
    await service.DeleteServerAsync(plain);
    Check("auto-connect: deleting the server forgets it", !MumbleService.AutoConnectsHere(ticked));
    File.WriteAllText(configPath, settingsThen);

    var schema = EveConsole.Agent.AgentSchema.Build(factory.CreateDbContext());
    Check("agent: Mumble's tables are kept from the in-app agent",
          !schema.TableNames.Any(t => t.StartsWith("Mumble", StringComparison.OrdinalIgnoreCase)) && schema.TableNames.Count > 0);
    Check("agent: nor is it told of the shared logins", !schema.Has(SharedLogins.Table));
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
