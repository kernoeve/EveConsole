using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

public class ProductionCalculatorService(IDbContextFactory<AppDbContext> dbFactory)
{
    private const string MfgActivity = "manufacturing";
    private const string RxnActivity = "reaction";
    private const double UpwellRoleBonus    = 0.97;
    private const double UpwellMatBonus     = 0.01;
    private const double SccSurcharge       = 0.04;
    private const int    AttrMfgME          = 2594;
    private const int    AttrRxnME          = 2714;
    private const int    AttrRigLowsecMult  = 2356;
    private const int    AttrRigNullsecMult = 2357;

    private static readonly HashSet<string> UpwellKeys    = ["raitaru","azbel","sotiyo","athanor","tatara","astrahus","fortizar","keepstar","draccous","horiuchi","moreau","prometheus","lancer"];
    private static readonly HashSet<string> EngComplexKeys = ["raitaru","azbel","sotiyo"];

    // EVE material consumption for a whole job: the per-run adjusted quantity (base × ME/rig/role
    // modifiers) is rounded to 2 dp, multiplied by the run count, and ceilinged ONCE — not rounded
    // per unit and then multiplied. Floors at one per run. Rounding per unit inflates batches (e.g.
    // a 4.5/run material over 2 runs is 9, not ceil(4.5)×2 = 10).
    private static int JobMaterialTotal(int baseQty, double factor, int runs)
    {
        double perRun = Math.Round(baseQty * factor, 2);
        double total  = Math.Round(perRun * runs, 4);   // guard floating-point before the ceiling
        return Math.Max(runs, (int)Math.Ceiling(total));
    }

    /// <summary>
    /// Loads a plan once and reuses it. Identical for every item and responsible for nearly all
    /// of a plan's cost, so a caller costing thousands of items should load it once and pass it
    /// to <see cref="Calculate"/> rather than going through <see cref="CalculateAsync"/> each time.
    /// </summary>
    public async Task<ProductionContext> LoadContextAsync(int parkId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // ── Load blueprint index ────────────────────────────────────────────
        // Published blueprints only — some products also have an unpublished "Test
        // Reaction Blueprint" with a tiny output quantity that would inflate materials.
        var bpProducts = await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => (p.Activity == MfgActivity || p.Activity == RxnActivity)
                     && db.SdeTypes.Any(t => t.TypeId == p.TypeId && t.Published))
            .ToListAsync(ct);

        var blueprintByProduct = bpProducts
            .GroupBy(p => p.ProductTypeId)
            .ToDictionary(g => g.Key, g => g.First());

        var bpTypeIds = bpProducts.Select(p => p.TypeId).Distinct().ToList();

        var bpMaterials = await db.SdeBlueprintMaterials.AsNoTracking()
            .Where(m => bpTypeIds.Contains(m.TypeId) &&
                        (m.Activity == MfgActivity || m.Activity == RxnActivity))
            .ToListAsync(ct);

        var materialsByBp = bpMaterials
            .GroupBy(m => m.TypeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // BPO-sourced blueprints: buyable on the market, or invented from a buyable source
        // blueprint. Anything else is a BPC bought from contracts and added as an input material.
        var marketBlueprints = (await db.SdeTypes.AsNoTracking()
            .Where(t => bpTypeIds.Contains(t.TypeId) && t.MarketGroupId != null)
            .Select(t => t.TypeId).ToListAsync(ct)).ToHashSet();
        // BPC-only loot tiers (Storyline 3, Faction 4, Officer 5, Deadspace 6) have no obtainable BPO
        // even when the blueprint carries a market group (e.g. Imperial Navy Bastion Module Blueprint)
        // — their build consumes a purchased BPC, so drop them from the BPO set.
        var mfgProductIds = bpProducts.Where(p => p.Activity == MfgActivity)
            .Select(p => p.ProductTypeId).Distinct().ToList();
        var bpcOnlyProductIds = (await db.SdeTypes.AsNoTracking()
            .Where(t => mfgProductIds.Contains(t.TypeId) && t.MetaGroupId >= 3 && t.MetaGroupId <= 6)
            .Select(t => t.TypeId).ToListAsync(ct)).ToHashSet();
        marketBlueprints.ExceptWith(bpProducts
            .Where(p => p.Activity == MfgActivity && bpcOnlyProductIds.Contains(p.ProductTypeId))
            .Select(p => p.TypeId));
        var inventedFromMarket = (await db.SdeBlueprintProducts.AsNoTracking()
                .Where(p => p.Activity == "invention")
                .Select(p => new { p.TypeId, p.ProductTypeId }).ToListAsync(ct))
            .Where(r => marketBlueprints.Contains(r.TypeId))
            .Select(r => r.ProductTypeId).ToHashSet();
        // ── Type names and group/category info ─────────────────────────────
        var typeNames = await db.SdeTypes.AsNoTracking()
            .Select(t => new { t.TypeId, t.Name })
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        // Named record structs rather than anonymous types: these have to survive being returned
        // from this method, which an anonymous type cannot do.
        var typeGroupMap = await db.SdeTypes.AsNoTracking()
            .Select(t => new ProductionContext.TypeGroup(t.TypeId, t.GroupId))
            .ToDictionaryAsync(t => t.TypeId, ct);

        var groupCatMap = await db.SdeGroups.AsNoTracking()
            .Select(g => new ProductionContext.GroupCat(g.GroupId, g.CategoryId, g.Name))
            .ToDictionaryAsync(g => g.GroupId, ct);

        // Classification for the default-ME rule (shared with BuildCostService via IndustryMe).
        var t2TypeIds = (await db.SdeTypes.AsNoTracking()
            .Where(t => t.MetaGroupId == 2).Select(t => t.TypeId).ToListAsync(ct)).ToHashSet();
        var titanKeepstarIds = (await db.SdeTypes.AsNoTracking()
            .Where(t => t.GroupId == IndustryMe.TitanGroupId || t.TypeId == IndustryMe.KeepstarTypeId)
            .Select(t => t.TypeId).ToListAsync(ct)).ToHashSet();

        // ── Park / structure data ──────────────────────────────────────────
        var structures  = await db.IndyStructures.AsNoTracking().Where(s => s.ParkId == parkId).ToListAsync(ct);
        var rigs        = await db.IndyStructureRigs.AsNoTracking()
            .Where(r => structures.Select(s => s.Id).Contains(r.StructureId))
            .ToListAsync(ct);
        var park          = await db.IndyParks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parkId, ct);
        var defaultStruct = park?.DefaultStructureId is { } dsId
            ? structures.FirstOrDefault(s => s.Id == dsId)
            : null;
        var assignments   = await db.IndyCategoryAssignments.AsNoTracking().Where(a => a.ParkId == parkId).ToListAsync(ct);
        var itemExceptions = await db.IndyItemExceptions.AsNoTracking().Where(e => e.ParkId == parkId).ToListAsync(ct);
        var itemOverrides  = itemExceptions
            .Where(e => e.StructureId.HasValue)
            .ToDictionary(e => e.TypeId, e => structures.FirstOrDefault(s => s.Id == e.StructureId!.Value));

