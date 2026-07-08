using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveCortex.Models;

// ── Wallet ───────────────────────────────────────────────────────────────────

public record EsiWalletJournalEntry(
    [property: JsonPropertyName("id")]              long    Id,
    [property: JsonPropertyName("date")]            DateTimeOffset Date,
    [property: JsonPropertyName("ref_type")]        string  RefType,
    [property: JsonPropertyName("first_party_id")]  long?   FirstPartyId,
    [property: JsonPropertyName("second_party_id")] long?   SecondPartyId,
    [property: JsonPropertyName("amount")]          double? Amount,
    [property: JsonPropertyName("balance")]         double? Balance,
    [property: JsonPropertyName("description")]     string? Description,
    [property: JsonPropertyName("reason")]          string? Reason,
    [property: JsonPropertyName("tax")]             double? Tax,
    [property: JsonPropertyName("tax_receiver_id")] long?   TaxReceiverId,
    [property: JsonPropertyName("context_id")]      long?   ContextId,
    [property: JsonPropertyName("context_id_type")] string? ContextIdType
);

public record EsiWalletTransaction(
    [property: JsonPropertyName("transaction_id")] long   TransactionId,
    [property: JsonPropertyName("date")]           DateTimeOffset Date,
    [property: JsonPropertyName("client_id")]      long   ClientId,
    [property: JsonPropertyName("location_id")]    long   LocationId,
    [property: JsonPropertyName("quantity")]       int    Quantity,
    [property: JsonPropertyName("type_id")]        int    TypeId,
    [property: JsonPropertyName("unit_price")]     double UnitPrice,
    [property: JsonPropertyName("is_buy")]         bool   IsBuy,
    [property: JsonPropertyName("is_personal")]    bool   IsPersonal,
    [property: JsonPropertyName("journal_ref_id")] long   JournalRefId
);

// ── Industry ─────────────────────────────────────────────────────────────────

public record EsiIndustryJob(
    [property: JsonPropertyName("job_id")]               int    JobId,
    [property: JsonPropertyName("installer_id")]         long   InstallerId,
    [property: JsonPropertyName("facility_id")]          long   FacilityId,
    [property: JsonPropertyName("station_id")]           long   StationId,
    [property: JsonPropertyName("activity_id")]          int    ActivityId,
    [property: JsonPropertyName("blueprint_id")]         long   BlueprintId,
    [property: JsonPropertyName("blueprint_type_id")]    int    BlueprintTypeId,
    [property: JsonPropertyName("blueprint_location_id")]long   BlueprintLocationId,
    [property: JsonPropertyName("output_location_id")]   long   OutputLocationId,
    [property: JsonPropertyName("runs")]                 int    Runs,
    [property: JsonPropertyName("cost")]                 double Cost,
    [property: JsonPropertyName("licensed_runs")]        int?   LicensedRuns,
    [property: JsonPropertyName("probability")]          float? Probability,
    [property: JsonPropertyName("product_type_id")]      int?   ProductTypeId,
    [property: JsonPropertyName("status")]               string Status,
    [property: JsonPropertyName("duration")]             int    Duration,
    [property: JsonPropertyName("start_date")]           DateTimeOffset StartDate,
    [property: JsonPropertyName("end_date")]             DateTimeOffset EndDate,
    [property: JsonPropertyName("pause_date")]           DateTimeOffset? PauseDate,
    [property: JsonPropertyName("completed_date")]       DateTimeOffset? CompletedDate,
    [property: JsonPropertyName("completed_character_id")]long? CompletedCharacterId,
    [property: JsonPropertyName("successful_runs")]      int?   SuccessfulRuns
);

// ── Market orders ─────────────────────────────────────────────────────────────

