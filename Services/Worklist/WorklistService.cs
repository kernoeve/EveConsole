using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>One generator's contribution to a run, kept separate so a failure is attributable.</summary>
public sealed record WorklistSection(string SourceId, string DisplayName,
                                     List<WorklistItem> Items, string? Error);

public sealed record WorklistRun(List<WorklistSection> Sections, DateTimeOffset GeneratedAt)
{
    public IEnumerable<WorklistItem> AllItems => Sections.SelectMany(s => s.Items);
}

/// <summary>
/// Builds the worklist by asking every generator what needs doing, then layering on the small
/// amount of state that cannot be recomputed.
///
/// Generators run in parallel and are individually guarded: one throwing costs its own section
/// and nothing else, because a worklist that vanishes whenever a single rule has a bad day is
/// worse than one with a gap in it.
/// </summary>
public class WorklistService(
    IDbContextFactory<AppDbContext> dbFactory,
    IEnumerable<IWorklistGenerator> generators,
    WorklistSettings                settings,
    IndustryAssignmentService       assignment,
    AppErrorLogger                  errorLogger)
{
    private readonly List<IWorklistGenerator> _generators = generators.ToList();

    public IReadOnlyList<IWorklistGenerator> Generators => _generators;

    public WorklistSettings Settings => settings;

    public async Task<WorklistRun> BuildAsync(CancellationToken ct = default)
    {
        // Newly authorised characters join the industry list here, once, before the fan-out below.
        // Inside a generator it would run once per generator in parallel, and they would race to
        // insert the same rows.
        await assignment.EnrolMissingAsync(ct);

        // A disabled source is skipped rather than filtered afterwards: it should cost no
        // queries at all, not run and have its output thrown away.
        var active = _generators.Where(g => settings.IsSourceEnabled(g.Id)).ToList();

        var tasks = active.Select(async g =>
        {
            try
            {
                var items = await g.GenerateAsync(ct);
                return new WorklistSection(g.Id, g.DisplayName, items, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errorLogger.Log("WorklistService", g.Id, ex);
                return new WorklistSection(g.Id, g.DisplayName, [], ex.Message);
            }
        });

        var sections = (await Task.WhenAll(tasks)).ToList();

        // Before the merge, because these carry no quantity and would not merge — two rows saying
        // the same bid is losing is a duplicate however the amounts work out.
        DropDuplicateOutbid(sections);

        // Before volume and state: the merged task must be sized and aged as the one task it is,
        // not as whichever fragment happened to be written first.
        MergeDuplicatePurchases(sections);

        // After the merge, so a haul that was two rows is promoted once, as the trip it is.
        PromoteUnblockingHauls(sections);

        await ApplyVolumeAsync(sections, ct);
        await ApplyStateAsync(sections, ct);

        return new WorklistRun(sections, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Ranks each haul by the work it would restart.
    ///
    /// <para>A delivery is worth what it unblocks, and no generator can see that on its own:
    /// the logistics generator knows a station is short of something, and the industry
    /// generator knows which jobs stopped because of it, and neither knows the other. One
    /// crate of Self-Harmonizing Power Cores restarting four jobs at one station outranks a
    /// trip that restarts one, and until now they sorted identically.</para>
    ///
    /// <para>⚠️ The haul inherits the priority of the most urgent job it frees, never more.
    /// Adding a bonus per job unblocked would push a haul through the order band, where one
    /// step means one customer order — so the count breaks ties instead, below priority.</para>
    /// </summary>
    private static void PromoteUnblockingHauls(List<WorklistSection> sections)
    {
        var all = sections.SelectMany(s => s.Items).ToList();

        PromoteUnblockingBuys(sections, all);

        // Jobs stopped for want of material that exists somewhere else, by where they run.
        // MustBuy shortfalls are excluded: no haul fixes something nobody owns.
        var stopped = all
            .Where(x => x.LocationId > 0)
            .SelectMany(x => x.Shortages.Where(h => !h.MustBuy)
                              .Select(h => (Job: x, Key: (x.LocationId, h.TypeId))))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Job).ToList());

        if (stopped.Count == 0) return;

        for (var si = 0; si < sections.Count; si++)
        {
            var section = sections[si];

            for (var n = 0; n < section.Items.Count; n++)
            {
                var haul = section.Items[n];
                if (haul.Kind != WorklistKind.Haul || haul.DestinationId <= 0) continue;

                var carried = haul.Lines.Count > 0
                    ? haul.Lines.Select(l => l.TypeId)
                    : [haul.TypeId];

                var freed = carried
                    .SelectMany(t => stopped.GetValueOrDefault((haul.DestinationId, t), []))
                    .DistinctBy(j => j.Key)
                    .ToList();

                if (freed.Count == 0) continue;

                section.Items[n] = haul with
                {
                    Unblocks = freed.Count,
                    Priority = Math.Max(haul.Priority, freed.Max(j => j.Priority)),
                    Detail   = haul.Detail
                             + $" Restarts {freed.Count:N0} stopped job(s) on arrival: "
                             + string.Join(", ", freed.Take(3).Select(j => j.TypeName))
                             + (freed.Count > 3 ? $", and {freed.Count - 3:N0} more." : "."),
                };
            }
        }
    }

    /// <summary>
    /// Ranks each purchase by the work it would release, exactly as hauls are ranked.
    ///
    /// <para>The two are complements and read the same shortage list from opposite ends. A haul
    /// answers a shortage of something we own elsewhere; a purchase answers one of something
    /// nobody owns, which is what <c>MustBuy</c> marks. Neither generator can see the jobs it
    /// would restart — the purchase generator knows the plan is short, the industry generator
    /// knows which jobs stopped, and neither knows the other.</para>
    ///
    /// <para>⚠️ No destination filter, unlike the haul pass. A haul lands its cargo at one
    /// station and only frees jobs there; a purchase of something nobody owns answers the
    /// shortage wherever the job is, because the alternative to buying it is not having it at
    /// all.</para>
    ///
    /// <para>⚠️ The purchase inherits the priority of the most urgent job it frees, never more,
    /// and the count breaks ties below priority rather than adding to it — the same rule the
    /// hauls follow, so a crate and a market order for the same material stay commensurable.
    /// Purchases used to carry a flat OrderDriven, which put every one of them above refining,
    /// outbid orders, standing projects and final products regardless of what was waiting.</para>
    /// </summary>
    private static void PromoteUnblockingBuys(
        List<WorklistSection> sections, List<WorklistItem> all)
    {
        // Jobs stopped for want of something nobody owns, by material. The mirror of the haul
        // rule below, which excludes exactly these for the same reason.
        var unowned = all
            .SelectMany(x => x.Shortages.Where(h => h.MustBuy)
                              .Select(h => (Job: x, h.TypeId)))
            .GroupBy(x => x.TypeId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Job).DistinctBy(j => j.Key).ToList());

        if (unowned.Count == 0) return;

        for (var si = 0; si < sections.Count; si++)
        {
            var section = sections[si];

            for (var n = 0; n < section.Items.Count; n++)
            {
                var buy = section.Items[n];
                if (buy.Kind != WorklistKind.Buy) continue;

                var bought = buy.Lines.Count > 0
                    ? buy.Lines.Select(l => l.TypeId)
                    : [buy.TypeId];

                var freed = bought
                    .SelectMany(t => unowned.GetValueOrDefault(t, []))
                    .DistinctBy(j => j.Key)
                    .ToList();

                if (freed.Count == 0) continue;

                section.Items[n] = buy with
                {
                    Unblocks = freed.Count,
                    Priority = Math.Max(buy.Priority, freed.Max(j => j.Priority)),
                    Detail   = buy.Detail
                             + $" Releases {freed.Count:N0} stopped job(s): "
                             + string.Join(", ", freed.Take(3).Select(j => j.TypeName))
                             + (freed.Count > 3 ? $", and {freed.Count - 3:N0} more." : "."),
                };
            }
        }
    }

    /// <summary>
    /// One row per losing bid, however many reasons there are to care about it.
    ///
    /// <para>An order can be needed by a build plan, by a stocking rule, and be a standing order
    /// the player maintains on its own terms — three sources, one price to change. Each finds it
    /// honestly and independently, which is what keeps them from having to know about each other,
    /// and the reader would get the same sentence three times.</para>
    ///
    /// <para>Matched on the order's type and station rather than on the task key, because that is
    /// what identifies the order; the sources key their tasks differently and rightly so. The most
    /// specific survivor wins: a standing order the player configured explicitly says more about
    /// what to do than a shortfall that merely happens to depend on it, so a source that names the
    /// order outright is kept over one that inferred it from a need.</para>
    /// </summary>
    private static void DropDuplicateOutbid(List<WorklistSection> sections)
    {
        var dupes = sections
            .SelectMany(s => s.Items.Select(i => (Section: s, Item: i)))
            .Where(x => x.Item.Priority == WorklistPriority.Outbid && x.Item.TypeId > 0)
            .GroupBy(x => (x.Item.TypeId, x.Item.LocationId))
            .Where(g => g.Count() > 1);

        foreach (var group in dupes)
        {
            // Anything that is not this service's own inferred row is a source that knows the
            // order first-hand. Falls back to the first when they all are.
            var keep = group.FirstOrDefault(x => x.Item.Source != "outbid");
            if (keep.Item is null) keep = group.First();

            foreach (var x in group)
                if (!ReferenceEquals(x.Item, keep.Item)) x.Section.Items.Remove(x.Item);
        }
    }

    /// <summary>
    /// Folds purchases of the same type at the same station into one task.
    ///
    /// <para>Generators are deliberately ignorant of each other, which is what keeps them simple
    /// — but it means the job materials and an inventory rule can each raise a buy for the same
    /// thing in the same place, and the reader gets two rows for one order. Adding them up is
    /// the only place in this service that reaches across sections, and it belongs here rather
    /// than in either generator: neither can see the other's answer.</para>
    ///
    /// <para>Only items carrying a <see cref="WorklistItem.MergeKey"/> take part, so contract
    /// purchases and order-maintenance tasks are left alone.</para>
    /// </summary>
    private static void MergeDuplicatePurchases(List<WorklistSection> sections)
    {
        var groups = sections
            .SelectMany(s => s.Items.Select(i => (Section: s, Item: i)))
            .Where(x => x.Item.MergeKey is not null)
            .GroupBy(x => x.Item.MergeKey!)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            // The most urgent contributor decides where the combined task lives and how it sorts.
            // Its section is also the one a reader would look in for this purchase.
            var parts = group.OrderByDescending(x => x.Item.Priority).ToList();
            var lead  = parts[0];

            // ⚠️ Demands add up; the stock that fills them does not. Every contributor has already
            // subtracted the same pile from its own demand, so summing their answers credits that
            // pile once per contributor. Measured on Fullerite-C32: a job wanting 540,933 and a
            // rule wanting 500,000 both subtracted the same 125,298 on hand, 12,886 on order and
            // 333,374 recoverable, and the row asked for 97,817 against a real requirement of
            // 569,375 — the whole supply credited twice.
            //
            // So pooled demand less supply counted once, at the largest figure any contributor
            // claimed. Falls back to the old sum when a contributor cannot report its halves,
            // which is right for anything that is genuinely a separate errand.
            var pooled = parts.All(p => p.Item.GrossDemand is not null && p.Item.SupplyCredited is not null);

            var demand = pooled ? parts.Sum(p => p.Item.GrossDemand!.Value)      : 0;
            var supply = pooled ? parts.Max(p => p.Item.SupplyCredited!.Value)   : 0;

            var total = pooled ? Math.Max(0, demand - supply)
                               : parts.Sum(p => p.Item.Quantity);

            // Each contributor's reason is kept verbatim. The point of merging is one errand, not
            // one explanation — "why am I buying this many" is the question the row has to answer.
            var reasons = string.Join("  •  ", parts.Select(p => p.Item.Detail).Where(d => d.Length > 0));

            // Blocked wins: the combined order cannot be placed if any part of it cannot be.
            var blocked = parts.FirstOrDefault(p => p.Item.Readiness == WorklistReadiness.Blocked).Item;

            // Kept through the merge: a blueprint is acquired on contract, and losing that when
            // a job's demand for one folds into a stocking rule's would send the reader to the
            // market window for something that is not on it.
            var tag = parts.Select(p => p.Item.TitleTag).FirstOrDefault(t => t is not null);

            var merged = lead.Item with
            {
                // Keyed off the merge key, so the combined task keeps one identity across
                // refreshes even as the contributing generators come and go. Snoozing it snoozes
                // the purchase, which is the thing the player decided to leave for later.
                Key       = $"merged:{group.Key}",
                // A zero total means every contributor was the "none owned at all" row, which
                // carries no count by design — naming a number there would invent one.
                Title     = (tag, total) switch
                {
                    (null, _) => $"{lead.Item.TypeName} × {total:N0}",
                    (_,    0) => $"{lead.Item.TypeName} — {tag}",
                    _         => $"{lead.Item.TypeName} — {tag} × {total:N0}",
                },
                Quantity  = total,
                // ⚠️ The contributors' own figures do not add up to this, and saying so is the
                // point. Each was computed against the whole of the shared stock, so a reader
                // adding the "short" numbers gets a figure that credits that stock once per
                // demand — which is what this row used to print. The sum is spelled out instead.
                Detail    = pooled
                    ? $"{total:N0} in total — {demand:N0} wanted between them, less {supply:N0} " +
                      $"already on hand, on order or recoverable, counted once. {reasons}"
                    : $"{total:N0} in total. {reasons}",
                Priority  = parts.Max(p => p.Item.Priority),
                Readiness = blocked is not null ? blocked.Readiness : lead.Item.Readiness,
                BlockedBy = blocked?.BlockedBy ?? "",
                DataAsOf  = parts.Min(p => p.Item.DataAsOf),
            };

            foreach (var part in parts) part.Section.Items.Remove(part.Item);
            lead.Section.Items.Add(merged);
        }
    }

    /// <summary>
    /// Works out how much space each task's contents take.
    ///
    /// <para>Done here rather than in the generators so every source gets the figure from one
    /// lookup. Five generators each fetching volumes would be five chances to use a different
    /// packaged size for the same item, and four more queries.</para>
    ///
    /// <para>Packaged volume is the honest figure for material being hauled or bought. An
    /// assembled ship takes far more room than its packaged form, but nothing here is assembled —
    /// these are things being moved to or made at a structure.</para>
    /// </summary>
    private async Task ApplyVolumeAsync(List<WorklistSection> sections, CancellationToken ct)
    {
        var items = sections.SelectMany(s => s.Items).ToList();

        var typeIds = items
            .SelectMany(i => i.Lines.Count > 0
                ? i.Lines.Select(l => l.TypeId)
                : i.Quantity > 0 && i.TypeId > 0 ? [i.TypeId] : Array.Empty<int>())
            .Distinct().ToList();
        if (typeIds.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var volumes = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Volume, ct);

        // Priced here too, off the same one lookup and at whatever the asset valuation is set to
        // use — so what a task is worth and what the hangar it comes from is worth are the same
        // number, and a summary can add them up without a second opinion about prices.
        var prices = new Dictionary<int, double>();
        var market = await db.MarketDefaultSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (market?.AssetValueConfigId is int configId)
            prices = (await db.MarketItemPrices.AsNoTracking()
                    .Where(p => p.ConfigId == configId && typeIds.Contains(p.TypeId))
                    .ToListAsync(ct))
                .ToDictionary(p => p.TypeId, p => market.AssetValuePriceType switch
                {
                    MarketPriceType.Buy  => p.BuyPrice,
                    MarketPriceType.Sell => p.SellPrice,
                    _                    => p.Midpoint,
                });

        // ⚠️ Contracts fill the gaps the market cannot. A blueprint has no market price at all —
        // it is bought and sold on contracts — so a buy task for one valued at nothing, and the
        // ISK column on those rows sat blank while the task itself was worth billions. Applied
        // only where the market had no price rather than in preference to it: the market is the
        // better number when it exists, and mixing the two per type would make the total depend on
        // which source happened to answer.
        var unpriced = typeIds.Where(id => !prices.ContainsKey(id) || prices[id] <= 0).ToList();
        if (unpriced.Count > 0)
            foreach (var cp in await db.ContractPrices.AsNoTracking()
                         .Where(c => unpriced.Contains(c.TypeId)).ToListAsync(ct))
                if (ContractPricing.EffectivePrice(cp) is { } effective && effective > 0)
                    prices[cp.TypeId] = (double)effective;

        // ⚠️ A blueprint is priced as a copy, and this overrides both sources above rather than
        // filling a gap they left. ContractPrices holds the whole-item price, which for a
        // blueprint type is the original — and an Avatar BPO is tens of billions against a copy
        // at a small fraction of that. Nobody with a task to acquire a titan print is buying the
        // original; they are buying a copy, which is what the task's own note already quotes.
        // Valuing the row off the BPO put a number on the list that no part of the plan matched.
        //
        // A type with no BPC contract price keeps whatever it had. That is deliberate: the
        // fallback would be the BPO price, which is the figure being corrected here.
        var bpTypeIds = await KillmailValuation.BlueprintTypeIdsAsync(db, typeIds, ct);
        if (bpTypeIds.Count > 0)
            foreach (var (typeId, perRun) in
                     await KillmailValuation.CheapestBpcPerRunAsync(db, bpTypeIds, ct))
                if (perRun > 0) prices[typeId] = perRun;

        foreach (var section in sections)
        {
            for (var i = 0; i < section.Items.Count; i++)
            {
                var item = section.Items[i];

                var m3 = item.Lines.Count > 0
                    ? item.Lines.Sum(l => volumes.GetValueOrDefault(l.TypeId) * l.Quantity)
                    : item.Quantity > 0 ? volumes.GetValueOrDefault(item.TypeId) * item.Quantity : 0;

                var isk = item.Lines.Count > 0
                    ? item.Lines.Sum(l => prices.GetValueOrDefault(l.TypeId) * l.Quantity)
                    : item.Quantity > 0 ? prices.GetValueOrDefault(item.TypeId) * item.Quantity : 0;

                // Stamped onto each line as well, off the same two lookups. The manifest shows a
                // per-item value and volume, and taking them from anywhere else would let the
                // lines disagree with the total they add up to.
                var lines = item.Lines.Count == 0 ? item.Lines
                    : item.Lines.Select(l => l with
                      {
                          Volume = volumes.GetValueOrDefault(l.TypeId) * l.Quantity,
                          Value  = prices.GetValueOrDefault(l.TypeId)  * l.Quantity,
                      }).ToList();

                if (m3 > 0 || isk > 0)
                    section.Items[i] = item with { Volume = m3, Value = isk, Lines = lines };
            }
        }
    }

    /// <summary>
    /// Stamps first-seen and snooze onto freshly generated items, and records first-seen for
    /// keys never encountered before.
    ///
    /// Rows for keys that no longer generate are left alone rather than swept. They cost a few
    /// bytes, and keeping them means an item that comes back — a standing order that lapses
    /// again next month — is not misreported as brand new, nor its snooze quietly forgotten.
    /// </summary>
    private async Task ApplyStateAsync(List<WorklistSection> sections, CancellationToken ct)
    {
        var keys = sections.SelectMany(s => s.Items).Select(i => i.Key).Distinct().ToList();
        if (keys.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var states = await db.WorklistItemStates.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, ct);

        var now      = DateTimeOffset.UtcNow;
        var newState = new List<WorklistItemState>();
        var seen     = new HashSet<string>();

        for (int si = 0; si < sections.Count; si++)
        {
            var section = sections[si];
            for (int i = 0; i < section.Items.Count; i++)
            {
                var item = section.Items[i];
                if (states.TryGetValue(item.Key, out var st))
                {
                    section.Items[i] = item with
                    {
                        FirstSeenAt  = st.FirstSeenAt,
                        SnoozedUntil = st.SnoozedUntil,
                    };
                }
                else
                {
                    section.Items[i] = item with { FirstSeenAt = now };

                    // ⚠️ Only once per key. The same suggestion can appear in more than one
                    // section — the key is what makes it the same suggestion — and adding a row
                    // per appearance put two rows with one key into a single SaveChanges. That
                    // failed the whole batch on the unique constraint, so EVERY new item lost its
                    // first-seen stamp, not just the repeated one, and the "age" column stayed
                    // empty for all of them.
                    if (seen.Add(item.Key))
                        newState.Add(new WorklistItemState { Key = item.Key, FirstSeenAt = now });
                }
            }
        }

        if (newState.Count == 0) return;

        db.WorklistItemStates.AddRange(newState);
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            // A rebuild running at the same time — the tool and the Overview's sections both do
            // one — can insert the same key between the read above and here. Retry a row at a
            // time so one collision costs one stamp instead of the whole batch. A row that fails
            // now is one somebody else has already written, which is the outcome we wanted.
            db.ChangeTracker.Clear();

            var failed = 0;
            foreach (var row in newState)
            {
                db.WorklistItemStates.Add(row);
                try { await db.SaveChangesAsync(ct); }
                catch { failed++; db.ChangeTracker.Clear(); }
            }

            // Every single one failing is not a race, it is something else.
            if (failed == newState.Count)
                errorLogger.Log("WorklistService", "ApplyState",
                    $"None of {newState.Count} first-seen row(s) could be stored. Items will show " +
                    $"no age until this is resolved.");
        }
    }

    /// <summary>Hides an item until a chosen time. Pass null to un-snooze.</summary>
    public async Task SnoozeAsync(string key, DateTimeOffset? until, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var row = await db.WorklistItemStates.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new WorklistItemState { Key = key, FirstSeenAt = DateTimeOffset.UtcNow };
            db.WorklistItemStates.Add(row);
        }

        row.SnoozedUntil = until;
        await db.SaveChangesAsync(ct);
    }
}
