namespace EveConsole.Services;

// Helpers for classifying EVE location/entity IDs by their well-known numeric ranges.
public static class EveIds
{
    // Player-owned Upwell structures (citadels, engineering complexes, refineries) and everything
    // anchored/contained above this threshold. NPC stations are 60,000,000–64,000,000 and solar
    // systems are 30–33 million, so anything at/above 1 trillion is a structure, ship, or container.
    public const long PlayerStructureThreshold = 1_000_000_000_000L;

    public static bool IsPlayerStructure(long id) => id >= PlayerStructureThreshold;
}