        // ── Rig dogma attributes ───────────────────────────────────────────
        var rigTypeIds = rigs.Select(r => r.RigTypeId).Distinct().ToList();
        var rigAttrs   = rigTypeIds.Count > 0
            ? await db.SdeTypeDogmaAttributes.AsNoTracking()
                .Where(a => rigTypeIds.Contains(a.TypeId) &&
                            (a.AttributeId == AttrMfgME || a.AttributeId == AttrRxnME ||
                             a.AttributeId == AttrRigLowsecMult || a.AttributeId == AttrRigNullsecMult))
                .ToListAsync(ct)
            : [];

        var mfgRigBonusAttr    = new Dictionary<int, double>();
        var rxnRigBonusAttr    = new Dictionary<int, double>();
        var rigLowsecMultAttr  = new Dictionary<int, double>();
        var rigNullsecMultAttr = new Dictionary<int, double>();
        foreach (var a in rigAttrs)
        {
            if (a.AttributeId == AttrMfgME)          mfgRigBonusAttr[a.TypeId]    = Math.Abs(a.Value) / 100.0;
            if (a.AttributeId == AttrRxnME)          rxnRigBonusAttr[a.TypeId]    = Math.Abs(a.Value) / 100.0;
            if (a.AttributeId == AttrRigLowsecMult)  rigLowsecMultAttr[a.TypeId]  = a.Value;
            if (a.AttributeId == AttrRigNullsecMult) rigNullsecMultAttr[a.TypeId] = a.Value;
        }

        // Load rig names to determine which production category each rig applies to.
        // Standup rigs follow a strict "Basic/Advanced [Category]" naming convention.
        var rigTypeNames = rigTypeIds.Count > 0
            ? await db.SdeTypes.AsNoTracking()
                .Where(t => rigTypeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct)
            : new Dictionary<int, string>();

