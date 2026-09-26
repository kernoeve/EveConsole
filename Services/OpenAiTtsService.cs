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

    private string Format => IsOpenAi ? "mp3" : "wav";

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

    /// <summary>What a server of our own is asked to say, unheard, to show it can still speak.</summary>
    internal const string ProbeText = "Ready.";

    /// <summary>
    /// Whether the voice can speak, at no cost. OpenAI's service: the free model list, with the
    /// key. A server of our own: one word, made and not played.
    ///
    /// <para>⚠️ Not merely "does it answer". A server that has lost its GPU — a system update
    /// can do that to a running container — or never loaded its model still answers everything
    /// except the one request that matters. Trusting that would bring the voice back every ten
    /// minutes, only for it to fail on the first sentence and announce the handover all over
    /// again. One word costs nothing on a GPU of our own.</para>
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_vlc is null) return false;
        if (IsOpenAi && string.IsNullOrEmpty(_apiKey)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!IsOpenAi)
            {
                // ⚠️ Generous: the first request after a server starts pays its warm-up and the
                // preparing of the voice — measured at 12 s for Chatterbox Turbo on an RTX 3080
                // that then speaks far faster — and a check that gave up sooner started the app
                // on the next voice, only to "return" five minutes later.
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await SynthesizeAsync(ProbeText, timeout.Token);
                return true;
            }

            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }   // timed out
        catch (HttpRequestException)      { return false; }
        catch (InvalidOperationException) { return false; }   // answered, with no audio
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
            var made  = System.Diagnostics.Stopwatch.StartNew();
            var bytes = await SynthesizeAsync(stripped, token);
            LastSynthesis = made.Elapsed;
            await PlayAsync(bytes, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* stopped */ }
    }

    /// <summary>How long the last utterance took to make, before it could start playing — on a
    /// server, the wait before each sentence.</summary>
    public TimeSpan? LastSynthesis { get; private set; }

    /// <summary>The audio for <paramref name="input"/>. Throws when the service refused, or sent nothing.</summary>
    private async Task<byte[]> SynthesizeAsync(string input, CancellationToken ct)
    {
        // ⚠️ WAV from a server of our own: Orpheus-FastAPI makes nothing else, and Chatterbox
        // and Kokoro-FastAPI both make it too. Size is no concern on a local network. MP3 from
        // OpenAI's service, where it comes over the internet.
        var body = JsonSerializer.Serialize(new
        {
            model           = _model,
            input,
            voice           = _voice,
            speed           = _speed,
            response_format = Format,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/audio/speech");
        if (_apiKey.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(CancellationToken.None);
            throw new HttpRequestException(
                $"{(IsOpenAi ? "OpenAI" : _endpoint)} answered {(int)resp.StatusCode} {resp.ReasonPhrase}: {Reason(detail)}");
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) throw new InvalidOperationException("The voice server returned no audio.");
        return bytes;
    }

    /// <summary>
    /// The reason inside an error answer — FastAPI servers send {"detail": "…"}, OpenAI
    /// {"error": {"message": "…"}} — or the answer itself. It ends up in front of the person
    /// setting the voice up: "Voice file 'Taylor' not found." says what to fix, the raw JSON less so.
    /// </summary>
    private static string Reason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                    return Short(detail.GetString()!);
                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String) return Short(error.GetString()!);
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
                        && message.ValueKind == JsonValueKind.String)
                        return Short(message.GetString()!);
                }
            }
        }
        catch (JsonException) { }
        return Short(body);
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;

    private async Task PlayAsync(byte[] bytes, CancellationToken ct)
    {
        // Write to a temp file so VLC can read it reliably without stream lifecycle issues.
        var temp = Path.Combine(Path.GetTempPath(), $"aura_{Guid.NewGuid():N}.{Format}");
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
