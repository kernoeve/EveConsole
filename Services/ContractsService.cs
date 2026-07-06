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

    // Pace between successive per-region / per-contract calls.
    private const int CallDelayMs = 100;

    // Contract types that carry an item list. "loan" has none.
    private static readonly HashSet<string> ItemBearingTypes = ["item_exchange", "auction", "courier"];

    private string _statusText = "Contracts: not started";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
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
        _publicLoop = Task.Run(() => RunLoopAsync("contract.public", 3600, SweepPublicContractsAsync, _cts.Token));
        _itemsLoop  = Task.Run(() => RunLoopAsync("contract.items",   600, SweepContractItemsAsync,   _cts.Token));
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        foreach (var t in new[] { _publicLoop, _itemsLoop })
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
                await UpsertPublicContractsAsync(db, region.RegionId, r.Data, ct);
                total += r.Data.Count;
                StatusText = $"Contracts: public {i + 1}/{regions.Count} regions · {total:N0} listed";
            }

            try { await Task.Delay(CallDelayMs, ct); } catch (OperationCanceledException) { break; }
        }
        StatusText = $"Contracts: public list updated ({total:N0}) — {DateTimeOffset.Now:t}";
    }

    // Upsert a region's public contracts; existing rows are updated, missing ones inserted,
    // and rows no longer returned are retained (public listings churn constantly).
    private static async Task UpsertPublicContractsAsync(
        AppDbContext db, int regionId, List<EsiPublicContract> data, CancellationToken ct)
    {
        var existing = (await db.EsiContracts
                .Where(c => c.OwnerType == "public" && c.OwnerId == regionId)
                .ToListAsync(ct))
            .ToDictionary(c => c.ContractId);

        foreach (var c in data)
        {
            if (existing.TryGetValue(c.ContractId, out var row))
            {
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
        await db.SaveChangesAsync(ct);
    }

    // ── Contract items (character / corp / public) ──────────────────────────────

    public async Task SweepContractItemsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Character & corp contracts still needing items (item-bearing types). Public contract
        // items are NOT auto-pulled — there are tens of thousands; those are fetched on demand.
        var pending = (await db.EsiContracts.AsNoTracking()
                .Where(c => !c.ItemsPulled && c.OwnerType != "public")
                .Select(c => new { c.ContractId, c.OwnerId, c.OwnerType, c.Type, c.IssuerCorporationId, c.Status })
                .ToListAsync(ct))
            .Where(c => ItemBearingTypes.Contains(c.Type))
            .GroupBy(c => c.ContractId)
            .ToList();

        int done = 0, skipped = 0;
        foreach (var group in pending)
        {
            if (ct.IsCancellationRequested) break;

            // Pick an owner row we can actually fetch items from. ESI only serves corp contract
            // items for contracts the corp ISSUED (or finished ones) — calling for contracts
            // issued by another corp just 404s and burns the ESI error limit.
            var src = group.FirstOrDefault(c =>
                c.OwnerType == "character" ||
                (c.OwnerType == "corporation" &&
                 (c.IssuerCorporationId == (int)c.OwnerId || c.Status.StartsWith("finished"))));

            if (src is null)
            {
                // Not fetchable via any owner (e.g. outstanding, issued by another corp) — mark
                // done so we don't reconsider it (and don't 404) every sweep.
                await db.EsiContracts.Where(x => x.ContractId == group.Key && !x.ItemsPulled)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ItemsPulled, true), ct);
                skipped++;
                continue;
            }

            while (_esi.IsErrorLimitBlocked && !ct.IsCancellationRequested)
            { try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; } }
            if (ct.IsCancellationRequested) break;

            if (await FetchAndStoreItemsAsync(db, group.Key, src.OwnerId, src.OwnerType, ct))
            {
                // Mark every owner row for this contract as pulled.
                await db.EsiContracts.Where(x => x.ContractId == group.Key && !x.ItemsPulled)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ItemsPulled, true), ct);
                done++;
            }

            if (((done + skipped) & 31) == 0)
                StatusText = $"Contracts: items {done:N0} pulled, {skipped:N0} skipped…";
            try { await Task.Delay(CallDelayMs, ct); } catch (OperationCanceledException) { break; }
        }
        StatusText = $"Contracts: item pass done ({done:N0} pulled, {skipped:N0} skipped) — {DateTimeOffset.Now:t}";
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
            // 403/404 (gone / no access) → treat as "handled" so we stop retrying it.
            // Other failures → log and retry next sweep.
            if (status is not (403 or 404))
                _errorLogger.Log("ContractsService",
                    $"items contract={contractId} owner={ownerType}", $"HTTP {status}: {error}");
            return status is 403 or 404;
        }

        if (items.Count > 0) db.EsiContractItems.AddRange(items);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
