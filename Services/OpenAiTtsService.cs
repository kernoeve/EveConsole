using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibVLCSharp.Shared;

namespace EveCortex.Services;

public sealed class OpenAiTtsService : IDisposable
{
    private static readonly HttpClient _http = new();

    // LibVLC instance — created once; null if native libs are unavailable.
    private static readonly LibVLC? _vlc;

    static OpenAiTtsService()
    {
        try
        {
            Core.Initialize();
            _vlc = new LibVLC(enableDebugLogs: false);
        }
        catch
        {
            _vlc = null;
        }
    }

    public static bool IsVlcAvailable => _vlc is not null;

    public static readonly IReadOnlyList<string> Voices =
        ["alloy", "ash", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer"];

    public static readonly IReadOnlyList<string> Models =
        ["tts-1", "tts-1-hd", "gpt-4o-mini-tts"];

    private string _apiKey = "";
    private string _voice  = "nova";
    private string _model  = "tts-1";
    private double _speed  = 1.0;
    private int    _volume = 100; // VLC 0–200 (100 = normal)

    private MediaPlayer? _player;
    private readonly object _playerLock = new();
    private CancellationTokenSource _cts = new();

    public void Configure(string apiKey, string voice, string model, double speed)
    {
        _apiKey = apiKey ?? "";
        _voice  = string.IsNullOrEmpty(voice) ? "nova"  : voice;
        _model  = string.IsNullOrEmpty(model) ? "tts-1" : model;
        _speed  = speed is < 0.25 or > 4.0   ? 1.0     : speed;
    }

    // volume: 0.0 – 1.0 maps to VLC 0–100 (normal output, no amplification)
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
        if (_vlc is null || string.IsNullOrEmpty(_apiKey)) return;
        var stripped = StripMarkdown(text);
        if (string.IsNullOrWhiteSpace(stripped)) return;

        Stop();
        var ct = _cts.Token;

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model           = _model,
                input           = stripped,
                voice           = _voice,
                speed           = _speed,
                response_format = "mp3",
            });

            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.openai.com/v1/audio/speech");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req,
                HttpCompletionOption.ResponseContentRead, ct);
            resp.EnsureSuccessStatusCode();

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (ct.IsCancellationRequested) return;

            await PlayAsync(bytes, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenAI TTS] {ex.Message}");
        }
    }

    private async Task PlayAsync(byte[] bytes, CancellationToken ct)
    {
        // Write to a temp file so VLC can read it reliably without stream lifecycle issues.
        var temp = Path.Combine(Path.GetTempPath(), $"aura_{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(temp, bytes, CancellationToken.None);

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
            player.EndReached      += OnEnd;
            player.EncounteredError += OnEnd;

            try
            {
                player.Play(media);
                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                player.Stop();
            }
            finally
            {
                player.EndReached      -= OnEnd;
                player.EncounteredError -= OnEnd;
                lock (_playerLock)
                {
                    if (_player == player) _player = null;
                }
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
