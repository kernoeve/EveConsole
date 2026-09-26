using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibVLCSharp.Shared;

namespace EveConsole.Services;

/// <summary>
/// Speech through OpenAI's speech API — OpenAI's own service, or any server that speaks the same
/// API: Kokoro-FastAPI, Chatterbox-TTS-Server, Orpheus-FastAPI, usually on another machine's GPU.
///
/// <para>⚠️ One utterance at a time, and it RETURNS WHEN IT HAS FINISHED PLAYING and THROWS WHEN
/// IT COULD NOT SPEAK. It used to stop whatever was playing before starting — so each sentence of
/// a streamed answer cut off the one before it — and to swallow every failure into a debug line,
/// so a voice that had stopped working was recorded as speech that worked. TtsService's queue
/// orders the sentences now, and needs the failure to know when to hand over to another voice.</para>
/// </summary>
public sealed class OpenAiTtsService : IDisposable
{
    public const string OpenAiEndpoint = "https://api.openai.com/v1";

    /// <summary>
    /// ⚠️ A short connect timeout: a server that is switched off must be given up on in seconds,
    /// or the voice falls silent mid-answer while it waits. The overall timeout is generous —
    /// a long sentence on a busy GPU can take a while to synthesise.
    /// </summary>
    private static readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) })
    {
        Timeout = TimeSpan.FromSeconds(90),
    };

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

    private string _endpoint = OpenAiEndpoint;
    private string _apiKey   = "";
    private string _voice    = "nova";
    private string _model    = "tts-1";
    private double _speed    = 1.0;
    private int    _volume   = 100; // VLC 0–200 (100 = normal)

    private MediaPlayer? _player;
    private readonly object _playerLock = new();
    private CancellationTokenSource _cts = new();

    /// <summary>OpenAI's own service, which needs a key.</summary>
    public bool IsOpenAi => _endpoint == OpenAiEndpoint;

    public void Configure(string apiKey, string voice, string model, double speed) =>
        Configure(OpenAiEndpoint, apiKey, voice, model, speed);

    /// <param name="endpoint">Up to and including /v1: "http://gpu-box:8880/v1".</param>
    public void Configure(string endpoint, string apiKey, string voice, string model, double speed)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? OpenAiEndpoint : endpoint.Trim().TrimEnd('/');
        _apiKey   = apiKey ?? "";
        _voice    = voice ?? "";
        _model    = model ?? "";
        _speed    = speed is < 0.25 or > 4.0 ? 1.0 : speed;

        // OpenAI's defaults; a local server's model and voice are whatever it calls them.
        if (IsOpenAi)
        {
            if (_voice.Length == 0) _voice = "nova";
            if (_model.Length == 0) _model = "tts-1";
        }
    }

    // volume: 0.0 – 1.0 maps to VLC 0–100 (normal output, no amplification)
    public void SetVolume(float volume)
    {
        _volume = (int)(Math.Clamp(volume, 0f, 1f) * 100);
        lock (_playerLock) { try { if (_player is not null) _player.Volume = _volume; } catch { } }
    }

    /// <summary>Stops what is playing, and anything this call's token was handed to.</summary>
    public void Stop()
    {
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
        lock (_playerLock) { try { _player?.Stop(); } catch { } }
    }

    /// <summary>
    /// Whether the voice can be reached, at no cost. OpenAI's service: the free model list, with
    /// the key. A server of our own: any answer at all within a few seconds — it is up; whether
    /// it can speak shows on the next sentence.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_vlc is null) return false;
        if (IsOpenAi && string.IsNullOrEmpty(_apiKey)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}/models");
            if (_apiKey.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return !IsOpenAi || resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }   // timed out
        catch (HttpRequestException) { return false; }
    }

    /// <summary>Speaks one utterance and returns when it has finished playing. Throws when it
    /// could not; returns quietly when stopped.</summary>
    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (_vlc is null) throw new InvalidOperationException("Audio playback (VLC) is not available.");
        if (IsOpenAi && string.IsNullOrEmpty(_apiKey)) throw new InvalidOperationException("No OpenAI API key is set.");
        var stripped = StripMarkdown(text);
        if (string.IsNullOrWhiteSpace(stripped)) return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;

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

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/audio/speech");
            if (_apiKey.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, token);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(CancellationToken.None);
                throw new HttpRequestException(
                    $"{(IsOpenAi ? "OpenAI" : _endpoint)} answered {(int)resp.StatusCode} {resp.ReasonPhrase}: {Short(detail)}");
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(token);
            if (bytes.Length == 0) throw new InvalidOperationException("The voice server returned no audio.");
            await PlayAsync(bytes, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* stopped */ }
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;

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
            void OnError(object? s, EventArgs e) => tcs.TrySetException(new InvalidOperationException("The audio could not be played."));
            player.EndReached       += OnEnd;
            player.EncounteredError += OnError;

            try
            {
                player.Play(media);
                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                player.Stop();
                throw;
            }
            finally
            {
                player.EndReached       -= OnEnd;
                player.EncounteredError -= OnError;
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
