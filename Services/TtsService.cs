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

    /// <summary>The configured voice or model, kept so a usage row can name what was billed.</summary>
    private string _model = "";

    /// <summary>
    /// Where speech usage is recorded. Optional — absent means unmeasured, not broken.
    /// </summary>
    public Agent.AgentTelemetryService? Telemetry { get; set; }

    public float Volume  => _volume;
    public bool  IsMuted => _muted;

    public void Configure(AgentSettings s)
    {
        _provider = s.TtsProvider;
        _volume   = Math.Clamp(s.TtsVolume, 0f, 1f);

        // Whichever of these is billed depends on the provider, so the name is captured once here
        // rather than reached for at every utterance.
        _model = s.TtsProvider switch
        {
            TtsProvider.OpenAi     => s.OpenAiTtsModel,
            TtsProvider.ElevenLabs => s.ElevenLabsModel,
            TtsProvider.Kokoro     => s.KokoroVoice,
            TtsProvider.Piper      => s.PiperVoice,
            _                      => "",
        };

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

    private void Enqueue(TtsProvider provider, string model, string billedText, Action speak)
    {
        lock (_queueGate)
        {
            var generation = _generation;
            _speechChain = _speechChain.ContinueWith(_ =>
            {
                // ⚠️ Both of these return WITHOUT recording, and that is the point: an utterance
                // dropped here was never handed to a voice, so billing for it would overstate the
                // ledger by however much was queued when the capsuleer hit stop.
                if (Volatile.Read(ref _generation) != generation) return;   // stopped since queued
                if (_muted) return;

                var started = Environment.TickCount64;
                var failure = "";
                // One failed utterance must not break the chain for every later one — but it is
                // recorded rather than discarded, so a voice that has stopped working is visible
                // in the usage detail instead of just producing silence.
                try   { speak(); }
                catch (Exception ex) { failure = ex.Message; }
                Record(provider, model, billedText, started, failure);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Runs a cloud voice and records what it took.
    ///
    /// <para>Takes a factory rather than a started task so the clock begins before the request
    /// does — a task passed in has already been running for however long the caller took.</para>
    /// </summary>
    private async Task SpeakTimedAsync(TtsProvider provider, string model, string billedText, Func<Task> work)
    {
        var started = Environment.TickCount64;
        var failure = "";
        try   { await work().ConfigureAwait(false); }
        catch (Exception ex) { failure = ex.Message; }
        Record(provider, model, billedText, started, failure);
    }

    /// <summary>
    /// Records what an utterance cost.
    ///
    /// <para>⚠️ Counted AFTER the EVE pronunciation pass, because a provider bills what it is
    /// SENT. Expanding "C-FD0D" to "C tac F D zero D" quadruples the text, so counting what was
    /// written rather than what was submitted would understate every intel alert.</para>
    ///
    /// <para>Local voices are logged too, with IsLocal set. They cost nothing, but the volume and
    /// the latency still answer "what would this have cost on a paid voice" — which is the
    /// question worth having an answer to before switching.</para>
    /// </summary>
    /// <para>⚠️ Called when the utterance is DONE, not when it is queued. It used to run at the
    /// top of SpeakAsync against a clock started on the previous line, so every row recorded a
    /// duration of exactly zero — a figure that looked measured and was not — and carried the
    /// timestamp of the enqueue rather than of the speech, which for a serialised queue can be
    /// several seconds earlier and identical across a whole answer.</para>
    /// <para>⚠️ The provider and model are passed IN, not read from the fields. Recording now
    /// happens when the utterance finishes, and the queue means that can be well after it was
    /// spoken — so reading the current setting attributes finished speech to whatever provider
    /// happens to be selected by then. Changing voice mid-answer billed Kokoro's words to OpenAI.</para>
    private void Record(TtsProvider provider, string model, string billedText, long startedTicks, string error = "")
        => Telemetry?.ServiceCall(
            kind:       "tts",
            provider:   provider.ToString(),
            model:      model,
            isLocal:    provider is TtsProvider.Kokoro or TtsProvider.Piper,
            unitKind:   "characters",
            units:      billedText.Length,
            durationMs: (int)(Environment.TickCount64 - startedTicks),
            error:      error);

    public void SpeakAsync(string text)
    {
        if (_muted) return;

        // Say system names the way capsuleers do — "C-FD0D" as "C tac F D zero D" — rather than
        // however the engine guesses. Done here, on the way out, so the text shown on screen is
        // unaffected.
        text = EvePronunciation.Expand(text);

        // Captured HERE, at the moment the utterance is handed over, because the recording that
        // uses them happens after it has been spoken — by which time the selection may have moved.
        var provider = _provider;
        var model    = _model;

        switch (provider)
        {
            case TtsProvider.OpenAi:
                _ = SpeakTimedAsync(provider, model, text, () => _openAi.SpeakAsync(text));
                break;

            case TtsProvider.ElevenLabs:
                _ = SpeakTimedAsync(provider, model, text, () => _elevenLabs.SpeakAsync(text));
                break;

            // ⚠️ Queued, not just moved off the caller's thread. These two are synchronous and
            // void despite the name — Kokoro runs ONNX inference, Piper drives a local binary,
            // both on whatever thread calls them — so they must leave the UI thread. But a bare
            // Task.Run per utterance runs them CONCURRENTLY, and now that speech is fed sentence
            // by sentence as the answer streams, several land at once and talk over each other:
            // parts of the answer are skipped rather than queued. Enqueue serialises them.
            case TtsProvider.Kokoro:
                Enqueue(provider, model, text, () => _kokoro.SpeakAsync(text));
                break;

            case TtsProvider.Piper:
                Enqueue(provider, model, text, () => _piper.SpeakAsync(text));
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
