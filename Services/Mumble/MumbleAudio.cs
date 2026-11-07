using System.Diagnostics;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace EveConsole.Services.Mumble;

/// <summary>
/// Everyone being heard, mixed into one stream.
///
/// <para>No sound hardware in here: it takes packets in and hands samples out, so the path from a voice
/// packet to what would reach the speakers can be tested with none attached.</para>
///
/// <para>Each speaker has a jitter buffer. Packets wait in sequence order and are decoded only as they are needed, so
/// one arriving out of order still plays in its place, and one that never comes is covered by the codec's own guess at
/// what it held. How long they wait follows how uneven this speaker's packets have been arriving. ⚠️ A good connection
/// can't show any of this: test it with made-up delay and loss.</para>
/// </summary>
public sealed class MumbleMixer
{
    public delegate void MixedHandler(ReadOnlySpan<short> samples);

    /// <summary>What has just been handed to the speakers, silence included. On the sound device's thread.</summary>
    public event MixedHandler? Mixed;

    public const int SampleRate = 48_000;
    public const int SamplesPer10Ms = SampleRate / 100;

    /// <summary>The longest frame Opus can carry: 120 ms.</summary>
    private const int MaxFrame = SampleRate * 120 / 1000;

    /// <summary>The frame nearly everyone sends, in sequence numbers: 20 ms. Mumble counts them in tens of milliseconds.</summary>
    private const int UsualUnits = 2;

    private const int MsPerUnit = 10;

    /// <summary>The wait before starting someone new, until there is enough to go on for how uneven they are.</summary>
    private const int FirstWaitMs = 60;

    /// <summary>The shortest and longest wait. Longer than this and talking over each other gets hard.</summary>
    private const int LeastWaitMs = 40, MostWaitMs = 240;

    /// <summary>Past this behind, a speaker drops their oldest sound rather than falling further back.</summary>
    private const int MaxBacklogMs = 400;

    /// <summary>Frames in a row made up by the codec before stopping to wait. Past this its guess turns into a drone.</summary>
    private const int MostGuessed = 3;

    /// <summary>A missing stretch longer than this isn't worth guessing at: play on from what came next.</summary>
    private const int MostGapUnits = 20;

    /// <summary>A sequence number this far from where someone is playing means they started counting again.</summary>
    private const int RestartUnits = 100;

    /// <summary>Packets in a row that far behind before they count as a restart.</summary>
    private const int FarBehindIsRestart = 3;

    /// <summary>Frames in a row well ahead of the wait, before one is skipped to bring the delay back down.</summary>
    private const int AheadBeforeSkip = 50;

    /// <summary>How many recent arrivals say how uneven someone is: 2 seconds of 20 ms frames.</summary>
    private const int ArrivalsKept = 100;

    private const int ArrivalsEnough = 8;

    private static readonly TimeSpan HeardFor = TimeSpan.FromMilliseconds(350);

    private readonly record struct Waiting(byte[] Opus, double Gain);

    private sealed class Speaker
    {
        public required IOpusDecoder Decoder { get; init; }
        public readonly SortedList<long, Waiting> Packets = [];
        public readonly Queue<short> Samples = new();
        public readonly double[] Arrivals = new double[ArrivalsKept];
        public int ArrivalCount, ArrivalAt;
        public bool Playing;
        public DateTime LastHeard;
        public bool Ended;
        public int Target;

        /// <summary>The sequence number due next, or -1 between one stretch of talking and the next.</summary>
        public long Next = -1;

        public int FrameSamples = MumbleEncoder.FrameSamples;
        public double Gain = 1;
        public int Guessed;
        public int Ahead;
        public int FarBehind;

        /// <summary>How uneven they were last time, kept across the gap between one press and the next.</summary>
        public double LastUnevenMs = -1;

        /// <summary>Extra wait after running dry, because the last while was worse than the arrivals say.</summary>
        public int ExtraMs;
    }

    private readonly object _gate = new();
    private readonly Dictionary<(int Server, uint Session), Speaker> _speakers = [];
    private readonly short[] _decoded = new short[MaxFrame];
    private readonly double[] _sorting = new double[ArrivalsKept];
    private readonly Queue<short> _cue = new();

    /// <summary>The time in milliseconds. A test sets this to run a made-up network faster than real time.</summary>
    public Func<double> Clock { get; set; } = static () => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>Packets that came too late to play, since this mixer started.</summary>
    public long LatePackets { get; private set; }

