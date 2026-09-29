using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows.Input;
using EveConsole.Agent;
using EveConsole.Agent.Providers;
using EveConsole.Services;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

/// <summary>
/// One voice on the settings tab: its persona's name, its engine and that engine's settings.
/// Every engine's fields are kept, so switching a voice's engine back and forth loses nothing
/// typed; only the engine chosen is used.
/// </summary>
public sealed class VoiceProfileVm : ReactiveObject
{
    private readonly AgentSettingsViewModel _owner;
    private readonly TtsService?            _tts;

    /// <summary>The settings tab, for what belongs to an engine rather than a voice — its key,
    /// Kokoro's model — shown with whichever voice uses that engine.</summary>
    public AgentSettingsViewModel Owner => _owner;

    public VoiceProfileVm(AgentSettingsViewModel owner, TtsService? tts, VoiceProfile p)
    {
        _owner = owner;
        _tts   = tts;
        _name              = p.Name;
        _provider          = p.Provider is TtsProvider.None ? TtsProvider.Kokoro : p.Provider;
        _kokoroVoiceId     = string.IsNullOrEmpty(p.KokoroVoice) ? "af_heart" : p.KokoroVoice;
        _piperVoiceKey     = string.IsNullOrEmpty(p.PiperVoice) ? "en_US-libritts_r-medium" : p.PiperVoice;
        _openAiVoice       = p.OpenAiVoice;
        _openAiModel       = p.OpenAiModel;
        _openAiSpeed       = p.OpenAiSpeed;
        _elevenLabsVoiceId = p.ElevenLabsVoiceId;
        _elevenLabsVoiceName = p.ElevenLabsVoiceName;
        _elevenLabsModel   = p.ElevenLabsModel;
        _serverUrl         = p.ServerUrl;
        _serverModel       = p.ServerModel;
        _serverVoice       = p.ServerVoice;
        _serverSpeed       = p.ServerSpeed;
        _serverApiKey      = p.ServerApiKey;

        TestVoiceCommand          = ReactiveCommand.CreateFromTask(TestAsync);
        DownloadPiperVoiceCommand = ReactiveCommand.Create(DownloadPiperVoice);

        OpenAiModelList = new ServiceListChoice(() => _openAiModel, id => OpenAiModel = id, OpenAiModelSource,
            n => string.Format(SettingsText.VoiceListFoundOpenAiModels, n), () => SettingsText.VoiceListEmptyOpenAiModels,
            ServiceListChoice.Holds.Models, SettingsText.TypeModelNameInstead, listed => _owner.AddListedRates(ListedRates.OpenAiSpeech, listed));
        OpenAiVoiceList = new ServiceListChoice(() => _openAiVoice, id => OpenAiVoice = id, OpenAiVoiceSource,
            _ => SettingsText.VoiceListOpenAiVoices, () => "",
            ServiceListChoice.Holds.Voices, "");
        ElevenLabsVoiceList = new ServiceListChoice(() => _elevenLabsVoiceId, id => ElevenLabsVoiceId = id,
            () => ElevenLabsSource("voices", SettingsText.VoiceNeedElevenLabsKeyVoices, ElevenLabsTtsService.ListVoicesAsync),
            n => string.Format(SettingsText.VoiceListFoundElevenLabsVoices, n), () => SettingsText.VoiceListEmptyElevenLabsVoices,
            ServiceListChoice.Holds.Voices, SettingsText.TypeVoiceIdInstead,
            // A voice kept from before names were: its name, now the list has it.
            listed => { if (_elevenLabsVoiceName.Length == 0 && VoiceNameFor(_elevenLabsVoiceId) is { Length: > 0 } name) { _elevenLabsVoiceName = name; this.RaisePropertyChanged(nameof(Label)); } });
        ElevenLabsModelList = new ServiceListChoice(() => _elevenLabsModel, id => ElevenLabsModel = id,
            () => ElevenLabsSource("models", SettingsText.VoiceNeedElevenLabsKeyModels, ElevenLabsTtsService.ListModelsAsync),
            n => string.Format(SettingsText.VoiceListFoundElevenLabsModels, n), () => SettingsText.VoiceListEmptyElevenLabsModels,
            ServiceListChoice.Holds.Models, SettingsText.TypeModelIdInstead, listed => _owner.AddListedRates(ListedRates.ElevenLabs, listed));

        _relist.Throttle(TimeSpan.FromMilliseconds(700))
               .ObserveOnUi("voice lists")
               .Subscribe(settled => { if (ReferenceEquals(_owner.SelectedVoice, this)) _ = LoadListsAsync(); });
    }

