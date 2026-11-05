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
/// </summary>
public sealed class MumbleMixer
{
    public const int SampleRate = 48_000;
    public const int SamplesPer10Ms = SampleRate / 100;

    /// <summary>The longest frame Opus can carry: 120 ms.</summary>
    private const int MaxFrame = SampleRate * 120 / 1000;

    /// <summary>How much to hold before starting someone: enough to ride over the network's bumps.</summary>
    private const int StartAfter = SamplesPer10Ms * 6;

    /// <summary>Past this behind, a speaker drops their oldest sound rather than falling further back.</summary>
    private const int MaxBacklog = SamplesPer10Ms * 40;

    private static readonly TimeSpan HeardFor = TimeSpan.FromMilliseconds(350);

    private sealed class Speaker
    {
        public required IOpusDecoder Decoder { get; init; }
        public readonly Queue<short> Samples = new();
        public bool Playing;
        public DateTime LastHeard;
        public bool Ended;
        public long LastSequence = -1;
    }

    private readonly object _gate = new();
    private readonly Dictionary<(int Server, uint Session), Speaker> _speakers = [];
    private readonly short[] _decoded = new short[MaxFrame];

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

            speaker.LastHeard = DateTime.UtcNow;
            speaker.Ended = packet.Last;

            // A terminator carrying no sound only says they let go.
            if (packet.Opus.Length == 0) return;

            int count;
            try { count = speaker.Decoder.Decode(packet.Opus, _decoded, MaxFrame, false); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException or Concentus.OpusException)
            {
                return;   // one damaged packet is a click at worst, not a reason to stop
            }

            for (var i = 0; i < count; i++)
                speaker.Samples.Enqueue((short)Math.Clamp(_decoded[i] * gain, short.MinValue, short.MaxValue));

            while (speaker.Samples.Count > MaxBacklog) speaker.Samples.Dequeue();
        }
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
                    // Wait for a little to build up, unless that is all there is ever going to be.
                    if (speaker.Samples.Count >= StartAfter || speaker.Ended && speaker.Samples.Count > 0) speaker.Playing = true;
                    else continue;
                }

                if (speaker.Samples.Count == 0)
                {
                    speaker.Playing = false;
                    continue;
                }

                heard++;
                var take = Math.Min(into.Length, speaker.Samples.Count);
                for (var i = 0; i < take; i++) mix[i] += speaker.Samples.Dequeue();
            }

            if (heard > 0)
            {
                var volume = Volume;
                for (var i = 0; i < into.Length; i++)
                    into[i] = (short)Math.Clamp(mix[i] * volume, short.MinValue, short.MaxValue);
            }
        }

        return heard;
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
        _opus = OpusCodecFactory.CreateEncoder(MumbleMixer.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _opus.Bitrate = bitrate;
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
