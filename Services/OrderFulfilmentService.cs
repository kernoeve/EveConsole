using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Works out where each pending order is going to come from, and notices when one has been
/// delivered.
///
/// <para>Three questions per order, in this order, because they answer different things:
/// has a contract already delivered it; can it be filled from stock nobody else has claimed;
/// is there a job running that will produce it. The first is history, the other two are a
/// forecast — so a contract wins outright and ends the order.</para>
///
/// <para>Everything it writes is derived. It never invents an order, and the only user-entered
/// field it touches is the estimated date, and then only for an order whose date it set itself.
/// </para>
/// </summary>
public class OrderFulfilmentService(
    IDbContextFactory<AppDbContext> dbFactory,
    AppErrorLogger errorLogger)
{
    /// <summary>Sources, as stored in TrackedOrder.FulfilmentSource.</summary>
    public const string SourceNone     = "";
    public const string SourceStock    = "stock";
    public const string SourceJob      = "job";
    public const string SourceContract = "contract";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // ── What the background-process view shows ────────────────────────────────
    //
    // A loop nobody can see is indistinguishable from one that is not running — and this one
    // failed silently into the error log for a whole session before anyone noticed the column was
    // empty. These are plain properties, read by the activity window on its own refresh.

    /// <summary>When the last pass finished, and when the next one is due.</summary>
    public DateTimeOffset? LastRunAt { get; private set; }
    public DateTimeOffset? NextRunAt { get; private set; }

    /// <summary>What the last pass found, in words.</summary>
    public string StatusText { get; private set; } = "Not run yet";

    /// <summary>Pending orders, and how many of them have a source worked out.</summary>
    public int PendingCount { get; private set; }
    public int LinkedCount  { get; private set; }

    private Task? _loop;

    /// <summary>
    /// Starts the poll. Five minutes rather than on demand: the inputs are polled ESI data —
    /// assets, industry jobs and contracts — so checking more often than they change would only
    /// re-read the same rows.
    /// </summary>
    public void Start(CancellationToken ct = default)
    {
        if (_loop is not null) return;

        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await RunOnceAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    StatusText = $"Last pass failed: {ex.Message}";
                    errorLogger.Log(nameof(OrderFulfilmentService), "poll", ex);
                }

                LastRunAt = DateTimeOffset.UtcNow;
                NextRunAt = LastRunAt + Interval;

                try { await Task.Delay(Interval, ct); }
                catch (OperationCanceledException) { return; }
            }
        }, ct);
    }

    /// <summary>One pass over the pending orders. Public so the tool can force it after an edit.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var orders = await db.TrackedOrders
            .Where(o => o.Status == "pending")
            .ToListAsync(ct);
        if (orders.Count == 0) return;

        // Ranked the way the tool ranks them, because that is the order stock should be claimed
        // in: a priority order takes from the shelf before an older ordinary one.
        orders = orders
            .OrderByDescending(o => o.IsPriority)
            .ThenBy(o => o.CreatedAt)
            .ToList();

        var typeIds = orders.Select(o => o.TypeId).Distinct().ToList();

        var ours = await OurIdsAsync(db, ct);

        // Stock, counted once for every type in play. Assets are a snapshot of what is sitting in
        // hangars; nothing here reserves anything in game, so the reservation below is purely this
        // app's own bookkeeping across its own orders.
        var stock = await db.EsiAssets
            .Where(a => typeIds.Contains(a.TypeId))
            .GroupBy(a => a.TypeId)
            .Select(g => new { TypeId = g.Key, Units = g.Sum(a => (long)a.Quantity) })
            .ToDictionaryAsync(x => x.TypeId, x => x.Units, ct);

        // Jobs that have not yet delivered. A delivered job's output is already in assets, so
        // counting it here as well would promise the same units twice.
        var openJobs = await db.EsiIndustryJobs
            .Where(j => j.ProductTypeId != null
                     && typeIds.Contains(j.ProductTypeId!.Value)
                     && j.Status != "delivered" && j.Status != "cancelled")
            .ToListAsync(ct);

        // A contract already spoken for cannot deliver a second order.
        var claimedContracts = await db.TrackedOrders
            .Where(o => o.LinkedContractId != null)
            .Select(o => o.LinkedContractId!.Value)
            .ToListAsync(ct);
        var claimed = claimedContracts.ToHashSet();

        // Jobs that have NOT delivered yet, so a contracted order can tell an in-flight job
        // (which cannot be its supply) from a finished one (which may well have been).
        var openJobIds = openJobs.Select(j => j.JobId).ToHashSet();

        // Units a single run of each product yields. A run is not a unit: a run of Nanite Repair
        // Paste makes far more than one, and counting runs against an order for fifty would call
        // a single run enough.
        var perRun = await db.SdeBlueprintProducts
            .Where(p => typeIds.Contains(p.ProductTypeId)
                     && (p.Activity == "manufacturing" || p.Activity == "reaction"))
            .GroupBy(p => p.ProductTypeId)
            .Select(g => new { TypeId = g.Key, Qty = g.Max(x => x.Quantity) })
            .ToDictionaryAsync(x => x.TypeId, x => Math.Max(1, x.Qty), ct);

        var claimedJobs = new HashSet<int>();
        var changed = false;

        foreach (var order in orders)
        {
            ct.ThrowIfCancellationRequested();

            // ── Delivered, or on its way? ──────────────────────────────────────
            // A contract is linked as soon as one is found, but only ACCEPTANCE completes the
            // order: an outstanding contract has been offered and not taken, which is a promise,
            // not a sale. The link is still worth showing while it sits there.
            if (await FindContractAsync(db, order, ours, claimed, ct) is { } hit)
            {
                claimed.Add(hit.ContractId);

                if (order.LinkedContractId != hit.ContractId)
                {
                    order.LinkedContractId = hit.ContractId;
                    changed = true;
                }

                // The contract is the agreed price once there is one — it is what the buyer
                // actually pays, where the order's figure was an intention.
                if (hit.Price > 0 && Math.Abs(order.PurchasePrice - hit.Price) > 0.01)
                {
                    order.PurchasePrice = hit.Price;
                    changed = true;
                }

                if (hit.IsAccepted)
                {
                    order.Status      = "completed";
                    order.CompletedOn = (hit.AcceptedAt ?? DateTimeOffset.UtcNow)
                        .UtcDateTime.ToString("yyyy-MM-dd");
                    changed = true;
                    continue;   // settled; nothing left to forecast
                }

                if (hit.IsDeclined)
                {
                    // Offered exactly what they asked for and turned down. Reserving stock for
                    // them after that holds goods nobody is waiting on.
                    order.Status      = "canceled";
                    order.CompletedOn = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd");
                    changed = true;
                    continue;
                }

                // ⚠️ Offered and not yet taken, and that is as far as this order goes.
                //
                // It used to fall through to stock and jobs, on the reasoning that a pending
                // order still wants a supply. It does not: the goods are already in the
                // contract with the buyer's name on them. Falling through claimed the soonest
                // job producing the type and pinned it to this order — a job that cannot be for
                // it, since what the order needed is sitting in the contract — and took that job
                // away from the order that was actually waiting on it.
                //
                // ⚠️ An incomplete job attached to a contracted order is cleared for the same
                // reason. A COMPLETE one is left alone: it may well be where the contracted
                // goods came from, and erasing that would lose the only record of how the order
                // was filled.
                if (order.LinkedJobId is int linked && openJobIds.Contains(linked))
                {
                    order.FulfilmentSource = SourceContract;
                    order.LinkedJobId      = null;
                    order.LinkedJobIds     = "";
                    order.UnitsInBuild     = 0;
                    changed = true;
                }
                else if (order.FulfilmentSource != SourceContract && order.LinkedJobId is null)
                {
                    order.FulfilmentSource = SourceContract;
                    changed = true;
                }

                continue;
            }

            // ── On the shelf? ──────────────────────────────────────────────────
            // Reserved as we go: an earlier order taking the last unit means the next one is not
            // "from stock", which is the whole point of walking them in rank order.
            //
            // ⚠️ PARTIAL takes are reserved too, and recorded. Nineteen of the fifty an order
            // wants are as spoken for as fifty would be, and leaving them on the shelf let the
            // next order count the same nineteen again. It also gives the order tracker a real
            // number to show: an order for fifty with nineteen on hand reads 19/50 instead of an
            // empty box that looks identical to nothing at all.
            var available = stock.GetValueOrDefault(order.TypeId);
            var take      = (int)Math.Min(available, order.Units);

            if (take > 0) stock[order.TypeId] = available - take;

            if (order.StockOnHand != take) { order.StockOnHand = take; changed = true; }

            if (take >= order.Units)
            {
                changed |= SetJobs(order, SourceStock, [], 0);
                continue;
            }

            // ── Being made? ────────────────────────────────────────────────────
            // Unclaimed jobs producing it, soonest first, taken until the shortfall is covered.
            // A job is claimed by at most one order for the same reason a contract is: two orders
            // pointing at one job would both promise its output.
            //
            // ⚠️ As many as it takes, not one. An order for fifty took the soonest job and stopped,
            // so a run of five looked exactly like a run of fifty and the other jobs really
            // building the order were left unattached and free for another order to claim.
            var shortfall = order.Units - take;
            var yield     = perRun.GetValueOrDefault(order.TypeId, 1);

            var picked  = new List<int>();
            var made    = 0;
            DateTimeOffset? lastEnd = null;

            foreach (var j in openJobs
                         .Where(j => j.ProductTypeId == order.TypeId && !claimedJobs.Contains(j.JobId))
                         .OrderBy(j => j.EndDate))
            {
                if (made >= shortfall) break;

                claimedJobs.Add(j.JobId);
                picked.Add(j.JobId);
                made   += j.Runs * yield;
                lastEnd = j.EndDate;
            }

            if (picked.Count > 0)
            {
                changed |= SetJobs(order, SourceJob, picked, made);

                // ⚠️ The date moves only when the jobs actually cover what is missing. On a
                // single-unit order any job does, which is why this was never noticed; on an
                // order for fifty, one run of five was pinning a delivery date the order had no
                // way of meeting. Short of the shortfall, whatever date is on the order — a
                // human estimate, usually — is better than a confident wrong one.
                //
                // The LAST job's end date, not the first: the order is not filled until the one
                // that finishes latest does.
                if (made >= shortfall && lastEnd is { } end)
                {
                    var estimate = end.UtcDateTime.ToString("yyyy-MM-dd");
                    if (order.EstimatedDate != estimate)
                    {
                        order.EstimatedDate = estimate;
                        changed = true;
                    }
                }
                continue;
            }

            // ── Nothing found ──────────────────────────────────────────────────
            // ⚠️ Clears a previous derived source, but never the estimated date: a date this
            // service set is left standing rather than wiped the moment a job is delivered, and a
            // date the user typed was never ours to remove.
            changed |= SetJobs(order, SourceNone, [], 0);
        }

        if (changed) await db.SaveChangesAsync(ct);

        PendingCount = orders.Count(o => o.Status == "pending");
        LinkedCount  = orders.Count(o => o.Status == "pending" && o.FulfilmentSource.Length > 0);
        StatusText   = PendingCount == 0
            ? "No pending orders"
            : $"{LinkedCount:N0} of {PendingCount:N0} pending order(s) have a source";
    }

    /// <summary>Sets the derived fields, reporting whether anything actually moved.</summary>
    /// <summary>
    /// Records where an order is coming from, and which jobs are building it.
    ///
    /// <para>⚠️ LinkedJobId is written as the head of the list, never independently. It is what
    /// a contracted order clears by id and what rows written before the list existed carry, so
    /// it has to stay truthful — but two fields that can disagree about the same thing is how
    /// the tracker ended up showing a job that no longer had anything to do with the order.</para>
    /// </summary>
    private static bool SetJobs(
        TrackedOrder order, string source, IReadOnlyList<int> jobIds, int unitsInBuild)
    {
        var ids  = string.Join(",", jobIds);
        var head = jobIds.Count > 0 ? jobIds[0] : (int?)null;

        if (order.FulfilmentSource == source
         && order.LinkedJobIds     == ids
         && order.LinkedJobId      == head
         && order.UnitsInBuild     == unitsInBuild) return false;

        order.FulfilmentSource = source;
        order.LinkedJobIds     = ids;
        order.LinkedJobId      = head;
        order.UnitsInBuild     = unitsInBuild;
        return true;
    }

    /// <summary>
    /// The contract that delivered this order, if there is one.
    ///
    /// <para>Deliberately strict, because the consequence is marking an order complete:</para>
    /// <list type="bullet">
    /// <item>issued by one of our characters or personal corporations — not just any contract;</item>
    /// <item>assigned to this order's buyer, by id. ⚠️ An order whose buyer predates the id column
    /// carries a typed name only, and is skipped rather than matched by name: the wrong contract
    /// would silently close somebody else's order;</item>
    /// <item>issued AFTER the order was placed, so a delivery from three months ago cannot be read
    /// as fulfilling something ordered today;</item>
    /// <item>carrying at least the ordered units of the ordered type;</item>
    /// <item>not already linked to another order.</item>
    /// </list>
    ///
    /// <para>Only a finished contract completes an order. An outstanding one has been offered and
    /// not yet accepted, which is not a sale.</para>
    /// <summary>A contract that could settle an order, with what it says.</summary>
    private sealed record ContractHit(int ContractId, string Status, double Price, DateTimeOffset? AcceptedAt)
    {
        /// <summary>Accepted is the only status that means the buyer actually took it.</summary>
        public bool IsAccepted => Status is "finished";

        /// <summary>
        /// The buyer turned it down. That ends the order — they were offered exactly what they
        /// asked for and said no, so continuing to reserve stock for them would hold goods
        /// nobody is waiting on.
        ///
        /// <para>⚠️ Only "rejected", not "cancelled" or "failed". Those two are the ISSUER's
        /// side — a contract withdrawn to re-cut at a different price is not the buyer changing
        /// their mind, and cancelling the order for it would throw away a sale still in progress.
        /// Neither is matched at all, so such an order simply goes back to being forecast from
        /// stock and jobs.</para>
        /// </summary>
        public bool IsDeclined => Status is "rejected";
    }

    private static async Task<ContractHit?> FindContractAsync(
        AppDbContext db, TrackedOrder order, OurIds ours, HashSet<int> claimed, CancellationToken ct)
    {
        if (order.BuyerId <= 0) return null;
        if (ours.Characters.Count == 0 && ours.Corporations.Count == 0) return null;

        // ⚠️ Raw SQL, not a LINQ Where. ContractRecord.DateIssued is a DateTimeOffset and EF Core's
        // SQLite provider cannot translate a comparison on one — it throws at RUNTIME, not at build
        // time, so the first version of this failed into the error log every five minutes while the
        // column simply stayed empty. The retention purges avoid it the same way.
        //
        // Both columns hold EF's own ISO text ("2026-08-18 23:09:15+00:00"), which sorts
        // lexicographically, so a "yyyy-MM-dd HH:mm:ss" cutoff compares correctly against it.
        var placed = order.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");

        // Ids come from our own tables, so they are embedded rather than parameterised — a list of
        // longs cannot carry anything but digits.
        var tests = new List<string>();
        if (ours.Characters.Count > 0)
            tests.Add($"""c."IssuerId" IN ({string.Join(",", ours.Characters)})""");
        if (ours.Corporations.Count > 0)
            tests.Add($"""c."IssuerCorporationId" IN ({string.Join(",", ours.Corporations)})""");

        // Outstanding and in-progress contracts are candidates too: the link is worth showing
        // before acceptance, it just does not complete the order.
        //
        // ⚠️ GROUP BY, not DISTINCT on the row: contracts are polled per owner, so one contract
        // appears once for each of our characters or corporations that can see it, and the copies
        // are identical. Without this a single delivery looks like several candidates.
        var sql = $$"""
            SELECT c."ContractId" AS "Value"
            FROM "EsiContracts" c
            JOIN "EsiContractItems" i ON i."ContractId" = c."ContractId"
            WHERE c."Status" IN ('finished', 'outstanding', 'in_progress', 'rejected')
              AND c."AssigneeId" = {0}
              AND c."DateIssued" > {1}
              AND ({{string.Join(" OR ", tests)}})
              AND i."TypeId" = {2} AND i."IsIncluded" = 1 AND i."Quantity" >= {3}
            GROUP BY c."ContractId"
            ORDER BY c."ContractId"
            """;

        // ⚠️ Scalar ids only. SqlQueryRaw with an unmapped result type is not something to rely on
        // here — the details are read back through EF below, where there is no DateTimeOffset
        // comparison left to translate and the columns arrive properly typed.
        var ids = await db.Database
            .SqlQueryRaw<int>(sql, order.BuyerId, placed, order.TypeId, order.Units)
            .ToListAsync(ct);
        if (ids.Count == 0) return null;

        // An already-linked contract stays this order's, so a re-run cannot hand it to another.
        var chosen = ids.FirstOrDefault(id => id == order.LinkedContractId);
        if (chosen == 0) chosen = ids.FirstOrDefault(id => !claimed.Contains(id));
        if (chosen == 0) return null;

        var c = await db.EsiContracts.AsNoTracking()
            .Where(x => x.ContractId == chosen)
            .Select(x => new { x.Status, x.Price, x.DateAccepted })
            .FirstOrDefaultAsync(ct);
        if (c is null) return null;

        return new ContractHit(chosen, c.Status, (double)c.Price, c.DateAccepted);
    }


    private sealed record OurIds(HashSet<long> Characters, HashSet<long> Corporations);

    /// <summary>
    /// Who counts as "us" for the issuer test: authenticated characters, and corporations marked
    /// personal. A contract from an unrelated corporation the user happens to see is not a sale
    /// they made.
    /// </summary>
    private static async Task<OurIds> OurIdsAsync(AppDbContext db, CancellationToken ct)
    {
        var chars = await db.Characters.Where(c => c.RefreshToken != "")
            .Select(c => c.Id).ToListAsync(ct);
        var corps = await db.Corporations.Where(c => c.IsPersonal)
            .Select(c => (long)c.Id).ToListAsync(ct);
        return new OurIds(chars.ToHashSet(), corps.ToHashSet());
    }
}