public record EsiMarketOrder(
    [property: JsonPropertyName("order_id")]      long    OrderId,
    [property: JsonPropertyName("type_id")]       int     TypeId,
    [property: JsonPropertyName("location_id")]   long    LocationId,
    [property: JsonPropertyName("volume_total")]  int     VolumeTotal,
    [property: JsonPropertyName("volume_remain")] int     VolumeRemain,
    [property: JsonPropertyName("min_volume")]    int     MinVolume,
    [property: JsonPropertyName("price")]         double  Price,
    [property: JsonPropertyName("is_buy_order")]  bool    IsBuyOrder,
    [property: JsonPropertyName("duration")]      int     Duration,
    [property: JsonPropertyName("issued")]        DateTimeOffset Issued,
    [property: JsonPropertyName("range")]         string  Range,
    [property: JsonPropertyName("escrow")]        double? Escrow,
    [property: JsonPropertyName("is_corporation")]bool?   IsCorporation,
    [property: JsonPropertyName("region_id")]     int?    RegionId,
    [property: JsonPropertyName("state")]         string? State
);

// ── Contracts ─────────────────────────────────────────────────────────────────

public record EsiContractData(
    [property: JsonPropertyName("contract_id")]         int    ContractId,
    [property: JsonPropertyName("issuer_id")]           long   IssuerId,
    [property: JsonPropertyName("issuer_corporation_id")]int   IssuerCorporationId,
    [property: JsonPropertyName("assignee_id")]         long?  AssigneeId,
    [property: JsonPropertyName("acceptor_id")]         long?  AcceptorId,
    [property: JsonPropertyName("start_location_id")]   long?  StartLocationId,
    [property: JsonPropertyName("end_location_id")]     long?  EndLocationId,
    [property: JsonPropertyName("type")]                string Type,
    [property: JsonPropertyName("status")]              string Status,
    [property: JsonPropertyName("title")]               string? Title,
    [property: JsonPropertyName("for_corporation")]     bool   ForCorporation,
    [property: JsonPropertyName("availability")]        string Availability,
    [property: JsonPropertyName("date_issued")]         DateTimeOffset DateIssued,
    [property: JsonPropertyName("date_expired")]        DateTimeOffset? DateExpired,
    [property: JsonPropertyName("date_accepted")]       DateTimeOffset? DateAccepted,
    [property: JsonPropertyName("date_completed")]      DateTimeOffset? DateCompleted,
    [property: JsonPropertyName("days_to_complete")]    int    DaysToComplete,
    [property: JsonPropertyName("price")]               double Price,
    [property: JsonPropertyName("reward")]              double Reward,
    [property: JsonPropertyName("collateral")]          double Collateral,
    [property: JsonPropertyName("buyout")]              double Buyout,
    [property: JsonPropertyName("volume")]              double Volume
);

// Personal / corp contract item (/characters|corporations/{id}/contracts/{cid}/items/)
public record EsiContractItem(
    [property: JsonPropertyName("record_id")]   long  RecordId,
    [property: JsonPropertyName("type_id")]     int   TypeId,
    [property: JsonPropertyName("quantity")]    long  Quantity,
    [property: JsonPropertyName("is_included")] bool  IsIncluded,
    [property: JsonPropertyName("is_singleton")]bool  IsSingleton,
    [property: JsonPropertyName("raw_quantity")]int?  RawQuantity
);

// Public contract list entry (/contracts/public/{region_id}/)
public record EsiPublicContract(
    [property: JsonPropertyName("contract_id")]          int    ContractId,
    [property: JsonPropertyName("type")]                 string Type,
    [property: JsonPropertyName("issuer_id")]            long   IssuerId,
    [property: JsonPropertyName("issuer_corporation_id")]int   IssuerCorporationId,
    [property: JsonPropertyName("start_location_id")]    long?  StartLocationId,
    [property: JsonPropertyName("end_location_id")]      long?  EndLocationId,
    [property: JsonPropertyName("title")]                string? Title,
    [property: JsonPropertyName("date_issued")]          DateTimeOffset DateIssued,
    [property: JsonPropertyName("date_expired")]         DateTimeOffset? DateExpired,
    [property: JsonPropertyName("days_to_complete")]     int    DaysToComplete,
    [property: JsonPropertyName("price")]                double Price,
    [property: JsonPropertyName("reward")]               double Reward,
    [property: JsonPropertyName("collateral")]           double Collateral,
    [property: JsonPropertyName("buyout")]               double Buyout,
    [property: JsonPropertyName("volume")]               double Volume
);

