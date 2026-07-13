using System.Text.Json;
using System.Text.Json.Serialization;
using EveCortex.Data;
using EveCortex.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveCortex.Services;

public class BuildCostService
{
    // Blueprint ME assumption for manufactured items.
    // Reactions use ME0 (no blueprint ME research applies to reactions).
    private const double MfgBlueprintMeFactor        = 0.90; // ME10 — T1 and most items
    private const double T2MfgBlueprintMeFactor       = 0.97; // ME3  — T2 items (invention cap)
    private const double FactionMfgBlueprintMeFactor  = 1.00; // ME0  — faction BPCs (not researchable)

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

    private static string RigCategoryFromName(string n)
    {
        if (n.Contains("Advanced Small Ship"))     return "adv_small_ships";
        if (n.Contains("Basic Small Ship"))        return "small_ships";
        if (n.Contains("Advanced Medium Ship"))    return "adv_medium_ships";
        if (n.Contains("Basic Medium Ship"))       return "medium_ships";
        if (n.Contains("Advanced Large Ship"))     return "adv_large_ships";
        if (n.Contains("Basic Large Ship"))        return "large_ships";
        if (n.Contains("Capital Ship"))            return "capital_ships";
        if (n.Contains("Drone and Fighter"))       return "drones_fighters";
        if (n.Contains("Equipment"))               return "modules_equipment";
        if (n.Contains("Ammunition"))              return "ammo_charges";
        if (n.Contains("Basic Capital Component")) return "capital_components";
        if (n.Contains("Advanced Component"))      return "adv_components";
        if (n.Contains("Structure"))               return "structure_ammo";
        // Tatara L-Set: one generic rig applies to ALL reaction types — use wildcard key.
        // Athanor M-Set: separate rigs per reaction subcategory — use specific keys.
        if (n.Contains("L-Set Reactor"))           return "biochemical_reactions";  // wildcard
        if (n.Contains("Biochemical Reactor"))     return "react_bio_gas";
        if (n.Contains("Composite Reactor"))       return "react_composite";
        if (n.Contains("Hybrid Reactor"))          return "react_composite";
        if (n.Contains("Reactor"))                 return "biochemical_reactions";  // fallback wildcard
        return "";
    }

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
            bool isReactionCat = itemCategoryKey.StartsWith("react_");
            return rigs.Where(r =>
                {
                    if (r.StructureId != s.Id || r.RigTypeId == 0) return false;
                    var rigCat = rigCategoryKeys.GetValueOrDefault(r.RigTypeId, "");
                    return rigCat == itemCategoryKey || (isReactionCat && rigCat == "biochemical_reactions");
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
        var factionTypeIds = metaGroupTypes.Where(t => t.MetaGroupId == 4).Select(t => t.TypeId).ToHashSet();

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
                    429 or 974 or 4096 => "react_composite",
                    _               => "",
                };
            }

            if (!groupCatMap.TryGetValue(tg.GroupId, out var gc)) return "";
            return (gc.CategoryId, gc.Name) switch
            {
                // ── Category 6: Ships ────────────────────────────────────────────────
                (6, "Frigate" or "Destroyer" or "Shuttle" or "Corvette" or "Rookie Ship"
                   or "Hauler" or "Mining Barge")                                           => "small_ships",
                (6, "Cruiser" or "Battlecruiser" or "Combat Battlecruiser"
                   or "Attack Battlecruiser")                                               => "medium_ships",
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
                // Structure Modules — service modules and all structure rigs — are built at
                // engineering complexes like equipment.
                (66, _)         => "modules_equipment",
                (8, _)          => "ammo_charges",
                (18, _) or (87, _)                                                          => "drones_fighters",
                _ when tg.GroupId == 1136                                  => "structure_ammo",   // Fuel Blocks
                _ when gc.Name.Contains("Capital") && gc.CategoryId == 4   => "capital_components",
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
            double bpMeFactor   = isReaction                    ? 1.0
                                : factionTypeIds.Contains(typeId) ? FactionMfgBlueprintMeFactor
                                : t2TypeIds.Contains(typeId)      ? T2MfgBlueprintMeFactor
                                : MfgBlueprintMeFactor;
            double rigMeBonus   = isReaction ? RigBonus(structure, catKey, rxnRigBonus)
                                             : RigBonus(structure, catKey, mfgRigBonus);
            double matRoleBonus = (!isReaction && IsUpwell(structure)) ? UpwellMaterialBonus : 0.0;
            double meFactor     = bpMeFactor * (1.0 - rigMeBonus) * (1.0 - matRoleBonus);

            decimal rawMatRun    = 0m; // market price of leaf inputs for this run
            decimal subJobRun    = 0m; // job fees of sub-components for this run
            double  eivRun       = 0.0;

            foreach (var mat in materials)
            {
                int effectiveQty = Math.Max(1, (int)Math.Ceiling(mat.Quantity * meFactor));

                // Use the sub-component's raw material cost (not its full unitCost) so that
                // its job fees are counted in subJobRun rather than inflating rawMatRun.
                decimal subRaw = rawMatCosts.TryGetValue(mat.MaterialTypeId, out var rm) ? rm : 0m;
                decimal subJob = totalJobCosts.TryGetValue(mat.MaterialTypeId, out var tj) ? tj : 0m;
                rawMatRun += effectiveQty * subRaw;
                subJobRun += effectiveQty * subJob;

                double adjPrice = adjustedPrices.TryGetValue(mat.MaterialTypeId, out var ap) ? ap : 0;
                eivRun          += mat.Quantity * adjPrice; // EIV always uses base (ME0) quantities
            }

            // Non-BPO items consume a blueprint copy bought from contracts. Treat the BPC as one
            // more purchased input per run, valued at its contract-derived market value (set on
            // blueprint types by MarketPricingService). EIV/job cost is unaffected — a BPC has no
            // adjusted price and does not enter the job-fee base.
            if (!isReaction && !BlueprintIsBpoSourced(bpTypeId)
                && marketPrices.TryGetValue(bpTypeId, out var bpcPrice) && bpcPrice > 0)
                rawMatRun += bpcPrice;

            // Job cost using the formula from the in-game breakdown.
            string activity   = isReaction ? "reaction" : "manufacturing";
            double costIndex  = GetCostIndex(structure, activity);
            double facTax     = structure is not null ? (double)structure.FacilityTax / 100.0 : 0.0;
            double roleBonus  = IsUpwell(structure) ? UpwellRoleBonus : 1.0;

            decimal jobGrossRun = Math.Round((decimal)(eivRun * costIndex * roleBonus), 0);
            decimal taxesRun    = Math.Round((decimal)(eivRun * (facTax + SccSurcharge)), 0);
            decimal thisJobRun  = jobGrossRun + taxesRun;

            decimal rawMatPerUnit   = rawMatRun                  / outputQty;
            decimal totalJobPerUnit = (subJobRun + thisJobRun)   / outputQty;

            unitCosts[typeId]     = rawMatPerUnit + totalJobPerUnit;
            rawMatCosts[typeId]   = rawMatPerUnit;
            totalJobCosts[typeId] = totalJobPerUnit;

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
