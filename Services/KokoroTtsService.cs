using KokoroSharp;
using System.Text.RegularExpressions;

namespace EveCortex.Services;

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

    public void SpeakAsync(string text)
    {
        if (_tts is null) return;
        var stripped = StripMarkdown(text);
        if (string.IsNullOrWhiteSpace(stripped)) return;

        var voice = KokoroVoiceManager.GetVoice(_voiceId);
        _tts.SpeakFast(stripped, voice);
    }

    public void Stop()
    {
        // KokoroSharp's job-queue system doesn't expose a public stop/cancel API.
        // The current utterance finishes naturally; mute state prevents new speech.
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