// Public contract item (/contracts/public/items/{contract_id}/)
public record EsiPublicContractItem(
    [property: JsonPropertyName("record_id")]           long RecordId,
    [property: JsonPropertyName("type_id")]             int  TypeId,
    [property: JsonPropertyName("quantity")]            long Quantity,
    [property: JsonPropertyName("is_included")]         bool IsIncluded,
    [property: JsonPropertyName("is_blueprint_copy")]   bool? IsBlueprintCopy,
    [property: JsonPropertyName("material_efficiency")] int? MaterialEfficiency,
    [property: JsonPropertyName("time_efficiency")]     int? TimeEfficiency,
    [property: JsonPropertyName("runs")]                int? Runs
);

// ── Assets & blueprints ───────────────────────────────────────────────────────

public record EsiAsset(
    [property: JsonPropertyName("item_id")]          long   ItemId,
    [property: JsonPropertyName("type_id")]          int    TypeId,
    [property: JsonPropertyName("location_id")]      long   LocationId,
    [property: JsonPropertyName("location_type")]    string LocationType,
    [property: JsonPropertyName("location_flag")]    string LocationFlag,
    [property: JsonPropertyName("quantity")]         int    Quantity,
    [property: JsonPropertyName("is_singleton")]     bool   IsSingleton,
    [property: JsonPropertyName("is_blueprint_copy")]bool?  IsBlueprintCopy
);

public record EsiBlueprintData(
    [property: JsonPropertyName("item_id")]             long   ItemId,
    [property: JsonPropertyName("type_id")]             int    TypeId,
    [property: JsonPropertyName("location_id")]         long   LocationId,
    [property: JsonPropertyName("location_flag")]       string LocationFlag,
    [property: JsonPropertyName("quantity")]            int    Quantity,
    [property: JsonPropertyName("time_efficiency")]     int    TimeEfficiency,
    [property: JsonPropertyName("material_efficiency")] int    MaterialEfficiency,
    [property: JsonPropertyName("runs")]                int    Runs
);

// ── Attributes ────────────────────────────────────────────────────────────────

public record EsiCharacterAttributesData(
    [property: JsonPropertyName("charisma")]     int   Charisma,
    [property: JsonPropertyName("intelligence")] int   Intelligence,
    [property: JsonPropertyName("memory")]       int   Memory,
    [property: JsonPropertyName("perception")]   int   Perception,
    [property: JsonPropertyName("willpower")]    int   Willpower,
    [property: JsonPropertyName("bonus_remaps")] int   BonusRemaps,
    [property: JsonPropertyName("last_remap_date")]             DateTimeOffset? LastRemapDate,
    [property: JsonPropertyName("accruing_remap_cooldown_date")]DateTimeOffset? AccruingRemapCooldownDate
);

// ── Clones ────────────────────────────────────────────────────────────────────

public record EsiClonesData(
    [property: JsonPropertyName("home_location")]            EsiHomeLocation?       HomeLocation,
    [property: JsonPropertyName("jump_clones")]              List<EsiJumpCloneData> JumpClones,
    [property: JsonPropertyName("last_clone_jump_date")]     DateTimeOffset?        LastCloneJumpDate,
    [property: JsonPropertyName("last_station_change_date")] DateTimeOffset?        LastStationChangeDate
);

public record EsiHomeLocation(
    [property: JsonPropertyName("location_id")]   long   LocationId,
    [property: JsonPropertyName("location_type")] string LocationType
);

