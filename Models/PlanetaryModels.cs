namespace EveConsole.Models;

// ── Planetary Industry: colony layouts as ESI last described them ────────────────────────────
//
// GET /characters/{id}/planets/{planet_id}, stored one colony at a time. ⚠️ Everything here is a
// snapshot as of the colony's last_update, which only moves when its owner views the colony in the
// client: an extractor's install and expiry are exact, but storage and factory state later than
// last_update can only be simulated (see PiEngine). Each colony is replaced whole, in one
// transaction, so a reader never sees one half gone.

/// <summary>
/// One colony's stored layout: which colony-list <c>last_update</c> it describes, and when it was
/// read. A layout is re-read only when the colony list reports a different <c>last_update</c>,
/// because nothing else can change it.
/// </summary>
public class PlanetaryLayout
{
    public long CharacterId { get; set; }
    public int  PlanetId    { get; set; }

    /// <summary>The colony's <c>last_update</c> this layout was read for — the moment it describes.</summary>
    public DateTimeOffset LastUpdate { get; set; }

    /// <summary>When the app read it.</summary>
    public DateTimeOffset FetchedAt { get; set; }
}

/// <summary>A structure on the planet's surface: command center, extractor control unit,
/// processor, storage facility or launchpad. What KIND it is follows from its type's group —
/// see PiStaticData.</summary>
public class PlanetaryPin
{
    public long CharacterId { get; set; }
    public int  PlanetId    { get; set; }
    public long PinId       { get; set; }
    public int  TypeId      { get; set; }

    /// <summary>A processor's schematic: <c>factory_details.schematic_id</c>, or the pin's own
    /// <c>schematic_id</c> where ESI gives that instead. Null for everything else.</summary>
    public int? SchematicId { get; set; }

    public DateTimeOffset? InstallTime    { get; set; }
    /// <summary>⚠️ For an extractor this is fixed when the program is installed, so "stopped" and
    /// "stops at" are exact rather than estimated.</summary>
    public DateTimeOffset? ExpiryTime     { get; set; }
    public DateTimeOffset? LastCycleStart { get; set; }

    public double Latitude  { get; set; }
    public double Longitude { get; set; }

    // Extractor control units only.
    public int?    ExtractorProductTypeId { get; set; }
    /// <summary>⚠️ Seconds, as ESI documents it — not minutes.</summary>
    public int?    ExtractorCycleTime     { get; set; }
    /// <summary>The program's base output, which CCP's formula decays and modulates per cycle.
    /// ⚠️ Can be missing, or stale, on ESI's side.</summary>
    public int?    ExtractorQtyPerCycle   { get; set; }
    public double? ExtractorHeadRadius    { get; set; }
    public int     ExtractorHeadCount     { get; set; }
}

/// <summary>What a pin held at the snapshot: storage, a launchpad or the command center, or a
/// processor's input buffer.</summary>
public class PlanetaryPinContent
{
    public long CharacterId { get; set; }
    public int  PlanetId    { get; set; }
    public long PinId       { get; set; }
    public int  TypeId      { get; set; }
    public long Amount      { get; set; }
}

/// <summary>A route: one type moving from a source pin to a destination pin.</summary>
public class PlanetaryRoute
{
    public long   CharacterId      { get; set; }
    public int    PlanetId         { get; set; }
    public long   RouteId          { get; set; }
    public long   SourcePinId      { get; set; }
    public long   DestinationPinId { get; set; }
    public int    ContentTypeId    { get; set; }
    public double Quantity         { get; set; }
    /// <summary>Waypoint pin ids, comma-separated, in order.</summary>
    public string Waypoints        { get; set; } = "";
}

/// <summary>A link between two pins, which routes travel along.</summary>
public class PlanetaryLink
{
    public long CharacterId      { get; set; }
    public int  PlanetId         { get; set; }
    public long SourcePinId      { get; set; }
    public long DestinationPinId { get; set; }
    public int  LinkLevel        { get; set; }
}

/// <summary>
/// What left a colony and what arrived on it between two snapshots, beyond what the colony would
/// have made and used by itself — the evidence customs tax is learned from. See PiTaxLearning.
///
/// <para>Written when a new layout replaces an old one: the old snapshot is simulated forward to
/// the new one's <c>last_update</c>, and the difference from what the new snapshot actually holds
/// is what was taken off (Removed) or brought in (Added). The journal's tax entries carry no
/// quantities, which is why this has to exist at all.</para>
/// </summary>
public class PiColonyMovement
{
    public long Id          { get; set; }
    public long CharacterId { get; set; }
    public int  PlanetId    { get; set; }

    /// <summary>The older snapshot's <c>last_update</c>: the window starts just after it.</summary>
    public DateTimeOffset FromUpdate { get; set; }
    /// <summary>The newer snapshot's <c>last_update</c>: the window ends at it, inclusive.</summary>
    public DateTimeOffset ToUpdate   { get; set; }

    public int  TypeId  { get; set; }
    public long Removed { get; set; }
    public long Added   { get; set; }
}

/// <summary>
/// The customs or skyhook tax rate a planet actually charges, learned from the most recent tax
/// entry the journal holds for it. ⚠️ Per PLANET, not per system: null-sec owners set different
/// rates for different planet types. A planet with no row is charged the default from Settings.
/// </summary>
public class PiPlanetTaxRate
{
    public int    PlanetId    { get; set; }

    /// <summary>A fraction: 0.10 is 10 %.</summary>
    public double Rate        { get; set; }

    /// <summary>The date of the journal entry it was learned from.</summary>
    public DateTimeOffset LearnedAt { get; set; }

    /// <summary>That entry's ESI journal id (the newest, where several were taken together).</summary>
    public long   JournalId   { get; set; }

    public long   CharacterId { get; set; }

    /// <summary>"export", "import" or "both": which kind of entry it came from.</summary>
    public string Source      { get; set; } = "";

    /// <summary>The units the rate was worked out over, for judging how far to trust it.</summary>
    public long   Units       { get; set; }
}
