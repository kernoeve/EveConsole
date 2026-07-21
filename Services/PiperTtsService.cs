using System.Text.RegularExpressions;
using LibVLCSharp.Shared;
using PiperSharp;
using PiperSharp.Models;

namespace EveConsole.Services;

/// <summary>
/// Local TTS via Piper — high-quality neural TTS using VITS ONNX models.
/// The Piper binary is bundled with the application (downloaded at build time by MSBuild);
/// only voice models (~30–130 MB) are downloaded at runtime.
/// Audio is played via LibVLC for cross-platform consistency.
/// </summary>
public sealed class PiperTtsService : IDisposable
{
    // The Piper binary is bundled in the app output directory (downloaded at build time).
    // Structure: [AppDir]/piper/piper.exe (Windows) or [AppDir]/piper/piper (Linux/macOS).
    private static readonly string BundledExePath = Path.Combine(
        AppContext.BaseDirectory, "piper",
        OperatingSystem.IsWindows() ? "piper.exe" : "piper");

    // Fallback: user-local download path (for backwards compat or developer override).
    private static readonly string LocalAppPiperDir = Path.Combine(
        AppConfig.AppDataDir, "piper");

    private static string ExePath =>
        File.Exists(BundledExePath) ? BundledExePath
        : Path.Combine(LocalAppPiperDir, "piper",
            OperatingSystem.IsWindows() ? "piper.exe" : "piper");

    // LibVLC instance (shared with OpenAI TTS — VLC is already initialised by then).
    private static readonly LibVLC? _vlc;

    static PiperTtsService()
    {
        try
        {
            Core.Initialize();
            _vlc = new LibVLC(enableDebugLogs: false);
        }
        catch { _vlc = null; }
    }

    // Curated English voice list (huggingface key → display label).
    // Quality tiers: low (fast, small), medium (balanced), high (best).
    public static readonly IReadOnlyList<(string Key, string Label, string Size)> VoiceCatalogue =
    [
        ("en_US-libritts_r-medium", "LibriTTS R — US, Medium",        "~130 MB"),
        ("en_US-lessac-high",       "Lessac — US, High",              "~63 MB"),
        ("en_US-ryan-high",         "Ryan — US Male, High",            "~63 MB"),
        ("en_US-amy-medium",        "Amy — US Female, Medium",         "~63 MB"),
        ("en_US-joe-medium",        "Joe — US Male, Medium",           "~63 MB"),
        ("en_US-arctic-medium",     "Arctic — US, Medium",             "~83 MB"),
        ("en_GB-jenny_dioco-medium","Jenny — GB Female, Medium",       "~63 MB"),
        ("en_GB-alan-medium",       "Alan — GB Male, Medium",          "~63 MB"),
        ("en_GB-cori-high",         "Cori — GB Female, High",          "~63 MB"),
        ("en_US-lessac-medium",     "Lessac — US, Medium (compact)",   "~40 MB"),
        ("en_US-ryan-medium",       "Ryan — US Male, Medium (compact)","~40 MB"),
        ("en_US-ljspeech-high",     "LJSpeech — US Female, High",      "~63 MB"),
    ];

    private string       _voiceKey = "en_US-libritts_r-medium";
    private VoiceModel?  _model;
    private int          _volume   = 100; // VLC 0–100 (normal)

    private MediaPlayer? _player;
    private readonly object _playerLock = new();
    private CancellationTokenSource _cts = new();

    public bool IsBinaryAvailable => File.Exists(ExePath);
    public bool IsVoiceDownloaded  => _model is not null || GetVoiceModelPath(_voiceKey) is { } p && Directory.Exists(p);

    public static string GetVoiceModelPath(string key) =>
        Path.Combine(LocalAppPiperDir, "voices", key);

    public void Configure(string voiceKey)
    {
        _voiceKey = string.IsNullOrEmpty(voiceKey) ? "en_US-libritts_r-medium" : voiceKey;
        _model    = null; // will be reloaded on next speak
    }