public record EsiJumpCloneData(
    [property: JsonPropertyName("jump_clone_id")] int       JumpCloneId,
    [property: JsonPropertyName("location_id")]   long      LocationId,
    [property: JsonPropertyName("location_type")] string    LocationType,
    [property: JsonPropertyName("name")]          string?   Name,
    [property: JsonPropertyName("implants")]      List<int> Implants
);

// ── Fatigue ───────────────────────────────────────────────────────────────────

public record EsiFatigueData(
    [property: JsonPropertyName("last_jump_date")]           DateTimeOffset? LastJumpDate,
    [property: JsonPropertyName("jump_fatigue_expire_date")] DateTimeOffset? JumpFatigueExpireDate,
    [property: JsonPropertyName("last_update_date")]         DateTimeOffset? LastUpdateDate
);

// ── Mining ────────────────────────────────────────────────────────────────────

public record EsiMiningData(
    [property: JsonPropertyName("date")]            string Date,   // "YYYY-MM-DD"
    [property: JsonPropertyName("solar_system_id")] int    SolarSystemId,
    [property: JsonPropertyName("type_id")]         int    TypeId,
    [property: JsonPropertyName("quantity")]        long   Quantity
);

// ── Notifications ─────────────────────────────────────────────────────────────

public record EsiNotificationData(
    [property: JsonPropertyName("notification_id")] long          NotificationId,
    [property: JsonPropertyName("type")]            string        Type,
    [property: JsonPropertyName("sender_id")]       long          SenderId,
    [property: JsonPropertyName("sender_type")]     string        SenderType,
    [property: JsonPropertyName("timestamp")]       DateTimeOffset Timestamp,
    [property: JsonPropertyName("is_read")]         bool?         IsRead,
    [property: JsonPropertyName("text")]            string?       Text
);

// ── Contacts ─────────────────────────────────────────────────────────────────

public record EsiContactData(
    [property: JsonPropertyName("contact_id")]   long      ContactId,
    [property: JsonPropertyName("contact_type")] string    ContactType,
    [property: JsonPropertyName("standing")]     float     Standing,
    [property: JsonPropertyName("is_watched")]   bool?     IsWatched,
    [property: JsonPropertyName("is_blocked")]   bool?     IsBlocked,
    [property: JsonPropertyName("label_ids")]    List<long>? LabelIds
);

// ── Kill mails ────────────────────────────────────────────────────────────────

public record EsiKillMailRef(
    [property: JsonPropertyName("killmail_id")]   int    KillMailId,
    [property: JsonPropertyName("killmail_hash")] string KillMailHash
);

public record EsiKillMailFull(
    [property: JsonPropertyName("killmail_id")]     int                    KillMailId,
    [property: JsonPropertyName("killmail_time")]   DateTimeOffset         KillMailTime,
    [property: JsonPropertyName("solar_system_id")] int                    SolarSystemId,
    [property: JsonPropertyName("moon_id")]         int?                   MoonId,
    [property: JsonPropertyName("war_id")]          int?                   WarId,
    [property: JsonPropertyName("victim")]          EsiKillVictim?         Victim,
    [property: JsonPropertyName("attackers")]       List<EsiKillAttacker>? Attackers
);

public record EsiKillVictim(
    [property: JsonPropertyName("character_id")]   long?             CharacterId,
    [property: JsonPropertyName("corporation_id")] long?             CorporationId,
    [property: JsonPropertyName("alliance_id")]    long?             AllianceId,
    [property: JsonPropertyName("faction_id")]     int?              FactionId,
    [property: JsonPropertyName("ship_type_id")]   int               ShipTypeId,
    [property: JsonPropertyName("damage_taken")]   int               DamageTaken,
    [property: JsonPropertyName("items")]          List<EsiKillItem>? Items,
    [property: JsonPropertyName("position")]       EsiKillPosition?   Position
);

