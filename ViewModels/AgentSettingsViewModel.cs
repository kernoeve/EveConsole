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
            this.RaisePropertyChanged(nameof(DefaultName));
            foreach (var voice in Voices) voice.RefreshLabel();
        }
    }

    // Falls back to the default when the field is left blank, so the labels below always
    // reflect what the agent will actually be called.
    private string DisplayAgentName =>
        string.IsNullOrWhiteSpace(_agentName) ? AgentSettings.DefaultAgentName : _agentName.Trim();

    // ── The person ─────────────────────────────────────────────────────────────
    private string _userName = AgentSettings.DefaultUserName;
    public string UserName
    {
        get => _userName;
        set
        {
            this.RaiseAndSetIfChanged(ref _userName, value);
            this.RaisePropertyChanged(nameof(UserGuidanceHelpText));
        }
    }

    private string _userGuidance = "";
    public string UserGuidance
    {
        get => _userGuidance;
        set => this.RaiseAndSetIfChanged(ref _userGuidance, value);
    }

    public string UserNameHelpText =>
        $"What {DisplayAgentName} calls you. Default: {AgentSettings.DefaultUserName}. Set it to your main and they will use it.";

    public string UserGuidanceHelpText =>
        $"Given to {DisplayAgentName} with every message, and declared to override their standard guidance — what your words mean, who people are, how you want them to behave. One instruction per line. " +
        $"You can also just tell them: \"when I say home, I mean Jita 4-4\" — they record it here themselves.";

    public string DefaultAgentNameHelpText =>
        $"The name shown in the panel header and used when the agent refers to itself. Default: {AgentSettings.DefaultAgentName}.";

    public string HeaderTitleText     => $"{DisplayAgentName} Agent";
    public string EnableCheckboxText  => $"Enable {DisplayAgentName} AI companion";
    public string EnableHelpText      =>
        $"When enabled, the {DisplayAgentName} panel is available from the title bar. Requires a configured provider below.";
    public string HistoryHelpText     =>
        $"History is saved to disk and reloaded when the application starts. Clear it using the ⌫ button in the {DisplayAgentName} panel.";
    public string SummarizationHelpText =>
        $"When the estimated conversation length crosses this value, {DisplayAgentName} will silently compact older messages into a summary in the background — typically while you are reading their last response. Lower values reduce API cost per message but sacrifice older context. Default: 20,000 (~$0.06/message at that size for Sonnet).";
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

    // ── Claude prompt-cache lifetime ────────────────────────────────────────────
    public IReadOnlyList<string> ClaudeCacheTtlOptions { get; } = ["5 minutes (default)", "1 hour"];

    private string _claudeCacheTtl = "5m";
    public string ClaudeCacheTtlOption
    {
        get => _claudeCacheTtl == "1h" ? ClaudeCacheTtlOptions[1] : ClaudeCacheTtlOptions[0];
        set
        {
            var ttl = value == ClaudeCacheTtlOptions[1] ? "1h" : "5m";
            if (ttl == _claudeCacheTtl) return;
            _claudeCacheTtl = ttl;
            this.RaisePropertyChanged();
        }
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

    // ── Voices ─────────────────────────────────────────────────────────────────
    //
    // A list, in order of preference: the first that can speak is used, and when it fails the
    // next takes over. Each voice can carry a name of its own — the voice is part of who the
    // capsuleer is talking to — which stands in for the agent's name while that voice speaks.

    private bool _speechOn;
    public bool SpeechOn
    {
        get => _speechOn;
        set => this.RaiseAndSetIfChanged(ref _speechOn, value);
    }

    public System.Collections.ObjectModel.ObservableCollection<VoiceProfileVm> Voices { get; } = [];

    private VoiceProfileVm? _selectedVoice;
    public VoiceProfileVm? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedVoice, value);
            this.RaisePropertyChanged(nameof(HasSelectedVoice));
        }
    }

    public bool HasSelectedVoice => _selectedVoice is not null;

    public ICommand AddVoiceCommand      { get; }
    public ICommand RemoveVoiceCommand   { get; }
    public ICommand MoveVoiceUpCommand   { get; }
    public ICommand MoveVoiceDownCommand { get; }

    /// <summary>The agent's own name, which a voice without a name of its own goes by.</summary>
    public string DefaultName => DisplayAgentName;

    private void AddVoice()
    {
        // A new voice starts as Kokoro — free and bundled — and goes to the end: a fallback,
        // until it is moved up.
        var vm = new VoiceProfileVm(this, _tts, new VoiceProfile());
        Voices.Add(vm);
        Renumber();
        SelectedVoice = vm;
        OnVoiceEnginesChanged();
    }

    private void RemoveVoice()
    {
        if (_selectedVoice is not { } vm) return;
        var at = Voices.IndexOf(vm);
        Voices.Remove(vm);
        Renumber();
        SelectedVoice = Voices.Count == 0 ? null : Voices[Math.Min(at, Voices.Count - 1)];
        OnVoiceEnginesChanged();
    }

    private void MoveVoice(int by)
    {
        if (_selectedVoice is not { } vm) return;
        var from = Voices.IndexOf(vm);
        var to   = from + by;
        if (from < 0 || to < 0 || to >= Voices.Count) return;
        Voices.Move(from, to);
        Renumber();
        SelectedVoice = vm;
    }

    private void Renumber()
    {
        for (var i = 0; i < Voices.Count; i++) Voices[i].Position = i + 1;
    }

    /// <summary>A voice's engine changed, or a voice came or went: the Kokoro model box shows
    /// only while some voice uses it.</summary>
    public void OnVoiceEnginesChanged() => this.RaisePropertyChanged(nameof(UsesKokoro));

    public bool UsesKokoro => Voices.Any(v => v.Provider == TtsProvider.Kokoro);

    // ── When the voice changes ─────────────────────────────────────────────────

    private bool _announceVoiceChanges = true;
    public bool AnnounceVoiceChanges
    {
        get => _announceVoiceChanges;
        set => this.RaiseAndSetIfChanged(ref _announceVoiceChanges, value);
    }

    private string _voiceHandoverMessage = "";
    public string VoiceHandoverMessage
    {
        get => _voiceHandoverMessage;
        set => this.RaiseAndSetIfChanged(ref _voiceHandoverMessage, value);
    }

    private string _voiceReturnMessage = "";
    public string VoiceReturnMessage
    {
        get => _voiceReturnMessage;
        set => this.RaiseAndSetIfChanged(ref _voiceReturnMessage, value);
    }

    private int _voiceSwitchGapMinutes = 10;
    public int VoiceSwitchGapMinutes
    {
        get => _voiceSwitchGapMinutes;
        set => this.RaiseAndSetIfChanged(ref _voiceSwitchGapMinutes, Math.Max(0, value));
    }

    private int _voicePreferredUpMinutes = 5;
    public int VoicePreferredUpMinutes
    {
        get => _voicePreferredUpMinutes;
        set => this.RaiseAndSetIfChanged(ref _voicePreferredUpMinutes, Math.Max(0, value));
    }

    // ── Keys the voices share with the rest of the agent ─────────────────────────

    private string _elevenLabsApiKey = "";
    public string ElevenLabsApiKey
    {
        get => _elevenLabsApiKey;
        set => this.RaiseAndSetIfChanged(ref _elevenLabsApiKey, value);
    }

    // ── Kokoro's model: one, however many Kokoro voices are listed ───────────────

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
    //
    // The first entry is not a device: it is "follow whatever the operating system has as its
    // default input", which the recorder has always supported as an empty name and which the
    // list never offered. Without it the tab pinned the capsuleer to whichever device happened
    // to be first the day they opened it, and a headset plugged in later was never heard.
    public const string SystemDefaultMicrophone = "System default";

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
        var found   = _speech?.GetInputDeviceNames() ?? (IReadOnlyList<string>)[];
        var devices = new List<string>(found.Count + 1) { SystemDefaultMicrophone };
        devices.AddRange(found);
        MicrophoneDevices = devices;

        if (_selectedMicrophoneDevice is not null && devices.Contains(_selectedMicrophoneDevice))
            return; // keep saved selection

        // A saved device that is no longer present, or nothing saved at all: the system default,
        // which is what the recorder falls back to anyway.
        SelectedMicrophoneDevice = SystemDefaultMicrophone;
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

    private string _whisperLanguage = "en";
    public string WhisperLanguage
    {
        get => _whisperLanguage;
        set => this.RaiseAndSetIfChanged(ref _whisperLanguage, value ?? "");
    }

    /// <summary>Where the local model runs, once it has loaded.</summary>
    public string LocalWhisperRuntime => _speech?.LocalWhisper.LoadedRuntime ?? "";

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
        DownloadModelCommand      = ReactiveCommand.Create(DownloadModel);
        DownloadKokoroModelCommand = ReactiveCommand.Create(DownloadKokoroModel);
        AddVoiceCommand            = ReactiveCommand.Create(AddVoice);
        RemoveVoiceCommand         = ReactiveCommand.Create(RemoveVoice);
        MoveVoiceUpCommand         = ReactiveCommand.Create(() => MoveVoice(-1));
        MoveVoiceDownCommand       = ReactiveCommand.Create(() => MoveVoice(+1));
        RefreshMicDevicesCommand  = ReactiveCommand.Create(RefreshMicrophoneDevices);
        LoadFromService();
        SaveCommand               = ReactiveCommand.Create(Save);

        // ⚠️ The agent writes the standing instructions too, through update_guidance. This tab
        // rebuilds its whole settings object on Save, so without following the service's copy a
        // Save pressed after the agent recorded something would write the old text back over it.
        _service.WhenAnyValue(x => x.Settings)
                .Subscribe(s =>
                {
                    AgentName    = string.IsNullOrWhiteSpace(s.AgentName) ? AgentSettings.DefaultAgentName : s.AgentName;
                    Verbosity    = s.Verbosity;
                    UserGuidance = s.UserGuidance ?? "";
                    UserName     = string.IsNullOrWhiteSpace(s.UserName) ? AgentSettings.DefaultUserName : s.UserName;
                });

        // What another client has written since this one started. The subscription above puts
        // it on the tab when it lands.
        _ = _service.RefreshSharedAsync();
    }

    private void LoadFromService()
    {
        var s = _service.Settings;
        _agentName              = string.IsNullOrWhiteSpace(s.AgentName) ? AgentSettings.DefaultAgentName : s.AgentName;
        _verbosity              = s.Verbosity;
        _userName               = string.IsNullOrWhiteSpace(s.UserName) ? AgentSettings.DefaultUserName : s.UserName;
        _userGuidance           = s.UserGuidance ?? "";
        _isEnabled              = s.Enabled;
        _selectedProvider       = s.Provider;
        _claudeApiKey           = s.ClaudeApiKey;
        _claudeModel            = s.ClaudeModel;
        _claudeCacheTtl         = s.ClaudeCacheTtl == "1h" ? "1h" : "5m";
        _openAiApiKey           = s.OpenAiApiKey;
        _openAiModel            = s.OpenAiModel;
        _localEndpoint          = s.LocalEndpoint;
        _localModel             = s.LocalModel;
        _persistHistory         = s.PersistHistory;
        _summarizationThreshold = s.SummarizationThreshold;

        s.NormalizeVoices();
        _speechOn                = s.SpeechOn;
        _elevenLabsApiKey        = s.ElevenLabsApiKey;
        _announceVoiceChanges    = s.AnnounceVoiceChanges;
        _voiceHandoverMessage    = s.VoiceHandoverMessage;
        _voiceReturnMessage      = s.VoiceReturnMessage;
        _voiceSwitchGapMinutes   = s.VoiceSwitchGapMinutes;
        _voicePreferredUpMinutes = s.VoicePreferredUpMinutes;
        Voices.Clear();
        foreach (var profile in s.Voices) Voices.Add(new VoiceProfileVm(this, _tts, profile));
        Renumber();
        _selectedVoice = Voices.FirstOrDefault();

        _speechInputProvider      = s.SpeechInputProvider;
        _whisperLocalModel        = s.WhisperLocalModel;
        _whisperLanguage          = string.IsNullOrWhiteSpace(s.WhisperLanguage) ? "en" : s.WhisperLanguage;
        _selectedMicrophoneDevice = string.IsNullOrEmpty(s.MicrophoneDeviceName) ? SystemDefaultMicrophone : s.MicrophoneDeviceName;
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
            UserName     = string.IsNullOrWhiteSpace(_userName) ? AgentSettings.DefaultUserName : _userName.Trim(),
            UserGuidance = (_userGuidance ?? "").Trim(),
            Enabled       = _isEnabled,
            Provider      = _selectedProvider,
            ClaudeApiKey  = _claudeApiKey.Trim(),
            ClaudeModel   = string.IsNullOrWhiteSpace(_claudeModel)    ? "claude-sonnet-4-6"          : _claudeModel.Trim(),
            ClaudeCacheTtl = _claudeCacheTtl,
            OpenAiApiKey  = _openAiApiKey.Trim(),
            OpenAiModel   = string.IsNullOrWhiteSpace(_openAiModel)    ? "gpt-5"                      : _openAiModel.Trim(),
            LocalEndpoint = string.IsNullOrWhiteSpace(_localEndpoint)  ? "http://localhost:11434"      : _localEndpoint.Trim(),
            LocalModel    = string.IsNullOrWhiteSpace(_localModel)     ? "llama3.1"                   : _localModel.Trim(),
            PersistHistory         = _persistHistory,
            SummarizationThreshold = _summarizationThreshold < 1000 ? 1000 : _summarizationThreshold,

            SpeechOn                = _speechOn,
            Voices                  = [.. Voices.Select(v => v.ToProfile())],
            ElevenLabsApiKey        = _elevenLabsApiKey.Trim(),
            AnnounceVoiceChanges    = _announceVoiceChanges,
            VoiceHandoverMessage    = string.IsNullOrWhiteSpace(_voiceHandoverMessage) ? new AgentSettings().VoiceHandoverMessage : _voiceHandoverMessage.Trim(),
            VoiceReturnMessage      = string.IsNullOrWhiteSpace(_voiceReturnMessage)   ? new AgentSettings().VoiceReturnMessage   : _voiceReturnMessage.Trim(),
            VoiceSwitchGapMinutes   = _voiceSwitchGapMinutes,
            VoicePreferredUpMinutes = _voicePreferredUpMinutes,

            // Kept as they are: the panel sets these, and a Save here must not reset them.
            TtsVolume = _service.Settings.TtsVolume,
            PanelOpen = _service.Settings.PanelOpen,

            SpeechInputProvider   = _speechInputProvider,
            WhisperLocalModel     = _whisperLocalModel,
            WhisperLanguage       = string.IsNullOrWhiteSpace(_whisperLanguage) ? "en" : _whisperLanguage.Trim(),
            // The empty name is what the recorder reads as "the system default".
            MicrophoneDeviceName  = _selectedMicrophoneDevice is null or SystemDefaultMicrophone ? "" : _selectedMicrophoneDevice,
            PushToTalkKey         = GlobalHotkeyService.KeyOptions
                .FirstOrDefault(k => k.Name == _selectedPushToTalkKeyName).WinVk,
        };

        // Configure speech and TTS FIRST so their IsAvailable/HasTts are already true
        // when _service.Configure raises the Settings property-changed (which re-evaluates
        // HasSpeechInput and HasTts on AgentPanelViewModel).
        _speech?.Configure(settings.SpeechInputProvider, settings.OpenAiApiKey,
                           settings.WhisperLocalModel, settings.MicrophoneDeviceName, settings.WhisperLanguage);
        _tts?.Configure(settings);
        _service.Configure(settings);

        SaveStatus = "Saved.";
    }

    /// <summary>
    /// Speaks with one voice as it stands on the tab, unsaved — on its own, not through the list,
    /// so a test neither fails over nor disturbs the voice a conversation is using.
    /// </summary>
    public Task<VoiceTestResult> TestVoiceAsync(VoiceProfileVm voice)
    {
        if (_tts is null) return Task.FromResult(new VoiceTestResult(false, "Speech is not available."));
        var keys = new AgentSettings { OpenAiApiKey = _openAiApiKey.Trim(), ElevenLabsApiKey = _elevenLabsApiKey.Trim() };
        return _tts.TestVoiceAsync(voice.ToProfile(), keys, $"{voice.SpokenName} voice test. Your AI companion is ready, Capsuleer.");
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
                await _tts.Kokoro.LoadAsync(); // downloads the model into the data folder the first time
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
