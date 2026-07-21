using EveConsole.Agent;
using EveConsole.Services;
using ReactiveUI;
using System.Windows.Input;
using System.Linq;

namespace EveConsole.ViewModels;

public sealed class AgentSettingsViewModel : ReactiveObject
{
    private readonly AgentService        _service;
    private readonly TtsService?         _tts;
    private readonly SpeechInputService? _speech;

    // ── personalisation ───────────────────────────────────────────────────────
    private string _agentName = AgentSettings.DefaultAgentName;
    public string AgentName
    {
        get => _agentName;
        set
        {
            this.RaiseAndSetIfChanged(ref _agentName, value);
            this.RaisePropertyChanged(nameof(DisplayAgentName));
            this.RaisePropertyChanged(nameof(HeaderTitleText));
            this.RaisePropertyChanged(nameof(EnableCheckboxText));
            this.RaisePropertyChanged(nameof(EnableHelpText));
            this.RaisePropertyChanged(nameof(HistoryHelpText));
            this.RaisePropertyChanged(nameof(SummarizationHelpText));
            this.RaisePropertyChanged(nameof(TtsVolumeHelpText));
            this.RaisePropertyChanged(nameof(MicHelpText));
            this.RaisePropertyChanged(nameof(PttHelpText));
        }
    }

    // Falls back to the default when the field is left blank, so the labels below always
    // reflect what the agent will actually be called.
    private string DisplayAgentName =>
        string.IsNullOrWhiteSpace(_agentName) ? AgentSettings.DefaultAgentName : _agentName.Trim();

    public string DefaultAgentNameHelpText =>
        $"The name shown in the panel header and used when the agent refers to itself. Default: {AgentSettings.DefaultAgentName}.";

    public string HeaderTitleText     => $"{DisplayAgentName} Agent";
    public string EnableCheckboxText  => $"Enable {DisplayAgentName} AI companion";
    public string EnableHelpText      =>
        $"When enabled, the {DisplayAgentName} panel is available from the title bar. Requires a configured provider below.";
    public string HistoryHelpText     =>
        $"History is saved to disk and reloaded when the application starts. Clear it using the ⌫ button in the {DisplayAgentName} panel.";
    public string SummarizationHelpText =>
        $"When the estimated conversation length crosses this value, {DisplayAgentName} will silently compact older messages into a summary in the background — typically while you are reading her last response. Lower values reduce API cost per message but sacrifice older context. Default: 20,000 (~$0.06/message at that size for Sonnet).";
    public string TtsVolumeHelpText   => $"Volume and mute are available directly in the {DisplayAgentName} panel while it is open.";
    public string MicHelpText         => $"When configured, a mic button appears in the {DisplayAgentName} panel. Hold it to record, release to transcribe.";
    public string PttHelpText         =>
        $"Hold this key to record — works even when the game has focus. F13-F20 are rarely captured by games and recommended as PTT keys. The mic button in the {DisplayAgentName} panel always works regardless of this setting.";

    public IReadOnlyList<VerbositySetting> VerbosityOptions { get; } =
        Enum.GetValues<VerbositySetting>();

    private VerbositySetting _verbosity = VerbositySetting.Balanced;
    public VerbositySetting Verbosity
    {
        get => _verbosity;
        set => this.RaiseAndSetIfChanged(ref _verbosity, value);
    }