public record EsiKillAttacker(
    [property: JsonPropertyName("character_id")]    long?  CharacterId,
    [property: JsonPropertyName("corporation_id")]  long?  CorporationId,
    [property: JsonPropertyName("alliance_id")]     long?  AllianceId,
    [property: JsonPropertyName("faction_id")]      int?   FactionId,
    [property: JsonPropertyName("damage_done")]     int    DamageDone,
    [property: JsonPropertyName("final_blow")]      bool   FinalBlow,
    [property: JsonPropertyName("security_status")] float  SecurityStatus,
    [property: JsonPropertyName("ship_type_id")]    int?   ShipTypeId,
    [property: JsonPropertyName("weapon_type_id")]  int?   WeaponTypeId
);

public record EsiKillItem(
    [property: JsonPropertyName("flag")]               int   Flag,
    [property: JsonPropertyName("item_type_id")]       int   ItemTypeId,
    [property: JsonPropertyName("quantity_destroyed")] long? QuantityDestroyed,
    [property: JsonPropertyName("quantity_dropped")]   long? QuantityDropped,
    [property: JsonPropertyName("singleton")]          int   Singleton
);

public record EsiKillPosition(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z
);

// ── Planetary colonies ────────────────────────────────────────────────────────

public record EsiPlanetaryColony(
    [property: JsonPropertyName("planet_id")]      int           PlanetId,
    [property: JsonPropertyName("planet_type")]    string        PlanetType,
    [property: JsonPropertyName("solar_system_id")]int           SolarSystemId,
    [property: JsonPropertyName("owner_id")]       long          OwnerId,
    [property: JsonPropertyName("last_update")]    DateTimeOffset LastUpdate,
    [property: JsonPropertyName("num_pins")]       int           NumPins,
    [property: JsonPropertyName("upgrade_level")]  int           UpgradeLevel
);

// ── Agents research ───────────────────────────────────────────────────────────

public record EsiAgentResearch(
    [property: JsonPropertyName("agent_id")]         int           AgentId,
    [property: JsonPropertyName("skill_type_id")]    int           SkillTypeId,
    [property: JsonPropertyName("started_at")]       DateTimeOffset StartedAt,
    [property: JsonPropertyName("points_per_day")]   float         PointsPerDay,
    [property: JsonPropertyName("remainder_points")] float         RemainderPoints
);

// ── Loyalty points ────────────────────────────────────────────────────────────

public record EsiLoyaltyPoint(
    [property: JsonPropertyName("corporation_id")]  int CorporationId,
    [property: JsonPropertyName("loyalty_points")]  int LoyaltyPoints
);

// ── Medals ────────────────────────────────────────────────────────────────────

public record EsiMedalData(
    [property: JsonPropertyName("medal_id")]       int           MedalId,
    [property: JsonPropertyName("corporation_id")] int           CorporationId,
    [property: JsonPropertyName("issuer_id")]      long          IssuerId,
    [property: JsonPropertyName("date")]           DateTimeOffset Date,
    [property: JsonPropertyName("reason")]         string        Reason,
    [property: JsonPropertyName("status")]         string        Status
);

// ── Standings ─────────────────────────────────────────────────────────────────

public record EsiStandingData(
    [property: JsonPropertyName("from_id")]   long   FromId,
    [property: JsonPropertyName("from_type")] string FromType,
    [property: JsonPropertyName("standing")]  float  Standing
);

// ── Titles ────────────────────────────────────────────────────────────────────

public record EsiTitleData(
    [property: JsonPropertyName("title_id")] int    TitleId,
    [property: JsonPropertyName("name")]     string Name
);

// ── Roles ─────────────────────────────────────────────────────────────────────

public record EsiRolesData(
    [property: JsonPropertyName("roles")]          List<string>? Roles,
    [property: JsonPropertyName("roles_at_hq")]    List<string>? RolesAtHq,
    [property: JsonPropertyName("roles_at_base")]  List<string>? RolesAtBase,
    [property: JsonPropertyName("roles_at_other")] List<string>? RolesAtOther
);

// ── Fittings ─────────────────────────────────────────────────────────────────

