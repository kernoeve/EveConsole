using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Player entities the app has met, drawn from UniverseNames — the cache filled whenever an
/// id is resolved from a killmail, contract or chat log.
///
/// Three tabs of the same shape, so they are three instances of one view model rather than
/// three copies of one.
/// </summary>
public class PlayerEntitiesViewModel : ReactiveObject
{
    public EntityTabViewModel Pilots    { get; }
    public EntityTabViewModel Corps     { get; }
    public EntityTabViewModel Alliances { get; }

    public PlayerEntitiesViewModel(EntityBrowserService service, KillmailBrowserService killmails)
    {
        Pilots    = new EntityTabViewModel(service, killmails, EntityKind.Pilot);
        Corps     = new EntityTabViewModel(service, killmails, EntityKind.PlayerCorp);
        Alliances = new EntityTabViewModel(service, killmails, EntityKind.Alliance);

        // A link on one tab opens the entity on the tab that owns that kind. NPC kinds are
        // handed to the NPC tool if one is attached, since they have no home here.
        foreach (var tab in new[] { Pilots, Corps, Alliances })
            tab.NavigateTo = Open;
    }

    /// <summary>Set by MainWindowViewModel so NPC links can cross to the other tool.</summary>
    public Action<EntityKind, long>? NavigateToNpc { get; set; }

    public void Open(EntityKind kind, long id)
    {
        switch (kind)
        {
            case EntityKind.Pilot:      SelectedTabIndex = 0; _ = Pilots.LoadAsync(id);    break;
            case EntityKind.PlayerCorp: SelectedTabIndex = 1; _ = Corps.LoadAsync(id);     break;
            case EntityKind.Alliance:   SelectedTabIndex = 2; _ = Alliances.LoadAsync(id); break;
            default:                    NavigateToNpc?.Invoke(kind, id);                   break;
        }
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
    }
}
