using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Services;

public class BuildCostService
{
    // Blueprint ME assumptions live in IndustryMe (shared with the Production Calculator):
    // ME10 default, T2 ME3, BPC-only/faction ME0, titans/Keepstar/Fortizar ME9, reactions ME0.

    // Upwell role bonuses: -3% job gross cost, -1% material requirements (Engineering Complexes).
    private const double UpwellRoleBonus     = 0.97;
    private const double UpwellMaterialBonus = 0.01;

    // SCC surcharge: fixed 4% of EIV.
    private const double SccSurcharge = 0.04;

    // Dogma attribute IDs for rig ME bonuses and security-zone multipliers.
    private const int AttrMfgME          = 2594;
    private const int AttrRxnME          = 2714;
    private const int AttrRigLowsecMult  = 2356;
    private const int AttrRigNullsecMult = 2357;

    // Dogma attribute IDs for build-TIME modelling.
    private const int AttrMfgRigTE       = 2593; // rig manufacturing time bonus (percent, negative)
    private const int AttrRxnRigTE       = 2713; // rig reaction time bonus (percent, negative)
    private const int AttrStrEngTime     = 2602; // structure manufacturing time role bonus (multiplier)
    private const int AttrStrRxnTime     = 2721; // structure reaction time role bonus (multiplier)

    // Build-time assumptions: a fully researched blueprint and maxed industry skills,
    // matching the "ideal setup" spirit of the ME-researched cost side. Structure role
    // and rig time bonuses ARE modelled (from the default park), per activity.
    private const double MfgTimeEfficiency = 0.80; // TE20 researched blueprint (−20% time)
    private const double MfgSkillFactor    = 0.68; // Industry V (0.80) × Advanced Industry V (0.85)
    private const double RxnSkillFactor    = 0.85; // reactions: Advanced Industry V only; no TE research

    /// <summary>
    /// ⚠️ One copy, in IndyRigMatching. This was a third private transcription of the same
    /// rules, and the three had already drifted: the XL rigs were missing from every one of
    /// them, so a Sotiyo costed a titan with no rig bonus at all.
    /// </summary>
    private static string RigCategoryFromName(string n) => IndyRigMatching.RigCategoryFromName(n);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory   _httpFactory;
    private readonly AppErrorLogger       _errorLogger;
    private readonly ApiActivityLog       _log;

    public string StatusText { get; private set; } = "Build costs: not yet calculated";