public record EsiFittingData(
    [property: JsonPropertyName("fitting_id")]  int                  FittingId,
    [property: JsonPropertyName("name")]        string               Name,
    [property: JsonPropertyName("description")] string               Description,
    [property: JsonPropertyName("ship_type_id")]int                  ShipTypeId,
    [property: JsonPropertyName("items")]       List<EsiFittingItem> Items
);

public record EsiFittingItem(
    [property: JsonPropertyName("type_id")]  int    TypeId,
    [property: JsonPropertyName("flag")]     string Flag,
    [property: JsonPropertyName("quantity")] int    Quantity
);

// ── Corp wallet ───────────────────────────────────────────────────────────────

public record EsiCorpWalletBalance(
    [property: JsonPropertyName("division")] int    Division,
    [property: JsonPropertyName("balance")]  double Balance
);

public record EsiCorpDivisionEntry(
    [property: JsonPropertyName("division")] int     Division,
    [property: JsonPropertyName("name")]     string? Name
);

public record EsiCorpDivisionsResponse(
    [property: JsonPropertyName("hangar")] List<EsiCorpDivisionEntry>? Hangar,
    [property: JsonPropertyName("wallet")] List<EsiCorpDivisionEntry>? Wallet
);

// ── Corp personnel ────────────────────────────────────────────────────────────

public record EsiCorpRoleEntry(
    [property: JsonPropertyName("character_id")]   long          CharacterId,
    [property: JsonPropertyName("roles")]          List<string>? Roles,
    [property: JsonPropertyName("roles_at_hq")]    List<string>? RolesAtHq,
    [property: JsonPropertyName("roles_at_base")]  List<string>? RolesAtBase,
    [property: JsonPropertyName("roles_at_other")] List<string>? RolesAtOther
);

public record EsiCorpTitleEntry(
    [property: JsonPropertyName("title_id")] int    TitleId,
    [property: JsonPropertyName("name")]     string Name
);

public record EsiCorpMedalEntry(
    [property: JsonPropertyName("medal_id")]    int            MedalId,
    [property: JsonPropertyName("title")]       string         Title,
    [property: JsonPropertyName("description")] string         Description,
    [property: JsonPropertyName("creator_id")]  long           CreatorId,
    [property: JsonPropertyName("created")]     DateTimeOffset CreatedAt
);

// ── Corp infrastructure ───────────────────────────────────────────────────────

public record EsiCorpStructureEntry(
    [property: JsonPropertyName("structure_id")]         long            StructureId,
    [property: JsonPropertyName("type_id")]              int             TypeId,
    [property: JsonPropertyName("system_id")]            int             SystemId,
    [property: JsonPropertyName("profile_id")]           int?            ProfileId,
    [property: JsonPropertyName("state")]                string          State,
    [property: JsonPropertyName("state_timer_start")]    DateTimeOffset? StateTimerStart,
    [property: JsonPropertyName("state_timer_end")]      DateTimeOffset? StateTimerEnd,
    [property: JsonPropertyName("unanchors_at")]         DateTimeOffset? UnanchorsAt,
    [property: JsonPropertyName("fuel_expires")]         DateTimeOffset? FuelExpires,
    [property: JsonPropertyName("next_reinforce_apply")] DateTimeOffset? NextReinforceApply,
    [property: JsonPropertyName("next_reinforce_hour")]  int?            NextReinforceHour,
    [property: JsonPropertyName("reinforce_hour")]       int?            ReinforceHour
);

public record EsiCorpStarbaseEntry(
    [property: JsonPropertyName("starbase_id")]      long            StarbaseId,
    [property: JsonPropertyName("type_id")]          int             TypeId,
    [property: JsonPropertyName("system_id")]        int             SystemId,
    [property: JsonPropertyName("moon_id")]          long            MoonId,
    [property: JsonPropertyName("state")]            string          State,
    [property: JsonPropertyName("unanchor_at")]      DateTimeOffset? UnanchorAt,
    [property: JsonPropertyName("reinforced_until")] DateTimeOffset? ReinforcedUntil,
    [property: JsonPropertyName("onlined_since")]    DateTimeOffset? OnlinedSince
);

