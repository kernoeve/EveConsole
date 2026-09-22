using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.Services;

// Background loops for the parts of contracts that aren't per-token list polls:
//   • Public contract lists across all regions (paged, unauth).
//   • Item lists for any contract we haven't pulled items for yet (character / corp / public).
// Character & corp contract *lists* are still pulled by EsiPollingService.
public class ContractsService : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly EsiClient                       _esi;
    private readonly ApiActivityLog                  _log;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly TimerSettingsService            _timerSettings;

    private CancellationTokenSource? _cts;
    private Task? _publicLoop;
    private Task? _itemsLoop;
    private Task? _pricingLoop;

    // Fired after each contract re-pricing so per-type price history can re-snapshot.
    public event Func<CancellationToken, Task>? AfterPricing;

    // Pace between successive public-list region calls.
    private const int CallDelayMs = 100;

    // Public contract items have no token-bucket limit — pace only to be polite (~6/sec).
    private const int PublicItemDelayMs = 150;

    // Character/corp contract items are limited to 600 requests / 15 minutes (a shared token
    // bucket). 1700 ms ≈ 35/min ≈ 529 per 15 min — comfortably under the cap.
    private const int AuthedItemDelayMs = 1700;

    // After ESI refuses one (400/403/404). A refusal also spends from the error budget every
    // endpoint in the app shares — 100 a minute — so a run of them is walked at a fifth of the
    // pace, 12 a minute at worst, and the other pollers keep their room.
    private const int RefusedItemDelayMs = 5000;

    // Contract types that carry an item list. "loan" has none.
    private static readonly HashSet<string> ItemBearingTypes = ["item_exchange", "auction", "courier"];

    private string _statusText = "Contracts: not started";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _itemsSweeping;
    public bool IsSweepingItems
    {
        get => _itemsSweeping;
        private set => this.RaiseAndSetIfChanged(ref _itemsSweeping, value);
    }

    // How far the running item pass has got, for the status bar: contracts handled so far, and
    // how many the pass set out with. Both zero between passes.
    private volatile int _itemsDone, _itemsTotal;
    public int ItemsDone  => _itemsDone;
    public int ItemsTotal => _itemsTotal;

    // Snapshot for the API-log Contracts monitor. Refused = contracts ESI answered 4xx for and
    // holds no items of; they count among the owned as pulled, since they were asked.
    public record ContractItemsStatus(
        int PublicTotal, int PublicPulled,
        int OwnedTotal,  int OwnedPulled,
        int Refused,     bool Running);

    // Item-bearing type predicate reused by the count queries (must be inlined for EF).
    // item_exchange / auction / courier carry items; loan does not.
    public async Task<ContractItemsStatus> GetItemsStatusAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var pubTotal = await db.EsiContracts.CountAsync(c => c.OwnerType == "public"
            && (c.Type == "item_exchange" || c.Type == "auction" || c.Type == "courier"), ct);
        var pubPulled = await db.EsiContracts.CountAsync(c => c.OwnerType == "public" && c.ItemsPulled
            && (c.Type == "item_exchange" || c.Type == "auction" || c.Type == "courier"), ct);

        // "Owned" = every character and corporation contract — all of them are asked for.
        var ownedTotal = await db.EsiContracts
            .Where(c => (c.Type == "item_exchange" || c.Type == "auction" || c.Type == "courier")
                && c.OwnerType != "public")
            .Select(c => c.ContractId).Distinct().CountAsync(ct);
        var ownedPulled = await db.EsiContracts
            .Where(c => c.ItemsPulled && (c.Type == "item_exchange" || c.Type == "auction" || c.Type == "courier")
                && c.OwnerType != "public")
            .Select(c => c.ContractId).Distinct().CountAsync(ct);

        // Refused = asked, and answered 400/403/404 by every endpoint that had it.
        var refused = await db.EsiContracts
            .Where(c => c.ItemsPulled && c.ItemsStatus >= 400 && c.OwnerType != "public"
                && (c.Type == "item_exchange" || c.Type == "auction" || c.Type == "courier"))
            .Select(c => c.ContractId).Distinct().CountAsync(ct);

        return new ContractItemsStatus(pubTotal, pubPulled, ownedTotal, ownedPulled, refused, IsSweepingItems);
    }

    public ContractsService(
        IDbContextFactory<AppDbContext> dbFactory,
        EsiClient                       esi,
        ApiActivityLog                  log,
        AppErrorLogger                  errorLogger,
        TimerSettingsService            timerSettings)
    {
        _dbFactory     = dbFactory;
        _esi           = esi;
        _log           = log;
        _errorLogger   = errorLogger;
        _timerSettings = timerSettings;
    }

    public void Start()
    {
        if (_publicLoop is not null) return;

        _cts         = new CancellationTokenSource();
        _publicLoop  = Task.Run(() => RunLoopAsync("contract.public",  3600, SweepPublicContractsAsync, _cts.Token));
        _itemsLoop   = Task.Run(() => RunLoopAsync("contract.items",    600, SweepContractItemsAsync,   _cts.Token));
        _pricingLoop = Task.Run(() => RunLoopAsync("contract.pricing", 1800, RecomputePricingAsync,     _cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        foreach (var t in new[] { _publicLoop, _itemsLoop, _pricingLoop })
            if (t is not null) try { await t; } catch (OperationCanceledException) { }

        // ⚠️ Cleared, not merely cancelled. A CancellationTokenSource stays cancelled once it
        // has been, so restarting onto the same one hands all three loops a token that is already
        // dead: each returns on its first await and never runs again. Stop used to be called only
        // on the way out, where that could not matter. The worker lease can be lost and regained,
        // so it has to be an undoable thing now.
        _cts.Dispose();
        _cts         = null;
        _publicLoop  = null;
        _itemsLoop   = null;
        _pricingLoop = null;
    }

    private async Task RunLoopAsync(string timerKey, int defaultSeconds, Func<CancellationToken, Task> sweep, CancellationToken ct)
    {
        // Background for the ESI gate: never holds the slot kept for whatever the user is doing.
        using var _ = EsiClient.Background();

        try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await sweep(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errorLogger.Log("ContractsService", timerKey, ex); }

            int interval = _timerSettings.GetInterval(timerKey, defaultSeconds);
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
                await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Public contract lists (all regions) ─────────────────────────────────────

    public async Task SweepPublicContractsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var regions = await db.SdeRegions.AsNoTracking()
            .Where(r => !r.IsWormhole)
            .Select(r => new { r.RegionId, r.Name })
            .ToListAsync(ct);

        int total = 0;
        for (int i = 0; i < regions.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            while (_esi.IsErrorLimitBlocked && !ct.IsCancellationRequested)
            { try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; } }
            if (ct.IsCancellationRequested) break;

            var region = regions[i];
            using var handle = _log.StartCall(region.Name, "contract.public");
            var r = await _esi.ExecutePublicAllPagesAsync<EsiPublicContract>(
                $"contracts/public/{region.RegionId}/", ct);
            handle.Complete(r.IsSuccess, r.StatusCode, r.Error);

            if (r.IsSuccess && r.Data is not null)
            {
                await UpsertPublicContractsAsync(db, region.RegionId, r.Data, r.Complete, ct);
                total += r.Data.Count;
                StatusText = $"Contracts: public {i + 1}/{regions.Count} regions · {total:N0} listed";
            }

            try { await Task.Delay(CallDelayMs, ct); } catch (OperationCanceledException) { break; }
        }
        StatusText = $"Contracts: public list updated ({total:N0}) — {DateTimeOffset.Now:t}";
    }

    // Upsert a region's public contracts. Existing rows are updated and missing ones inserted;
    // rows are never deleted. The public list only contains CURRENTLY-ACTIVE contracts, so a row
    // we previously stored that isn't in a COMPLETE fresh pull has dropped off (accepted / expired /
    // deleted — we can't tell which) and is marked "closed" with the drop-off time, letting the UI
    // and pricing separate active from historical. A partial pull (complete == false) skips the
    // reconciliation so a dropped page can't mass-close active contracts.
    private static async Task UpsertPublicContractsAsync(
        AppDbContext db, int regionId, List<EsiPublicContract> data, bool complete, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = (await db.EsiContracts
                .Where(c => c.OwnerType == "public" && c.OwnerId == regionId)
                .ToListAsync(ct))
            .ToDictionary(c => c.ContractId);

        var returnedIds = new HashSet<int>(data.Count);
        foreach (var c in data)
        {
            returnedIds.Add(c.ContractId);
            if (existing.TryGetValue(c.ContractId, out var row))
            {
                // Still listed → active. Reactivate if a prior (possibly partial) pull closed it.
                row.Status        = "outstanding";
                row.DateCompleted = null;
                row.DateExpired = c.DateExpired;
                row.Price       = (decimal)c.Price;
                row.Reward      = (decimal)c.Reward;
                row.Collateral  = (decimal)c.Collateral;
                row.Buyout      = (decimal)c.Buyout;
                row.Volume      = (decimal)c.Volume;
            }
            else
            {
                db.EsiContracts.Add(new ContractRecord
                {
                    ContractId          = c.ContractId,
                    OwnerId             = regionId,
                    OwnerType           = "public",
                    RegionId            = regionId,
                    IssuerId            = c.IssuerId,
                    IssuerCorporationId = c.IssuerCorporationId,
                    StartLocationId     = c.StartLocationId,
                    EndLocationId       = c.EndLocationId,
                    Type                = c.Type,
                    Status              = "outstanding",
                    Title               = c.Title,
                    Availability        = "public",
                    DateIssued          = c.DateIssued,
                    DateExpired         = c.DateExpired,
                    DaysToComplete      = c.DaysToComplete,
                    Price               = (decimal)c.Price,
                    Reward              = (decimal)c.Reward,
                    Collateral          = (decimal)c.Collateral,
                    Buyout              = (decimal)c.Buyout,
                    Volume              = (decimal)c.Volume,
                });
            }
        }

        // Reconcile: rows we still hold that the (complete) pull no longer returned have dropped off.
        if (complete)
        {
            foreach (var row in existing.Values)
            {
                if (row.Status == "outstanding" && !returnedIds.Contains(row.ContractId))
                {
                    row.Status        = "closed";
                    row.DateCompleted = now;   // last time it was known active (bounds pricing window)
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Contract items (character / corp / public) ──────────────────────────────

    public async Task SweepContractItemsAsync(CancellationToken ct)
    {
        IsSweepingItems = true;
        try { await SweepContractItemsCoreAsync(ct); }
        finally { IsSweepingItems = false; _itemsDone = 0; _itemsTotal = 0; }
    }

    private async Task SweepContractItemsCoreAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // This sweep reuses one context across many contracts, only ever Add-ing new items or
        // ExecuteUpdate-ing (which bypasses tracking). Change detection is therefore unneeded, and
        // leaving it on made every SaveChanges re-scan an ever-growing tracked graph — an O(n²)
        // cost that also crashed EF's ChangeDetector (fatal CLR access violation) once large. We
        // disable auto-detect and Clear() the tracker after each contract to keep it bounded.
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        // All contracts still needing items (item-bearing types), grouped by ContractId so a
        // contract seen by several owners is fetched once.
        var pending = (await db.EsiContracts.AsNoTracking()
                .Where(c => !c.ItemsPulled)
                .Select(c => new { c.ContractId, c.OwnerId, c.OwnerType, c.Type, c.IssuerCorporationId, c.AssigneeId, c.AcceptorId })
                .ToListAsync(ct))
            .Where(c => ItemBearingTypes.Contains(c.Type))
            .GroupBy(c => c.ContractId)
            // The player's own contracts first — issued, assigned or accepted by a character or
            // corporation of theirs — then the ones merely available to a corporation, which are
            // what other people put up to the alliance, then everyone else's.
            //
            // This list had no order at all, which in practice meant insertion order — and with
            // forty thousand public listings against a few hundred owned ones, a player's own
            // contracts sat scattered through the queue behind a delay apiece. They were never
            // skipped, merely always last, so they read as permanently empty while the public
            // backfill ground on. Public contracts are browsing; a contract you issued or were
            // assigned is one you are waiting on.
            .OrderByDescending(g => g.Max(c => c.OwnerType switch
            {
                "character"   => 2,
                "corporation" => (long)c.IssuerCorporationId == c.OwnerId
                                 || c.AssigneeId == c.OwnerId || c.AcceptorId == c.OwnerId ? 2 : 1,
                _             => 0,
            }))
            .ThenBy(g => g.Key)
            .ToList();

        // Which corporation row to ask through when a contract sits under several: the corporation
        // that issued it, else the one it was assigned to or accepted by, else whichever holds it.
        //
        // ⚠️ Every corporation row is asked, whoever issued the contract. From July to September
        // 2026 the sweep deferred — never called for — any corporation contract issued by another
        // corporation, on the belief that the corporation endpoint answered 404 for those. The
        // database said otherwise: 78 such contracts, from corporations no token here belongs to
        // and listed nowhere public, had their items, which only that endpoint could have served,
        // while some 460 sat unasked for two months and read as empty. What distinguishes the
        // ones it does refuse is not established; a refusal is recorded on the row and costs one
        // call, so asking is the cheaper mistake.
        static int CorpRank(long ownerId, int issuerCorp, long? assignee, long? acceptor)
            => issuerCorp == ownerId ? 2 : assignee == ownerId || acceptor == ownerId ? 1 : 0;

        int done = 0, refused = 0, skipped = 0;
        _itemsTotal = pending.Count;
        _itemsDone  = 0;
        foreach (var group in pending)
        {
            if (ct.IsCancellationRequested) break;
            _itemsDone = done + refused + skipped;

            // Prefer the public endpoint (no token bucket, items always visible), then a
            // character token, then a corporation's.
            var corp = group.Where(c => c.OwnerType == "corporation")
                .OrderByDescending(c => CorpRank(c.OwnerId, c.IssuerCorporationId, c.AssigneeId, c.AcceptorId))
                .FirstOrDefault();
            var src = group.FirstOrDefault(c => c.OwnerType == "public")
                   ?? group.FirstOrDefault(c => c.OwnerType == "character")
                   ?? corp;
            if (src is null) { skipped++; continue; }   // cannot happen: every group has a row

            // The PUBLIC items endpoint serves item_exchange / auction only — couriers return
            // HTTP 400. There's no way to read a public courier's cargo, and left unmarked they
            // were re-tried every sweep, firing a burst of hundreds of 400s that trips ESI's
            // global error limit. Prefer an owned source (the authed endpoint does return courier
            // cargo); if the only source is public, mark done without calling.
            if (src.OwnerType == "public" && src.Type == "courier")
            {
                var owned = group.FirstOrDefault(c => c.OwnerType == "character") ?? corp;
                if (owned is null)
                {
                    await db.EsiContracts.Where(x => x.ContractId == group.Key && !x.ItemsPulled)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.ItemsPulled, true), ct);
                    skipped++;
                    continue;
                }
                src = owned;
            }

            while (_esi.IsErrorLimitBlocked && !ct.IsCancellationRequested)
            { try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; } }
            if (ct.IsCancellationRequested) break;

            bool isPublic = src.OwnerType == "public";
            var (handled, status) = await FetchAndStoreItemsAsync(db, group.Key, src.OwnerId, src.OwnerType, ct);
            if (handled)
            {
                // The answer goes on every owner row: the items are the contract's, not the row's.
                await db.EsiContracts.Where(x => x.ContractId == group.Key && !x.ItemsPulled)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ItemsPulled, true)
                                              .SetProperty(x => x.ItemsStatus, status), ct);
                if (status >= 400) refused++; else done++;
            }

            if (((done + refused) & 63) == 0)
                StatusText = $"Contracts: {done:N0} pulled, {refused:N0} refused…";

            // Authed items share a 600/15min token bucket; public items don't. A refusal spends
            // from the error budget as well, so it is followed by the longer pause.
            var pause = status >= 400 ? RefusedItemDelayMs : isPublic ? PublicItemDelayMs : AuthedItemDelayMs;
            try { await Task.Delay(pause, ct); }
            catch (OperationCanceledException) { break; }
        }
        StatusText = $"Contracts: item pass done ({done:N0} pulled, {refused:N0} refused, "
                   + $"{skipped:N0} skipped) — {DateTimeOffset.Now:t}";
    }

    // Fetches a contract's items via the right endpoint and stores them (dedup by RecordId).
    // Returns false only on a hard call failure so the contract is retried next sweep.
    /// <summary>What one attempt to pull a contract's items came to.</summary>
    public sealed record ItemPullOutcome(bool Stored, int Count, string Message);

    /// <summary>
    /// Pulls one contract's items now, on request, from every owner row that holds it in the
    /// order the sweep prefers — public listing, a character, the corporations — and says
    /// exactly what each endpoint answered. The sweep asks once and records the answer; this is
    /// for asking again, when the answer was a refusal or the sweep has not reached it yet.
    /// </summary>
    public async Task<ItemPullOutcome> PullItemsNowAsync(int contractId, CancellationToken ct = default)
    {
        if (_esi.IsErrorLimitBlocked) return new(false, 0, "ESI is paused right now; try again shortly.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var rows = await db.EsiContracts.AsNoTracking().Where(c => c.ContractId == contractId).ToListAsync(ct);
        if (rows.Count == 0) return new(false, 0, "The contract is not stored.");
        var held = await db.EsiContractItems.CountAsync(i => i.ContractId == contractId, ct);
        if (held > 0) return new(true, held, $"{held:N0} item(s) already held.");

        var sources = rows.Where(c => c.OwnerType == "public" && c.Type != "courier")
            .Concat(rows.Where(c => c.OwnerType == "character"))
            .Concat(rows.Where(c => c.OwnerType == "corporation"))
            .ToList();
        if (sources.Count == 0) return new(false, 0, "No endpoint can serve this contract's items: a public courier's cargo is not listed.");

        var answers = new List<string>();
        foreach (var src in sources)
        {
            var r = await FetchAndStoreItemsCoreAsync(db, contractId, src.OwnerId, src.OwnerType, ct);
            if (r.Stored is int n)
            {
                await db.EsiContracts.Where(x => x.ContractId == contractId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ItemsPulled, true)
                                              .SetProperty(x => x.ItemsStatus, r.Status), ct);
                return new(true, n, n == 0 ? $"ESI lists no items for it (via the {src.OwnerType} endpoint)."
                                           : $"{n:N0} item(s) pulled via the {src.OwnerType} endpoint.");
            }
            answers.Add($"{src.OwnerType} endpoint: HTTP {r.Status}{(string.IsNullOrEmpty(r.Error) ? "" : $" — {r.Error}")}");
        }
        return new(false, 0, "Not served: " + string.Join("; ", answers));
    }

    /// <summary>Whether the contract is settled — items stored, or a refusal worth recording —
    /// and the status that settled it, 0 when no call was needed.</summary>
    private async Task<(bool Handled, int Status)> FetchAndStoreItemsAsync(
        AppDbContext db, int contractId, long ownerId, string ownerType, CancellationToken ct)
    {
        // Already have items from another owner row — nothing to fetch, no answer to record.
        if (await db.EsiContractItems.AnyAsync(i => i.ContractId == contractId, ct))
            return (true, 0);

        var r = await FetchAndStoreItemsCoreAsync(db, contractId, ownerId, ownerType, ct);
        if (r.Stored is not null) return (true, r.Status);

        // 400 (wrong contract type for this endpoint), 403/404 (gone / no access) are terminal:
        // mark handled so we stop retrying and don't keep feeding ESI's global error limit.
        // Anything else is transient — log and retry next sweep.
        if (r.Status is 400 or 403 or 404) return (true, r.Status);
        _errorLogger.Log("ContractsService",
            $"items contract={contractId} owner={ownerType}", $"HTTP {r.Status}: {r.Error}");
        return (false, r.Status);
    }

    /// <summary>One call to one endpoint and the store that follows: how many were stored, or
    /// the status and words ESI answered with. A save that failed reads as status 0 and is
    /// retried by the next sweep.</summary>
    private async Task<(int? Stored, int Status, string? Error)> FetchAndStoreItemsCoreAsync(
        AppDbContext db, int contractId, long ownerId, string ownerType, CancellationToken ct)
    {
        // NOTE: individual item calls are intentionally NOT logged to the API activity log —
        // there can be thousands, which would flood it. Genuine failures go to the error log.
        List<ContractItem>? items = null;
        int status = 0;
        string? error = null;

        if (ownerType == "public")
        {
            var r = await _esi.ExecutePublicAllPagesAsync<EsiPublicContractItem>(
                $"contracts/public/items/{contractId}/", ct);
            status = r.StatusCode; error = r.Error;
            if (r.IsSuccess && r.Data is not null)
                items = r.Data.Select(i => new ContractItem
                {
                    ContractId = contractId, RecordId = i.RecordId, TypeId = i.TypeId,
                    Quantity = i.Quantity, IsIncluded = i.IsIncluded, IsSingleton = false,
                    IsBlueprintCopy = i.IsBlueprintCopy, MaterialEfficiency = i.MaterialEfficiency,
                    TimeEfficiency = i.TimeEfficiency, Runs = i.Runs,
                }).ToList();
        }
        else
        {
            var path = ownerType == "corporation"
                ? $"corporations/{ownerId}/contracts/{contractId}/items/"
                : $"characters/{ownerId}/contracts/{contractId}/items/";
            var r = ownerType == "corporation"
                ? await _esi.ExecuteCorpAllPagesAsync<EsiContractItem>(ownerId, path, ct)
                : await _esi.ExecuteAllPagesAsync<EsiContractItem>(ownerId, path, ct);
            status = r.StatusCode; error = r.Error;
            if (r.IsSuccess && r.Data is not null)
                items = r.Data.Select(i => new ContractItem
                {
                    ContractId = contractId, RecordId = i.RecordId, TypeId = i.TypeId,
                    Quantity = i.Quantity, IsIncluded = i.IsIncluded, IsSingleton = i.IsSingleton,
                    RawQuantity = i.RawQuantity,
                }).ToList();
        }

        if (items is null) return (null, status, error);

        // Dedupe by RecordId. ESI has been observed returning the same record twice inside
        // one contract's paged item list, and EF rejects the pair on the (ContractId,
        // RecordId) key before anything reaches the database. The method comment above has
        // always claimed this happened; it did not, which is the whole bug.
        var deduped = items.Count > 1
            ? items.GroupBy(i => i.RecordId).Select(g => g.First()).ToList()
            : items;

        try
        {
            if (deduped.Count > 0) db.EsiContractItems.AddRange(deduped);
            await db.SaveChangesAsync(ct);
            return (deduped.Count, status, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Retried next sweep rather than marked pulled — but not at the cost of the
            // rest of this one.
            _errorLogger.Log("ContractsService", $"items contract={contractId} owner={ownerType}", ex);
            return (null, 0, ex.Message);
        }
        finally
        {
            // Must clear on failure too, not just success. A rejected SaveChanges leaves the
            // offending entities in the tracker, so every later contract in the same sweep
            // throws the same conflict — which is how one bad contract took out whole passes.
            db.ChangeTracker.Clear();
        }
    }

    // ── Contract pricing (single-item-type sells) ───────────────────────────────

    // Rebuilds the ContractPrices table. A qualifying "sell" is an item_exchange contract that
    // offers exactly ONE item type for an ISK price and requests nothing back. The per-unit price
    // is the contract price divided by the total number of units of that type. For each such type
    // we record the current best (lowest) per-unit price among active contracts and the 30-day
    // average of the daily-best per-unit price (reconstructed from each contract's issued→ended
    // window). The table is fully replaced each run.
    public async Task RecomputePricingAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Pure delete + bulk insert of a fresh set — no change detection needed (and a large
        // DetectChanges pass is the failure mode we're avoiding elsewhere in this service).
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        // Per-contract item aggregate. MinType==MaxType ⇒ a single distinct type; Requested==0 ⇒
        // nothing is asked for in return (a pure item-for-ISK sell); Qty = total units offered.
        var itemAgg = await db.EsiContractItems
            .GroupBy(i => i.ContractId)
            .Select(g => new
            {
                ContractId = g.Key,
                MinType    = g.Min(x => x.TypeId),
                MaxType    = g.Max(x => x.TypeId),
                Requested  = g.Sum(x => x.IsIncluded ? 0 : 1),
                Qty        = g.Sum(x => x.IsIncluded ? x.Quantity : 0L),
            })
            .Where(a => a.MinType == a.MaxType && a.Requested == 0 && a.Qty > 0)
            .ToDictionaryAsync(a => a.ContractId, ct);

        // Sell contracts: item_exchange with an ISK price and no reward. (Dates are only projected,
        // never compared in SQL — EF Core + SQLite can't translate DateTimeOffset comparisons.)
        var contractRows = (await db.EsiContracts.AsNoTracking()
                .Where(c => c.Type == "item_exchange" && c.Price > 0m && c.Reward == 0m && c.ItemsPulled)
                .Select(c => new
                {
                    c.ContractId, c.Price, c.Status,
                    c.DateIssued, c.DateExpired, c.DateAccepted, c.DateCompleted,
                })
                .ToListAsync(ct))
            .GroupBy(c => c.ContractId)          // one contract can appear under several owners
            .Select(g => g.First())
            .ToList();

        var now      = DateTimeOffset.UtcNow;
        var todayUtc = now.UtcDateTime.Date;

        // TypeId → per-contract (per-unit price, active window, whether currently active).
        var byType = new Dictionary<int, List<(decimal PerUnit, DateTime Start, DateTime End, bool ActiveNow)>>();
        // (BlueprintTypeId, ME) → per-contract PER-RUN price for BPC sales.
        var byBpc  = new Dictionary<(int TypeId, int Me), List<(decimal PerUnit, DateTime Start, DateTime End, bool ActiveNow)>>();

        // BPC items on offer (only PUBLIC contracts carry runs/ME — personal/corp items don't).
        var bpcByContract = (await db.EsiContractItems
                .Where(i => i.IsIncluded && i.IsBlueprintCopy == true && i.Runs != null && i.Runs > 0)
                .Select(i => new { i.ContractId, i.TypeId, i.Quantity, Runs = i.Runs!.Value, Me = i.MaterialEfficiency ?? 0 })
                .ToListAsync(ct))
            .GroupBy(i => i.ContractId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var c in contractRows)
        {
            if (!itemAgg.TryGetValue(c.ContractId, out var agg) || agg.Qty <= 0) continue;

            var start = c.DateIssued.UtcDateTime;
            var end   = (c.DateAccepted ?? c.DateCompleted ?? c.DateExpired ?? now).UtcDateTime;
            bool activeNow = c.Status == "outstanding" && (c.DateExpired is null || c.DateExpired > now);

            // BPC sale: single-type contract whose items are all BPCs of that type with a uniform ME.
            // Price per RUN = ask ÷ total runs offered, keyed by (type, ME). Not also priced as a
            // finished item.
            if (bpcByContract.TryGetValue(c.ContractId, out var bpcs) && bpcs.All(b => b.TypeId == agg.MinType))
            {
                var mes = bpcs.Select(b => b.Me).Distinct().ToList();
                long totalRuns = bpcs.Sum(b => (long)b.Runs * Math.Max(1L, b.Quantity));
                if (mes.Count == 1 && totalRuns > 0)
                {
                    var bkey = (agg.MinType, mes[0]);
                    if (!byBpc.TryGetValue(bkey, out var blist)) byBpc[bkey] = blist = new();
                    blist.Add((c.Price / totalRuns, start, end, activeNow));
                }
                continue;
            }

            decimal perUnit = c.Price / agg.Qty;
            if (!byType.TryGetValue(agg.MinType, out var list))
                byType[agg.MinType] = list = new();
            list.Add((perUnit, start, end, activeNow));
        }

        // Current best = lowest price among contracts active now; 30-day average = mean over the
        // last 30 days of each day's lowest price among contracts whose active window overlapped it.
        (decimal? Best, int ActiveCount, decimal? Avg30, int SampleDays,
         decimal? Last, DateTime? LastEnd) Summarize(
            List<(decimal PerUnit, DateTime Start, DateTime End, bool ActiveNow)> list)
        {
            decimal? best = null; int activeCount = 0;
            foreach (var e in list)
                if (e.ActiveNow) { activeCount++; if (best is null || e.PerUnit < best) best = e.PerUnit; }

            decimal daySum = 0m; int sampleDays = 0;
            for (int k = 0; k < 30; k++)
            {
                var dayStart = todayUtc.AddDays(-k);
                var dayEnd   = dayStart.AddDays(1);
                decimal? dayBest = null;
                foreach (var e in list)
                    if (e.Start < dayEnd && e.End >= dayStart && (dayBest is null || e.PerUnit < dayBest))
                        dayBest = e.PerUnit;
                if (dayBest is not null) { daySum += dayBest.Value; sampleDays++; }
            }
            // The most recent observation at whatever age, for callers that would rather have an
            // old price than none. A tie on the end date takes the cheaper, matching the
            // lowest-wins rule above, so a fallback is comparable with what it replaces instead
            // of being whichever contract happened to be enumerated last.
            decimal? last = null; DateTime? lastEnd = null;
            foreach (var e in list)
                if (lastEnd is null || e.End > lastEnd || (e.End == lastEnd && e.PerUnit < last))
                { lastEnd = e.End; last = e.PerUnit; }

            return (best, activeCount, sampleDays > 0 ? daySum / sampleDays : null, sampleDays,
                    last, lastEnd);
        }
        static decimal? Round2(decimal? v) => v is { } x ? Math.Round(x, 2, MidpointRounding.AwayFromZero) : null;

        var results = new List<ContractPrice>(byType.Count);
        foreach (var (typeId, list) in byType)
        {
            // Deliberately no fallback here. These are ordinary items, which trade on the market
            // as well as on contract, so an absent contract price is covered by a market price
            // rather than being a hole; carrying old asks forward would quietly widen the
            // contract channel's influence over prices it was never asked to set.
            var (best, activeCount, avg30, sampleDays, _, _) = Summarize(list);
            if (best is null && avg30 is null) continue;
            results.Add(new ContractPrice
            {
                TypeId = typeId, BestPrice = Round2(best), Avg30Best = Round2(avg30),
                ActiveCount = activeCount, SampleDays = sampleDays, UpdatedAt = now,
            });
        }

        var bpcResults = new List<ContractBpcPrice>(byBpc.Count);
        foreach (var ((typeId, me), list) in byBpc)
        {
            // ⚠️ Kept even when nothing is current, unlike the item rows above. A blueprint copy
            // has no market price to fall back on, so dropping the row does not mean "priced
            // elsewhere", it means "free" — and this table is rebuilt from scratch every run,
            // so a row dropped once is gone for good.
            var (best, activeCount, avg30, sampleDays, last, lastEnd) = Summarize(list);
            if (best is null && avg30 is null && last is null) continue;
            bpcResults.Add(new ContractBpcPrice
            {
                TypeId = typeId, Me = me, BestPerRun = Round2(best), Avg30PerRun = Round2(avg30),
                LastPerRun = Round2(last),
                LastSeenAt = lastEnd is { } le ? new DateTimeOffset(le, TimeSpan.Zero) : null,
                ActiveCount = activeCount, SampleDays = sampleDays, UpdatedAt = now,
            });
        }

        // Full replace — both are small per-type summaries.
        await db.ContractPrices.ExecuteDeleteAsync(ct);
        if (results.Count > 0) db.ContractPrices.AddRange(results);
        await db.ContractBpcPrices.ExecuteDeleteAsync(ct);
        if (bpcResults.Count > 0) db.ContractBpcPrices.AddRange(bpcResults);
        await db.SaveChangesAsync(ct);

        var staleBpcs = bpcResults.Count(b => b.BestPerRun is null && b.Avg30PerRun is null);
        StatusText = $"Contracts: priced {results.Count:N0} types, {bpcResults.Count:N0} BPCs"
                   + (staleBpcs > 0 ? $" ({staleBpcs:N0} from older contracts)" : "")
                   + $" — {DateTimeOffset.Now:t}";

        if (AfterPricing is not null && !ct.IsCancellationRequested)
        {
            try { await AfterPricing(ct); }
            catch (Exception ex) { _errorLogger.Log("ContractsService", "AfterPricing", ex); }
        }
    }
}
