namespace EveConsole.Services.Pi;

/// <summary>
/// One colony as the PI engine sees it: the stored ESI snapshot, detached from the database so
/// the engine can be run on constructed data. Build it from the tables with
/// <see cref="PiLayoutStore.LoadAsync"/>.
/// </summary>
public sealed class PiColonyLayout
{
    public long   CharacterId   { get; init; }
    public int    PlanetId      { get; init; }
    /// <summary>ESI's <c>planet_type</c>: "barren", "gas", …</summary>
    public string PlanetType    { get; init; } = "";
    public int    SolarSystemId { get; init; }
    /// <summary>The command center's upgrade level, 0–5.</summary>
    public int    UpgradeLevel  { get; init; }

    /// <summary>The moment the snapshot describes. ⚠️ Moves only when the owner views the colony
    /// in the client; everything after it is simulated.</summary>
    public DateTimeOffset LastUpdate { get; init; }

    /// <summary>When the app read it from ESI; null for a constructed layout.</summary>
    public DateTimeOffset? FetchedAt { get; init; }

    public IReadOnlyList<PiLayoutPin>   Pins   { get; init; } = [];
    public IReadOnlyList<PiLayoutRoute> Routes { get; init; } = [];
    public IReadOnlyList<PiLayoutLink>  Links  { get; init; } = [];
}

public sealed record PiLayoutPin
{
    public long PinId  { get; init; }
    public int  TypeId { get; init; }
    public int? SchematicId { get; init; }
    public DateTimeOffset? InstallTime    { get; init; }
    public DateTimeOffset? ExpiryTime     { get; init; }
    public DateTimeOffset? LastCycleStart { get; init; }

    /// <summary>What the pin held at the snapshot, by type.</summary>
    public IReadOnlyDictionary<int, long> Contents { get; init; } = new Dictionary<int, long>();

    /// <summary>Set for an extractor control unit only.</summary>
    public PiLayoutExtractor? Extractor { get; init; }
}

/// <param name="CycleSeconds">⚠️ Seconds, as ESI gives it.</param>
/// <param name="QtyPerCycle">The program's base output; null when ESI did not give one.</param>
public sealed record PiLayoutExtractor(int? ProductTypeId, int? CycleSeconds, int? QtyPerCycle, int HeadCount);

public sealed record PiLayoutRoute(long RouteId, long SourcePinId, long DestinationPinId, int ContentTypeId, double Quantity);

public sealed record PiLayoutLink(long SourcePinId, long DestinationPinId, int LinkLevel);