public record EsiCorpFacilityEntry(
    [property: JsonPropertyName("facility_id")] long   FacilityId,
    [property: JsonPropertyName("type_id")]     int    TypeId,
    [property: JsonPropertyName("system_id")]   int    SystemId,
    [property: JsonPropertyName("region_id")]   int?   RegionId,
    [property: JsonPropertyName("tax_rate")]    float? TaxRate
);

// ── Corp mining ───────────────────────────────────────────────────────────────

public record EsiCorpMiningExtractionEntry(
    [property: JsonPropertyName("moon_id")]               long           MoonId,
    [property: JsonPropertyName("structure_id")]          long           StructureId,
    [property: JsonPropertyName("extraction_start_time")] DateTimeOffset ExtractionStartTime,
    [property: JsonPropertyName("chunk_arrival_time")]    DateTimeOffset ChunkArrivalTime,
    [property: JsonPropertyName("natural_decay_time")]    DateTimeOffset NaturalDecayTime
);

public record EsiCorpMiningObserverEntry(
    [property: JsonPropertyName("observer_id")]   long           ObserverId,
    [property: JsonPropertyName("observer_type")] string         ObserverType,
    [property: JsonPropertyName("last_updated")]  DateTimeOffset LastUpdated
);

public record EsiCorpMiningLedgerEntry(
    [property: JsonPropertyName("character_id")]            long           CharacterId,
    [property: JsonPropertyName("type_id")]                 int            TypeId,
    [property: JsonPropertyName("quantity")]                long           Quantity,
    [property: JsonPropertyName("recorded_corporation_id")] long           RecordedCorporationId,
    [property: JsonPropertyName("last_updated")]            DateTimeOffset LastUpdated
);

// ── Corp projects ─────────────────────────────────────────────────────────────

public record EsiProjectCursor(
    [property: JsonPropertyName("before")] string? Before,
    [property: JsonPropertyName("after")]  string? After
);

public record EsiProjectProgress(
    [property: JsonPropertyName("current")] long Current,
    [property: JsonPropertyName("desired")] long Desired
);

public record EsiProjectReward(
    [property: JsonPropertyName("initial")]   double Initial,
    [property: JsonPropertyName("remaining")] double Remaining
);

public record EsiCorpProjectEntry(
    [property: JsonPropertyName("id")]            string              Id,
    [property: JsonPropertyName("name")]          string              Name,
    [property: JsonPropertyName("state")]         string              State,
    [property: JsonPropertyName("last_modified")] DateTimeOffset      LastModified,
    [property: JsonPropertyName("progress")]      EsiProjectProgress? Progress,
    [property: JsonPropertyName("reward")]        EsiProjectReward?   Reward
);

public record EsiCorpProjectsPage(
    [property: JsonPropertyName("cursor")]   EsiProjectCursor?          Cursor,
    [property: JsonPropertyName("projects")] List<EsiCorpProjectEntry>? Projects
);

public record EsiProjectDetails(
    [property: JsonPropertyName("description")] string?        Description,
    [property: JsonPropertyName("career")]      string?        Career,
    [property: JsonPropertyName("created")]     DateTimeOffset Created
);

public record EsiProjectContribution(
    [property: JsonPropertyName("reward_per_contribution")] double RewardPerContribution
);

public record EsiProjectCreator(
    [property: JsonPropertyName("id")]   long   Id,
    [property: JsonPropertyName("name")] string Name
);

