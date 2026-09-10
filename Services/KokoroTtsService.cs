using KokoroSharp;
using System.Text.RegularExpressions;

namespace EveConsole.Services;

/// <summary>
/// Local TTS via Kokoro 82M (ONNX inference, fully offline after initial download).
/// The ONNX model (~320 MB full precision) is downloaded and cached automatically by
/// KokoroSharp on first call to LoadAsync(). Voice embeddings ship with the NuGet package.
/// No executable code is downloaded — only the neural network weight file.
/// </summary>
public sealed class KokoroTtsService : IDisposable
{
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

    public bool IsReady => _tts is not null;

    public void Configure(string voiceId)
    {
        _voiceId = string.IsNullOrEmpty(voiceId) ? "af_heart" : voiceId;
    }

    // Load (and download if necessary) the Kokoro ONNX model.
    // KokoroSharp caches the model file automatically.
    // Model is ~320 MB on first download; subsequent loads read from cache.
    public Task LoadAsync() => Task.Run(() =>
    {
        _tts = KokoroTTS.LoadModel(); // downloads + caches automatically
    });

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
    public void SpeakAsync(string text)
    {
        if (_tts is null) return;
        var stripped = StripMarkdown(text);
        if (string.IsNullOrWhiteSpace(stripped)) return;

        var voice  = KokoroVoiceManager.GetVoice(_voiceId);
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
        _tts = null;
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