    /// <summary>Frames the codec had to make up, since this mixer started.</summary>
    public long GuessedFrames { get; private set; }

    /// <summary>Plays a short tone to this listener alone: rising for starting to talk, falling for stopping.</summary>
    public void PlayCue(bool starting)
    {
        const int Length = SampleRate * 80 / 1000;
        var phase = 0.0;
        lock (_gate)
        {
            _cue.Clear();
            for (var i = 0; i < Length; i++)
            {
                var along = (double)i / Length;
                phase += 2 * Math.PI * (starting ? 480 + 240 * along : 720 - 240 * along) / SampleRate;
                _cue.Enqueue((short)(Math.Sin(phase) * Math.Sin(Math.PI * along) * 4000));
            }
        }
    }

    /// <summary>Everything, from 0 silent to 1 as sent. Anyone's own volume multiplies this.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>
    /// Takes one packet of someone's voice.
    /// </summary>
    /// <param name="gain">This person's own volume, 1 as sent.</param>
    public void Receive(int server, MumbleVoicePacket packet, double gain = 1.0)
    {
        lock (_gate)
        {
            var key = (server, packet.Session);
            if (!_speakers.TryGetValue(key, out var speaker))
                _speakers[key] = speaker = new Speaker { Decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1) };

            var hadEnded = speaker.Ended;
            speaker.LastHeard = DateTime.UtcNow;
            speaker.Ended = packet.Last;
            speaker.Target = packet.Target;

            // A terminator carrying no sound only says they let go.
            if (packet.Opus.Length == 0) return;

            var sequence = packet.Sequence;
            var farBehind = speaker.Next >= 0 && sequence < speaker.Next - RestartUnits;
            speaker.FarBehind = farBehind ? speaker.FarBehind + 1 : 0;

            // ⚠️ Far behind only counts when it keeps happening: one very late packet isn't someone starting again, and
            // starting over for it would throw away everything waiting behind it.
            if (speaker.Next >= 0 && (sequence > speaker.Next + RestartUnits || speaker.FarBehind >= FarBehindIsRestart || hadEnded && sequence < speaker.Next))
                StartOver(speaker);

            // ⚠️ Too late to use. Its place has already been played, as a guess or as what came after it.
            if (speaker.Next >= 0 && sequence < speaker.Next)
            {
                LatePackets++;
                return;
            }

            speaker.Packets.TryAdd(sequence, new Waiting(packet.Opus, gain));

            speaker.Arrivals[speaker.ArrivalAt] = Clock() - sequence * MsPerUnit;
            speaker.ArrivalAt = (speaker.ArrivalAt + 1) % ArrivalsKept;
            speaker.ArrivalCount = Math.Min(speaker.ArrivalCount + 1, ArrivalsKept);

            while (BufferedMs(speaker) > MaxBacklogMs && speaker.Packets.Count > 1)
            {
                speaker.Packets.RemoveAt(0);
                speaker.Next = speaker.Packets.Keys[0];
            }
        }
    }

    /// <summary>The sound waiting for someone: what's decoded and what isn't, gaps counted as they'll take that long too.</summary>
    private static int BufferedMs(Speaker speaker)
    {
        var decoded = speaker.Samples.Count / (SamplesPer10Ms / MsPerUnit);
        if (speaker.Packets.Count == 0) return decoded;
        var from = speaker.Next >= 0 ? speaker.Next : speaker.Packets.Keys[0];
        return decoded + (int)(speaker.Packets.Keys[^1] - from) * MsPerUnit + speaker.FrameSamples / (SamplesPer10Ms / MsPerUnit);
    }

    /// <summary>How far apart this speaker's arrivals have been, from most packets to the latest of them.</summary>
    private double UnevenMs(Speaker speaker)
    {
        if (speaker.ArrivalCount < ArrivalsEnough) return speaker.LastUnevenMs;
        Array.Copy(speaker.Arrivals, _sorting, speaker.ArrivalCount);
        Array.Sort(_sorting, 0, speaker.ArrivalCount);
        return _sorting[speaker.ArrivalCount * 98 / 100] - _sorting[0];
    }

    /// <summary>How much to have waiting before playing: enough to ride over the bumps seen so far, and one frame more.</summary>
    private int WaitMs(Speaker speaker)
    {
        var uneven = UnevenMs(speaker);
        var wait = uneven < 0 ? FirstWaitMs : (int)uneven + speaker.FrameSamples / (SamplesPer10Ms / MsPerUnit);
        return Math.Clamp(wait + speaker.ExtraMs, LeastWaitMs, MostWaitMs);
    }

    /// <summary>Between one stretch of talking and the next: the sequence may restart, and what's unplayed won't be missed.</summary>
    private void StartOver(Speaker speaker)
    {
        var uneven = UnevenMs(speaker);
        if (uneven >= 0) speaker.LastUnevenMs = uneven;
        speaker.ArrivalCount = speaker.ArrivalAt = 0;
        speaker.Packets.Clear();
        speaker.Next = -1;
        speaker.Playing = false;
        speaker.Guessed = speaker.Ahead = 0;
        speaker.ExtraMs /= 2;
        speaker.Decoder.ResetState();
    }

    /// <summary>Puts the next frame's sound in the queue. False when there's nothing to go on for now.</summary>
    private bool NextFrame(Speaker speaker)
    {
        if (speaker.Packets.Count == 0)
        {
            // Stopped talking means stopped: a guess here would be a sound they never made.
            if (speaker.Ended || speaker.Guessed >= MostGuessed) return false;
            Guess(speaker);
            return true;
        }

        var first = speaker.Packets.Keys[0];
        if (first > speaker.Next)
        {
            // Lost on the way, as far as anyone can tell: the wait for it is over.
            if (first - speaker.Next <= MostGapUnits && speaker.Guessed < MostGuessed && !speaker.Ended)
            {
                Guess(speaker);
                return true;
            }
            speaker.Next = first;
        }

        var packet = speaker.Packets.Values[0];
        speaker.Packets.RemoveAt(0);
        speaker.Gain = packet.Gain;

        int count;
        try { count = speaker.Decoder.Decode(packet.Opus, _decoded, MaxFrame, false); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException or Concentus.OpusException)
        {
            speaker.Next = first + UsualUnits;   // one damaged packet is a click at worst, not a reason to stop
            return true;
        }

        speaker.Next = first + Math.Max(1, count / SamplesPer10Ms);
        speaker.Guessed = 0;
        if (count > 0) speaker.FrameSamples = count;

        // Well ahead for a while, after a bump that has passed: a frame goes, or the delay would stay until they let go.
        // ⚠️ Decoded all the same, as the next frame builds on it.
        if (BufferedMs(speaker) > WaitMs(speaker) + 2 * UsualUnits * MsPerUnit)
        {
            if (++speaker.Ahead >= AheadBeforeSkip)
            {
                speaker.Ahead = 0;
                return true;
            }
        }
        else speaker.Ahead = 0;

        Queue(speaker, count);
        return true;
    }

    /// <summary>The codec's own guess at one missing frame.</summary>
    private void Guess(Speaker speaker)
    {
        int count;
        try { count = speaker.Decoder.Decode(ReadOnlySpan<byte>.Empty, _decoded, speaker.FrameSamples, false); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException or Concentus.OpusException)
        {
            count = 0;
        }
        speaker.Guessed++;
        GuessedFrames++;
        speaker.Next += Math.Max(1, speaker.FrameSamples / SamplesPer10Ms);
        Queue(speaker, count);
    }

    private void Queue(Speaker speaker, int count)
    {
        for (var i = 0; i < count; i++)
            speaker.Samples.Enqueue((short)Math.Clamp(_decoded[i] * speaker.Gain, short.MinValue, short.MaxValue));
    }

    /// <summary>Forgets a speaker: they left, or the server's connection ended.</summary>
    public void Remove(int server, uint session)
    {
        lock (_gate) _speakers.Remove((server, session));
    }

    public void RemoveServer(int server)
    {
        lock (_gate)
            foreach (var key in _speakers.Keys.Where(k => k.Server == server).ToList()) _speakers.Remove(key);
    }

    /// <summary>
    /// Fills a buffer with everyone mixed together, called from the sound device's own thread. Returns how
    /// many were heard, so nothing needs waking when nobody is talking.
    /// </summary>
    public int ReadMix(Span<short> into)
    {
        into.Clear();
        var heard = 0;

        lock (_gate)
        {
            Span<int> mix = into.Length <= 4096 ? stackalloc int[into.Length] : new int[into.Length];

            foreach (var speaker in _speakers.Values)
            {
                if (!speaker.Playing)
                {
                    if (speaker.Packets.Count == 0 && speaker.Samples.Count == 0) continue;

                    // Wait for a little to build up, unless that is all there is ever going to be.
                    if (BufferedMs(speaker) < WaitMs(speaker) && !speaker.Ended) continue;
                    speaker.Playing = true;
                    if (speaker.Next < 0 && speaker.Packets.Count > 0) speaker.Next = speaker.Packets.Keys[0];
                }

                while (speaker.Samples.Count < into.Length && NextFrame(speaker)) { }

                if (speaker.Samples.Count == 0)
                {
                    speaker.Playing = false;
                    if (speaker.Ended) StartOver(speaker);
                    else
                    {
                        // Ran dry in the middle of talking: this connection needs a longer wait than it looked like.
                        speaker.ExtraMs = Math.Min(speaker.ExtraMs + 2 * UsualUnits * MsPerUnit, MostWaitMs);
                        speaker.Guessed = 0;
                    }
                    continue;
                }

                heard++;
                var take = Math.Min(into.Length, speaker.Samples.Count);
                for (var i = 0; i < take; i++) mix[i] += speaker.Samples.Dequeue();
            }

            var cued = _cue.Count > 0;
            for (var i = 0; i < into.Length && _cue.Count > 0; i++) mix[i] += _cue.Dequeue();

            if (heard > 0 || cued)
            {
                var volume = Volume;
                for (var i = 0; i < into.Length; i++)
                    into[i] = (short)Math.Clamp(mix[i] * volume, short.MinValue, short.MaxValue);
            }
        }

        Mixed?.Invoke(into);
        return heard;
    }

    /// <summary>What the last packet of someone's voice said it was: see <see cref="MumbleService.TalkOf"/>.</summary>
    public int TargetOf(int server, uint session)
    {
        lock (_gate) return _speakers.TryGetValue((server, session), out var speaker) ? speaker.Target : 0;
    }

    /// <summary>Who is talking right now, by the last time a packet of theirs was seen.</summary>
    public List<(int Server, uint Session)> TalkingNow()
    {
        var now = DateTime.UtcNow;
        lock (_gate)
            return [.. _speakers.Where(s => !s.Value.Ended && now - s.Value.LastHeard < HeardFor).Select(s => s.Key)];
    }
}

