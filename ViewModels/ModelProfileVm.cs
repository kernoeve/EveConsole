using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows.Input;
using EveConsole.Agent;
using EveConsole.Agent.Providers;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// An entry in a role's list: a model, or "None" / "Same as conversation". The models themselves
/// are the entries, so a rename shows in every list as it is typed.
/// </summary>
public abstract class ModelChoice : ReactiveObject
{
    /// <summary>The model's id; empty for the fixed entries.</summary>
    public abstract string Id    { get; }
    public abstract string Label { get; }
}

/// <summary>"None" or "Same as conversation".</summary>
public sealed class FixedModelChoice(string label) : ModelChoice
{
    public override string Id    => "";
    public override string Label => label;
}

/// <summary>
/// One model on the settings tab: what it is called, which service runs it, its model name and —
/// for a server of the capsuleer's own — its address. The keys are per service, kept on the tab
/// and shown with whichever model uses that service.
/// </summary>
public sealed class ModelProfileVm : ModelChoice
{
    private readonly AgentSettingsViewModel _owner;

    /// <summary>The settings tab, for what belongs to a service rather than a model: its key.</summary>
    public AgentSettingsViewModel Owner => _owner;

    /// <summary>What the roles refer to it by; kept through every edit.</summary>
    public override string Id { get; }

    public ModelProfileVm(AgentSettingsViewModel owner, ModelProfile p)
    {
        _owner    = owner;
        Id        = p.Id;
        _name     = p.Name;
        _provider = p.Provider;
        _model    = p.Model;
        _endpoint = p.Endpoint;
        _think    = p.Think;
        TestModelCommand   = ReactiveCommand.CreateFromTask(TestAsync);
        RefreshListCommand = ReactiveCommand.CreateFromTask(() => LoadListAsync(force: true));
        _relist.Throttle(TimeSpan.FromMilliseconds(700))
               .ObserveOnUi("model list")
               .Subscribe(settled => { if (ReferenceEquals(_owner.SelectedModel, this)) _ = LoadListAsync(); });
    }

    public ModelProfile ToProfile() => new()
    {
        Id       = Id,
        Name     = (_name ?? "").Trim(),
        Provider = _provider,
        Model    = (_model ?? "").Trim(),
        Endpoint = (_endpoint ?? "").Trim(),
        Think    = _think,
    };

    /// <summary>What the lists show: its name or what it is, and whether it costs money to use.</summary>
    public override string Label => $"{ToProfile().Label}  ({(_provider == AgentProviderType.Local ? "free" : "paid")})";

    /// <summary>Something the lists show was edited. (Not "Changed": that is ReactiveObject's own
    /// observable, and a method of that name hid it.)</summary>
    private void Edited()
    {
        this.RaisePropertyChanged(nameof(Label));
        _owner.OnModelsChanged();
    }

    // ── Who ───────────────────────────────────────────────────────────────────