    // ── master enable ──────────────────────────────────────────────────────────
    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
    }

    // ── provider selection ─────────────────────────────────────────────────────
    public IReadOnlyList<AgentProviderType> Providers { get; } =
        Enum.GetValues<AgentProviderType>();

    private AgentProviderType _selectedProvider;
    public AgentProviderType SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedProvider, value);
            this.RaisePropertyChanged(nameof(ShowClaude));
            this.RaisePropertyChanged(nameof(ShowOpenAi));
            this.RaisePropertyChanged(nameof(ShowLocal));
        }
    }

    public bool ShowClaude => _selectedProvider == AgentProviderType.Claude;
    public bool ShowOpenAi => _selectedProvider == AgentProviderType.OpenAI;
    public bool ShowLocal  => _selectedProvider == AgentProviderType.Local;

    // ── Claude ─────────────────────────────────────────────────────────────────
    private string _claudeApiKey = "";
    public string ClaudeApiKey
    {
        get => _claudeApiKey;
        set => this.RaiseAndSetIfChanged(ref _claudeApiKey, value);
    }

    private string _claudeModel = "";
    public string ClaudeModel
    {
        get => _claudeModel;
        set => this.RaiseAndSetIfChanged(ref _claudeModel, value);
    }

    // ── OpenAI ─────────────────────────────────────────────────────────────────
    private string _openAiApiKey = "";
    public string OpenAiApiKey
    {
        get => _openAiApiKey;
        set => this.RaiseAndSetIfChanged(ref _openAiApiKey, value);
    }

    private string _openAiModel = "";
    public string OpenAiModel
    {
        get => _openAiModel;
        set => this.RaiseAndSetIfChanged(ref _openAiModel, value);
    }

    // ── Local LLM ──────────────────────────────────────────────────────────────
    private string _localEndpoint = "";
    public string LocalEndpoint
    {
        get => _localEndpoint;
        set => this.RaiseAndSetIfChanged(ref _localEndpoint, value);
    }

    private string _localModel = "";
    public string LocalModel
    {
        get => _localModel;
        set => this.RaiseAndSetIfChanged(ref _localModel, value);
    }

    // ── context management ─────────────────────────────────────────────────────
    private bool _persistHistory;
    public bool PersistHistory
    {
        get => _persistHistory;
        set => this.RaiseAndSetIfChanged(ref _persistHistory, value);
    }

    private int _summarizationThreshold;
    public int SummarizationThreshold
    {
        get => _summarizationThreshold;
        set => this.RaiseAndSetIfChanged(ref _summarizationThreshold, value);
    }

    // ── TTS provider selection ─────────────────────────────────────────────────
    public IReadOnlyList<TtsProvider> TtsProviders { get; } = Enum.GetValues<TtsProvider>();

    private TtsProvider _ttsProvider;
    public TtsProvider TtsProvider
    {
        get => _ttsProvider;
        set
        {
            this.RaiseAndSetIfChanged(ref _ttsProvider, value);
            this.RaisePropertyChanged(nameof(ShowOpenAiTtsSettings));
            this.RaisePropertyChanged(nameof(ShowElevenLabsTtsSettings));
            this.RaisePropertyChanged(nameof(ShowKokoroTtsSettings));
            this.RaisePropertyChanged(nameof(ShowPiperTtsSettings));
        }
    }

    public bool ShowOpenAiTtsSettings       => _ttsProvider == TtsProvider.OpenAi;
    public bool ShowElevenLabsTtsSettings   => _ttsProvider == TtsProvider.ElevenLabs;
    public bool ShowKokoroTtsSettings       => _ttsProvider == TtsProvider.Kokoro;
    public bool ShowPiperTtsSettings        => _ttsProvider == TtsProvider.Piper;

    // ── OpenAI TTS ─────────────────────────────────────────────────────────────
    public IReadOnlyList<string> OpenAiTtsVoices => TtsService.OpenAiVoices;
    public IReadOnlyList<string> OpenAiTtsModels => TtsService.OpenAiModels;

    private string _openAiTtsVoice = "nova";
    public string OpenAiTtsVoice
    {
        get => _openAiTtsVoice;
        set => this.RaiseAndSetIfChanged(ref _openAiTtsVoice, value);
    }

    private string _openAiTtsModel = "tts-1";
    public string OpenAiTtsModel
    {
        get => _openAiTtsModel;
        set => this.RaiseAndSetIfChanged(ref _openAiTtsModel, value);
    }

    private double _openAiTtsSpeed = 1.0;
    public double OpenAiTtsSpeed
    {
        get => _openAiTtsSpeed;
        set => this.RaiseAndSetIfChanged(ref _openAiTtsSpeed, value);
    }

    // ── ElevenLabs TTS ────────────────────────────────────────────────────────
    public IReadOnlyList<string> ElevenLabsModels => TtsService.ElevenLabsModels;

    private string _elevenLabsApiKey = "";
    public string ElevenLabsApiKey
    {
        get => _elevenLabsApiKey;
        set => this.RaiseAndSetIfChanged(ref _elevenLabsApiKey, value);
    }

    private string _elevenLabsVoiceId = "21m00Tcm4TlvDq8ikWAM";
    public string ElevenLabsVoiceId
    {
        get => _elevenLabsVoiceId;
        set => this.RaiseAndSetIfChanged(ref _elevenLabsVoiceId, value);
    }

    private string _elevenLabsModel = "eleven_turbo_v2_5";
    public string ElevenLabsModel
    {
        get => _elevenLabsModel;
        set => this.RaiseAndSetIfChanged(ref _elevenLabsModel, value);
    }

    // ── Kokoro local TTS ──────────────────────────────────────────────────────
    public IReadOnlyList<string> KokoroVoiceLabels =>
        KokoroTtsService.Voices.Select(v => v.Label).ToList();

    private string _kokoroVoiceId = "af_heart";

    public string? SelectedKokoroVoiceLabel
    {
        get => KokoroTtsService.Voices.FirstOrDefault(v => v.Id == _kokoroVoiceId).Label;
        set
        {
            var match = KokoroTtsService.Voices.FirstOrDefault(v => v.Label == value);
            _kokoroVoiceId = match.Id ?? _kokoroVoiceId;
            this.RaisePropertyChanged();
        }
    }

    public bool IsKokoroModelDownloaded => _tts?.Kokoro.IsReady == true;

    private bool _isDownloadingKokoroModel;
    public bool IsDownloadingKokoroModel
    {
        get => _isDownloadingKokoroModel;
        private set => this.RaiseAndSetIfChanged(ref _isDownloadingKokoroModel, value);
    }

    private string _kokoroModelStatus = "";
    public string KokoroModelStatus
    {
        get => _kokoroModelStatus;
        private set => this.RaiseAndSetIfChanged(ref _kokoroModelStatus, value);
    }

    public ICommand DownloadKokoroModelCommand { get; }

    // ── Piper local TTS ───────────────────────────────────────────────────────
    public IReadOnlyList<string> PiperVoiceLabels =>
        PiperTtsService.VoiceCatalogue.Select(v => $"{v.Label}  [{v.Size}]").ToList();

    private string _piperVoiceKey = "en_US-libritts_r-medium";

    public string? SelectedPiperVoiceLabel
    {
        get => PiperTtsService.VoiceCatalogue
            .Select(v => $"{v.Label}  [{v.Size}]")
            .FirstOrDefault(label => PiperTtsService.VoiceCatalogue
                .Any(v => v.Key == _piperVoiceKey && $"{v.Label}  [{v.Size}]" == label));
        set
        {
            var match = PiperTtsService.VoiceCatalogue
                .FirstOrDefault(v => $"{v.Label}  [{v.Size}]" == value);
            if (match != default)
            {
                _piperVoiceKey = match.Key;
                this.RaisePropertyChanged(nameof(IsPiperVoiceDownloaded));
            }
            this.RaisePropertyChanged();
        }
    }

    public bool IsPiperBinaryAvailable => _tts?.Piper.IsBinaryAvailable == true;

    public bool IsPiperVoiceDownloaded =>
        !string.IsNullOrEmpty(_piperVoiceKey) &&
        Directory.Exists(PiperTtsService.GetVoiceModelPath(_piperVoiceKey));

    private bool _isDownloadingPiper;
    public bool IsDownloadingPiper
    {
        get => _isDownloadingPiper;
        private set => this.RaiseAndSetIfChanged(ref _isDownloadingPiper, value);
    }

    private string _piperDownloadStatus = "";
    public string PiperDownloadStatus
    {
        get => _piperDownloadStatus;
        private set => this.RaiseAndSetIfChanged(ref _piperDownloadStatus, value);
    }

    public ICommand DownloadPiperVoiceCommand  { get; }

    // ── Speech input (push-to-talk) ───────────────────────────────────────────
    public IReadOnlyList<SpeechInputProvider> SpeechInputProviders { get; } =
        Enum.GetValues<SpeechInputProvider>();

    private SpeechInputProvider _speechInputProvider;
    public SpeechInputProvider SpeechInputProvider
    {
        get => _speechInputProvider;
        set
        {
            this.RaiseAndSetIfChanged(ref _speechInputProvider, value);
            this.RaisePropertyChanged(nameof(ShowLocalWhisperSettings));
            this.RaisePropertyChanged(nameof(ShowCloudWhisperSettings));
            this.RaisePropertyChanged(nameof(ShowMicrophoneSettings));
            if (value != SpeechInputProvider.None && _microphoneDevices.Count == 0)
                RefreshMicrophoneDevices();
        }
    }

    public bool ShowLocalWhisperSettings => _speechInputProvider == SpeechInputProvider.LocalWhisper;
    public bool ShowCloudWhisperSettings => _speechInputProvider == SpeechInputProvider.OpenAiWhisper;
    public bool ShowMicrophoneSettings   => _speechInputProvider != SpeechInputProvider.None;

    // ── Microphone device selection ────────────────────────────────────────────
    private IReadOnlyList<string> _microphoneDevices = [];
    public IReadOnlyList<string> MicrophoneDevices
    {
        get => _microphoneDevices;
        private set => this.RaiseAndSetIfChanged(ref _microphoneDevices, value);
    }

    private string? _selectedMicrophoneDevice;
    public string? SelectedMicrophoneDevice
    {
        get => _selectedMicrophoneDevice;
        set => this.RaiseAndSetIfChanged(ref _selectedMicrophoneDevice, value);
    }

    private void RefreshMicrophoneDevices()
    {
        var devices = _speech?.GetInputDeviceNames() ?? (IReadOnlyList<string>)[];
        MicrophoneDevices = devices;

        if (_selectedMicrophoneDevice is not null && devices.Contains(_selectedMicrophoneDevice))
            return; // keep saved selection

        SelectedMicrophoneDevice = devices.Count > 0 ? devices[0] : null;
    }

    // ── Push-to-talk global key ────────────────────────────────────────────────
    public IReadOnlyList<string> PushToTalkKeyNames { get; } =
        GlobalHotkeyService.KeyOptions.Select(k => k.Name).ToList();

    private string _selectedPushToTalkKeyName =
        GlobalHotkeyService.KeyOptions[0].Name; // "Disabled"

    public string SelectedPushToTalkKeyName
    {
        get => _selectedPushToTalkKeyName;
        set => this.RaiseAndSetIfChanged(ref _selectedPushToTalkKeyName, value);
    }

    public ICommand RefreshMicDevicesCommand { get; }

    private string _whisperLocalModel = "tiny";
    public string WhisperLocalModel
    {
        get => _whisperLocalModel;
        set => this.RaiseAndSetIfChanged(ref _whisperLocalModel, value);
    }

    public IReadOnlyList<(string Id, string Label)> LocalWhisperModels =>
        LocalWhisperService.Models;

    public IReadOnlyList<string> LocalWhisperModelLabels =>
        LocalWhisperService.Models.Select(m => m.Label).ToList();

    public string? SelectedWhisperModelLabel
    {
        get => LocalWhisperService.Models.FirstOrDefault(m => m.Id == _whisperLocalModel).Label;
        set
        {
            var match = LocalWhisperService.Models.FirstOrDefault(m => m.Label == value);
            WhisperLocalModel = match.Id ?? _whisperLocalModel;
            this.RaisePropertyChanged(nameof(IsSelectedModelDownloaded));
        }
    }

    // Model download state
    private bool _isDownloadingModel;
    public bool IsDownloadingModel
    {
        get => _isDownloadingModel;
        private set => this.RaiseAndSetIfChanged(ref _isDownloadingModel, value);
    }

    private double _modelDownloadProgress;
    public double ModelDownloadProgress
    {
        get => _modelDownloadProgress;
        private set => this.RaiseAndSetIfChanged(ref _modelDownloadProgress, value);
    }

    private string _modelDownloadStatus = "";
    public string ModelDownloadStatus
    {
        get => _modelDownloadStatus;
        private set => this.RaiseAndSetIfChanged(ref _modelDownloadStatus, value);
    }

    public bool IsSelectedModelDownloaded =>
        _speech?.LocalWhisper.IsModelDownloaded(_whisperLocalModel) == true;

    public ICommand DownloadModelCommand { get; }

    public ICommand TestVoiceCommand { get; }

    // ── feedback ───────────────────────────────────────────────────────────────
    private string _saveStatus = "";
    public string SaveStatus
    {
        get => _saveStatus;
        set => this.RaiseAndSetIfChanged(ref _saveStatus, value);
    }

    public ICommand SaveCommand { get; }

    public AgentSettingsViewModel(AgentService service, TtsService? tts = null,
        SpeechInputService? speech = null, GlobalHotkeyService? hotkey = null)
    {
        _service                  = service;
        _tts                      = tts;
        _speech                   = speech;
        TestVoiceCommand          = ReactiveCommand.Create(TestVoice);
        DownloadModelCommand      = ReactiveCommand.Create(DownloadModel);
        DownloadKokoroModelCommand = ReactiveCommand.Create(DownloadKokoroModel);
        DownloadPiperVoiceCommand  = ReactiveCommand.Create(DownloadPiperVoice);
        RefreshMicDevicesCommand  = ReactiveCommand.Create(RefreshMicrophoneDevices);
        LoadFromService();
        SaveCommand               = ReactiveCommand.Create(Save);
    }

    private void LoadFromService()
    {
        var s = _service.Settings;
        _agentName              = string.IsNullOrWhiteSpace(s.AgentName) ? AgentSettings.DefaultAgentName : s.AgentName;
        _verbosity              = s.Verbosity;
        _isEnabled              = s.Enabled;
        _selectedProvider       = s.Provider;
        _claudeApiKey           = s.ClaudeApiKey;
        _claudeModel            = s.ClaudeModel;
        _openAiApiKey           = s.OpenAiApiKey;
        _openAiModel            = s.OpenAiModel;
        _localEndpoint          = s.LocalEndpoint;
        _localModel             = s.LocalModel;
        _persistHistory         = s.PersistHistory;
        _summarizationThreshold = s.SummarizationThreshold;

        _ttsProvider      = s.TtsProvider;
        _openAiTtsVoice   = s.OpenAiTtsVoice;
        _openAiTtsModel   = s.OpenAiTtsModel;
        _openAiTtsSpeed   = s.OpenAiTtsSpeed;

        _elevenLabsApiKey  = s.ElevenLabsApiKey;
        _elevenLabsVoiceId = s.ElevenLabsVoiceId;
        _elevenLabsModel   = s.ElevenLabsModel;

        _kokoroVoiceId = string.IsNullOrEmpty(s.KokoroVoice) ? "af_heart" : s.KokoroVoice;
        _piperVoiceKey = string.IsNullOrEmpty(s.PiperVoice)  ? "en_US-libritts_r-medium" : s.PiperVoice;

        _speechInputProvider      = s.SpeechInputProvider;
        _whisperLocalModel        = s.WhisperLocalModel;
        _selectedMicrophoneDevice = s.MicrophoneDeviceName;
        _selectedPushToTalkKeyName = GlobalHotkeyService.VkName(s.PushToTalkKey) ?? GlobalHotkeyService.KeyOptions[0].Name;

        if (s.SpeechInputProvider != SpeechInputProvider.None)
            RefreshMicrophoneDevices();
    }

    private void Save()
    {
        var settings = new AgentSettings
        {
            AgentName  = string.IsNullOrWhiteSpace(_agentName) ? AgentSettings.DefaultAgentName : _agentName.Trim(),
            Verbosity  = _verbosity,
            Enabled       = _isEnabled,
            Provider      = _selectedProvider,
            ClaudeApiKey  = _claudeApiKey.Trim(),
            ClaudeModel   = string.IsNullOrWhiteSpace(_claudeModel)    ? "claude-sonnet-4-6"          : _claudeModel.Trim(),
            OpenAiApiKey  = _openAiApiKey.Trim(),
            OpenAiModel   = string.IsNullOrWhiteSpace(_openAiModel)    ? "gpt-4o"                     : _openAiModel.Trim(),
            LocalEndpoint = string.IsNullOrWhiteSpace(_localEndpoint)  ? "http://localhost:11434"      : _localEndpoint.Trim(),
            LocalModel    = string.IsNullOrWhiteSpace(_localModel)     ? "llama3.1"                   : _localModel.Trim(),
            PersistHistory         = _persistHistory,
            SummarizationThreshold = _summarizationThreshold < 1000 ? 1000 : _summarizationThreshold,

            TtsProvider    = _ttsProvider,
            OpenAiTtsVoice = _openAiTtsVoice,
            OpenAiTtsModel = _openAiTtsModel,
            OpenAiTtsSpeed = _openAiTtsSpeed,

            ElevenLabsApiKey  = _elevenLabsApiKey.Trim(),
            ElevenLabsVoiceId = _elevenLabsVoiceId.Trim(),
            ElevenLabsModel   = _elevenLabsModel,

            KokoroVoice = _kokoroVoiceId,
            PiperVoice  = _piperVoiceKey,

            SpeechInputProvider   = _speechInputProvider,
            WhisperLocalModel     = _whisperLocalModel,
            MicrophoneDeviceName  = _selectedMicrophoneDevice ?? "",
            PushToTalkKey         = GlobalHotkeyService.KeyOptions
                .FirstOrDefault(k => k.Name == _selectedPushToTalkKeyName).WinVk,
        };

        // Configure speech and TTS FIRST so their IsAvailable/HasTts are already true
        // when _service.Configure raises the Settings property-changed (which re-evaluates
        // HasSpeechInput and HasTts on AgentPanelViewModel).
        _speech?.Configure(settings.SpeechInputProvider, settings.OpenAiApiKey,
                           settings.WhisperLocalModel, settings.MicrophoneDeviceName);
        _tts?.Configure(settings);
        _service.Configure(settings);

        SaveStatus = "Saved.";
    }

    private void TestVoice()
    {
        if (_tts is null) return;
        _tts.Configure(new AgentSettings
        {
            TtsProvider       = _ttsProvider,
            OpenAiApiKey      = _openAiApiKey.Trim(),
            OpenAiTtsVoice    = _openAiTtsVoice,
            OpenAiTtsModel    = _openAiTtsModel,
            OpenAiTtsSpeed    = _openAiTtsSpeed,
            ElevenLabsApiKey  = _elevenLabsApiKey.Trim(),
            ElevenLabsVoiceId = _elevenLabsVoiceId.Trim(),
            ElevenLabsModel   = _elevenLabsModel,
            KokoroVoice       = _kokoroVoiceId,
            PiperVoice        = _piperVoiceKey,
        });
        var name = string.IsNullOrWhiteSpace(_agentName) ? AgentSettings.DefaultAgentName : _agentName.Trim();
        _tts.SpeakAsync($"{name} voice test. Your AI companion is ready, Capsuleer.");
    }

    private void DownloadKokoroModel()
    {
        if (IsDownloadingKokoroModel || _tts is null) return;
        IsDownloadingKokoroModel = true;
        KokoroModelStatus = "Downloading/loading model (~320 MB on first run)…";

        _ = Task.Run(async () =>
        {
            try
            {
                await _tts.Kokoro.LoadAsync(); // KokoroSharp handles download + caching internally
                KokoroModelStatus = "Kokoro model ready.";
                this.RaisePropertyChanged(nameof(IsKokoroModelDownloaded));
            }
            catch (Exception ex)
            {
                KokoroModelStatus = $"Load failed: {ex.Message}";
            }
            finally
            {
                IsDownloadingKokoroModel = false;
            }
        });
    }

    private void DownloadPiperVoice()
    {
        if (IsDownloadingPiper || _tts is null) return;
        IsDownloadingPiper = true;
        PiperDownloadStatus = $"Downloading voice '{_piperVoiceKey}'…";

        _ = Task.Run(async () =>
        {
            try
            {
                _tts.Piper.Configure(_piperVoiceKey);
                await _tts.Piper.DownloadVoiceAsync(
                    new Progress<string>(msg => PiperDownloadStatus = msg),
                    CancellationToken.None);
                this.RaisePropertyChanged(nameof(IsPiperVoiceDownloaded));
                PiperDownloadStatus = "Voice model ready.";
            }
            catch (Exception ex)
            {
                PiperDownloadStatus = $"Download failed: {ex.Message}";
            }
            finally
            {
                IsDownloadingPiper = false;
            }
        });
    }

    private void DownloadModel()
    {
        if (IsDownloadingModel) return;
        var model = _whisperLocalModel;

        IsDownloadingModel    = true;
        ModelDownloadProgress = 0;
        ModelDownloadStatus   = $"Downloading {model}…";

        _ = Task.Run(async () =>
        {
            try
            {
                await _speech!.LocalWhisper.DownloadModelAsync(
                    model,
                    new Progress<double>(bytes =>
                    {
                        ModelDownloadProgress = bytes;
                        ModelDownloadStatus   = $"Downloaded {bytes / 1_048_576.0:F1} MB…";
                    }),
                    CancellationToken.None);

                ModelDownloadStatus = $"Model '{model}' ready.";
                this.RaisePropertyChanged(nameof(IsSelectedModelDownloaded));
            }
            catch (Exception ex)
            {
                ModelDownloadStatus = $"Download failed: {ex.Message}";
            }
            finally
            {
                IsDownloadingModel = false;
            }
        });
    }
}
