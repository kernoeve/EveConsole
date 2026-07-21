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

    public void SpeakAsync(string text)
    {
        if (_muted) return;

        switch (_provider)
        {
            case TtsProvider.OpenAi:
                _ = _openAi.SpeakAsync(text);
                break;

            case TtsProvider.ElevenLabs:
                _ = _elevenLabs.SpeakAsync(text);
                break;

            case TtsProvider.Kokoro:
                _kokoro.SpeakAsync(text);
                break;

            case TtsProvider.Piper:
                _piper.SpeakAsync(text);
                break;
        }
    }

    public void Stop()
    {
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