    private string _name;
    public string Name
    {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value); Edited(); }
    }

    public string NameWatermark => ToProfile() is var p ? p.Describe() : "";

    // ── Service ───────────────────────────────────────────────────────────────

    /// <summary>A service as the list shows it.</summary>
    public sealed record ServiceOption(AgentProviderType Provider, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>The free one first.</summary>
    public static IReadOnlyList<ServiceOption> Services { get; } =
    [
        new(AgentProviderType.Local,  "Local server — Ollama, LM Studio… (free)"),
        new(AgentProviderType.Claude, "Claude — Anthropic (paid)"),
        new(AgentProviderType.OpenAI, "OpenAI (paid)"),
    ];

    public IReadOnlyList<ServiceOption> ServiceOptions => Services;

    public ServiceOption? SelectedService
    {
        get => Services.FirstOrDefault(s => s.Provider == _provider);
        set { if (value is not null && value.Provider != _provider) Provider = value.Provider; }
    }

    private AgentProviderType _provider;
    public AgentProviderType Provider
    {
        get => _provider;
        set
        {
            this.RaiseAndSetIfChanged(ref _provider, value);
            this.RaisePropertyChanged(nameof(SelectedService));
            this.RaisePropertyChanged(nameof(ShowEndpoint));
            this.RaisePropertyChanged(nameof(ShowClaude));
            this.RaisePropertyChanged(nameof(ShowOpenAi));
            this.RaisePropertyChanged(nameof(ModelWatermark));
            this.RaisePropertyChanged(nameof(ModelHelp));
            this.RaisePropertyChanged(nameof(NameWatermark));
            Edited();
            // A model's name belongs to its service: another service, another list to choose from,
            // and its first offered once it arrives.
            Model = "";
            _ = LoadListAsync();
        }
    }

    public bool ShowEndpoint => _provider == AgentProviderType.Local;
    public bool ShowClaude   => _provider == AgentProviderType.Claude;
    public bool ShowOpenAi   => _provider == AgentProviderType.OpenAI;

    // ── Model ─────────────────────────────────────────────────────────────────

    private string _model;
    public string Model
    {
        get => _model;
        set
        {
            this.RaiseAndSetIfChanged(ref _model, value);
            this.RaisePropertyChanged(nameof(NameWatermark));
            this.RaisePropertyChanged(nameof(SelectedListing));
            Edited();
        }
    }

    /// <summary>For typing a name, when the service's list cannot be had.</summary>
    public string ModelWatermark => _provider == AgentProviderType.Local ? "as the server names it" : "as the service names it";

    public string ModelHelp => _provider == AgentProviderType.Local
        ? "The server must offer OpenAI's /v1/chat/completions, as Ollama, LM Studio and most local runners do."
        : "";

    private string _endpoint;
    /// <summary>The server's root: http://gpu-box:11434 for Ollama.</summary>
    public string Endpoint
    {
        get => _endpoint;
        set
        {
            this.RaiseAndSetIfChanged(ref _endpoint, value);
            this.RaisePropertyChanged(nameof(NameWatermark));
            Edited();
            _relist.OnNext(Unit.Default);
        }
    }

    // ── The service's own list of models ──────────────────────────────────────
    //
    // ⚠️ Asked of the service, never written here: a list in the source is out of date by the
    // time it ships (see ModelListing). Loaded when the model is selected on the tab, and again
    // once its key or address has stopped changing.

    private IReadOnlyList<ModelListing> _listings = [];
    /// <summary>
    /// What the service offers, newest first where it says — with the model chosen before at the
    /// top, marked, when it is no longer among them. Empty until the list has loaded, and when it
    /// cannot be: then the model's name is typed instead.
    /// </summary>
    public IReadOnlyList<ModelListing> Listings
    {
        get => _listings;
        private set
        {
            this.RaiseAndSetIfChanged(ref _listings, value);
            this.RaisePropertyChanged(nameof(ShowListings));
            this.RaisePropertyChanged(nameof(SelectedListing));
        }
    }

    public bool ShowListings => _listings.Count > 0;

    public ModelListing? SelectedListing
    {
        get => _listings.FirstOrDefault(l => l.Id == (_model ?? "").Trim());
        set { if (value is not null && value.Id != (_model ?? "").Trim()) Model = value.Id; }
    }

    private string _listStatus = "";
    /// <summary>How many there are, or why there is no list: no key yet, the service's refusal.</summary>
    public string ListStatus
    {
        get => _listStatus;
        private set { this.RaiseAndSetIfChanged(ref _listStatus, value); this.RaisePropertyChanged(nameof(HasListStatus)); }
    }

    public bool HasListStatus => _listStatus.Length > 0;

    public ICommand RefreshListCommand { get; }

    /// <summary>What the list was last asked for — service, address, key — so a model selected
    /// again is not asked twice.</summary>
    private string _listedFor = "";
    private CancellationTokenSource? _listing;

    /// <summary>A key or an address being typed: the list is asked for once it settles.</summary>
    private readonly Subject<Unit> _relist = new();

    /// <summary>The owner's key for this model's service changed.</summary>
    internal void KeyChanged() => _relist.OnNext(Unit.Default);

    /// <summary>Asks the service for its models, unless it was just asked the same.</summary>
    public async Task LoadListAsync(bool force = false)
    {
        var key = _provider switch
        {
            AgentProviderType.Claude => _owner.ClaudeApiKey.Trim(),
            AgentProviderType.OpenAI => _owner.OpenAiApiKey.Trim(),
            _                        => "",
        };
        var endpoint = (_endpoint ?? "").Trim();
        var forWhat  = $"{_provider}|{endpoint}|{key.Length}:{key.GetHashCode()}";   // not the key itself
        if (!force && forWhat == _listedFor) return;

        _listing?.Cancel();
        var cts = _listing = new CancellationTokenSource();

        var cannot = _provider switch
        {
            AgentProviderType.Claude when key.Length == 0     => "Enter the Claude key above to choose from Anthropic's models.",
            AgentProviderType.OpenAI when key.Length == 0     => "Enter the OpenAI key above to choose from OpenAI's models.",
            AgentProviderType.Local  when endpoint.Length == 0 => "Enter the server's address to choose from its models.",
            _ => null,
        };
        if (cannot is not null) { _listedFor = forWhat; Listings = []; ListStatus = cannot; return; }

        ListStatus = "Asking for the list of models…";
        try
        {
            var found = _provider switch
            {
                AgentProviderType.Claude => await ClaudeProvider.ListModelsAsync(key, cts.Token),
                AgentProviderType.OpenAI => await OpenAiCompatibleProvider.ListOpenAiModelsAsync(key, cts.Token),
                _                        => await OpenAiCompatibleProvider.ListLocalModelsAsync(endpoint, cts.Token),
            };
            if (cts.IsCancellationRequested) return;
            _listedFor = forWhat;

            // A model chosen before and no longer offered stays, marked, rather than swapped for
            // another behind the capsuleer's back. None chosen yet: the first offered, which on
            // Anthropic's and OpenAI's lists is the newest.
            var chosen = (_model ?? "").Trim();
            Listings = found.Count > 0 && chosen.Length > 0 && found.All(l => l.Id != chosen)
                ? [new ModelListing(chosen, chosen, Listed: false), .. found]
                : found;
            if (chosen.Length == 0 && found.Count > 0) Model = found[0].Id;

            ListStatus = (_provider, found.Count) switch
            {
                (AgentProviderType.Local, 0) => "The server lists no models: pull one first, as \"ollama pull\" does.",
                (_, 0)                       => "The service lists no models for this key.",
                (AgentProviderType.Claude, var n) => $"{n} models your key can use, newest first.",
                (AgentProviderType.OpenAI, var n) => $"{n} models for conversation, newest first.",
                (_, var n)                   => $"{n} models on the server.",
            };
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            _listedFor = "";                   // asked again next time
            Listings   = [];
            var why    = ex is OperationCanceledException ? "no answer within 15 seconds." : ex.GetBaseException().Message;
            ListStatus = $"Could not get the list of models — {why} Type the model's name instead.";
        }
        catch (OperationCanceledException) { /* superseded by a newer request */ }
    }

    private bool _think;
    /// <summary>A local reasoning model may think before it answers — slower, never shown.</summary>
    public bool Think
    {
        get => _think;
        set => this.RaiseAndSetIfChanged(ref _think, value);
    }

    // ── Trying it ─────────────────────────────────────────────────────────────

    public ICommand TestModelCommand { get; }

    private string _testStatus = "";
    /// <summary>"Testing…", then how long it took to answer and what it said — or why it could not.</summary>
    public string TestStatus
    {
        get => _testStatus;
        private set { this.RaiseAndSetIfChanged(ref _testStatus, value); this.RaisePropertyChanged(nameof(HasTestStatus)); }
    }

    public bool HasTestStatus => _testStatus.Length > 0;

    private bool _testOk;
    public bool TestOk { get => _testOk; private set => this.RaiseAndSetIfChanged(ref _testOk, value); }

    private bool _testFailed;
    public bool TestFailed { get => _testFailed; private set => this.RaiseAndSetIfChanged(ref _testFailed, value); }

    private async Task TestAsync()
    {
        TestOk     = false;
        TestFailed = false;
        TestStatus = "Testing…";
        (bool Ok, string Message) result;
        try   { result = await _owner.TestModelAsync(this); }
        catch (Exception ex) { result = (false, ex.GetBaseException().Message); }
        TestOk     = result.Ok;
        TestFailed = !result.Ok;
        TestStatus = result.Message;
    }
}
