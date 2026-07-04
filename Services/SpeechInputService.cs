using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using EveCortex.Agent;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace EveCortex.Services;

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

    public void Configure(SpeechInputProvider provider, string apiKey, string localModel, string microphoneDeviceName = "")
    {
        _provider             = provider;
        _apiKey               = apiKey ?? "";
        _localModel           = string.IsNullOrWhiteSpace(localModel) ? "tiny" : localModel;
        _microphoneDeviceName = microphoneDeviceName ?? "";
    }

    // Returns available input device names. Initialises PortAudio if needed.
    public IReadOnlyList<string> GetInputDeviceNames()
    {
        if (!EnsurePortAudioInit()) return [];
        var names = new List<string>();
        try
        {
            for (int i = 0; i < PortAudio.DeviceCount; i++)
            {
                var info = PortAudio.GetDeviceInfo(i);
                if (info.maxInputChannels > 0)
                    names.Add(info.name);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeechInput] Device enumeration failed: {ex.Message}");
        }
        return names;
    }

    private int ResolveDeviceIndex()
    {
        if (string.IsNullOrEmpty(_microphoneDeviceName))
            return PortAudio.DefaultInputDevice;

        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            if (PortAudio.GetDeviceInfo(i).name == _microphoneDeviceName)
                return i;
        }

        // Named device not found — fall back to default
        System.Diagnostics.Debug.WriteLine($"[SpeechInput] Microphone '{_microphoneDeviceName}' not found, using default.");
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
            System.Diagnostics.Debug.WriteLine($"[SpeechInput] StartRecording failed: {ex.Message}");
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

        return _provider switch
        {
            SpeechInputProvider.OpenAiWhisper => await _cloud.TranscribeAsync(wav, _apiKey, ct),
            SpeechInputProvider.LocalWhisper  => await _local.TranscribeAsync(wav, _localModel, ct),
            _                                  => null,
        };
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

    private static bool EnsurePortAudioInit()
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
                System.Diagnostics.Debug.WriteLine($"[SpeechInput] PortAudio init failed: {ex.Message}");
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
    }
}
