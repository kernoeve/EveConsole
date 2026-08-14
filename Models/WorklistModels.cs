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
public class WorklistMarketAlt
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
/// No character here on purpose. The station's <see cref="WorklistMarketAlt"/> answers that, so an
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

    /// <summary>
    /// "Buy" places a market order for the shortfall; "Build" starts a job for it. Which is right
    /// depends on the group: raw materials are bought, components and finished goods are made,
    /// and a rule that could only buy would tell you to purchase things you manufacture.
    /// </summary>
    public string Action { get; set; } = "Buy";
}

/// <summary>
/// "Plan the pending customer orders against this park, and buy what is missing here."
///
/// The order-driven counterpart to <see cref="WorklistInvRule"/>. Where that one keeps a
/// stockpile topped up regardless of demand, this buys only what outstanding orders actually
/// need — the build-to-order style, where the order drives acquisition rather than the shelf.
/// </summary>
public class WorklistOrderRule
{
    public int Id     { get; set; }

    /// <summary>Which Indy Park to plan against — it decides facilities, rigs and therefore
    /// the material quantities.</summary>
    public int ParkId { get; set; }

    /// <summary>Where the resulting buy orders go. Its market alt does the work.</summary>
    public long   LocationId   { get; set; }
    public string LocationName { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Which character maintains a corporation's standing projects.
///
/// A sibling of <see cref="WorklistMarketAlt"/>, kept separate rather than folded in because the
/// key is different in kind: one answers "who trades at this station", this answers "who
/// administers this corporation". Storing both in a table named for market alts would make the
/// name lie about half its rows, and the trivial CRUD they share is the cheaper duplication.
/// </summary>
public class WorklistCorpAlt
{
    public int    Id              { get; set; }
    public long   CorporationId   { get; set; }
    public string CorporationName { get; set; } = "";
    public long   CharacterId     { get; set; }
    public string CharacterName   { get; set; } = "";
    public string Note            { get; set; } = "";
}

/// <summary>
/// A character the worklist may assign industry jobs to, and on what terms.
///
/// Opt-in per character, because slot capacity across every alt is a meaningless number: alts
/// sitting in corps that never run industry would contribute dozens of "free" slots that are
/// not actually available for work.
///
/// The three activities are independent switches rather than one flag. A character given a pile
/// of BPOs might run copies all day with manufacturing and reactions deliberately left alone,
/// and collapsing that into "does industry" would either hide their slots or invent work for
/// slots the player has other plans for.
/// </summary>
public class WorklistIndyChar
{
    public int    Id            { get; set; }
    public long   CharacterId   { get; set; }
    public string CharacterName { get; set; } = "";

    public bool Manufacturing { get; set; } = true;
    public bool Reactions     { get; set; } = true;
    public bool Science       { get; set; }

    /// <summary>
    /// Where this character's jobs may draw materials from. Both are offered because the habit
    /// differs: materials pooled in a corp hangar serve every alt in that corp, while a player
    /// who keeps stock in personal hangars per structure needs the personal side counted instead.
    /// Getting this wrong does not merely mis-count — it suggests jobs that cannot start.
    /// </summary>
    public bool IncludeCorpAssets     { get; set; } = true;
    public bool IncludePersonalAssets { get; set; } = true;

    public string Note { get; set; } = "";
}

/// <summary>
/// A station added to the industry asset scope on top of the region or system it is set to.
///
/// The scope answers "where would I actually pull material from", and for most players that is
/// their home region plus the trade hubs they import through. Jita is not in the region and never
/// will be, but stock sitting there is stock they have — counting it as absent would raise a
/// purchase for something already bought and waiting to be hauled.
/// </summary>
public class WorklistIndyScopeStation
{
    public int    Id           { get; set; }
    public long   LocationId   { get; set; }
    public string LocationName { get; set; } = "";
}
