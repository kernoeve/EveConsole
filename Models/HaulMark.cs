namespace EveConsole.Models;

/// <summary>
/// Some of one item a pilot has said they hauled, ticked off at a loop's stop: picked up, and once
/// it is put down, delivered.
///
/// <para>⚠️ A correction for the asset lag, not a record of history. Assets are polled hourly, and
/// until the poll sees a haul the planner would send the pilot for cargo already in their hold and
/// leave the job it was for waiting. A delivered mark moves the goods in every stock count, the way
/// a contract does (see ContractLag), until the owner's asset poll has run since — the snapshot
/// shows it then. A mark picked up and not yet delivered is cargo aboard the pilot's ship: on the
/// shelf it came off in every count but the hauling plan's, until it is put down or the pilot's
/// own poll shows the ship without it (see HaulLag).</para>
/// </summary>
public class HaulMark
{
    public long Id { get; set; }

    /// <summary>Who hauled it: whose hold it is in between the two ticks.</summary>
    public long PilotId { get; set; }

    /// <summary>The structure or station it was picked up at — or, below zero, the pilot's hold
    /// (HaulLoopPlanner.HoldOf): cargo aboard with no pick-up ticked, put down and delivered at once.</summary>
    public long FromId { get; set; }

    /// <summary>Put down out of the hold, with no pick-up ticked for it. Not stored.</summary>
    public bool FromHold => FromId < 0;

    /// <summary>Where it was going, and is put down at delivery.</summary>
    public long ToId { get; set; }

    public int  TypeId { get; set; }
    public long Units  { get; set; }

    /// <summary>Whose hangar it left and lands in: the asset's own owner at the pick-up — a
    /// corporation's for corp stock — so it is that owner's poll that settles it.</summary>
    public string OwnerType { get; set; } = "character";
    public long   OwnerId   { get; set; }

    public DateTimeOffset  PickedAt    { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
