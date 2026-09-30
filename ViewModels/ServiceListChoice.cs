using System.Windows.Input;
using EveConsole.Agent;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

/// <summary>
/// One setting chosen from a service's own list — a model, a voice — rather than from names
/// written into the app, which are out of date by the time it ships (see ModelListing).
///
/// <para>The owner says when to ask: when its row is selected, or once a key or address it typed
/// has settled. The same question is not asked twice. A value chosen before and no longer listed
/// stays chosen, marked, never swapped behind the capsuleer's back; with none chosen, the first
/// listed is chosen. With no list — no key yet, no answer — the reason shows, and the value is
/// typed instead.</para>
/// </summary>
public sealed class ServiceListChoice : ReactiveObject
{
    /// <summary>What to ask the service, or why it cannot be asked.</summary>
    /// <param name="Cannot">Why there is no list to ask for: no key yet, no address.</param>
    /// <param name="Question">The same text for the same request, so it is not asked twice.
    /// ⚠️ Never the key itself: a hash of it will do.</param>
    /// <param name="Fetch">The request.</param>
    public sealed record Source(string? Cannot, string Question, Func<CancellationToken, Task<IReadOnlyList<ModelListing>>>? Fetch)
    {
        public static Source Unavailable(string why, string question = "") => new(why, question, null);
    }

    private readonly Func<string>      _value;
    private readonly Action<string>    _choose;
    private readonly Func<Source>      _source;
    private readonly Func<int, string> _found;
    private readonly Func<string>      _none;
    private readonly Holds             _holds;
    private readonly string            _typeInstead;
    private readonly Action<IReadOnlyList<ModelListing>>? _listed;

    /// <summary>What a list holds, which picks the wording of its status lines — whole sentences,
    /// not a noun put into one.</summary>
    public enum Holds { Models, Voices }

    /// <param name="value">The value as the owner holds it.</param>
    /// <param name="choose">Sets it.</param>
    /// <param name="source">What to ask, as things stand now.</param>
    /// <param name="found">What a list of that many says: "3 models your key can use, newest first."</param>
    /// <param name="none">What an empty list says.</param>
    /// <param name="holds">What the list holds, for the status lines: models or voices.</param>
    /// <param name="typeInstead">What to do without a list: "Type the model's name instead."</param>
    /// <param name="listed">Told what the service listed, each time it answers: for the rate rows.</param>
    public ServiceListChoice(Func<string> value, Action<string> choose, Func<Source> source,
                             Func<int, string> found, Func<string> none, Holds holds, string typeInstead,
                             Action<IReadOnlyList<ModelListing>>? listed = null)
    {
        _listed      = listed;
        _value       = value;
        _choose      = choose;
        _source      = source;
        _found       = found;
        _none        = none;
        _holds       = holds;
        _typeInstead = typeInstead;
        RefreshCommand = ReactiveCommand.CreateFromTask(() => LoadAsync(force: true));
    }

    private IReadOnlyList<ModelListing> _listings = [];
    /// <summary>
    /// What the service offers, in its own order — with the value chosen before at the top,
    /// marked, when it is no longer among them. Empty until it has loaded, and when it cannot.
    /// </summary>
    public IReadOnlyList<ModelListing> Listings
    {
        get => _listings;
        private set
        {
            this.RaiseAndSetIfChanged(ref _listings, value);
            this.RaisePropertyChanged(nameof(IsListed));
            this.RaisePropertyChanged(nameof(Selected));
        }
    }

    /// <summary>A list to choose from; without one, the value is typed.</summary>
    public bool IsListed => _listings.Count > 0;

    public ModelListing? Selected
    {
        get => _listings.FirstOrDefault(l => l.Id == Current);
        set { if (value is not null && value.Id != Current) _choose(value.Id); }
    }

    private string Current => (_value() ?? "").Trim();

    /// <summary>The owner's value changed: the selection follows it.</summary>
    public void ValueChanged() => this.RaisePropertyChanged(nameof(Selected));

    private string _status = "";
    /// <summary>How many there are, or why there is no list: no key yet, the service's refusal.</summary>
    public string Status
    {
        get => _status;
        private set { this.RaiseAndSetIfChanged(ref _status, value); this.RaisePropertyChanged(nameof(HasStatus)); }
    }

    public bool HasStatus => _status.Length > 0;

    public ICommand RefreshCommand { get; }

    private string _asked = "";
    private CancellationTokenSource? _loading;

    /// <summary>Asks the service, unless it was just asked the same.</summary>
    public async Task LoadAsync(bool force = false)
    {
        var source = _source();
        if (!force && source.Question.Length > 0 && source.Question == _asked) return;

        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();

        if (source.Cannot is not null || source.Fetch is null)
        {
            _asked   = source.Question;
            Listings = [];
            Status   = source.Cannot ?? "";
            return;
        }

        Status = _holds == Holds.Voices ? SettingsText.ListAskingVoices
                                        : SettingsText.ListAskingModels;
        try
        {
            var found = await source.Fetch(cts.Token);
            if (cts.IsCancellationRequested) return;
            _asked = source.Question;

            var chosen = Current;
            Listings = found.Count > 0 && chosen.Length > 0 && found.All(l => l.Id != chosen)
                ? [new ModelListing(chosen, chosen, Listed: false), .. found]
                : found;
            if (chosen.Length == 0 && found.Count > 0) _choose(found[0].Id);
            Status = found.Count == 0 ? _none() : _found(found.Count);
            if (found.Count > 0) _listed?.Invoke(found);
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            _asked   = "";                          // asked again next time
            Listings = [];
            var why  = ex is OperationCanceledException ? SettingsText.ListNoAnswer : ex.GetBaseException().Message;
            Status   = _holds == Holds.Voices
                ? string.Format(SettingsText.ListFailedVoices, why, _typeInstead)
                : string.Format(SettingsText.ListFailedModels, why, _typeInstead);
        }
        catch (OperationCanceledException) { /* superseded by a newer request */ }
    }
}
