using System.Text.RegularExpressions;
using LibVLCSharp.Shared;
using PiperSharp;
using PiperSharp.Models;
using EveConsole.Localization;

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
    // Each voice's name is its own and stays as it is; what kind of voice it is, and its quality,
    // are translated. Chosen by Key on the settings tab, never by these words.
    public static readonly IReadOnlyList<(string Key, string Label, string Size)> VoiceCatalogue =
    [
        ("en_US-libritts_r-medium", Label("LibriTTS R", SettingsText.PiperKindUs,         SettingsText.PiperQualityMedium),        "~130 MB"),
        ("en_US-lessac-high",       Label("Lessac",     SettingsText.PiperKindUs,         SettingsText.PiperQualityHigh),          "~63 MB"),
        ("en_US-ryan-high",         Label("Ryan",       SettingsText.PiperKindUsMale,     SettingsText.PiperQualityHigh),          "~63 MB"),
        ("en_US-amy-medium",        Label("Amy",        SettingsText.PiperKindUsFemale,   SettingsText.PiperQualityMedium),        "~63 MB"),
        ("en_US-joe-medium",        Label("Joe",        SettingsText.PiperKindUsMale,     SettingsText.PiperQualityMedium),        "~63 MB"),
        ("en_US-arctic-medium",     Label("Arctic",     SettingsText.PiperKindUs,         SettingsText.PiperQualityMedium),        "~83 MB"),
        ("en_GB-jenny_dioco-medium",Label("Jenny",      SettingsText.PiperKindGbFemale,   SettingsText.PiperQualityMedium),        "~63 MB"),
        ("en_GB-alan-medium",       Label("Alan",       SettingsText.PiperKindGbMale,     SettingsText.PiperQualityMedium),        "~63 MB"),
        ("en_GB-cori-high",         Label("Cori",       SettingsText.PiperKindGbFemale,   SettingsText.PiperQualityHigh),          "~63 MB"),
        ("en_US-lessac-medium",     Label("Lessac",     SettingsText.PiperKindUs,         SettingsText.PiperQualityMediumCompact), "~40 MB"),
        ("en_US-ryan-medium",       Label("Ryan",       SettingsText.PiperKindUsMale,     SettingsText.PiperQualityMediumCompact), "~40 MB"),
        ("en_US-ljspeech-high",     Label("LJSpeech",   SettingsText.PiperKindUsFemale,   SettingsText.PiperQualityHigh),          "~63 MB"),
    ];

    private static string Label(string name, string kind, string quality) =>
        string.Format(SettingsText.PiperVoiceLabel, name, kind, quality);

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
        status?.Report(string.Format(SettingsText.VoiceDownloading, _voiceKey));
        var voiceDir = GetVoiceModelPath(_voiceKey);
        Directory.CreateDirectory(voiceDir);
        var info = await PiperDownloader.GetModelByKey(_voiceKey);
        if (info is null) { status?.Report(string.Format(SettingsText.PiperVoiceKeyNotFound, _voiceKey)); return; }
        _model = await PiperDownloader.DownloadModel(info, voiceDir);
        status?.Report(SettingsText.VoiceModelReady);
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

    /// <summary>Can speak now: the bundled runtime is there and the voice has been downloaded.</summary>
    public bool IsAvailable => _vlc is not null && IsBinaryAvailable && IsVoiceDownloaded;

    /// <summary>
    /// Speaks one utterance and returns when it has finished playing. Throws when it could not;
    /// returns quietly when stopped.
    ///
    /// <para>⚠️ It used to stop what was playing, start the new one in the background and return
    /// at once — so each streamed sentence cut the previous one off — and to swallow every failure.
    /// TtsService's queue orders the sentences now, and needs the failure to hand over.</para>
    /// </summary>
    public async Task SpeakAsync(string text, CancellationToken cancel = default)
    {
        if (_vlc is null) throw new InvalidOperationException(SettingsText.PiperNoVlc);
        var stripped = StripMarkdown(text);
        if (string.IsNullOrWhiteSpace(stripped)) return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _cts.Token);
        var ct = linked.Token;

        if (_model is null)
        {
            await LoadVoiceAsync();
            if (_model is null)
                throw new InvalidOperationException(string.Format(SettingsText.PiperVoiceNotLoaded, _voiceKey));
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
            var wavBytes = await provider.InferAsync(stripped, AudioOutputType.Wav);
            if (ct.IsCancellationRequested) return;
            if (wavBytes is null || wavBytes.Length == 0) throw new InvalidOperationException(SettingsText.PiperNoAudio);

            await PlayWavAsync(wavBytes, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* stopped */ }
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
            void OnError(object? s, EventArgs e) => tcs.TrySetException(new InvalidOperationException(SettingsText.PiperPlaybackFailed));
            player.EndReached       += OnEnd;
            player.EncounteredError += OnError;

            try
            {
                player.Play(media);
                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { player.Stop(); throw; }
            finally
            {
                player.EndReached       -= OnEnd;
                player.EncounteredError -= OnError;
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