    public void SetVolume(float volume)
    {
        _volume = (int)(Math.Clamp(volume, 0f, 1f) * 100);
        lock (_playerLock)
        {
            try { if (_player is not null) _player.Volume = _volume; }
            catch { }
        }
    }

    // Download the selected voice model (ONNX + JSON config) to a per-key subdirectory.
    public async Task DownloadVoiceAsync(IProgress<string>? status, CancellationToken ct)
    {
        status?.Report($"Downloading voice '{_voiceKey}'…");
        var voiceDir = GetVoiceModelPath(_voiceKey);
        Directory.CreateDirectory(voiceDir);
        var info = await PiperDownloader.GetModelByKey(_voiceKey);
        if (info is null) { status?.Report($"Voice key '{_voiceKey}' not found in HuggingFace model list."); return; }
        _model = await PiperDownloader.DownloadModel(info, voiceDir);
        status?.Report("Voice model ready.");
    }

    // Load a previously-downloaded voice model from disk.
    public async Task LoadVoiceAsync()
    {
        var voiceDir = GetVoiceModelPath(_voiceKey);
        // PiperDownloader.DownloadModel creates an extra named subdirectory inside voiceDir
        var subDir   = Path.Combine(voiceDir, _voiceKey);
        var loadDir  = Directory.Exists(subDir) ? subDir : voiceDir;
        if (!Directory.Exists(loadDir)) return;
        try { _model = await VoiceModel.LoadModel(loadDir); }
        catch { _model = null; }
    }

    public void SpeakAsync(string text)
    {
        if (_vlc is null) return;
        var stripped = StripMarkdown(text);
        if (string.IsNullOrWhiteSpace(stripped)) return;

        Stop();
        var ct = _cts.Token;
        _ = InferAndPlayAsync(stripped, ct);
    }

    private async Task InferAndPlayAsync(string text, CancellationToken ct)
    {
        if (_model is null)
        {
            await LoadVoiceAsync();
            if (_model is null)
            {
                System.Diagnostics.Debug.WriteLine("[Piper] Voice model not loaded — cannot speak.");
                return;
            }
        }

        try
        {
            var config = new PiperConfiguration
            {
                ExecutableLocation = ExePath,
                WorkingDirectory   = Path.GetDirectoryName(ExePath)!,
                Model              = _model,
            };

            var provider = new PiperProvider(config);
            var wavBytes = await provider.InferAsync(text, AudioOutputType.Wav);
            if (ct.IsCancellationRequested || wavBytes is null || wavBytes.Length == 0) return;

            await PlayWavAsync(wavBytes, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Piper TTS] {ex.Message}");
        }
    }

    private async Task PlayWavAsync(byte[] wavBytes, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"aura_piper_{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(temp, wavBytes, CancellationToken.None);

        try
        {
            using var media = new Media(_vlc!, new Uri(temp));

            MediaPlayer player;
            lock (_playerLock)
            {
                _player?.Dispose();
                _player = player = new MediaPlayer(_vlc!);
                player.Volume = _volume;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

            void OnEnd(object? s, EventArgs e) => tcs.TrySetResult(true);
            player.EndReached       += OnEnd;
            player.EncounteredError += OnEnd;

            try
            {
                player.Play(media);
                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { player.Stop(); }
            finally
            {
                player.EndReached       -= OnEnd;
                player.EncounteredError -= OnEnd;
                lock (_playerLock) { if (_player == player) _player = null; }
                player.Dispose();
            }
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    public void Stop()
    {
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
        lock (_playerLock) { try { _player?.Stop(); } catch { } }
    }

    public void Dispose()
    {
        Stop();
        lock (_playerLock) { _player?.Dispose(); _player = null; }
    }

    private static string StripMarkdown(string text)
    {
        text = Regex.Replace(text, @"```[\s\S]*?```", " ");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
        text = Regex.Replace(text, @"\*([^*]+)\*", "$1");
        text = Regex.Replace(text, @"__([^_]+)__", "$1");
        text = Regex.Replace(text, @"_([^_]+)_", "$1");
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*[>*\-+]\s", "", RegexOptions.Multiline);
        return text.Trim();
    }
}
