using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Works out where each pending order is going to come from, and notices when one has been
/// delivered.
///
/// <para>Three questions per order, in this order, because they answer different things:
/// how much of it contracts already carry; can the rest be filled from stock nobody else has
/// claimed; is there a job running that will produce it. The first is history, the other two
/// are a forecast — so contracts win outright for the units they carry, and the order ends
/// when accepted contracts carry all of it.</para>
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

        // What each order's contracts carry, worked out for every order before any of them takes
        // stock or a job — see ContractsAsync.
        var (onContract, changed) = await ContractsAsync(db, orders, ours, now, ct);

        foreach (var order in orders)
        {
            ct.ThrowIfCancellationRequested();

            // ── Delivered, or on its way? ──────────────────────────────────────
            // A contract is linked as soon as one is found, but only ACCEPTANCE delivers: an
            // outstanding contract has been offered and not taken, which is a promise, not a
            // sale. The link is still worth showing while it sits there.
            var contracts = onContract.GetValueOrDefault(order.Id) ?? Contracted.None;

            // The contracts are the agreed price once they carry the whole order — what the
            // buyer actually pays, where the order's figure was an intention.
            if (contracts.Price is double price && Math.Abs(order.PurchasePrice - price) > 0.01)
            {
                order.PurchasePrice = price;
                changed = true;
            }

            // ⚠️ Every unit, not the first contract. An order for two delivered as two contracts
            // of one is half delivered when the first is accepted — completing it then would drop
            // the second hull from everything that plans supply.
            if (contracts.Delivered >= Math.Max(1, order.Units))
            {
                changed |= ReleaseSupply(order, openJobIds);
                order.Status      = "completed";
                order.CompletedOn = (contracts.LastAccepted ?? now).UtcDateTime.ToString("yyyy-MM-dd");
                changed = true;
                continue;
            }

            if (contracts.Declined)
            {
                // Offered exactly what they asked for and turned down. Reserving stock for them
                // after that holds goods nobody is waiting on.
                changed |= ReleaseSupply(order, openJobIds);
                order.Status      = "canceled";
                order.CompletedOn = (contracts.DeclinedAt ?? now).UtcDateTime.ToString("yyyy-MM-dd");
                changed = true;
                continue;
            }

            // ⚠️ Only what no contract carries is looked for on the shelf and in build. An order
            // on a contract used to fall through to stock and jobs and pin the soonest job to
            // itself — a job that could not be for it, the goods being in the contract with the
            // buyer's name on them — taking it from the order that was waiting on it. An order a
            // contract covers only in part still wants the rest, and nothing more.
            var need = order.Units - contracts.Delivered - contracts.Offered;
            if (need <= 0)
            {
                changed |= ReleaseSupply(order, openJobIds);
                continue;
            }

            // A contract waiting on the buyer is what they are told about first, whatever the
            // rest is coming from: it is the one state where the next move is theirs.
            var waiting = contracts.Offered > 0;

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
            var take      = (int)Math.Min(available, need);

            if (take > 0) stock[order.TypeId] = available - take;

            if (order.StockOnHand != take) { order.StockOnHand = take; changed = true; }

            if (take >= need)
            {
                changed |= SetJobs(order, waiting ? SourceContract : SourceStock, [], 0);
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
            var shortfall = need - take;

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
                changed |= SetJobs(order, waiting ? SourceContract : SourceJob, picked, made);

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
            changed |= SetJobs(order, waiting ? SourceContract : SourceNone, [], 0);
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
    /// What each pending order's contracts come to, by order id, and whether anything about the
    /// links moved. Written onto the orders as it goes: <see cref="TrackedOrder.LinkedContracts"/>,
    /// UnitsDelivered, UnitsContracted, and the one contract to name in LinkedContractId.
    ///
    /// <para>A linked contract — attached by hand, or matched by an earlier pass — stays the
    /// order's, and is read for what it now says. ⚠️ It used to count only if it also passed the
    /// match below, so a contract attached by hand precisely because the match could not see it
    /// was never read at all: the order sat pending beside a contract that had been accepted. A
    /// link is let go only when its contract has lapsed — withdrawn, deleted, failed, or left
    /// outstanding past its expiry — and the order is then matched afresh.</para>
    ///
    /// <para>⚠️ Counted in units, not contracts. An order for two often goes out as two
    /// contracts of one, and one contract of two can carry two orders for one; with a link per
    /// order, the first contract for one hull either completed the order for two outright or was
    /// never linked at all. Each contract gives an order what it has left of the ordered type, up
    /// to what the order still needs; the order completes once accepted contracts carry all of
    /// it, and whatever no contract carries is still looked for on the shelf and in build.</para>
    ///
    /// <para>The rest are matched to contracts with units left. ⚠️ Exact fits first, across every
    /// order, and only then the oldest contracts, in rank order — otherwise an order for one hull
    /// that happens to rank first takes half of the contract cut for an order for two.</para>
    /// </summary>
    private static async Task<(Dictionary<int, Contracted> ByOrder, bool Changed)> ContractsAsync(
        AppDbContext db, List<TrackedOrder> orders, OurIds ours, DateTimeOffset now, CancellationToken ct)
    {
        var links   = orders.ToDictionary(o => o.Id, OrderContractLinks.Of);
        var typeIds = orders.Select(o => o.TypeId).Distinct().ToList();

        var linkedIds = links.Values.SelectMany(l => l).Select(l => l.ContractId).Distinct().ToList();
        var states    = await StatesAsync(db, linkedIds, ct);
        var offered   = await OfferedAsync(db, linkedIds, typeIds, ct);

        bool IsDeclined(int contractId) => states.TryGetValue(contractId, out var s) && s.IsDeclined;

        // What an order still wants from a contract. A link not yet counted is taken to carry
        // the rest, as a link always did before contracts were counted.
        int Need(TrackedOrder o)
        {
            var need = o.Units;
            foreach (var l in links[o.Id])
            {
                if (IsDeclined(l.ContractId)) continue;
                if (l.Units is not int units) return 0;
                need -= units;
            }
            return Math.Max(0, need);
        }

        // Lapsed contracts are let go.
        foreach (var order in orders)
            links[order.Id] = links[order.Id]
                .Where(l => !(states.TryGetValue(l.ContractId, out var s) && s.HasLapsed(now)))
                .ToList();

        // A contract attached by hand is counted once the pass can see what it holds: what it
        // has left of the ordered type — after what other pending orders already hold of it — up
        // to what the order still needs. ⚠️ Taken at the user's word when it offers none of the
        // type — a substitute, or an item list ESI would not give — and then it carries whatever
        // the order still needs, as a link always did.
        var heldByPending = new Dictionary<(int Contract, int Type), long>();
        foreach (var order in orders)
            foreach (var l in links[order.Id])
                if (l.Units is int units)
                    heldByPending[(l.ContractId, order.TypeId)] = heldByPending.GetValueOrDefault((l.ContractId, order.TypeId)) + units;

        foreach (var order in orders)
        {
            var list = links[order.Id];
            var need = order.Units - list.Where(l => l.Units is int && !IsDeclined(l.ContractId)).Sum(l => l.Units!.Value);
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Units is not null
                    || !states.TryGetValue(list[i].ContractId, out var s) || !s.ItemsKnown) continue;

                var key   = (list[i].ContractId, order.TypeId);
                var has   = offered.GetValueOrDefault(key);
                var units = (int)(has > 0
                    ? Math.Min(Math.Max(0, has - heldByPending.GetValueOrDefault(key)), Math.Max(0, need))
                    : Math.Max(0, need));
                list[i] = list[i] with { Units = units };
                heldByPending[key] = heldByPending.GetValueOrDefault(key) + units;
                if (!s.IsDeclined) need -= units;
            }
        }

        // ⚠️ What every order already holds, the settled ones included: a unit a contract has
        // given is not there to give again, and a contract shared with another order does not
        // price this one. Settled orders of the same recipients are the ones that can share one.
        var recipients = orders.SelectMany(o => new[] { o.BuyerId, o.ContractToId }).Where(id => id > 0).Distinct().ToList();
        var settled = recipients.Count == 0 ? [] : await db.TrackedOrders.AsNoTracking()
            .Where(o => o.Status != "pending" && o.LinkedContractId != null
                     && (recipients.Contains(o.BuyerId) || recipients.Contains(o.ContractToId)))
            .ToListAsync(ct);
        var settledLinks = settled.Select(o => (o.TypeId, Links: OrderContractLinks.Of(o))).ToList();

        // ── Matching what is still wanted ─────────────────────────────────────
        var found = new Dictionary<int, List<int>>();
        foreach (var order in orders)
            if (Need(order) > 0 && await CandidateIdsAsync(db, order, ours, ct) is { Count: > 0 } ids)
                found[order.Id] = ids;

        if (found.Count > 0)
        {
            var fresh = found.Values.SelectMany(ids => ids).Distinct().Where(id => !states.ContainsKey(id)).ToList();
            foreach (var (id, s) in await StatesAsync(db, fresh, ct)) states[id] = s;
            foreach (var (key, units) in await OfferedAsync(db, fresh, typeIds, ct)) offered[key] = units;

            // A link counted in units holds those units; one never counted — from a version that
            // linked whole contracts — holds all of it, as it did when it was made.
            var given = new Dictionary<(int Contract, int Type), long>();
            var whole = new HashSet<int>();
            void Hold(int typeId, IEnumerable<ContractLink> held)
            {
                foreach (var l in held)
                    if (l.Units is int units) given[(l.ContractId, typeId)] = given.GetValueOrDefault((l.ContractId, typeId)) + units;
                    else whole.Add(l.ContractId);
            }

            foreach (var (typeId, held) in settledLinks) Hold(typeId, held);
            foreach (var o in orders) Hold(o.TypeId, links[o.Id]);

            long Left(int contractId, int typeId) =>
                whole.Contains(contractId) ? 0
                    : offered.GetValueOrDefault((contractId, typeId)) - given.GetValueOrDefault((contractId, typeId));

            void Give(TrackedOrder o, int contractId, int units)
            {
                links[o.Id].Add(new ContractLink(contractId, units));
                given[(contractId, o.TypeId)] = given.GetValueOrDefault((contractId, o.TypeId)) + units;
            }

            // Each order's candidates, oldest first: contracts still in play, not already its own.
            var candidates = orders.Where(o => found.ContainsKey(o.Id)).ToDictionary(o => o.Id, o => found[o.Id]
                .Where(c => states.TryGetValue(c, out var s) && !s.HasLapsed(now)
                         && links[o.Id].All(l => l.ContractId != c))
                .OrderBy(c => c)
                .ToList());

            // Exact fits first, for every order: a contract cut for exactly what an order still
            // wants is that order's before it is anybody's change.
            foreach (var order in orders)
            {
                var need = Need(order);
                if (need <= 0 || !candidates.TryGetValue(order.Id, out var ids)) continue;
                var exact = ids.FirstOrDefault(c => !IsDeclined(c) && Left(c, order.TypeId) == need);
                if (exact != 0) Give(order, exact, need);
            }

            // Then the oldest contracts, in rank order, each giving what it has left.
            foreach (var order in orders)
            {
                if (!candidates.TryGetValue(order.Id, out var ids)) continue;
                foreach (var contractId in ids.Where(c => !IsDeclined(c)))
                {
                    var need = Need(order);
                    if (need <= 0) break;
                    var left = Left(contractId, order.TypeId);
                    if (left > 0 && links[order.Id].All(l => l.ContractId != contractId))
                        Give(order, contractId, (int)Math.Min(left, need));
                }
            }

            // ⚠️ A declined contract is matched only when it was for the whole order and nothing
            // else has been — the buyer turning the order down. One part of the order declined
            // is one delivery that did not happen, and the order still wants those units.
            foreach (var order in orders)
            {
                if (links[order.Id].Count > 0 || !candidates.TryGetValue(order.Id, out var ids)) continue;
                var turnedDown = ids.FirstOrDefault(c => IsDeclined(c) && Left(c, order.TypeId) >= order.Units);
                if (turnedDown != 0) Give(order, turnedDown, order.Units);
            }
        }

        // ── What it comes to ──────────────────────────────────────────────────
        var holders = links.Values.Concat(settledLinks.Select(s => s.Links)).SelectMany(held => held)
            .GroupBy(l => l.ContractId).ToDictionary(g => g.Key, g => g.Count());
        var changed = false;
        var byOrder = new Dictionary<int, Contracted>();

        foreach (var order in orders)
        {
            var list = links[order.Id];
            int delivered = 0, waiting = 0, declined = 0;
            DateTimeOffset? lastAccepted = null, declinedAt = null;

            // Counted links first; one not yet counted carries whatever is left after them.
            foreach (var l in list.OrderBy(x => x.Units is null))
            {
                states.TryGetValue(l.ContractId, out var s);
                var units = l.Units ?? Math.Max(0, order.Units - delivered - waiting);
                if (s is { IsAccepted: true })
                {
                    delivered   += units;
                    lastAccepted = Later(lastAccepted, s.SettledAt);
                }
                else if (s is { IsDeclined: true })
                {
                    declined  += units;
                    declinedAt = Later(declinedAt, s.SettledAt);
                }
                else waiting += units;
            }
            delivered = Math.Min(delivered, order.Units);
            waiting   = Math.Min(waiting, order.Units - delivered);

            // The one to name: the oldest the buyer still has to accept, or else the latest.
            int? head = list.Count == 0 ? null
                : list.FirstOrDefault(l => !(states.TryGetValue(l.ContractId, out var s) && (s.IsAccepted || s.IsDeclined)))
                      is { ContractId: > 0 } open ? open.ContractId : list[^1].ContractId;

            // The price the contracts put on it, once they carry the whole order and carry
            // nothing for anyone else. A declined one was never paid.
            double? price = null;
            var paying = list.Where(l => !IsDeclined(l.ContractId)).ToList();
            if (paying.Count > 0 && delivered + waiting >= order.Units
                && paying.All(l => holders[l.ContractId] == 1 && states.TryGetValue(l.ContractId, out var s) && s.Price > 0))
                price = paying.Sum(l => states[l.ContractId].Price);

            var formatted = OrderContractLinks.Format(list);
            if (order.LinkedContracts  != formatted) { order.LinkedContracts  = formatted; changed = true; }
            if (order.UnitsDelivered   != delivered) { order.UnitsDelivered   = delivered; changed = true; }
            if (order.UnitsContracted  != waiting)   { order.UnitsContracted  = waiting;   changed = true; }
            if (order.LinkedContractId != head)      { order.LinkedContractId = head;      changed = true; }

            if (list.Count > 0)
                byOrder[order.Id] = new Contracted(delivered, waiting,
                    Declined: delivered == 0 && waiting == 0 && declined >= order.Units,
                    lastAccepted, declinedAt, price);
        }

        return (byOrder, changed);

        static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
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
            .Select(c => new { c.ContractId, c.Status, c.Price, c.DateAccepted, c.DateCompleted, c.DateExpired, c.ItemsPulled })
            .ToListAsync(ct);

        return rows.GroupBy(r => r.ContractId).ToDictionary(g => g.Key, g =>
        {
            var said = g.OrderBy(r => Settledness(r.Status)).First();
            return new ContractState(g.Key, said.Status, (double)said.Price,
                g.Max(r => r.DateAccepted) ?? g.Max(r => r.DateCompleted),
                g.Max(r => r.DateExpired),
                g.Any(r => r.ItemsPulled));
        });
    }

    /// <summary>How far along a contract's status is, the most settled lowest.</summary>
    internal static int Settledness(string status) => status switch
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
        int ContractId, string Status, double Price, DateTimeOffset? SettledAt, DateTimeOffset? ExpiresAt,
        bool ItemsKnown)
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
    }

    /// <summary>What an order's contracts come to.</summary>
    /// <param name="Delivered">Units on contracts the buyer has accepted.</param>
    /// <param name="Offered">Units on contracts still waiting for the buyer.</param>
    /// <param name="Declined">Every unit offered and turned down: the buyer declined the order.</param>
    /// <param name="Price">What the contracts charge together, once they carry the whole order and
    /// nothing for anyone else; otherwise null, and the order keeps its own price.</param>
    private sealed record Contracted(int Delivered, int Offered, bool Declined,
        DateTimeOffset? LastAccepted, DateTimeOffset? DeclinedAt, double? Price)
    {
        public static readonly Contracted None = new(0, 0, false, null, null, null);
    }

    /// <summary>
    /// How many of each ordered type each contract offers.
    ///
    /// <para>⚠️ Summed over the contract's lines, never read off one. A hull with rigs fitted is
    /// listed on a line of its own, so a contract for two Phoenixes, one of them assembled and
    /// rigged, is two lines of one — and the match used to want a single line of two, so it
    /// never linked. What else is offered alongside, the rigs included, does not count against
    /// a contract, and neither does whether a hull is packaged.</para>
    /// </summary>
    private static async Task<Dictionary<(int Contract, int Type), long>> OfferedAsync(
        AppDbContext db, List<int> contractIds, List<int> typeIds, CancellationToken ct)
    {
        if (contractIds.Count == 0) return new();

        return (await db.EsiContractItems.AsNoTracking()
                .Where(i => contractIds.Contains(i.ContractId) && typeIds.Contains(i.TypeId) && i.IsIncluded)
                .GroupBy(i => new { i.ContractId, i.TypeId })
                .Select(g => new { g.Key.ContractId, g.Key.TypeId, Units = g.Sum(i => i.Quantity) })
                .ToListAsync(ct))
            .ToDictionary(x => (x.ContractId, x.TypeId), x => x.Units);
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