/// <summary>
/// Turns microphone samples into Opus packets for one server, 20 ms at a time.
///
/// <para>One per server: an encoder carries state from frame to frame, and each server has its own sequence.</para>
/// </summary>
public sealed class MumbleEncoder
{
    public const int FrameSamples = MumbleMixer.SampleRate / 50;

    /// <summary>Mumble counts sequence numbers in tens of milliseconds.</summary>
    private const int SequenceStep = 2;

    private const int MaxPacket = 1024;

    private readonly IOpusEncoder _opus;
    private readonly byte[] _packet = new byte[MaxPacket];
    private long _sequence;

    public MumbleEncoder(int bitrate)
    {
        // As the standard client sets it up at this bitrate: general audio, at a constant rate.
        _opus = OpusCodecFactory.CreateEncoder(MumbleMixer.SampleRate, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        _opus.Bitrate = bitrate;
        _opus.UseVBR = false;
    }

    /// <summary>The bitrate for a server's bandwidth limit, leaving room for the packets' own overhead.</summary>
    /// <param name="maxBandwidth">The server's limit in bits a second. Zero means it set none.</param>
    public static int BitrateFor(int maxBandwidth)
    {
        const int Wanted = 40_000, Least = 8_000, Overhead = 16_000;
        return maxBandwidth <= 0 ? Wanted : Math.Clamp(maxBandwidth - Overhead, Least, Wanted);
    }

    /// <summary>A frame of voice as a packet ready to send.</summary>
    public MumbleVoicePacket Encode(ReadOnlySpan<short> frame, int target, bool last)
    {
        var size = _opus.Encode(frame, FrameSamples, _packet, MaxPacket);
        var packet = new MumbleVoicePacket(target, 0, _sequence, _packet.AsSpan(0, size).ToArray(), last);
        _sequence += SequenceStep;
        return packet;
    }
}

/// <summary>
/// Brings a voice up, or down, to a steady level. The standard client always does this, with room to raise a voice by
/// 29 dB, climbing at 12 dB a second and falling at 60. A microphone that is simply quiet is otherwise just quiet to
/// everyone. One voice at a time, 20 ms at a time, and fast: it runs on the sound device's thread.
/// </summary>
public sealed class MumbleVoiceLeveler
{
    /// <summary>
    /// What is aimed for. ⚠️ Real speech lands about 3 dB under it, because this follows a voice's louder moments:
    /// this is what brought one voice to the standard client's -21 dBFS, measured at a listener on the same server.
    /// </summary>
    public const double TargetDb = -18;

