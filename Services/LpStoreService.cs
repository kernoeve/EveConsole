using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.Services;

/// <summary>
/// Pulls LP store catalogues from /loyalty/stores/{corporation_id}/offers/.
///
/// ESI is the only source for this. CCP ships no loyalty store data in the SDE, so without
/// these calls an LP balance is a number with nothing attached to it — no way to say what
/// 1.19M Paragon LP can actually buy.
///
/// The endpoint is public and unauthenticated, so the sweep costs no token budget. Roughly
/// half the ~283 NPC corporations have no store; measured against live ESI they answer
/// 200 with an empty array rather than 404, so this costs nothing against the global error
/// limit. Those are still recorded in EsiLpStoreCorps and skipped for a month, purely to
/// keep the daily sweep to the corporations that actually have something to say.
///
/// Offers change on patch boundaries, not continuously, so the sweep runs daily.
/// </summary>
public class LpStoreService : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly EsiClient                       _esi;
    private readonly ApiActivityLog                  _log;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly TimerSettingsService            _timerSettings;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Public endpoint, no token bucket — paced only to stay polite (~7/sec).</summary>
    private const int CallDelayMs = 140;

    /// <summary>
    /// How long an empty catalogue stands before the corporation is re-tested. CCP does add
    /// stores, so the negative result has to expire — just far less often than a live
    /// catalogue is refreshed.
    /// </summary>
    private static readonly TimeSpan NoStoreRecheck = TimeSpan.FromDays(30);

    private string _statusText = "LP store: idle";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public LpStoreService(
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

    /// <summary>Live sweep progress for the Background Processes window.</summary>
    public record LpStoreStatus(
        int CorpsTotal, int CorpsChecked, int CorpsWithStore, int Offers, bool Running,
        DateTime? LastCheckedAt);

    private volatile bool _running;

    public async Task<LpStoreStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return new LpStoreStatus(
            CorpsTotal:     await db.SdeNpcCorporations.CountAsync(ct),
            CorpsChecked:   await db.EsiLpStoreCorps.CountAsync(ct),
            CorpsWithStore: await db.EsiLpStoreCorps.CountAsync(c => c.HasStore, ct),
            Offers:         await db.EsiLpStoreOffers.CountAsync(ct),
            Running:        _running,
            LastCheckedAt:  await db.EsiLpStoreCorps.MaxAsync(c => (DateTime?)c.LastCheckedAt, ct));
    }

    public void Start() =>
        _loop = Task.Run(() => RunLoopAsync("lpstore.offers", 86400, SweepAsync, _cts.Token));

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null) try { await _loop; } catch (OperationCanceledException) { }
    }

    private async Task RunLoopAsync(string timerKey, int defaultSeconds,
                                    Func<CancellationToken, Task> sweep, CancellationToken ct)
    {
        // Let startup settle before adding a few hundred calls to the queue.
        try { await Task.Delay(TimeSpan.FromSeconds(120), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await sweep(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errorLogger.Log("LpStoreService", timerKey, ex); }

            int interval = _timerSettings.GetInterval(timerKey, defaultSeconds);
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
                await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task SweepAsync(CancellationToken ct = default)
    {
        _running = true;
        try { await SweepCoreAsync(ct); }
        finally { _running = false; }
    }

    private async Task SweepCoreAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var corpNames = await db.SdeNpcCorporations.AsNoTracking()
            .ToDictionaryAsync(c => c.CorporationId, c => c.Name, ct);
        if (corpNames.Count == 0)
        {
            // No SDE import yet — nothing to enumerate. Say so rather than reporting success.
            StatusText = "LP store: no NPC corporations in the SDE — import the SDE first";
            return;
        }

        var state = await db.EsiLpStoreCorps.AsNoTracking()
            .ToDictionaryAsync(c => c.CorporationId, ct);

        var now = DateTime.UtcNow;

        // Corporations we hold LP with go first. If the sweep is cut short — cancelled, or
        // the error limit trips — the ones the user can actually spend at are already done.
        var withLp = (await db.EsiLoyaltyPoints.AsNoTracking()
                .Select(l => l.CorporationId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var targets = corpNames.Keys
            .Where(id =>
            {
                if (!state.TryGetValue(id, out var s)) return true;          // never checked
                if (s.HasStore) return true;                                 // refresh the catalogue
                return s.LastCheckedAt is null || now - s.LastCheckedAt > NoStoreRecheck;
            })
            .OrderByDescending(withLp.Contains)
            .ThenBy(id => id)
            .ToList();

        int stores = 0, offers = 0, empty = 0, failed = 0, checkedCount = 0;

        foreach (var corpId in targets)
        {
            if (ct.IsCancellationRequested) break;

            while (_esi.IsErrorLimitBlocked && !ct.IsCancellationRequested)
            { try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; } }
            if (ct.IsCancellationRequested) break;

            using (var handle = _log.StartCall(corpNames.GetValueOrDefault(corpId, $"Corp {corpId}"),
                                               "lpstore.offers"))
            {
                var r = await _esi.ExecutePublicAllPagesAsync<EsiLpStoreOffer>(
                    $"loyalty/stores/{corpId}/offers/", ct);
                handle.Complete(r.IsSuccess, r.StatusCode, r.Error);

                if (!r.IsSuccess)
                {
                    // A corporation without a store answers 200 with an empty list, so 404 is
                    // not expected here — handled defensively as "no store" rather than logged,
                    // in case CCP ever changes that. Anything else is transient: leave the
                    // state row alone so the next sweep retries.
                    if (r.StatusCode == 404)
                    {
                        await UpsertCorpAsync(db, corpId, hasStore: false, offerCount: 0, now, ct);
                        empty++;
                    }
                    else
                    {
                        _errorLogger.Log("LpStoreService", $"offers corp={corpId}",
                            $"HTTP {r.StatusCode}: {r.Error}");
                        failed++;
                    }
                }
                else
                {
                    var data = r.Data ?? [];
                    await ReplaceOffersAsync(db, corpId, data, now, ct);
                    await UpsertCorpAsync(db, corpId, hasStore: data.Count > 0,
                                          offerCount: data.Count, now, ct);
                    if (data.Count > 0) { stores++; offers += data.Count; } else empty++;
                }
            }

            checkedCount++;
            if ((checkedCount & 15) == 0)
                StatusText = $"LP store: {checkedCount:N0}/{targets.Count:N0} corps, {offers:N0} offers…";

            try { await Task.Delay(CallDelayMs, ct); }
            catch (OperationCanceledException) { break; }
        }

        StatusText = $"LP store: {stores:N0} stores, {offers:N0} offers, {empty:N0} without a store"
                   + (failed > 0 ? $", {failed:N0} failed" : "")
                   + $" — {DateTimeOffset.Now:t}";
    }

    /// <summary>
    /// Full replace per corporation. Offers are withdrawn as well as added, and an offer that
    /// no longer exists must not linger — unlike contracts, there is no historical value in a
    /// catalogue entry you can no longer buy.
    /// </summary>
    private static async Task ReplaceOffersAsync(
        AppDbContext db, int corpId, List<EsiLpStoreOffer> data, DateTime now, CancellationToken ct)
    {
        // One transaction, so the replace is never observable as a gap. ExecuteDeleteAsync
        // commits on its own otherwise, leaving this corporation with no catalogue at all
        // until the inserts land — a reader in that window sees the item as unsold rather
        // than as being refreshed. An update should overwrite what is there, never blank it
        // first.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Scoped by corporation, which is half the key — an offer id on its own belongs to
        // no single store.
        await db.EsiLpStoreOfferItems.Where(i => i.CorporationId == corpId).ExecuteDeleteAsync(ct);
        await db.EsiLpStoreOffers.Where(o => o.CorporationId == corpId).ExecuteDeleteAsync(ct);

        // One offer id can appear twice in a response; the key would reject the pair.
        foreach (var o in data.GroupBy(o => o.OfferId).Select(g => g.First()))
        {
            db.EsiLpStoreOffers.Add(new LpStoreOffer
            {
                CorporationId = corpId,
                OfferId       = o.OfferId,
                TypeId        = o.TypeId,
                Quantity      = o.Quantity,
                LpCost        = o.LpCost,
                IskCost       = o.IskCost,
                AkCost        = o.AkCost ?? 0,
                UpdatedAt     = now,
            });

            foreach (var req in (o.RequiredItems ?? []).GroupBy(i => i.TypeId).Select(g => g.First()))
                db.EsiLpStoreOfferItems.Add(new LpStoreOfferItem
                {
                    CorporationId = corpId,
                    OfferId       = o.OfferId,
                    TypeId        = req.TypeId,
                    Quantity      = req.Quantity,
                });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    private static async Task UpsertCorpAsync(
        AppDbContext db, int corpId, bool hasStore, int offerCount, DateTime now, CancellationToken ct)
    {
        var row = await db.EsiLpStoreCorps.FirstOrDefaultAsync(c => c.CorporationId == corpId, ct);
        if (row is null)
            db.EsiLpStoreCorps.Add(new LpStoreCorp
            {
                CorporationId = corpId, HasStore = hasStore,
                OfferCount = offerCount, LastCheckedAt = now,
            });
        else
        {
            row.HasStore      = hasStore;
            row.OfferCount    = offerCount;
            row.LastCheckedAt = now;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }
}
