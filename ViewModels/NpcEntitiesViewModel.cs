using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// NPC entities from the SDE — agents, corporations and factions.
///
/// Unlike the player side this data is fixed and modest (11,000 agents, 283 corporations,
/// 27 factions), so the lists are complete rather than activity-ranked; only the agent list
/// is large enough to need the row cap in practice.
/// </summary>
public class NpcEntitiesViewModel : ReactiveObject
{
    private readonly EntityBrowserService _service;

    public ObservableCollection<AgentRow>   Agents  { get; } = [];
    public ObservableCollection<NpcCorpRow> Corps   { get; } = [];
    public ObservableCollection<FactionRow> Factions { get; } = [];

    public NpcEntitiesViewModel(EntityBrowserService service)
    {
        _service = service;

        this.WhenAnyValue(x => x.AgentSearch)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(q => _ = LoadAgentsAsync());

        this.WhenAnyValue(x => x.CorpSearch)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(q => _ = LoadCorpsAsync());

        this.WhenAnyValue(x => x.FactionSearch)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(q => _ = LoadFactionsAsync());
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
    }

    // ── Agents ────────────────────────────────────────────────────────────────

    private string _agentSearch = "";
    public string AgentSearch { get => _agentSearch; set => this.RaiseAndSetIfChanged(ref _agentSearch, value); }

    private string _agentStatus = "";
    public string AgentStatus { get => _agentStatus; private set => this.RaiseAndSetIfChanged(ref _agentStatus, value); }

    public async Task LoadAgentsAsync()
    {
        try
        {
            var rows = await _service.AgentsAsync(AgentSearch);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Agents.Clear();
                foreach (var r in rows) Agents.Add(r);
                AgentStatus = PlayerEntitiesViewModel.Describe(
                    rows.Count, AgentSearch, "agent", "highest level first");
            });
        }
        catch (Exception ex) { AgentStatus = $"Error: {ex.Message}"; }
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
            var rows = await _service.NpcCorpsAsync(CorpSearch);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Corps.Clear();
                foreach (var r in rows) Corps.Add(r);
                CorpStatus = PlayerEntitiesViewModel.Describe(
                    rows.Count, CorpSearch, "NPC corporation", "by name");
            });
        }
        catch (Exception ex) { CorpStatus = $"Error: {ex.Message}"; }
    }

    // ── Factions ──────────────────────────────────────────────────────────────

    private string _factionSearch = "";
    public string FactionSearch { get => _factionSearch; set => this.RaiseAndSetIfChanged(ref _factionSearch, value); }

    private string _factionStatus = "";
    public string FactionStatus { get => _factionStatus; private set => this.RaiseAndSetIfChanged(ref _factionStatus, value); }

    public async Task LoadFactionsAsync()
    {
        try
        {
            var rows = await _service.FactionsAsync(FactionSearch);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Factions.Clear();
                foreach (var r in rows) Factions.Add(r);
                FactionStatus = PlayerEntitiesViewModel.Describe(
                    rows.Count, FactionSearch, "faction", "by name");
            });
        }
        catch (Exception ex) { FactionStatus = $"Error: {ex.Message}"; }
    }
}
