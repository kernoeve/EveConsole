using System.Text.Json.Serialization;
using EveConsole.Models;

namespace EveConsole.Services;

// ---------------------------------------------------------------------------
// Wire shapes for the map statistics endpoints.
//
// EVE Ref republishes ESI's JSON verbatim, so these DTOs deserialise the live
// endpoint and the archived snapshot alike — there is no separate archive
// parser. The one thing the payloads never carry is a timestamp: which hour a
// snapshot describes comes from the ESI Last-Modified header or the archive
// index's file_time, never from the body.
// ---------------------------------------------------------------------------

public sealed class EsiSystemJump
{
    [JsonPropertyName("system_id")]  public int SystemId  { get; set; }
    [JsonPropertyName("ship_jumps")] public int ShipJumps { get; set; }
}

public sealed class EsiSystemKill
{
    [JsonPropertyName("system_id")]  public int SystemId  { get; set; }
    [JsonPropertyName("ship_kills")] public int ShipKills { get; set; }
    [JsonPropertyName("pod_kills")]  public int PodKills  { get; set; }
    [JsonPropertyName("npc_kills")]  public int NpcKills  { get; set; }
}

public sealed class EsiSovereigntyEntry
{
    [JsonPropertyName("system_id")]      public int   SystemId      { get; set; }
    [JsonPropertyName("faction_id")]     public int?  FactionId     { get; set; }
    [JsonPropertyName("corporation_id")] public long? CorporationId { get; set; }
    [JsonPropertyName("alliance_id")]    public long? AllianceId    { get; set; }
}

public sealed class EsiSovStructureEntry
{
    [JsonPropertyName("structure_id")]                 public long            StructureId     { get; set; }
    [JsonPropertyName("solar_system_id")]              public int             SystemId        { get; set; }
    [JsonPropertyName("alliance_id")]                  public long?           AllianceId      { get; set; }
    [JsonPropertyName("structure_type_id")]            public int             StructureTypeId { get; set; }
    [JsonPropertyName("vulnerability_occupancy_level")] public double?        Adm             { get; set; }
    [JsonPropertyName("vulnerable_start_time")]        public DateTimeOffset? VulnerableStart { get; set; }
    [JsonPropertyName("vulnerable_end_time")]          public DateTimeOffset? VulnerableEnd   { get; set; }
}

public sealed class EsiIndustrySystem
{
    [JsonPropertyName("solar_system_id")] public int SystemId { get; set; }
    [JsonPropertyName("cost_indices")]    public List<EsiCostIndex> CostIndices { get; set; } = [];
}

public sealed class EsiCostIndex
{
    [JsonPropertyName("activity")]   public string Activity  { get; set; } = "";
    [JsonPropertyName("cost_index")] public double CostIndex { get; set; }
}

public sealed class EsiFwSystem
{
    [JsonPropertyName("solar_system_id")]          public int    SystemId               { get; set; }
    [JsonPropertyName("owner_faction_id")]         public int    OwnerFactionId         { get; set; }
    [JsonPropertyName("occupier_faction_id")]      public int    OccupierFactionId      { get; set; }
    [JsonPropertyName("contested")]                public string Contested              { get; set; } = "";
    [JsonPropertyName("victory_points")]           public int    VictoryPoints          { get; set; }
    [JsonPropertyName("victory_points_threshold")] public int    VictoryPointsThreshold { get; set; }
}

public sealed class EsiIncursion
{
    [JsonPropertyName("constellation_id")]  public int    ConstellationId { get; set; }
    [JsonPropertyName("staging_solar_system_id")] public int StagingSystemId { get; set; }
    [JsonPropertyName("faction_id")]        public int    FactionId       { get; set; }
    [JsonPropertyName("state")]             public string State           { get; set; } = "";
    [JsonPropertyName("influence")]         public double Influence       { get; set; }
    [JsonPropertyName("has_boss")]          public bool   HasBoss         { get; set; }
}

/// <summary>
/// Turns a decoded payload into the rows for one bucket. Kept apart from both the ESI poller
/// and the archive backfill so that the two cannot drift into storing subtly different rows
/// for the same hour — the whole design depends on them being interchangeable.
/// </summary>
public static class MapStatsIngest
{
    public static List<MapSystemJump> Jumps(string bucket, IEnumerable<EsiSystemJump> src) =>
        src.Select(x => new MapSystemJump
        {
            Bucket = bucket, SystemId = x.SystemId, ShipJumps = x.ShipJumps,
        }).ToList();

    public static List<MapSystemKill> Kills(string bucket, IEnumerable<EsiSystemKill> src) =>
        src.Select(x => new MapSystemKill
        {
            Bucket    = bucket,
            SystemId  = x.SystemId,
            ShipKills = x.ShipKills,
            PodKills  = x.PodKills,
            NpcKills  = x.NpcKills,
        }).ToList();

    public static List<MapSovereignty> Sovereignty(string bucket, IEnumerable<EsiSovereigntyEntry> src) =>
        src
            // Systems with no holder at all carry only system_id; storing those would be a row
            // per empty system per hour for no information.
            .Where(x => x.FactionId is not null || x.CorporationId is not null || x.AllianceId is not null)
            .Select(x => new MapSovereignty
            {
                Bucket        = bucket,
                SystemId      = x.SystemId,
                FactionId     = x.FactionId,
                CorporationId = x.CorporationId,
                AllianceId    = x.AllianceId,
            }).ToList();

    public static List<MapSovStructure> SovStructures(string bucket, IEnumerable<EsiSovStructureEntry> src) =>
        src.Select(x => new MapSovStructure
        {
            Bucket          = bucket,
            StructureId     = x.StructureId,
            SystemId        = x.SystemId,
            AllianceId      = x.AllianceId,
            StructureTypeId = x.StructureTypeId,
            Adm             = x.Adm,
            VulnerableStart = x.VulnerableStart,
            VulnerableEnd   = x.VulnerableEnd,
        }).ToList();

    public static List<MapIndustryIndex> Industry(string bucket, IEnumerable<EsiIndustrySystem> src) =>
        src.SelectMany(s => s.CostIndices.Select(c => new MapIndustryIndex
        {
            Bucket    = bucket,
            SystemId  = s.SystemId,
            Activity  = c.Activity,
            CostIndex = c.CostIndex,
        }))
        // One row per (system, activity) — a duplicate activity would break the primary key
        // and lose the whole bucket.
        .GroupBy(r => (r.SystemId, r.Activity))
        .Select(g => g.First())
        .ToList();

    public static List<MapFactionWarfare> FactionWarfare(string bucket, IEnumerable<EsiFwSystem> src) =>
        src.Select(x => new MapFactionWarfare
        {
            Bucket                 = bucket,
            SystemId               = x.SystemId,
            OwnerFactionId         = x.OwnerFactionId,
            OccupierFactionId      = x.OccupierFactionId,
            ContestedState         = x.Contested,
            VictoryPoints          = x.VictoryPoints,
            VictoryPointsThreshold = x.VictoryPointsThreshold,
        }).ToList();

    public static List<MapIncursion> Incursions(string bucket, IEnumerable<EsiIncursion> src) =>
        src.GroupBy(x => x.ConstellationId).Select(g =>
        {
            var x = g.First();
            return new MapIncursion
            {
                Bucket          = bucket,
                ConstellationId = x.ConstellationId,
                StagingSystemId = x.StagingSystemId,
                FactionId       = x.FactionId,
                State           = x.State,
                Influence       = x.Influence,
                HasBoss         = x.HasBoss,
            };
        }).ToList();
}
