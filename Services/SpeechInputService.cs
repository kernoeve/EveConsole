using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using EveConsole.Agent;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace EveConsole.Services;

public sealed class SpeechInputService : IDisposable
{
    private const int  SampleRate      = 16000;
    private const uint FramesPerBuffer = 512;

    private readonly OpenAiWhisperService _cloud = new();
    private readonly LocalWhisperService  _local = new();

    private SpeechInputProvider _provider             = SpeechInputProvider.None;
    private string              _apiKey               = "";
    private string              _localModel           = "tiny";
    private string              _language             = "en";
    private string              _microphoneDeviceName = "";

    // PortAudio state — lazily initialized once per process
    private static bool   _paInitialized;
    private static object _paLock = new();

    private PaStream?          _stream;
    private PaStream.Callback? _callbackDelegate; // keep reference to prevent GC collection
    private readonly object    _streamLock = new();

    // The rolling capture — see "Capture" below.
    private readonly ConcurrentQueue<(long Tick, byte[] Bytes)> _chunks = new();
    private volatile int _state;   // Idle, Capturing, or Released and waiting to be collected
    private long _pressedTick, _releasedTick;

    public bool IsRecording    => _state == Capturing;
    public bool IsAvailable    => _provider != SpeechInputProvider.None;

    public LocalWhisperService LocalWhisper => _local;

    /// <summary>
    /// Where transcription usage is recorded. Optional — absent means unmeasured, not broken.
    /// </summary>
    public Agent.AgentTelemetryService? Telemetry { get; set; }

    /// <summary>
    /// Where failures go.
    ///
    /// <para>⚠️ Everything in here used to fail to <c>Debug.WriteLine</c>, which in a release
    /// build goes nowhere at all. A microphone that will not open, a saved device that no longer
    /// exists, a PortAudio that will not initialise — all of them produced a push-to-talk that
    /// simply did nothing, with no entry anywhere saying why.</para>
    /// </summary>
    public AppErrorLogger? Errors { get; set; }

    /// <summary>The last thing that went wrong, in words, for a harness or a status line to show.</summary>
    public string? LastFailure { get; private set; }

    private void Fail(string context, string message)
    {
        LastFailure = $"{context}: {message}";
        System.Diagnostics.Debug.WriteLine($"[SpeechInput] {context}: {message}");
        Errors?.Log("SpeechInput", context, message, null);
    }

    public void Configure(SpeechInputProvider provider, string apiKey, string localModel, string microphoneDeviceName = "", string language = "en")
    {
        var reopen = provider != _provider || (microphoneDeviceName ?? "") != _microphoneDeviceName;
        _provider             = provider;
        _apiKey               = apiKey ?? "";
        _localModel           = string.IsNullOrWhiteSpace(localModel) ? "tiny" : localModel;
        _language             = language ?? "en";
        _microphoneDeviceName = microphoneDeviceName ?? "";

        // The microphone opens now rather than at the first press, so the first press is as quick
        // as every later one — and closes when speech input is switched off. The local model is
        // loaded and run once now for the same reason.
        if (reopen) CloseStream();
        if (provider != SpeechInputProvider.None) _ = Task.Run(EnsureStreamOpen);
        if (provider == SpeechInputProvider.LocalWhisper) _ = Task.Run(() => _local.WarmUpAsync(_localModel, _language));
    }

