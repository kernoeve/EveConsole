using EveConsole.Localization;

namespace EveConsole.Services.Worklist;

/// <summary>
/// What kind of trip a haul is, by how far apart its two ends are.
///
/// <para>⚠️ Three different jobs, not one job at three lengths. Shuffling between structures in
/// one system is a loop flown as jobs finish; a few gates over is a run in whatever the pilot has;
/// the far end of the map is usually a hauling service or a jump freighter. Each is set up on its
/// own — method, ship, pilots — and grouped on its own, because a list that mixes a 3 m³ hop next
/// door with a 20 million m³ import answers none of the three.</para>
/// </summary>
/// <remarks>The values order the worklist's haul groups: in system first.</remarks>
public enum HaulLane
{
    /// <summary>Not a haul.</summary>
    None         = 0,
    /// <summary>Both ends in the same solar system.</summary>
    InSystem     = 1,
    /// <summary>Within <see cref="WorklistSettings.HaulNearJumps"/> gates.</summary>
    Nearby       = 2,
    /// <summary>Further than that, or with no gate route known.</summary>
    LongDistance = 3,
}

/// <summary>How a lane's trips are made.</summary>
/// <remarks>⚠️ Stored by name, so the numbers may change; the names may not.</remarks>
public enum HaulMethod
{
    /// <summary>A pilot flies it — the only way inside one system.</summary>
    Fly,
    /// <summary>A courier contract for a player hauling service.</summary>
    Service,
    /// <summary>A freighter through jump bridges.</summary>
    BridgedFreighter,
    /// <summary>A jump freighter.</summary>
    JumpFreighter,
    /// <summary>Through the gates.</summary>
    Gating,
}

/// <summary>
/// How one lane is set up.
/// </summary>
/// <param name="ShipTypeId">The hull to plan in, or 0 for whatever the pilot is flying when the
/// list is built.</param>
/// <param name="CargoM3">The hold to plan with, or 0 to take the hull's own. A hull's SDE capacity
/// is its unfitted cargo hold before skills — a freighter with expanders carries far more, and a
/// transport's fleet hangar is not in it at all — so this is how a real fit is told.</param>
/// <param name="CharacterIds">Who flies it, in order: the first is the one used when only one is
/// needed.</param>
public sealed record HaulLaneSettings(
    HaulLane Lane, HaulMethod Method, int ShipTypeId, double CargoM3, IReadOnlyList<long> CharacterIds)
{
    /// <summary>Planned in the pilot's own current ship rather than a chosen hull.</summary>
    public bool UsesCurrentShip => ShipTypeId <= 0;

    /// <summary>A service carries it: no pilot of ours, so no ship to size it by.</summary>
    public bool NeedsShip => Method != HaulMethod.Service;

    /// <summary>
    /// The most one courier contract may hold, for a lane a service carries: a trip bigger than
    /// this is split into one task per contract. <see cref="DefaultContractM3"/> unless set.
    /// </summary>
    public double ContractM3 { get; init; } = DefaultContractM3;

    /// <summary>What the common hauling services take in one contract.</summary>
    public const double DefaultContractM3 = 350_000;
}

/// <summary>
/// The hulls that haul: haulers, transports, freighters and the industrial capitals.
///
/// <para>By SDE group rather than by cargo hold, because the hold is not the story for half of
/// them — a deep space transport's real space is its fleet hangar, which no capacity field
/// carries. The settings offer these as lane ships, and an in-system loop is shared only among
/// pilots sitting in one.</para>
/// </summary>
public static class HaulHulls
{
    public static readonly int[] Groups =
    [
        28,    // Hauler
        380,   // Deep Space Transport
        1202,  // Blockade Runner
        513,   // Freighter
        902,   // Jump Freighter
        941,   // Industrial Command Ship
        883,   // Capital Industrial Ship
    ];
}

/// <summary>The words for lanes and methods, in one place for the list and the settings.</summary>
public static class HaulLaneText
{
    public static string Lane(HaulLane lane) => lane switch
    {
        HaulLane.InSystem     => WorklistText.HaulLaneInSystem,
        HaulLane.Nearby       => WorklistText.HaulLaneNearby,
        HaulLane.LongDistance => WorklistText.HaulLaneLongDistance,
        _                     => "",
    };

    public static string Method(HaulMethod method) => method switch
    {
        HaulMethod.Service          => WorklistText.HaulMethodService,
        HaulMethod.BridgedFreighter => WorklistText.HaulMethodBridgedFreighter,
        HaulMethod.JumpFreighter    => WorklistText.HaulMethodJumpFreighter,
        HaulMethod.Gating           => WorklistText.HaulMethodGating,
        _                           => WorklistText.HaulMethodFly,
    };
}
