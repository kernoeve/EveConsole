using System.Windows.Input;
using EveConsole.Agent;
using EveConsole.Services;
using ReactiveUI;

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
        _elevenLabsModel   = p.ElevenLabsModel;
        _serverUrl         = p.ServerUrl;
        _serverModel       = p.ServerModel;
        _serverVoice       = p.ServerVoice;
        _serverSpeed       = p.ServerSpeed;
        _serverApiKey      = p.ServerApiKey;

        TestVoiceCommand          = ReactiveCommand.Create(() => _owner.TestVoice(this));
        DownloadPiperVoiceCommand = ReactiveCommand.Create(DownloadPiperVoice);
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

    public string NameWatermark => $"{_owner.DefaultName} (the agent's name)";

    // ── Engine ────────────────────────────────────────────────────────────────

    /// <summary>An engine as the list shows it.</summary>
    public sealed record EngineOption(TtsProvider Provider, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>The engines offered, the free ones first.</summary>
    public static IReadOnlyList<EngineOption> Engines { get; } =
    [
        new(TtsProvider.Kokoro,      "Kokoro (on this PC, free)"),
        new(TtsProvider.LocalServer, "Local server — Chatterbox, Orpheus, Kokoro-FastAPI… (free)"),
        new(TtsProvider.Piper,       "Piper (on this PC, free)"),
        new(TtsProvider.OpenAi,      "OpenAI (paid)"),
        new(TtsProvider.ElevenLabs,  "ElevenLabs (paid)"),
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
            _owner.OnVoiceEnginesChanged();
        }
    }

    public bool ShowKokoro      => _provider == TtsProvider.Kokoro;
    public bool ShowPiper       => _provider == TtsProvider.Piper;
    public bool ShowOpenAi      => _provider == TtsProvider.OpenAi;
    public bool ShowElevenLabs  => _provider == TtsProvider.ElevenLabs;
    public bool ShowLocalServer => _provider == TtsProvider.LocalServer;

    public ICommand TestVoiceCommand { get; }

    // ── Kokoro ────────────────────────────────────────────────────────────────

    public IReadOnlyList<string> KokoroVoiceLabels { get; } = [.. KokoroTtsService.Voices.Select(v => v.Label)];

    private string _kokoroVoiceId;
    public string? SelectedKokoroVoiceLabel
    {
        get => KokoroTtsService.Voices.FirstOrDefault(v => v.Id == _kokoroVoiceId).Label;
        set
        {
            var match = KokoroTtsService.Voices.FirstOrDefault(v => v.Label == value);
            if (match.Id is null || match.Id == _kokoroVoiceId) return;
            _kokoroVoiceId = match.Id;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(Label));
        }
    }

    // ── Piper ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<string> PiperVoiceLabels { get; } =
        [.. PiperTtsService.VoiceCatalogue.Select(v => $"{v.Label}  [{v.Size}]")];

    private string _piperVoiceKey;
    public string? SelectedPiperVoiceLabel
    {
        get => PiperTtsService.VoiceCatalogue.Where(v => v.Key == _piperVoiceKey)
                                             .Select(v => $"{v.Label}  [{v.Size}]").FirstOrDefault();
        set
        {
            var match = PiperTtsService.VoiceCatalogue.FirstOrDefault(v => $"{v.Label}  [{v.Size}]" == value);
            if (match.Key is null || match.Key == _piperVoiceKey) return;
            _piperVoiceKey = match.Key;
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
        PiperDownloadStatus = $"Downloading voice '{_piperVoiceKey}'…";
        var key = _piperVoiceKey;

        _ = Task.Run(async () =>
        {
            try
            {
                _tts.Piper.Configure(key);
                await _tts.Piper.DownloadVoiceAsync(new Progress<string>(msg => PiperDownloadStatus = msg), CancellationToken.None);
                this.RaisePropertyChanged(nameof(IsPiperVoiceDownloaded));
                PiperDownloadStatus = "Voice model ready.";
            }
            catch (Exception ex) { PiperDownloadStatus = $"Download failed: {ex.Message}"; }
            finally { IsDownloadingPiper = false; }
        });
    }

    // ── OpenAI ────────────────────────────────────────────────────────────────

    public IReadOnlyList<string> OpenAiVoices => TtsService.OpenAiVoices;
    public IReadOnlyList<string> OpenAiModels => TtsService.OpenAiModels;

    private string _openAiVoice;
    public string OpenAiVoice
    {
        get => _openAiVoice;
        set { this.RaiseAndSetIfChanged(ref _openAiVoice, value); this.RaisePropertyChanged(nameof(Label)); }
    }

    private string _openAiModel;
    public string OpenAiModel { get => _openAiModel; set => this.RaiseAndSetIfChanged(ref _openAiModel, value); }

    private double _openAiSpeed;
    public double OpenAiSpeed { get => _openAiSpeed; set => this.RaiseAndSetIfChanged(ref _openAiSpeed, value); }

    // ── ElevenLabs ────────────────────────────────────────────────────────────

    public IReadOnlyList<string> ElevenLabsModels => TtsService.ElevenLabsModels;

    private string _elevenLabsVoiceId;
    public string ElevenLabsVoiceId
    {
        get => _elevenLabsVoiceId;
        set { this.RaiseAndSetIfChanged(ref _elevenLabsVoiceId, value); this.RaisePropertyChanged(nameof(Label)); }
    }

    private string _elevenLabsModel;
    public string ElevenLabsModel { get => _elevenLabsModel; set => this.RaiseAndSetIfChanged(ref _elevenLabsModel, value); }

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
