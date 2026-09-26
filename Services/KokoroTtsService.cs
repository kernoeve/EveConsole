using KokoroSharp;
using System.Text.RegularExpressions;

namespace EveConsole.Services;

/// <summary>
/// Local TTS via Kokoro 82M (ONNX inference, fully offline after initial download).
/// The ONNX model (~320 MB full precision) is downloaded once, on the first call to
/// LoadAsync(), into the app's data folder. Voice embeddings and the espeak phonemiser ship
/// with the app. No executable code is downloaded — only the neural network weight file.
///
/// <para>⚠️ Everything is found by absolute path. Left to itself KokoroSharp reads "voices" and
/// "kokoro.onnx" relative to the WORKING directory, so what it found depended on how the app
/// was started; and in an installed app the working directory is the version folder, which the
/// next update replaces — taking a downloaded model with it.</para>
/// </summary>
public sealed class KokoroTtsService : IDisposable
{
    /// <summary>Beside the executable: the release carries them (see the csproj).</summary>
    private static string VoicesDir => Path.Combine(AppContext.BaseDirectory, "voices");

    /// <summary>In the data folder, which survives updates, as the Whisper models do.</summary>
    private static string ModelPath => Path.Combine(AppConfig.AppDataDir, "kokoro-models", "kokoro.onnx");

    /// <summary>The full-precision model, from where KokoroSharp's own download takes it.</summary>
    private const string ModelUrl = "https://github.com/taylorchu/kokoro-onnx/releases/download/v0.2.0/kokoro.onnx";
    // ── English voices bundled with KokoroSharp (via NuGet content) ─────────
    public static readonly IReadOnlyList<(string Id, string Label)> Voices =
    [
        // American Female
        ("af_heart",   "Heart (American Female — default)"),
        ("af_sky",     "Sky (American Female)"),
        ("af_bella",   "Bella (American Female)"),
        ("af_sarah",   "Sarah (American Female)"),
        ("af_nicole",  "Nicole (American Female)"),
        ("af_alloy",   "Alloy (American Female)"),
        ("af_nova",    "Nova (American Female)"),
        ("af_jessica", "Jessica (American Female)"),
        ("af_kore",    "Kore (American Female)"),
        ("af_aoede",   "Aoede (American Female)"),
        ("af_river",   "River (American Female)"),
        // American Male
        ("am_adam",    "Adam (American Male)"),
        ("am_michael", "Michael (American Male)"),
        ("am_echo",    "Echo (American Male)"),
        ("am_eric",    "Eric (American Male)"),
        ("am_liam",    "Liam (American Male)"),
        ("am_onyx",    "Onyx (American Male)"),
        ("am_puck",    "Puck (American Male)"),
        // British Female
        ("bf_emma",     "Emma (British Female)"),
        ("bf_isabella", "Isabella (British Female)"),
        ("bf_alice",    "Alice (British Female)"),
        ("bf_lily",     "Lily (British Female)"),
        // British Male
        ("bm_george",  "George (British Male)"),
        ("bm_lewis",   "Lewis (British Male)"),
        ("bm_daniel",  "Daniel (British Male)"),
        ("bm_fable",   "Fable (British Male)"),
    ];

    private KokoroTTS? _tts;
    private string     _voiceId = "af_heart";
    private float      _volume  = 1f;