    public const double MostRaiseDb = 29, MostLowerDb = -12;

    /// <summary>What the pauses between words are lowered by: the standard client's own noise suppression strength.</summary>
    public const double PauseLowerDb = -30;

    private const double FrameSeconds = 0.02, RaisePerSecond = 12, FirstRaisePerSecond = 60, LowerPerSecond = 60;

    /// <summary>The loudest a frame may be turned up to. ⚠️ Below full scale, because Opus can overshoot what it was given by a decibel or so.</summary>
    private const double GuardDb = -3;

    /// <summary>How long a voice's end is left alone before the pause counts as one.</summary>
    private const int PauseAfterFrames = 8;

    /// <summary>How long it climbs quickly at first. ⚠️ The microphone is only open while talking, unlike the standard client's, so it has no silence to learn in.</summary>
    private const int LearningFrames = 75;

    /// <summary>What is held back at the start of a press: the key's click and the microphone opening are there, not the voice.</summary>
    private const int QuietFrames = 3;

    private const double LimitFrom = 0.6 * short.MaxValue, Ceiling = 0.84 * short.MaxValue;

    private double _gainDb, _appliedDb, _floorDb = -70, _levelPower, _pauseDb;
    private int _speechFrames, _framesIntoTalk, _framesSinceSpeech = PauseAfterFrames;

