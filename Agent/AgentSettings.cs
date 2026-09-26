namespace EveConsole.Agent;

public enum AgentProviderType  { Claude, OpenAI, Local }
/// <summary>A voice's engine. LocalServer is any server speaking OpenAI's speech API —
/// Kokoro-FastAPI, Chatterbox, Orpheus — usually on another machine's GPU.</summary>
public enum TtsProvider        { None = 0, OpenAi = 2, ElevenLabs = 3, Kokoro = 4, Piper = 5, LocalServer = 6 }
public enum SpeechInputProvider { None, OpenAiWhisper, LocalWhisper }
public enum VerbositySetting   { Concise, Balanced, Detailed }

public sealed class AgentSettings
{
    // Single source of truth for the default agent name — change here only.
    public const string DefaultAgentName = "Eden";

    public bool              Enabled       { get; set; } = false;
    public AgentProviderType Provider      { get; set; } = AgentProviderType.Claude;

    // Personalisation
    public string            AgentName     { get; set; } = DefaultAgentName;
    public VerbositySetting  Verbosity     { get; set; } = VerbositySetting.Balanced;

    /// <summary>What the agent calls the person it is talking to. The instructions say "the
    /// capsuleer" throughout; a name here is substituted for that in the prompt's own framing.</summary>
    public const string DefaultUserName = "capsuleer";
    public string UserName { get; set; } = DefaultUserName;

    /// <summary>
    /// The capsuleer's own standing instructions, given to the agent with every message and
    /// declared to override the built-in guidance. "When I say home, I mean Jita 4-4."
    /// Edited in Settings, or by the agent itself through update_guidance when told
    /// "from now on…".
    /// </summary>
    public string UserGuidance { get; set; } = "";

    /// <summary>A copy, so a change can be published as a NEW Settings value. WhenAnyValue on
    /// the service ignores a notification whose value is the same reference. The voice list is
    /// copied too, so editing the copy's voices never reaches the original.</summary>
    public AgentSettings Clone()
    {
        var copy = (AgentSettings)MemberwiseClone();
        copy.Voices = [.. Voices.Select(v => v.Clone())];
        return copy;
    }

    public string ClaudeApiKey  { get; set; } = "";
    public string ClaudeModel   { get; set; } = "claude-sonnet-4-6";

    /// <summary>
    /// How long Anthropic keeps the cached prompt prefix warm between requests: "5m" (the
    /// default) or "1h". A read refreshes the timer on either; the hour costs 2× to write
    /// against 1.25×, and only pays when turns are typically 5–60 minutes apart. Claude only —
    /// the other providers cache automatically with no lifetime to choose.
    /// </summary>
    public string ClaudeCacheTtl { get; set; } = "5m";

    public string OpenAiApiKey  { get; set; } = "";
    public string OpenAiModel   { get; set; } = "gpt-5";

    public string LocalEndpoint { get; set; } = "http://localhost:11434";
    public string LocalModel    { get; set; } = "llama3.1";

    // Context management
    public bool PersistHistory           { get; set; } = true;
    public int  SummarizationThreshold   { get; set; } = 20_000;

    // Text-to-speech
    public TtsProvider TtsProvider     { get; set; } = TtsProvider.None;

    // OpenAI TTS (reuses OpenAiApiKey above)
    public string OpenAiTtsVoice { get; set; } = "nova";
    public string OpenAiTtsModel { get; set; } = "tts-1";
    public double OpenAiTtsSpeed { get; set; } = 1.0;

    // ElevenLabs TTS
    public string ElevenLabsApiKey  { get; set; } = "";
    public string ElevenLabsVoiceId { get; set; } = "21m00Tcm4TlvDq8ikWAM"; // Rachel
    public string ElevenLabsModel   { get; set; } = "eleven_turbo_v2_5";

    // Kokoro local TTS
    public string KokoroVoice { get; set; } = "af_heart";

    // Piper local TTS
    public string PiperVoice { get; set; } = "en_US-libritts_r-medium";

    // Volume: 0.0–1.0 (saved, applied at startup; mute is always session-only)
    public float TtsVolume { get; set; } = 1.0f;

    // ── Voices ────────────────────────────────────────────────────────────────
    //
    // A voice is part of the persona in a way the model behind it is not: two comparable models
    // answer alike, two voices never sound alike. So there is a list, in order of preference,
    // each voice optionally with a name of its own; the first that can speak is used, the next
    // takes over when it fails — and says so — and the preferred one comes back once it has been
    // up long enough to trust.

    /// <summary>Whether the agent speaks at all.</summary>
    public bool SpeechOn { get; set; }

    /// <summary>The voices, most preferred first.</summary>
    public List<VoiceProfile> Voices { get; set; } = [];

    /// <summary>Speak an announcement when the voice changes in the middle of a session.</summary>
    public bool AnnounceVoiceChanges { get; set; } = true;

    /// <summary>Spoken by the voice taking over. {previous} and {current} are the two names.</summary>
    public string VoiceHandoverMessage { get; set; } =
        "Sorry, {previous} had to step away. I'm {current}, and I'll pick up from here.";

    /// <summary>Spoken by the preferred voice when it returns.</summary>
    public string VoiceReturnMessage { get; set; } =
        "{current} here, back with you. Thank you, {previous}.";

    /// <summary>At least this long between one voice change and the next voluntary one, so a
    /// flaky server cannot make the persona flip back and forth. A failing voice is always
    /// replaced at once.</summary>
    public int VoiceSwitchGapMinutes { get; set; } = 10;