    /// <summary>0.0–1.0, through KokoroSharp's own playback. Kept and applied on load as well,
    /// so a volume set before the model has loaded is not lost.</summary>
    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        try { _tts?.SetVolume(_volume); } catch { /* the engine has gone */ }
    }

    public bool IsReady => _tts is not null;

    public void Configure(string voiceId)
    {
        _voiceId = string.IsNullOrEmpty(voiceId) ? "af_heart" : voiceId;
    }

    // Load (and download if necessary) the Kokoro ONNX model — ~320 MB the first time.
    private Task? _load;

    /// <summary>
    /// Loads the voices and the model once; later calls return the same task, so an utterance
    /// that arrives while the engine is still loading has something to wait on. A failed load is
    /// retried on the next call rather than remembered.
    /// </summary>
    public Task LoadAsync()
    {
        if (_tts is not null) return Task.CompletedTask;
        if (_load is { IsFaulted: true } or { IsCanceled: true }) _load = null;
        return _load ??= Task.Run(async () =>
        {
            if (KokoroVoiceManager.Voices.Count == 0)
            {
                if (!Directory.Exists(VoicesDir))
                    throw new DirectoryNotFoundException($"Kokoro's voices are missing from {VoicesDir}");
                KokoroVoiceManager.LoadVoicesFromPath(VoicesDir);
            }
            var tts = KokoroTTS.LoadModel(await EnsureModelAsync());
            try { tts.SetVolume(_volume); } catch { /* default volume, then */ }
            _tts = tts;
        });
    }

    /// <summary>The model's path, fetching it first if this machine has never had it.</summary>
    private static async Task<string> EnsureModelAsync()
    {
        var path = ModelPath;
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Where KokoroSharp's own download put it: the working directory — beside the executable
        // for an installed app, and bin\ for a development run. Moved rather than fetched again.
        foreach (var old in new[] { Path.Combine(AppContext.BaseDirectory, "kokoro.onnx"),
                                    Path.Combine(Environment.CurrentDirectory, "kokoro.onnx") }.Distinct())
        {
            if (!File.Exists(old)) continue;
            try { File.Move(old, path); return path; }
            catch (IOException) { /* in use, or another volume that refused: download instead */ }
        }

        // Written under a temporary name and renamed when complete, so a download cut short is
        // never mistaken for the model on the next start.
        var temp = path + ".download";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        using (var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(temp);
            await source.CopyToAsync(target);
        }
        File.Move(temp, path, overwrite: true);
        return path;
    }


    /// <summary>
    /// Speaks one utterance and does not return until it has finished playing.
    ///
    /// <para>⚠️ Blocking on purpose, and this is the whole fix for speech being skipped.
    /// <c>SpeakFast</c> hands back a handle immediately — it SUBMITS the work, it does not play
    /// it — and a later call arriving while the previous one is still speaking cancels it. That
    /// did not matter while the entire answer was spoken in one go. It matters completely now that
    /// sentences are handed over as they stream, because each new one cut off its predecessor and
    /// the listener lost whole passages.</para>
    ///
    /// <para>⚠️ Serialising the CALLS is not enough, which is why the first attempt at this did
    /// not work: they return in microseconds, so every sentence was submitted almost at once and
    /// each cancelled the one before. What has to be serialised is the PLAYBACK, and the only
    /// thing that knows when that ends is the handle.</para>
    ///
    /// <para>Callers arrive through TtsService's queue, which already runs this on a pool thread,
    /// so blocking holds up nothing but the next utterance — which is the point.</para>
    /// </summary>
    /// <summary>Can speak, or can once it has loaded: the voices are here and the last load did
    /// not fail. A model still to be downloaded counts as available — it is fetched on first use.</summary>
    public bool IsAvailable => Directory.Exists(VoicesDir) && _load is not { IsFaulted: true };

    /// <param name="voiceId">Which voice — several Kokoro voices share the one loaded model.
    /// Null: the configured one.</param>
    public void Speak(string text, string? voiceId = null, CancellationToken ct = default)
    {
        using var stopOnCancel = ct.Register(Stop);
        if (ct.IsCancellationRequested) return;

        // ⚠️ An utterance that arrives while the model is still loading WAITS for it rather than
        // being dropped. The model takes seconds to load at startup, and an alarm that fired in
        // the first minute — a store order transition caught by the startup polls — was written
        // as an alert and never spoken. Bounded, so a load that never finishes cannot hold the
        // speech queue for ever; and on the queue's pool thread, so nothing else waits with it.
        if (_tts is null)
        {
            try { LoadAsync().Wait(TimeSpan.FromSeconds(120)); }
            catch { /* the load's own failure, reported below */ }

            // ⚠️ Thrown, not returned: TtsService records a throw as a failed utterance. A plain
            // return was recorded as speech that worked, and the only sign of a voice that could
            // not load was silence.
            if (_tts is null)
                throw new InvalidOperationException(
                    _load?.Exception?.GetBaseException().Message is { } why
                        ? $"The Kokoro voice could not load: {why}"
                        : "The Kokoro voice is still downloading or loading its model.");
        }
        var stripped = StripMarkdown(text);

        if (string.IsNullOrWhiteSpace(stripped)) return;

        var voice  = KokoroVoiceManager.GetVoice(string.IsNullOrEmpty(voiceId) ? _voiceId : voiceId);
        var done   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = _tts.SpeakFast(stripped, voice);

        handle.OnSpeechCompleted += _ => done.TrySetResult();
        handle.OnSpeechCanceled  += _ => done.TrySetResult();

        // ⚠️ The job can finish between being returned and these callbacks being attached — a very
        // short utterance does — and then neither ever fires and this waits out the timeout for
        // nothing.
        if (handle.Job?.isDone == true) done.TrySetResult();

        // A ceiling, not an expectation: if a completion signal is ever missed, speech resumes
        // late rather than stopping for the rest of the session.
        done.Task.Wait(TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Stops whatever is currently being spoken.
    ///
    /// <para>⚠️ This was empty, with a comment saying KokoroSharp exposes no stop or cancel API.
    /// It does — <c>StopPlayback</c> — so asking the agent to stop talking did nothing, and the
    /// previous answer carried on over the next question.</para>
    /// </summary>
    public void Stop()
    {
        try   { _tts?.StopPlayback(); }
        catch { /* nothing useful to do if the engine has already gone */ }
    }

    public void Dispose()
    {
        Stop();
        _tts?.Dispose();
        _tts  = null;
        _load = null;

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
