using System.Windows.Input;
using EveConsole.Agent;
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
/// for a server of the capsuleer's own — its address. The keys are per service, on the tab.
/// </summary>
public sealed class ModelProfileVm : ModelChoice
{
    private readonly AgentSettingsViewModel _owner;

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
        TestModelCommand = ReactiveCommand.CreateFromTask(TestAsync);
    }

    public ModelProfile ToProfile() => new()
    {
        Id       = Id,
        Name     = (_name ?? "").Trim(),
        Provider = _provider,
        Model    = (_model ?? "").Trim(),
        Endpoint = (_endpoint ?? "").Trim(),
    };

    /// <summary>What the lists show: its name or what it is, and whether it costs money to use.</summary>
    public override string Label => $"{ToProfile().Label}  ({(_provider == AgentProviderType.Local ? "free" : "paid")})";

    private void Changed()
    {
        this.RaisePropertyChanged(nameof(Label));
        _owner.OnModelsChanged();
    }

    // ── Who ───────────────────────────────────────────────────────────────────

    private string _name;
    public string Name
    {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value); Changed(); }
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
            this.RaisePropertyChanged(nameof(ModelWatermark));
            this.RaisePropertyChanged(nameof(ModelHelp));
            this.RaisePropertyChanged(nameof(NameWatermark));
            Changed();
        }
    }

    public bool ShowEndpoint => _provider == AgentProviderType.Local;

    // ── Model ─────────────────────────────────────────────────────────────────

    private string _model;
    public string Model
    {
        get => _model;
        set { this.RaiseAndSetIfChanged(ref _model, value); this.RaisePropertyChanged(nameof(NameWatermark)); Changed(); }
    }

    public string ModelWatermark => _provider switch
    {
        AgentProviderType.Local => "qwen3:8b",
        _                       => ModelProfile.DefaultModel(_provider),
    };

    public string ModelHelp => _provider switch
    {
        AgentProviderType.Claude =>
            "Available: claude-opus-4-8, claude-sonnet-4-6, claude-haiku-4-5-20251001. Uses the Claude key below.",
        AgentProviderType.OpenAI =>
            "Uses the OpenAI key below.",
        _ =>
            "As the server lists it — for Ollama, what \"ollama list\" shows. The server must offer OpenAI's " +
            "/v1/chat/completions, as Ollama, LM Studio and most local runners do.",
    };

    private string _endpoint;
    /// <summary>The server's root: http://gpu-box:11434 for Ollama.</summary>
    public string Endpoint
    {
        get => _endpoint;
        set { this.RaiseAndSetIfChanged(ref _endpoint, value); this.RaisePropertyChanged(nameof(NameWatermark)); Changed(); }
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
