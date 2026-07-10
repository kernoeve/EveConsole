using EveCortex.Data;
using EveCortex.Models;
using Microsoft.EntityFrameworkCore;

namespace EveCortex.Services;

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

    public async Task<ProductionPlan> CalculateAsync(
        List<ProductionQueueEntry> requests,
        int parkId,
        bool includeBpcCost = false,
        CancellationToken ct = default)
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
        bool BlueprintIsBpoSourced(int bpTypeId) =>
            marketBlueprints.Contains(bpTypeId) || inventedFromMarket.Contains(bpTypeId);

        // ── Type names and group/category info ─────────────────────────────
        var typeNames = await db.SdeTypes.AsNoTracking()
            .Select(t => new { t.TypeId, t.Name })
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var typeGroupMap = await db.SdeTypes.AsNoTracking()
            .Select(t => new { t.TypeId, t.GroupId })
            .ToDictionaryAsync(t => t.TypeId, ct);

        var groupCatMap = await db.SdeGroups.AsNoTracking()
            .Select(g => new { g.GroupId, g.CategoryId, g.Name })
            .ToDictionaryAsync(g => g.GroupId, ct);

        // ── Park / structure data ──────────────────────────────────────────
        var structures  = await db.IndyStructures.AsNoTracking().Where(s => s.ParkId == parkId).ToListAsync(ct);
        var rigs        = await db.IndyStructureRigs.AsNoTracking()
            .Where(r => structures.Select(s => s.Id).Contains(r.StructureId))
            .ToListAsync(ct);
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

        // ── Per-structure cost indices ─────────────────────────────────────
        var systemNames = structures.Select(s => s.SystemName).Distinct().ToList();
        var systemIds   = await db.SdeSolarSystems.AsNoTracking()
            .Where(ss => systemNames.Contains(ss.Name))
            .ToDictionaryAsync(ss => ss.Name, ss => ss.SolarSystemId, ct);

        var costIndices = await db.IndustryCostIndices.AsNoTracking().ToListAsync(ct);
        var ciLookup    = costIndices.GroupBy(c => c.SolarSystemId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(c => c.Activity, c => c.CostIndex));

        double GetCostIndex(IndyStructure? s, string activity)
        {
            if (s is null) return 0;
            if (!systemIds.TryGetValue(s.SystemName, out var sysId)) return 0;
            return ciLookup.TryGetValue(sysId, out var ci) && ci.TryGetValue(activity, out var idx) ? idx : 0;
        }

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

        // ── Adjusted prices (for EIV / job cost) ──────────────────────────
        var adjPrices = await db.EsiAdjustedPrices.AsNoTracking()
            .ToDictionaryAsync(p => p.TypeId, p => p.AdjustedPrice, ct);

        // ── Pre-computed build costs (used for leftover valuation and missing-price fallback) ─
        var buildCostLookup = await db.BuildCosts.AsNoTracking()
            .ToDictionaryAsync(b => b.TypeId, b => b.TotalCost, ct);

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
                    429 or 974 or 4096 => "react_composite",   // Composite / Hybrid Polymers / Molecular-Forged
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
                (8, _)          => "ammo_charges",
                (18 or 87, _)   => "drones_fighters",
                _ when tg.GroupId == 1136                                 => "structure_ammo",   // Fuel Blocks
                _ when gc.Name.Contains("Capital") && gc.CategoryId == 4  => "capital_components",
                _ when gc.Name.Contains("Component")                       => "adv_components",
                _ when gc.CategoryId is 22 or 65                          => "structure_ammo",
                // R.A.M. items and Data Interfaces are manufactured at standard facilities
                _ when gc.CategoryId == 17 && gc.Name is "Tool" or "Data Interfaces" => "modules_equipment",
                _ => ""
            };
        }

        // Returns null when the category is unknown or not configured in this park.
        // Item-level overrides take precedence over category assignments.
        // Callers must NOT fall back to a default — missing assignments are caught after expansion.
        IndyStructure? StructureFor(string catKey, int typeId)
        {
            if (itemOverrides.TryGetValue(typeId, out var overrideStruct))
                return overrideStruct;
            if (string.IsNullOrEmpty(catKey)) return null;
            return structByCategory.TryGetValue(catKey, out var s) ? s : null;
        }

        // ── Expansion state ────────────────────────────────────────────────
        var jobPool       = new Dictionary<int, PlanJob>();
        var rawPool       = new Dictionary<int, int>();
        var finalMeLevels = requests.ToDictionary(r => r.TypeId, r => r.MeLevel);

        // Tracks items whose category could not be determined or is not assigned in this park.
        var unmappedItems = new SortedSet<string>();

        void ExpandItem(int typeId, int qty, bool isFinal)
        {
            if (!blueprintByProduct.TryGetValue(typeId, out var bpProd))
            {
                rawPool[typeId] = rawPool.GetValueOrDefault(typeId, 0) + qty;
                return;
            }

            var    activity   = bpProd.Activity;
            bool   isReaction = activity == RxnActivity;
            string catKey     = ItemCategoryKey(typeId, isReaction);

            // Detect misconfigured items early — collect rather than silently wrong-assigning.
            // Item-level overrides satisfy the requirement regardless of category status.
            if (!itemOverrides.ContainsKey(typeId))
            {
                if (string.IsNullOrEmpty(catKey))
                {
                    var name = typeNames.GetValueOrDefault(typeId, $"TypeId {typeId}");
                    unmappedItems.Add($"{name} (unrecognized type — update ItemCategoryKey)");
                }
                else if (!structByCategory.ContainsKey(catKey))
                {
                    var name = typeNames.GetValueOrDefault(typeId, $"TypeId {typeId}");
                    unmappedItems.Add($"{name} (category '{catKey}' not assigned in this park)");
                }
            }

            // Reaction formulas have no ME research — always ME 0. Manufacturing BPs default to ME 10.
            int    meLevel    = isReaction ? 0 : (isFinal && finalMeLevels.TryGetValue(typeId, out var ml)) ? ml : 10;
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
                        int effPerRun = Math.Max(1, (int)Math.Ceiling(mat.Quantity * meFactor));
                        // Keep TotalQty in sync when the job gains additional runs
                        var existingMat = existing.Materials.FirstOrDefault(m => m.MaterialTypeId == mat.MaterialTypeId);
                        if (existingMat is not null)
                            existingMat.TotalQty += effPerRun * extraRuns;
                        ExpandItem(mat.MaterialTypeId, effPerRun * extraRuns, false);
                    }
                    // If this (final) job included its blueprint copy, keep the BPC quantity in
                    // sync as the job gains runs (and add the extra copies to the raw pool).
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
                    MeReductionPct = meLevel,
                    RigBonusPct    = rigBonus * 100.0,
                    RoleBonusPct   = matRoleBonus * 100.0,
                    CombinedFactor = meFactor,
                };
                foreach (var mat in bpMats)
                {
                    int    basePerRun = mat.Quantity;
                    double raw        = basePerRun * meFactor;
                    int    effPerRun  = Math.Max(1, (int)Math.Ceiling(raw));
                    job.Materials.Add(new PlanJobMaterial
                    {
                        MaterialTypeId = mat.MaterialTypeId,
                        TypeName       = typeNames.GetValueOrDefault(mat.MaterialTypeId, $"Type {mat.MaterialTypeId}"),
                        BaseQtyPerRun  = basePerRun,
                        EffQtyPerRun   = effPerRun,
                        TotalQty       = effPerRun * runs,
                        IsBought       = !blueprintByProduct.ContainsKey(mat.MaterialTypeId),
                        FormulaDisplay = $"ceil({basePerRun:N0} × {meFactor:F4}) = ceil({raw:N2}) → {effPerRun:N0}",
                    });
                    ExpandItem(mat.MaterialTypeId, effPerRun * runs, false);
                }
                // Include the blueprint copy as a bought input for the FINAL product only (one per
                // run), valued at its contract-derived market value — forced when the item is non-BPO
                // (no obtainable BPO) and optional otherwise (the includeBpcCost toggle). Sub-materials
                // never add their BPC. It's bought (never expanded) and joins the raw-material pool.
                if (!isReaction && isFinal && (!BlueprintIsBpoSourced(bpProd.TypeId) || includeBpcCost))
                {
                    job.Materials.Add(new PlanJobMaterial
                    {
                        MaterialTypeId = bpProd.TypeId,
                        TypeName       = typeNames.GetValueOrDefault(bpProd.TypeId, $"Type {bpProd.TypeId}") + " (BPC)",
                        BaseQtyPerRun  = 1,
                        EffQtyPerRun   = 1,
                        TotalQty       = runs,
                        IsBought       = true,
                        FormulaDisplay = "1 BPC per run (contract price)",
                    });
                    ExpandItem(bpProd.TypeId, runs, false);
                }
                jobPool[typeId] = job;
            }
        }

        foreach (var req in requests)
            ExpandItem(req.TypeId, req.Quantity, true);

        if (unmappedItems.Count > 0)
        {
            var sample = string.Join("\n  • ", unmappedItems.Take(10));
            var suffix = unmappedItems.Count > 10 ? $"\n  … and {unmappedItems.Count - 10} more" : "";
            throw new InvalidOperationException(
                $"{unmappedItems.Count} item(s) cannot be assigned to a structure in this park:\n  • {sample}{suffix}");
        }

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
                mat.UnitPrice = PriceOf(mat.MaterialTypeId);
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
        var finalProducts = requests.Select(req =>
        {
            var rootJob = jobPool.GetValueOrDefault(req.TypeId);
            var seen    = new HashSet<int>();
            decimal subtreeRawMat = 0;
            decimal subtreeJobCost = 0;

            void WalkSubtree(int tid)
            {
                if (!seen.Add(tid) || !jobPool.TryGetValue(tid, out var j)) return;
                subtreeJobCost += j.JobCost;
                foreach (var mat in j.Materials)
                    if (mat.IsBought) subtreeRawMat += mat.TotalQty * mat.UnitPrice;
                foreach (var childId in j.ChildTypeIds)
                    WalkSubtree(childId);
            }
            if (rootJob is not null) WalkSubtree(req.TypeId);

            decimal totalCost = subtreeRawMat + subtreeJobCost;
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
}