        static string RigCategoryFromName(string n)
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
            // Tatara L-Set: one generic rig covers ALL reaction types — use wildcard key.
            // Athanor M-Set: separate rigs per reaction subcategory — use specific keys.
            if (n.Contains("L-Set Reactor"))           return "biochemical_reactions";  // wildcard
            if (n.Contains("Biochemical Reactor"))     return "react_bio_gas";
            if (n.Contains("Composite Reactor"))       return "react_composite";
            if (n.Contains("Hybrid Reactor"))          return "react_composite";
            if (n.Contains("Reactor"))                 return "biochemical_reactions";  // fallback wildcard
            return "";
        }

        var rigCategoryKeys = rigTypeIds.ToDictionary(
            id => id,
            id => rigTypeNames.TryGetValue(id, out var n) ? RigCategoryFromName(n) : "");

        // ── Per-structure cost indices ─────────────────────────────────────
        var systemNames = structures.Select(s => s.SystemName).Distinct().ToList();
        var systemIds   = await db.SdeSolarSystems.AsNoTracking()
            .Where(ss => systemNames.Contains(ss.Name))
            .ToDictionaryAsync(ss => ss.Name, ss => ss.SolarSystemId, ct);

        var costIndices = await db.IndustryCostIndices.AsNoTracking().ToListAsync(ct);
        var ciLookup    = costIndices.GroupBy(c => c.SolarSystemId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(c => c.Activity, c => c.CostIndex));

        // ── Category → structure mapping ────────────────────────────────────
        var structByCategory = assignments
            .GroupBy(a => a.CategoryKey)
            .ToDictionary(g => g.Key,
                g => structures.FirstOrDefault(s => s.Id == g.First().StructureId!.Value));

        // ── Market prices ──────────────────────────────────────────────────
        var defaults       = await db.MarketDefaultSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var mktConfigId    = defaults?.ManufacturingConfigId;
        var mktType        = defaults?.ManufacturingPriceType ?? "Sell";
        var markupFactor   = 1.0 + (double)(defaults?.MissingPriceMarkupPct ?? 15m) / 100.0;
        var unitCosts      = new Dictionary<int, decimal>();
        if (mktConfigId.HasValue)
        {
            var prices = await db.MarketItemPrices.AsNoTracking()
                .Where(p => p.ConfigId == mktConfigId.Value).ToListAsync(ct);
            foreach (var p in prices)
                unitCosts[p.TypeId] = (decimal)(mktType switch { "Buy" => p.BuyPrice, "Sell" => p.SellPrice, _ => p.Midpoint });
        }

        // Per-run BPC contract prices grouped by blueprint type → [(ME, per-run price)]. A BPC-only
        // item consumes one run of a purchased BPC; value it per run at the item's ME.
        var bpcPerRun = (await db.ContractBpcPrices.AsNoTracking().ToListAsync(ct))
            .Select(c => new { c.TypeId, c.Me, Price = ContractPricing.EffectivePerRun(c) })
            .Where(x => x.Price is > 0m)
            .GroupBy(x => x.TypeId)
            .ToDictionary(g => g.Key, g => g.Select(x => (Me: x.Me, PerRun: x.Price!.Value)).ToList());

        // Items the build-cost calc found cheaper to BUY than build — buy them here too (raw material,
        // no job) so the two calcs agree.
        var boughtSet = (await db.BuildCosts.AsNoTracking().Where(b => b.Bought)
            .Select(b => b.TypeId).ToListAsync(ct)).ToHashSet();

        // User price overrides. Market → the item's price (PriceOf); contract → the per-run BPC
        // price; build → pin the item as a fixed-value leaf (not expanded) at the given cost, unless
        // it was cheaper to buy (already in boughtSet, priced at market). These mirror the overlays
        // BuildCostService applies so both calculators agree.
        // Applied per queue rather than here: which overrides bite depends on what was requested
        // (a requested final product's build-cost pin is skipped), so this is done in Calculate.
        var overrides   = await db.PriceOverrides.AsNoTracking().ToDictionaryAsync(o => o.TypeId, ct);

        // ── Adjusted prices (for EIV / job cost) ──────────────────────────
        var adjPrices = await db.EsiAdjustedPrices.AsNoTracking()
            .ToDictionaryAsync(p => p.TypeId, p => p.AdjustedPrice, ct);

        // ── Pre-computed build costs (used for leftover valuation and missing-price fallback) ─
        var buildCostLookup = await db.BuildCosts.AsNoTracking()
            .ToDictionaryAsync(b => b.TypeId, b => b.TotalCost, ct);

        return new ProductionContext
        {
            ParkId             = parkId,
            BpProducts         = bpProducts,
            BpTypeIds          = bpTypeIds,
            BlueprintByProduct = blueprintByProduct,
            MaterialsByBp      = materialsByBp,
            MarketBlueprints   = marketBlueprints,
            InventedFromMarket = inventedFromMarket,
            TypeNames          = typeNames,
            TypeGroupMap       = typeGroupMap,
            GroupCatMap        = groupCatMap,
            T2TypeIds          = t2TypeIds,
            TitanKeepstarIds   = titanKeepstarIds,
            Structures         = structures,
            Rigs               = rigs,
            Assignments        = assignments,
            ItemOverrides      = itemOverrides,
            StructByCategory   = structByCategory,
            DefaultStructure   = defaultStruct,
            MfgRigBonusAttr    = mfgRigBonusAttr,
            RxnRigBonusAttr    = rxnRigBonusAttr,
            RigLowsecMultAttr  = rigLowsecMultAttr,
            RigNullsecMultAttr = rigNullsecMultAttr,
            RigCategoryKeys    = rigCategoryKeys,
            SystemIds          = systemIds,
            CiLookup           = ciLookup,
            MarkupFactor       = markupFactor,
            UnitCosts          = unitCosts,
            BpcPerRun          = bpcPerRun,
            BoughtSet          = boughtSet,
            Overrides          = overrides,
            AdjPrices          = adjPrices,
            BuildCostLookup    = buildCostLookup,
        };
    }

    /// <summary>Loads a context and calculates in one go — the everyday entry point.</summary>
    public async Task<ProductionPlan> CalculateAsync(
        List<ProductionQueueEntry> requests,
        int parkId,
        bool includeBpcCost = false,
        CancellationToken ct = default)
        => Calculate(requests, await LoadContextAsync(parkId, ct), includeBpcCost);

    /// <summary>
    /// Plans a queue against an already-loaded context. Pure computation — no database access —
    /// so a caller with many items to cost pays the loading cost once.
    /// </summary>
    public ProductionPlan Calculate(
        List<ProductionQueueEntry> requests,
        ProductionContext ctx,
        bool includeBpcCost = false)
    {
        // Bound back to the names the body below already uses, so the planning logic is the same
        // code it was when it and the loading lived in one method.
        var bpProducts         = ctx.BpProducts;
        var bpTypeIds          = ctx.BpTypeIds;
        var blueprintByProduct = ctx.BlueprintByProduct;
        var materialsByBp      = ctx.MaterialsByBp;
        var marketBlueprints   = ctx.MarketBlueprints;
        var inventedFromMarket = ctx.InventedFromMarket;
        var typeNames          = ctx.TypeNames;
        var typeGroupMap       = ctx.TypeGroupMap;
        var groupCatMap        = ctx.GroupCatMap;
        var t2TypeIds          = ctx.T2TypeIds;
        var titanKeepstarIds   = ctx.TitanKeepstarIds;
        var structures         = ctx.Structures;
        var rigs               = ctx.Rigs;
        var assignments        = ctx.Assignments;
        var itemOverrides      = ctx.ItemOverrides;
        var structByCategory   = ctx.StructByCategory;
        var mfgRigBonusAttr    = ctx.MfgRigBonusAttr;
        var rxnRigBonusAttr    = ctx.RxnRigBonusAttr;
        var rigLowsecMultAttr  = ctx.RigLowsecMultAttr;
        var rigNullsecMultAttr = ctx.RigNullsecMultAttr;
        var rigCategoryKeys    = ctx.RigCategoryKeys;
        var systemIds          = ctx.SystemIds;
        var ciLookup           = ctx.CiLookup;
        var markupFactor       = ctx.MarkupFactor;
        var boughtSet          = ctx.BoughtSet;
        var overrides          = ctx.Overrides;
        var adjPrices          = ctx.AdjPrices;
        var buildCostLookup    = ctx.BuildCostLookup;

        bool BlueprintIsBpoSourced(int bpTypeId) =>
            marketBlueprints.Contains(bpTypeId) || inventedFromMarket.Contains(bpTypeId);

        double SecMult(IndyStructure s, int rigTypeId) => s.SecurityClass switch
        {
            "lowsec"   => rigLowsecMultAttr.TryGetValue(rigTypeId, out var lm) ? lm : 1.9,
            "nullsec"  => rigNullsecMultAttr.TryGetValue(rigTypeId, out var nm) ? nm : 2.1,
            "wormhole" => rigNullsecMultAttr.TryGetValue(rigTypeId, out var wm) ? wm : 2.1,
            _          => 1.0,
        };

        double RigBonus(IndyStructure? s, string itemCategoryKey, Dictionary<int, double> bonusAttr)
        {
            if (s is null) return 0;
            bool isReactionCat = itemCategoryKey.StartsWith("react_");
            return rigs.Where(r =>
                {
                    if (r.StructureId != s.Id || r.RigTypeId == 0) return false;
                    var rigCat = rigCategoryKeys.GetValueOrDefault(r.RigTypeId);
                    // "biochemical_reactions" is the generic reactor rig key — it matches all react_* items.
                    return rigCat == itemCategoryKey || (isReactionCat && rigCat == "biochemical_reactions");
                })
                .Sum(r => bonusAttr.TryGetValue(r.RigTypeId, out var b) ? b * SecMult(s, r.RigTypeId) : 0.0);
        }

        double GetCostIndex(IndyStructure? s, string activity)
        {
            if (s is null) return 0;
            if (!systemIds.TryGetValue(s.SystemName, out var sysId)) return 0;
            return ciLookup.TryGetValue(sysId, out var ci) && ci.TryGetValue(activity, out var idx) ? idx : 0;
        }

        // Copied, because the override pass below rewrites them and the context is shared with
        // every other plan calculated from it.
        var unitCosts = new Dictionary<int, decimal>(ctx.UnitCosts);
        var bpcPerRun = ctx.BpcPerRun.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());

        decimal BpcPerRunAt(int bpTypeId, int me)
        {
            if (!bpcPerRun.TryGetValue(bpTypeId, out var opts) || opts.Count == 0) return 0m;
            foreach (var (m, p) in opts) if (m == me) return p;   // exact ME, else the cheapest available
            return opts.Min(o => o.PerRun);
        }

        // User price overrides. Market → the item's price (PriceOf); contract → the per-run BPC
        // price; build → pin the item as a fixed-value leaf (not expanded) at the given cost, unless
        // it was cheaper to buy (already in boughtSet, priced at market). These mirror the overlays
        // BuildCostService applies so both calculators agree.
        var requestedIds = requests.Select(r => r.TypeId).ToHashSet();
        var pinnedBuild  = new HashSet<int>();
        foreach (var o in overrides.Values)
        {
            if (o.MarketValue.HasValue)   unitCosts[o.TypeId] = o.MarketValue.Value;
            if (o.ContractValue.HasValue) bpcPerRun[o.TypeId] = [(0, o.ContractValue.Value)];
            // Pin a sub-component's build cost as a fixed-value leaf. Skip requested final products —
            // their build cost is computed from the tree, and unitCosts drives their market value read.
            if (o.BuildCost.HasValue && !boughtSet.Contains(o.TypeId) && !requestedIds.Contains(o.TypeId))
            {
                unitCosts[o.TypeId] = o.BuildCost.Value;   // PriceOf → pinned build cost (a consumed leaf)
                pinnedBuild.Add(o.TypeId);
            }
        }

        // Returns the market price for a type, falling back to build cost × markup when no
        // market order exists for it. Returns 0 only when both market and build cost are absent.
        decimal PriceOf(int typeId)
        {
            if (unitCosts.TryGetValue(typeId, out var p) && p > 0) return p;
            if (buildCostLookup.TryGetValue(typeId, out var bc) && bc > 0)
                return bc * (decimal)markupFactor;
            return 0m;
        }

        // ── Helper: ItemCategoryKey ─────────────────────────────────────────
        string ItemCategoryKey(int typeId, bool isReaction)
        {
            if (!typeGroupMap.TryGetValue(typeId, out var tg)) return "";

            if (isReaction)
            {
                // Reaction category is determined by the product's SDE group.
                // "biochemical_reactions" is reserved as the rig wildcard key — not used here.
                return tg.GroupId switch
                {
                    712             => "react_bio_gas",         // Biochemical Material (gas reactions)
                    428             => "react_biochemical",     // Intermediate Materials (moon processing)
                    // 4932 Unrefined Mineral — the eight Unrefined Tritanium/Pyerite/… products,
                    // reaction-produced and bonused by the composite rig. See IndyRigMatching.
                    429 or 974 or 4096 or 4932 => "react_composite",   // Composite / Hybrid Polymers / Molecular-Forged
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
                // Structure Modules — service modules and all structure rigs — are built at
                // engineering complexes like equipment.
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
                (18 or 87, _)   => "drones_fighters",
                _ when tg.GroupId == 1136                                 => "structure_ammo",   // Fuel Blocks
                // Capital Construction Components (group 873) are category 17, not 4 —
                // the old guard matched nothing, so capital parts were assigned to the
                // advanced-component structure. "Advanced" is excluded because group 913
                // is genuinely bonused by the advanced rig. See IndyRigMatching.
                _ when gc.Name.Contains("Capital") && gc.Name.Contains("Component")
                                                   && !gc.Name.Contains("Advanced")
                                                                          => "capital_components",
                _ when gc.Name.Contains("Component")                       => "adv_components",
                _ when gc.CategoryId is 22 or 65                          => "structure_ammo",
                // R.A.M. items and Data Interfaces are manufactured at standard facilities
                _ when gc.CategoryId == 17 && gc.Name is "Tool" or "Data Interfaces" => "modules_equipment",
                _ => ""
            };
        }

        // Item-level overrides take precedence over category assignments; anything neither
        // covers falls through to the park's catch-all facility.
        //
        // The fallback is what keeps the calculation alive. A job here gets the structure's
        // role bonus, tax and system cost index but no rig bonus — RigBonus resolves the
        // item's own category against the rigs actually fitted, so an unclassified item
        // (empty key) matches nothing and scores zero. That is the intended reading of
        // "a facility not rigged for it", not a special case.
        //
        // Still null when the park nominates no catch-all, which plans with no structure
        // and no bonuses. Either way the caller records a warning rather than aborting.
        IndyStructure? StructureFor(string catKey, int typeId)
        {
            if (itemOverrides.TryGetValue(typeId, out var overrideStruct))
                return overrideStruct;
            if (!string.IsNullOrEmpty(catKey) && structByCategory.TryGetValue(catKey, out var s))
                return s;
            return ctx.DefaultStructure;
        }

        // ── Expansion state ────────────────────────────────────────────────
        var jobPool       = new Dictionary<int, PlanJob>();
        var rawPool       = new Dictionary<int, int>();
        var finalMeLevels = requests.ToDictionary(r => r.TypeId, r => r.MeLevel);

        // Tracks items whose category could not be determined or is not assigned in this park.
        var unmappedItems = new SortedSet<string>();

        void ExpandItem(int typeId, int qty, bool isFinal)
        {
            // Cheaper to buy than build (per the build-cost calc), or pinned to a fixed build cost by
            // a price override → treat as a raw material with no job. The final product is always
            // built. Keeps this calc consistent with build costs.
            if (!isFinal && (boughtSet.Contains(typeId) || pinnedBuild.Contains(typeId)))
            {
                rawPool[typeId] = rawPool.GetValueOrDefault(typeId, 0) + qty;
                return;
            }

            if (!blueprintByProduct.TryGetValue(typeId, out var bpProd))
            {
                rawPool[typeId] = rawPool.GetValueOrDefault(typeId, 0) + qty;
                return;
            }

            var    activity   = bpProd.Activity;
            bool   isReaction = activity == RxnActivity;
            string catKey     = ItemCategoryKey(typeId, isReaction);

            // Report items no assignment covers. They are still planned — against the park's
            // catch-all facility, with no rig bonus — so this surfaces a gap in the rules or
            // the park setup without costing the user the rest of the plan.
            // Item-level overrides satisfy the requirement regardless of category status.
            if (!itemOverrides.ContainsKey(typeId))
            {
                var whereItWent = ctx.DefaultStructure is { } fb
                    ? $"planned in {fb.DisplayName} with no rig bonus"
                    : "planned with no structure and no bonuses — set a catch-all facility on this park";

                if (string.IsNullOrEmpty(catKey))
                {
                    var name = typeNames.GetValueOrDefault(typeId, $"TypeId {typeId}");
                    unmappedItems.Add($"{name} (unrecognized type — update ItemCategoryKey; {whereItWent})");
                }
                else if (!structByCategory.ContainsKey(catKey))
                {
                    var name = typeNames.GetValueOrDefault(typeId, $"TypeId {typeId}");
                    unmappedItems.Add($"{name} (category '{catKey}' not assigned in this park; {whereItWent})");
                }
            }

            // Final products use the user-chosen ME (defaulted per the same rule when added to the
            // queue). Sub-components follow the shared default-ME rule (ME10 / T2 ME3 / BPC-only ME0 /
            // titan & Keepstar ME9 / reactions ME0) so this matches the stored build cost.
            int    meLevel    = (isFinal && !isReaction && finalMeLevels.TryGetValue(typeId, out var ml))
                                ? ml
                                : IndustryMe.DefaultMe(isReaction, !isReaction && !BlueprintIsBpoSourced(bpProd.TypeId),
                                      t2TypeIds.Contains(typeId), titanKeepstarIds.Contains(typeId));
            var    structure  = StructureFor(catKey, typeId);
            bool   isEngCx    = structure is not null && EngComplexKeys.Contains(structure.StructureTypeKey);
            double bpMeFactor = (100.0 - meLevel) / 100.0;
            double rigBonus   = isReaction ? RigBonus(structure, catKey, rxnRigBonusAttr)
                                           : RigBonus(structure, catKey, mfgRigBonusAttr);
            double matRoleBonus = (!isReaction && isEngCx) ? UpwellMatBonus : 0.0;
            double meFactor   = bpMeFactor * (1.0 - rigBonus) * (1.0 - matRoleBonus);

            var bpMats = materialsByBp.TryGetValue(bpProd.TypeId, out var m) ? m : [];

            if (jobPool.TryGetValue(typeId, out var existing))
            {
                int oldRuns   = existing.Runs;
                existing.QuantityNeeded += qty;
                int newRuns   = (int)Math.Ceiling((double)existing.QuantityNeeded / bpProd.Quantity);
                int extraRuns = newRuns - oldRuns;
                existing.Runs = newRuns;
                if (extraRuns > 0)
                {
                    foreach (var mat in bpMats)
                    {
                        // Recompute the whole-job total at the new run count and expand only the
                        // delta — rounding at the job level, not per run (see JobMaterialTotal).
                        int newTotal    = JobMaterialTotal(mat.Quantity, meFactor, newRuns);
                        var existingMat = existing.Materials.FirstOrDefault(m => m.MaterialTypeId == mat.MaterialTypeId);
                        int oldTotal    = existingMat?.TotalQty ?? JobMaterialTotal(mat.Quantity, meFactor, oldRuns);
                        int delta       = newTotal - oldTotal;
                        if (existingMat is not null)
                        {
                            existingMat.TotalQty = newTotal;
                            existingMat.FormulaDisplay =
                                $"ceil({mat.Quantity:N0} × {meFactor:F4} × {newRuns:N0} runs) → {newTotal:N0}";
                        }
                        if (delta > 0) ExpandItem(mat.MaterialTypeId, delta, false);
                    }
                    // If this job included its blueprint copy, keep the BPC quantity in sync as it
                    // gains runs (and add the extra copies to the raw pool).
                    var bpcMat = existing.Materials.FirstOrDefault(m => m.MaterialTypeId == bpProd.TypeId);
                    if (bpcMat is not null)
                    {
                        bpcMat.TotalQty += extraRuns;
                        ExpandItem(bpProd.TypeId, extraRuns, false);
                    }
                }
            }
            else
            {
                int runs = (int)Math.Ceiling((double)qty / bpProd.Quantity);
                var job  = new PlanJob
                {
                    OutputTypeId   = typeId,
                    OutputTypeName = typeNames.GetValueOrDefault(typeId, $"Type {typeId}"),
                    IsReaction     = isReaction,
                    MeLevel        = meLevel,
                    QuantityNeeded = qty,
                    QuantityPerRun = bpProd.Quantity,
                    Runs           = runs,
                    IsFinalProduct = isFinal,
                    StructureName  = structure?.DisplayName ?? "",
                    SystemName     = structure?.SystemName  ?? "",
                    // Only set when the park structure has been linked to a real facility.
                    // Left null otherwise, which the availability pass reports as unknown.
                    StationId      = structure?.RealStructureId,
                    StationName    = structure?.RealStructureName ?? "",
                    MeReductionPct = meLevel,
                    RigBonusPct    = rigBonus * 100.0,
                    RoleBonusPct   = matRoleBonus * 100.0,
                    CombinedFactor = meFactor,
                };
                foreach (var mat in bpMats)
                {
                    int    basePerRun = mat.Quantity;
                    double perRunAdj  = Math.Round(basePerRun * meFactor, 2);
                    int    totalQty   = JobMaterialTotal(basePerRun, meFactor, runs);
                    job.Materials.Add(new PlanJobMaterial
                    {
                        MaterialTypeId = mat.MaterialTypeId,
                        TypeName       = typeNames.GetValueOrDefault(mat.MaterialTypeId, $"Type {mat.MaterialTypeId}"),
                        BaseQtyPerRun  = basePerRun,
                        EffQtyPerRun   = (int)Math.Ceiling(perRunAdj),
                        TotalQty       = totalQty,
                        IsBought       = !blueprintByProduct.ContainsKey(mat.MaterialTypeId)
                                          || boughtSet.Contains(mat.MaterialTypeId)
                                          || pinnedBuild.Contains(mat.MaterialTypeId),
                        FormulaDisplay = $"ceil({basePerRun:N0} × {meFactor:F4} × {runs:N0} runs) = ceil({perRunAdj:N2} × {runs:N0}) → {totalQty:N0}",
                    });
                    ExpandItem(mat.MaterialTypeId, totalQty, false);
                }
                // A BPC-only item (no obtainable BPO) always consumes one purchased BPC per run; a
                // BPO item only does so when the user opts in (includeBpcCost). Valued PER RUN at the
                // item's ME. It's a priced job material (not expanded into the raw pool) so its cost
                // is counted once and matches the build-cost calc.
                bool bpcOnly = !isReaction && !BlueprintIsBpoSourced(bpProd.TypeId);
                if (bpcOnly || (!isReaction && isFinal && includeBpcCost))
                {
                    // Overlay the BPC's PER-RUN price (at this item's ME) into the price table so both
                    // the raw-material total and the job-material line value it identically.
                    unitCosts[bpProd.TypeId] = BpcPerRunAt(bpProd.TypeId, meLevel);
                    job.Materials.Add(new PlanJobMaterial
                    {
                        MaterialTypeId = bpProd.TypeId,
                        TypeName       = typeNames.GetValueOrDefault(bpProd.TypeId, $"Type {bpProd.TypeId}") + " (BPC)",
                        BaseQtyPerRun  = 1,
                        EffQtyPerRun   = 1,
                        TotalQty       = runs,
                        IsBought       = true,
                        FormulaDisplay = $"1 BPC per run @ ME{meLevel} contract price",
                    });
                    ExpandItem(bpProd.TypeId, runs, false);   // also a raw-material line (per-run priced)
                }
                jobPool[typeId] = job;
            }
        }

        foreach (var req in requests)
            ExpandItem(req.TypeId, req.Quantity, true);

        // Reported, not thrown. An item the rig rules don't cover is a gap in the rules,
        // not a reason to refuse the other several hundred jobs in the plan — and throwing
        // is what left BuildCostService falling back to stale estimates for 446 types.
        // These jobs were planned against the park's catch-all facility with no rig bonus.
        var planWarnings = unmappedItems.ToList();

        // ── Wire parent/child relationships ────────────────────────────────
        foreach (var job in jobPool.Values)
        {
            foreach (var mat in job.Materials.Where(mat2 => !mat2.IsBought))
            {
                if (jobPool.TryGetValue(mat.MaterialTypeId, out var childJob))
                {
                    if (!job.ChildTypeIds.Contains(mat.MaterialTypeId))
                        job.ChildTypeIds.Add(mat.MaterialTypeId);
                    if (!childJob.ParentTypeIds.Contains(job.OutputTypeId))
                        childJob.ParentTypeIds.Add(job.OutputTypeId);
                }
            }
        }

        // ── Calculate costs per job ────────────────────────────────────────
        foreach (var job in jobPool.Values)
        {
            string jobCatKey = ItemCategoryKey(job.OutputTypeId, job.IsReaction);
            var    structure  = StructureFor(jobCatKey, job.OutputTypeId);
            bool   isUpwell  = structure is not null && UpwellKeys.Contains(structure.StructureTypeKey);
            string activity  = job.IsReaction ? "reaction" : "manufacturing";
            double costIndex = GetCostIndex(structure, activity);
            double facTax    = structure is not null ? (double)structure.FacilityTax / 100.0 : 0;
            double roleBonus = isUpwell ? UpwellRoleBonus : 1.0;

            decimal matCost = 0;
            double  eiv     = 0;
            foreach (var mat in job.Materials)
            {
                mat.UnitPrice = PriceOf(mat.MaterialTypeId);   // BPCs read the per-run overlay above
                // Only count purchased inputs; built intermediates have their own job cost
                if (mat.IsBought) matCost += mat.TotalQty * mat.UnitPrice;
                double ap = adjPrices.GetValueOrDefault(mat.MaterialTypeId, 0.0);
                eiv += mat.BaseQtyPerRun * job.Runs * ap;
            }

            decimal jobGross = Math.Round((decimal)(eiv * costIndex * roleBonus), 0);
            decimal jobTaxes = Math.Round((decimal)(eiv * (facTax + SccSurcharge)), 0);

            job.MaterialCost = matCost;
            job.JobCost      = jobGross + jobTaxes;
        }

        // ── Build raw materials list ────────────────────────────────────────
        var rawMaterials = rawPool
            .Select(kvp => new PlanRawMaterial
            {
                TypeId    = kvp.Key,
                TypeName  = typeNames.GetValueOrDefault(kvp.Key, $"Type {kvp.Key}"),
                Quantity  = kvp.Value,
                UnitPrice = PriceOf(kvp.Key),
                TotalCost = kvp.Value * PriceOf(kvp.Key),
            })
            .OrderByDescending(r => r.TotalCost)
            .ToList();

        // ── Build intermediates list ────────────────────────────────────────
        // MarketUnitPrice here is set to build cost (more accurate for leftover valuation).
        var intermediates = jobPool.Values
            .Where(j => !j.IsFinalProduct)
            .Select(j =>
            {
                decimal buildVal = buildCostLookup.TryGetValue(j.OutputTypeId, out var bc) ? bc
                                   : PriceOf(j.OutputTypeId);
                return new PlanIntermediate
                {
                    TypeId           = j.OutputTypeId,
                    TypeName         = j.OutputTypeName,
                    QuantityNeeded   = j.QuantityNeeded,
                    QuantityProduced = j.QuantityProduced,
                    Leftover         = j.Leftover,
                    MarketUnitPrice  = buildVal,
                    LeftoverValue    = j.Leftover * buildVal,
                };
            })
            .OrderBy(i => i.TypeName)
            .ToList();

        // ── Build final products summary ────────────────────────────────────
        // Walk the subtree for each final product, summing only raw-material costs
        // (IsBought=true) and all job costs. Summing job.MaterialCost directly would
        // double-count intermediates that have market prices but are also produced.
        var leftoverValueByType = intermediates
            .Where(i => i.Leftover > 0)
            .ToDictionary(i => i.TypeId, i => i.LeftoverValue);

        var finalProducts = requests.Select(req =>
        {
            var rootJob = jobPool.GetValueOrDefault(req.TypeId);
            var seen    = new HashSet<int>();
            decimal subtreeRawMat   = 0;
            decimal subtreeJobCost  = 0;
            decimal subtreeLeftover = 0;

            void WalkSubtree(int tid)
            {
                if (!seen.Add(tid) || !jobPool.TryGetValue(tid, out var j)) return;
                subtreeJobCost += j.JobCost;
                foreach (var mat in j.Materials)
                    if (mat.IsBought) subtreeRawMat += mat.TotalQty * mat.UnitPrice;

                // Over-production of a sub-component is stock, not cost. Excludes the product
                // itself, whose surplus is counted as produced units rather than as leftovers.
                if (tid != req.TypeId && leftoverValueByType.TryGetValue(tid, out var lv))
                    subtreeLeftover += lv;

                foreach (var childId in j.ChildTypeIds)
                    WalkSubtree(childId);
            }
            if (rootJob is not null) WalkSubtree(req.TypeId);

            // Net of leftovers, matching the Cost Summary's Net Cost. Charging the gross would
            // bill this run for components it did not consume and still has on the shelf.
            decimal totalCost = subtreeRawMat + subtreeJobCost - subtreeLeftover;
            int     produced  = rootJob?.QuantityProduced ?? req.Quantity;
            return new PlanFinalProduct
            {
                TypeId            = req.TypeId,
                TypeName          = typeNames.GetValueOrDefault(req.TypeId, $"Type {req.TypeId}"),
                QuantityRequested = req.Quantity,
                QuantityProduced  = produced,
                MeLevel           = req.MeLevel,
                TotalMaterialCost = subtreeRawMat,
                TotalJobCost      = subtreeJobCost,
                TotalCost         = totalCost,
                UnitCost          = produced > 0 ? totalCost / produced : 0,
                MarketUnitPrice   = PriceOf(req.TypeId),
                MarketTotalValue  = PriceOf(req.TypeId) * produced,
            };
        }).ToList();

        // ── Build leftovers list ────────────────────────────────────────────
        // Use build cost for valuation — market prices for produced items can be unreliable.
        var leftovers = new List<PlanLeftoverItem>();
        foreach (var interm in intermediates.Where(i => i.Leftover > 0))
            leftovers.Add(new PlanLeftoverItem
            {
                TypeId     = interm.TypeId,
                TypeName   = interm.TypeName,
                Quantity   = interm.Leftover,
                UnitPrice  = interm.MarketUnitPrice, // already set to build cost above
                TotalValue = interm.LeftoverValue,
                Source     = "Intermediate",
            });
        foreach (var fp in finalProducts.Where(f => f.QuantityProduced > f.QuantityRequested))
        {
            int  overrun  = fp.QuantityProduced - fp.QuantityRequested;
            decimal uCost = fp.QuantityProduced > 0 ? fp.TotalCost / fp.QuantityProduced : 0m;
            leftovers.Add(new PlanLeftoverItem
            {
                TypeId     = fp.TypeId,
                TypeName   = fp.TypeName,
                Quantity   = overrun,
                UnitPrice  = uCost,
                TotalValue = uCost * overrun,
                Source     = "Final Product",
            });
        }
        leftovers = [.. leftovers.OrderByDescending(l => l.TotalValue)];

        // ── Totals ─────────────────────────────────────────────────────────
        decimal totalRawMat   = rawMaterials.Sum(r => r.TotalCost);
        decimal totalJobCost  = jobPool.Values.Sum(j => j.JobCost);
        decimal totalLeftover = leftovers.Sum(l => l.TotalValue);

        return new ProductionPlan
        {
            AllJobs              = jobPool.Values.OrderByDescending(j => j.IsFinalProduct).ThenBy(j => j.OutputTypeName).ToList(),
            RootTypeIds          = requests.Where(r => jobPool.ContainsKey(r.TypeId)).Select(r => r.TypeId).ToList(),
            Warnings             = planWarnings,
            RawMaterials         = rawMaterials,
            Intermediates        = intermediates,
            FinalProducts        = finalProducts,
            Leftovers            = leftovers,
            TotalRawMaterialCost = totalRawMat,
            TotalJobCost         = totalJobCost,
            TotalLeftoverValue   = totalLeftover,
            NetCost              = totalRawMat + totalJobCost - totalLeftover,
        };
    }

    // Default ME to pre-select when an item is added to the production queue, per the shared rule
    // (ME10 / T2 ME3 / BPC-only ME0 / titan & Keepstar ME9 / reactions ME0). Users can override it.
    public async Task<int> GetDefaultMeAsync(int productTypeId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bp = await db.SdeBlueprintProducts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProductTypeId == productTypeId
                && (p.Activity == MfgActivity || p.Activity == RxnActivity), ct);
        if (bp is null) return 10;
        bool isReaction = bp.Activity == RxnActivity;

        var prod = await db.SdeTypes.AsNoTracking()
            .Where(t => t.TypeId == productTypeId)
            .Select(t => new { t.MetaGroupId, t.GroupId })
            .FirstOrDefaultAsync(ct);
        int? meta  = prod?.MetaGroupId;
        int  group = prod?.GroupId ?? 0;

        // BPO-sourced if the blueprint has a market group or is invented from one; faction/loot
        // tiers (meta 3-6) are always BPC-only regardless.
        bool bpHasMarket = await db.SdeTypes.AnyAsync(t => t.TypeId == bp.TypeId && t.MarketGroupId != null, ct);
        bool inventedFromMarket = await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => p.Activity == "invention" && p.ProductTypeId == bp.TypeId)
            .Join(db.SdeTypes, p => p.TypeId, t => t.TypeId, (p, t) => t.MarketGroupId)
            .AnyAsync(mg => mg != null, ct);
        bool lootTier = meta is >= 3 and <= 6;
        bool bpcOnly  = !isReaction && (lootTier || !(bpHasMarket || inventedFromMarket));

        bool isT2 = meta == 2;
        bool isTitanKeepstar = group == IndustryMe.TitanGroupId || productTypeId == IndustryMe.KeepstarTypeId;
        return IndustryMe.DefaultMe(isReaction, bpcOnly, isT2, isTitanKeepstar);
    }

    // ── Batch-add helpers: direct materials (single blueprint) ────────────────

    public async Task<Dictionary<int, (int Qty, string Name)>> GetDirectMaterialsAsync(
        int blueprintTypeId,
        int runs,
        int meLevel,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var mats = await db.SdeBlueprintMaterials.AsNoTracking()
            .Where(m => m.TypeId == blueprintTypeId && m.Activity == MfgActivity)
            .ToListAsync(ct);

        var typeIds  = mats.Select(m => m.MaterialTypeId).Distinct().ToList();
        var names    = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        double meFactor = (100.0 - meLevel) / 100.0;
        var result = new Dictionary<int, (int, string)>();
        foreach (var m in mats)
        {
            int qty = Math.Max(runs, (int)Math.Ceiling(m.Quantity * meFactor * runs));
            result[m.MaterialTypeId] = (qty, names.GetValueOrDefault(m.MaterialTypeId, $"Type {m.MaterialTypeId}"));
        }
        return result;
    }

    // ── Batch-add helpers: whole-chain raw materials ──────────────────────────

    // With a park: calls full CalculateAsync so rig bonuses are applied.
    // Without a park: simple recursive expansion using ME only (no rig bonuses).
    public async Task<Dictionary<int, (int Qty, string Name)>> GetChainMaterialsAsync(
        int productTypeId,
        int runs,
        int meLevel,
        int? parkId,
        CancellationToken ct = default)
    {
        if (parkId.HasValue)
        {
            var plan = await CalculateAsync(
                [new ProductionQueueEntry { TypeId = productTypeId, Quantity = runs, MeLevel = meLevel }],
                parkId.Value, ct: ct);
            return plan.RawMaterials.ToDictionary(
                r => r.TypeId,
                r => (r.Quantity, r.TypeName));
        }

        // No park: simple recursive expansion with ME only
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Published blueprints only (avoids junk unpublished duplicates — which would also
        // throw here on the ToDictionary duplicate key).
        var bpProducts = await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => p.Activity == MfgActivity
                     && db.SdeTypes.Any(t => t.TypeId == p.TypeId && t.Published))
            .ToListAsync(ct);
        var byProduct = bpProducts.ToDictionary(p => p.ProductTypeId);

        var bpTypeIds = bpProducts.Select(p => p.TypeId).Distinct().ToList();
        var bpMats = await db.SdeBlueprintMaterials.AsNoTracking()
            .Where(m => bpTypeIds.Contains(m.TypeId) && m.Activity == MfgActivity)
            .ToListAsync(ct);
        var materialsByBp = bpMats.GroupBy(m => m.TypeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rawPool = new Dictionary<int, int>();

        void ExpandSimple(int typeId, int qty)
        {
            if (!byProduct.TryGetValue(typeId, out var bpProd))
            {
                rawPool[typeId] = rawPool.GetValueOrDefault(typeId) + qty;
                return;
            }
            // Non-final items use default ME 10; final product uses caller-supplied ME
            bool isFinal = typeId == productTypeId;
            int  me      = isFinal ? meLevel : 10;
            double factor = (100.0 - me) / 100.0;
            int jobRuns   = (int)Math.Ceiling((double)qty / bpProd.Quantity);
            var mats = materialsByBp.TryGetValue(bpProd.TypeId, out var m) ? m : [];
            foreach (var mat in mats)
            {
                int effPerRun = Math.Max(1, (int)Math.Ceiling(mat.Quantity * factor));
                ExpandSimple(mat.MaterialTypeId, effPerRun * jobRuns);
            }
        }

        ExpandSimple(productTypeId, runs);

        var allTypeIds = rawPool.Keys.ToList();
        var names = await db.SdeTypes.AsNoTracking()
            .Where(t => allTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        return rawPool.ToDictionary(
            kv => kv.Key,
            kv => (kv.Value, names.GetValueOrDefault(kv.Key, $"Type {kv.Key}")));
    }

    // ── Stock availability ────────────────────────────────────────────────────

    /// <summary>How the Raw Materials tab decides what is missing.</summary>
    public enum MissingMode
    {
        /// <summary>Compare against stock at each job's linked facility. Jobs sharing a
        /// structure share its pile, so their demand is summed before comparing.</summary>
        Station,

        /// <summary>Compare against every asset owned, wherever it sits.</summary>
        Assets,
    }

    /// <summary>
    /// Fills the availability fields on a finished plan from current asset holdings.
    ///
    /// Job rows are always station-based: a job's materials are compared against stock at
    /// the facility its park structure is linked to, independently of every other job —
    /// the question a job row answers is "can I start this one now".
    ///
    /// Raw material rows answer a different question — "what do I have to buy" — so there
    /// the same stock cannot be promised to two jobs. In <see cref="MissingMode.Station"/>
    /// demand is summed per structure and each structure's shortfall added up; ten jobs in
    /// one Raitaru compete for one pile. In <see cref="MissingMode.Assets"/> the plan total
    /// is compared against everything owned.
    ///
    /// Anything that cannot be answered is left unknown rather than guessed: a job whose
    /// park structure has no linked facility has no station whose stock could be counted,
    /// and reporting its full requirement as "missing" would be a fabricated number.
    /// </summary>
    public async Task ApplyAvailabilityAsync(
        ProductionPlan plan,
        MissingMode rawMode,
        CancellationToken ct = default)
    {
        var typeIds = plan.AllJobs.SelectMany(j => j.Materials).Select(m => m.MaterialTypeId)
            .Concat(plan.RawMaterials.Select(r => r.TypeId))
            .Distinct().ToList();
        if (typeIds.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // RootLocationId is the terminal station reached by walking the container chain, so
        // this counts materials sitting in cans and ship holds at the station too.
        var rows = await db.EsiAssets.AsNoTracking()
            .Where(a => typeIds.Contains(a.TypeId))
            .Select(a => new { a.TypeId, a.RootLocationId, a.Quantity })
            .ToListAsync(ct);

        var byStation = rows
            .GroupBy(a => (a.RootLocationId, a.TypeId))
            .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity));
        var everywhere = rows
            .GroupBy(a => a.TypeId)
            .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity));

        static int Clamp(long v) => (int)Math.Min(v, int.MaxValue);

        int StockAt(long stationId, int typeId) =>
            byStation.TryGetValue((stationId, typeId), out var q) ? Clamp(q) : 0;

        // ── Job rows ─────────────────────────────────────────────────────────
        foreach (var job in plan.AllJobs)
        foreach (var mat in job.Materials)
        {
            mat.AvailabilityKnown = job.StationId.HasValue;
            mat.Available = job.StationId.HasValue ? StockAt(job.StationId.Value, mat.MaterialTypeId) : 0;
        }

        // ── Raw material rows ────────────────────────────────────────────────
        if (rawMode == MissingMode.Assets)
        {
            foreach (var raw in plan.RawMaterials)
            {
                raw.AvailabilityKnown = true;
                raw.Available = everywhere.TryGetValue(raw.TypeId, out var q) ? Clamp(q) : 0;
                raw.Missing   = Math.Max(0, raw.Quantity - raw.Available);
            }
            return;
        }

        // Station mode. Demand is rebuilt from the jobs rather than taken from the raw
        // material totals, because the shortfall has to be worked out structure by
        // structure before it can be added up.
        var demand = new Dictionary<(long Station, int TypeId), long>();
        var unlinked = new HashSet<int>();   // materials with demand from an unlinked job

        foreach (var job in plan.AllJobs)
        foreach (var mat in job.Materials.Where(m => m.IsBought))
        {
            if (!job.StationId.HasValue) { unlinked.Add(mat.MaterialTypeId); continue; }
            var key = (job.StationId.Value, mat.MaterialTypeId);
            demand[key] = demand.GetValueOrDefault(key) + mat.TotalQty;
        }

        var shortfall = new Dictionary<int, long>();
        var onHand    = new Dictionary<int, long>();
        foreach (var ((station, typeId), needed) in demand)
        {
            long stock = StockAt(station, typeId);
            shortfall[typeId] = shortfall.GetValueOrDefault(typeId) + Math.Max(0, needed - stock);
            // Only the part actually usable against this structure's demand, so the
            // Available column never claims more stock than the shortfall accounts for.
            onHand[typeId]    = onHand.GetValueOrDefault(typeId) + Math.Min(needed, stock);
        }

        foreach (var raw in plan.RawMaterials)
        {
            // Unknown as soon as any job needing this material has no linked facility —
            // its share of the demand cannot be checked against anything, so a total that
            // silently omitted it would read as more complete than it is.
            raw.AvailabilityKnown = !unlinked.Contains(raw.TypeId);
            raw.Available = raw.AvailabilityKnown ? Clamp(onHand.GetValueOrDefault(raw.TypeId))    : 0;
            raw.Missing   = raw.AvailabilityKnown ? Clamp(shortfall.GetValueOrDefault(raw.TypeId)) : 0;
        }
    }
}