    public double GainDb => _gainDb;

    public void StartTalk() => _framesIntoTalk = 0;

    /// <param name="frame">One frame, in place.</param>
    /// <param name="notSpeech">
    /// Known not to be anyone talking, whatever its level says. ⚠️ Echo cancelling leaves a little behind, and this would
    /// otherwise take that for a quiet voice and raise it by up to 29 dB, undoing nearly all of what was taken out.
    /// </param>
    public void Process(Span<short> frame, bool notSpeech = false)
    {
        double sum = 0, peak = 0;
        foreach (var s in frame)
        {
            sum += (double)s * s;
            peak = Math.Max(peak, Math.Abs((double)s));
        }
        var power = sum / frame.Length;
        var db = power <= 1 ? -90 : 10 * Math.Log10(power / (32768.0 * 32768.0));

        // The background: follows silence straight down, and creeps up only slowly, so a long sound never becomes it.
        _floorDb = db < _floorDb ? db : _floorDb + FrameSeconds;

        if (!notSpeech && _framesIntoTalk >= QuietFrames && db > Math.Max(_floorDb + 10, -60))
        {
            // Quick to hear a voice get louder, slow to believe it got quieter, or the next loud word comes through far too hot.
            _levelPower = _levelPower == 0 ? power : _levelPower + (power - _levelPower) * (power > _levelPower ? 0.35 : 0.04);

            var level = 10 * Math.Log10(_levelPower / (32768.0 * 32768.0));
            var wanted = Math.Clamp(TargetDb - level, MostLowerDb, MostRaiseDb);
            var up = (_speechFrames < LearningFrames ? FirstRaisePerSecond : RaisePerSecond) * FrameSeconds;

            _gainDb += Math.Clamp(wanted - _gainDb, -LowerPerSecond * FrameSeconds, up);
            _speechFrames++;
            _framesSinceSpeech = 0;
        }
        else _framesSinceSpeech++;

        // One loud moment is turned down for that moment, without losing what has been learned about the voice.
        var peakDb = peak <= 1 ? -90 : 20 * Math.Log10(peak / 32768.0);
        var appliedDb = Math.Min(_gainDb, GuardDb - peakDb);

        // Quiet between words, open again at once for the next.
        var pauseDb = _framesSinceSpeech < PauseAfterFrames ? 0 : Math.Max(_pauseDb - 6, PauseLowerDb);

        // Down at once, up gradually: easing into a loud frame would let its first part through at the old gain.
        var start = Math.Pow(10, (Math.Min(_appliedDb, appliedDb) + _pauseDb) / 20);
        var end = Math.Pow(10, (appliedDb + pauseDb) / 20);
        _appliedDb = appliedDb;
        _pauseDb = pauseDb;

        var opening = _framesIntoTalk < QuietFrames ? 0.0 : _framesIntoTalk == QuietFrames ? -1.0 : 1.0;

        for (var i = 0; i < frame.Length; i++)
        {
            var along = (double)i / frame.Length;
            var value = frame[i] * (start + (end - start) * along) * (opening < 0 ? along : opening);
            frame[i] = (short)Math.Round(Limit(value));
        }

        _framesIntoTalk++;
    }

