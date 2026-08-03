namespace EveConsole.Models;

// ---------------------------------------------------------------------------
// Time-series map statistics.
//
// CCP publishes these as discrete hourly buckets, each identified by the
// Last-Modified header on the ESI response — not as a free-running window you
// sample whenever you like. Every row here is therefore keyed by the bucket it
// belongs to rather than by when we happened to fetch it.
//
// That is what lets the live ESI poll and the EVE Ref archive backfill write
// interchangeable rows: both produce the same (Bucket, SystemId) key for the
// same hour, so they deduplicate against each other and the polling schedule
// stops needing to be precise. ESI only ever serves the current hour, so the
// archive is the only way to recover a period when the app was closed.
//
// Bucket is stored as "yyyy-MM-dd HH" (UTC). Sortable as text, unambiguous,
// and cheap to group by day with SUBSTR.
// ---------------------------------------------------------------------------

/// <summary>Ship jumps through a system in one hour. From /universe/system_jumps/.</summary>
public class MapSystemJump
{
    public string Bucket    { get; set; } = "";
    public int    SystemId  { get; set; }
    public int    ShipJumps { get; set; }
}

/// <summary>Kills in a system in one hour. From /universe/system_kills/.</summary>
public class MapSystemKill
{
    public string Bucket    { get; set; } = "";
    public int    SystemId  { get; set; }
    public int    ShipKills { get; set; }
    public int    PodKills  { get; set; }
    public int    NpcKills  { get; set; }
}

/// <summary>
/// Daily totals, produced by rolling up the hourly tables once they age past the
/// retention window. Long-range trend survives without keeping ~42M rows a year
/// for jumps alone.
/// </summary>
public class MapSystemDaily
{
    public string Day       { get; set; } = "";   // yyyy-MM-dd (UTC)
    public int    SystemId  { get; set; }
    public int    ShipJumps { get; set; }
    public int    ShipKills { get; set; }
    public int    PodKills  { get; set; }
    public int    NpcKills  { get; set; }
    /// <summary>How many hourly buckets this was built from — a day rolled up from
    /// fewer than 24 is incomplete, which matters because the archive itself has
    /// occasional missing hours.</summary>
    public int    Hours     { get; set; }
}

/// <summary>
/// Who holds a system, sampled hourly. Sovereignty changes rarely, so this is
/// stored as a state row per bucket rather than a change log — simpler to query
/// for "who held this at time X" and still cheap.
/// </summary>
public class MapSovereignty
{
    public string Bucket        { get; set; } = "";
    public int    SystemId      { get; set; }
    public int?   FactionId     { get; set; }
    public long?  CorporationId { get; set; }
    public long?  AllianceId    { get; set; }
}

/// <summary>
/// Sovereignty structure state, most importantly the Activity Defense Multiplier
/// that dotlan surfaces on its region maps.
/// </summary>
public class MapSovStructure
{
    public string          Bucket          { get; set; } = "";
    public long            StructureId     { get; set; }
    public int             SystemId        { get; set; }
    public long?           AllianceId      { get; set; }
    public int             StructureTypeId { get; set; }
    /// <summary>Activity Defense Multiplier.</summary>
    public double?         Adm             { get; set; }
    public DateTimeOffset? VulnerableStart { get; set; }
    public DateTimeOffset? VulnerableEnd   { get; set; }
}

/// <summary>Industry cost indices per system per activity. From /industry/systems/.</summary>
public class MapIndustryIndex
{
    public string Bucket        { get; set; } = "";
    public int    SystemId      { get; set; }
    public string Activity      { get; set; } = "";   // manufacturing, researching_time_efficiency, ...
    public double CostIndex     { get; set; }
}

/// <summary>Faction warfare system ownership and contest state. From /fw/systems/.</summary>
public class MapFactionWarfare
{
    public string Bucket                 { get; set; } = "";
    public int    SystemId               { get; set; }
    public int    OwnerFactionId         { get; set; }
    public int    OccupierFactionId      { get; set; }
    public string ContestedState         { get; set; } = "";
    public int    VictoryPoints          { get; set; }
    public int    VictoryPointsThreshold { get; set; }
}

/// <summary>
/// Active incursions. Keyed by constellation rather than system, which is how CCP
/// scopes them; the staging system is one member of that constellation.
/// </summary>
public class MapIncursion
{
    public string Bucket          { get; set; } = "";
    public int    ConstellationId { get; set; }
    public int    StagingSystemId { get; set; }
    public int    FactionId       { get; set; }
    public string State           { get; set; } = "";
    public double Influence       { get; set; }
    public bool   HasBoss         { get; set; }
}

/// <summary>
/// One row per (dataset, bucket) that has been stored, whatever the source. This is
/// what makes gap detection possible: an hour with genuinely no activity produces no
/// stat rows at all, so the absence of rows cannot distinguish "nothing happened"
/// from "never fetched". Without it, every gap-fill pass would re-download the same
/// quiet hours forever.
/// </summary>
public class MapStatBucket
{
    public string         Dataset   { get; set; } = "";
    public string         Bucket    { get; set; } = "";
    public DateTimeOffset StoredAt  { get; set; }
    /// <summary>"esi" or "everef" — which path supplied it, for diagnostics only.</summary>
    public string         Source    { get; set; } = "";
    public int            RowCount  { get; set; }
}
