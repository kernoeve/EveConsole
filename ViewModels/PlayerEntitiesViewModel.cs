using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>Which Killmail Browser filter a double-click should populate. Alliances are
/// absent deliberately: that tool has no alliance filter to set.</summary>
public enum KillmailFilterKind { Character, Corporation }

/// <summary>
/// Player entities the app has met — pilots, corporations and alliances, drawn from the
/// name cache and coloured by what killmails say about them.
///
/// Each tab searches independently and holds its own text, because moving between them
/// while chasing one name is the normal way this gets used and losing the search each time
/// would be tedious.
/// </summary>
public class PlayerEntitiesViewModel : ReactiveObject
{
    private readonly EntityBrowserService _service;

    public ObservableCollection<PilotRow>      Pilots    { get; } = [];
    public ObservableCollection<PlayerCorpRow> Corps     { get; } = [];
    public ObservableCollection<AllianceRow>   Alliances { get; } = [];

    public PlayerEntitiesViewModel(EntityBrowserService service)
    {
        _service = service;

        // One debounce per tab. Typing a name fires a query per keystroke otherwise, and
        // these carry per-row subqueries over the killmail tables.
        this.WhenAnyValue(x => x.PilotSearch)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(q => _ = LoadPilotsAsync());

        this.WhenAnyValue(x => x.CorpSearch)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(q => _ = LoadCorpsAsync());

        this.WhenAnyValue(x => x.AllianceSearch)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(q => _ = LoadAlliancesAsync());
    }

    /// <summary>
    /// Set by MainWindowViewModel — opens the Killmail Browser with one of its filters set.
    /// The killmail filters match on name rather than id, so that is what is handed over.
    /// </summary>
    public Action<KillmailFilterKind, string>? NavigateToKillmailsAction { get; set; }

    public void ShowKillmailsFor(KillmailFilterKind kind, string name)
    {
        if (!string.IsNullOrWhiteSpace(name)) NavigateToKillmailsAction?.Invoke(kind, name);
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
    }

    // ── Pilots ────────────────────────────────────────────────────────────────

    private string _pilotSearch = "";
    public string PilotSearch { get => _pilotSearch; set => this.RaiseAndSetIfChanged(ref _pilotSearch, value); }

    private string _pilotStatus = "";
    public string PilotStatus { get => _pilotStatus; private set => this.RaiseAndSetIfChanged(ref _pilotStatus, value); }

    public async Task LoadPilotsAsync()
    {
        try
        {
            var rows = await _service.PilotsAsync(PilotSearch);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Pilots.Clear();
                foreach (var r in rows) Pilots.Add(r);
                PilotStatus = Describe(rows.Count, PilotSearch, "pilot", "most active by killmail appearances");
            });
        }
        catch (Exception ex) { PilotStatus = $"Error: {ex.Message}"; }
    }

    // ── Corporations ──────────────────────────────────────────────────────────

    private string _corpSearch = "";
    public string CorpSearch { get => _corpSearch; set => this.RaiseAndSetIfChanged(ref _corpSearch, value); }

    private string _corpStatus = "";
    public string CorpStatus { get => _corpStatus; private set => this.RaiseAndSetIfChanged(ref _corpStatus, value); }

    public async Task LoadCorpsAsync()
    {
        try
        {
            var rows = await _service.PlayerCorpsAsync(CorpSearch);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Corps.Clear();
                foreach (var r in rows) Corps.Add(r);
                CorpStatus = Describe(rows.Count, CorpSearch, "corporation", "most active by killmail appearances");
            });
        }
        catch (Exception ex) { CorpStatus = $"Error: {ex.Message}"; }
    }

    // ── Alliances ─────────────────────────────────────────────────────────────

    private string _allianceSearch = "";
    public string AllianceSearch { get => _allianceSearch; set => this.RaiseAndSetIfChanged(ref _allianceSearch, value); }

    private string _allianceStatus = "";
    public string AllianceStatus { get => _allianceStatus; private set => this.RaiseAndSetIfChanged(ref _allianceStatus, value); }

    public async Task LoadAlliancesAsync()
    {
        try
        {
            var rows = await _service.AlliancesAsync(AllianceSearch);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Alliances.Clear();
                foreach (var r in rows) Alliances.Add(r);
                AllianceStatus = Describe(rows.Count, AllianceSearch, "alliance", "most active by killmail appearances");
            });
        }
        catch (Exception ex) { AllianceStatus = $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Says what the list is showing, and says so when it is capped — a grid that silently
    /// stops at 300 rows reads as "that is all there is".
    /// </summary>
    internal static string Describe(int count, string search, string noun, string defaultOrder)
    {
        var capped = count >= EntityBrowserService.MaxRows
            ? $" (capped at {EntityBrowserService.MaxRows} — narrow the search)"
            : "";

        return search.Trim().Length >= 2
            ? $"{count:N0} {noun}(s) matching “{search.Trim()}”{capped}"
            : $"{count:N0} {noun}(s), {defaultOrder}{capped}. Type at least two characters to search by name.";
    }
}
