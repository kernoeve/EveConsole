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

    public PlayerEntitiesViewModel(EntityBrowserService service)
    {
        Pilots    = new EntityTabViewModel(service, EntityKind.Pilot);
        Corps     = new EntityTabViewModel(service, EntityKind.PlayerCorp);
        Alliances = new EntityTabViewModel(service, EntityKind.Alliance);
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
    }
}
