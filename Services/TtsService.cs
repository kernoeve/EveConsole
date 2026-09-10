using EveConsole.Agent;

namespace EveConsole.Services;

// Facade over OpenAI/VLC, ElevenLabs, Kokoro, and Piper TTS providers.
// Call Configure() after loading/saving settings.
public sealed class TtsService : IDisposable
{
    private readonly OpenAiTtsService     _openAi     = new();
    private readonly ElevenLabsTtsService _elevenLabs = new();
    private readonly KokoroTtsService     _kokoro     = new();
    private readonly PiperTtsService      _piper      = new();

    // ── OpenAI / VLC ──────────────────────────────────────────────────────────
    public static IReadOnlyList<string> OpenAiVoices => OpenAiTtsService.Voices;
    public static IReadOnlyList<string> OpenAiModels => OpenAiTtsService.Models;
    public static bool                  VlcAvailable => OpenAiTtsService.IsVlcAvailable;

    // ── ElevenLabs ────────────────────────────────────────────────────────────
    public static IReadOnlyList<string> ElevenLabsModels => ElevenLabsTtsService.Models;

    // ── Kokoro ─────────────────────────────────────────────────────────────────
    public KokoroTtsService Kokoro => _kokoro;

    // ── Piper ──────────────────────────────────────────────────────────────────
    public PiperTtsService Piper => _piper;

    // ── Runtime state (not persisted except Volume) ───────────────────────────
    private TtsProvider _provider = TtsProvider.None;
    private float       _volume   = 1f;   // 0.0–1.0
    private bool        _muted    = false;

    public float Volume  => _volume;
    public bool  IsMuted => _muted;

    public void Configure(AgentSettings s)
    {
        _provider = s.TtsProvider;
        _volume   = Math.Clamp(s.TtsVolume, 0f, 1f);

        _openAi.Configure(s.OpenAiApiKey, s.OpenAiTtsVoice, s.OpenAiTtsModel, s.OpenAiTtsSpeed);
        _elevenLabs.Configure(s.ElevenLabsApiKey, s.ElevenLabsVoiceId, s.ElevenLabsModel);
        _kokoro.Configure(s.KokoroVoice);
        _piper.Configure(s.PiperVoice);
        ApplyVolume();

        // Eagerly load Kokoro model if selected and not yet loaded
        if (s.TtsProvider == TtsProvider.Kokoro && !_kokoro.IsReady)
            _ = _kokoro.LoadAsync();

        // Eagerly load Piper voice if selected and already downloaded
        if (s.TtsProvider == TtsProvider.Piper && _piper.IsVoiceDownloaded)
            _ = _piper.LoadVoiceAsync();
    }

    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        ApplyVolume();
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (muted) Stop();
        ApplyVolume();
    }

    private void ApplyVolume()
    {
        var effective = _muted ? 0f : _volume;
        _openAi.SetVolume(effective);
        _elevenLabs.SetVolume(effective);
        _piper.SetVolume(effective);
        // Kokoro uses KokoroSharp's built-in audio — volume control through its own system
    }

    // ── Serial speech queue, for the synchronous local voices ────────────────
    //
    // One utterance at a time and in the order asked for. Speech is now handed over sentence by
    // sentence while the answer is still being written, so without this the second sentence starts
    // before the first has finished and the listener loses whichever lost the race.
    private readonly object _queueGate = new();
    private Task _speechChain = Task.CompletedTask;

    /// <summary>
    /// Bumped by <see cref="Stop"/>. Anything queued under an older generation is dropped rather
    /// than spoken — otherwise stopping would only silence what is playing now and the rest of the
    /// backlog would carry on into the next question.
    /// </summary>
    private int _generation;

    private void Enqueue(Action speak)
    {
        lock (_queueGate)
        {
            var generation = _generation;
            _speechChain = _speechChain.ContinueWith(_ =>
            {
                if (Volatile.Read(ref _generation) != generation) return;   // stopped since queued
                if (_muted) return;
                try   { speak(); }
                catch { /* one failed utterance must not break the chain for every later one */ }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    public void SpeakAsync(string text)
    {
        if (_muted) return;

        // Say system names the way capsuleers do — "C-FD0D" as "C tac F D zero D" — rather than
        // however the engine guesses. Done here, on the way out, so the text shown on screen is
        // unaffected.
        text = EvePronunciation.Expand(text);

        switch (_provider)
        {
            case TtsProvider.OpenAi:
                _ = _openAi.SpeakAsync(text);
                break;

            case TtsProvider.ElevenLabs:
                _ = _elevenLabs.SpeakAsync(text);
                break;

            // ⚠️ Queued, not just moved off the caller's thread. These two are synchronous and
            // void despite the name — Kokoro runs ONNX inference, Piper drives a local binary,
            // both on whatever thread calls them — so they must leave the UI thread. But a bare
            // Task.Run per utterance runs them CONCURRENTLY, and now that speech is fed sentence
            // by sentence as the answer streams, several land at once and talk over each other:
            // parts of the answer are skipped rather than queued. Enqueue serialises them.
            case TtsProvider.Kokoro:
                Enqueue(() => _kokoro.SpeakAsync(text));
                break;

            case TtsProvider.Piper:
                Enqueue(() => _piper.SpeakAsync(text));
                break;
        }
    }

    public void Stop()
    {
        // Drops anything still queued. Without this, stopping silences the current utterance and
        // the backlog simply carries on — into the next question's answer.
        Interlocked.Increment(ref _generation);

        _openAi.Stop();
        _elevenLabs.Stop();
        _kokoro.Stop();
        _piper.Stop();
    }

    public void Dispose()
    {
        _openAi.Dispose();
        _elevenLabs.Dispose();
        _kokoro.Dispose();
        _piper.Dispose();
    }

}
