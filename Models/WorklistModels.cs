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

    /// <summary>
    /// This group is something the operation sells or flies, not an input held so the next thing
    /// can be built.
    ///
    /// <para>⚠️ A count of blocked work cannot tell a customer from a cupboard. Nanotransistors
    /// blocks eleven tasks and ten of them are component buffers refilling themselves — real
    /// work, whose only customer is the shelf it came from. An isotropic blocking a Neurolink
    /// cell blocks every standard capital hull. Both score eleven; only one is worth a slot
    /// today.</para>
    ///
    /// <para>On the RULE rather than on the inventory level, because the level is a stocking
    /// target and this is a statement about what the pipeline is for. It is also per group,
    /// which is the grain people actually think in: "Titans" is final, "Capital Parts" is not.
    /// Off by default, and hand-set — nothing in the blueprint tree can derive it, and the
    /// answer differs between operations.</para>
    /// </summary>
    public bool IsFinalProduct { get; set; }
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
    /// Unused. Kept only so existing rows still load — nothing reads these.
    ///
    /// <para>They used to gate, per character, whether corp or personal stock counted for that
    /// character's jobs, which was wrong twice over. What material is the player's is the asset
    /// scope's business and applies to everyone alike; what a given pilot can physically reach is
    /// their own hangar and their own corporation's, which follows from who they are and needs no
    /// setting. In between, the personal flag quietly hid every asset belonging to a character not
    /// on this list — 8,985 rows of 11,624, including blueprints held by a trading alt.</para>
    /// </summary>
    [Obsolete("Asset visibility comes from the scope; per-character reach from corp membership.")]
    public bool IncludeCorpAssets     { get; set; } = true;

    /// <inheritdoc cref="IncludeCorpAssets"/>
    [Obsolete("Asset visibility comes from the scope; per-character reach from corp membership.")]
    public bool IncludePersonalAssets { get; set; } = true;

    public string Note { get; set; } = "";
}

/// <summary>
/// "Keep this group's stock at this station."
///
/// <para>A statement about <b>where</b> material should sit, not how much should exist. The
/// inventory rules already decide the quantity; this decides its distribution, so it raises
/// hauling and never buying or building. Treating it as additional demand would double every
/// target the moment a station was named for it.</para>
///
/// <para>One row per group per station, and a station may appear under several groups — a
/// structure can be the home for capital parts and merely a consumer of everything else.</para>
/// </summary>
public class WorklistStationLevel
{
    public int    Id           { get; set; }
    public int    GroupId      { get; set; }
    public long   LocationId   { get; set; }
    public string LocationName { get; set; } = "";

    /// <summary>
    /// Where this group's spare stock collects when nothing needs it.
    ///
    /// <para>Without somewhere to send it, surplus has no destination and simply stays wherever
    /// it was made. Capital parts belong at the capital shipyard even when no job is waiting on
    /// them, because that is where the next job will want them.</para>
    /// </summary>
    public bool AcceptsSurplus { get; set; }

    public bool Enabled { get; set; } = true;
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