public record EsiCorpProjectDetail(
    [property: JsonPropertyName("id")]            string                  Id,
    [property: JsonPropertyName("name")]          string                  Name,
    [property: JsonPropertyName("state")]         string                  State,
    [property: JsonPropertyName("last_modified")] DateTimeOffset          LastModified,
    [property: JsonPropertyName("progress")]      EsiProjectProgress?     Progress,
    [property: JsonPropertyName("reward")]        EsiProjectReward?       Reward,
    [property: JsonPropertyName("details")]       EsiProjectDetails?      Details,
    [property: JsonPropertyName("contribution")]  EsiProjectContribution? Contribution,
    [property: JsonPropertyName("creator")]       EsiProjectCreator?      Creator,
    [property: JsonPropertyName("configuration")] JsonElement?            Configuration
);

public record EsiProjectContributorEntry(
    [property: JsonPropertyName("id")]          long   Id,
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("contributed")] long   Contributed
);

public record EsiCorpProjectContributorsPage(
    [property: JsonPropertyName("cursor")]       EsiProjectCursor?                 Cursor,
    [property: JsonPropertyName("contributors")] List<EsiProjectContributorEntry>? Contributors
);

// ── Market / location lookup ──────────────────────────────────────────────────

public record EsiStationDetail(
    [property: JsonPropertyName("station_id")]   long   StationId,
    [property: JsonPropertyName("name")]         string Name,
    [property: JsonPropertyName("system_id")]    int    SystemId
);

public record EsiStructureDetail(
    [property: JsonPropertyName("name")]          string Name,
    [property: JsonPropertyName("solar_system_id")] int  SolarSystemId
);

public record EsiLocationSearch(
    [property: JsonPropertyName("station")]   IReadOnlyList<long>? Station,
    [property: JsonPropertyName("structure")] IReadOnlyList<long>? Structure
);

// ── Market history ────────────────────────────────────────────────────────────

public record EsiMarketHistoryEntry(
    [property: JsonPropertyName("date")]        string Date,
    [property: JsonPropertyName("average")]     double Average,
    [property: JsonPropertyName("highest")]     double Highest,
    [property: JsonPropertyName("lowest")]      double Lowest,
    [property: JsonPropertyName("volume")]      long   Volume,
    [property: JsonPropertyName("order_count")] int    OrderCount
);

// ── Eve Mail ──────────────────────────────────────────────────────────────────

public record EsiMailRecipientItem(
    [property: JsonPropertyName("recipient_id")]   long   RecipientId,
    [property: JsonPropertyName("recipient_type")] string RecipientType
);

public record EsiMailListEntry(
    [property: JsonPropertyName("mail_id")]    int                         MailId,
    [property: JsonPropertyName("from")]       long                        From,
    [property: JsonPropertyName("is_read")]    bool?                       IsRead,
    [property: JsonPropertyName("labels")]     List<int>?                  Labels,
    [property: JsonPropertyName("recipients")] List<EsiMailRecipientItem>? Recipients,
    [property: JsonPropertyName("subject")]    string?                     Subject,
    [property: JsonPropertyName("timestamp")]  DateTimeOffset              Timestamp
);

public record EsiMailDetail(
    [property: JsonPropertyName("body")]       string?                     Body,
    [property: JsonPropertyName("from")]       long                        From,
    [property: JsonPropertyName("is_read")]    bool?                       IsRead,
    [property: JsonPropertyName("labels")]     List<int>?                  Labels,
    [property: JsonPropertyName("recipients")] List<EsiMailRecipientItem>? Recipients,
    [property: JsonPropertyName("subject")]    string?                     Subject,
    [property: JsonPropertyName("timestamp")]  DateTimeOffset              Timestamp
);

public record EsiMailLabelInfo(
    [property: JsonPropertyName("label_id")]     int     LabelId,
    [property: JsonPropertyName("name")]         string? Name,
    [property: JsonPropertyName("color")]        string? Color,
    [property: JsonPropertyName("unread_count")] int?    UnreadCount
);

public record EsiMailLabelsWrapper(
    [property: JsonPropertyName("labels")]             List<EsiMailLabelInfo>? Labels,
    [property: JsonPropertyName("total_unread_count")] int?                   TotalUnreadCount
);
