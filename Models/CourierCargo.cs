namespace EveConsole.Models;

/// <summary>
/// One line of what is in one of our courier contracts, as the user entered it — ticked off a
/// hauling-service trip on the worklist, or typed or pasted in the Contracts tool.
///
/// <para>⚠️ ESI never lists a courier's cargo. Of 142 courier contracts of ours, 19 answered the
/// items call with HTTP 200 and not one returned a line: the goods leave the hangar into the
/// courier's package, and no asset or contract endpoint shows them again until they land. This
/// is the only record of what is on the way, kept apart from the assets on purpose: it is not in
/// any hangar, an asset load would wipe it, and the courier's collateral already stands for its
/// worth wherever net worth is counted.</para>
///
/// <para>Planning counts it (see <see cref="Services.CourierCargoLag"/>): in transit to the
/// destination while the courier is out, delivered there once it completes, until the asset poll
/// shows it.</para>
/// </summary>
public class CourierCargo
{
    public long Id { get; set; }

    /// <summary>The courier contract it is in; 0 until one is found for it — a worklist tick made
    /// before the contracts poll has seen the contract.</summary>
    public int ContractId { get; set; }

    /// <summary>Where the courier starts and ends: the trip's two ends until it is matched to a
    /// contract, the contract's after.</summary>
    public long FromId { get; set; }
    public long ToId   { get; set; }

    public int  TypeId   { get; set; }
    public long Quantity { get; set; }

    /// <summary>Whose hangar it left: the largest holder at the start when it was ticked. A
    /// contract's own issuer takes over once it is matched.</summary>
    public string OwnerType { get; set; } = "character";
    public long   OwnerId   { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}