    /// <summary>
    /// The input devices worth offering, one entry per microphone. Initialises PortAudio if needed.
    ///
    /// <para>⚠️ PortAudio enumerates every device once per HOST API, and Windows has four of them.
    /// The raw list on a machine with three microphones was seventeen entries: the same webcam
    /// four times, the same capture card four times. What looks like duplication is really the
    /// same hardware reached four different ways.</para>
    ///
    /// <para>⚠️ And they are not interchangeable, which is why this filters rather than only
    /// de-duplicating. Measured on a Windows 11 machine, every WASAPI entry and most WDM-KS
    /// entries REJECT the 16 kHz mono capture this service records at — so a capsuleer could pick
    /// a microphone by name, get the WASAPI copy of it, and have push-to-talk fail every time
    /// while the device list insisted the microphone was there. Only devices that can actually
    /// open at the recording format are offered.</para>
    ///
    /// <para>The survivors are then de-duplicated by name, preferring the host API most likely to
    /// work: DirectSound and MME resample and share the device, while WDM-KS commonly takes it
    /// exclusively — which would lock the microphone away from the game and everything else.</para>
    /// </summary>
    public IReadOnlyList<string> GetInputDeviceNames()
    {
        if (!EnsurePortAudioInit()) return [];
        try
        {
            // Rebuilt here rather than reused: this is what the Refresh button calls, and the
            // reason to press it is that the hardware changed.
            _usableDevices = null;

            var kept = new List<string>();
            foreach (var name in UsableInputDevices()
                                 .GroupBy(d => d.Name, StringComparer.Ordinal)
                                 .Select(g => g.First().Name))
            {
                if (!IsTruncatedDuplicate(name, kept)) kept.Add(name);
            }
            return kept;
        }
        catch (Exception ex)
        {
            Fail("Device enumeration", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Whether this name is the MME spelling of a device already offered under a better host API.
    ///
    /// <para>⚠️ MME truncates device names to 31 characters, so the same microphone appears as
    /// "Microphone (2- HyperX QuadCast S)" under DirectSound and "Microphone (2- HyperX QuadCast "
    /// under MME. De-duplicating by name alone cannot merge those two — they are different
    /// strings — and the list keeps a cut-off entry that looks like a second microphone.</para>
    ///
    /// <para>The length floor keeps this to the truncation case. Two genuinely different devices
    /// where one name is a prefix of the other is possible, but not at thirty characters, which is
    /// where MME cuts.</para>
    /// </summary>
    private static bool IsTruncatedDuplicate(string name, List<string> kept)
        => name.Length >= 30
        && kept.Any(k => k.Length > name.Length && k.StartsWith(name, StringComparison.Ordinal));

    /// <summary>
    /// Every input device that can be opened at the recording format, best host API first.
    ///
    /// <para>Ordered so that <c>GroupBy(...).First()</c> above and the lookup in
    /// <see cref="ResolveDeviceIndex"/> agree on which copy of a device to use — the list must
    /// name the device the recorder will actually open, or the setting means something different
    /// from what it says.</para>
    /// </summary>
    private List<(int Index, string Name, int HostApi)>? _usableDevices;

    private List<(int Index, string Name, int HostApi)> UsableInputDevices()
    {
        // ⚠️ Cached because ResolveDeviceIndex runs on every push-to-talk, and probing a WDM-KS
        // device for format support can mean briefly opening it. Paying that on each key press
        // would put the cost squarely in the gap between pressing the key and recording starting.
        if (_usableDevices is not null) return _usableDevices;

        var devices = new List<(int Index, string Name, int HostApi)>();
        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels <= 0) continue;
            if (HostApiRank(info.hostApi) == Excluded) continue;
            if (!SupportsRecordingFormat(i, info)) continue;
            devices.Add((i, info.name, info.hostApi));
        }

        // Stable: equal ranks keep PortAudio's own order, so the list does not reshuffle between
        // launches on a machine where nothing changed.
        return _usableDevices = devices.OrderBy(d => HostApiRank(d.HostApi)).ToList();
    }

    /// <summary>
    /// How much a host API is to be trusted with a shared microphone, lower being better.
    ///
    /// <para>⚠️ Ranked by NAME, not by index. PortAudio's host API indexes are assigned in
    /// whatever order the APIs initialise and are not a fixed enumeration, so a hard-coded number
    /// would silently mean a different API on another machine.</para>
    /// </summary>
    private static int HostApiRank(int hostApi) => HostApiName(hostApi) switch
    {
        var n when n.Contains("DirectSound", StringComparison.OrdinalIgnoreCase) => 0,
        var n when n.Contains("MME",         StringComparison.OrdinalIgnoreCase) => 1,
        var n when n.Contains("WASAPI",      StringComparison.OrdinalIgnoreCase) => 2,
        var n when n.Contains("WDM-KS",      StringComparison.OrdinalIgnoreCase) => Excluded,
        _                                                                        => 2,
    };

    /// <summary>
    /// ⚠️ WDM-KS is not offered at all, not merely ranked last. It opens the device EXCLUSIVELY:
    /// measured with the stream held, WASAPI answered "device in use" and waveIn "already
    /// allocated" — Discord, the game and everything else locked out of the microphone. Ranking
    /// it last was meant to keep it as a fallback, but Windows names the same microphone
    /// "Microphone (2- Foo)" for DirectSound and MME and "Microphone (Foo)" for WDM-KS, so a
    /// saved name matched only the exclusive one and the ranking never came into it.
    /// </summary>
    private const int Excluded = 99;

    /// <summary>
    /// Whether this device can open a stream shaped the way <see cref="StartRecording"/> opens it.
    ///
    /// <para>Asked of PortAudio rather than inferred from <c>defaultSampleRate</c>: a device
    /// reporting 44,100 may well accept 16,000 (DirectSound resamples) and a device reporting
    /// 48,000 may refuse it (WASAPI does). The only reliable answer is the one the library gives.</para>
    ///
    /// <para>⚠️ Assumed supported if the check itself is unavailable. The entry point is reached
    /// by P/Invoke into the same native library PortAudioSharp loads, and a build where that
    /// fails should offer too many devices rather than none.</para>
    /// </summary>
    private bool SupportsRecordingFormat(int device, DeviceInfo info)
    {
        var p = new StreamParameters
        {
            device                    = device,
            channelCount              = 1,
            sampleFormat              = SampleFormat.Int16,
            suggestedLatency          = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        var handle = GCHandle.Alloc(p, GCHandleType.Pinned);
        try   { return NativePa.IsFormatSupported(handle.AddrOfPinnedObject(), IntPtr.Zero, SampleRate) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return true; }
        finally { handle.Free(); }
    }

    /// <summary>
    /// The two PortAudio entry points PortAudioSharp2 1.0.6 does not wrap.
    ///
    /// <para>⚠️ "portaudio" is the same library name PortAudioSharp itself imports, so this
    /// resolves to the library it has already loaded — portaudio.dll on Windows, libportaudio.so
    /// on Linux — rather than to a second copy with its own device table.</para>
    /// </summary>
    private static class NativePa
    {
        private const string Lib = "portaudio";

        [StructLayout(LayoutKind.Sequential)]
        internal struct HostApiInfo
        {
            public int    structVersion;
            public int    type;
            public IntPtr name;
            public int    deviceCount;
            public int    defaultInputDevice;
            public int    defaultOutputDevice;
        }

        /// <summary>Returns 0 (paNoError) when a stream of this shape could be opened.</summary>
        [DllImport(Lib, EntryPoint = "Pa_IsFormatSupported")]
        internal static extern int IsFormatSupported(IntPtr inputParams, IntPtr outputParams, double sampleRate);

        [DllImport(Lib, EntryPoint = "Pa_GetHostApiInfo")]
        internal static extern IntPtr GetHostApiInfo(int hostApi);
    }

    private static string HostApiName(int hostApi)
    {
        try
        {
            var p = NativePa.GetHostApiInfo(hostApi);
            if (p == IntPtr.Zero) return "";
            return Marshal.PtrToStringAnsi(Marshal.PtrToStructure<NativePa.HostApiInfo>(p).name) ?? "";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return ""; }
    }

    /// <summary>
    /// Whether two names are the same microphone. Windows numbers a second device of a make
    /// "Microphone (2- Foo)" for some host APIs and not others, and MME cuts names at thirty-one
    /// characters, so a saved name is compared with both of those allowed for.
    /// </summary>
    internal static bool SameDevice(string a, string b)
    {
        static string Plain(string s) => System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\((\d+)- ", "(");
        var x = Plain(a);
        var y = Plain(b);
        if (string.Equals(x, y, StringComparison.Ordinal)) return true;
        var shorter = x.Length < y.Length ? x : y;
        var longer  = x.Length < y.Length ? y : x;
        return shorter.Length >= 30 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    private int ResolveDeviceIndex()
    {
        if (string.IsNullOrEmpty(_microphoneDeviceName))
            return PortAudio.DefaultInputDevice;

        // ⚠️ Searched among the USABLE devices, in the same preference order the settings list is
        // built in. A plain scan over every index returns the first name match, which is whichever
        // host API happens to enumerate first — not necessarily one that can open at 16 kHz, and
        // not necessarily the one the capsuleer was shown when they chose it.
        foreach (var d in UsableInputDevices())
            if (SameDevice(d.Name, _microphoneDeviceName))
                return d.Index;

        Fail("Microphone", $"'{_microphoneDeviceName}' is no longer available — using the system default instead.");
        return PortAudio.DefaultInputDevice;
    }

    // ── Capture ────────────────────────────────────────────────────────────────
    //
    // ⚠️ The microphone stays open. Opening a capture device takes a few hundred milliseconds,
    // and it used to happen on the UI thread AFTER the key press had queued behind whatever that
    // thread was doing — so a one-second utterance could be half over, or entirely over, before
    // a byte of it was captured, and the transcriber was handed a fragment that read as silence.
    // Measured: utterances here run one to three seconds, and the failed ones captured under
    // half a second. Now the stream runs from the moment speech input is configured; while
    // nobody holds the key the callback keeps only the last half second, and a press turns that
    // into the head of the recording. Press and release are flag flips any thread may make at
    // once, so the capture window is the key hold itself, whatever the UI thread is busy with.
    // The cost is a microphone that is open while the app runs; a provider of None closes it.

    private const int PreRollMs = 400;    // kept from before the press: people speak as they press
    private const int TailMs    = 150;    // kept after the release: the last word's last consonant
    private const int MinHoldMs = 150;    // a shorter press is a tap, not an utterance

    private const int Idle = 0, Capturing = 1, Released = 2;

    /// <summary>Opens the microphone if it is not open. Any thread; a press pays for it only if
    /// Configure's own attempt failed.</summary>
    private bool EnsureStreamOpen()
    {
        if (_provider == SpeechInputProvider.None) return false;
        lock (_streamLock)
        {
            if (_stream is not null) return true;
            if (!EnsurePortAudioInit()) return false;
            try
            {
                int device = ResolveDeviceIndex();
                if (device < 0) return false;

                var info = PortAudio.GetDeviceInfo(device);
                var inputParams = new StreamParameters
                {
                    device                    = device,
                    channelCount              = 1,
                    sampleFormat              = SampleFormat.Int16,
                    suggestedLatency          = info.defaultLowInputLatency,
                    hostApiSpecificStreamInfo = IntPtr.Zero,
                };

                _callbackDelegate = RecordCallback;
                var stream = new PaStream(
                    inParams:        inputParams,
                    outParams:       null,
                    sampleRate:      SampleRate,
                    framesPerBuffer: FramesPerBuffer,
                    streamFlags:     StreamFlags.ClipOff,
                    callback:        _callbackDelegate,
                    userData:        null);
                stream.Start();
                _stream = stream;
                return true;
            }
            catch (Exception ex)
            {
                Fail("Open microphone", $"{ex.GetType().Name}: {ex.Message}{(ex.InnerException is { } inner ? " — " + inner.Message : "")}");
                _callbackDelegate = null;
                _stream           = null;
                return false;
            }
        }
    }

    private void CloseStream()
    {
        lock (_streamLock)
        {
            _state = Idle;
            try { _stream?.Stop();    } catch { }
            try { _stream?.Dispose(); } catch { }
            _stream           = null;
            _callbackDelegate = null;
            while (_chunks.TryDequeue(out _)) { }
        }
    }

    private StreamCallbackResult RecordCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userDataPtr)
    {
        if (input == IntPtr.Zero)
            return StreamCallbackResult.Continue;

        var buf = new short[(int)frameCount];
        Marshal.Copy(input, buf, 0, (int)frameCount);

        var bytes = new byte[buf.Length * 2];
        Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);

        var now = Environment.TickCount64;
        _chunks.Enqueue((now, bytes));

        // Idle: keep the pre-roll and no more. Capturing or released: keep everything until the
        // collection after the release has taken what it wants.
        if (_state == Idle)
            while (_chunks.TryPeek(out var oldest) && now - oldest.Tick > PreRollMs + 100 && _chunks.TryDequeue(out _)) { }

        return StreamCallbackResult.Continue;
    }

    /// <summary>The key went down. Instant, from any thread. False when speech input is off, the
    /// microphone will not open, or a hold is already in progress.</summary>
    public bool BeginCapture()
    {
        if (_state != Idle) return false;
        if (!EnsureStreamOpen()) return false;
        _pressedTick = Environment.TickCount64;
        _state       = Capturing;
        return true;
    }

    /// <summary>The key came up. Instant, from any thread; <see cref="CollectAsync"/> then takes
    /// the recording. False when nothing was being captured.</summary>
    public bool EndCapture()
    {
        if (_state != Capturing) return false;
        _releasedTick = Environment.TickCount64;
        _state        = Released;
        return true;
    }

    /// <summary>
    /// The recording the last hold made — pre-roll, hold and tail — as raw PCM, or null when the
    /// hold was too short to be speech. Waits out the tail, so not for the hook thread.
    /// </summary>
    public async Task<byte[]?> CollectAsync(CancellationToken ct = default)
    {
        if (_state != Released) return null;
        try
        {
            await Task.Delay(TailMs + 50, ct);

            var from  = _pressedTick - PreRollMs;
            var to    = _releasedTick + TailMs;
            var parts = new List<byte[]>();
            // Chronological, so everything up to the window is dropped, the window is taken, and
            // what came after it stays for the next hold's pre-roll.
            while (_chunks.TryPeek(out var c) && c.Tick <= to)
            {
                _chunks.TryDequeue(out _);
                if (c.Tick >= from) parts.Add(c.Bytes);
            }

            if (_releasedTick - _pressedTick < MinHoldMs || parts.Count == 0) return null;

            var pcm    = new byte[parts.Sum(p => p.Length)];
            var offset = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, pcm, offset, p.Length);
                offset += p.Length;
            }
            return pcm;
        }
        finally
        {
            _state = Idle;
        }
    }

    /// <summary>The words in a recording, from whichever transcriber is configured.</summary>
    public async Task<string?> TranscribeAsync(byte[] pcm, CancellationToken ct = default)
    {
        if (pcm.Length / 2 < SampleRate / 5) return null; // < 0.2 s — too short

        var wav = BuildWav(pcm);

        // ⚠️ Transcription bills by AUDIO DURATION, not by characters or tokens — so the unit here
        // is seconds, and it is known from the PCM itself: two bytes per sample at SampleRate. It
        // cannot be read back from the provider, only counted from what was sent.
        var seconds      = (long)Math.Round(pcm.Length / 2.0 / SampleRate);
        var startedTicks = Environment.TickCount64;
        var failure      = "";

        try
        {
            return _provider switch
            {
                SpeechInputProvider.OpenAiWhisper => await _cloud.TranscribeAsync(wav, _apiKey, ct),
                SpeechInputProvider.LocalWhisper  => await _local.TranscribeAsync(wav, _localModel, _language, ct),
                _                                  => null,
            };
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            throw;
        }
        finally
        {
            // Recorded even when it threw: a failed transcription of thirty seconds of audio was
            // still thirty seconds sent, and on a paid provider still thirty seconds billed.
            if (_provider != SpeechInputProvider.None)
                Telemetry?.ServiceCall(
                    kind:       "stt",
                    provider:   _provider.ToString(),
                    model:      _provider == SpeechInputProvider.LocalWhisper ? _localModel : "whisper-1",
                    isLocal:    _provider == SpeechInputProvider.LocalWhisper,
                    unitKind:   "seconds",
                    units:      seconds,
                    durationMs: (int)(Environment.TickCount64 - startedTicks),
                    error:      failure);
        }
    }

    /// <summary>Begin, for a caller with a button rather than a key.</summary>
    public bool StartRecording() => BeginCapture();

    /// <summary>End, collect and transcribe in one, for the same caller.</summary>
    public async Task<string?> StopAndTranscribeAsync(CancellationToken ct = default)
    {
        if (!EndCapture()) return null;
        var pcm = await CollectAsync(ct);
        return pcm is null ? null : await TranscribeAsync(pcm, ct);
    }

    internal static byte[] BuildWav(byte[] pcmBytes)
    {
        int dataLen = pcmBytes.Length;
        using var ms = new MemoryStream(44 + dataLen);
        using var bw = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true);

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataLen);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);        // PCM
        bw.Write((short)1);        // mono
        bw.Write(SampleRate);
        bw.Write(SampleRate * 2);  // byte rate
        bw.Write((short)2);        // block align
        bw.Write((short)16);       // bits per sample
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLen);
        bw.Write(pcmBytes);

        return ms.ToArray();
    }

    // Instance rather than static so a failure can reach the error log through Fail; the state
    // it guards is still process-wide, because PortAudio's initialisation is.
    private bool EnsurePortAudioInit()
    {
        if (_paInitialized) return true;
        lock (_paLock)
        {
            if (_paInitialized) return true;
            try
            {
                PortAudio.Initialize();
                _paInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Fail("PortAudio init", ex.Message);
                return false;
            }
        }
    }

    public void Dispose()
    {
        CloseStream();

        // ⚠️ Needed since the local model started being kept between utterances. It holds native
        // memory measured in hundreds of megabytes — 1.5 GB for the medium model — and before
        // caching there was nothing to release, because each utterance disposed its own.
        try { _local.Dispose(); } catch { }
    }
}
