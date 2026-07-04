using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibVLCSharp.Shared;

namespace EveCortex.Services;

public sealed class ElevenLabsTtsService : IDisposable
{
    private static readonly HttpClient _http = new();

    // Reuse the LibVLC instance already initialized by OpenAiTtsService.
    private static LibVLC? _vlc;
    private static bool _vlcInitialized;

    private static LibVLC? GetVlc()
    {
        if (_vlcInitialized) return _vlc;
        _vlcInitialized = true;
        try
        {
            Core.Initialize();
            _vlc = new LibVLC(enableDebugLogs: false);
        }
        catch { _vlc = null; }
        return _vlc;
    }

    public static bool IsVlcAvailable => GetVlc() is not null;

    public static readonly IReadOnlyList<string> Models =
        ["eleven_turbo_v2_5", "eleven_flash_v2_5", "eleven_multilingual_v2", "eleven_turbo_v2"];

    private string _apiKey  = "";
    private string _voiceId = "21m00Tcm4TlvDq8ikWAM";
    private string _model   = "eleven_turbo_v2_5";
    private int    _volume  = 100;

    private MediaPlayer? _player;
    private readonly object _playerLock = new();
    private CancellationTokenSource _cts = new();

    public void Configure(string apiKey, string voiceId, string model)
    {
        _apiKey  = apiKey  ?? "";
        _voiceId = string.IsNullOrEmpty(voiceId) ? "21m00Tcm4TlvDq8ikWAM" : voiceId;
        _model   = string.IsNullOrEmpty(model)   ? "eleven_turbo_v2_5"    : model;
    }

    public void SetVolume(float volume)
    {
        _volume = (int)(Math.Clamp(volume, 0f, 1f) * 100);
        lock (_playerLock) { try { if (_player is not null) _player.Volume = _volume; } catch { } }
    }

    public void Stop()
    {
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
        lock (_playerLock) { try { _player?.Stop(); } catch { } }
    }

    public async Task SpeakAsync(string text)
    {
        var vlc = GetVlc();
        if (vlc is null || string.IsNullOrEmpty(_apiKey)) return;
        var stripped = StripMarkdown(text);
        if (string.IsNullOrWhiteSpace(stripped)) return;

        Stop();
        var ct = _cts.Token;

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                text        = stripped,
                model_id    = _model,
                voice_settings = new { stability = 0.5, similarity_boost = 0.75 },
            });

            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.elevenlabs.io/v1/text-to-speech/{_voiceId}");
            req.Headers.Add("xi-api-key", _apiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req,
                HttpCompletionOption.ResponseContentRead, ct);
            resp.EnsureSuccessStatusCode();

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (ct.IsCancellationRequested) return;

            await PlayAsync(vlc, bytes, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ElevenLabs TTS] {ex.Message}");
        }
    }

    private async Task PlayAsync(LibVLC vlc, byte[] bytes, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"aura_el_{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(temp, bytes, CancellationToken.None);

        try
        {
            using var media = new Media(vlc, new Uri(temp));

            MediaPlayer player;
            lock (_playerLock)
            {
                _player?.Dispose();
                _player = player = new MediaPlayer(vlc);
                player.Volume = _volume;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

            void OnEnd(object? s, EventArgs e) => tcs.TrySetResult(true);
            player.EndReached      += OnEnd;
            player.EncounteredError += OnEnd;

            try
            {
                player.Play(media);
                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { player.Stop(); }
            finally
            {
                player.EndReached      -= OnEnd;
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
