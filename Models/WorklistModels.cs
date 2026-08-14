namespace EveConsole.Models;

// ── Configuration ─────────────────────────────────────────────────────────────

/// <summary>
/// Who works a given station or structure.
///
/// Separate from any one generator because every one of them needs the same answer: a standing
/// buy order knows where it lives but not who maintains it, an inventory-level rule names a
/// station, and the order-driven generator routes builds by station. Holding the mapping once
/// means an alt changing hands is a single edit rather than three.
/// </summary>
public class WorklistDesk
{
    public int    Id            { get; set; }

    /// <summary>NPC station or player structure — the same id space the rest of the app uses.</summary>
    public long   LocationId    { get; set; }
    public string LocationName  { get; set; } = "";

    public long   CharacterId   { get; set; }
    public string CharacterName { get; set; } = "";

    /// <summary>Free text for the player's own benefit — "hauler", "market alt", and so on.</summary>
    public string Note          { get; set; } = "";
}

// ── Item state ────────────────────────────────────────────────────────────────

/// <summary>
/// The only part of a worklist item that is stored.
///
/// Items themselves are recomputed from live data on every refresh and never persisted: when a
/// buy order is finally placed, the suggestion stops being generated, which is the whole
/// completion mechanism. What cannot be recomputed is how long the player has been looking at
/// an item, and whether they asked it to go away for a while — so only those are kept, keyed by
/// the item's stable key.
/// </summary>
public class WorklistItemState
{
    /// <summary>The generated item's stable key. Same suggestion, same key, every refresh.</summary>
    public string Key          { get; set; } = "";

    public DateTimeOffset FirstSeenAt   { get; set; }

    /// <summary>Null when not snoozed. The item is generated regardless and filtered on display,
    /// so an expired snooze needs no cleanup pass.</summary>
    public DateTimeOffset? SnoozedUntil { get; set; }
}

/// <summary>
/// "When this inventory group drops below X%, there should be a buy order at this station."
///
/// One row per threshold, so several can point at the same group: below 100% order locally,
/// below 75% also order at the hub. They stack rather than override — being well short is a
/// reason to buy in both places, not to stop buying in the first one.
///
/// No character here on purpose. The station's <see cref="WorklistDesk"/> answers that, so an
/// alt changing hands does not mean editing every rule that names its station.
/// </summary>
public class WorklistInvRule
{
    public int  Id      { get; set; }
    public int  GroupId { get; set; }

    /// <summary>Fire when available stock falls below this share of the group target.</summary>
    public double ThresholdPercent { get; set; } = 100;

    /// <summary>
    /// How full to order back up to, as a share of the group target. Defaults to a full refill:
    /// ordering only back up to the threshold that fired guarantees the same suggestion returns
    /// the moment anything is consumed.
    /// </summary>
    public double FillTargetPercent { get; set; } = 100;

    public long   LocationId   { get; set; }
    public string LocationName { get; set; } = "";

    public bool Enabled { get; set; } = true;
}
