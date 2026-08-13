namespace EveConsole.Services;

/// <summary>
/// One place to ask "open this thing", wherever you are.
///
/// A killmail row appears in the Killmail Browser, in Corp Activity, on the map's system
/// page and in the entity viewers. Its links have to work in all four, and threading a
/// navigation callback down through every host that renders one is how the shared row
/// template ends up with four slightly different copies. Instead the row asks this, and the
/// main window wires it once at startup.
///
/// Every callback is optional: a host that has not wired one simply produces a link that
/// does nothing rather than a crash, which matters for design-time and for the settings
/// windows that render rows outside the main shell.
/// </summary>
public class EntityNavigator
{
    public Action<EntityKind, long>? OpenEntity   { get; set; }
    public Action<int>?              OpenSystem   { get; set; }
    public Action<int>?              OpenItem     { get; set; }
    public Action<int>?              OpenKillmail { get; set; }

    public void Entity(EntityKind kind, long id) { if (id > 0) OpenEntity?.Invoke(kind, id); }
    public void System(int systemId)             { if (systemId > 0) OpenSystem?.Invoke(systemId); }
    public void Item(int typeId)                 { if (typeId > 0) OpenItem?.Invoke(typeId); }
    public void Killmail(int killMailId)         { if (killMailId > 0) OpenKillmail?.Invoke(killMailId); }

    /// <summary>
    /// Shared instance. A static rather than an injected dependency on purpose: the row view
    /// models are constructed from plain records in half a dozen places, several of them
    /// deep inside LINQ projections, and threading a service into all of them to reach one
    /// window would be a lot of plumbing for no extra safety.
    /// </summary>
    public static EntityNavigator Instance { get; } = new();
}
