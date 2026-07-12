using System.Text.Json.Serialization;

namespace EveCortex.Models;

// -----------------------------------------------------------------------
// ESI response shapes — these match the JSON returned by the API
// -----------------------------------------------------------------------

public class EsiCharacterPublic
{
    [JsonPropertyName("name")]              public string Name           { get; init; } = "";
    [JsonPropertyName("corporation_id")]    public int    CorporationId  { get; init; }
    [JsonPropertyName("alliance_id")]       public int?   AllianceId     { get; init; }
    [JsonPropertyName("birthday")]          public DateTimeOffset Birthday { get; init; }
    [JsonPropertyName("description")]       public string Description    { get; init; } = "";
    [JsonPropertyName("security_status")]   public float  SecurityStatus { get; init; }
}

public class EsiCorporation
{
    [JsonPropertyName("name")]         public string Name        { get; init; } = "";
    [JsonPropertyName("ticker")]       public string Ticker      { get; init; } = "";
    [JsonPropertyName("ceo_id")]       public long   CeoId       { get; init; }
    [JsonPropertyName("member_count")] public int    MemberCount { get; init; }
    [JsonPropertyName("alliance_id")]  public int?   AllianceId  { get; init; }
}

public class EsiSkills
{
    [JsonPropertyName("skills")]          public List<EsiSkill> Skills       { get; init; } = [];
    [JsonPropertyName("total_sp")]        public long           TotalSp      { get; init; }
    [JsonPropertyName("unallocated_sp")]  public int            UnallocatedSp { get; init; }
}

public class EsiSkill
{
    [JsonPropertyName("skill_id")]             public int  SkillId            { get; init; }
    [JsonPropertyName("trained_skill_level")]  public int  TrainedSkillLevel  { get; init; }
    [JsonPropertyName("active_skill_level")]   public int  ActiveSkillLevel   { get; init; }
    [JsonPropertyName("skillpoints_in_skill")] public long SkillpointsInSkill { get; init; }
}

public class EsiUniverseName
{
    [JsonPropertyName("id")]       public int    Id       { get; init; }
    [JsonPropertyName("name")]     public string Name     { get; init; } = "";
    [JsonPropertyName("category")] public string Category { get; init; } = "";
}

// /universe/moons/{id}/ — moons aren't resolvable via universe/names, so they need this endpoint.
public class EsiMoonDetail
{
    [JsonPropertyName("moon_id")]   public int    MoonId   { get; init; }
    [JsonPropertyName("name")]      public string Name     { get; init; } = "";
    [JsonPropertyName("system_id")] public int    SystemId { get; init; }
}

// Same payload but with an int64 id, so it can hold character IDs above int.MaxValue
// (modern EVE character IDs exceed 2.1 billion).
public class EsiUniverseNameLong
{
    [JsonPropertyName("id")]       public long   Id       { get; init; }
    [JsonPropertyName("name")]     public string Name     { get; init; } = "";
    [JsonPropertyName("category")] public string Category { get; init; } = "";
}

// Persistent id → name cache (characters, corporations, alliances, structures, types).
// EVE entity names are immutable, so a resolved row is kept across sessions and never re-fetched.
// Shared with the Industry Browser, which originally created the UniverseNames table.
public class UniverseName
{
    public long   EntityId { get; set; }
    public string Name     { get; set; } = "";
    public string Category { get; set; } = "";
}

public class EsiSkillQueueItem
{
    [JsonPropertyName("skill_id")]          public int             SkillId         { get; init; }
    [JsonPropertyName("queue_position")]    public int             QueuePosition   { get; init; }
    [JsonPropertyName("finished_level")]    public int             FinishedLevel   { get; init; }
    [JsonPropertyName("training_start_sp")] public int             TrainingStartSp { get; init; }
    [JsonPropertyName("level_start_sp")]    public int             LevelStartSp    { get; init; }
    [JsonPropertyName("level_end_sp")]      public int             LevelEndSp      { get; init; }
    [JsonPropertyName("start_date")]        public DateTimeOffset? StartDate       { get; init; }
    [JsonPropertyName("finish_date")]       public DateTimeOffset? FinishDate      { get; init; }
}

// -----------------------------------------------------------------------
// Local database entities — what we actually persist
// -----------------------------------------------------------------------

public class Character
{
    public long   Id             { get; set; }   // Eve character ID
    public string Name           { get; set; } = "";
    public int    CorporationId  { get; set; }
    public int?   AllianceId     { get; set; }
    public float  SecurityStatus { get; set; }
    public long   TotalSp        { get; set; }
    public int    UnallocatedSp  { get; set; }
    public string RefreshToken   { get; set; } = "";
    // Space-separated list of ESI scopes granted to this character's token.
    public string GrantedScopes  { get; set; } = "";
    // Stored at auth time; access tokens auto-refresh, so use LastUpdated for "last authenticated".
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public DateTimeOffset LastUpdated { get; set; }

    public bool HasScope(string scope) =>
        GrantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope);
}

public class Corporation
{
    public int    Id              { get; set; }  // Eve corp ID
    public string Name            { get; set; } = "";
    public string Ticker          { get; set; } = "";
    // Stored as metadata only — no EF navigation property, so no relationship tracking.
    // Removing a Character has zero effect on this entity.
    public long   AuthCharacterId { get; set; }
    // Corp carries its own token so it is fully independent of the auth character's record.
    public string          RefreshToken         { get; set; } = "";
    public string          GrantedScopes        { get; set; } = "";
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public DateTimeOffset  LastUpdated          { get; set; }
    public bool            IsPersonal           { get; set; } = false;

    // Comma-separated corp endpoint keys the auth character cannot poll (lacks the required
    // in-corp role). Populated from the auth character's roles on add / role refresh, and
    // self-healed when an endpoint returns a 403 "required role" error. Skipped by the poller
    // so we stop hammering endpoints we have no access to.
    public string          DeniedEndpoints      { get; set; } = "";
}
