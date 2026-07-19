using EveCortex.Api;
using EveCortex.Data;
using EveCortex.Models;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.Services;

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

    private readonly CancellationTokenSource _cts = new();
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

    // Snapshot for the API-log Contracts monitor.
    public record ContractItemsStatus(
        int PublicTotal, int PublicPulled,
        int OwnedTotal,  int OwnedPulled,
        int Deferred,    bool Running);

    // Item-bearing type predicate reused by the count queries (must be inlined for EF).
    // item_exchange / auction / courier carry items; loan does not.
    public async Task<ContractItemsStatus> GetItemsStatusAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var pubTotal = await db.EsiContracts.CountAsync(c => c.OwnerType == "public"
            && (c.Type == "item_exchange" || c.Type == "auction" || c.Type == "courier"), ct);
        var pubPulled = await db.EsiContracts.CountAsync(c => c.OwnerType == "public" && c.ItemsPulled
            && (c.Type == "item_exchange" || c.Type == "auction" || c.Type == "courier"), ct);

        // "Owned" = character contracts, or corp contracts the corp issued — the ones we pull.
        var ownedTotal = await db.EsiContracts
            .Where(c => (c.Type == "item_exchange" || c.Type == "auction" || c.Type == "courier")
                && (c.OwnerType == "character"
                    || (c.OwnerType == "corporation" && (long)c.IssuerCorporationId == c.OwnerId)))
            .Select(c => c.ContractId).Distinct().CountAsync(ct);
        var ownedPulled = await db.EsiContracts
            .Where(c => c.ItemsPulled && (c.Type == "item_exchange" || c.Type == "auction" || c.Type == "courier")
                && (c.OwnerType == "character"
                    || (c.OwnerType == "corporation" && (long)c.IssuerCorporationId == c.OwnerId)))
            .Select(c => c.ContractId).Distinct().CountAsync(ct);

        // Deferred = corp contracts issued by another corp (alliance-assigned / direct) that we
        // are intentionally not pulling items for right now.
        var deferred = await db.EsiContracts
            .Where(c => c.OwnerType == "corporation" && (long)c.IssuerCorporationId != c.OwnerId
                && (c.Type == "item_exchange" || c.Type == "auction" || c.Type == "courier"))
            .Select(c => c.ContractId).Distinct().CountAsync(ct);

        return new ContractItemsStatus(pubTotal, pubPulled, ownedTotal, ownedPulled, deferred, IsSweepingItems);
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
        _publicLoop  = Task.Run(() => RunLoopAsync("contract.public",  3600, SweepPublicContractsAsync, _cts.Token));
        _itemsLoop   = Task.Run(() => RunLoopAsync("contract.items",    600, SweepContractItemsAsync,   _cts.Token));
        _pricingLoop = Task.Run(() => RunLoopAsync("contract.pricing", 1800, RecomputePricingAsync,     _cts.Token));
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        foreach (var t in new[] { _publicLoop, _itemsLoop, _pricingLoop })
            if (t is not null) try { await t; } catch (OperationCanceledException) { }
    }

    private async Task RunLoopAsync(string timerKey, int defaultSeconds, Func<CancellationToken, Task> sweep, CancellationToken ct)
    {
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
        finally { IsSweepingItems = false; }
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
                .Select(c => new { c.ContractId, c.OwnerId, c.OwnerType, c.Type, c.IssuerCorporationId })
                .ToListAsync(ct))
            .Where(c => ItemBearingTypes.Contains(c.Type))
            .GroupBy(c => c.ContractId)
            .ToList();

        int done = 0, deferred = 0, skipped = 0;
        foreach (var group in pending)
        {
            if (ct.IsCancellationRequested) break;

            // Prefer the public endpoint (no token bucket, items always visible), then a
            // character token, then a corp contract the corp ISSUED.
            // Corp contracts issued by ANOTHER corp (alliance-assigned, or direct from another
            // corp) are observed to 404 on the corp items endpoint. Per user direction we DEFER
            // those — no call — to avoid inflating the error count. Why they 404 is not yet
            // established; revisit later. See memory: contract-items-alliance-corp-404.
            var src = group.FirstOrDefault(c => c.OwnerType == "public")
                   ?? group.FirstOrDefault(c => c.OwnerType == "character")
                   ?? group.FirstOrDefault(c => c.OwnerType == "corporation" &&
                        (long)c.IssuerCorporationId == c.OwnerId);

            if (src is null) { deferred++; continue; }

            // The PUBLIC items endpoint serves item_exchange / auction only — couriers return
            // HTTP 400. There's no way to read a public courier's cargo, and left unmarked they
            // were re-tried every sweep, firing a burst of hundreds of 400s that trips ESI's
            // global error limit. Prefer an owned source (the authed endpoint does return courier
            // cargo); if the only source is public, mark done without calling.
            if (src.OwnerType == "public" && src.Type == "courier")
            {
                var owned = group.FirstOrDefault(c => c.OwnerType == "character")
                         ?? group.FirstOrDefault(c => c.OwnerType == "corporation" &&
                              (long)c.IssuerCorporationId == c.OwnerId);
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
            if (await FetchAndStoreItemsAsync(db, group.Key, src.OwnerId, src.OwnerType, ct))
            {
                await db.EsiContracts.Where(x => x.ContractId == group.Key && !x.ItemsPulled)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ItemsPulled, true), ct);
                done++;
            }

            if ((done & 63) == 0)
                StatusText = $"Contracts: {done:N0} pulled, {deferred:N0} deferred…";

            // Authed items share a 600/15min token bucket; public items don't.
            try { await Task.Delay(isPublic ? PublicItemDelayMs : AuthedItemDelayMs, ct); }
            catch (OperationCanceledException) { break; }
        }
        StatusText = $"Contracts: item pass done ({done:N0} pulled, {deferred:N0} deferred, "
                   + $"{skipped:N0} skipped) — {DateTimeOffset.Now:t}";
    }

    // Fetches a contract's items via the right endpoint and stores them (dedup by RecordId).
    // Returns false only on a hard call failure so the contract is retried next sweep.
    private async Task<bool> FetchAndStoreItemsAsync(
        AppDbContext db, int contractId, long ownerId, string ownerType, CancellationToken ct)
    {
        // Already have items from another owner row — nothing to fetch.
        if (await db.EsiContractItems.AnyAsync(i => i.ContractId == contractId, ct))
            return true;

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

        if (items is null)
        {
            // 400 (wrong contract type for this endpoint), 403/404 (gone / no access) are terminal:
            // mark handled so we stop retrying and don't keep feeding ESI's global error limit.
            // Anything else is transient — log and retry next sweep.
            if (status is not (400 or 403 or 404))
                _errorLogger.Log("ContractsService",
                    $"items contract={contractId} owner={ownerType}", $"HTTP {status}: {error}");
            return status is 400 or 403 or 404;
        }

        if (items.Count > 0) db.EsiContractItems.AddRange(items);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();   // don't let saved items accumulate across the sweep
        return true;
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

        foreach (var c in contractRows)
        {
            if (!itemAgg.TryGetValue(c.ContractId, out var agg) || agg.Qty <= 0) continue;

            decimal perUnit = c.Price / agg.Qty;
            var start = c.DateIssued.UtcDateTime;
            var end   = (c.DateAccepted ?? c.DateCompleted ?? c.DateExpired ?? now).UtcDateTime;
            bool activeNow = c.Status == "outstanding" && (c.DateExpired is null || c.DateExpired > now);

            if (!byType.TryGetValue(agg.MinType, out var list))
                byType[agg.MinType] = list = new();
            list.Add((perUnit, start, end, activeNow));
        }

        var results = new List<ContractPrice>(byType.Count);
        foreach (var (typeId, list) in byType)
        {
            // Current best = lowest per-unit among contracts active right now.
            decimal? best = null;
            int activeCount = 0;
            foreach (var e in list)
                if (e.ActiveNow) { activeCount++; if (best is null || e.PerUnit < best) best = e.PerUnit; }

            // 30-day average of the daily-best: for each of the last 30 days, the lowest per-unit
            // among contracts whose active window overlapped that day; averaged over days with data.
            decimal daySum = 0m;
            int sampleDays = 0;
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

            decimal? avg30 = sampleDays > 0 ? daySum / sampleDays : null;
            if (best is null && avg30 is null) continue;   // nothing active and nothing in 30 days

            results.Add(new ContractPrice
            {
                TypeId      = typeId,
                BestPrice   = best  is { } b ? Math.Round(b, 2, MidpointRounding.AwayFromZero) : null,
                Avg30Best   = avg30 is { } a ? Math.Round(a, 2, MidpointRounding.AwayFromZero) : null,
                ActiveCount = activeCount,
                SampleDays  = sampleDays,
                UpdatedAt   = now,
            });
        }

        // Full replace — the table is a small per-type summary.
        await db.ContractPrices.ExecuteDeleteAsync(ct);
        if (results.Count > 0) db.ContractPrices.AddRange(results);
        await db.SaveChangesAsync(ct);

        StatusText = $"Contracts: priced {results.Count:N0} types — {DateTimeOffset.Now:t}";

        if (AfterPricing is not null && !ct.IsCancellationRequested)
        {
            try { await AfterPricing(ct); }
            catch (Exception ex) { _errorLogger.Log("ContractsService", "AfterPricing", ex); }
        }
    }
}
