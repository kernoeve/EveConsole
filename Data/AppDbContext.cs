using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // ── Dynamic / user data ──────────────────────────────────────────────
    public DbSet<Character>   Characters   => Set<Character>();
    public DbSet<Corporation> Corporations => Set<Corporation>();

    // ── ESI polling tracking ─────────────────────────────────────────────
    public DbSet<ApiCallRecord>              EsiCallRecords          => Set<ApiCallRecord>();
    public DbSet<ApiTimerSetting>            ApiTimerSettings        => Set<ApiTimerSetting>();

    // ── Polled character data ─────────────────────────────────────────────
    public DbSet<CharacterWalletBalance>     EsiWalletBalances       => Set<CharacterWalletBalance>();
    public DbSet<StoredCharacterAttributes>  EsiCharacterAttributes  => Set<StoredCharacterAttributes>();
    public DbSet<CharacterCloneState>        EsiCloneStates          => Set<CharacterCloneState>();
    public DbSet<StoredCharacterFatigue>     EsiCharacterFatigues    => Set<StoredCharacterFatigue>();
    public DbSet<StoredSkill>                EsiSkills               => Set<StoredSkill>();
    public DbSet<StoredSkillQueueEntry>      EsiSkillQueue           => Set<StoredSkillQueueEntry>();
    public DbSet<StoredJumpClone>            EsiJumpClones           => Set<StoredJumpClone>();
    public DbSet<StoredJumpCloneImplant>     EsiJumpCloneImplants    => Set<StoredJumpCloneImplant>();
    public DbSet<StoredImplant>              EsiImplants             => Set<StoredImplant>();
    public DbSet<WalletJournalEntry>         EsiWalletJournal        => Set<WalletJournalEntry>();
    public DbSet<WalletTransaction>          EsiWalletTransactions   => Set<WalletTransaction>();
    public DbSet<IndustryJob>                EsiIndustryJobs         => Set<IndustryJob>();
    public DbSet<MarketOrder>                EsiMarketOrders         => Set<MarketOrder>();
    public DbSet<ContractRecord>             EsiContracts            => Set<ContractRecord>();
    public DbSet<ContractItem>               EsiContractItems        => Set<ContractItem>();
    public DbSet<ContractPrice>              ContractPrices          => Set<ContractPrice>();
    public DbSet<ContractBpcPrice>           ContractBpcPrices       => Set<ContractBpcPrice>();
    public DbSet<PriceOverride>              PriceOverrides          => Set<PriceOverride>();
    public DbSet<WalletBackfillState>        WalletBackfillStates    => Set<WalletBackfillState>();
    public DbSet<UniverseName>               UniverseNames           => Set<UniverseName>();
    public DbSet<CharacterAsset>             EsiAssets               => Set<CharacterAsset>();
    public DbSet<CharacterBlueprint>         EsiBlueprints           => Set<CharacterBlueprint>();
    public DbSet<CharacterMiningEntry>       EsiMining               => Set<CharacterMiningEntry>();
    public DbSet<CharacterNotification>      EsiNotifications        => Set<CharacterNotification>();
    public DbSet<DismissedAlert>             DismissedAlerts         => Set<DismissedAlert>();
    public DbSet<ContactEntry>               EsiContacts             => Set<ContactEntry>();
    public DbSet<KillMailRef>                EsiKillMailRefs         => Set<KillMailRef>();
    public DbSet<KillMailDetail>             KillMailDetails         => Set<KillMailDetail>();
    public DbSet<ZkbKillFlag>                ZkbKillFlags            => Set<ZkbKillFlag>();
    public DbSet<KillMailAttacker>           KillMailAttackers       => Set<KillMailAttacker>();
    public DbSet<KillMailItem>               KillMailItems           => Set<KillMailItem>();
    public DbSet<PlanetaryColony>            EsiPlanetaryColonies    => Set<PlanetaryColony>();
    public DbSet<AgentResearch>              EsiAgentResearch        => Set<AgentResearch>();
    public DbSet<LoyaltyPoint>               EsiLoyaltyPoints        => Set<LoyaltyPoint>();
    public DbSet<LpStoreOffer>               EsiLpStoreOffers        => Set<LpStoreOffer>();
    public DbSet<LpStoreOfferItem>           EsiLpStoreOfferItems    => Set<LpStoreOfferItem>();
    public DbSet<LpStoreCorp>                EsiLpStoreCorps         => Set<LpStoreCorp>();
    public DbSet<LpCorpValue>                LpCorpValues            => Set<LpCorpValue>();
    public DbSet<LpCorpValueSnapshot>        LpCorpValueSnapshots    => Set<LpCorpValueSnapshot>();
    public DbSet<CharacterMedal>             EsiMedals               => Set<CharacterMedal>();
    public DbSet<StandingEntry>              EsiStandings            => Set<StandingEntry>();
    public DbSet<CharacterTitle>             EsiTitles               => Set<CharacterTitle>();
    public DbSet<CharacterRole>              EsiRoles                => Set<CharacterRole>();
    public DbSet<StoredFitting>              EsiFittings             => Set<StoredFitting>();
    public DbSet<FittingItem>                EsiFittingItems         => Set<FittingItem>();

    // ── Polled corporation data ───────────────────────────────────────────
    public DbSet<CorpDivision>          EsiCorpDivisions         => Set<CorpDivision>();
    public DbSet<CorpMember>            EsiCorpMembers           => Set<CorpMember>();
    public DbSet<CorpMemberTracking>    EsiCorpMemberTracking    => Set<CorpMemberTracking>();
    public DbSet<CorpMemberSession>     EsiCorpMemberSessions    => Set<CorpMemberSession>();
    public DbSet<CorpMemberRole>        EsiCorpMemberRoles       => Set<CorpMemberRole>();
    public DbSet<CorpTitle>             EsiCorpTitles            => Set<CorpTitle>();
    public DbSet<CorpMedal>             EsiCorpMedals            => Set<CorpMedal>();
    public DbSet<CorpStructure>         EsiCorpStructures        => Set<CorpStructure>();
    public DbSet<StructureName>         EsiStructureNames        => Set<StructureName>();
    public DbSet<StructureNameFailure>  EsiStructureNameFailures => Set<StructureNameFailure>();
    public DbSet<CorpStarbase>          EsiCorpStarbases         => Set<CorpStarbase>();
    public DbSet<CorpFacility>          EsiCorpFacilities        => Set<CorpFacility>();
    public DbSet<CorpMiningExtraction>  EsiCorpMiningExtractions => Set<CorpMiningExtraction>();
    public DbSet<CorpMiningObserver>    EsiCorpMiningObservers   => Set<CorpMiningObserver>();
    public DbSet<CorpMiningLedgerEntry> EsiCorpMiningLedger      => Set<CorpMiningLedgerEntry>();
    public DbSet<CorpProject>            EsiCorpProjects            => Set<CorpProject>();
    public DbSet<CorpProjectContributor> EsiCorpProjectContributors => Set<CorpProjectContributor>();
    public DbSet<CorpTop10Exclude>       CorpTop10Excludes          => Set<CorpTop10Exclude>();
    public DbSet<CorpStandingProject>    CorpStandingProjects       => Set<CorpStandingProject>();

    // ── Eve Mail ─────────────────────────────────────────────────────────────
    public DbSet<EveMailHeader>         EsiMailHeaders    => Set<EveMailHeader>();
    public DbSet<EveMailBody>           EsiMailBodies     => Set<EveMailBody>();
    public DbSet<EveMailRecipientEntry> EsiMailRecipients => Set<EveMailRecipientEntry>();
    public DbSet<EveMailLabelEntry>     EsiMailLabels     => Set<EveMailLabelEntry>();

    // ── Net worth history ────────────────────────────────────────────────
    public DbSet<NetWorthSnapshot> NetWorthSnapshots => Set<NetWorthSnapshot>();

    // ── Per-type price history ───────────────────────────────────────────
    public DbSet<TypePriceSnapshot> TypePriceSnapshots => Set<TypePriceSnapshot>();

    // ── Order Tracker (user-entered) ─────────────────────────────────────
    public DbSet<TrackedOrder> TrackedOrders => Set<TrackedOrder>();

    // ── Standing buy orders (user-defined intent) ────────────────────────
    public DbSet<StandingBuyOrder> StandingBuyOrders => Set<StandingBuyOrder>();
    public DbSet<WorklistMarketAlt>      WorklistMarketAlts      => Set<WorklistMarketAlt>();
    public DbSet<WorklistInvRule>   WorklistInvRules   => Set<WorklistInvRule>();
    public DbSet<WorklistCorpAlt>   WorklistCorpAlts   => Set<WorklistCorpAlt>();
    public DbSet<WorklistIndyChar>  WorklistIndyChars  => Set<WorklistIndyChar>();
    public DbSet<WorklistItemState> WorklistItemStates => Set<WorklistItemState>();
    public DbSet<WorklistIndyScopeStation> WorklistIndyScopeStations => Set<WorklistIndyScopeStation>();
    public DbSet<WorklistStationLevel>     WorklistStationLevels     => Set<WorklistStationLevel>();

    // ── Application error log ────────────────────────────────────────────
    public DbSet<AppErrorEntry> AppErrors => Set<AppErrorEntry>();

    // ── Client activity monitoring ───────────────────────────────────────
    public DbSet<CharacterStatus> CharacterStatuses => Set<CharacterStatus>();
    public DbSet<GameLogFile>     GameLogFiles      => Set<GameLogFile>();
    public DbSet<GameLogEvent>    GameLogEvents     => Set<GameLogEvent>();
    public DbSet<ChatLogFile>     ChatLogFiles      => Set<ChatLogFile>();
    public DbSet<ChatMessage>     ChatMessages      => Set<ChatMessage>();

    public DbSet<IntelReport>          IntelReports          => Set<IntelReport>();
    public DbSet<IntelReportCharacter> IntelReportCharacters => Set<IntelReportCharacter>();
    public DbSet<CharacterAffiliation> CharacterAffiliations  => Set<CharacterAffiliation>();
    public DbSet<NameLookupMiss>       NameLookupMisses       => Set<NameLookupMiss>();

    // ── Market pricing ───────────────────────────────────────────────
    public DbSet<MarketPricingConfig>   MarketPricingConfigs   => Set<MarketPricingConfig>();
    public DbSet<MarketItemPrice>       MarketItemPrices       => Set<MarketItemPrice>();
    public DbSet<MarketRawOrder>        MarketRawOrders        => Set<MarketRawOrder>();
    public DbSet<MarketDefaultSettings> MarketDefaultSettings  => Set<MarketDefaultSettings>();
    public DbSet<MarketTypeHistory>     MarketTypeHistories    => Set<MarketTypeHistory>();
    public DbSet<MarketHistoryFetch>    MarketHistoryFetches   => Set<MarketHistoryFetch>();
    public DbSet<PriceHistoryRegion>    PriceHistoryRegions    => Set<PriceHistoryRegion>();

    // ── SDE static data ──────────────────────────────────────────────────
    public DbSet<SdeBuildInfo>          SdeBuildInfos          => Set<SdeBuildInfo>();
    public DbSet<SdeCategory>           SdeCategories          => Set<SdeCategory>();
    public DbSet<SdeGroup>              SdeGroups              => Set<SdeGroup>();
    public DbSet<SdeType>               SdeTypes               => Set<SdeType>();
    public DbSet<SdeMarketGroup>        SdeMarketGroups        => Set<SdeMarketGroup>();
    public DbSet<SdeDogmaAttributeCategory> SdeDogmaAttributeCategories => Set<SdeDogmaAttributeCategory>();
    public DbSet<SdeDogmaAttribute>     SdeDogmaAttributes     => Set<SdeDogmaAttribute>();
    public DbSet<SdeDogmaEffect>        SdeDogmaEffects        => Set<SdeDogmaEffect>();
    public DbSet<SdeTypeDogmaAttribute> SdeTypeDogmaAttributes => Set<SdeTypeDogmaAttribute>();
    public DbSet<SdeTypeDogmaEffect>    SdeTypeDogmaEffects    => Set<SdeTypeDogmaEffect>();
    public DbSet<SdeBlueprint>          SdeBlueprints          => Set<SdeBlueprint>();
    public DbSet<SdeBlueprintMaterial>  SdeBlueprintMaterials  => Set<SdeBlueprintMaterial>();
    public DbSet<SdeBlueprintProduct>   SdeBlueprintProducts   => Set<SdeBlueprintProduct>();
    public DbSet<SdeBlueprintSkill>     SdeBlueprintSkills     => Set<SdeBlueprintSkill>();
    // Map statistics — hourly buckets plus their daily rollup.
    public DbSet<MapSystemJump>     MapSystemJumps     => Set<MapSystemJump>();
    public DbSet<MapSystemKill>     MapSystemKills     => Set<MapSystemKill>();
    public DbSet<MapSystemDaily>    MapSystemDailies   => Set<MapSystemDaily>();
    public DbSet<MapSovereignty>    MapSovereignties   => Set<MapSovereignty>();
    public DbSet<MapSovStructure>   MapSovStructures   => Set<MapSovStructure>();
    public DbSet<MapIndustryIndex>  MapIndustryIndices => Set<MapIndustryIndex>();
    public DbSet<MapFactionWarfare> MapFactionWarfares => Set<MapFactionWarfare>();
    public DbSet<MapIncursion>      MapIncursions      => Set<MapIncursion>();
    public DbSet<MapStatBucket>     MapStatBuckets     => Set<MapStatBucket>();

    public DbSet<SdeRegion>             SdeRegions             => Set<SdeRegion>();
    public DbSet<SdeConstellation>      SdeConstellations      => Set<SdeConstellation>();
    public DbSet<SdeSolarSystem>        SdeSolarSystems        => Set<SdeSolarSystem>();
    public DbSet<SdeStargate>           SdeStargates           => Set<SdeStargate>();
    public DbSet<SdePlanetResource>     SdePlanetResources     => Set<SdePlanetResource>();
    public DbSet<SdeAgent>              SdeAgents              => Set<SdeAgent>();
    public DbSet<SdeAgentType>          SdeAgentTypes          => Set<SdeAgentType>();
    public DbSet<SdeCorpDivision>       SdeCorpDivisions       => Set<SdeCorpDivision>();
    public DbSet<SdeCelestial>          SdeCelestials          => Set<SdeCelestial>();
    public DbSet<SdeStation>            SdeStations            => Set<SdeStation>();
    public DbSet<SdeStationService>          SdeStationServices          => Set<SdeStationService>();
    public DbSet<SdeStationOperation>        SdeStationOperations        => Set<SdeStationOperation>();
    public DbSet<SdeStationOperationService> SdeStationOperationServices => Set<SdeStationOperationService>();
    public DbSet<SdeFaction>            SdeFactions            => Set<SdeFaction>();
    public DbSet<SdeNpcCorporation>     SdeNpcCorporations     => Set<SdeNpcCorporation>();
    public DbSet<SdeRace>               SdeRaces               => Set<SdeRace>();
    public DbSet<SdeMetaGroup>          SdeMetaGroups          => Set<SdeMetaGroup>();
    public DbSet<SdeCertificate>        SdeCertificates        => Set<SdeCertificate>();
    public DbSet<SdeTypeMaterial>       SdeTypeMaterials       => Set<SdeTypeMaterial>();
    public DbSet<SdePlanetSchematic>    SdePlanetSchematics    => Set<SdePlanetSchematic>();
    public DbSet<SdePlanetSchematicType> SdePlanetSchematicTypes => Set<SdePlanetSchematicType>();
    public DbSet<SdeDogmaUnit>          SdeDogmaUnits          => Set<SdeDogmaUnit>();
    public DbSet<SdeIcon>               SdeIcons               => Set<SdeIcon>();
    public DbSet<SdeGraphic>            SdeGraphics            => Set<SdeGraphic>();
    public DbSet<SdeSkin>               SdeSkins               => Set<SdeSkin>();
    public DbSet<SdeSkinType>           SdeSkinTypes           => Set<SdeSkinType>();
    public DbSet<SdeSkinLicense>        SdeSkinLicenses        => Set<SdeSkinLicense>();

    // ── Hoboleaks complementary data ─────────────────────────────────────────
    public DbSet<HoboBuildInfo>          HoboBuildInfos          => Set<HoboBuildInfo>();
    public DbSet<HoboBlueprint>          HoboBlueprints          => Set<HoboBlueprint>();
    public DbSet<HoboBlueprintActivity>  HoboBlueprintActivities => Set<HoboBlueprintActivity>();
    public DbSet<HoboBlueprintMaterial>  HoboBlueprintMaterials  => Set<HoboBlueprintMaterial>();
    public DbSet<HoboBlueprintProduct>   HoboBlueprintProducts   => Set<HoboBlueprintProduct>();
    public DbSet<HoboBlueprintSkill>     HoboBlueprintSkills     => Set<HoboBlueprintSkill>();
    public DbSet<HoboTypeMaterial>       HoboTypeMaterials       => Set<HoboTypeMaterial>();
    public DbSet<HoboRepackagedVolume>   HoboRepackagedVolumes   => Set<HoboRepackagedVolume>();
    public DbSet<HoboCompressibleType>   HoboCompressibleTypes   => Set<HoboCompressibleType>();

    // ── Market Levels ────────────────────────────────────────────────────────
    public DbSet<MarketLevelCollection> MarketLevelCollections => Set<MarketLevelCollection>();
    public DbSet<MarketLevelGroup>      MarketLevelGroups      => Set<MarketLevelGroup>();
    public DbSet<MarketLevelItem>       MarketLevelItems       => Set<MarketLevelItem>();

    // ── Inventory Levels ─────────────────────────────────────────────────────
    public DbSet<InvLevelCollection> InvLevelCollections => Set<InvLevelCollection>();
    public DbSet<InvLevelGroup>      InvLevelGroups      => Set<InvLevelGroup>();
    public DbSet<InvLevelItem>       InvLevelItems       => Set<InvLevelItem>();

    // ── Sale Posting ─────────────────────────────────────────────────────────
    public DbSet<SalePosting>        SalePostings        => Set<SalePosting>();
    public DbSet<SalePostingSection> SalePostingSections => Set<SalePostingSection>();
    public DbSet<SalePostingItem>    SalePostingItems    => Set<SalePostingItem>();
    public DbSet<SalePostingPost>    SalePostingPosts    => Set<SalePostingPost>();

    // ── Stores (EVE mail order desk) ────────────────────────────────────────
    public DbSet<Store>       Stores       => Set<Store>();
    public DbSet<SlackWebhook>  SlackWebhooks  => Set<SlackWebhook>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<StoreSender> StoreSenders => Set<StoreSender>();
    public DbSet<StoreMail>   StoreMails   => Set<StoreMail>();
    public DbSet<OrderLabel>  OrderLabels  => Set<OrderLabel>();
    public DbSet<SaleLabel>   SaleLabels   => Set<SaleLabel>();

    // ── Indy Parks ──────────────────────────────────────────────────────────
    public DbSet<IndyPark>               IndyParks               => Set<IndyPark>();
    public DbSet<IndyStructure>          IndyStructures          => Set<IndyStructure>();
    public DbSet<IndyStructureRig>       IndyStructureRigs       => Set<IndyStructureRig>();
    public DbSet<IndyCategoryAssignment> IndyCategoryAssignments => Set<IndyCategoryAssignment>();
    public DbSet<IndyItemException>      IndyItemExceptions      => Set<IndyItemException>();
    public DbSet<IndyStructureService>   IndyStructureServices   => Set<IndyStructureService>();

    // ── Structures ───────────────────────────────────────────────────────────
    // The app's own record, fed from EsiStructureNames and editable. Deliberately separate from
    // that table so the UI never writes into polled data.
    public DbSet<Structure>              Structures              => Set<Structure>();
    public DbSet<StructureFitting>       StructureFittings       => Set<StructureFitting>();

    // EVE Ref's published structure snapshot. A third source, kept apart from both the polled
    // table and our own — see EveRefStructure.
    public DbSet<EveRefStructure>        EveRefStructures        => Set<EveRefStructure>();

    // ── Build cost calculation ───────────────────────────────────────────────
    public DbSet<EsiAdjustedPrice>   EsiAdjustedPrices   => Set<EsiAdjustedPrice>();
    public DbSet<IndustryCostIndex>  IndustryCostIndices => Set<IndustryCostIndex>();
    public DbSet<BuildCost>              BuildCosts              => Set<BuildCost>();
    public DbSet<ReprocessingItemValue>  ReprocessingItemValues  => Set<ReprocessingItemValue>();

    // ── Sales ────────────────────────────────────────────────────────────────
    public DbSet<SaleExclusion> SaleExclusions => Set<SaleExclusion>();

    // ── Alarms ───────────────────────────────────────────────────────────────
    public DbSet<Alarm>        Alarms        => Set<Alarm>();
    public DbSet<AlarmAction>  AlarmActions  => Set<AlarmAction>();
    public DbSet<AlarmSeenKey> AlarmSeenKeys => Set<AlarmSeenKey>();
    public DbSet<AlarmEvent>   AlarmEvents   => Set<AlarmEvent>();
    public DbSet<AlarmAlert>   AlarmAlerts   => Set<AlarmAlert>();

    // ── App settings ─────────────────────────────────────────────────────────
    public DbSet<AlertSettings>      AlertSettings       => Set<AlertSettings>();
    public DbSet<AppPreference>      AppPreferences      => Set<AppPreference>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── Dynamic tables ───────────────────────────────────────────────
        mb.Entity<Character>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).IsRequired().HasMaxLength(100);
            e.Property(c => c.RefreshToken).IsRequired();
        });

        mb.Entity<Corporation>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).IsRequired().HasMaxLength(100);
            e.Property(c => c.Ticker).HasMaxLength(10);
            // AuthCharacterId is a plain data column — no EF relationship configured.
            // EF has zero awareness of any Character↔Corporation link, so deleting a
            // Character entity never touches tracked Corporation entities.
        });

        // ── Market pricing ───────────────────────────────────────────────
        mb.Entity<MarketPricingConfig>(e => { e.HasKey(x => x.Id); });

        mb.Entity<MarketDefaultSettings>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever(); });

        mb.Entity<AlertSettings>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever(); });

        mb.Entity<AppPreference>(e => {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).ValueGeneratedNever(); });

        mb.Entity<MarketItemPrice>(e => {
            e.HasKey(x => new { x.ConfigId, x.TypeId });
            e.Property(x => x.ConfigId).ValueGeneratedNever();
            e.Property(x => x.TypeId).ValueGeneratedNever(); });

        mb.Entity<EsiAdjustedPrice>(e => {
            e.HasKey(x => x.TypeId);
            e.Property(x => x.TypeId).ValueGeneratedNever(); });

        mb.Entity<IndustryCostIndex>(e => {
            e.HasKey(x => new { x.SolarSystemId, x.Activity });
            e.Property(x => x.SolarSystemId).ValueGeneratedNever(); });

        mb.Entity<BuildCost>(e => {
            e.HasKey(x => x.TypeId);
            e.Property(x => x.TypeId).ValueGeneratedNever(); });

        mb.Entity<ReprocessingItemValue>(e => {
            e.HasKey(x => x.TypeId);
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.ToTable("ReprocessingValues"); });

        mb.Entity<MarketRawOrder>(e => {
            e.HasKey(x => new { x.ConfigId, x.OrderId });
            e.Property(x => x.ConfigId).ValueGeneratedNever();
            e.Property(x => x.OrderId).ValueGeneratedNever();
            e.HasIndex(x => new { x.ConfigId, x.TypeId, x.IsBuyOrder }); });

        // ── SDE build metadata — single row, always Id = 1 ──────────────
        mb.Entity<SdeBuildInfo>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever(); });

        // ── SDE tables — PKs are always supplied from YAML, never auto-generated ──
        mb.Entity<SdeCategory>(e => {
            e.HasKey(x => x.CategoryId);
            e.Property(x => x.CategoryId).ValueGeneratedNever(); });

        mb.Entity<SdeGroup>(e => {
            e.HasKey(x => x.GroupId);
            e.Property(x => x.GroupId).ValueGeneratedNever(); });

        mb.Entity<SdeType>(e => {
            e.HasKey(x => x.TypeId);
            e.Property(x => x.TypeId).ValueGeneratedNever(); });

        mb.Entity<SdeMarketGroup>(e => {
            e.HasKey(x => x.MarketGroupId);
            e.Property(x => x.MarketGroupId).ValueGeneratedNever(); });

        mb.Entity<SdeDogmaAttributeCategory>(e => {
            e.HasKey(x => x.CategoryId);
            e.Property(x => x.CategoryId).ValueGeneratedNever(); });

        mb.Entity<SdeDogmaAttribute>(e => {
            e.HasKey(x => x.AttributeId);
            e.Property(x => x.AttributeId).ValueGeneratedNever(); });

        mb.Entity<SdeDogmaEffect>(e => {
            e.HasKey(x => x.EffectId);
            e.Property(x => x.EffectId).ValueGeneratedNever(); });

        mb.Entity<SdeTypeDogmaAttribute>(e => {
            e.HasKey(x => new { x.TypeId, x.AttributeId });
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.Property(x => x.AttributeId).ValueGeneratedNever(); });

        mb.Entity<SdeTypeDogmaEffect>(e => {
            e.HasKey(x => new { x.TypeId, x.EffectId });
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.Property(x => x.EffectId).ValueGeneratedNever(); });

        mb.Entity<SdeBlueprint>(e => {
            e.HasKey(x => x.TypeId);
            e.Property(x => x.TypeId).ValueGeneratedNever(); });

        mb.Entity<SdeBlueprintMaterial>(e => {
            e.HasKey(x => new { x.TypeId, x.Activity, x.MaterialTypeId });
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.Property(x => x.MaterialTypeId).ValueGeneratedNever(); });

        mb.Entity<SdeBlueprintProduct>(e => {
            e.HasKey(x => new { x.TypeId, x.Activity, x.ProductTypeId });
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.Property(x => x.ProductTypeId).ValueGeneratedNever(); });

        mb.Entity<SdeBlueprintSkill>(e => {
            e.HasKey(x => new { x.TypeId, x.Activity, x.SkillTypeId });
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.Property(x => x.SkillTypeId).ValueGeneratedNever(); });

        mb.Entity<SdeRegion>(e => {
            e.HasKey(x => x.RegionId);
            e.Property(x => x.RegionId).ValueGeneratedNever(); });

        // Map statistics. Every hourly table is keyed by (Bucket, …) so that a row
        // written by the live ESI poll and the same row recovered later from the EVE Ref
        // archive collide on the primary key instead of duplicating.
        mb.Entity<MapSystemJump>(e => {
            e.HasKey(x => new { x.Bucket, x.SystemId });
            e.ToTable("MapSystemJumps"); });

        mb.Entity<MapSystemKill>(e => {
            e.HasKey(x => new { x.Bucket, x.SystemId });
            e.ToTable("MapSystemKills"); });

        mb.Entity<MapSystemDaily>(e => {
            e.HasKey(x => new { x.Day, x.SystemId });
            e.ToTable("MapSystemDailies"); });

        mb.Entity<MapSovereignty>(e => {
            e.HasKey(x => new { x.Bucket, x.SystemId });
            e.ToTable("MapSovereignties"); });

        mb.Entity<MapSovStructure>(e => {
            e.HasKey(x => new { x.Bucket, x.StructureId });
            e.HasIndex(x => x.SystemId);
            e.ToTable("MapSovStructures"); });

        mb.Entity<MapIndustryIndex>(e => {
            e.HasKey(x => new { x.Bucket, x.SystemId, x.Activity });
            e.ToTable("MapIndustryIndices"); });

        mb.Entity<MapFactionWarfare>(e => {
            e.HasKey(x => new { x.Bucket, x.SystemId });
            e.ToTable("MapFactionWarfares"); });

        mb.Entity<MapIncursion>(e => {
            e.HasKey(x => new { x.Bucket, x.ConstellationId });
            e.ToTable("MapIncursions"); });

        mb.Entity<MapStatBucket>(e => {
            e.HasKey(x => new { x.Dataset, x.Bucket });
            e.ToTable("MapStatBuckets"); });

        mb.Entity<SdeConstellation>(e => {
            e.HasKey(x => x.ConstellationId);
            e.Property(x => x.ConstellationId).ValueGeneratedNever(); });

        mb.Entity<SdeSolarSystem>(e => {
            e.HasKey(x => x.SolarSystemId);
            e.Property(x => x.SolarSystemId).ValueGeneratedNever(); });

        mb.Entity<SdeAgent>(e => {
            e.HasKey(x => x.AgentId);
            e.Property(x => x.AgentId).ValueGeneratedNever();
            e.HasIndex(x => x.LocationId);
            e.ToTable("SdeAgents"); });

        mb.Entity<SdeAgentType>(e => {
            e.HasKey(x => x.AgentTypeId);
            e.Property(x => x.AgentTypeId).ValueGeneratedNever();
            e.ToTable("SdeAgentTypes"); });

        mb.Entity<SdeCorpDivision>(e => {
            e.HasKey(x => x.DivisionId);
            e.Property(x => x.DivisionId).ValueGeneratedNever();
            e.ToTable("SdeCorpDivisions"); });

        mb.Entity<SdePlanetResource>(e => {
            e.HasKey(x => x.PlanetId);
            e.Property(x => x.PlanetId).ValueGeneratedNever();
            e.ToTable("SdePlanetResources"); });

        mb.Entity<SdeStargate>(e => {
            e.HasKey(x => x.StargateId);
            e.Property(x => x.StargateId).ValueGeneratedNever(); });

        mb.Entity<SdeCelestial>(e => {
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.HasIndex(x => x.SolarSystemId); });

        mb.Entity<SdeStation>(e => {
            e.HasKey(x => x.StationId);
            e.Property(x => x.StationId).ValueGeneratedNever(); });

        mb.Entity<SdeStationService>(e => {
            e.HasKey(x => x.ServiceId);
            e.Property(x => x.ServiceId).ValueGeneratedNever(); });

        mb.Entity<SdeStationOperation>(e => {
            e.HasKey(x => x.OperationId);
            e.Property(x => x.OperationId).ValueGeneratedNever(); });

        mb.Entity<SdeStationOperationService>(e => {
            e.HasKey(x => new { x.OperationId, x.ServiceId });
            e.Property(x => x.OperationId).ValueGeneratedNever(); });

        mb.Entity<SdeFaction>(e => {
            e.HasKey(x => x.FactionId);
            e.Property(x => x.FactionId).ValueGeneratedNever(); });

        mb.Entity<SdeNpcCorporation>(e => {
            e.HasKey(x => x.CorporationId);
            e.Property(x => x.CorporationId).ValueGeneratedNever(); });

        mb.Entity<SdeRace>(e => {
            e.HasKey(x => x.RaceId);
            e.Property(x => x.RaceId).ValueGeneratedNever(); });

        mb.Entity<SdeMetaGroup>(e => {
            e.HasKey(x => x.MetaGroupId);
            e.Property(x => x.MetaGroupId).ValueGeneratedNever(); });

        mb.Entity<SdeCertificate>(e => {
            e.HasKey(x => x.CertificateId);
            e.Property(x => x.CertificateId).ValueGeneratedNever(); });

        mb.Entity<SdeTypeMaterial>(e => {
            e.HasKey(x => new { x.TypeId, x.MaterialTypeId });
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.Property(x => x.MaterialTypeId).ValueGeneratedNever(); });

        mb.Entity<SdePlanetSchematic>(e => {
            e.HasKey(x => x.SchematicId);
            e.Property(x => x.SchematicId).ValueGeneratedNever(); });

        mb.Entity<SdePlanetSchematicType>(e => {
            e.HasKey(x => new { x.SchematicId, x.TypeId });
            e.Property(x => x.SchematicId).ValueGeneratedNever();
            e.Property(x => x.TypeId).ValueGeneratedNever(); });

        mb.Entity<SdeDogmaUnit>(e => {
            e.HasKey(x => x.UnitId);
            e.Property(x => x.UnitId).ValueGeneratedNever(); });

        mb.Entity<SdeIcon>(e => {
            e.HasKey(x => x.IconId);
            e.Property(x => x.IconId).ValueGeneratedNever(); });

        mb.Entity<SdeGraphic>(e => {
            e.HasKey(x => x.GraphicId);
            e.Property(x => x.GraphicId).ValueGeneratedNever(); });

        mb.Entity<SdeSkin>(e => {
            e.HasKey(x => x.SkinId);
            e.Property(x => x.SkinId).ValueGeneratedNever(); });

        mb.Entity<SdeSkinType>(e => {
            e.HasKey(x => new { x.SkinId, x.TypeId });
            e.Property(x => x.SkinId).ValueGeneratedNever();
            e.Property(x => x.TypeId).ValueGeneratedNever(); });

        mb.Entity<SdeSkinLicense>(e => {
            e.HasKey(x => x.LicenseTypeId);
            e.Property(x => x.LicenseTypeId).ValueGeneratedNever(); });

        // ── Market Levels ────────────────────────────────────────────────
        mb.Entity<MarketLevelGroup>(e => { e.HasKey(x => x.Id); });
        mb.Entity<MarketLevelItem>(e =>  { e.HasKey(x => x.Id); });

        // ── Hoboleaks tables ─────────────────────────────────────────────

        mb.Entity<HoboBuildInfo>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever(); });

        mb.Entity<HoboBlueprint>(e => {
            e.HasKey(x => x.TypeId);
            e.Property(x => x.TypeId).ValueGeneratedNever(); });

        mb.Entity<HoboBlueprintActivity>(e => {
            e.HasKey(x => new { x.TypeId, x.Activity });
            e.Property(x => x.TypeId).ValueGeneratedNever(); });

        mb.Entity<HoboBlueprintMaterial>(e => {
            e.HasKey(x => new { x.TypeId, x.Activity, x.MaterialTypeId });
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.Property(x => x.MaterialTypeId).ValueGeneratedNever(); });

        mb.Entity<HoboBlueprintProduct>(e => {
            e.HasKey(x => new { x.TypeId, x.Activity, x.ProductTypeId });
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.Property(x => x.ProductTypeId).ValueGeneratedNever(); });

        mb.Entity<HoboBlueprintSkill>(e => {
            e.HasKey(x => new { x.TypeId, x.Activity, x.SkillTypeId });
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.Property(x => x.SkillTypeId).ValueGeneratedNever(); });

        mb.Entity<HoboTypeMaterial>(e => {
            e.HasKey(x => new { x.TypeId, x.MaterialTypeId });
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.Property(x => x.MaterialTypeId).ValueGeneratedNever(); });

        mb.Entity<HoboRepackagedVolume>(e => {
            e.HasKey(x => x.TypeId);
            e.Property(x => x.TypeId).ValueGeneratedNever(); });

        mb.Entity<HoboCompressibleType>(e => {
            e.HasKey(x => x.SourceTypeId);
            e.Property(x => x.SourceTypeId).ValueGeneratedNever(); });

        // ── ESI polling entities ─────────────────────────────────────────

        mb.Entity<ApiCallRecord>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.Endpoint });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.ToTable("EsiCallRecords"); });

        mb.Entity<ApiTimerSetting>(e => {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).ValueGeneratedNever();
            e.ToTable("ApiTimerSettings"); });

        mb.Entity<CharacterWalletBalance>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.Division });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.ToTable("EsiWalletBalances"); });

        mb.Entity<StoredCharacterAttributes>(e => {
            e.HasKey(x => x.CharacterId);
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiCharacterAttributes"); });

        mb.Entity<CharacterCloneState>(e => {
            e.HasKey(x => x.CharacterId);
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiCloneStates"); });

        mb.Entity<StoredCharacterFatigue>(e => {
            e.HasKey(x => x.CharacterId);
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiCharacterFatigues"); });

        mb.Entity<StoredSkill>(e => {
            e.HasKey(x => new { x.CharacterId, x.SkillId });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.Property(x => x.SkillId).ValueGeneratedNever();
            e.ToTable("EsiSkills"); });

        mb.Entity<StoredSkillQueueEntry>(e => {
            e.HasKey(x => new { x.CharacterId, x.QueuePosition });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiSkillQueue"); });

        mb.Entity<StoredJumpClone>(e => {
            e.HasKey(x => x.JumpCloneId);
            e.Property(x => x.JumpCloneId).ValueGeneratedNever();
            e.ToTable("EsiJumpClones"); });

        mb.Entity<StoredJumpCloneImplant>(e => {
            e.HasKey(x => new { x.JumpCloneId, x.TypeId });
            e.Property(x => x.JumpCloneId).ValueGeneratedNever();
            e.ToTable("EsiJumpCloneImplants"); });

        mb.Entity<StoredImplant>(e => {
            e.HasKey(x => new { x.CharacterId, x.TypeId });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiImplants"); });

        mb.Entity<WalletJournalEntry>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.EsiId });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.Property(x => x.EsiId).ValueGeneratedNever();
            e.ToTable("EsiWalletJournal"); });

        mb.Entity<WalletTransaction>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.TransactionId });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.Property(x => x.TransactionId).ValueGeneratedNever();
            e.ToTable("EsiWalletTransactions"); });

        mb.Entity<IndustryJob>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.JobId });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.Property(x => x.JobId).ValueGeneratedNever();
            e.ToTable("EsiIndustryJobs"); });

        mb.Entity<MarketOrder>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.OrderId, x.IsHistory });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.Property(x => x.OrderId).ValueGeneratedNever();
            e.ToTable("EsiMarketOrders"); });

        mb.Entity<ContractRecord>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.ContractId });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.Property(x => x.ContractId).ValueGeneratedNever();
            e.ToTable("EsiContracts"); });

        mb.Entity<ContractItem>(e => {
            e.HasKey(x => new { x.ContractId, x.RecordId });
            e.Property(x => x.ContractId).ValueGeneratedNever();
            e.Property(x => x.RecordId).ValueGeneratedNever();
            e.ToTable("EsiContractItems"); });

        mb.Entity<ContractPrice>(e => {
            e.HasKey(x => x.TypeId);
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.ToTable("ContractPrices"); });

        mb.Entity<ContractBpcPrice>(e => {
            e.HasKey(x => new { x.TypeId, x.Me });
            e.ToTable("ContractBpcPrices"); });

        mb.Entity<PriceOverride>(e => {
            e.HasKey(x => x.TypeId);
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.ToTable("PriceOverrides"); });

        mb.Entity<UniverseName>(e => {
            e.HasKey(x => x.EntityId);
            e.Property(x => x.EntityId).ValueGeneratedNever();
            e.ToTable("UniverseNames"); });

        mb.Entity<WalletBackfillState>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.Kind, x.Division });
            e.ToTable("WalletBackfillState"); });

        mb.Entity<CharacterAsset>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.ItemId });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.ToTable("EsiAssets"); });

        mb.Entity<CharacterBlueprint>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.ItemId });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.ToTable("EsiBlueprints"); });

        mb.Entity<CharacterMiningEntry>(e => {
            e.HasKey(x => new { x.CharacterId, x.Date, x.SolarSystemId, x.TypeId });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiMining"); });

        mb.Entity<CharacterNotification>(e => {
            e.HasKey(x => new { x.CharacterId, x.NotificationId });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.Property(x => x.NotificationId).ValueGeneratedNever();
            e.ToTable("EsiNotifications"); });

        mb.Entity<DismissedAlert>(e => {
            e.HasKey(x => new { x.CharacterId, x.NotificationId });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.Property(x => x.NotificationId).ValueGeneratedNever();
            e.ToTable("DismissedAlerts"); });

        mb.Entity<ContactEntry>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.ContactId });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.Property(x => x.ContactId).ValueGeneratedNever();
            e.ToTable("EsiContacts"); });

        mb.Entity<KillMailRef>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.KillMailId });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.ToTable("EsiKillMailRefs"); });

        mb.Entity<KillMailDetail>(e => {
            e.HasKey(x => x.KillMailId);
            e.Property(x => x.KillMailId).ValueGeneratedNever();
            e.ToTable("KillMailDetails"); });

        mb.Entity<KillMailAttacker>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.ToTable("KillMailAttackers"); });

        mb.Entity<KillMailItem>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.ToTable("KillMailItems"); });

        mb.Entity<ZkbKillFlag>(e => {
            e.HasKey(x => x.KillMailId);
            e.Property(x => x.KillMailId).ValueGeneratedNever();
            e.ToTable("ZkbKillFlags"); });

        mb.Entity<PlanetaryColony>(e => {
            e.HasKey(x => new { x.CharacterId, x.PlanetId });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiPlanetaryColonies"); });

        mb.Entity<AgentResearch>(e => {
            e.HasKey(x => new { x.CharacterId, x.AgentId });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiAgentResearch"); });

        mb.Entity<LoyaltyPoint>(e => {
            e.HasKey(x => new { x.CharacterId, x.CorporationId });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiLoyaltyPoints"); });

        mb.Entity<LpStoreOffer>(e => {
            e.HasKey(x => new { x.CorporationId, x.OfferId });
            e.Property(x => x.OfferId).ValueGeneratedNever();
            e.ToTable("EsiLpStoreOffers"); });

        mb.Entity<LpStoreOfferItem>(e => {
            e.HasKey(x => new { x.CorporationId, x.OfferId, x.TypeId });
            e.Property(x => x.OfferId).ValueGeneratedNever();
            e.ToTable("EsiLpStoreOfferItems"); });

        mb.Entity<LpStoreCorp>(e => {
            e.HasKey(x => x.CorporationId);
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.ToTable("EsiLpStoreCorps"); });

        mb.Entity<LpCorpValue>(e => {
            e.HasKey(x => x.CorporationId);
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.ToTable("LpCorpValues"); });

        mb.Entity<LpCorpValueSnapshot>(e => {
            e.HasKey(x => new { x.CorporationId, x.Date });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.ToTable("LpCorpValueSnapshots"); });

        mb.Entity<CharacterMedal>(e => {
            e.HasKey(x => x.Id);
            e.ToTable("EsiMedals"); });

        mb.Entity<StandingEntry>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.FromId });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.ToTable("EsiStandings"); });

        mb.Entity<CharacterTitle>(e => {
            e.HasKey(x => new { x.CharacterId, x.TitleId });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiTitles"); });

        mb.Entity<CharacterRole>(e => {
            e.HasKey(x => new { x.CharacterId, x.Role, x.RoleType });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiRoles"); });

        mb.Entity<StoredFitting>(e => {
            e.HasKey(x => new { x.CharacterId, x.FittingId });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.Property(x => x.FittingId).ValueGeneratedNever();
            e.ToTable("EsiFittings"); });

        mb.Entity<FittingItem>(e => {
            e.HasKey(x => x.Id);
            e.ToTable("EsiFittingItems"); });

        // ── Corp entities ────────────────────────────────────────────────

        mb.Entity<CorpDivision>(e => {
            e.HasKey(x => new { x.CorporationId, x.Division, x.DivisionType });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.ToTable("EsiCorpDivisions"); });

        mb.Entity<CorpMember>(e => {
            e.HasKey(x => new { x.CorporationId, x.CharacterId });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiCorpMembers"); });

        mb.Entity<CorpMemberTracking>(e => {
            e.HasKey(x => new { x.CorporationId, x.CharacterId });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiCorpMemberTracking"); });

        mb.Entity<CorpMemberSession>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.CorporationId, x.CharacterId, x.LogonDate }).IsUnique();
            e.ToTable("EsiCorpMemberSessions"); });

        mb.Entity<CorpMemberRole>(e => {
            e.HasKey(x => new { x.CorporationId, x.CharacterId, x.Role, x.RoleType });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiCorpMemberRoles"); });

        mb.Entity<CorpTitle>(e => {
            e.HasKey(x => new { x.CorporationId, x.TitleId });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.TitleId).ValueGeneratedNever();
            e.ToTable("EsiCorpTitles"); });

        mb.Entity<CorpMedal>(e => {
            e.HasKey(x => new { x.CorporationId, x.MedalId });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.MedalId).ValueGeneratedNever();
            e.ToTable("EsiCorpMedals"); });

        mb.Entity<CorpStructure>(e => {
            e.HasKey(x => new { x.CorporationId, x.StructureId });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.StructureId).ValueGeneratedNever();
            e.ToTable("EsiCorpStructures"); });

        mb.Entity<StructureName>(e => {
            e.HasKey(x => x.StructureId);
            e.Property(x => x.StructureId).ValueGeneratedNever();
            e.ToTable("EsiStructureNames"); });

        mb.Entity<StructureNameFailure>(e => {
            e.HasKey(x => x.StructureId);
            e.Property(x => x.StructureId).ValueGeneratedNever();
            e.ToTable("EsiStructureNameFailures"); });

        // The app's own structure record. StructureId is the in-game location id, so it is never
        // generated — a row's identity is the structure it describes.
        mb.Entity<Structure>(e => {
            e.HasKey(x => x.StructureId);
            e.Property(x => x.StructureId).ValueGeneratedNever();
            e.ToTable("Structures"); });

        mb.Entity<StructureFitting>(e => {
            e.HasKey(x => x.Id);
            // One module per slot: the unique index is what stops a double-click leaving two
            // modules in the same hole.
            e.HasIndex(x => new { x.StructureId, x.Band, x.SlotIndex }).IsUnique();
            e.ToTable("StructureFittings"); });

        mb.Entity<EveRefStructure>(e => {
            e.HasKey(x => x.StructureId);
            e.Property(x => x.StructureId).ValueGeneratedNever();
            e.ToTable("EveRefStructures"); });

        mb.Entity<IndyStructureService>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.StructureId);
            e.ToTable("IndyStructureServices"); });

        mb.Entity<CorpStarbase>(e => {
            e.HasKey(x => new { x.CorporationId, x.StarbaseId });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.StarbaseId).ValueGeneratedNever();
            e.ToTable("EsiCorpStarbases"); });

        mb.Entity<CorpFacility>(e => {
            e.HasKey(x => new { x.CorporationId, x.FacilityId });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.FacilityId).ValueGeneratedNever();
            e.ToTable("EsiCorpFacilities"); });

        mb.Entity<CorpMiningExtraction>(e => {
            e.HasKey(x => new { x.CorporationId, x.MoonId, x.StructureId });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.MoonId).ValueGeneratedNever();
            e.Property(x => x.StructureId).ValueGeneratedNever();
            e.ToTable("EsiCorpMiningExtractions"); });

        mb.Entity<CorpMiningObserver>(e => {
            e.HasKey(x => new { x.CorporationId, x.ObserverId });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.ObserverId).ValueGeneratedNever();
            e.ToTable("EsiCorpMiningObservers"); });

        mb.Entity<CorpMiningLedgerEntry>(e => {
            e.HasKey(x => new { x.CorporationId, x.ObserverId, x.CharacterId, x.TypeId });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.ObserverId).ValueGeneratedNever();
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.ToTable("EsiCorpMiningLedger"); });

        mb.Entity<CorpProject>(e => {
            e.HasKey(x => new { x.CorporationId, x.ProjectId });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.ProjectId).ValueGeneratedNever();
            e.ToTable("EsiCorpProjects"); });

        mb.Entity<CorpProjectContributor>(e => {
            e.HasKey(x => new { x.CorporationId, x.ProjectId, x.CharacterId });
            e.Property(x => x.CorporationId).ValueGeneratedNever();
            e.Property(x => x.ProjectId).ValueGeneratedNever();
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiCorpProjectContributors"); });

        mb.Entity<CorpTop10Exclude>(e => {
            e.HasKey(x => new { x.EntityId, x.EntityType });
            e.Property(x => x.EntityId).ValueGeneratedNever();
            e.ToTable("CorpTop10Excludes"); });

        mb.Entity<EveMailHeader>(e => {
            e.HasKey(x => new { x.MailId, x.CharacterId });
            e.Property(x => x.MailId).ValueGeneratedNever();
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.ToTable("EsiMailHeaders"); });

        mb.Entity<EveMailBody>(e => {
            e.HasKey(x => x.MailId);
            e.Property(x => x.MailId).ValueGeneratedNever();
            e.ToTable("EsiMailBodies"); });

        mb.Entity<EveMailRecipientEntry>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.ToTable("EsiMailRecipients"); });

        mb.Entity<EveMailLabelEntry>(e => {
            e.HasKey(x => new { x.CharacterId, x.LabelId });
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.Property(x => x.LabelId).ValueGeneratedNever();
            e.ToTable("EsiMailLabels"); });

        mb.Entity<NetWorthSnapshot>(e => {
            e.HasKey(x => new { x.OwnerId, x.OwnerType, x.Date });
            e.Property(x => x.OwnerId).ValueGeneratedNever();
            e.ToTable("NetWorthSnapshots"); });

        mb.Entity<TypePriceSnapshot>(e => {
            e.HasKey(x => new { x.TypeId, x.Date });
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.ToTable("TypePriceSnapshots"); });

        mb.Entity<TrackedOrder>(e => {
            e.HasKey(x => x.Id);
            e.ToTable("TrackedOrders"); });

        mb.Entity<Store>(e => {
            e.HasKey(x => x.Id);
            e.ToTable("Stores"); });

        mb.Entity<SlackWebhook>(e => {
            e.HasKey(x => x.Id);
            e.ToTable("SlackWebhooks"); });

        mb.Entity<ScheduledTask>(e => {
            e.HasKey(x => x.Id);
            e.ToTable("ScheduledTasks"); });

        mb.Entity<StoreSender>(e => {
            e.HasKey(x => x.Id);
            e.ToTable("StoreSenders"); });

        mb.Entity<StoreMail>(e => {
            e.HasKey(x => x.Id);
            e.ToTable("StoreMails"); });

        mb.Entity<OrderLabel>(e => {
            e.HasKey(x => new { x.OrderId, x.Label });
            e.ToTable("OrderLabels"); });

        mb.Entity<SaleLabel>(e => {
            e.HasKey(x => new { x.Kind, x.SaleId, x.Label });
            e.ToTable("SaleLabels"); });

        mb.Entity<AppErrorEntry>(e => {
            e.HasKey(x => x.Id);
            e.ToTable("AppErrorLog"); });

        mb.Entity<StandingBuyOrder>(e => {
            e.HasKey(x => x.Id);
            // One standing order per type per location — a second would just be a
            // duplicate row matching the same live orders.
            e.HasIndex(x => new { x.TypeId, x.LocationId }).IsUnique(); });

        mb.Entity<WorklistMarketAlt>(e => {
            e.HasKey(x => x.Id);
            // One character per location: the desk answers "who works here", and two
            // answers for one station is not a routing rule, it is an ambiguity.
            e.HasIndex(x => x.LocationId).IsUnique(); });

        mb.Entity<WorklistInvRule>(e => {
            e.HasKey(x => x.Id); });

        mb.Entity<WorklistCorpAlt>(e => {
            e.HasKey(x => x.Id);
            // One maintainer per corporation, for the same reason a station has one trader.
            e.HasIndex(x => x.CorporationId).IsUnique(); });

        mb.Entity<WorklistStationLevel>(e => {
            e.HasKey(x => x.Id);
            // One row per group per station; a station may hold several groups.
            e.HasIndex(x => new { x.GroupId, x.LocationId }).IsUnique(); });

        mb.Entity<WorklistIndyScopeStation>(e => {
            e.HasKey(x => x.Id);
            // One row per station: adding Jita twice is not two scopes.
            e.HasIndex(x => x.LocationId).IsUnique(); });

        mb.Entity<WorklistIndyChar>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CharacterId).IsUnique(); });

        mb.Entity<WorklistItemState>(e => {
            e.HasKey(x => x.Key); });

        mb.Entity<CharacterStatus>(e => {
            e.HasKey(x => x.CharacterId);
            e.Property(x => x.CharacterId).ValueGeneratedNever();
            e.Ignore(x => x.IsDocked); });

        mb.Entity<GameLogFile>(e => {
            e.HasKey(x => x.Path);
            e.Property(x => x.Path).ValueGeneratedNever(); });

        mb.Entity<GameLogEvent>(e => {
            e.HasKey(x => x.Id);
            // Re-importing a file must not duplicate rows.
            e.HasIndex(x => new { x.SourceFile, x.LineNumber }).IsUnique();
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.CharacterId, x.Kind }); });

        mb.Entity<ChatLogFile>(e => {
            e.HasKey(x => x.Path);
            e.Property(x => x.Path).ValueGeneratedNever(); });

        mb.Entity<ChatMessage>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SourceFile, x.LineNumber }).IsUnique();
            e.HasIndex(x => x.OccurredAt);
            // Also what the import's duplicate check reads: the same conversation logged by a
            // second character, or imported from another PC's folder, is found by channel and
            // time rather than by file, which cannot see across files at all.
            e.HasIndex(x => new { x.ChannelName, x.OccurredAt }); });

        mb.Entity<IntelReport>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ChatMessageId).IsUnique();          // re-parsing cannot duplicate
            e.HasIndex(x => new { x.SystemId, x.ReportedAt });    // the overlays' query
            e.HasIndex(x => new { x.Obsolete, x.ReportedAt }); });

        mb.Entity<NameLookupMiss>(e => {
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).ValueGeneratedNever(); });

        mb.Entity<CharacterAffiliation>(e => {
            e.HasKey(x => x.CharacterId);
            e.Property(x => x.CharacterId).ValueGeneratedNever(); });

        mb.Entity<IntelReportCharacter>(e => {
            e.HasKey(x => new { x.IntelReportId, x.CharacterId });
            e.HasIndex(x => x.CharacterId); });                   // "where was this pilot last seen"

        mb.Entity<SaleExclusion>(e => {
            e.HasKey(x => new { x.Kind, x.SaleId }); });

        mb.Entity<Alarm>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Enabled); });

        mb.Entity<AlarmAction>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AlarmId); });

        mb.Entity<AlarmSeenKey>(e => {
            e.HasKey(x => new { x.AlarmId, x.MatchKey });
            // Pruning walks the ledger oldest-first per alarm.
            e.HasIndex(x => new { x.AlarmId, x.FirstSeenAt }); });

        mb.Entity<AlarmEvent>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AlarmId, x.FiredAt }); });

        mb.Entity<AlarmAlert>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Dismissed, x.CreatedAt }); });

        mb.Entity<MarketTypeHistory>(e => {
            e.HasKey(x => new { x.RegionId, x.TypeId, x.Date });
            e.Property(x => x.RegionId).ValueGeneratedNever();
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.Property(x => x.Date).ValueGeneratedNever();
            e.ToTable("MarketTypeHistories"); });

        mb.Entity<MarketHistoryFetch>(e => {
            e.HasKey(x => new { x.RegionId, x.TypeId });
            e.Property(x => x.RegionId).ValueGeneratedNever();
            e.Property(x => x.TypeId).ValueGeneratedNever();
            e.ToTable("MarketHistoryFetches"); });

        mb.Entity<PriceHistoryRegion>(e => {
            e.HasKey(x => x.RegionId);
            e.Property(x => x.RegionId).ValueGeneratedNever();
            e.ToTable("PriceHistoryRegions"); });
    }
}