    public VoiceProfile ToProfile() => new()
    {
        Name              = (_name ?? "").Trim(),
        Provider          = _provider,
        KokoroVoice       = _kokoroVoiceId,
        PiperVoice        = _piperVoiceKey,
        OpenAiVoice       = _openAiVoice,
        OpenAiModel       = _openAiModel,
        OpenAiSpeed       = _openAiSpeed,
        ElevenLabsVoiceId = (_elevenLabsVoiceId ?? "").Trim(),
        ElevenLabsVoiceName = _elevenLabsVoiceName ?? "",
        ElevenLabsModel   = _elevenLabsModel,
        ServerUrl         = (_serverUrl ?? "").Trim(),
        ServerModel       = (_serverModel ?? "").Trim(),
        ServerVoice       = (_serverVoice ?? "").Trim(),
        ServerSpeed       = _serverSpeed,
        ServerApiKey      = (_serverApiKey ?? "").Trim(),
    };

    // ── In the list ─────────────────────────────────────────────────────────────

    private int _position;
    /// <summary>1 for the preferred voice.</summary>
    public int Position
    {
        get => _position;
        set { this.RaiseAndSetIfChanged(ref _position, value); this.RaisePropertyChanged(nameof(Label)); }
    }

    /// <summary>"1. Eden — Kokoro — af_heart", as the list shows it.</summary>
    public string Label => $"{_position}. {SpokenName} — {ToProfile().Describe()}";

    /// <summary>The name this voice goes by: its own, or the agent's when it has none.</summary>
    public string SpokenName => string.IsNullOrWhiteSpace(_name) ? _owner.DefaultName : _name.Trim();

    public void RefreshLabel()
    {
        this.RaisePropertyChanged(nameof(Label));
        this.RaisePropertyChanged(nameof(SpokenName));
        this.RaisePropertyChanged(nameof(NameWatermark));
    }

    // ── Who ───────────────────────────────────────────────────────────────────