    /// <summary>How long the preferred voice must stay up, checked continuously, before the
    /// switch back to it.</summary>
    public int VoicePreferredUpMinutes { get; set; } = 5;

    /// <summary>Speech is on and there is something to speak with.</summary>
    public bool SpeechActive => SpeechOn && Voices.Count > 0;

    /// <summary>
    /// Brings a file written before voices became a list up to date: the single voice it
    /// describes becomes the first in the list. Idempotent; called after every load.
    /// </summary>
    public void NormalizeVoices()
    {
        if (Voices.Count > 0 || TtsProvider == TtsProvider.None) return;
        Voices   = [VoiceProfile.FromLegacy(this)];
        SpeechOn = true;
    }

    /// <summary>
    /// Writes the first voice back into the single-voice fields, so an older build that opens
    /// this file still finds a voice to speak with.
    /// </summary>
    public void MirrorLegacyVoice()
    {
        var first = SpeechOn && Voices.Count > 0 ? Voices[0] : null;
        TtsProvider = first is null ? TtsProvider.None
                    : first.Provider == TtsProvider.LocalServer ? TtsProvider.Kokoro   // unknown to an older build
                    : first.Provider;
        if (first is null) return;
        KokoroVoice       = first.KokoroVoice;
        PiperVoice        = first.PiperVoice;
        OpenAiTtsVoice    = first.OpenAiVoice;
        OpenAiTtsModel    = first.OpenAiModel;
        OpenAiTtsSpeed    = first.OpenAiSpeed;
        ElevenLabsVoiceId = first.ElevenLabsVoiceId;
        ElevenLabsModel   = first.ElevenLabsModel;
    }

    // UI state
    public bool PanelOpen { get; set; } = false;

    // Speech input (push-to-talk transcription)
    public SpeechInputProvider SpeechInputProvider { get; set; } = SpeechInputProvider.None;
    // OpenAI Whisper reuses OpenAiApiKey above
    public string WhisperLocalModel     { get; set; } = "tiny";
    /// <summary>The language spoken, as a two-letter code, or "auto" for the model's own guess —
    /// which on a two-second clip is often wrong and always costs an extra pass.</summary>
    public string WhisperLanguage       { get; set; } = "en";
    public string MicrophoneDeviceName  { get; set; } = "";   // empty = use system default
    public int    PushToTalkKey         { get; set; } = 0;    // 0 = disabled; Win32 VK code otherwise
}

/// <summary>
/// One voice the agent can speak with. Only the fields of its own engine matter; the rest keep
/// their defaults, so switching a profile's engine back and forth loses nothing typed.
/// </summary>
public sealed class VoiceProfile
{
    /// <summary>Who this voice is. Blank: the agent's own name from Personalisation.</summary>
    public string      Name     { get; set; } = "";
    public TtsProvider Provider { get; set; } = TtsProvider.Kokoro;

    public string KokoroVoice { get; set; } = "af_heart";
    public string PiperVoice  { get; set; } = "en_US-libritts_r-medium";

    // OpenAI's own service; the key is AgentSettings.OpenAiApiKey, shared with the model.
    public string OpenAiVoice { get; set; } = "nova";
    public string OpenAiModel { get; set; } = "tts-1";
    public double OpenAiSpeed { get; set; } = 1.0;

    // ElevenLabs; the key is AgentSettings.ElevenLabsApiKey.
    public string ElevenLabsVoiceId { get; set; } = "21m00Tcm4TlvDq8ikWAM"; // Rachel
    public string ElevenLabsModel   { get; set; } = "eleven_turbo_v2_5";

    // A server of our own speaking OpenAI's speech API.
    /// <summary>Up to and including /v1, as those servers document it: http://gpu-box:8880/v1.</summary>
    public string ServerUrl    { get; set; } = "http://localhost:8880/v1";
    public string ServerModel  { get; set; } = "";
    public string ServerVoice  { get; set; } = "";
    public double ServerSpeed  { get; set; } = 1.0;
    /// <summary>Most local servers want none; some want any non-empty key.</summary>
    public string ServerApiKey { get; set; } = "";

    public VoiceProfile Clone() => (VoiceProfile)MemberwiseClone();

    internal static VoiceProfile FromLegacy(AgentSettings s) => new()
    {
        Provider          = s.TtsProvider,
        KokoroVoice       = s.KokoroVoice,
        PiperVoice        = s.PiperVoice,
        OpenAiVoice       = s.OpenAiTtsVoice,
        OpenAiModel       = s.OpenAiTtsModel,
        OpenAiSpeed       = s.OpenAiTtsSpeed,
        ElevenLabsVoiceId = s.ElevenLabsVoiceId,
        ElevenLabsModel   = s.ElevenLabsModel,
    };

    /// <summary>"Kokoro — Heart", "Local server — chatterbox": what the list shows.</summary>
    public string Describe() => Provider switch
    {
        TtsProvider.Kokoro      => $"Kokoro — {KokoroVoice}",
        TtsProvider.Piper       => $"Piper — {PiperVoice}",
        TtsProvider.OpenAi      => $"OpenAI — {OpenAiVoice}",
        TtsProvider.ElevenLabs  => $"ElevenLabs — {ElevenLabsVoiceId}",
        TtsProvider.LocalServer => $"Local server — {(ServerModel.Length > 0 ? ServerModel : ServerUrl)}{(ServerVoice.Length > 0 ? $" ({ServerVoice})" : "")}",
        _                       => Provider.ToString(),
    };
}
