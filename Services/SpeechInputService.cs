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
    private string              _microphoneDeviceName = "";

    // PortAudio state — lazily initialized once per process
    private static bool   _paInitialized;
    private static object _paLock = new();

    private PaStream?               _stream;
    private PaStream.Callback?      _callbackDelegate; // keep reference to prevent GC collection
    private readonly ConcurrentQueue<byte[]> _chunks = new();
    private volatile bool _recording;

    public bool IsRecording    => _recording;
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

    private void Fail(string context, string message)
    {
        System.Diagnostics.Debug.WriteLine($"[SpeechInput] {context}: {message}");
        Errors?.Log("SpeechInput", context, message, null);
    }

    public void Configure(SpeechInputProvider provider, string apiKey, string localModel, string microphoneDeviceName = "")
    {
        _provider             = provider;
        _apiKey               = apiKey ?? "";
        _localModel           = string.IsNullOrWhiteSpace(localModel) ? "tiny" : localModel;
        _microphoneDeviceName = microphoneDeviceName ?? "";
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
        var n when n.Contains("WDM-KS",      StringComparison.OrdinalIgnoreCase) => 3,
        _                                                                        => 2,
    };

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

    private int ResolveDeviceIndex()
    {
        if (string.IsNullOrEmpty(_microphoneDeviceName))
            return PortAudio.DefaultInputDevice;

        // ⚠️ Searched among the USABLE devices, in the same preference order the settings list is
        // built in. A plain scan over every index returns the first name match, which is whichever
        // host API happens to enumerate first — not necessarily one that can open at 16 kHz, and
        // not necessarily the one the capsuleer was shown when they chose it.
        foreach (var d in UsableInputDevices())
            if (d.Name == _microphoneDeviceName)
                return d.Index;

        Fail("Microphone", $"'{_microphoneDeviceName}' is no longer available — using the system default instead.");
        return PortAudio.DefaultInputDevice;
    }

    public bool StartRecording()
    {
        if (_recording || _provider == SpeechInputProvider.None) return false;
        if (!EnsurePortAudioInit()) return false;

        try
        {
            while (_chunks.TryDequeue(out _)) { } // clear any leftover audio

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

            _stream = new PaStream(
                inParams:        inputParams,
                outParams:       null,
                sampleRate:      SampleRate,
                framesPerBuffer: FramesPerBuffer,
                streamFlags:     StreamFlags.ClipOff,
                callback:        _callbackDelegate,
                userData:        null);

            _recording = true;
            _stream.Start();
            return true;
        }
        catch (Exception ex)
        {
            Fail("StartRecording", ex.Message);
            _callbackDelegate = null;
            _stream?.Dispose();
            _stream    = null;
            _recording = false;
            return false;
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
        if (!_recording || input == IntPtr.Zero)
            return StreamCallbackResult.Continue;

        var buf = new short[(int)frameCount];
        Marshal.Copy(input, buf, 0, (int)frameCount);

        var bytes = new byte[buf.Length * 2];
        Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
        _chunks.Enqueue(bytes);

        return StreamCallbackResult.Continue;
    }

    public async Task<string?> StopAndTranscribeAsync(CancellationToken ct = default)
    {
        if (!_recording) return null;

        _recording = false;

        try   { _stream?.Stop();    } catch { }
        try   { _stream?.Dispose(); } catch { }
        finally
        {
            _stream           = null;
            _callbackDelegate = null;
        }

        // Collect all recorded PCM chunks
        var allChunks = new List<byte[]>();
        while (_chunks.TryDequeue(out var chunk))
            allChunks.Add(chunk);

        if (allChunks.Count == 0) return null;

        int totalBytes = allChunks.Sum(b => b.Length);
        var pcm        = new byte[totalBytes];
        int offset     = 0;
        foreach (var chunk in allChunks)
        {
            Buffer.BlockCopy(chunk, 0, pcm, offset, chunk.Length);
            offset += chunk.Length;
        }

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
                SpeechInputProvider.LocalWhisper  => await _local.TranscribeAsync(wav, _localModel, ct),
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

    private static byte[] BuildWav(byte[] pcmBytes)
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
        _recording = false;
        try { _stream?.Stop();    } catch { }
        try { _stream?.Dispose(); } catch { }
        _stream           = null;
        _callbackDelegate = null;

        // ⚠️ Needed since the local model started being kept between utterances. It holds native
        // memory measured in hundreds of megabytes — 1.5 GB for the medium model — and before
        // caching there was nothing to release, because each utterance disposed its own.
        try { _local.Dispose(); } catch { }
    }
}
