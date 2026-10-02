namespace EveConsole.Services.Pi;

/// <summary>What takes the tax when goods leave or reach a planet.</summary>
public enum PiChargeKind
{
    /// <summary>A customs office: high-sec, low-sec, wormholes and NPC null-sec.</summary>
    CustomsOffice,
    /// <summary>An orbital skyhook: sovereignty null-sec, which has no customs offices.</summary>
    Skyhook,
}

/// <summary>
/// The tax rate a planet charges and where it came from.
/// </summary>
/// <param name="Rate">A fraction: 0.10 is 10 %.</param>
/// <param name="Learned">True when it was learned from the journal for this planet; false when it
/// is the Settings default for <paramref name="Kind"/>.</param>
/// <param name="LearnedAt">The journal entry it was learned from; null for a default.</param>
public sealed record PiChargeRate(double Rate, PiChargeKind Kind, bool Learned = false, DateTimeOffset? LearnedAt = null);

/// <summary>What one type costs to move through the planet's customs office or skyhook, per unit
/// and per day at the colony's steady rate.</summary>
public sealed record PiTypeCharge(int TypeId, PiTier Tier,
                                  double ExportPerUnit, double ImportPerUnit,
                                  double ExportPerDay,  double ImportPerDay);

public static class PiCharges
{
    /// <summary>
    /// Export charge per unit: the tier's customs base cost times the rate.
    /// ⚠️ Game rule: import is half of export.
    /// </summary>
    public static double ExportPerUnit(PiTier tier, double rate) => PiTiers.BaseCost(tier) * rate;

    public static double ImportPerUnit(PiTier tier, double rate) => PiTiers.BaseCost(tier) * rate / 2;

    /// <summary>
    /// Customs office or skyhook, from the system.
    ///
    /// <para>High and low security (above 0.0) have customs offices; so do wormholes and NPC
    /// null-sec, whose systems carry a faction in the SDE. Sovereignty null-sec — security 0.0 or
    /// below, no faction, not a wormhole — has skyhooks instead. ⚠️ The true security, not the
    /// displayed one: a system at 0.02 shows as 0.1 and is low-sec.</para>
    /// </summary>
    public static PiChargeKind KindFor(double security, int? factionId, bool isWormhole)
        => security <= 0.0 && factionId is null && !isWormhole
            ? PiChargeKind.Skyhook
            : PiChargeKind.CustomsOffice;
}