    /// <summary>Rounds off what would otherwise clip, without turning everything down for one loud moment.</summary>
    private static double Limit(double value)
    {
        var size = Math.Abs(value);
        if (size <= LimitFrom) return value;

        var room = Ceiling - LimitFrom;
        return Math.Sign(value) * (LimitFrom + room * Math.Tanh((size - LimitFrom) / room));
    }
}

/// <summary>The sound devices: the mixer to the speakers, the microphone to whoever wants it.</summary>
public sealed class MumbleAudioDevices : IDisposable
{
    private readonly MumbleMixer _mixer;
    private readonly object _gate = new();

    private PaStream? _output;
    private PaStream? _input;
    private PaStream.Callback? _outputCallback;
    private PaStream.Callback? _inputCallback;

    private static bool _initialized;
    private static readonly object InitGate = new();

    public MumbleAudioDevices(MumbleMixer mixer) => _mixer = mixer;

    /// <summary>Samples from the microphone, 10 ms at a time, on the sound device's thread. Nothing slow here.</summary>
    public event Action<short[]>? Captured;

    /// <summary>The microphone is about to start, after being closed: nothing of what came before lines up with what comes next.</summary>
    public event Action? InputOpening;

    /// <summary>What went wrong the last time a device was opened, for the screen to say.</summary>
    public string? LastError { get; private set; }

    /// <summary>The saved device names, blank for the system's default.</summary>
    public string InputDevice { get; set; } = "";
    public string OutputDevice { get; set; } = "";

    public double MicrophoneGain { get; set; } = 1.0;

    private static bool EnsureInitialized()
    {
        lock (InitGate)
        {
            if (_initialized) return true;
            try
            {
                PortAudio.Initialize();
                _initialized = true;
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or PortAudioException or InvalidOperationException) { return false; }
        }
    }

