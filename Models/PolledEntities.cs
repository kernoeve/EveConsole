namespace EveConsole.Models;

// ── Polling tracking ─────────────────────────────────────────────────────────

public class ApiCallRecord
{
    public long   OwnerId      { get; set; }
    public string OwnerType    { get; set; } = "";   // "character" or "corp"
    public string Endpoint     { get; set; } = "";
    public DateTimeOffset LastCalledAt   { get; set; }
    public int    LastStatusCode { get; set; } = 200;
}

// ── Single-row-per-character ─────────────────────────────────────────────────

public class CharacterWalletBalance
{
    public long    OwnerId   { get; set; }
    public string  OwnerType { get; set; } = "";
    public int     Division  { get; set; }   // 0 = character (single wallet), 1–7 = corp division
    public decimal Balance   { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class StoredCharacterAttributes
{
    public long   CharacterId { get; set; }
    public int    Charisma    { get; set; }
    public int    Intelligence { get; set; }
    public int    Memory      { get; set; }
    public int    Perception  { get; set; }
    public int    Willpower   { get; set; }
    public int    BonusRemaps { get; set; }
    public DateTimeOffset? LastRemapDate             { get; set; }
    public DateTimeOffset? AccruingRemapCooldownDate { get; set; }
    public DateTimeOffset  UpdatedAt { get; set; }
}

public class CharacterCloneState
{
    public long   CharacterId           { get; set; }
    public long?  HomeLocationId        { get; set; }
    public string? HomeLocationType     { get; set; }
    public DateTimeOffset? LastCloneJumpDate     { get; set; }
    public DateTimeOffset? LastStationChangeDate { get; set; }
    public DateTimeOffset  UpdatedAt { get; set; }
}

public class StoredCharacterFatigue
{
    public long   CharacterId                    { get; set; }
    public DateTimeOffset? LastJumpDate          { get; set; }
    public DateTimeOffset? JumpFatigueExpireDate { get; set; }
    public DateTimeOffset? LastUpdateDate        { get; set; }
    public DateTimeOffset  UpdatedAt { get; set; }
}

// ── Skills ───────────────────────────────────────────────────────────────────

public class StoredSkill
{
    public long CharacterId        { get; set; }
    public int  SkillId            { get; set; }
    public int  TrainedSkillLevel  { get; set; }
    public int  ActiveSkillLevel   { get; set; }
    public long SkillpointsInSkill { get; set; }
}

public class StoredSkillQueueEntry
{
    public long   CharacterId    { get; set; }
    public int    QueuePosition  { get; set; }
    public int    SkillId        { get; set; }
    public int    FinishedLevel  { get; set; }
    public int    TrainingStartSp { get; set; }
    public int    LevelStartSp   { get; set; }
    public int    LevelEndSp     { get; set; }
    public DateTimeOffset? StartDate  { get; set; }
    public DateTimeOffset? FinishDate { get; set; }
}

// ── Clones & implants ─────────────────────────────────────────────────────────

public class StoredJumpClone
{
    public int    JumpCloneId  { get; set; }
    public long   CharacterId  { get; set; }
    public long   LocationId   { get; set; }
    public string LocationType { get; set; } = "";
    public string? Name        { get; set; }
}

public class StoredJumpCloneImplant
{
    public int JumpCloneId { get; set; }
    public int TypeId      { get; set; }
}

public class StoredImplant
{
    public long CharacterId { get; set; }
    public int  TypeId      { get; set; }
}

// ── Wallet ───────────────────────────────────────────────────────────────────

public class WalletJournalEntry
{
    public long   EsiId       { get; set; }   // ESI "id" field
    public long   OwnerId     { get; set; }
    public string OwnerType   { get; set; } = "";
    public int?   Division    { get; set; }   // corp wallet division, null for chars
    public DateTimeOffset Date { get; set; }
    public string  RefType    { get; set; } = "";
    public long?   FirstPartyId  { get; set; }
    public long?   SecondPartyId { get; set; }
    public decimal Amount        { get; set; }
    public decimal Balance       { get; set; }
    public string? Description   { get; set; }
    public string? Reason        { get; set; }
    public decimal? Tax          { get; set; }
    public long?   TaxReceiverId { get; set; }
    public long?   ContextId     { get; set; }
    public string? ContextIdType { get; set; }
}

public class WalletTransaction
{
    public long   TransactionId { get; set; }
    public long   OwnerId       { get; set; }
    public string OwnerType     { get; set; } = "";
    public int?   Division      { get; set; }
    public DateTimeOffset Date  { get; set; }
    public long   ClientId      { get; set; }
    public long   LocationId    { get; set; }
    public int    Quantity      { get; set; }
    public int    TypeId        { get; set; }
    public decimal UnitPrice    { get; set; }
    public bool   IsBuy         { get; set; }
    public bool   IsPersonal    { get; set; }
    public long   JournalRefId  { get; set; }
}

// ── Industry ─────────────────────────────────────────────────────────────────

public class IndustryJob
{
    public int    JobId               { get; set; }
    public long   OwnerId             { get; set; }
    public string OwnerType           { get; set; } = "";
    public long   InstallerId         { get; set; }
    public long   FacilityId          { get; set; }
    public long   StationId           { get; set; }
    public int    ActivityId          { get; set; }
    public long   BlueprintId         { get; set; }
    public int    BlueprintTypeId     { get; set; }
    public long   BlueprintLocationId { get; set; }
    public long   OutputLocationId    { get; set; }
    public int    Runs                { get; set; }
    public decimal Cost               { get; set; }
    public int?   LicensedRuns        { get; set; }
    public float? Probability         { get; set; }
    public int?   ProductTypeId       { get; set; }
    public string Status              { get; set; } = "";
    public int    Duration            { get; set; }
    public DateTimeOffset  StartDate  { get; set; }
    public DateTimeOffset  EndDate    { get; set; }
    public DateTimeOffset? PauseDate  { get; set; }
    public DateTimeOffset? CompletedDate        { get; set; }
    public long?  CompletedCharacterId          { get; set; }
    public int?   SuccessfulRuns      { get; set; }
}

// ── Market orders ─────────────────────────────────────────────────────────────

public class MarketOrder
{
    public long   OrderId      { get; set; }
    public long   OwnerId      { get; set; }
    public string OwnerType    { get; set; } = "";
    public int    TypeId       { get; set; }
    public long   LocationId   { get; set; }
    public int    VolumeTotal  { get; set; }
    public int    VolumeRemain { get; set; }
    public int    MinVolume    { get; set; }
    public decimal Price       { get; set; }
    public bool   IsBuyOrder   { get; set; }
    public int    Duration     { get; set; }
    public DateTimeOffset Issued { get; set; }
    public string Range        { get; set; } = "";
    public decimal? Escrow     { get; set; }
    public bool?  IsCorporation { get; set; }
    public int?   RegionId     { get; set; }
    public string? State       { get; set; }
    public bool   IsHistory    { get; set; }
}

// ── Contracts ─────────────────────────────────────────────────────────────────

public class ContractRecord
{
    public int    ContractId          { get; set; }
    public long   OwnerId             { get; set; }
    public string OwnerType           { get; set; } = "";
    public long   IssuerId            { get; set; }
    public int    IssuerCorporationId { get; set; }
    public long?  AssigneeId          { get; set; }
    public long?  AcceptorId          { get; set; }
    public long?  StartLocationId     { get; set; }
    public long?  EndLocationId       { get; set; }
    public string Type                { get; set; } = "";
    public string Status              { get; set; } = "";
    public string? Title              { get; set; }
    public bool   ForCorporation      { get; set; }
    public string Availability        { get; set; } = "";
    public DateTimeOffset  DateIssued    { get; set; }
    public DateTimeOffset? DateExpired   { get; set; }
    public DateTimeOffset? DateAccepted  { get; set; }
    public DateTimeOffset? DateCompleted { get; set; }
    public int    DaysToComplete      { get; set; }
    public decimal Price              { get; set; }
    public decimal Reward             { get; set; }
    public decimal Collateral         { get; set; }
    public decimal Buyout             { get; set; }
    public decimal Volume             { get; set; }

    // Region a public contract was listed in (0 for character/corp contracts).
    public int    RegionId            { get; set; }
    // True once the contract's item list has been fetched (item_exchange/auction/courier
    // contracts have items; we pull them once per contract and never call again).
    public bool   ItemsPulled         { get; set; }
}

// One line item on a contract (offered or requested). Shared across owner rows by ContractId.
public class ContractItem
{
    public int    ContractId       { get; set; }
    public long   RecordId         { get; set; }   // unique per item within the contract
    public int    TypeId           { get; set; }
    public long   Quantity         { get; set; }
    public bool   IsIncluded       { get; set; }   // true = offered by issuer, false = requested
    public bool   IsSingleton      { get; set; }
    public int?   RawQuantity      { get; set; }   // negative encodes BPC etc.
    // Public-contract extras (blueprint details); null for personal/corp items.
    public bool?  IsBlueprintCopy    { get; set; }
    public int?   MaterialEfficiency { get; set; }
    public int?   TimeEfficiency     { get; set; }
    public int?   Runs               { get; set; }
}

// Per-(owner, wallet division, kind) marker: true once a full page-through of the ESI wallet
// window has completed without interruption. While false (first run, or after a poll that was cut
// short) the fetch re-pages the entire window to fill any hole a partial poll may have left, rather
// than stopping at the first already-stored page. Division 0 = character single wallet.
public class WalletBackfillState
{
    public long   OwnerId   { get; set; }
    public string OwnerType { get; set; } = "";
    public string Kind      { get; set; } = "";   // "journal" | "transactions"
    public int    Division  { get; set; }
    public bool   Complete  { get; set; }
}

// Derived per-type pricing from single-item-type "sell" contracts (item_exchange offering one
// item type for an ISK price, nothing requested back). Rebuilt periodically from EsiContracts +
// EsiContractItems; the table is fully replaced each run. Prices are per unit (contract price ÷
// number of units of the single type). One row per TypeId.
public class ContractPrice
{
    public int      TypeId      { get; set; }   // key
    public decimal? BestPrice   { get; set; }   // lowest per-unit ask among currently-active sells
    public decimal? Avg30Best   { get; set; }   // 30-day average of the daily-best per-unit price
    public int      ActiveCount { get; set; }   // # currently-active qualifying sell contracts
    public int      SampleDays  { get; set; }   // days in the last 30 that had ≥1 active contract
    public DateTimeOffset UpdatedAt { get; set; }
}

// Per-run BPC contract price, keyed by (blueprint TypeId, ME). Unlike ContractPrice (a per-unit
// finished-item price), this divides a BPC contract's ask by the copy's runs so consumers can
// cost a single build run, and separates ME levels (which change material cost — matters for
// titans/supers). BestPerRun / Avg30PerRun mirror ContractPrice's best-vs-30-day-average rule.
public class ContractBpcPrice
{
    public int      TypeId      { get; set; }   // blueprint type id  (key part 1)
    public int      Me          { get; set; }   // material efficiency (key part 2)
    public decimal? BestPerRun  { get; set; }
    public decimal? Avg30PerRun { get; set; }
    public int      ActiveCount { get; set; }
    public int      SampleDays  { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// Manual, user-supplied price corrections for a single type. Each field is null unless the user
// has explicitly pinned it; a non-null value REPLACES the computed value in its channel:
//   BuildCost     → the item's build cost (cheaper-of still applies vs the market buy price)
//   MarketValue   → the item's market price (buy/build comparison, raw-material valuation)
//   ContractValue → the item's contract price; for a BLUEPRINT type it is the PER-RUN BPC price
// Used to work around contract/market manipulation (e.g. someone milking single-item BPC contracts).
public class PriceOverride
{
    public int      TypeId        { get; set; }   // key
    public string   TypeName      { get; set; } = "";
    public decimal? BuildCost     { get; set; }
    public decimal? MarketValue   { get; set; }
    public decimal? ContractValue { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ── Assets & blueprints ───────────────────────────────────────────────────────

public class CharacterAsset
{
    public long   ItemId       { get; set; }
    public long   OwnerId      { get; set; }
    public string OwnerType    { get; set; } = "";
    public int    TypeId       { get; set; }
    public long   LocationId   { get; set; }
    public string LocationType { get; set; } = "";
    public string LocationFlag { get; set; } = "";
    public int    Quantity     { get; set; }
    public bool   IsSingleton  { get; set; }
    public bool?  IsBlueprintCopy { get; set; }
    // Denormalised root location — the terminal station/structure/system reached by
    // walking the LocationId parent chain. Computed at insert time in C# so every
    // downstream query (asset browser, aggregations, etc.) can skip the chain walk.
    public long   RootLocationId   { get; set; }
    public string RootLocationType { get; set; } = "";
}

public class CharacterBlueprint
{
    public long   ItemId             { get; set; }
    public long   OwnerId            { get; set; }
    public string OwnerType          { get; set; } = "";
    public int    TypeId             { get; set; }
    public long   LocationId         { get; set; }
    public string LocationFlag       { get; set; } = "";
    public int    Quantity           { get; set; }
    public int    TimeEfficiency     { get; set; }
    public int    MaterialEfficiency { get; set; }
    public int    Runs               { get; set; }
}

// ── Misc character data ───────────────────────────────────────────────────────

public class CharacterMiningEntry
{
    public long   CharacterId   { get; set; }
    public string Date          { get; set; } = "";
    public int    SolarSystemId { get; set; }
    public int    TypeId        { get; set; }
    public long   Quantity      { get; set; }
}

public class CharacterNotification
{
    public long   NotificationId { get; set; }
    public long   CharacterId    { get; set; }
    public string Type           { get; set; } = "";
    public long   SenderId       { get; set; }
    public string SenderType     { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public bool   IsRead         { get; set; }
    public string? Text          { get; set; }
}

public class DismissedAlert
{
    public long CharacterId    { get; set; }
    public long NotificationId { get; set; }
}

public class ContactEntry
{
    public long   OwnerId     { get; set; }
    public string OwnerType   { get; set; } = "";
    public long   ContactId   { get; set; }
    public string ContactType { get; set; } = "";
    public float  Standing    { get; set; }
    public bool   IsWatched   { get; set; }
    public bool   IsBlocked   { get; set; }
}

public class KillMailRef
{
    public long   OwnerId      { get; set; }
    public string OwnerType    { get; set; } = "";
    public int    KillMailId   { get; set; }
    public string KillMailHash { get; set; } = "";
}

public class KillMailDetail
{
    public int            KillMailId        { get; set; }
    public string         KillMailHash      { get; set; } = "";
    public DateTimeOffset KillMailTime      { get; set; }
    public int            SolarSystemId     { get; set; }
    public int?           MoonId            { get; set; }
    public int?           WarId             { get; set; }
    public long           VictimCharId      { get; set; } // 0 when NPC
    public long           VictimCorpId      { get; set; }
    public long?          VictimAllianceId  { get; set; }
    public int?           VictimFactionId   { get; set; }
    public int            VictimShipTypeId  { get; set; }
    public int            VictimDamageTaken { get; set; }
    public double?        VictimPosX        { get; set; }
    public double?        VictimPosY        { get; set; }
    public double?        VictimPosZ        { get; set; }
}

public class KillMailAttacker
{
    public long  Id             { get; set; }
    public int   KillMailId     { get; set; }
    public long? CharacterId    { get; set; }
    public long? CorporationId  { get; set; }
    public long? AllianceId     { get; set; }
    public int?  FactionId      { get; set; }
    public int   DamageDone     { get; set; }
    public bool  FinalBlow      { get; set; }
    public float SecurityStatus { get; set; }
    public int?  ShipTypeId     { get; set; }
    public int?  WeaponTypeId   { get; set; }
}

public class KillMailItem
{
    public long  Id                { get; set; }
    public int   KillMailId        { get; set; }
    public int   Flag              { get; set; }
    public int   ItemTypeId        { get; set; }
    public long? QuantityDestroyed { get; set; }
    public long? QuantityDropped   { get; set; }
    public int   Singleton         { get; set; }
}

public class PlanetaryColony
{
    public long   CharacterId   { get; set; }
    public int    PlanetId      { get; set; }
    public string PlanetType    { get; set; } = "";
    public int    SolarSystemId { get; set; }
    public DateTimeOffset LastUpdate { get; set; }
    public int    NumPins       { get; set; }
    public int    UpgradeLevel  { get; set; }
}

public class AgentResearch
{
    public long  CharacterId    { get; set; }
    public int   AgentId        { get; set; }
    public int   SkillTypeId    { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public float PointsPerDay   { get; set; }
    public float RemainderPoints { get; set; }
}

public class LoyaltyPoint
{
    public long CharacterId   { get; set; }
    public int  CorporationId { get; set; }
    public int  Points        { get; set; }
}

public class CharacterMedal
{
    public int    Id            { get; set; }  // auto-increment
    public long   CharacterId   { get; set; }
    public int    MedalId       { get; set; }
    public int    CorporationId { get; set; }
    public long   IssuerId      { get; set; }
    public DateTimeOffset Date  { get; set; }
    public string Reason        { get; set; } = "";
    public string Status        { get; set; } = "";
}

public class StandingEntry
{
    public long   OwnerId   { get; set; }
    public string OwnerType { get; set; } = "";
    public long   FromId    { get; set; }
    public string FromType  { get; set; } = "";
    public float  Standing  { get; set; }
}

public class CharacterTitle
{
    public long   CharacterId { get; set; }
    public int    TitleId     { get; set; }
    public string Name        { get; set; } = "";
}

public class CharacterRole
{
    public long   CharacterId { get; set; }
    public string Role        { get; set; } = "";
    public string RoleType    { get; set; } = "";
}

public class StoredFitting
{
    public int    FittingId   { get; set; }
    public long   CharacterId { get; set; }
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
    public int    ShipTypeId  { get; set; }
}

public class FittingItem
{
    public int    Id        { get; set; }  // auto-increment
    public int    FittingId { get; set; }
    public int    TypeId    { get; set; }
    public string Flag      { get; set; } = "";
    public int    Quantity  { get; set; }
}

// ── Corp divisions (wallet/hangar names) ──────────────────────────────────────

public class CorpDivision
{
    public long   CorporationId { get; set; }
    public int    Division      { get; set; }    // 1–7
    public string DivisionType  { get; set; } = ""; // "wallet" or "hangar"
    public string Name          { get; set; } = "";
}

// ── Corp membership & roles ───────────────────────────────────────────────────

public class CorpMember
{
    public long CorporationId { get; set; }
    public long CharacterId   { get; set; }
}

public class CorpMemberRole
{
    public long   CorporationId { get; set; }
    public long   CharacterId   { get; set; }
    public string Role          { get; set; } = "";
    public string RoleType      { get; set; } = "";
}

public class CorpTitle
{
    public long   CorporationId { get; set; }
    public int    TitleId       { get; set; }
    public string Name          { get; set; } = "";
}

public class CorpMedal
{
    public long   CorporationId { get; set; }
    public int    MedalId       { get; set; }
    public string Title         { get; set; } = "";
    public string Description   { get; set; } = "";
    public long   CreatorId     { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// ── Corp infrastructure ───────────────────────────────────────────────────────

public class CorpStructure
{
    public long   CorporationId      { get; set; }
    public long   StructureId        { get; set; }
    public string Name               { get; set; } = "";
    public int    TypeId             { get; set; }
    public int    SystemId           { get; set; }
    public int?   ProfileId          { get; set; }
    public string State              { get; set; } = "";
    public DateTimeOffset? StateTimerStart    { get; set; }
    public DateTimeOffset? StateTimerEnd      { get; set; }
    public DateTimeOffset? UnanchorsAt        { get; set; }
    public DateTimeOffset? FuelExpires        { get; set; }
    public DateTimeOffset? NextReinforceApply { get; set; }
    public int?   NextReinforceHour  { get; set; }
    public int?   ReinforceHour      { get; set; }
}

// Universal cache of player-structure names resolved via /universe/structures/{id}/
public class StructureName
{
    public long          StructureId   { get; set; }
    public string        Name          { get; set; } = "";
    public int           SolarSystemId { get; set; }
    public DateTimeOffset PulledAt     { get; set; }
}

// Structures whose name could not be resolved (no docking rights = 403, or gone = 404).
// Used to stop re-polling them every cycle; retried only after a backoff period.
public class StructureNameFailure
{
    public long           StructureId { get; set; }
    public DateTimeOffset FailedAt    { get; set; }
    public int            StatusCode  { get; set; }
}

public class CorpStarbase
{
    public long   CorporationId   { get; set; }
    public long   StarbaseId      { get; set; }
    public int    TypeId          { get; set; }
    public int    SystemId        { get; set; }
    public long   MoonId          { get; set; }
    public string State           { get; set; } = "";
    public DateTimeOffset? UnanchorAt      { get; set; }
    public DateTimeOffset? ReinforcedUntil { get; set; }
    public DateTimeOffset? OnlinedSince    { get; set; }
}

public class CorpFacility
{
    public long   CorporationId { get; set; }
    public long   FacilityId   { get; set; }
    public int    TypeId       { get; set; }
    public int    SystemId     { get; set; }
    public int?   RegionId     { get; set; }
    public float? TaxRate      { get; set; }
}

// ── Corp mining ───────────────────────────────────────────────────────────────

public class CorpMiningExtraction
{
    public long   CorporationId       { get; set; }
    public long   MoonId              { get; set; }
    public long   StructureId         { get; set; }
    public DateTimeOffset ExtractionStartTime { get; set; }
    public DateTimeOffset ChunkArrivalTime    { get; set; }
    public DateTimeOffset NaturalDecayTime    { get; set; }
}

public class CorpMiningObserver
{
    public long   CorporationId { get; set; }
    public long   ObserverId    { get; set; }
    public string ObserverType  { get; set; } = "";
    public DateTimeOffset LastUpdated { get; set; }
}

public class CorpMiningLedgerEntry
{
    public long   CorporationId         { get; set; }
    public long   ObserverId            { get; set; }
    public long   CharacterId           { get; set; }
    public int    TypeId                { get; set; }
    public long   Quantity              { get; set; }
    public long   RecordedCorporationId { get; set; }
    public DateTimeOffset LastUpdated   { get; set; }
}

// ── Corp projects ────────────────────────────────────────────────────────────

public class CorpProject
{
    public long   CorporationId   { get; set; }
    public string ProjectId       { get; set; } = "";  // UUID string from ESI
    public string Name            { get; set; } = "";
    public string State           { get; set; } = "";
    public DateTimeOffset LastModified   { get; set; }
    public long   ProgressCurrent { get; set; }
    public long   ProgressDesired { get; set; }
    public double RewardInitial   { get; set; }
    public double RewardRemaining { get; set; }
    public string Description     { get; set; } = "";
    public string Career          { get; set; } = "";
    public DateTimeOffset? Created { get; set; }
    public double RewardPerContrib { get; set; }
    public long?  CreatorId       { get; set; }
    public string CreatorName     { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    // True once a terminal-state project has had both detail + all contributors successfully fetched.
    // Static projects are never re-fetched for detail/contributors — only list-level fields update.
    public bool    IsStatic          { get; set; }
    // True when the project appears in the list but its detail endpoint returns 404 (detail not
    // available to us). We keep updating cheap list fields but stop retrying the detail call.
    public bool    DetailUnavailable { get; set; }
    public string? ConfigType        { get; set; }  // e.g. "deliver_item"
    public string? ConfigurationJson { get; set; }
}

public class CorpProjectContributor
{
    public long   CorporationId { get; set; }
    public string ProjectId     { get; set; } = "";  // UUID string
    public long   CharacterId   { get; set; }
    public string Name          { get; set; } = "";
    public long   Contributed   { get; set; }
}

// ── Standing projects (operator-defined repeating projects) ──────────────────

public class CorpStandingProject
{
    public long   Id              { get; set; }
    public long   CorporationId   { get; set; }
    public string ProjectType     { get; set; } = "destroy_npc";  // "deliver_item" | "destroy_npc"
    // deliver_item fields
    public int?   ItemTypeId      { get; set; }
    public string ItemTypeName    { get; set; } = "";
    public long?  StationId       { get; set; }
    public string StationName     { get; set; } = "";
    // destroy_npc fields
    public string ScopeType       { get; set; } = "system";  // "system" | "region_adm" | "constellation_adm"
    public int?   SolarSystemId   { get; set; }
    public string SolarSystemName { get; set; } = "";
    public int?   ScopeEntityId   { get; set; }   // region or constellation ID for ADM scopes
    public string ScopeEntityName { get; set; } = "";
    public double? MinAdm         { get; set; }   // minimum ADM threshold for ADM scopes
    public DateTimeOffset CreatedAt { get; set; }
}

// ── Corp Top 10 exclude list ──────────────────────────────────────────────────

public class CorpTop10Exclude
{
    public long   EntityId   { get; set; }
    public string EntityType { get; set; } = "";  // "character" or "corporation"
    public string EntityName { get; set; } = "";
}

// ── Net worth history ─────────────────────────────────────────────────────────

public class NetWorthSnapshot
{
    public long   OwnerId            { get; set; }
    public string OwnerType          { get; set; } = "";  // "character" or "corporation"
    public string Date               { get; set; } = "";  // "yyyy-MM-dd" UTC
    public double AssetValue         { get; set; }
    public double IndustryJobValue   { get; set; }
    public double WalletBalance      { get; set; }
    public double SellOrderValue     { get; set; }
    public double BuyOrderEscrow     { get; set; }
    public double ContractCollateral { get; set; }
    public double ContractValue      { get; set; }
    public double Total              { get; set; }
    public DateTimeOffset ComputedAt { get; set; }
}

// ── Per-type price history ──────────────────────────────────────────────────────

// One row per TypeId per UTC day. Like NetWorthSnapshot, the current day's row is
// recomputed and overwritten as prices refresh; once the day rolls over the prior
// day's values are frozen, giving a point-in-time view of each price. Values are
// nullable — null means "no price of that kind on that day", distinct from 0.
public class TypePriceSnapshot
{
    public int    TypeId        { get; set; }
    public string Date          { get; set; } = "";  // "yyyy-MM-dd" UTC
    public double? MarketValue   { get; set; }        // from the asset-value market config + price type
    public double? BuildCost     { get; set; }        // BuildCosts.TotalCost
    public double? ContractPrice { get; set; }        // ContractPricing.EffectivePrice
    public DateTimeOffset ComputedAt { get; set; }
}

// ── Order Tracker (user-entered outgoing orders) ────────────────────────────────

// A user-tracked order: an item the user has agreed to supply to someone. Entirely user-entered
// (not from ESI). Status moves pending → completed/canceled.
public class TrackedOrder
{
    public int    Id            { get; set; }   // autoincrement
    public int    TypeId        { get; set; }
    public int    Units         { get; set; } = 1;
    public string Buyer         { get; set; } = "";   // who the item is sent to (free text)
    public string? EstimatedDate { get; set; }        // "yyyy-MM-dd", user's estimate
    public double PurchasePrice { get; set; }         // total agreed price for the order
    public string Status        { get; set; } = "pending";   // pending | completed | canceled
    public DateTimeOffset CreatedAt { get; set; }
}

// ── Error logging ─────────────────────────────────────────────────────────────

public class AppErrorEntry
{
    public int    Id           { get; set; }
    public DateTimeOffset OccurredAt  { get; set; }
    public string Source      { get; set; } = "";
    public string Context     { get; set; } = "";
    public string Message     { get; set; } = "";
    public string? InnerMessage { get; set; }
}
