using EveConsole.Agent;
using EveConsole.Services;
using ReactiveUI;
using System.Reactive.Linq;
using System.Text.Json;
using System.Windows.Input;
using System.Linq;
using EveConsole.Localization;
// The enum by another name: SpeechInputProvider is also the name of the property the list binds to.
using SpeechInputKind = EveConsole.Agent.SpeechInputProvider;

namespace EveConsole.ViewModels;

/// <summary>
/// The AI Agent tab and its four inner tabs, over one settings object.
///
/// <para>Saved as it is changed: once typing pauses for a text box, at once for anything picked,
/// ticked or added. Each save hands the whole object to the agent, its voices and speech input —
/// so a change that comes to nothing is not saved (<see cref="_savedSignature"/>), and speech
/// input is only reconfigured when one of its own fields changed.</para>
/// </summary>
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
        string.Format(SettingsText.AgentHelpUserName, DisplayAgentName, AgentSettings.DefaultUserName);

    public string UserGuidanceHelpText =>
        string.Format(SettingsText.AgentHelpUserGuidance, DisplayAgentName);

    public string DefaultAgentNameHelpText =>
        string.Format(SettingsText.AgentHelpAgentName, AgentSettings.DefaultAgentName);

    public string HeaderTitleText     => string.Format(SettingsText.AgentHeaderTitle, DisplayAgentName);
    public string EnableCheckboxText  => string.Format(SettingsText.AgentEnableCheckbox, DisplayAgentName);
    public string EnableHelpText      =>
        string.Format(SettingsText.AgentHelpEnable, DisplayAgentName);
    public string HistoryHelpText     =>
        string.Format(SettingsText.AgentHelpHistory, DisplayAgentName);
    public string SummarizationHelpText =>
        string.Format(SettingsText.AgentHelpSummarization, DisplayAgentName);
    public string TtsVolumeHelpText   => string.Format(SettingsText.AgentHelpTtsVolume, DisplayAgentName);
    public string MicHelpText         => string.Format(SettingsText.AgentHelpMic, DisplayAgentName);
    public string PttHelpText         =>
        string.Format(SettingsText.AgentHelpPtt, DisplayAgentName);

    /// <summary>The verbosity settings, by the value the settings keep, with the word shown for each
    /// (the list used to show the enum's own names).</summary>
    public IReadOnlyList<Choice<VerbositySetting>> VerbosityOptions { get; } =
    [
        new(VerbositySetting.Concise,  SettingsText.VerbosityConcise),
        new(VerbositySetting.Balanced, SettingsText.VerbosityBalanced),
        new(VerbositySetting.Detailed, SettingsText.VerbosityDetailed),
    ];

    private VerbositySetting _verbosity = VerbositySetting.Balanced;
    public Choice<VerbositySetting> Verbosity
    {
        get => VerbosityOptions.FirstOrDefault(o => o.Value == _verbosity) ?? VerbosityOptions[1];
        set
        {
            // A detaching ComboBox sets null; that is not a choice.
            if (value is null) { this.RaisePropertyChanged(); return; }
            _verbosity = value.Value;
            this.RaisePropertyChanged();
        }
    }

    // ── master enable ──────────────────────────────────────────────────────────
    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
    }

    // ── Models ─────────────────────────────────────────────────────────────────
    //
    // Every model the agent can think with, and the roles that use them: the conversation, the
    // questions that need the capsuleer's data, and the summaries. One model in both roles is how
    // the agent always worked, and is what an older settings file becomes.

    public System.Collections.ObjectModel.ObservableCollection<ModelProfileVm> Models { get; } = [];

    private ModelProfileVm? _selectedModel;
    public ModelProfileVm? SelectedModel
    {
        get => _selectedModel;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedModel, value);
            this.RaisePropertyChanged(nameof(HasSelectedModel));
            _ = value?.ModelList.LoadAsync();   // the service's models, to choose from
        }
    }

    public bool HasSelectedModel => _selectedModel is not null;

    /// <summary>At least one model stays: the conversation needs one.</summary>
    public bool CanRemoveModel => _selectedModel is not null && Models.Count > 1;

    public ICommand AddModelCommand    { get; }
    public ICommand RemoveModelCommand { get; }

    private void AddModel()
    {
        var model = new ModelProfileVm(this, new ModelProfile
        {
            Provider = AgentProviderType.Local,
            Endpoint = "http://localhost:11434",
        });
        Models.Add(model);
        AddChoice(model);
        SelectedModel = model;
    }

    private void RemoveModel()
    {
        if (_selectedModel is not { } model || Models.Count <= 1) return;
        var index = Models.IndexOf(model);
        Models.Remove(model);

        // A role that named it: the conversation takes the first model left; the others go back
        // to "same as conversation" or to no fallback.
        if (_conversationModelId   == model.Id) _conversationModelId   = Models[0].Id;
        if (_conversationFallbackId == model.Id) _conversationFallbackId = "";
        if (_analystModelId        == model.Id) _analystModelId        = "";
        if (_analystFallbackId     == model.Id) _analystFallbackId     = "";
        if (_summaryModelId        == model.Id) _summaryModelId        = "";

        RemoveChoice(model);
        SelectedModel = Models[Math.Min(index, Models.Count - 1)];
    }

    /// <summary>A model's name or service changed: the tags that depend on it follow. The lists
    /// update themselves — the entries are the models.</summary>
    public void OnModelsChanged() => this.RaisePropertyChanged(nameof(RoleSummary));

    // What each role's list offers. The models are the entries; "None" and "Same as conversation"
    // are fixed ones in front.
    private readonly FixedModelChoice _none = new(SettingsText.RoleNone);
    private readonly FixedModelChoice _same = new(SettingsText.RoleSameAsConversation);

    public System.Collections.ObjectModel.ObservableCollection<ModelChoice> ConversationChoices { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<ModelChoice> AnalystChoices      { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<ModelChoice> FallbackChoices     { get; } = [];

    /// <summary>
    /// The lists from scratch, as the tab opens. The selections are read again, by id.
    ///
    /// <para>⚠️ Only then. A list emptied and refilled while the tab is open leaves each role's
    /// dropdown blank: emptying it sends null back through the binding, the role's setter rightly
    /// refuses it, and the binding then takes the role as unchanged and never shows it again. The
    /// role was kept, but all five dropdowns read as cleared. A model added or removed changes the
    /// lists by that one entry instead (AddChoice, RemoveChoice).</para>
    /// </summary>
    private void RebuildChoices()
    {
        ConversationChoices.Clear();
        AnalystChoices.Clear();
        FallbackChoices.Clear();
        AnalystChoices.Add(_same);
        FallbackChoices.Add(_none);
        foreach (var m in Models)
        {
            ConversationChoices.Add(m);
            AnalystChoices.Add(m);
            FallbackChoices.Add(m);
        }
        RaiseRoles();
        this.RaisePropertyChanged(nameof(CanRemoveModel));
    }

    /// <summary>A model added: one more entry in each role's list, and every selection left as it is.</summary>
    private void AddChoice(ModelProfileVm model)
    {
        ConversationChoices.Add(model);
        AnalystChoices.Add(model);
        FallbackChoices.Add(model);
        RaiseRoles();
        this.RaisePropertyChanged(nameof(CanRemoveModel));
    }

    /// <summary>A model removed: its entry out of each list. The roles that named it have been
    /// moved on already, and are shown as they now are.</summary>
    private void RemoveChoice(ModelProfileVm model)
    {
        ConversationChoices.Remove(model);
        AnalystChoices.Remove(model);
        FallbackChoices.Remove(model);
        RaiseRoles();
        this.RaisePropertyChanged(nameof(CanRemoveModel));
    }

    private void RaiseRoles()
    {
        this.RaisePropertyChanged(nameof(SelectedConversationModel));
        this.RaisePropertyChanged(nameof(SelectedConversationFallback));
        this.RaisePropertyChanged(nameof(SelectedAnalystModel));
        this.RaisePropertyChanged(nameof(SelectedAnalystFallback));
        this.RaisePropertyChanged(nameof(SelectedSummaryModel));
        this.RaisePropertyChanged(nameof(IsSplit));
        this.RaisePropertyChanged(nameof(RoleSummary));
    }

    private ModelChoice? Find(string id, FixedModelChoice empty) =>
        id.Length == 0 ? empty : Models.FirstOrDefault(m => m.Id == id) ?? (ModelChoice)empty;

    // ── Roles ───────────────────────────────────────────────────────────────────

    private string _conversationModelId = "";
    public ModelChoice? SelectedConversationModel
    {
        get => Models.FirstOrDefault(m => m.Id == _conversationModelId) ?? Models.FirstOrDefault();
        set
        {
            // ⚠️ Null comes back through the binding whenever the list is rebuilt; it is not a choice.
            if (value is not ModelProfileVm m || m.Id == _conversationModelId) return;
            _conversationModelId = m.Id;
            RaiseRoles();
        }
    }

    private string _conversationFallbackId = "";
    public ModelChoice? SelectedConversationFallback
    {
        get => Find(_conversationFallbackId, _none);
        set
        {
            if (value is null || value.Id == _conversationFallbackId) return;
            _conversationFallbackId = value.Id;
            RaiseRoles();
        }
    }

    private bool _conversationAnnounce = true;
    public bool ConversationAnnounce
    {
        get => _conversationAnnounce;
        set => this.RaiseAndSetIfChanged(ref _conversationAnnounce, value);
    }

    private string _analystModelId = "";
    public ModelChoice? SelectedAnalystModel
    {
        get => Find(_analystModelId, _same);
        set
        {
            if (value is null || value.Id == _analystModelId) return;
            _analystModelId = value.Id;
            RaiseRoles();
        }
    }

    private string _analystFallbackId = "";
    public ModelChoice? SelectedAnalystFallback
    {
        get => Find(_analystFallbackId, _none);
        set
        {
            if (value is null || value.Id == _analystFallbackId) return;
            _analystFallbackId = value.Id;
            RaiseRoles();
        }
    }

    private bool _analystAnnounce = true;
    public bool AnalystAnnounce
    {
        get => _analystAnnounce;
        set => this.RaiseAndSetIfChanged(ref _analystAnnounce, value);
    }

    private string _summaryModelId = "";
    public ModelChoice? SelectedSummaryModel
    {
        get => Find(_summaryModelId, _same);
        set
        {
            if (value is null || value.Id == _summaryModelId) return;
            _summaryModelId = value.Id;
            RaiseRoles();
        }
    }

    /// <summary>Data questions go to a model of their own — so there is routing to describe.</summary>
    public bool IsSplit => _analystModelId.Length > 0 && _analystModelId != _conversationModelId;

    /// <summary>What the roles come to, in a sentence, under the Roles box.</summary>
    public string RoleSummary
    {
        get
        {
            var conversation = (SelectedConversationModel as ModelProfileVm)?.ToProfile().Label ?? SettingsText.RoleFirstModel;
            if (!IsSplit)
                return string.Format(SettingsText.RoleSummaryOne, conversation);
            var analyst = Models.FirstOrDefault(m => m.Id == _analystModelId)?.ToProfile().Label ?? "?";
            return string.Format(SettingsText.RoleSummarySplit, conversation, analyst);
        }
    }

    private string _handOffWhen = AgentSettings.DefaultHandOffWhen;
    /// <summary>Finishes "Send a message to the data model when…" — what the router is told.</summary>
    public string HandOffWhen
    {
        get => _handOffWhen;
        set => this.RaiseAndSetIfChanged(ref _handOffWhen, value);
    }

    public ICommand ResetHandOffWhenCommand { get; }

    // ── When a model changes ──────────────────────────────────────────────────

    private string _modelFailoverMessage = "";
    public string ModelFailoverMessage
    {
        get => _modelFailoverMessage;
        set => this.RaiseAndSetIfChanged(ref _modelFailoverMessage, value);
    }

    private string _modelReturnMessage = "";
    public string ModelReturnMessage
    {
        get => _modelReturnMessage;
        set => this.RaiseAndSetIfChanged(ref _modelReturnMessage, value);
    }

    private int _modelSwitchGapMinutes = 10;
    public int ModelSwitchGapMinutes
    {
        get => _modelSwitchGapMinutes;
        set => this.RaiseAndSetIfChanged(ref _modelSwitchGapMinutes, Math.Clamp(value, 0, 240));
    }

    private int _modelPreferredUpMinutes = 5;
    public int ModelPreferredUpMinutes
    {
        get => _modelPreferredUpMinutes;
        set => this.RaiseAndSetIfChanged(ref _modelPreferredUpMinutes, Math.Clamp(value, 0, 240));
    }

    /// <summary>
    /// Asks one model for a sentence, as it stands on the tab — unsaved, with the keys as typed.
    /// A model of our own costs nothing to ask; a paid one, a few tokens, recorded like any other.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestModelAsync(ModelProfileVm vm)
    {
        var profile = vm.ToProfile();
        var keys    = new AgentSettings
        {
            ClaudeApiKey   = _claudeApiKey.Trim(),
            ClaudeCacheTtl = _claudeCacheTtl,
            OpenAiApiKey   = _openAiApiKey.Trim(),
        };
        if (AgentService.BuildProvider(profile, keys) is not { } provider)
            return (false, profile.Provider switch
            {
                _ when profile.ModelName.Length == 0 => SettingsText.AgentTestChooseModel,
                AgentProviderType.Claude => SettingsText.AgentTestNeedClaudeKey,
                AgentProviderType.OpenAI => SettingsText.AgentTestNeedOpenAiKey,
                _                        => SettingsText.AgentTestNeedAddress,
            });

        var telemetry = _service.Telemetry;
        telemetry?.Begin("settings-test", provider.ProviderName, profile.ModelName, 0);
        var failure = "";
        var reply   = new System.Text.StringBuilder();
        var watch   = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? first = null;
        try
        {
            // A model of our own may have to load onto the GPU first.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await foreach (var chunk in provider.StreamAsync(
                               "You are being tested. Reply with one short sentence.",
                               [new AgentMessage(MessageRole.User, "Say hello.")],
                               tools: null, onUsage: u => telemetry?.Usage(u), ct: timeout.Token).ConfigureAwait(false))
            {
                if (chunk.Length > 0) first ??= watch.Elapsed;
                reply.Append(chunk);
            }

            var text = reply.ToString().Trim();
            if (text.Length == 0) { failure = "no answer"; return (false, SettingsText.AgentTestNoAnswer); }
            if (text.Length > 90) text = text[..90] + "…";
            return (true, string.Format(SettingsText.AgentTestAnswered, (first ?? watch.Elapsed).TotalSeconds, text));
        }
        catch (OperationCanceledException) { failure = "timed out"; return (false, SettingsText.AgentTestTimedOut); }
        catch (Exception ex) { failure = ex.Message; return (false, ex.GetBaseException().Message); }
        finally { telemetry?.Complete(reply.Length, failure); }
    }

    // ── Claude ─────────────────────────────────────────────────────────────────
    private string _claudeApiKey = "";
    public string ClaudeApiKey
    {
        get => _claudeApiKey;
        set
        {
            this.RaiseAndSetIfChanged(ref _claudeApiKey, value);
            if (_selectedModel?.Provider == AgentProviderType.Claude) _selectedModel.KeyChanged();
        }
    }

    // ── Claude prompt-cache lifetime ────────────────────────────────────────────
    /// <summary>The cache lifetimes, by the value the settings keep ("5m", "1h"), with the words shown.</summary>
    public IReadOnlyList<Choice<string>> ClaudeCacheTtlOptions { get; } =
    [
        new("5m", SettingsText.CacheTtl5Minutes),
        new("1h", SettingsText.CacheTtl1Hour),
    ];

    private string _claudeCacheTtl = "5m";
    public Choice<string> ClaudeCacheTtlOption
    {
        get => ClaudeCacheTtlOptions.FirstOrDefault(o => o.Value == _claudeCacheTtl) ?? ClaudeCacheTtlOptions[0];
        set
        {
            // A detaching ComboBox sets null; that is not a choice. (It used to be read as
            // "not 1 hour" and quietly set the lifetime back to 5 minutes.)
            if (value is null) { this.RaisePropertyChanged(); return; }
            if (value.Value == _claudeCacheTtl) return;
            _claudeCacheTtl = value.Value;
            this.RaisePropertyChanged();
        }
    }

    // ── OpenAI ─────────────────────────────────────────────────────────────────
    private string _openAiApiKey = "";
    public string OpenAiApiKey
    {
        get => _openAiApiKey;
        set
        {
            this.RaiseAndSetIfChanged(ref _openAiApiKey, value);
            if (_selectedModel?.Provider == AgentProviderType.OpenAI) _selectedModel.KeyChanged();
            if (_selectedVoice?.Provider == TtsProvider.OpenAi) _selectedVoice.KeyChanged();
            if (_speechInputProvider == SpeechInputKind.OpenAiWhisper) _relistTranscription.OnNext(System.Reactive.Unit.Default);
        }
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
            _ = value?.LoadListsAsync();        // its engine's models and voices, to choose from
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
    }

    private void RemoveVoice()
    {
        if (_selectedVoice is not { } vm) return;
        var at = Voices.IndexOf(vm);
        Voices.Remove(vm);
        Renumber();
        SelectedVoice = Voices.Count == 0 ? null : Voices[Math.Min(at, Voices.Count - 1)];
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
        set
        {
            this.RaiseAndSetIfChanged(ref _elevenLabsApiKey, value);
            if (_selectedVoice?.Provider == TtsProvider.ElevenLabs) _selectedVoice.KeyChanged();
        }
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
    /// <summary>The speech-input providers, by the value the settings keep, with the words shown for
    /// each (the list used to show the enum's own names).</summary>
    public IReadOnlyList<Choice<SpeechInputKind>> SpeechInputProviders { get; } =
    [
        new(SpeechInputKind.None,          SettingsText.SpeechInputNone),
        new(SpeechInputKind.OpenAiWhisper, SettingsText.SpeechInputOpenAiWhisper),
        new(SpeechInputKind.LocalWhisper,  SettingsText.SpeechInputLocalWhisper),
    ];

    private SpeechInputKind _speechInputProvider;
    public Choice<SpeechInputKind> SpeechInputProvider
    {
        get => SpeechInputProviders.FirstOrDefault(o => o.Value == _speechInputProvider) ?? SpeechInputProviders[0];
        set
        {
            // A detaching ComboBox sets null; that is not a choice.
            if (value is null) { this.RaisePropertyChanged(); return; }
            _speechInputProvider = value.Value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(ShowLocalWhisperSettings));
            this.RaisePropertyChanged(nameof(ShowCloudWhisperSettings));
            this.RaisePropertyChanged(nameof(ShowMicrophoneSettings));
            if (value.Value != SpeechInputKind.None && _microphoneDevices.Count == 0)
                RefreshMicrophoneDevices();
            if (value.Value == SpeechInputKind.OpenAiWhisper) _ = TranscriptionList.LoadAsync();
        }
    }

    /// <summary>
    /// A rate row for each model a service listed that has none (see ListedRates): so a model can be
    /// priced as itself, not as the service's "(any model)" rate, without the capsuleer adding it.
    /// </summary>
    internal void AddListedRates((string Kind, string Provider) rate, IReadOnlyList<ModelListing> listed)
    {
        if (_service.Telemetry is not { } telemetry) return;
        var ids = listed.Select(l => l.Id).ToList();
        _ = Task.Run(async () =>
        {
            // And each new row its published rate, where LiteLLM's list has one (see PublishedRates).
            if (await telemetry.AddListedRatesAsync(rate.Kind, rate.Provider, ids) > 0)
                await telemetry.ApplyPublishedRatesAsync();
        });
    }

    // ── OpenAI's transcription model: from its list, never a name written here ──

    private string _transcriptionModel = "";
    public string TranscriptionModel
    {
        get => _transcriptionModel;
        set { this.RaiseAndSetIfChanged(ref _transcriptionModel, value); TranscriptionList.ValueChanged(); }
    }

    /// <summary>OpenAI's transcription models for the key, newest first.</summary>
    public ServiceListChoice TranscriptionList { get; }

    /// <summary>The key being typed: the list is asked for once it settles.</summary>
    private readonly System.Reactive.Subjects.Subject<System.Reactive.Unit> _relistTranscription = new();

    private ServiceListChoice.Source TranscriptionSource()
    {
        var key = _openAiApiKey.Trim();
        return key.Length == 0
            ? ServiceListChoice.Source.Unavailable(SettingsText.TranscriptionNeedOpenAiKey, "none")
            : new(null, $"openai-stt|{key.Length}:{key.GetHashCode()}",   // not the key itself
                  ct => EveConsole.Agent.Providers.OpenAiCompatibleProvider.ListOpenAiTranscriptionModelsAsync(key, ct));
    }

    public bool ShowLocalWhisperSettings => _speechInputProvider == SpeechInputKind.LocalWhisper;
    public bool ShowCloudWhisperSettings => _speechInputProvider == SpeechInputKind.OpenAiWhisper;
    public bool ShowMicrophoneSettings   => _speechInputProvider != SpeechInputKind.None;

    // ── Microphone device selection ────────────────────────────────────────────
    //
    // The first entry is not a device: it is "follow whatever the operating system has as its
    // default input", which the recorder has always supported as an empty name and which the
    // list never offered. Without it the tab pinned the capsuleer to whichever device happened
    // to be first the day they opened it, and a headset plugged in later was never heard.
    //
    // ⚠️ Each entry is the name the recorder is given and the words shown for it: "" and
    // "System default" for that first entry, a device's own name for the rest. The default is
    // told apart by its empty name, never by the words, which are translated.

    private IReadOnlyList<Choice<string>> _microphoneDevices = [];
    public IReadOnlyList<Choice<string>> MicrophoneDevices
    {
        get => _microphoneDevices;
        private set => this.RaiseAndSetIfChanged(ref _microphoneDevices, value);
    }

    /// <summary>The device the recorder is given, as saved; empty for the system default.</summary>
    private string _microphoneDevice = "";
    public Choice<string>? SelectedMicrophoneDevice
    {
        get => _microphoneDevices.FirstOrDefault(d => d.Value == _microphoneDevice);
        set
        {
            // A detaching ComboBox sets null; that is not a choice.
            if (value is null) { this.RaisePropertyChanged(); return; }
            _microphoneDevice = value.Value;
            this.RaisePropertyChanged();
        }
    }

    private void RefreshMicrophoneDevices()
    {
        var found   = _speech?.GetInputDeviceNames() ?? (IReadOnlyList<string>)[];
        var devices = new List<Choice<string>>(found.Count + 1) { new("", SettingsText.MicSystemDefault) };
        devices.AddRange(found.Select(name => new Choice<string>(name, name)));
        MicrophoneDevices = devices;

        // A saved device that is no longer present: the system default, which is what the
        // recorder falls back to anyway. One still present keeps its selection.
        if (devices.All(d => d.Value != _microphoneDevice)) _microphoneDevice = "";
        this.RaisePropertyChanged(nameof(SelectedMicrophoneDevice));
    }

    // ── Push-to-talk global key ────────────────────────────────────────────────
    /// <summary>The keys offered, by the virtual-key code the settings keep (0 for none), with the
    /// words shown for each. Chosen by the code, never by the name: "Disabled" is translated.</summary>
    public IReadOnlyList<Choice<int>> PushToTalkKeyNames { get; } =
        GlobalHotkeyService.KeyOptions.Select(k => new Choice<int>(k.WinVk, k.Name)).ToList();

    /// <summary>The Win32 virtual-key code, as saved; 0 = disabled.</summary>
    private int _pushToTalkKey;

    public Choice<int> SelectedPushToTalkKeyName
    {
        get => PushToTalkKeyNames.FirstOrDefault(k => k.Value == _pushToTalkKey) ?? PushToTalkKeyNames[0];
        set
        {
            // A detaching ComboBox sets null; that is not a choice.
            if (value is null) { this.RaisePropertyChanged(); return; }
            _pushToTalkKey = value.Value;
            this.RaisePropertyChanged();
        }
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

    /// <summary>The local models, by the id the settings keep, with the words shown for each —
    /// chosen by the id, never by the words, which are translated.</summary>
    public IReadOnlyList<Choice<string>> LocalWhisperModelLabels { get; } =
        [.. LocalWhisperService.Models.Select(m => new Choice<string>(m.Id, m.Label))];

    public Choice<string>? SelectedWhisperModelLabel
    {
        get => LocalWhisperModelLabels.FirstOrDefault(m => m.Value == _whisperLocalModel);
        set
        {
            // A detaching ComboBox sets null; that is not a choice.
            if (value is null) { this.RaisePropertyChanged(); return; }
            WhisperLocalModel = value.Value;
            this.RaisePropertyChanged();
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
        private set => this.RaiseAndSetIfChanged(ref _saveStatus, value);
    }

    // ── saving as it is changed ──────────────────────────────────────────────────

    private readonly AutoSave _autoSave;

    /// <summary>The settings as last saved, serialised: a save that would write the same is skipped.
    /// Most of what this tab raises — a selection moving, a list's roles re-read — changes nothing
    /// that is kept.</summary>
    private string _savedSignature = "";

    /// <summary>Speech input's own fields as last given to it. ⚠️ Its Configure warms the local model
    /// up — a transcription of a second of silence — every time it is called, so it is called only
    /// when one of these changed, not at every name typed elsewhere on the tab.</summary>
    private string _speechSignature = "";

    /// <summary>Set while this tab's own save is being handed to the agent, whose Settings then
    /// comes straight back through the subscription below.</summary>
    private bool _configuring;

    private readonly IDisposable _serviceEcho;

    public AgentSettingsViewModel(AgentService service, TtsService? tts = null,
        SpeechInputService? speech = null, GlobalHotkeyService? hotkey = null)
    {
        _service                  = service;
        _tts                      = tts;
        _speech                   = speech;
        _autoSave                 = new AutoSave(SaveAsync,
            ex => SaveStatus = string.Format(CommonText.ErrorWithMessage, ex.Message));
        DownloadModelCommand      = ReactiveCommand.Create(DownloadModel);
        DownloadKokoroModelCommand = ReactiveCommand.Create(DownloadKokoroModel);
        AddVoiceCommand            = ReactiveCommand.Create(AddVoice);
        RemoveVoiceCommand         = ReactiveCommand.Create(RemoveVoice);
        MoveVoiceUpCommand         = ReactiveCommand.Create(() => MoveVoice(-1));
        MoveVoiceDownCommand       = ReactiveCommand.Create(() => MoveVoice(+1));
        AddModelCommand            = ReactiveCommand.Create(AddModel);
        RemoveModelCommand         = ReactiveCommand.Create(RemoveModel);
        ResetHandOffWhenCommand    = ReactiveCommand.Create(() =>
        {
            HandOffWhen = AgentSettings.DefaultHandOffWhen;
            _autoSave.Changed();   // a button, not typing: saved now
        });
        RefreshMicDevicesCommand  = ReactiveCommand.Create(RefreshMicrophoneDevices);
        TranscriptionList         = new ServiceListChoice(() => _transcriptionModel, id => TranscriptionModel = id, TranscriptionSource,
            n => string.Format(SettingsText.TranscriptionListFound, n), () => SettingsText.TranscriptionListEmpty,
            ServiceListChoice.Holds.Models, SettingsText.TypeModelNameInstead, listed => AddListedRates(ListedRates.OpenAiTranscribe, listed));
        _relistTranscription.Throttle(TimeSpan.FromMilliseconds(700))
                            .ObserveOnUi("transcription list")
                            .Subscribe(settled => _ = TranscriptionList.LoadAsync());
        LoadFromService();

        // ⚠️ The agent writes the standing instructions too, through update_guidance. This tab
        // rebuilds its whole settings object on a save, so without following the service's copy a
        // save made after the agent recorded something would write the old text back over it.
        _serviceEcho = _service.WhenAnyValue(x => x.Settings)
                .Subscribe(s =>
                {
                    // ⚠️ Not this tab's own save coming back. It holds the text trimmed and a blank
                    // name as the default's: put on the tab, it would take the space just typed, or
                    // fill a name box somebody has just emptied to type a new one.
                    if (_configuring) return;

                    // Somebody else's values, not an edit: not saved back.
                    using var applying = _autoSave.Suspend();
                    AgentName    = string.IsNullOrWhiteSpace(s.AgentName) ? AgentSettings.DefaultAgentName : s.AgentName;
                    _verbosity   = s.Verbosity;
                    this.RaisePropertyChanged(nameof(Verbosity));
                    UserGuidance = s.UserGuidance ?? "";
                    UserName     = string.IsNullOrWhiteSpace(s.UserName) ? AgentSettings.DefaultUserName : s.UserName;
                });

        // What the tab holds now is what is saved; watched from here on, so loading saves nothing.
        var loaded       = BuildSettings();
        _savedSignature  = JsonSerializer.Serialize(loaded);
        // What speech input was last given: the agent's settings as they stand, not as this tab
        // tidies them (a blank language reads as "en" here), so the first save that differs sends them.
        _speechSignature = SpeechSignature(_service.Settings);
        Watch(this);
        foreach (var model in Models) Watch(model);
        foreach (var voice in Voices) Watch(voice);
        Models.CollectionChanged += (_, e) => ListEdited(e);
        Voices.CollectionChanged += (_, e) => ListEdited(e);

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
        _claudeApiKey           = s.ClaudeApiKey;
        _claudeCacheTtl         = s.ClaudeCacheTtl == "1h" ? "1h" : "5m";
        _openAiApiKey           = s.OpenAiApiKey;
        _persistHistory         = s.PersistHistory;
        _summarizationThreshold = s.SummarizationThreshold;

        s.NormalizeModels();
        Models.Clear();
        foreach (var model in s.Models) Models.Add(new ModelProfileVm(this, model));
        _selectedModel           = Models.FirstOrDefault();
        _conversationModelId     = s.ConversationRole.ModelId;
        _conversationFallbackId  = s.ConversationRole.FallbackId;
        _conversationAnnounce    = s.ConversationRole.Announce;
        _analystModelId          = s.AnalystRole.ModelId;
        _analystFallbackId       = s.AnalystRole.FallbackId;
        _analystAnnounce         = s.AnalystRole.Announce;
        _summaryModelId          = s.SummaryModelId;
        // The words in force, the default's when none of the capsuleer's own are stored.
        _handOffWhen             = s.HandOffWhenText;
        _modelFailoverMessage    = s.ModelFailoverMessageText;
        _modelReturnMessage      = s.ModelReturnMessageText;
        _modelSwitchGapMinutes   = s.ModelSwitchGapMinutes;
        _modelPreferredUpMinutes = s.ModelPreferredUpMinutes;
        RebuildChoices();
        _ = _selectedModel?.ModelList.LoadAsync();   // the first model's list, as the tab opens on it

        s.NormalizeVoices();
        _speechOn                = s.SpeechOn;
        _elevenLabsApiKey        = s.ElevenLabsApiKey;
        _announceVoiceChanges    = s.AnnounceVoiceChanges;
        _voiceHandoverMessage    = s.VoiceHandoverMessageText;
        _voiceReturnMessage      = s.VoiceReturnMessageText;
        _voiceSwitchGapMinutes   = s.VoiceSwitchGapMinutes;
        _voicePreferredUpMinutes = s.VoicePreferredUpMinutes;
        Voices.Clear();
        foreach (var profile in s.Voices) Voices.Add(new VoiceProfileVm(this, _tts, profile));
        Renumber();
        _selectedVoice = Voices.FirstOrDefault();
        _ = _selectedVoice?.LoadListsAsync();

        _speechInputProvider      = s.SpeechInputProvider;
        s.NormalizeTranscription();
        _transcriptionModel       = s.OpenAiTranscriptionModel ?? "";
        if (_speechInputProvider == SpeechInputKind.OpenAiWhisper) _ = TranscriptionList.LoadAsync();
        _whisperLocalModel        = s.WhisperLocalModel;
        _whisperLanguage          = string.IsNullOrWhiteSpace(s.WhisperLanguage) ? "en" : s.WhisperLanguage;
        _microphoneDevice         = s.MicrophoneDeviceName ?? "";
        // A key the list does not offer reads, and is saved, as none — as it always was.
        _pushToTalkKey = GlobalHotkeyService.KeyOptions.Any(k => k.WinVk == s.PushToTalkKey) ? s.PushToTalkKey : 0;

        if (s.SpeechInputProvider != SpeechInputKind.None)
            RefreshMicrophoneDevices();
    }

    /// <summary>Saves a change still waiting — a box losing focus.</summary>
    public Task FlushAsync() => _autoSave.FlushAsync();

    /// <summary>
    /// The Settings window closed: what is waiting is saved, and this tab stops following the
    /// agent's settings. ⚠️ A new one is made each time the window opens; without letting go, every
    /// one ever opened would go on taking the agent's settings for the rest of the session.
    /// </summary>
    public async Task CloseAsync()
    {
        await _autoSave.FlushAsync();
        _serviceEcho.Dispose();
    }

    private void Watch(ReactiveObject source) =>
        source.Changed.Subscribe(e => Edited(e.Sender, e.PropertyName));

    /// <summary>
    /// A property changed on the tab, a model or a voice: saved once typing pauses when it is text,
    /// at once otherwise. One with no public setter is shown, not edited — a status, a list being
    /// fetched — and saves nothing.
    /// </summary>
    private void Edited(object sender, string? name)
    {
        if (name is null || _autoSave.IsSuspended) return;
        var property = sender.GetType().GetProperties().FirstOrDefault(p => p.Name == name);
        if (property?.SetMethod is not { IsPublic: true }) return;
        if (property.PropertyType == typeof(string)) _autoSave.Typed();
        else                                          _autoSave.Changed();
    }

    /// <summary>A model or voice added, removed or moved: watched if new, and saved at once.</summary>
    private void ListEdited(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var added in e.NewItems?.OfType<ReactiveObject>() ?? []) Watch(added);
        _autoSave.Changed();
    }

    /// <summary>What speech input is configured with, as one string to compare.</summary>
    private static string SpeechSignature(AgentSettings s) => JsonSerializer.Serialize(new
    {
        s.SpeechInputProvider, s.OpenAiApiKey, s.WhisperLocalModel, s.MicrophoneDeviceName,
        s.WhisperLanguage, s.OpenAiTranscriptionModel,
    });

    /// <summary>
    /// Hands the tab's settings to the agent, its voices and speech input, which store them.
    ///
    /// <para>What each does with them: the agent writes its settings file and the shared half to
    /// the database, and rebuilds its models only when a model, a role or a key changed; the voices
    /// rebuild only when a voice or its key changed; speech input reopens the microphone only when
    /// the device or the service changed. So a save restarts nothing that did not change.</para>
    /// </summary>
    private Task SaveAsync()
    {
        var settings  = BuildSettings();
        var signature = JsonSerializer.Serialize(settings);
        if (signature == _savedSignature) return Task.CompletedTask;

        // Configure speech and TTS FIRST so their IsAvailable/HasTts are already true
        // when _service.Configure raises the Settings property-changed (which re-evaluates
        // HasSpeechInput and HasTts on AgentPanelViewModel).
        var speech = SpeechSignature(settings);
        if (speech != _speechSignature)
        {
            _speech?.Configure(settings.SpeechInputProvider, settings.OpenAiApiKey,
                               settings.WhisperLocalModel, settings.MicrophoneDeviceName, settings.WhisperLanguage,
                               settings.OpenAiTranscriptionModel ?? "");
            _speechSignature = speech;
        }
        _tts?.Configure(settings);

        _configuring = true;
        try     { _service.Configure(settings); }
        finally { _configuring = false; }

        _savedSignature = signature;
        _autoSave.Flash(s => SaveStatus = s, SettingsText.Saved);
        return Task.CompletedTask;
    }

    private AgentSettings BuildSettings()
    {
        return new AgentSettings
        {
            AgentName  = string.IsNullOrWhiteSpace(_agentName) ? AgentSettings.DefaultAgentName : _agentName.Trim(),
            Verbosity  = _verbosity,
            UserName     = string.IsNullOrWhiteSpace(_userName) ? AgentSettings.DefaultUserName : _userName.Trim(),
            UserGuidance = (_userGuidance ?? "").Trim(),
            Enabled       = _isEnabled,
            ClaudeApiKey  = _claudeApiKey.Trim(),
            ClaudeCacheTtl = _claudeCacheTtl,
            OpenAiApiKey  = _openAiApiKey.Trim(),

            Models           = [.. Models.Select(m => m.ToProfile())],
            ConversationRole = new() { ModelId = _conversationModelId, FallbackId = _conversationFallbackId, Announce = _conversationAnnounce },
            AnalystRole      = new() { ModelId = _analystModelId,      FallbackId = _analystFallbackId,      Announce = _analystAnnounce },
            SummaryModelId   = _summaryModelId,
            // Only the capsuleer's own words are stored; a default's are stored as nothing, so
            // a better default reaches them too (see DefaultWording).
            HandOffWhen             = AgentSettings.HandOffWhenWording.Store(_handOffWhen),
            ModelFailoverMessage    = AgentSettings.ModelFailoverWording.Store(_modelFailoverMessage),
            ModelReturnMessage      = AgentSettings.ModelReturnWording.Store(_modelReturnMessage),
            ModelSwitchGapMinutes   = _modelSwitchGapMinutes,
            ModelPreferredUpMinutes = _modelPreferredUpMinutes,

            // The old single-model fields are carried as they are; the file gets them written
            // back from the data model when it is saved, for an older build.
            Provider      = _service.Settings.Provider,
            ClaudeModel   = _service.Settings.ClaudeModel,
            OpenAiModel   = _service.Settings.OpenAiModel,
            LocalEndpoint = _service.Settings.LocalEndpoint,
            LocalModel    = _service.Settings.LocalModel,

            PersistHistory         = _persistHistory,
            SummarizationThreshold = _summarizationThreshold < 1000 ? 1000 : _summarizationThreshold,

            SpeechOn                = _speechOn,
            Voices                  = [.. Voices.Select(v => v.ToProfile())],
            ElevenLabsApiKey        = _elevenLabsApiKey.Trim(),
            AnnounceVoiceChanges    = _announceVoiceChanges,
            VoiceHandoverMessage    = AgentSettings.VoiceHandoverWording.Store(_voiceHandoverMessage),
            VoiceReturnMessage      = AgentSettings.VoiceReturnWording.Store(_voiceReturnMessage),
            VoiceSwitchGapMinutes   = _voiceSwitchGapMinutes,
            VoicePreferredUpMinutes = _voicePreferredUpMinutes,

            // Kept as they are: the panel sets these, and a Save here must not reset them.
            TtsVolume = _service.Settings.TtsVolume,
            PanelOpen = _service.Settings.PanelOpen,

            SpeechInputProvider   = _speechInputProvider,
            WhisperLocalModel     = _whisperLocalModel,
            WhisperLanguage       = string.IsNullOrWhiteSpace(_whisperLanguage) ? "en" : _whisperLanguage.Trim(),
            OpenAiTranscriptionModel = (_transcriptionModel ?? "").Trim(),
            // The empty name is what the recorder reads as "the system default".
            MicrophoneDeviceName  = _microphoneDevice,
            PushToTalkKey         = _pushToTalkKey,
        };
    }

    /// <summary>
    /// Speaks with one voice as it stands on the tab, unsaved — on its own, not through the list,
    /// so a test neither fails over nor disturbs the voice a conversation is using.
    /// </summary>
    public Task<VoiceTestResult> TestVoiceAsync(VoiceProfileVm voice)
    {
        if (_tts is null) return Task.FromResult(new VoiceTestResult(false, SettingsText.AgentSpeechUnavailable));
        var keys = new AgentSettings { OpenAiApiKey = _openAiApiKey.Trim(), ElevenLabsApiKey = _elevenLabsApiKey.Trim() };
        return _tts.TestVoiceAsync(voice.ToProfile(), keys, string.Format(SettingsText.VoiceTestSentence, voice.SpokenName));
    }

    private void DownloadKokoroModel()
    {
        if (IsDownloadingKokoroModel || _tts is null) return;
        IsDownloadingKokoroModel = true;
        KokoroModelStatus = SettingsText.KokoroDownloading;

        _ = Task.Run(async () =>
        {
            try
            {
                await _tts.Kokoro.LoadAsync(); // downloads the model into the data folder the first time
                KokoroModelStatus = SettingsText.KokoroReady;
                this.RaisePropertyChanged(nameof(IsKokoroModelDownloaded));
            }
            catch (Exception ex)
            {
                KokoroModelStatus = string.Format(SettingsText.KokoroLoadFailed, ex.Message);
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
        ModelDownloadStatus   = string.Format(SettingsText.WhisperDownloading, model);

        _ = Task.Run(async () =>
        {
            try
            {
                await _speech!.LocalWhisper.DownloadModelAsync(
                    model,
                    new Progress<double>(bytes =>
                    {
                        ModelDownloadProgress = bytes;
                        ModelDownloadStatus   = string.Format(SettingsText.WhisperDownloaded, bytes / 1_048_576.0);
                    }),
                    CancellationToken.None);

                ModelDownloadStatus = string.Format(SettingsText.WhisperModelReady, model);
                this.RaisePropertyChanged(nameof(IsSelectedModelDownloaded));
            }
            catch (Exception ex)
            {
                ModelDownloadStatus = string.Format(SettingsText.DownloadFailed, ex.Message);
            }
            finally
            {
                IsDownloadingModel = false;
            }
        });
    }
}
