using EveConsole.Services;

namespace EveConsole.ViewModels;

/// <summary>
/// One stack of the item the Item Browser is showing: where it is, who holds it, how many.
///
/// <para>Read from the Asset Browser's own Base query, filtered to the one type, so the two tools
/// cannot disagree about where something is, what it is worth or what counts as an asset — that
/// includes the item a running industry job will deliver, which carries the flag "Industry Job".</para>
/// </summary>
public sealed class ItemAssetRowVm
{
    public string  Location           { get; init; } = "";
    /// <summary>The root location: the station, structure or system the stack is in, however
    /// deep in containers it sits.</summary>
    public long    LocationId         { get; init; }
    public bool    IsStation          { get; init; }

    public string  Owner              { get; init; } = "";
    public long    OwnerId            { get; init; }
    public bool    OwnerIsCorporation { get; init; }

    /// <summary>The containers between the location and the stack, outermost first, with the
    /// corporation hangar division where there is one. Empty for a stack on the hangar floor.</summary>
    public string  Container          { get; init; } = "";
    public string  Flag               { get; init; } = "";

    public long    Quantity           { get; init; }
    public string  QuantityText       => Quantity.ToString("N0");

    public string  SolarSystem        { get; init; } = "";
    public int     SolarSystemId      { get; init; }
    public string  Region             { get; init; } = "";
    public double? Security           { get; init; }
    public string  SecurityText       => Security is double s ? s.ToString("0.0") : "";

    public double  Value              { get; init; }
    public string  ValueText          => Value > 0 ? MarketFmt.Isk(Value) : "—";

    /// <summary>Not in a hangar yet: the product of a running job, or the blueprint in one.</summary>
    public bool    IsInJob            => Flag == "Industry Job";

    public bool HasLocationLink => LocationId > 0 && Location.Length > 0;
    public bool HasOwnerLink    => OwnerId > 0;
    public bool HasSystemLink   => SolarSystemId > 0;

    /// <summary>NPC station to the entity browser, player structure to its own tool — decided by
    /// the location type the asset poll recorded, not by the id.</summary>
    public void OpenLocation()
    {
        if (IsStation) EntityNavigator.Instance.Entity(EntityKind.Station, LocationId);
        else           EntityNavigator.Instance.Structure(LocationId);
    }

    public void OpenOwner() =>
        EntityNavigator.Instance.Entity(OwnerIsCorporation ? EntityKind.PlayerCorp : EntityKind.Pilot, OwnerId);

    public void OpenSystem() => EntityNavigator.Instance.System(SolarSystemId);
}
