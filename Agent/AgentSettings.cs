namespace EveConsole.Agent;

public enum AgentProviderType  { Claude, OpenAI, Local }
public enum TtsProvider        { None = 0, OpenAi = 2, ElevenLabs = 3, Kokoro = 4, Piper = 5 }
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
    /// the service ignores a notification whose value is the same reference.</summary>
    public AgentSettings Clone() => (AgentSettings)MemberwiseClone();

    public string ClaudeApiKey  { get; set; } = "";
    public string ClaudeModel   { get; set; } = "claude-sonnet-4-6";

    public string OpenAiApiKey  { get; set; } = "";
    public string OpenAiModel   { get; set; } = "gpt-4o";

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

    // UI state
    public bool PanelOpen { get; set; } = false;

    // Speech input (push-to-talk transcription)
    public SpeechInputProvider SpeechInputProvider { get; set; } = SpeechInputProvider.None;
    // OpenAI Whisper reuses OpenAiApiKey above
    public string WhisperLocalModel     { get; set; } = "tiny";
    public string MicrophoneDeviceName  { get; set; } = "";   // empty = use system default
    public int    PushToTalkKey         { get; set; } = 0;    // 0 = disabled; Win32 VK code otherwise
}