    /// <summary>
    /// How much a host API is to be trusted, lower being better: the same order speech input uses, for the
    /// reasons given there. DirectSound and MME resample and share a device. WASAPI in shared mode often
    /// refuses mono at 48 kHz, and WDM-KS takes a device exclusively, locking the game and everything else
    /// out of it, so it is never offered.
    /// </summary>
    private static int Rank(int hostApi)
    {
        var name = SpeechInputService.HostApiName(hostApi);
        if (name.Contains("WDM-KS", StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
        if (name.Contains("DirectSound", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("MME", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    private static List<(int Index, string Name)> Devices(bool input)
    {
        if (!EnsureInitialized()) return [];

        var found = new List<(int Index, string Name, int Rank)>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if ((input ? info.maxInputChannels : info.maxOutputChannels) <= 0) continue;
            var rank = Rank(info.hostApi);
            if (rank != int.MaxValue) found.Add((i, info.name, rank));
        }

        // One line per device: MME cuts names at 31 characters, so a cut one is the same device again.
        var kept = new List<(int Index, string Name)>();
        foreach (var d in found.OrderBy(d => d.Rank))
        {
            if (kept.Any(k => k.Name == d.Name || d.Name.Length >= 30 && k.Name.StartsWith(d.Name, StringComparison.Ordinal))) continue;
            kept.Add((d.Index, d.Name));
        }
        return kept;
    }

    public static IReadOnlyList<string> InputNames() => [.. Devices(true).Select(d => d.Name)];
    public static IReadOnlyList<string> OutputNames() => [.. Devices(false).Select(d => d.Name)];

    private static int Resolve(string saved, bool input)
    {
        if (saved.Length > 0)
            foreach (var d in Devices(input))
                if (d.Name == saved) return d.Index;

        // A device unplugged since it was chosen falls back rather than leaving the person silent.
        return input ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice;
    }

    /// <summary>Starts playing the mix. Safe to call again.</summary>
    public bool StartOutput()
    {
        lock (_gate)
        {
            _wantOutput = true;
            if (_output is not null) return true;
            if (!EnsureInitialized()) { LastError = "PortAudio could not start."; return false; }

            try
            {
                var device = Resolve(OutputDevice, false);
                if (device < 0) { LastError = "No output device."; return false; }

                var info = PortAudio.GetDeviceInfo(device);
                var parameters = new StreamParameters
                {
                    device = device, channelCount = 1, sampleFormat = SampleFormat.Int16,
                    suggestedLatency = info.defaultLowOutputLatency, hostApiSpecificStreamInfo = IntPtr.Zero,
                };

                _outputCallback = OnOutput;
                var stream = new PaStream(null, parameters, MumbleMixer.SampleRate, MumbleMixer.SamplesPer10Ms, StreamFlags.ClipOff, _outputCallback, null);
                stream.Start();
                _output = stream;
                LastError = null;
                return true;
            }
            catch (Exception e) when (e is PortAudioException or InvalidOperationException or ArgumentException)
            {
                LastError = e.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Reopens the speakers if their stream has ended, as unplugging a headset does. Called every couple of seconds.
    /// ⚠️ Not run against real hardware being unplugged: it tries, and says why if it cannot.
    /// </summary>
    public void KeepOutputAlive()
    {
        lock (_gate)
        {
            if (!_wantOutput) return;
            if (_output is not null)
            {
                try { if (_output.IsActive) return; }
                catch (Exception e) when (e is PortAudioException or InvalidOperationException or ObjectDisposedException) { /* gone, then */ }
                Close(ref _output);
            }
        }
        StartOutput();
    }

    private bool _wantOutput;

    public void StopOutput()
    {
        lock (_gate)
        {
            _wantOutput = false;
            Close(ref _output);
            _outputCallback = null;
        }
    }

    /// <summary>Opens the microphone. Only while someone is talking, so the system's "in use" light means it.</summary>
    public bool StartInput()
    {
        lock (_gate)
        {
            if (_input is not null) return true;
            if (!EnsureInitialized()) { LastError = "PortAudio could not start."; return false; }

            try
            {
                var device = Resolve(InputDevice, true);
                if (device < 0) { LastError = "No microphone."; return false; }

                var info = PortAudio.GetDeviceInfo(device);
                var parameters = new StreamParameters
                {
                    device = device, channelCount = 1, sampleFormat = SampleFormat.Int16,
                    suggestedLatency = info.defaultLowInputLatency, hostApiSpecificStreamInfo = IntPtr.Zero,
                };

                _inputCallback = OnInput;
                var stream = new PaStream(parameters, null, MumbleMixer.SampleRate, MumbleMixer.SamplesPer10Ms, StreamFlags.ClipOff, _inputCallback, null);
                InputOpening?.Invoke();
                stream.Start();
                _input = stream;
                LastError = null;
                return true;
            }
            catch (Exception e) when (e is PortAudioException or InvalidOperationException or ArgumentException)
            {
                LastError = e.Message;
                return false;
            }
        }
    }

    public void StopInput()
    {
        lock (_gate)
        {
            Close(ref _input);
            _inputCallback = null;
        }
    }

    private static void Close(ref PaStream? stream)
    {
        try { stream?.Stop(); stream?.Dispose(); }
        catch (Exception e) when (e is PortAudioException or InvalidOperationException) { /* going anyway */ }
        stream = null;
    }

    private StreamCallbackResult OnOutput(IntPtr input, IntPtr output, uint frames, ref StreamCallbackTimeInfo time, StreamCallbackFlags flags, IntPtr user)
    {
        unsafe
        {
            _mixer.ReadMix(new Span<short>((void*)output, (int)frames));
        }
        return StreamCallbackResult.Continue;
    }

    private StreamCallbackResult OnInput(IntPtr input, IntPtr output, uint frames, ref StreamCallbackTimeInfo time, StreamCallbackFlags flags, IntPtr user)
    {
        if (input == IntPtr.Zero) return StreamCallbackResult.Continue;

        var samples = new short[frames];
        Marshal.Copy(input, samples, 0, (int)frames);

        var gain = MicrophoneGain;
        if (Math.Abs(gain - 1.0) > 0.001)
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);

        Captured?.Invoke(samples);
        return StreamCallbackResult.Continue;
    }

    public void Dispose()
    {
        StopInput();
        StopOutput();
    }
}
