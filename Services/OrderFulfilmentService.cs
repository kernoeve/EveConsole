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

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Released by <see cref="Nudge"/> to run the next pass at once. One slot, so a burst
    /// of nudges is one early pass, not a queue of them.</summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

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
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Run after every pass, on the same client: what lets a store-order alarm be evaluated the
    /// moment an order's state has been worked out rather than at its own next interval. Set
    /// at startup.
    /// </summary>
    public Func<CancellationToken, Task>? AfterPass { get; set; }

    /// <summary>
    /// Starts the poll. Every thirty seconds, and at once when a poll has just brought in what a
    /// pass reads — assets, industry jobs, contracts — see <see cref="Nudge"/>. A pass that finds
    /// nothing changed is a few small queries, which is a fair price for a status that follows
    /// the action by seconds rather than minutes.
    /// </summary>
    public void Start(CancellationToken outerCt = default)
    {
        if (_loop is not null) return;

        // ⚠️ Linked to a source of our own. Every caller leaves the parameter at its default,
        // so until now nothing could stop this loop once started — and the lease has to be
        // able to, the moment this client stops being the worker.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var ct = _cts.Token;

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

                // The interval, or sooner when nudged.
                try { await _wake.WaitAsync(Interval, ct); }
                catch (OperationCanceledException) { return; }
            }
        }, ct);
    }

    /// <summary>
    /// Runs the next pass now rather than at the interval. Called when a poll has just brought in
    /// what a pass reads, so a contract or a job shows against its order seconds after ESI
    /// reported it. Harmless when the loop is not running.
    /// </summary>
    public void Nudge()
    {
        if (_wake.CurrentCount > 0) return;
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Stops the pending-order poll, and leaves it startable again.
    ///
    /// <para>⚠️ Both fields cleared. _loop is what Start guards on, and a
    /// CancellationTokenSource stays cancelled once it has been — keeping either would make
    /// the next Start a silent no-op for the rest of the session.</para>
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        if (_loop is not null)
            try { await _loop; } catch (OperationCanceledException) { }

        _cts.Dispose();
        _cts  = null;
        _loop = null;
    }

    /// <summary>One pass over the pending orders. Public so the tool can force it after an edit.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var changed = false;
        try { changed = await PassAsync(ct); }
        finally
        {
            if (AfterPass is { } after)
            {
                try { await after(ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { errorLogger.Log(nameof(OrderFulfilmentService), "after pass", ex); }
            }
            if (changed && PassChanged is { } listeners)
            {
                try { listeners(); }
                catch (Exception ex) { errorLogger.Log(nameof(OrderFulfilmentService), "pass changed", ex); }
            }
        }
    }

    /// <summary>Raised after a pass that wrote something — a source linked, a date set, an order
    /// completed — and not after the many that find nothing new. What the Order Tracker reloads
    /// on, so a change shows the moment it is made rather than at the grid's next refresh.</summary>
    public event Action? PassChanged;

    /// <summary>True when the pass changed an order.</summary>
    private async Task<bool> PassAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var orders = await db.TrackedOrders
            .Where(o => o.Status == "pending")
            .ToListAsync(ct);
        if (orders.Count == 0) return false;

        // Ranked the way the tool ranks them, because that is the order stock should be claimed
        // in: a priority order takes from the shelf before an older ordinary one.
        orders = orders
            .OrderByDescending(o => o.IsPriority)
            .ThenBy(o => o.CreatedAt)
            .ToList();

        var typeIds = orders.Select(o => o.TypeId).Distinct().ToList();

        var ours = await OurIdsAsync(db, ct);

        // ⚠️ Only what is OURS counts as supply: an authenticated character's own hangar, or a
        // corporation marked personal. A corporation token lets the app SEE an alliance corp's
        // assets and jobs — that is what it is for — and both of these queries used to count
        // them. An order for a capital read "in stock" off five hulls in an alliance
        // corporation's hangar the user could not sell from, while the two actually being built
        // for it sat unattached. The same test the contract match already applies, for the same
        // reason.
        var stock = await db.EsiAssets
            .Where(a => typeIds.Contains(a.TypeId))
            .Where(a => (a.OwnerType == "character"   && ours.Characters.Contains(a.OwnerId))
                     || (a.OwnerType == "corporation" && ours.Corporations.Contains(a.OwnerId)))
            .GroupBy(a => a.TypeId)
            .Select(g => new { TypeId = g.Key, Units = g.Sum(a => (long)a.Quantity) })
            .ToDictionaryAsync(x => x.TypeId, x => x.Units, ct);

        // Jobs that have not yet delivered. A delivered job's output is already in assets, so
        // counting it here as well would promise the same units twice.
        var openJobs = await db.EsiIndustryJobs
            .Where(j => j.ProductTypeId != null
                     && typeIds.Contains(j.ProductTypeId!.Value)
                     && j.Status != "delivered" && j.Status != "cancelled")
            .Where(j => (j.OwnerType == "character"   && ours.Characters.Contains(j.OwnerId))
                     || (j.OwnerType == "corporation" && ours.Corporations.Contains(j.OwnerId)))
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

        // ⚠️ Units left on each job, not a set of jobs claimed. A job was claimed whole by the
        // first order to reach it, so a two-run Phoenix job covered one order for one hull and
        // the second order for one hull found nothing — while the job was making both. An
        // order now takes only the units it is short from a job and leaves the rest for the
        // next; the job is spent when its output is, which is what "claimed" was meant to say.
        var jobUnitsLeft = openJobs.ToDictionary(
            j => j.JobId,
            j => (long)j.Runs * perRun.GetValueOrDefault(j.ProductTypeId!.Value, 1));

        var now = DateTimeOffset.UtcNow;

        // Which contract each order is on, settled for every order before any of them takes
        // stock or a job — see ContractsAsync.
        var (onContract, changed) = await ContractsAsync(db, orders, ours, claimed, now, ct);

        foreach (var order in orders)
        {
            ct.ThrowIfCancellationRequested();

            // ── Delivered, or on its way? ──────────────────────────────────────
            // A contract is linked as soon as one is found, but only ACCEPTANCE completes the
            // order: an outstanding contract has been offered and not taken, which is a promise,
            // not a sale. The link is still worth showing while it sits there.
            if (onContract.TryGetValue(order.Id, out var contract))
            {
                // The contract is the agreed price once there is one — it is what the buyer
                // actually pays, where the order's figure was an intention.
                if (contract.Price > 0 && Math.Abs(order.PurchasePrice - contract.Price) > 0.01)
                {
                    order.PurchasePrice = contract.Price;
                    changed = true;
                }

                // ⚠️ Offered, taken or turned down, this order holds no supply from here on.
                //
                // It used to fall through to stock and jobs, on the reasoning that a pending
                // order still wants a supply. It does not: the goods are already in the
                // contract with the buyer's name on them. Falling through claimed the soonest
                // job producing the type and pinned it to this order — a job that cannot be for
                // it, since what the order needed is sitting in the contract — and took that job
                // away from the order that was actually waiting on it.
                changed |= ReleaseSupply(order, openJobIds);

                if (contract.IsAccepted)
                {
                    order.Status      = "completed";
                    order.CompletedOn = (contract.SettledAt ?? now).UtcDateTime.ToString("yyyy-MM-dd");
                    changed = true;
                }
                else if (contract.IsDeclined)
                {
                    // Offered exactly what they asked for and turned down. Reserving stock for
                    // them after that holds goods nobody is waiting on.
                    order.Status      = "canceled";
                    order.CompletedOn = (contract.SettledAt ?? now).UtcDateTime.ToString("yyyy-MM-dd");
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
            // Jobs producing it with output still unspoken for, soonest first, each drawn on for
            // what this order is short and no more — a job with output left over serves the next
            // order too. What cannot happen is two orders being promised the same unit, which is
            // what the per-job units above guarantee.
            //
            // ⚠️ As many as it takes, not one. An order for fifty took the soonest job and stopped,
            // so a run of five looked exactly like a run of fifty and the other jobs really
            // building the order were left unattached and free for another order to claim.
            var shortfall = order.Units - take;

            var picked  = new List<int>();
            var made    = 0;
            DateTimeOffset? lastEnd = null;

            foreach (var j in openJobs
                         .Where(j => j.ProductTypeId == order.TypeId && jobUnitsLeft[j.JobId] > 0)
                         .OrderBy(j => j.EndDate))
            {
                if (made >= shortfall) break;

                var taken = (int)Math.Min(jobUnitsLeft[j.JobId], shortfall - made);
                jobUnitsLeft[j.JobId] -= taken;
                picked.Add(j.JobId);
                made   += taken;
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
        return changed;
    }

    /// <summary>
    /// Records where an order is coming from, and which jobs are building it, reporting whether
    /// anything actually moved.
    ///
    /// <para>⚠️ LinkedJobId is written as the head of the list, never independently. It is what
    /// rows written before the list existed carry, so it has to stay truthful — but two fields
    /// that can disagree about the same thing is how the tracker ended up showing a job that no
    /// longer had anything to do with the order.</para>
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
    /// Clears what an order on a contract was holding: nothing on the shelf is reserved for it
    /// and nothing is in build for it, so the pass hands both to the orders behind it. Reports
    /// whether anything moved.
    ///
    /// <para>⚠️ Units in build are cleared outright. Left over from the forecast made before
    /// the contract, they went on reading "2 in build" beside a contract carrying both hulls —
    /// saying the order still waited on jobs that were by then building for the orders queued
    /// behind it.</para>
    ///
    /// <para>⚠️ Running jobs are let go; finished ones are kept. A running job cannot be where
    /// the contracted goods came from — they are already made — whereas a FINISHED one may well
    /// be, and erasing it would lose the only record of how the order was filled. The source
    /// stays as it was while one is kept.</para>
    /// </summary>
    private static bool ReleaseSupply(TrackedOrder order, HashSet<int> openJobIds)
    {
        var jobs = order.LinkedJobIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();
        // A row written before the list existed carries only the one.
        if (jobs.Count == 0 && order.LinkedJobId is int only) jobs.Add(only);

        var finished = jobs.Where(id => !openJobIds.Contains(id)).ToList();
        var source   = finished.Count > 0 ? order.FulfilmentSource : SourceContract;

        var moved = SetJobs(order, source, finished, 0);
        if (order.StockOnHand != 0)
        {
            order.StockOnHand = 0;
            moved = true;
        }
        return moved;
    }

    /// <summary>
    /// The contract each pending order is on, by order id, and whether a link moved.
    ///
    /// <para>A linked contract — attached by hand, or matched by an earlier pass — is the
    /// order's, and is read for what it now says. ⚠️ It used to count only if it also passed the
    /// match below, so a contract attached by hand precisely because the match could not see it
    /// was never read at all: the order sat pending beside a contract that had been accepted,
    /// still holding the hulls in build that the orders behind it were waiting on.</para>
    ///
    /// <para>A link is let go only when its contract has lapsed — withdrawn, deleted, failed, or
    /// left outstanding past its expiry — and the order is then matched afresh like any other.
    /// A linked contract no poll has brought in stays linked: it was typed in by hand and is
    /// taken at its word, but only a contract that can be read settles the order.</para>
    ///
    /// <para>The rest are matched to contracts nobody has claimed. ⚠️ Exact fits first, across
    /// every order, and only then contracts with units to spare — otherwise an order for one
    /// hull that happens to rank first takes the two-hull contract cut for an order for two,
    /// which is then left with nothing to match.</para>
    /// </summary>
    private static async Task<(Dictionary<int, ContractState> OnContract, bool Changed)> ContractsAsync(
        AppDbContext db, List<TrackedOrder> orders, OurIds ours, HashSet<int> claimed,
        DateTimeOffset now, CancellationToken ct)
    {
        var onContract = new Dictionary<int, ContractState>();
        var changed    = false;

        var linkedIds = orders.Where(o => o.LinkedContractId is not null)
                              .Select(o => o.LinkedContractId!.Value).Distinct().ToList();
        var linked = await StatesAsync(db, linkedIds, ct);

        foreach (var order in orders)
        {
            if (order.LinkedContractId is not int id) continue;

            if (!linked.TryGetValue(id, out var state))
                onContract[order.Id] = ContractState.Unseen(id);
            else if (!state.HasLapsed(now))
                onContract[order.Id] = state;
            else
            {
                order.LinkedContractId = null;
                changed = true;
            }
        }

        // What could be each unlinked order's: contracts sent to its recipient that offer the
        // ordered type.
        var found = new Dictionary<int, List<int>>();
        foreach (var order in orders)
            if (order.LinkedContractId is null
                && await CandidateIdsAsync(db, order, ours, ct) is { Count: > 0 } ids)
                found[order.Id] = ids;
        if (found.Count == 0) return (onContract, changed);

        var candidateIds = found.Values.SelectMany(ids => ids).Distinct().ToList();
        var typeIds      = orders.Select(o => o.TypeId).Distinct().ToList();
        var states       = await StatesAsync(db, candidateIds, ct);

        // ⚠️ Summed over the contract's lines, never read off one. A hull with rigs fitted is
        // listed on a line of its own, so a contract for two Phoenixes, one of them assembled and
        // rigged, is two lines of one — and the match used to want a single line of two, so it
        // never linked. What else is offered alongside, the rigs included, does not count
        // against a contract, and neither does whether a hull is packaged.
        var carried = (await db.EsiContractItems.AsNoTracking()
                .Where(i => candidateIds.Contains(i.ContractId) && typeIds.Contains(i.TypeId) && i.IsIncluded)
                .GroupBy(i => new { i.ContractId, i.TypeId })
                .Select(g => new { g.Key.ContractId, g.Key.TypeId, Units = g.Sum(i => i.Quantity) })
                .ToListAsync(ct))
            .ToDictionary(x => (x.ContractId, x.TypeId), x => x.Units);

        // Each order's candidates, the closest fit first and then the oldest: the whole order,
        // for a contract still in play.
        var candidates = new Dictionary<int, List<Candidate>>();
        foreach (var order in orders)
        {
            if (!found.TryGetValue(order.Id, out var ids)) continue;
            candidates[order.Id] = ids
                .Where(id => states.TryGetValue(id, out var s) && !s.HasLapsed(now))
                .Select(id => new Candidate(states[id], carried.GetValueOrDefault((id, order.TypeId))))
                .Where(c => c.Units >= order.Units)
                .OrderBy(c => c.Units - order.Units)
                .ThenBy(c => c.Contract.ContractId)
                .ToList();
        }

        foreach (var exact in new[] { true, false })
            foreach (var order in orders)
            {
                if (onContract.ContainsKey(order.Id)
                    || !candidates.TryGetValue(order.Id, out var list)
                    || list.FirstOrDefault(c => !claimed.Contains(c.Contract.ContractId)
                                             && (!exact || c.Units == order.Units)) is not { } pick)
                    continue;

                claimed.Add(pick.Contract.ContractId);
                onContract[order.Id]   = pick.Contract;
                order.LinkedContractId = pick.Contract.ContractId;
                changed = true;
            }

        return (onContract, changed);
    }

    /// <summary>
    /// Contracts that could have delivered this order, by id.
    ///
    /// <para>Deliberately strict, because the consequence is marking an order complete:</para>
    /// <list type="bullet">
    /// <item>issued by one of our characters or personal corporations — not just any contract;</item>
    /// <item>assigned, by id, to this order's buyer — or to whoever the order says the contract
    /// is to be made out to, when it names someone (an alt that will fly the thing, their
    /// corporation). ⚠️ An order whose buyer predates the id column carries a typed name only,
    /// and is skipped rather than matched by name: the wrong contract would silently close
    /// somebody else's order;</item>
    /// <item>issued AFTER the order was placed, so a delivery from three months ago cannot be read
    /// as fulfilling something ordered today;</item>
    /// <item>offering the ordered type. Whether it offers enough of it is for the caller, which
    /// counts every line.</item>
    /// </list>
    ///
    /// <para>Only a finished contract completes an order. An outstanding one has been offered and
    /// not yet accepted, which is not a sale.</para>
    /// </summary>
    private static async Task<List<int>> CandidateIdsAsync(
        AppDbContext db, TrackedOrder order, OurIds ours, CancellationToken ct)
    {
        // The order said who the contract goes to, or it goes to the buyer; a contract to either
        // is this order's. The recipient can be a corporation — ESI puts a corporation's id in
        // assignee_id just the same. ⚠️ This field was on the order for a release before anything
        // here read it, and a titan contracted to the buyer's alt sat unmatched beside its order.
        var recipients = new List<long>();
        if (order.BuyerId      > 0) recipients.Add(order.BuyerId);
        if (order.ContractToId > 0 && !recipients.Contains(order.ContractToId)) recipients.Add(order.ContractToId);
        if (recipients.Count == 0) return [];
        if (ours.Characters.Count == 0 && ours.Corporations.Count == 0) return [];

        // ⚠️ Raw SQL, not a LINQ Where. ContractRecord.DateIssued is a DateTimeOffset and EF Core's
        // SQLite provider cannot translate a comparison on one — it throws at RUNTIME, not at build
        // time, so the first version of this failed into the error log every five minutes while the
        // column simply stayed empty. The retention purges avoid it the same way.
        //
        // Both columns hold EF's own ISO text ("2026-08-18 23:09:15+00:00"), which sorts
        // lexicographically, so a "yyyy-MM-dd HH:mm:ss" cutoff compares correctly against it.
        // ⚠️ A DateTimeOffset, not a string. DateIssued is a timestamptz on a server and
        // "operator does not exist: timestamp with time zone > text" is what comparing it to one
        // gets. On SQLite the provider renders it to the same text this used to build by hand.
        var placed = order.CreatedAt.ToUniversalTime();

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
        // ⚠️ The items are an EXISTS, not a join, and the ids DISTINCT. Contracts are polled per
        // owner, so one contract is a row for each of our characters or corporations that can
        // see it, while its items are stored once: a join repeats every item for every owner
        // row, and anything counted over it counts one hull as two.
        var sql = $$"""
            SELECT DISTINCT c."ContractId" AS "Value"
            FROM "EsiContracts" c
            WHERE c."Status" IN ('finished', 'outstanding', 'in_progress', 'rejected')
              AND c."AssigneeId" IN ({{string.Join(",", recipients)}})
              AND c."DateIssued" > {0}
              AND ({{string.Join(" OR ", tests)}})
              AND EXISTS (SELECT 1 FROM "EsiContractItems" i
                          WHERE i."ContractId" = c."ContractId"
                            AND i."TypeId" = {1} AND i."IsIncluded" = TRUE)
            """;

        // ⚠️ Scalar ids only. SqlQueryRaw with an unmapped result type is not something to rely on
        // here — what each contract says is read back through EF, where there is no DateTimeOffset
        // comparison left to translate and the columns arrive properly typed.
        return await db.Database
            .SqlQueryRaw<int>(sql, placed, order.TypeId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// What each of these contracts now says, by id. One no poll has brought in is absent.
    ///
    /// <para>⚠️ A contract is a row for each owner that can see it, each only as fresh as that
    /// owner's last poll: a corporation's copy can still read outstanding after a character's
    /// has seen it accepted. The most settled row speaks for the contract — a contract that has
    /// been decided cannot become undecided — where whichever row came back first could read a
    /// delivered order as still waiting.</para>
    /// </summary>
    private static async Task<Dictionary<int, ContractState>> StatesAsync(
        AppDbContext db, List<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();

        var rows = await db.EsiContracts.AsNoTracking()
            .Where(c => ids.Contains(c.ContractId))
            .Select(c => new { c.ContractId, c.Status, c.Price, c.DateAccepted, c.DateCompleted, c.DateExpired })
            .ToListAsync(ct);

        return rows.GroupBy(r => r.ContractId).ToDictionary(g => g.Key, g =>
        {
            var said = g.OrderBy(r => Settledness(r.Status)).First();
            return new ContractState(g.Key, said.Status, (double)said.Price,
                g.Max(r => r.DateAccepted) ?? g.Max(r => r.DateCompleted),
                g.Max(r => r.DateExpired));
        });
    }

    /// <summary>How far along a contract's status is, the most settled lowest.</summary>
    private static int Settledness(string status) => status switch
    {
        "finished"                                         => 0,
        "rejected"                                         => 1,
        "cancelled" or "deleted" or "failed" or "reversed" => 2,
        "outstanding"                                      => 4,
        _                                                  => 3,   // in progress, or a courier half done
    };

    /// <summary>How long past its expiry an outstanding contract is still given; see
    /// <see cref="ContractState.HasLapsed"/>.</summary>
    private static readonly TimeSpan ExpiryGrace = TimeSpan.FromHours(1);

    /// <summary>A contract as it now stands, as far as the order it carries is concerned.</summary>
    private sealed record ContractState(
        int ContractId, string Status, double Price, DateTimeOffset? SettledAt, DateTimeOffset? ExpiresAt)
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
        /// They lapse instead: the order lets go of the contract and goes back to being matched,
        /// and forecast from stock and jobs.</para>
        /// </summary>
        public bool IsDeclined => Status is "rejected";

        /// <summary>
        /// Gone without being taken: withdrawn, deleted, failed or reversed — or left outstanding
        /// past its expiry, which is how ESI goes on reporting an expired contract.
        ///
        /// <para>⚠️ An hour's grace on the expiry. Contracts are polled, not pushed, so one
        /// accepted in its last minutes reads as outstanding and expired until the next poll
        /// brings the acceptance in, and letting go of it then would unlink an order that was in
        /// fact delivered.</para>
        /// </summary>
        public bool HasLapsed(DateTimeOffset now) =>
            Status is "cancelled" or "deleted" or "failed" or "reversed"
            || (Status is "outstanding" && ExpiresAt is { } expires && expires < now - ExpiryGrace);

        /// <summary>
        /// A contract linked by hand that no poll has brought in — one no character here can see,
        /// or one not polled yet. There is nothing to read, so it neither settles nor lapses.
        /// </summary>
        public static ContractState Unseen(int contractId) => new(contractId, "", 0, null, null);
    }

    /// <summary>A contract that could be an order's, and how many of the ordered type it carries.</summary>
    private sealed record Candidate(ContractState Contract, long Units);

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
