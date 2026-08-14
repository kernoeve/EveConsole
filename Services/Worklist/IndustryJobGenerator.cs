using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Jobs to start, and who should start them.
///
/// Demand comes from inventory rules marked Build: a group short of target whose items are made
/// rather than bought. Each shortfall becomes a job, assigned to the least capable character who
/// can actually run it — the alt who can build titans should not be filled with work anyone
/// could do, because that capacity is the one thing that cannot be substituted.
///
/// <para>Readiness earns its keep here. A job whose materials are not at the structure is
/// Blocked and says what is missing; one that is ready to go but has no free slot is Waiting;
/// only a job that could be started right now is Ready. That distinction is the whole reason
/// the tool exists — logging in to start a job and finding the inputs elsewhere is the cost
/// being paid today.</para>
/// </summary>
public class IndustryJobGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    IndustryAssignmentService       assignment,
    InvLevelService                 invLevels,
    ProductionCalculatorService     production,
    WorklistSettings                settings) : IWorklistGenerator
{
    public string Id          => "industry_jobs";
    public string DisplayName => "Industry Jobs";


    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var parkId = settings.IndustryParkId;
        if (parkId <= 0) return [];

        var rules = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled && r.Action == "Build")
            .ToListAsync(ct);
        if (rules.Count == 0) return [];

        var candidates = await assignment.LoadCandidatesAsync(ct);
        if (candidates.Count == 0) return [];

        // Loaded once per run and passed down. Calculate() is pure given a context, so each item
        // costs only arithmetic — and a field would race, since generators run in parallel and
        // two refreshes can overlap.
        var ctx = await production.LoadContextAsync(parkId, ct);

        // Where the work happens. Only linked structures can be checked for materials — an
        // unlinked one models rigs but points at no real place, so nothing can be counted there.
        var linked = await db.IndyStructures.AsNoTracking()
            .Where(s => s.ParkId == parkId && s.RealStructureId != null)
            .Select(s => new { s.RealStructureId, s.RealStructureName })
            .ToListAsync(ct);
        var siteIds = linked.Select(s => s.RealStructureId!.Value).ToList();

        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => rules.Select(r => r.GroupId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        var items     = new List<WorklistItem>();
        var slotsLeft = candidates.ToDictionary(
            c => c.Config.CharacterId,
            c => new Dictionary<IndustryPool, int>(c.FreeSlots));

        // Rules in a fixed order, and items within them too, so the greedy slot allocation below
        // walks demand identically on every run and therefore assigns identically.
        foreach (var rule in rules.OrderByDescending(r => r.ThresholdPercent).ThenBy(r => r.Id))
        {
            if (!groups.TryGetValue(rule.GroupId, out var group)) continue;

            var groupItems = await db.InvLevelItems.AsNoTracking()
                .Where(i => i.GroupId == group.Id)
                .ToListAsync(ct);
            if (groupItems.Count == 0) continue;

            var typeIds = groupItems.Select(i => i.TypeId).Distinct().ToList();
            var avail   = await invLevels.LoadAvailableAsync(group, typeIds, ct);
            var names   = await invLevels.GetTypeNamesAsync(typeIds, ct);

            // Blueprints that make these items, and what each needs by way of skills.
            var products = await db.SdeBlueprintProducts.AsNoTracking()
                .Where(p => typeIds.Contains(p.ProductTypeId))
                .ToListAsync(ct);
            var bpIds = products.Select(p => p.TypeId).Distinct().ToList();
            var bpSkills = (await db.SdeBlueprintSkills.AsNoTracking()
                    .Where(s => bpIds.Contains(s.TypeId))
                    .ToListAsync(ct))
                .GroupBy(s => (s.TypeId, s.Activity))
                .ToDictionary(g => g.Key, g => (IReadOnlyList<SdeBlueprintSkill>)g.ToList());

            foreach (var gi in groupItems.OrderBy(i => i.TypeId))
            {
                var target = (long)gi.TargetQuantity * Math.Max(1, group.Multiplier);
                if (target <= 0) continue;

                var a    = avail.TryGetValue(gi.TypeId, out var av) ? av : null;
                var have = (a?.Assets ?? 0) + (a?.IndustryJobs ?? 0);
                if (have >= target * (rule.ThresholdPercent / 100.0)) continue;

                var wanted    = (long)Math.Ceiling(target * (rule.FillTargetPercent / 100.0));
                var shortfall = wanted - have;
                if (shortfall <= 0) continue;

                var product = products
                    .Where(p => p.ProductTypeId == gi.TypeId)
                    .OrderBy(p => p.TypeId)
                    .FirstOrDefault();
                if (product is null) continue;   // nothing makes it — a Buy rule's job, not this

                var pool     = product.Activity == "reaction" ? IndustryPool.Reaction
                                                             : IndustryPool.Manufacturing;
                var required = bpSkills.GetValueOrDefault((product.TypeId, product.Activity), []);
                var eligible = IndustryAssignmentService.EligibleFor(candidates, pool, required);
                var name     = names.GetValueOrDefault(gi.TypeId, $"Type {gi.TypeId}");
                var pct      = have * 100.0 / target;

                // An inventory level is more urgent the emptier it is, so the priority carries
                // how far below target it has fallen rather than treating every shortfall alike.
                var priority = IndustryPriority.ForInventory(pct);

                string blockedBy = "";
                var readiness    = WorklistReadiness.Ready;
                IndustryCandidate? chosen = null;

                if (eligible.Count == 0)
                {
                    readiness = WorklistReadiness.Blocked;
                    blockedBy = required.Count > 0
                        ? "No enabled character has the skills for this job"
                        : "No enabled character runs this activity";
                }
                else
                {
                    var needed = await MaterialsForAsync(ctx, gi.TypeId, shortfall, ct);

                    foreach (var c in eligible)
                    {
                        if (slotsLeft[c.Config.CharacterId].GetValueOrDefault(pool) <= 0) continue;
                        chosen = c;
                        break;
                    }

                    if (chosen is null)
                    {
                        // Everyone able to do it is busy — real information, and different from
                        // being unable to do it at all.
                        chosen    = eligible[0];
                        readiness = WorklistReadiness.Waiting;
                        blockedBy = "Every character who can run this has all slots busy";
                    }
                    else
                    {
                        // Materials are checked against the chosen character's own reach: which
                        // hangars they can draw from, at the park's linked structures. A job whose
                        // inputs sit somewhere else cannot be started, and saying so is the point
                        // of the tool.
                        var missing = await MissingAtSiteAsync(db, chosen, needed, siteIds, ct);

                        if (missing.Count > 0)
                        {
                            readiness = WorklistReadiness.Blocked;
                            blockedBy = "Materials not at the build site: "
                                      + string.Join(", ", missing.Take(4))
                                      + (missing.Count > 4 ? $", and {missing.Count - 4} more" : "");
                        }
                        else
                        {
                            // Only a job that can actually start consumes a slot in this pass.
                            slotsLeft[chosen.Config.CharacterId][pool] -= 1;
                        }
                    }
                }

                var siteNote = siteIds.Count == 0
                    ? " No structure in the park is linked to a real location, so materials cannot be checked."
                    : "";

                items.Add(new WorklistItem
                {
                    // No character in the key. Assignment can legitimately move between refreshes
                    // as slots free up, and a key that moved with it would reset the item's age
                    // and silently drop its snooze.
                    Key           = $"industry_job:{rule.Id}:{gi.TypeId}",
                    Source        = Id,
                    Title         = $"Start job — {name}",
                    Detail        = $"{group.Name} · {have:N0} of {target:N0} ({pct:0.#}%) — "
                                  + $"build {shortfall:N0}.{siteNote}",
                    Readiness     = readiness,
                    BlockedBy     = blockedBy,
                    CharacterId   = chosen?.Config.CharacterId   ?? 0,
                    CharacterName = chosen?.Config.CharacterName ?? "",
                    TypeId        = gi.TypeId,
                    TypeName      = name,
                    Priority      = priority,
                });
            }
        }

        return items;
    }

    /// <summary>
    /// Exactly what one job consumes, at the ME and rig bonuses that will actually apply.
    ///
    /// Planned through <see cref="ProductionCalculatorService"/> against the configured park, so
    /// the figures match what the Production Calculator would quote for the same build. Base SDE
    /// quantities were tried first and were wrong in the direction that matters: over-stating a
    /// requirement produces a job reported as blocked for materials that are sitting in the
    /// station. On an expensive, rarely-run build the difference is not a rounding error, and
    /// waiting on a job that could have started is the exact cost this tool exists to remove.
    /// </summary>
    private async Task<Dictionary<int, long>> MaterialsForAsync(
        ProductionContext ctx, int productTypeId, long quantity, CancellationToken ct)
    {
        var entry = new ProductionQueueEntry
        {
            TypeId   = productTypeId,
            Quantity = (int)Math.Min(int.MaxValue, quantity),
            MeLevel  = await production.GetDefaultMeAsync(productTypeId, ct),
        };

        var plan = production.Calculate([entry], ctx);

        // The root job's own inputs. Sub-components the plan would build are separate jobs with
        // their own worklist items, so their materials are not this job's problem.
        var root = plan.AllJobs.FirstOrDefault(j => j.OutputTypeId == productTypeId);
        if (root is null) return [];

        return root.Materials
            .GroupBy(m => m.MaterialTypeId)
            .ToDictionary(g => g.Key, g => (long)g.Sum(m => m.TotalQty));
    }

    /// <summary>
    /// Which of the needed materials are not within the chosen character's reach at the build
    /// site, named rather than counted — "short Tritanium" tells you what to move.
    ///
    /// Reach is deliberately per character. Materials pooled in a corp hangar serve every alt in
    /// that corp, while a player who keeps stock in personal hangars needs the personal side
    /// counted instead; assuming either would suggest jobs that cannot start.
    /// </summary>
    private static async Task<List<string>> MissingAtSiteAsync(
        AppDbContext db, IndustryCandidate who, Dictionary<int, long> needed,
        List<long> siteIds, CancellationToken ct)
    {
        // Without a linked structure there is nowhere to look. Reporting everything as missing
        // would be a guess dressed as a finding, so the caller notes the gap instead.
        if (needed.Count == 0 || siteIds.Count == 0) return [];

        var typeIds = needed.Keys.ToList();

        var rows = await db.EsiAssets.AsNoTracking()
            .Where(a => typeIds.Contains(a.TypeId) && siteIds.Contains(a.RootLocationId))
            .Select(a => new { a.TypeId, a.OwnerId, a.OwnerType, a.Quantity })
            .ToListAsync(ct);

        var reachable = rows
            .Where(a => a.OwnerType == "corporation"
                          ? who.Config.IncludeCorpAssets
                          : who.Config.IncludePersonalAssets && a.OwnerId == who.Config.CharacterId)
            .GroupBy(a => a.TypeId)
            .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity));

        var shortIds = needed
            .Where(n => reachable.GetValueOrDefault(n.Key) < n.Value)
            .Select(n => n.Key)
            .OrderBy(id => id)
            .ToList();
        if (shortIds.Count == 0) return [];

        var names = await db.SdeTypes.AsNoTracking()
            .Where(t => shortIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        return shortIds.Select(id => names.GetValueOrDefault(id, $"Type {id}")).ToList();
    }
}

/// <summary>
/// How industry work ranks against itself and against everything else.
///
/// Two things drive it. Work that fills a customer order outranks work that tops up a shelf,
/// because someone is waiting on the first. And within stock-keeping, emptier outranks fuller:
/// a group at 25% of target is closer to stopping production than one at 75%, so it earns a
/// higher place even though both are technically short.
/// </summary>
public static class IndustryPriority
{
    /// <summary>Jobs serving a pending customer order.</summary>
    public const int OrderDriven = 120;

    /// <summary>
    /// Stock-keeping, scaled by depletion: empty scores near the order-driven band without
    /// reaching it, full-but-short scores just above the floor.
    /// </summary>
    public static int ForInventory(double percentOfTarget)
    {
        var depleted = Math.Clamp(100 - percentOfTarget, 0, 100) / 100.0;
        return 40 + (int)Math.Round(depleted * 60);   // 40 at target, 100 at empty
    }
}
