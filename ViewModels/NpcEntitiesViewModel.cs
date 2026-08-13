using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>NPC entities from the SDE: agents, stations, corporations and factions.</summary>
public class NpcEntitiesViewModel : ReactiveObject
{
    public EntityTabViewModel Agents   { get; }
    public EntityTabViewModel Stations { get; }
    public EntityTabViewModel Corps    { get; }
    public EntityTabViewModel Factions { get; }

    public NpcEntitiesViewModel(EntityBrowserService service, KillmailBrowserService killmails)
    {
        Agents   = new EntityTabViewModel(service, killmails, EntityKind.Agent);
        Stations = new EntityTabViewModel(service, killmails, EntityKind.Station);
        Corps    = new EntityTabViewModel(service, killmails, EntityKind.NpcCorp);
        Factions = new EntityTabViewModel(service, killmails, EntityKind.Faction);

        foreach (var tab in new[] { Agents, Stations, Corps, Factions })
        {
            tab.NavigateTo = Open;
            tab.NavigateToItemAction   = id => NavigateToItem?.Invoke(id);
            tab.NavigateToSystemAction = id => NavigateToSystem?.Invoke(id);
        }
    }

    /// <summary>Set by MainWindowViewModel — opens the Item Browser.</summary>
    public Action<int>? NavigateToItem { get; set; }

    /// <summary>Set by MainWindowViewModel — opens the Universe map on a system.</summary>
    public Action<int>? NavigateToSystem { get; set; }

    public void Open(EntityKind kind, long id)
    {
        switch (kind)
        {
            case EntityKind.Agent:   SelectedTabIndex = 0; _ = Agents.LoadAsync(id);   break;
            case EntityKind.Station: SelectedTabIndex = 1; _ = Stations.LoadAsync(id); break;
            case EntityKind.NpcCorp: SelectedTabIndex = 2; _ = Corps.LoadAsync(id);    break;
            case EntityKind.Faction: SelectedTabIndex = 3; _ = Factions.LoadAsync(id); break;
        }
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
    }
}