    // Fired after each RecalculateAllAsync completes; MarketPricingService subscribes to
    // re-run the price-gap fill so fresh build costs are immediately reflected in prices.
    public event Func<CancellationToken, Task>? AfterRecalculate;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public BuildCostService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory   httpFactory,
        AppErrorLogger       errorLogger,
        ApiActivityLog       log)
    {
        _scopeFactory = scopeFactory;
        _httpFactory  = httpFactory;
        _errorLogger  = errorLogger;
        _log          = log;
    }

    // Called after each market price refresh cycle.
    public async Task RunAfterMarketRefreshAsync(CancellationToken ct = default)
    {
        try
        {
            StatusText = "Build costs: fetching ESI data…";
            await FetchAdjustedPricesAsync(ct);
            await FetchCostIndicesAsync(ct);
            StatusText = "Build costs: calculating…";
            await RecalculateAllAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Build costs: error — {ex.Message[..Math.Min(60, ex.Message.Length)]}";
            _errorLogger.Log("BuildCostService", "RunAfterMarketRefreshAsync", ex);
        }
    }

    // ── ESI fetch: /markets/prices/ ───────────────────────────────────────────

    public async Task FetchAdjustedPricesAsync(CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient("esi");
        var response = await http.GetAsync("markets/prices/", ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var dtos = await JsonSerializer.DeserializeAsync<List<EsiMarketPriceDto>>(stream, JsonOpts, ct);
        if (dtos is null) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.EsiAdjustedPrices.ExecuteDeleteAsync(ct);
        db.EsiAdjustedPrices.AddRange(dtos.Select(d => new EsiAdjustedPrice
        {
            TypeId        = d.TypeId,
            AdjustedPrice = d.AdjustedPrice,
            AveragePrice  = d.AveragePrice ?? 0,
        }));
        await db.SaveChangesAsync(ct);
    }

    // ── ESI fetch: /industry/systems/ ────────────────────────────────────────

    public async Task FetchCostIndicesAsync(CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient("esi");
        var response = await http.GetAsync("industry/systems/", ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var dtos = await JsonSerializer.DeserializeAsync<List<EsiIndustrySystemDto>>(stream, JsonOpts, ct);
        if (dtos is null) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.IndustryCostIndices.ExecuteDeleteAsync(ct);
        db.IndustryCostIndices.AddRange(
            dtos.SelectMany(s => s.CostIndices.Select(ci => new IndustryCostIndex
            {
                SolarSystemId = s.SolarSystemId,
                Activity      = ci.Activity,
                CostIndex     = ci.CostIndex,
            })));
        await db.SaveChangesAsync(ct);
    }

    // EVE material consumption for a whole job: per-run adjusted quantity (base × ME/rig/role
    // modifiers) rounded to 2 dp, × run count, ceilinged ONCE, floored at one per run. Must match
    // ProductionCalculatorService.JobMaterialTotal so the two calculators agree.
    private static int JobMaterialTotal(int baseQty, double factor, int runs)
    {
        double perRun = Math.Round(baseQty * factor, 2);
        double total  = Math.Round(perRun * runs, 4);
        return Math.Max(runs, (int)Math.Ceiling(total));
    }

    // ── Core calculation ──────────────────────────────────────────────────────

    public async Task RecalculateAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Need a default park to know which structures/rigs/systems to use.
        var defaultPark = await db.IndyParks.AsNoTracking()
            .FirstOrDefaultAsync(p => p.IsDefault, ct);
        if (defaultPark is null)
        {
            StatusText = "Build costs: no default park set — mark a park as default in Indy Parks";
            return;
        }

        // Load structures and their rigs.
        var structures = await db.IndyStructures.AsNoTracking()
            .Where(s => s.ParkId == defaultPark.Id).ToListAsync(ct);

        var structureIds = structures.Select(s => s.Id).ToList();
        var rigs = await db.IndyStructureRigs.AsNoTracking()
            .Where(r => structureIds.Contains(r.StructureId) && r.RigTypeId > 0)
            .ToListAsync(ct);

        // Load dogma ME bonus attributes for all installed rigs.
        var rigTypeIds = rigs.Select(r => r.RigTypeId).Distinct().ToList();
        var rigAttrs = rigTypeIds.Count > 0
            ? await db.SdeTypeDogmaAttributes.AsNoTracking()
                .Where(a => rigTypeIds.Contains(a.TypeId) &&
                            (a.AttributeId == AttrMfgME         || a.AttributeId == AttrRxnME ||
                             a.AttributeId == AttrMfgRigTE       || a.AttributeId == AttrRxnRigTE ||
                             a.AttributeId == AttrRigLowsecMult  || a.AttributeId == AttrRigNullsecMult))
                .ToListAsync(ct)
            : [];

        var mfgRigBonus     = new Dictionary<int, double>();
        var rxnRigBonus     = new Dictionary<int, double>();
        var mfgRigTimeBonus = new Dictionary<int, double>();
        var rxnRigTimeBonus = new Dictionary<int, double>();
        var rigLowsecMult   = new Dictionary<int, double>();
        var rigNullsecMult  = new Dictionary<int, double>();
        foreach (var a in rigAttrs)
        {
            if (a.AttributeId == AttrMfgME)          mfgRigBonus[a.TypeId]     = Math.Abs(a.Value) / 100.0;
            if (a.AttributeId == AttrRxnME)          rxnRigBonus[a.TypeId]     = Math.Abs(a.Value) / 100.0;
            if (a.AttributeId == AttrMfgRigTE)       mfgRigTimeBonus[a.TypeId] = Math.Abs(a.Value) / 100.0;
            if (a.AttributeId == AttrRxnRigTE)       rxnRigTimeBonus[a.TypeId] = Math.Abs(a.Value) / 100.0;
            if (a.AttributeId == AttrRigLowsecMult)  rigLowsecMult[a.TypeId]   = a.Value;
            if (a.AttributeId == AttrRigNullsecMult) rigNullsecMult[a.TypeId]  = a.Value;
        }

        // Load rig type names so we can determine which category each rig applies to.
        var rigTypeNames = rigTypeIds.Count > 0
            ? await db.SdeTypes.AsNoTracking()
                .Where(t => rigTypeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct)
            : new Dictionary<int, string>();

        var rigCategoryKeys = rigTypeIds.ToDictionary(
            id => id,
            id => rigTypeNames.TryGetValue(id, out var n) ? RigCategoryFromName(n) : "");

        // Category assignments — all categories (manufacturing + reactions).
        var assignments = await db.IndyCategoryAssignments.AsNoTracking()
            .Where(a => a.ParkId == defaultPark.Id && a.StructureId.HasValue)
            .ToListAsync(ct);

        var structByCategory = assignments
            .GroupBy(a => a.CategoryKey)
            .ToDictionary(g => g.Key,
                g => structures.FirstOrDefault(s => s.Id == g.First().StructureId!.Value));

        // Per-item exception overrides take precedence over category assignment.
        var itemExceptions = await db.IndyItemExceptions.AsNoTracking()
            .Where(e => e.ParkId == defaultPark.Id && e.StructureId.HasValue)
            .ToListAsync(ct);
        var itemOverrides = itemExceptions
            .ToDictionary(e => e.TypeId, e => structures.FirstOrDefault(s => s.Id == e.StructureId!.Value));

        // Structure time role bonuses, keyed by lowercased structure type name (which
        // matches IndyStructure.StructureTypeKey, e.g. "raitaru"). These are stored as
        // multipliers on the structure type (e.g. Raitaru 0.85 → −15% manufacturing time).
        var roleTimeRows = await db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => a.AttributeId == AttrStrEngTime || a.AttributeId == AttrStrRxnTime)
            .ToListAsync(ct);
        var roleTimeNames = await db.SdeTypes.AsNoTracking()
            .Where(t => roleTimeRows.Select(r => r.TypeId).Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name.ToLowerInvariant(), ct);
        var mfgRoleTimeByKey = new Dictionary<string, double>();
        var rxnRoleTimeByKey = new Dictionary<string, double>();
        foreach (var r in roleTimeRows)
        {
            if (!roleTimeNames.TryGetValue(r.TypeId, out var key)) continue;
            if (r.AttributeId == AttrStrEngTime) mfgRoleTimeByKey[key] = r.Value;
            else                                 rxnRoleTimeByKey[key] = r.Value;
        }

        double StructureRoleTime(IndyStructure? s, bool isReaction)
        {
            if (s is null) return 1.0;
            var key = s.StructureTypeKey.ToLowerInvariant();
            var map = isReaction ? rxnRoleTimeByKey : mfgRoleTimeByKey;
            return map.TryGetValue(key, out var m) ? m : 1.0;
        }

        // Base blueprint activity times (seconds per run), keyed by (blueprintTypeId, activity).
        var activityTimes = await db.HoboBlueprintActivities.AsNoTracking()
            .Where(a => a.Activity == "manufacturing" || a.Activity == "reaction")
            .ToListAsync(ct);
        var timeByBp = activityTimes
            .GroupBy(a => (a.TypeId, a.Activity))
            .ToDictionary(g => g.Key, g => (double)g.First().Time);

        double SecMult(IndyStructure s, int rigTypeId) => s.SecurityClass switch
        {
            "lowsec"   => rigLowsecMult.TryGetValue(rigTypeId, out var lm) ? lm : 1.9,
            "nullsec"  => rigNullsecMult.TryGetValue(rigTypeId, out var nm) ? nm : 2.1,
            "wormhole" => rigNullsecMult.TryGetValue(rigTypeId, out var wm) ? wm : 2.1,
            _          => 1.0,
        };

        // Filter rigs by category key; L-Set generic reactor rigs match any react_* item.
        double RigBonus(IndyStructure? s, string itemCategoryKey, Dictionary<int, double> bonusAttr)
        {
            if (s is null) return 0;
            return rigs.Where(r =>
                {
                    if (r.StructureId != s.Id || r.RigTypeId == 0) return false;
                    var rigCat = rigCategoryKeys.GetValueOrDefault(r.RigTypeId, "");
                    return IndyRigMatching.RigApplies(rigCat, itemCategoryKey);
                })
                .Sum(r => bonusAttr.TryGetValue(r.RigTypeId, out var b) ? b * SecMult(s, r.RigTypeId) : 0.0);
        }

        IndyStructure? StructureFor(string catKey, int typeId)
        {
            if (itemOverrides.TryGetValue(typeId, out var ov)) return ov;
            if (string.IsNullOrEmpty(catKey)) return null;
            return structByCategory.TryGetValue(catKey, out var s) ? s : null;
        }

        // Map solar system names → IDs for cost index lookup.
        var sysNames = structures.Select(s => s.SystemName)
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sysNameToId = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => sysNames.Contains(s.Name))
            .ToDictionaryAsync(s => s.Name.ToUpperInvariant(), s => s.SolarSystemId, ct);

        var costIndexRows = await db.IndustryCostIndices.AsNoTracking().ToListAsync(ct);
        var costIndexMap  = costIndexRows.ToDictionary(c => (c.SolarSystemId, c.Activity), c => c.CostIndex);

        double GetCostIndex(IndyStructure? s, string activity)
        {
            if (s is null || string.IsNullOrWhiteSpace(s.SystemName)) return 0;
            return sysNameToId.TryGetValue(s.SystemName.ToUpperInvariant(), out var sid)
                && costIndexMap.TryGetValue((sid, activity), out var ci) ? ci : 0;
        }

        bool IsUpwell(IndyStructure? s) => s is not null && s.StructureTypeKey != "npc_station";

        // Adjusted prices for EIV calculation.
        var adjustedPrices = await db.EsiAdjustedPrices.AsNoTracking()
            .ToDictionaryAsync(p => p.TypeId, p => p.AdjustedPrice, ct);

        // Market prices for leaf-node materials (what we buy).
        var defaultSettings = await db.MarketDefaultSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        int? mktConfigId    = defaultSettings?.ManufacturingConfigId;
        string mktPriceType = defaultSettings?.ManufacturingPriceType ?? "Sell";
        // Opt-in cheaper-of-buy: only replace building with buying when the user has enabled it, and
        // then only when market value <= threshold% of build. Items that can't be costed are still
        // bought regardless. Off by default (bouncy/thin component markets make it unreliable).
        bool    buyWhenCheaper  = defaultSettings?.PurchaseWhenCheaper ?? false;
        decimal buyThresholdPct = defaultSettings?.PurchaseThresholdPct ?? 100m;

        if (!mktConfigId.HasValue)
        {
            var first = await db.MarketPricingConfigs.AsNoTracking()
                .Where(c => c.IsEnabled).OrderBy(c => c.SortOrder).FirstOrDefaultAsync(ct);
            mktConfigId = first?.Id;
        }

        var marketPrices = new Dictionary<int, decimal>();
        if (mktConfigId.HasValue)
        {
            var prices = await db.MarketItemPrices.AsNoTracking()
                .Where(p => p.ConfigId == mktConfigId.Value).ToListAsync(ct);
            foreach (var p in prices)
            {
                marketPrices[p.TypeId] = (decimal)(mktPriceType switch
                {
                    "Buy"      => p.BuyPrice,
                    "Sell"     => p.SellPrice,
                    "Midpoint" => p.Midpoint,
                    _          => p.SellPrice,
                });
            }
        }

        // Load all blueprint products (manufacturing + reaction only). Only PUBLISHED
        // blueprints — a handful of products (e.g. Tungsten Carbide) also have an
        // unpublished "Test Reaction Blueprint" with a tiny output quantity that would
        // otherwise be picked and inflate the per-unit cost ~500x.
        var allProducts = await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => (p.Activity == "manufacturing" || p.Activity == "reaction")
                     && db.SdeTypes.Any(t => t.TypeId == p.TypeId && t.Published))
            .ToListAsync(ct);

        // productMap: productTypeId → the (single, published) SdeBlueprintProduct record
        var productMap = allProducts
            .GroupBy(p => p.ProductTypeId)
            .ToDictionary(g => g.Key, g => g.First());

        // T2 items (MetaGroupId = 2) cap at ME 3 (invention limit).
        // Faction items (MetaGroupId = 4) are always ME 0 BPCs — not researchable.
        // Load both as HashSets for O(1) lookup in the cost loop below.
        var productTypeIds = productMap.Keys.ToList();
        var metaGroupTypes = await db.SdeTypes.AsNoTracking()
            .Where(t => productTypeIds.Contains(t.TypeId) && (t.MetaGroupId == 2 || t.MetaGroupId == 4))
            .Select(t => new { t.TypeId, t.MetaGroupId })
            .ToListAsync(ct);
        var t2TypeIds      = metaGroupTypes.Where(t => t.MetaGroupId == 2).Select(t => t.TypeId).ToHashSet();
        // Titans (30), supercarriers (659), the Keepstar and the Fortizar get ME9; other items
        // follow the standard rule.
        var titanKeepstarIds = (await db.SdeTypes.AsNoTracking()
            .Where(t => productTypeIds.Contains(t.TypeId)
                     && (t.GroupId == IndustryMe.TitanGroupId
                      || t.GroupId == IndustryMe.SuperGroupId
                      || t.TypeId  == IndustryMe.KeepstarTypeId
                      || t.TypeId  == IndustryMe.FortizarTypeId))
            .Select(t => t.TypeId).ToListAsync(ct)).ToHashSet();

        // BPO-sourced blueprints: the blueprint type is buyable on the market (has a market group)
        // OR is invented from a source blueprint that is buyable (T2 from a T1 BPO). Anything else
        // is a BPC that must be bought from contracts — mirrors the Industry Opportunities filter.
        var bpTypeIdList = allProducts.Select(p => p.TypeId).Distinct().ToList();
        var marketBlueprints = (await db.SdeTypes.AsNoTracking()
            .Where(t => bpTypeIdList.Contains(t.TypeId) && t.MarketGroupId != null)
            .Select(t => t.TypeId).ToListAsync(ct)).ToHashSet();

        // Products in the BPC-only loot tiers — Storyline (3), Faction (4), Officer (5), Deadspace (6)
        // — never have an obtainable BPO, even when their blueprint carries a market group (some
        // faction module blueprints do, e.g. Imperial Navy Bastion Module Blueprint). Their build
        // cost must include the purchased BPC, so exclude those blueprints from the BPO set.
        var bpcOnlyProductIds = (await db.SdeTypes.AsNoTracking()
            .Where(t => productTypeIds.Contains(t.TypeId) && t.MetaGroupId >= 3 && t.MetaGroupId <= 6)
            .Select(t => t.TypeId).ToListAsync(ct)).ToHashSet();
        marketBlueprints.ExceptWith(allProducts
            .Where(p => p.Activity == "manufacturing" && bpcOnlyProductIds.Contains(p.ProductTypeId))
            .Select(p => p.TypeId));
        var inventionRows = await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => p.Activity == "invention")
            .Select(p => new { p.TypeId, p.ProductTypeId })
            .ToListAsync(ct);
        var inventedFromMarket = inventionRows
            .Where(r => marketBlueprints.Contains(r.TypeId))
            .Select(r => r.ProductTypeId).ToHashSet();
        bool BlueprintIsBpoSourced(int bpTypeId) =>
            marketBlueprints.Contains(bpTypeId) || inventedFromMarket.Contains(bpTypeId);

        // ⚠️ Titans, supercarriers, the Keepstar and the Fortizar are costed as BPC purchases
        // even though a BPO exists for each of them. Those BPOs are priced in the hundreds of
        // billions, so treating one as owned and free — which is what "has a BPO, so no blueprint
        // cost" amounts to — was pricing a titan below what anyone can actually build one for.
        //
        // ⚠️ Faction hulls are excluded: they are already loot BPCs at ME0, and a Molok or a
        // Revenant costed as an ME9 researched copy would be as wrong in the other direction.
        var boughtBpcIds = productTypeIds
            .Where(id => titanKeepstarIds.Contains(id) && !bpcOnlyProductIds.Contains(id))
            .ToHashSet();

        // Load all blueprint materials (manufacturing + reaction).
        var allMaterials = await db.SdeBlueprintMaterials.AsNoTracking()
            .Where(m => m.Activity == "manufacturing" || m.Activity == "reaction")
            .ToListAsync(ct);

        // materialsMap: (blueprintTypeId, activity) → list of materials
        var materialsMap = allMaterials
            .GroupBy(m => (m.TypeId, m.Activity))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Type names for result storage.
        var typeNames = await db.SdeTypes.AsNoTracking()
            .Select(t => new { t.TypeId, t.Name })
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        // Type → group and group → (categoryId, name) for per-item structure selection.
        var typeGroupMap = await db.SdeTypes.AsNoTracking()
            .Select(t => new { t.TypeId, t.GroupId })
            .ToDictionaryAsync(t => t.TypeId, ct);
        var groupCatMap = await db.SdeGroups.AsNoTracking()
            .Select(g => new { g.GroupId, g.CategoryId, g.Name })
            .ToDictionaryAsync(g => g.GroupId, ct);

        string ItemCategoryKey(int typeId, bool isReaction)
        {
            if (!typeGroupMap.TryGetValue(typeId, out var tg)) return "";

            if (isReaction)
            {
                return tg.GroupId switch
                {
                    712             => "react_bio_gas",
                    428             => "react_biochemical",
                    // 4932 Unrefined Mineral — the eight Unrefined Tritanium/Pyerite/… products,
                    // reaction-produced and bonused by the composite rig. See IndyRigMatching.
                    429 or 974 or 4096 or 4932 => "react_composite",
                    _               => "",
                };
            }

            if (!groupCatMap.TryGetValue(tg.GroupId, out var gc)) return "";
            return (gc.CategoryId, gc.Name) switch
            {
                // ── Category 6: Ships ────────────────────────────────────────────────
                (6, "Frigate" or "Destroyer" or "Shuttle" or "Corvette" or "Rookie Ship"
                   or "Hauler" or "Mining Barge")                                           => "small_ships",
                // "Special Edition Yachts" — Victorieux, the Opux pair, the YC128 buses. All
                // cruiser-hulled at 115,000 m³. See IndyRigMatching.
                (6, "Cruiser" or "Battlecruiser" or "Combat Battlecruiser"
                   or "Attack Battlecruiser" or "Special Edition Yachts")                   => "medium_ships",
                (6, "Battleship" or "Freighter")                                            => "large_ships",
                // T2 frigates/destroyers; SDE group is "Interdictor" not "Interdiction Destroyer"
                (6, "Interceptor" or "Assault Frigate" or "Covert Ops"
                   or "Electronic Attack Ship" or "Interdictor" or "Tactical Destroyer"
                   or "Logistics Frigate" or "Expedition Frigate"
                   or "Stealth Bomber" or "Command Destroyer" or "Exhumer")                 => "adv_small_ships",
                // T2 cruisers; SDE groups are "Force Recon Ship" / "Combat Recon Ship" not "Recon Ship"
                (6, "Heavy Assault Cruiser" or "Force Recon Ship" or "Combat Recon Ship"
                   or "Heavy Interdiction Cruiser" or "Logistics" or "Command Ship"
                   or "Strategic Cruiser" or "Blockade Runner" or "Deep Space Transport"
                   or "Flag Cruiser" or "Expedition Command Ship")                          => "adv_medium_ships",
                (6, "Marauder" or "Black Ops")                                              => "adv_large_ships",
                // Command Carrier (Ymir etc.) and Lancer Dreadnought are capital-class ships
                (6, "Dreadnought" or "Carrier" or "Force Auxiliary" or "Capital Industrial Ship"
                   or "Supercarrier" or "Titan" or "Command Carrier" or "Lancer Dreadnought"
                   or "Jump Freighter" or "Industrial Command Ship")                        => "capital_ships",
                // ── Other categories ────────────────────────────────────────────────
                (7, _)          => "modules_equipment",

                // ⚠️ A structure RIG takes the structure rig, not the equipment one, and this
                // is the material side of it. Every rig group in category 66 is named
                // "... Rig <size> - ..."; no module, weapon or service-module group contains
                // " Rig ". See IndyRigMatching.ItemCategoryKey for the measurement.
                //
                // ⚠️ THIS MAPPING EXISTS THREE TIMES — here, in ProductionCalculatorService
                // and in IndyRigMatching — and they have to agree. Fixing one and not the
                // others is how a structure rig came to be costed against an equipment rig it
                // could never match.
                (66, var n) when n.Contains(" Rig ")                                    => "structure_ammo",

                // The rest of category 66: service modules, weapons, fitting modules.
                (66, _)         => "modules_equipment",
                // T3 subsystems — Loki/Tengu/Legion/Proteus. Previously unmapped, so every
                // subsystem threw "cannot be assigned to a structure" and lost its chain cost.
                (32, _)         => "modules_equipment",
                // Implants and boosters (20), starbase structures and POS modules (23),
                // and sovereignty / infrastructure hub upgrades (39). Each category holds
                // nothing but its own kind, so matching the whole category is safe.
                (20, _)         => "modules_equipment",
                (23, _)         => "modules_equipment",
                (39, _)         => "modules_equipment",
                // Special Edition Assets — the Deactivated Station Key Pass is the only
                // manufacturable member of the category. See IndyRigMatching.
                (63, _)         => "modules_equipment",
                // Sovereignty Structures (40) and Orbitals (46) take the structure rig. Five
                // manufacturable members between them, all structures. See IndyRigMatching.
                (40, _)         => "structure_ammo",
                (46, _)         => "structure_ammo",
                // Category 2 (Celestial) is a junk drawer — planets, suns, wrecks, 1,697
                // non-interactable objects. Only its container groups are manufacturable.
                (2, var celestial) when celestial.Contains("Container") => "modules_equipment",
                // Mutaplasmids. Matched by group, not category — category 17 also holds
                // fuel blocks and capital components, which have their own rigs below.
                (17, "Mutaplasmids")                                    => "modules_equipment",
                // Abyssal, jump and warp matrix filaments. The other filament groups in
                // category 17 have no manufacturable members, so this cannot reach them.
                (17, var fil) when fil.Contains("Filament")             => "modules_equipment",
                // Individually classified — these sit in category 17's junk-drawer groups,
                // so the type id is the only thing precise enough to match on.
                // 76203 Stellar Transmuter Datacore, 76204 Transport Relay Datacore,
                // 29226 Basic Robotics, 3585 Mangled Sansha Data Analyzer,
                // 29202 Modified Augumene Antidote.
                _ when typeId is 76203 or 76204 or 29226                => "structure_ammo",
                _ when typeId == 3585                                   => "modules_equipment",
                _ when typeId == 29202                                  => "ammo_charges",
                // 88172-88177 Narrow/Mid/Wideband Emission Amplifiers and Limiters —
                // a contiguous block holding exactly those six and nothing else.
                _ when typeId is >= 88172 and <= 88177                  => "adv_components",
                (8, _)          => "ammo_charges",
                (18, _) or (87, _)                                                          => "drones_fighters",
                _ when tg.GroupId == 1136                                  => "structure_ammo",   // Fuel Blocks
                // Capital Construction Components (group 873) are category 17, not 4 —
                // the old guard matched nothing, so capital parts were costed with the
                // advanced-component rig. "Advanced" is excluded because group 913 is
                // genuinely bonused by the advanced rig. See IndyRigMatching.
                _ when gc.Name.Contains("Capital") && gc.Name.Contains("Component")
                                                   && !gc.Name.Contains("Advanced")
                                                                           => "capital_components",
                // Group 536 "Structure Components" ahead of the generic match, which claims it
                // on the group name alone. See IndyRigMatching.
                _ when tg.GroupId == 536                                    => "structure_ammo",
                _ when gc.Name.Contains("Component")                        => "adv_components",
                _ when gc.CategoryId is 22 or 65                           => "structure_ammo",
                // R.A.M. items and Data Interfaces are manufactured at standard facilities
                _ when gc.CategoryId == 17 && gc.Name is "Tool" or "Data Interfaces" => "modules_equipment",
                _                                                           => ""
            };
        }

        // ── Topological sort via iterative DFS post-order ─────────────────────

        var visited  = new HashSet<int>();
        var ordering = new List<int>();

        void Visit(int typeId)
        {
            if (!visited.Add(typeId)) return;

            if (productMap.TryGetValue(typeId, out var prod)
                && materialsMap.TryGetValue((prod.TypeId, prod.Activity), out var mats))
            {
                foreach (var m in mats)
                    Visit(m.MaterialTypeId);
            }

            ordering.Add(typeId); // post-order: leaves come first
        }

        foreach (var typeId in productMap.Keys)
            Visit(typeId);

        // Per-run BPC contract prices grouped by blueprint type → [(ME, per-run price)]. A BPC-only
        // item is built by consuming one run of its (purchased) BPC; we pick the ME that minimises
        // total cost. Empty for BPCs we've never seen on contracts.
        var bpcPerRun = (await db.ContractBpcPrices.AsNoTracking().ToListAsync(ct))
            .Select(c => new { c.TypeId, c.Me, Price = ContractPricing.EffectivePerRun(c) })
            .Where(x => x.Price is > 0m)
            .GroupBy(x => x.TypeId)
            .ToDictionary(g => g.Key, g => g.Select(x => (Me: x.Me, PerRun: x.Price!.Value)).ToList());

        // User price overrides. A market override replaces the buy price (so it drives the
        // build-vs-buy comparison and any use of this item as a leaf); a contract override
        // replaces the per-run BPC price at every ME. Build-cost overrides are applied per type
        // inside the loop below.
        var overrides = await db.PriceOverrides.AsNoTracking().ToDictionaryAsync(o => o.TypeId, ct);
        foreach (var o in overrides.Values)
        {
            if (o.MarketValue.HasValue)   marketPrices[o.TypeId] = o.MarketValue.Value;
            if (o.ContractValue.HasValue) bpcPerRun[o.TypeId]    = [(0, o.ContractValue.Value)];
        }

        // ── Bottom-up cost calculation ────────────────────────────────────────
        // rawMatCosts: pure market-purchase cost of all leaf inputs (no job fees anywhere).
        // totalJobCosts: sum of every job fee in the build chain per unit of this item.
        // unitCosts = rawMatCosts + totalJobCosts = TotalCost.
        // Keeping them separate means MaterialCost and JobCost in BuildCosts match what the
        // Production Calculator shows, rather than folding sub-component job fees into MaterialCost.

        var unitCosts     = new Dictionary<int, decimal>();
        var rawMatCosts   = new Dictionary<int, decimal>(); // leaf-input market cost only
        var totalJobCosts = new Dictionary<int, decimal>(); // all job fees through the chain
        var buildSeconds  = new Dictionary<int, double>();  // time to build ONE unit of this item
        var boughtTypes   = new HashSet<int>();             // items cheaper to buy than build

        // Per-type build recipe captured during the pass below, then walked as a FULL CHAIN (not a
        // per-unit rollup) to get accurate quantities — building 2 items in one job may need 9 of a
        // sub-material, not 2 × ceil(4.5) = 10. See RecomputeFullChain at the end.
        var recOutputQty = new Dictionary<int, int>();               // units produced per run
        var recMeFactor  = new Dictionary<int, double>();            // material factor used to build it
        var recMeLevel   = new Dictionary<int, int>();               // the ME level behind that factor
        var recIsReaction = new HashSet<int>();                      // made by a reaction, not a job
        var recJobPerRun = new Dictionary<int, decimal>();           // job fee for one run
        var recBpcPerRun = new Dictionary<int, decimal>();           // BPC contract cost per run (0 if none)
        var recMaterials = new Dictionary<int, List<(int Mat, int Qty)>>();
        var recOverride  = new Dictionary<int, decimal>();           // pinned build cost (fixed leaf)

        foreach (var typeId in ordering)
        {
            if (!productMap.TryGetValue(typeId, out var prod))
            {
                // Leaf node — buy from market.
                unitCosts[typeId]     = marketPrices.TryGetValue(typeId, out var mp) ? mp : 0m;
                rawMatCosts[typeId]   = unitCosts[typeId];
                totalJobCosts[typeId] = 0m;
                continue;
            }

            bool   isReaction  = prod.Activity == "reaction";
            int    bpTypeId    = prod.TypeId;
            int    outputQty   = Math.Max(1, prod.Quantity);
            var    key         = (bpTypeId, prod.Activity);

            if (!materialsMap.TryGetValue(key, out var materials) || materials.Count == 0)
            {
                unitCosts[typeId]     = marketPrices.TryGetValue(typeId, out var mp2) ? mp2 : 0m;
                rawMatCosts[typeId]   = unitCosts[typeId];
                totalJobCosts[typeId] = 0m;
                continue;
            }

            string catKey       = ItemCategoryKey(typeId, isReaction);
            var    structure    = StructureFor(catKey, typeId);
            // Default ME assumption: ME10, except T2 (ME3), BPC-only/faction (ME0), titans &
            // Keepstars & Fortizars (ME9), reactions (ME0). Shared with the Production Calculator
            // via IndustryMe.
            // A BPO exists, but nobody uses it — add the copy's price further down. See
            // boughtBpcIds. ⚠️ Deliberately NOT folded into bpcItem: that flag also decides
            // whether an item is costable at all, and routing a titan through the BPC-only path
            // made "no contract price" mean "cannot be built, buy it at market" — which flipped
            // every titan to bought and moved the stored figure by far more than a blueprint.
            bool   boughtBpc    = boughtBpcIds.Contains(typeId);
            bool   bpcItem      = !isReaction && !BlueprintIsBpoSourced(bpTypeId);
            int    defaultMe    = IndustryMe.DefaultMe(isReaction, bpcItem,
                                      t2TypeIds.Contains(typeId), titanKeepstarIds.Contains(typeId));
            double bpMeFactor   = IndustryMe.Factor(defaultMe);
            double rigMeBonus   = isReaction ? RigBonus(structure, catKey, rxnRigBonus)
                                             : RigBonus(structure, catKey, mfgRigBonus);
            double matRoleBonus = (!isReaction && IsUpwell(structure)) ? UpwellMaterialBonus : 0.0;
            double meFactor     = bpMeFactor * (1.0 - rigMeBonus) * (1.0 - matRoleBonus);

            // EIV uses base (ME0) quantities and is independent of the ME chosen.
            double eivRun = 0.0;
            foreach (var mat in materials)
                eivRun += mat.Quantity * (adjustedPrices.TryGetValue(mat.MaterialTypeId, out var ap) ? ap : 0);

            // Job cost — driven by EIV, independent of ME.
            string activity   = isReaction ? "reaction" : "manufacturing";
            double costIndex  = GetCostIndex(structure, activity);
            double facTax     = structure is not null ? (double)structure.FacilityTax / 100.0 : 0.0;
            double roleBonus  = IsUpwell(structure) ? UpwellRoleBonus : 1.0;
            decimal thisJobRun = Math.Round((decimal)(eivRun * costIndex * roleBonus), 0)
                               + Math.Round((decimal)(eivRun * (facTax + SccSurcharge)), 0);

            // Material + sub-component-job cost for one run at a given material factor.
            (decimal Raw, decimal SubJob) RunAt(double mf)
            {
                decimal raw = 0m, sub = 0m;
                foreach (var mat in materials)
                {
                    int q = Math.Max(1, (int)Math.Ceiling(mat.Quantity * mf));
                    raw += q * (rawMatCosts.TryGetValue(mat.MaterialTypeId, out var rm) ? rm : 0m);
                    sub += q * (totalJobCosts.TryGetValue(mat.MaterialTypeId, out var tj) ? tj : 0m);
                }
                return (raw, sub);
            }
            double structRig = (1.0 - rigMeBonus) * (1.0 - matRoleBonus);

            bool    bpcOnly = bpcItem;
            decimal buildRawRun, buildSubJobRun, bpcRun = 0m;
            bool    costable = true;
            double  usedMeFactor = meFactor;   // factor the full-chain walk will reuse

            if (bpcOnly && bpcPerRun.TryGetValue(bpTypeId, out var meOptions) && meOptions.Count > 0)
            {
                // Consumes one run of a purchased BPC. Pick the ME that minimises materials + BPC.
                decimal bestTotal = decimal.MaxValue; (decimal Raw, decimal SubJob) best = default;
                foreach (var (me, perRun) in meOptions)
                {
                    double mf = (1.0 - me / 100.0) * structRig;
                    var r = RunAt(mf);
                    decimal total = r.Raw + r.SubJob + perRun;
                    if (total < bestTotal) { bestTotal = total; best = r; bpcRun = perRun; usedMeFactor = mf; }
                }
                (buildRawRun, buildSubJobRun) = best;
            }
            else if (bpcOnly)
            {
                // BPC-only but never seen on contracts — we can't cost the build; prefer buying.
                costable = false;
                usedMeFactor = structRig;
                (buildRawRun, buildSubJobRun) = RunAt(structRig);   // ME0 placeholder
            }
            else
            {
                // BPO-sourced (or reaction): researched-BPO ME assumption, no BPC cost.
                (buildRawRun, buildSubJobRun) = RunAt(meFactor);

                // …except the few nobody owns the original of. Add the copy's price at this
                // item's ME and change nothing else: the item stays costable, stays built, and
                // moves by exactly the price of one blueprint copy.
                if (boughtBpc && bpcPerRun.TryGetValue(bpTypeId, out var opts) && opts.Count > 0)
                    bpcRun = opts.OrderBy(o => Math.Abs(o.Me - defaultMe))
                                 .ThenBy(o => o.PerRun)
                                 .First().PerRun;
            }

            // Capture the recipe for the full-chain recompute (quantities rounded per whole job).
            recOutputQty[typeId] = outputQty;
            recMeFactor[typeId]  = usedMeFactor;
            recMeLevel[typeId]   = defaultMe;
            if (isReaction) recIsReaction.Add(typeId);
            recJobPerRun[typeId] = thisJobRun;
            recBpcPerRun[typeId] = bpcRun;
            recMaterials[typeId] = materials.Select(m => (m.MaterialTypeId, m.Quantity)).ToList();

            decimal buildRawPerUnit = (buildRawRun + bpcRun) / outputQty;
            decimal buildJobPerUnit = (buildSubJobRun + thisJobRun) / outputQty;
            decimal buildTotal      = buildRawPerUnit + buildJobPerUnit;

            // A build-cost override pins the build side to a fixed value (counted entirely as
            // material, no job fee) — cheaper-of vs buying still applies below.
            if (overrides.TryGetValue(typeId, out var ov) && ov.BuildCost.HasValue)
            {
                costable        = true;
                buildRawPerUnit = ov.BuildCost.Value;
                buildJobPerUnit = 0m;
                buildTotal      = ov.BuildCost.Value;
                recOverride[typeId] = ov.BuildCost.Value;   // fixed-value leaf in the full-chain walk
            }

            // Cheaper-of build vs buy the finished item on the configured market. When buying wins
            // (or the build can't be costed), the item becomes a purchased leaf — its cost is the
            // buy price and it carries no job fees for its parents.
            decimal buyPrice = marketPrices.TryGetValue(typeId, out var fin) ? fin : 0m;
            bool    cheaperToBuy = buyWhenCheaper && buyPrice <= buildTotal * buyThresholdPct / 100m;
            if (buyPrice > 0 && (!costable || cheaperToBuy))
            {
                unitCosts[typeId]     = buyPrice;
                rawMatCosts[typeId]   = buyPrice;
                totalJobCosts[typeId] = 0m;
                boughtTypes.Add(typeId);
            }
            else
            {
                unitCosts[typeId]     = buildTotal;
                rawMatCosts[typeId]   = buildRawPerUnit;
                totalJobCosts[typeId] = buildJobPerUnit;
            }

            // Build time for ONE unit. A manufacturing job for this item ties up only its
            // own slot (sub-components are separate jobs), so this is not a chain sum:
            //   baseRunTime × TE × skills × structureRoleBonus × (1 − rigTimeBonus) ÷ output.
            double baseRunTime = timeByBp.TryGetValue(key, out var brt) ? brt : 0.0;
            if (baseRunTime > 0)
            {
                double teFactor    = isReaction ? 1.0 : MfgTimeEfficiency;
                double skillFactor = isReaction ? RxnSkillFactor : MfgSkillFactor;
                double roleTime    = StructureRoleTime(structure, isReaction);
                double rigTime     = isReaction ? RigBonus(structure, catKey, rxnRigTimeBonus)
                                                : RigBonus(structure, catKey, mfgRigTimeBonus);
                double rigFactor   = Math.Max(0.0, 1.0 - rigTime);
                buildSeconds[typeId] = baseRunTime * teFactor * skillFactor * roleTime * rigFactor / outputQty;
            }
        }

        // ── Full-chain recompute ──────────────────────────────────────────────
        {
            // Cost every item through the Production Calculator itself, so the stored figure and
            // what the calculator shows cannot drift apart. The context is loaded once — it is
            // the same for every item and is nearly all of a plan's cost, so paying it per item
            // would turn a minute into twenty.
            var calc = new ProductionCalculatorService(
                scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>());
            var planContext = await calc.LoadContextAsync(defaultPark.Id, ct);

            // ⚠️ This is where the titan fix actually lands. The stored cost comes from the
            // calculator, not from the pass above, so telling the pass to include a BPC and not
            // telling the calculator would have changed nothing anyone can see.
            planContext.AlwaysBpcTypes = boughtBpcIds;

            // ── Batch size ────────────────────────────────────────────────────
            // Costing one run at a time charges every unit for a whole run's worth of rounding,
            // and leaves a big surplus that then has to be credited back at a per-unit figure
            // computed the same inflated way — which compounds down the chain.
            //
            // Reactions and components are built in batches in practice, so they are costed at
            // 100 runs and divided by what that yields. The saving is the ceil() on material
            // quantities being applied once to the batch rather than once per run, so it
            // amortises: nearly all of it appears in the first handful of runs and 100 versus
            // 200 barely moves the per-unit figure.
            var marketGroups = await db.SdeMarketGroups.AsNoTracking()
                .Select(g => new { g.MarketGroupId, g.ParentGroupId, g.Name })
                .ToListAsync(ct);
            var mgById = marketGroups.ToDictionary(g => g.MarketGroupId);

            // The parts of the market tree whose items are built in batches in practice.
            // Anything beneath one of these paths is costed as a batch.
            string[][] batchPaths =
            [
                // "Manufacture & Research", not "Manufacturing" — the SDE's name, and getting it
                // wrong silently batched none of the components.
                ["Manufacture & Research", "Components"],
                ["Drones"],
                ["Ammunition & Charges"],
                ["Ship and Module Modifications"],
                ["Ship Equipment"],
                ["Structure Equipment"],
                ["Ships", "Shuttles"],
                ["Ships", "Frigates"],
                ["Ships", "Destroyers"],
            ];

            var childrenOf = marketGroups
                .Where(g => g.ParentGroupId != null)
                .GroupBy(g => g.ParentGroupId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(x => x.MarketGroupId).ToList());

            var batchGroupIds = new HashSet<int>();
            foreach (var path in batchPaths)
            {
                // Walk the named path down from a top-level group, then take everything under it.
                var current = marketGroups
                    .Where(g => g.ParentGroupId == null && g.Name == path[0])
                    .Select(g => g.MarketGroupId)
                    .ToList();

                foreach (var segment in path.Skip(1))
                    current = current
                        .SelectMany(id => childrenOf.GetValueOrDefault(id, []))
                        .Where(id => mgById[id].Name == segment)
                        .ToList();

                if (current.Count == 0)
                {
                    _errorLogger.Log("BuildCostService", "batch groups",
                        $"market group path \"{string.Join(" > ", path)}\" matched nothing — " +
                        "its items will be costed one run at a time.");
                    continue;
                }

                var stack = new Stack<int>(current);
                while (stack.Count > 0)
                {
                    var id = stack.Pop();
                    if (!batchGroupIds.Add(id)) continue;      // also guards a malformed cycle
                    foreach (var child in childrenOf.GetValueOrDefault(id, []))
                        stack.Push(child);
                }
            }

            var componentTypes = (await db.SdeTypes.AsNoTracking()
                    .Where(t => t.MarketGroupId != null)
                    .Select(t => new { t.TypeId, MarketGroupId = t.MarketGroupId!.Value })
                    .ToListAsync(ct))
                .Where(t => batchGroupIds.Contains(t.MarketGroupId))
                .Select(t => t.TypeId)
                .ToHashSet();

            // Named exceptions: not things anyone builds a hundred of at a time.
            var batchByName = new Dictionary<string, int>
            {
                ["Enhanced Neurolink Protection Cell"] = 1,
                ["Neurolink Protection Cell"]          = 10,
                ["Capital Core Temperature Regulator"] = 10,
            };
            var batchExceptions = typeNames
                .Where(kv => batchByName.ContainsKey(kv.Value))
                .ToDictionary(kv => kv.Key, kv => batchByName[kv.Value]);

            // No clamp to the blueprint's maxProductionLimit here. That field looks like a per-job
            // run cap and is not one — the only limit the game imposes on an original is thirty
            // days of run time, which a hundred runs of anything costed here comes nowhere near.
            // Clamping to it costed capital components in batches of 40 rather than 100.
            int BatchRuns(int typeId) =>
                batchExceptions.TryGetValue(typeId, out var runs) ? runs
              : recIsReaction.Contains(typeId) || componentTypes.Contains(typeId) ? 100
              : 1;

            // Order matters, and it is what makes crediting leftovers possible at all.
            //
            // The calculator values leftover sub-components at their build cost. Costing items in
            // arbitrary order means reading the PREVIOUS pass's figures — the inflated ones this
            // is replacing — and over-crediting against them, which drove 1,522 items negative.
            //
            // Costing a component before anything that consumes it means every leftover is valued
            // at a figure already recomputed in this same pass. Leftovers only arise where a
            // blueprint yields more than one unit per run, and those outputs always sit strictly
            // below their consumers in the material graph, so such an order exists.
            var builtTypes = productMap.Keys
                .Where(t => !boughtTypes.Contains(t) && recMaterials.ContainsKey(t))
                .ToHashSet();

            var consumers = new Dictionary<int, List<int>>();
            var remaining = builtTypes.ToDictionary(t => t, _ => 0);
            foreach (var t in builtTypes)
                foreach (var (mat, _) in recMaterials[t])
                    if (builtTypes.Contains(mat))
                    {
                        if (!consumers.TryGetValue(mat, out var list)) consumers[mat] = list = [];
                        list.Add(t);
                        remaining[t]++;
                    }

            var ready = new Queue<int>(remaining.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var order = new List<int>(builtTypes.Count);
            while (ready.Count > 0)
            {
                var t = ready.Dequeue();
                order.Add(t);
                if (!consumers.TryGetValue(t, out var cons)) continue;
                foreach (var c in cons)
                    if (--remaining[c] == 0) ready.Enqueue(c);
            }

            // Whatever is left sits in a cycle. EVE's material graph is not guaranteed acyclic,
            // and composite reactions consuming intermediate reaction products are exactly where
            // that shows up. Cost them last; their leftovers fall back on the previous figure,
            // which is the best available when a thing transitively depends on itself.
            //
            // ⚠️ Deliberately not logged as an error. This is the designed handling of a graph EVE
            // genuinely has, it fires on every recalculation, and a recurring note about working
            // behaviour buries the faults the log exists to surface. The count goes on StatusText,
            // where it is visible while the recalculation is being watched and gone afterwards.
            var ordered = order.ToHashSet();
            var cyclic  = builtTypes.Where(t => !ordered.Contains(t)).ToList();
            order.AddRange(cyclic);
            if (cyclic.Count > 0)
                StatusText = $"Build costs: calculating… ({cyclic.Count} in a dependency cycle, costed last)";

            foreach (var typeId in order)
            {
                int outQ = recOutputQty.TryGetValue(typeId, out var oq) ? Math.Max(1, oq) : 1;
                var me   = recMeLevel.TryGetValue(typeId, out var m) ? m : 10;

                try
                {
                    // Cost a realistic batch, then divide by what it yields.
                    var plan = calc.Calculate(
                        [new ProductionQueueEntry
                        {
                            TypeId   = typeId,
                            Quantity = outQ * BatchRuns(typeId),
                            MeLevel  = me,
                        }],
                        planContext);

                    // Unclassified items no longer abort the plan, so they arrive as
                    // warnings instead of an exception. Still logged — a gap in the rig
                    // rules is worth knowing about — but the cost below is now real
                    // rather than a stale estimate from the previous pass.
                    if (plan.Warnings.Count > 0)
                        _errorLogger.Log("BuildCostService", $"chain cost for type {typeId}",
                            string.Join("; ", plan.Warnings.Take(5))
                            + (plan.Warnings.Count > 5 ? $"; …and {plan.Warnings.Count - 5} more" : ""));

                    var produced = Math.Max(1, plan.FinalProducts.Count > 0
                        ? plan.FinalProducts[0].QuantityProduced
                        : outQ * BatchRuns(typeId));

                    // Net of leftovers: over-produced sub-components are stock, not cost.
                    rawMatCosts[typeId]   = plan.TotalRawMaterialCost / produced;
                    totalJobCosts[typeId] = plan.TotalJobCost / produced;
                    unitCosts[typeId]     = plan.NetCost / produced;

                    // Publish it into the context, so everything costed after this — which, by
                    // the ordering above, is everything that consumes it — values leftovers of
                    // this component at the figure just computed rather than the stale one.
                    planContext.BuildCostLookup[typeId] = unitCosts[typeId];
                }
                catch (Exception ex)
                {
                    // One unplannable item must not lose the whole recalculation. It keeps the
                    // per-unit estimate from the pass above rather than getting no cost at all.
                    _errorLogger.Log("BuildCostService", $"chain cost for type {typeId}", ex);
                }
            }
        }

        // ── Persist results ───────────────────────────────────────────────────

        using var handle = _log.StartCall(defaultPark.Name, "build.costs");
        var now     = DateTime.UtcNow;
        var results = productMap.Keys
            .Where(tid => unitCosts.ContainsKey(tid))
            .Select(tid => new BuildCost
            {
                TypeId       = tid,
                TypeName     = typeNames.TryGetValue(tid, out var n) ? n : "",
                TotalCost    = unitCosts[tid],
                MaterialCost = rawMatCosts.TryGetValue(tid, out var rm) ? rm : 0m,
                JobCost      = totalJobCosts.TryGetValue(tid, out var tj) ? tj : 0m,
                BuildSeconds = buildSeconds.TryGetValue(tid, out var bs) ? bs : 0.0,
                Bought       = boughtTypes.Contains(tid),
                UpdatedAt    = now,
            })
            .ToList();

        await db.BuildCosts.ExecuteDeleteAsync(ct);
        db.BuildCosts.AddRange(results);
        await db.SaveChangesAsync(ct);

        handle.Complete(true, results.Count, $"{results.Count:N0} items");
        StatusText = $"Build costs: last updated {DateTimeOffset.Now:t} ({results.Count:N0} items)";

        if (AfterRecalculate is not null)
        {
            try { await AfterRecalculate(ct); }
            catch (Exception ex) { _errorLogger.Log("BuildCostService", "AfterRecalculate", ex); }
        }
    }

    // ── ESI JSON DTOs ─────────────────────────────────────────────────────────

    private record EsiMarketPriceDto(
        [property: JsonPropertyName("type_id")]        int     TypeId,
        [property: JsonPropertyName("adjusted_price")] double  AdjustedPrice,
        [property: JsonPropertyName("average_price")]  double? AveragePrice);

    private record EsiIndustryCostIndexDto(
        [property: JsonPropertyName("activity")]   string Activity,
        [property: JsonPropertyName("cost_index")] double CostIndex);

    private record EsiIndustrySystemDto(
        [property: JsonPropertyName("solar_system_id")] int                          SolarSystemId,
        [property: JsonPropertyName("cost_indices")]    List<EsiIndustryCostIndexDto> CostIndices);
}