    private string _name;
    public string Name
    {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value); RefreshLabel(); }
    }

    public string NameWatermark => string.Format(SettingsText.VoiceNameHint, _owner.DefaultName);

    // ── Engine ────────────────────────────────────────────────────────────────

    /// <summary>An engine as the list shows it.</summary>
    public sealed record EngineOption(TtsProvider Provider, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>The engines offered, the free ones first.</summary>
    public static IReadOnlyList<EngineOption> Engines { get; } =
    [
        new(TtsProvider.Kokoro,      SettingsText.VoiceEngineKokoro),
        new(TtsProvider.LocalServer, SettingsText.VoiceEngineLocalServer),
        new(TtsProvider.Piper,       SettingsText.VoiceEnginePiper),
        new(TtsProvider.OpenAi,      SettingsText.ModelServiceOpenAi),
        new(TtsProvider.ElevenLabs,  SettingsText.VoiceEngineElevenLabs),
    ];

    public IReadOnlyList<EngineOption> EngineOptions => Engines;

    public EngineOption? SelectedEngine
    {
        get => Engines.FirstOrDefault(e => e.Provider == _provider);
        set { if (value is not null && value.Provider != _provider) Provider = value.Provider; }
    }

    private TtsProvider _provider;
    public TtsProvider Provider
    {
        get => _provider;
        set
        {
            this.RaiseAndSetIfChanged(ref _provider, value);
            this.RaisePropertyChanged(nameof(SelectedEngine));
            this.RaisePropertyChanged(nameof(ShowKokoro));
            this.RaisePropertyChanged(nameof(ShowPiper));
            this.RaisePropertyChanged(nameof(ShowOpenAi));
            this.RaisePropertyChanged(nameof(ShowElevenLabs));
            this.RaisePropertyChanged(nameof(ShowLocalServer));
            this.RaisePropertyChanged(nameof(Label));
            _ = LoadListsAsync();               // the new engine's lists
        }
    }

    public bool ShowKokoro      => _provider == TtsProvider.Kokoro;
    public bool ShowPiper       => _provider == TtsProvider.Piper;
    public bool ShowOpenAi      => _provider == TtsProvider.OpenAi;
    public bool ShowElevenLabs  => _provider == TtsProvider.ElevenLabs;
    public bool ShowLocalServer => _provider == TtsProvider.LocalServer;

    public ICommand TestVoiceCommand { get; }

    private string _testStatus = "";
    /// <summary>How the last test went: "Testing…", then the time it took or the reason it could
    /// not speak — the server's own words, e.g. "Voice file 'Taylor' not found.".</summary>
    public string TestStatus
    {
        get => _testStatus;
        private set { this.RaiseAndSetIfChanged(ref _testStatus, value); this.RaisePropertyChanged(nameof(HasTestStatus)); }
    }

    public bool HasTestStatus => _testStatus.Length > 0;

    private bool _testSpoke;
    public bool TestSpoke { get => _testSpoke; private set => this.RaiseAndSetIfChanged(ref _testSpoke, value); }

    private bool _testFailed;
    public bool TestFailed { get => _testFailed; private set => this.RaiseAndSetIfChanged(ref _testFailed, value); }

    private async Task TestAsync()
    {
        TestSpoke  = false;
        TestFailed = false;
        TestStatus = SettingsText.Testing;
        VoiceTestResult result;
        try   { result = await _owner.TestVoiceAsync(this); }
        catch (Exception ex) { result = new VoiceTestResult(false, ex.GetBaseException().Message); }
        TestSpoke  = result.Spoke;
        TestFailed = !result.Spoke;
        TestStatus = result.Message;
    }

    // ── Kokoro ────────────────────────────────────────────────────────────────

    /// <summary>Kokoro's voices, by the id the profile keeps, with the words shown for each —
    /// chosen by the id, never by the words, which are translated.</summary>
    public IReadOnlyList<Choice<string>> KokoroVoiceLabels { get; } =
        [.. KokoroTtsService.Voices.Select(v => new Choice<string>(v.Id, v.Label))];

    private string _kokoroVoiceId;
    public Choice<string>? SelectedKokoroVoiceLabel
    {
        get => KokoroVoiceLabels.FirstOrDefault(v => v.Value == _kokoroVoiceId);
        set
        {
            // A detaching ComboBox sets null; that is not a choice.
            if (value is null || value.Value == _kokoroVoiceId) return;
            _kokoroVoiceId = value.Value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(Label));
        }
    }

    // ── Piper ─────────────────────────────────────────────────────────────────

    /// <summary>Piper's voices, by the key the profile keeps, each shown with the size of its
    /// download — chosen by the key, never by the words, which are translated.</summary>
    public IReadOnlyList<Choice<string>> PiperVoiceLabels { get; } =
        [.. PiperTtsService.VoiceCatalogue.Select(v => new Choice<string>(v.Key, $"{v.Label}  [{v.Size}]"))];

    private string _piperVoiceKey;
    public Choice<string>? SelectedPiperVoiceLabel
    {
        get => PiperVoiceLabels.FirstOrDefault(v => v.Value == _piperVoiceKey);
        set
        {
            // A detaching ComboBox sets null; that is not a choice.
            if (value is null || value.Value == _piperVoiceKey) return;
            _piperVoiceKey = value.Value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsPiperVoiceDownloaded));
            this.RaisePropertyChanged(nameof(Label));
        }
    }

    public bool IsPiperBinaryAvailable => _tts?.Piper.IsBinaryAvailable == true;

    public bool IsPiperVoiceDownloaded =>
        !string.IsNullOrEmpty(_piperVoiceKey) && Directory.Exists(PiperTtsService.GetVoiceModelPath(_piperVoiceKey));

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

    public ICommand DownloadPiperVoiceCommand { get; }

    private void DownloadPiperVoice()
    {
        if (IsDownloadingPiper || _tts is null) return;
        IsDownloadingPiper  = true;
        PiperDownloadStatus = string.Format(SettingsText.VoiceDownloading, _piperVoiceKey);
        var key = _piperVoiceKey;

        _ = Task.Run(async () =>
        {
            try
            {
                _tts.Piper.Configure(key);
                await _tts.Piper.DownloadVoiceAsync(new Progress<string>(msg => PiperDownloadStatus = msg), CancellationToken.None);
                this.RaisePropertyChanged(nameof(IsPiperVoiceDownloaded));
                PiperDownloadStatus = SettingsText.VoiceModelReady;
            }
            catch (Exception ex) { PiperDownloadStatus = string.Format(SettingsText.DownloadFailed, ex.Message); }
            finally { IsDownloadingPiper = false; }
        });
    }

    // ── The services' own lists ───────────────────────────────────────────────
    //
    // ⚠️ Models and voices are asked of the service, never written here (see ModelListing): when
    // the voice is selected on the tab, when its engine changes, and once a key typed has settled.

    /// <summary>A key being typed: the lists are asked for once it settles.</summary>
    private readonly Subject<Unit> _relist = new();

    /// <summary>The owner's key for this voice's engine changed.</summary>
    internal void KeyChanged() => _relist.OnNext(Unit.Default);

    /// <summary>The lists this voice's engine has, asked for unless just asked the same.</summary>
    public Task LoadListsAsync() => _provider switch
    {
        TtsProvider.OpenAi     => Task.WhenAll(OpenAiModelList.LoadAsync(), OpenAiVoiceList.LoadAsync()),
        TtsProvider.ElevenLabs => Task.WhenAll(ElevenLabsVoiceList.LoadAsync(), ElevenLabsModelList.LoadAsync()),
        _                      => Task.CompletedTask,
    };

    private static string Question(string what, string key) => $"{what}|{key.Length}:{key.GetHashCode()}";   // not the key itself

    // ── OpenAI ────────────────────────────────────────────────────────────────

    /// <summary>OpenAI's speech models for the key, newest first.</summary>
    public ServiceListChoice OpenAiModelList { get; }

    /// <summary>The voices OpenAI documents for the chosen model (it publishes no list to ask).</summary>
    public ServiceListChoice OpenAiVoiceList { get; }

    private ServiceListChoice.Source OpenAiModelSource()
    {
        var key = _owner.OpenAiApiKey.Trim();
        return key.Length == 0
            ? ServiceListChoice.Source.Unavailable(SettingsText.VoiceNeedOpenAiKey, "none")
            : new(null, Question("openai-tts", key), ct => OpenAiCompatibleProvider.ListOpenAiSpeechModelsAsync(key, ct));
    }

    private ServiceListChoice.Source OpenAiVoiceSource()
    {
        var model = (_openAiModel ?? "").Trim();
        IReadOnlyList<ModelListing> voices = [.. OpenAiTtsService.VoicesFor(model).Select(v => new ModelListing(v, v))];
        return new(null, "voices|" + model, _ => Task.FromResult(voices));
    }

    private string _openAiVoice;
    public string OpenAiVoice
    {
        get => _openAiVoice;
        set { this.RaiseAndSetIfChanged(ref _openAiVoice, value); this.RaisePropertyChanged(nameof(Label)); OpenAiVoiceList.ValueChanged(); }
    }

    private string _openAiModel;
    public string OpenAiModel
    {
        get => _openAiModel;
        set
        {
            this.RaiseAndSetIfChanged(ref _openAiModel, value);
            OpenAiModelList.ValueChanged();
            _ = OpenAiVoiceList.LoadAsync();    // the voices that model takes
        }
    }

    private double _openAiSpeed;
    public double OpenAiSpeed { get => _openAiSpeed; set => this.RaiseAndSetIfChanged(ref _openAiSpeed, value); }

    // ── ElevenLabs ────────────────────────────────────────────────────────────

    /// <summary>The voices in the ElevenLabs account, by name.</summary>
    public ServiceListChoice ElevenLabsVoiceList { get; }

    /// <summary>ElevenLabs' models that can speak, in its own order.</summary>
    public ServiceListChoice ElevenLabsModelList { get; }

    /// <param name="what">Which list, for telling one question from another: "voices", "models".</param>
    /// <param name="cannot">What the list says while there is no key to ask with.</param>
    private ServiceListChoice.Source ElevenLabsSource(string what, string cannot,
                                                      Func<string, CancellationToken, Task<IReadOnlyList<ModelListing>>> list)
    {
        var key = _owner.ElevenLabsApiKey.Trim();
        return key.Length == 0
            ? ServiceListChoice.Source.Unavailable(cannot, "none")
            : new(null, Question("elevenlabs-" + what, key), ct => list(key, ct));
    }

    private string _elevenLabsVoiceId;
    public string ElevenLabsVoiceId
    {
        get => _elevenLabsVoiceId;
        set
        {
            this.RaiseAndSetIfChanged(ref _elevenLabsVoiceId, value);
            // By name when it comes from the account's list; a voice typed in by its ID is not.
            _elevenLabsVoiceName = VoiceNameFor(value);
            this.RaisePropertyChanged(nameof(Label));
            ElevenLabsVoiceList.ValueChanged();
        }
    }

    private string _elevenLabsVoiceName;

    /// <summary>A voice's name as the account's list gives it — without the " · premade" after
    /// it — or empty when the list does not have it.</summary>
    private string VoiceNameFor(string? id) =>
        ElevenLabsVoiceList.Listings.FirstOrDefault(l => l.Listed && l.Id == (id ?? "").Trim()) is { } listing
            ? listing.Name.Split(" · ")[0]
            : "";

    private string _elevenLabsModel;
    public string ElevenLabsModel
    {
        get => _elevenLabsModel;
        set { this.RaiseAndSetIfChanged(ref _elevenLabsModel, value); ElevenLabsModelList.ValueChanged(); }
    }

    // ── A server of our own ───────────────────────────────────────────────────

    private string _serverUrl;
    /// <summary>Up to and including /v1.</summary>
    public string ServerUrl
    {
        get => _serverUrl;
        set { this.RaiseAndSetIfChanged(ref _serverUrl, value); this.RaisePropertyChanged(nameof(Label)); }
    }

    private string _serverModel;
    public string ServerModel
    {
        get => _serverModel;
        set { this.RaiseAndSetIfChanged(ref _serverModel, value); this.RaisePropertyChanged(nameof(Label)); }
    }

    private string _serverVoice;
    public string ServerVoice
    {
        get => _serverVoice;
        set { this.RaiseAndSetIfChanged(ref _serverVoice, value); this.RaisePropertyChanged(nameof(Label)); }
    }

    private double _serverSpeed;
    public double ServerSpeed { get => _serverSpeed; set => this.RaiseAndSetIfChanged(ref _serverSpeed, value); }

    private string _serverApiKey;
    public string ServerApiKey { get => _serverApiKey; set => this.RaiseAndSetIfChanged(ref _serverApiKey, value); }
}
