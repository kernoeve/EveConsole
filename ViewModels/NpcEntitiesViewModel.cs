using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>NPC entities from the SDE: agents, corporations and factions.</summary>
public class NpcEntitiesViewModel : ReactiveObject
{
    public EntityTabViewModel Agents   { get; }
    public EntityTabViewModel Corps    { get; }
    public EntityTabViewModel Factions { get; }

    public NpcEntitiesViewModel(EntityBrowserService service, KillmailBrowserService killmails)
    {
        Agents   = new EntityTabViewModel(service, killmails, EntityKind.Agent);
        Corps    = new EntityTabViewModel(service, killmails, EntityKind.NpcCorp);
        Factions = new EntityTabViewModel(service, killmails, EntityKind.Faction);

        foreach (var tab in new[] { Agents, Corps, Factions })
            tab.NavigateTo = Open;
    }

    public void Open(EntityKind kind, long id)
    {
        switch (kind)
        {
            case EntityKind.Agent:   SelectedTabIndex = 0; _ = Agents.LoadAsync(id);   break;
            case EntityKind.NpcCorp: SelectedTabIndex = 1; _ = Corps.LoadAsync(id);    break;
            case EntityKind.Faction: SelectedTabIndex = 2; _ = Factions.LoadAsync(id); break;
        }
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
    }
}
