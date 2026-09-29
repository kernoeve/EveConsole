using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibVLCSharp.Shared;
using EveConsole.Localization;

namespace EveConsole.Services;

public sealed class ElevenLabsTtsService : IDisposable
{
    // A short connect timeout, so an unreachable service is handed over in seconds.
    // ⚠️ Not readonly, and only so a harness can put a client over a fake service in its place, as
    // tools/AgentStreamCheck does for the model providers; .NET 9 refuses a reflection write to an
    // initonly static. Nothing in the application assigns it.
    private static HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) })
    {
        Timeout = TimeSpan.FromSeconds(90),
    };

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

    // ⚠️ No voice or model named here: both come from the account's own lists (ListVoicesAsync,
    // ListModelsAsync), chosen on the settings tab. A voice with neither is not set up.
    private string _apiKey  = "";
    private string _voiceId = "";
    private string _model   = "";
    private int    _volume  = 100;

    private MediaPlayer? _player;
    private readonly object _playerLock = new();
    private CancellationTokenSource _cts = new();

    public void Configure(string apiKey, string voiceId, string model)
    {
        _apiKey  = apiKey ?? "";
        _voiceId = (voiceId ?? "").Trim();
        _model   = (model   ?? "").Trim();
    }

    /// <summary>
    /// The models this account can speak with, in ElevenLabs' own order: GET /v1/models, less
    /// those that do not do text to speech. Throws with the service's reason when it refuses.
    /// </summary>
    public static async Task<IReadOnlyList<EveConsole.Agent.ModelListing>> ListModelsAsync(string apiKey, CancellationToken ct = default)
    {
        using var doc = await GetAsync("https://api.elevenlabs.io/v1/models", apiKey, ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return [.. doc.RootElement.EnumerateArray()
            .Where(m => m.TryGetProperty("can_do_text_to_speech", out var tts) && tts.ValueKind == JsonValueKind.True)
            .Select(m => (Id: Text(m, "model_id"), Name: Text(m, "name")))
            .Where(m => m.Id.Length > 0)
            .Select(m => new EveConsole.Agent.ModelListing(m.Id, m.Name.Length > 0 ? m.Name : m.Id))];
    }

    /// <summary>
    /// The voices in this account — its own, and the ones it has added — by name: GET /v2/voices,
    /// every page. Throws with the service's reason when it refuses.
    /// </summary>
    public static async Task<IReadOnlyList<EveConsole.Agent.ModelListing>> ListVoicesAsync(string apiKey, CancellationToken ct = default)
    {
        var voices = new List<EveConsole.Agent.ModelListing>();
        string? next = null;
        do
        {
            var url = "https://api.elevenlabs.io/v2/voices?page_size=100&include_total_count=false"
                    + (next is null ? "" : "&next_page_token=" + Uri.EscapeDataString(next));
            using var doc = await GetAsync(url, apiKey, ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("voices", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var v in list.EnumerateArray())
                {
                    var id = Text(v, "voice_id");
                    if (id.Length == 0) continue;
                    var name     = Text(v, "name");
                    var category = Text(v, "category");
                    voices.Add(new EveConsole.Agent.ModelListing(id,
                        (name.Length > 0 ? name : id) + (category.Length > 0 ? $" · {category}" : "")));
                }
            next = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True
                   && Text(root, "next_page_token") is { Length: > 0 } token ? token : null;
        }
        while (next is not null && voices.Count < 5000);
        return voices;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static async Task<JsonDocument> GetAsync(string url, string apiKey, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("xi-api-key", apiKey);
        using var resp = await _http.SendAsync(req, timeout.Token);
        var body = await resp.Content.ReadAsStringAsync(timeout.Token);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"ElevenLabs answered {(int)resp.StatusCode}: {Reason(body)}", null, resp.StatusCode);
        return JsonDocument.Parse(body);
    }

    /// <summary>The words in an error answer — {"detail": {"message": "…"}} or {"detail": "…"} — or the answer itself.</summary>
    private static string Reason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d))
            {
                if (d.ValueKind == JsonValueKind.String) return d.GetString() ?? body;
                if (d.ValueKind == JsonValueKind.Object && Text(d, "message") is { Length: > 0 } message) return message;
            }
        }
        catch (JsonException) { }
        return body.Length > 200 ? body[..200] + "…" : body;
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

    /// <summary>Whether the voice can be reached, at no cost: the account endpoint, with the key.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (GetVlc() is null || string.IsNullOrEmpty(_apiKey) || _voiceId.Length == 0 || _model.Length == 0) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/user");
            req.Headers.Add("xi-api-key", _apiKey);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
    }

    /// <summary>
    /// Speaks one utterance and returns when it has finished playing. Throws when it could not;
    /// returns quietly when stopped.
    ///
    /// <para>⚠️ It no longer stops what is playing first, and no longer swallows failures: the
    /// first cut each streamed sentence off with the next, the second recorded a voice that had
    /// stopped working as speech that worked. TtsService's queue orders the sentences.</para>
    /// </summary>
    public async Task SpeakAsync(string text, CancellationToken cancel = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _cts.Token);
        var ct = linked.Token;
        try
        {
            var play = await PrepareAsync(text, ct);
            await play(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* stopped */ }
    }

    /// <summary>How long the last utterance took to make, before it could start playing.</summary>
    public TimeSpan? LastSynthesis { get; private set; }

    /// <summary>The last audio made, as it will be played — for a harness to measure.</summary>
    internal byte[]? LastAudio { get; private set; }

    /// <summary>
    /// Makes the audio for an utterance WITHOUT playing it, and returns what plays it — so the next
    /// sentence is made while this one plays, as the other paid and local voices do. Throws when
    /// it could not be made; the returned player returns quietly when stopped.
    /// </summary>
    public async Task<Func<CancellationToken, Task>> PrepareAsync(string text, CancellationToken ct = default)
    {
        var vlc = GetVlc() ?? throw new InvalidOperationException(SettingsText.PiperNoVlc);
        if (string.IsNullOrEmpty(_apiKey)) throw new InvalidOperationException("No ElevenLabs API key is set.");
        if (_voiceId.Length == 0 || _model.Length == 0)
            throw new InvalidOperationException("No ElevenLabs voice or model is chosen for this voice — choose them in Settings.");
        var stripped = StripMarkdown(text);
        if (string.IsNullOrWhiteSpace(stripped)) return _ => Task.CompletedTask;

        var made = System.Diagnostics.Stopwatch.StartNew();
        var body = JsonSerializer.Serialize(new
        {
            text        = stripped,
            model_id    = _model,
            voice_settings = new { stability = 0.5, similarity_boost = 0.75 },
        });

        // ⚠️ WAV, not the MP3 it sends unless asked: the levelling reads WAV, and ElevenLabs'
        // voices are brought to Kokoro's level like every other (see SpeechLoudness). 24 kHz, the
        // highest rate every plan may ask for — 44.1 kHz is for Pro and above.
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.elevenlabs.io/v1/text-to-speech/{_voiceId}?output_format=wav_24000");
        req.Headers.Add("xi-api-key", _apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(CancellationToken.None);
            throw new HttpRequestException(
                $"ElevenLabs answered {(int)resp.StatusCode} {resp.ReasonPhrase}: {(detail.Length > 200 ? detail[..200] + "…" : detail)}");
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) throw new InvalidOperationException("ElevenLabs returned no audio.");
        bytes = SpeechLoudness.Level(bytes);
        LastSynthesis = made.Elapsed;
        LastAudio     = bytes;
        return playCt => PlayMadeAsync(vlc, bytes, playCt);
    }

    private async Task PlayMadeAsync(LibVLC vlc, byte[] bytes, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        try { await PlayAsync(vlc, bytes, linked.Token); }
        catch (OperationCanceledException) when (linked.Token.IsCancellationRequested) { /* stopped */ }
    }

    private async Task PlayAsync(LibVLC vlc, byte[] bytes, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"aura_el_{Guid.NewGuid():N}.wav");
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
            void OnError(object? s, EventArgs e) => tcs.TrySetException(new InvalidOperationException(SettingsText.PiperPlaybackFailed));
            player.EndReached      += OnEnd;
            player.EncounteredError += OnError;

            try
            {
                player.Play(media);
                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { player.Stop(); throw; }
            finally
            {
                player.EndReached      -= OnEnd;
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
