using System.Collections.Concurrent;
using System.Text.Json;
using EveCortex.Api;
using EveCortex.Data;
using EveCortex.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace EveCortex.Services;

public record PollingResult(
    bool    Success,
    int     StatusCode,
    string? ErrorMessage       = null,
    string? RateLimitGroup     = null,
    int?    RateLimitRemaining = null,
    int?    RetryAfterSeconds  = null,
    int?    ErrorLimitRemain   = null,
    int?    ErrorLimitReset    = null);

public record EndpointInfo(string Key, string DisplayName, int MinSeconds, int DefaultSeconds);

public class EsiPollingService : ReactiveObject
{
    private readonly IServiceScopeFactory    _scopeFactory;
    private readonly EsiClient               _esi;
    private readonly ApiActivityLog          _log;
    private readonly AppErrorLogger          _errorLogger;
    private readonly KillMailService         _killMailService;
    private readonly TimerSettingsService    _timerSettings;
    private readonly NetWorthService         _netWorth;
    private readonly AppPreferencesService   _prefs;
    private readonly EveMailService          _mailService;

    private static readonly HashSet<string> s_netWorthCharEndpoints = [
        "char.wallet.balance", "char.industry.jobs", "char.orders.active", "char.assets", "char.contracts"
    ];
    private static readonly HashSet<string> s_netWorthCorpEndpoints = [
        "corp.wallet.balances", "corp.industry.jobs", "corp.orders.active", "corp.assets", "corp.contracts"
    ];

    public IReadOnlyList<EndpointInfo> CharacterEndpointInfos { get; private set; } = [];
    public IReadOnlyList<EndpointInfo> CorpEndpointInfos      { get; private set; } = [];

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCallTimes  = new();
    private readonly ConcurrentDictionary<string, GroupState>     _rateLimits     = new();
    private readonly ConcurrentDictionary<string, string>         _endpointGroups = new(); // endpoint→group
    private readonly ConcurrentDictionary<long, string>           _charNames      = new();

    // UTC ticks; 0 = not blocked. Written/read via Interlocked so parallel tasks see updates safely.
    private long _errorLimitBlockedUntilTicks;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    private string _statusText = "Polling: Not started";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private record GroupState(int Remaining, int Limit, DateTimeOffset? BlockedUntil);

    private record EndpointDef(
        string Key,
        int    MinCacheSeconds,
        int    DefaultIntervalSeconds,
        Func<long, AppDbContext, CancellationToken, Task<PollingResult>> Handler);

    private readonly List<EndpointDef> _characterEndpoints;
    private readonly List<EndpointDef> _corpEndpoints;

    private static readonly Dictionary<string, string> s_displayNames = new()
    {
        ["char.skills"]           = "Skills",
        ["char.skillqueue"]       = "Skill Queue",
        ["char.wallet.balance"]   = "Wallet Balance",
        ["char.wallet.journal"]   = "Wallet Journal",
        ["char.wallet.txns"]      = "Wallet Transactions",
        ["char.industry.jobs"]    = "Industry Jobs",
        ["char.orders.active"]    = "Active Orders",
        ["char.orders.history"]   = "Order History",
        ["char.assets"]           = "Assets",
        ["char.blueprints"]       = "Blueprints",
        ["char.contracts"]        = "Contracts",
        ["char.attributes"]       = "Attributes",
        ["char.clones"]           = "Clones",
        ["char.implants"]         = "Implants",
        ["char.fatigue"]          = "Jump Fatigue",
        ["char.mining"]           = "Mining Ledger",
        ["char.notifications"]    = "Notifications",
        ["char.contacts"]         = "Contacts",
        ["char.killmails"]        = "Kill Mails",
        ["char.planets"]          = "Planetary Interaction",
        ["char.agents_research"]  = "Agent Research",
        ["char.loyalty"]          = "Loyalty Points",
        ["char.medals"]           = "Medals",
        ["char.standings"]        = "Standings",
        ["char.titles"]           = "Titles",
        ["char.roles"]            = "Roles",
        ["char.fittings"]         = "Fittings",
        ["char.mail"]             = "Eve Mail",
        ["corp.wallet.balances"]  = "Wallet Balances",
        ["corp.divisions"]        = "Divisions",
        ["corp.wallet.journal"]   = "Wallet Journal",
        ["corp.wallet.txns"]      = "Wallet Transactions",
        ["corp.industry.jobs"]    = "Industry Jobs",
        ["corp.orders.active"]    = "Active Orders",
        ["corp.orders.history"]   = "Order History",
        ["corp.assets"]           = "Assets",
        ["corp.blueprints"]       = "Blueprints",
        ["corp.contracts"]        = "Contracts",
        ["corp.contacts"]         = "Contacts",
        ["corp.killmails"]        = "Kill Mails",
        ["corp.standings"]        = "Standings",
        ["corp.structures"]       = "Structures",
        ["corp.starbases"]        = "Starbases",
        ["corp.facilities"]       = "Facilities",
        ["corp.members"]          = "Members",
        ["corp.roles"]            = "Roles",
        ["corp.titles"]           = "Titles",
        ["corp.medals"]           = "Medals",
        ["corp.projects"]           = "Corp Projects",
        ["corp.mining.extractions"] = "Mining Extractions",
        ["corp.mining.observers"]   = "Mining Observers & Ledger",
        ["market.refresh"]        = "Market Price Refresh",
        ["build.costs"]           = "Build Cost Calculation",
        ["contract.public"]       = "Public Contracts",
        ["contract.items"]        = "Contract Items",
    };

    public EsiPollingService(IServiceScopeFactory scopeFactory, EsiClient esi, ApiActivityLog log, AppErrorLogger errorLogger, TimerSettingsService timerSettings, NetWorthService netWorth, KillMailService killMailService, AppPreferencesService prefs, EveMailService mailService)
    {
        _scopeFactory       = scopeFactory;
        _esi                = esi;
        _log                = log;
        _errorLogger        = errorLogger;
        _timerSettings      = timerSettings;
        _netWorth           = netWorth;
        _killMailService    = killMailService;
        _prefs              = prefs;
        _mailService        = mailService;
        _characterEndpoints = BuildEndpoints();
        _corpEndpoints      = BuildCorpEndpoints();
        CharacterEndpointInfos = _characterEndpoints
            .Select(e => new EndpointInfo(e.Key, s_displayNames.GetValueOrDefault(e.Key, e.Key), e.MinCacheSeconds, e.DefaultIntervalSeconds))
            .ToList();
        CorpEndpointInfos = _corpEndpoints
            .Select(e => new EndpointInfo(e.Key, s_displayNames.GetValueOrDefault(e.Key, e.Key), e.MinCacheSeconds, e.DefaultIntervalSeconds))
            .ToList();
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public void Start()
    {
        _cts         = new CancellationTokenSource();
        _pollingTask = Task.Run(() => RunPollingLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        if (_pollingTask is not null)
            try { await _pollingTask; } catch (OperationCanceledException) { }
        _cts = null;
        StatusText = "Polling: Stopped";
    }

    // ── Main loop ────────────────────────────────────────────────────────────

    private async Task RunPollingLoopAsync(CancellationToken ct)
    {
        await LoadLastCallTimesAsync(ct);
        await LoadCharacterTokensAsync(ct);
        await LoadCorpTokensAsync(ct);
        StatusText = "Polling: Running";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.WhenAll(
                    RunOneCycleAsync(ct),
                    RunCorpCycleAsync(ct),
                    _killMailService.FetchMissingAsync(ct: ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                StatusText = $"Polling: Error — {msg[..Math.Min(60, msg.Length)]}";
                _errorLogger.Log("EsiPollingService", "RunPollingLoopAsync", ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private async Task RunOneCycleAsync(CancellationToken ct)
    {
        List<Character> characters;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            characters = await db.Characters
                .Where(c => c.RefreshToken != "")
                .AsNoTracking()
                .ToListAsync(ct);
        }

        if (characters.Count == 0) return;

        foreach (var ch in characters)
            _charNames[ch.Id] = ch.Name;

        var now = DateTimeOffset.UtcNow;
        if (Interlocked.Read(ref _errorLimitBlockedUntilTicks) is var bt and > 0 && now.UtcTicks < bt)
            return;

        // Stagger character tasks — prevents simultaneous bursts at startup when all endpoints are due.
        await Task.WhenAll(characters.Select(async (ch, i) =>
        {
            if (i > 0) await Task.Delay(i * 200, ct);
            await ProcessCharacterAsync(ch, now, ct);
        }));
    }

    // Character endpoints that need a scope beyond the base set. A token authorized before the
    // scope existed won't have it, so we skip the call instead of 401ing on it every cycle —
    // the character must be re-added to grant the scope.
    private static readonly Dictionary<string, string> s_charEndpointScopes = new()
    {
        ["char.roles"] = "esi-characters.read_corporation_roles.v1",
    };

    private async Task ProcessCharacterAsync(Character character, DateTimeOffset now, CancellationToken ct)
    {
        var netWorthDirty = false;

        foreach (var ep in _characterEndpoints)
        {
            ct.ThrowIfCancellationRequested();

            if (s_charEndpointScopes.TryGetValue(ep.Key, out var reqScope) && !character.HasScope(reqScope))
                continue;

            var callKey = $"{ep.Key}:{character.Id}:character";

            if (_lastCallTimes.TryGetValue(callKey, out var lastCalled) &&
                (now - lastCalled).TotalSeconds < _timerSettings.GetInterval(ep.Key, ep.DefaultIntervalSeconds))
                continue;

            if (_endpointGroups.TryGetValue(ep.Key, out var groupName) &&
                _rateLimits.TryGetValue(groupName, out var gs) &&
                gs.BlockedUntil.HasValue && now < gs.BlockedUntil.Value)
                continue;

            // Re-check global error limit — a parallel task may have tripped it since cycle start.
            if (Interlocked.Read(ref _errorLimitBlockedUntilTicks) is var bt and > 0 && DateTimeOffset.UtcNow.UtcTicks < bt)
                return;

            using var scope  = _scopeFactory.CreateScope();
            var callDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var name = _charNames.TryGetValue(character.Id, out var n) ? n : character.Id.ToString();
            using var handle = _log.StartCall(name, ep.Key);

            PollingResult result;
            try
            {
                result = await ep.Handler(character.Id, callDb, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result = new PollingResult(false, 0, ex.InnerException?.Message ?? ex.Message);
                _errorLogger.Log("EsiPollingService", $"{ep.Key}:{character.Id}", ex);
            }

            var callTime = DateTimeOffset.UtcNow;
            _lastCallTimes[callKey] = callTime;
            await PersistCallRecordAsync(character.Id, "character", ep.Key, callTime, result.StatusCode, ct);

            UpdateRateLimitState(ep.Key, result);
            handle.Complete(result.Success, result.StatusCode, result.ErrorMessage);

            if (!result.Success && result.StatusCode > 0)
                _errorLogger.Log("EsiPollingService", $"{ep.Key}:{character.Id}",
                    $"HTTP {result.StatusCode}", result.ErrorMessage);

            if (result.Success && s_netWorthCharEndpoints.Contains(ep.Key))
                netWorthDirty = true;

            await Task.Delay(500, ct);
        }

        if (netWorthDirty)
            _ = _netWorth.RecalculateAsync(character.Id, "character", ct);
    }

    // ── DB helpers ───────────────────────────────────────────────────────────

    // Clears in-memory last-call timestamps for the given endpoint key across all owners.
    // The next poll cycle will treat the endpoint as never-run and fire it immediately.
    public void ResetCallTime(string endpointKey)
    {
        var prefix = endpointKey + ":";
        foreach (var key in _lastCallTimes.Keys.Where(k => k.StartsWith(prefix)).ToList())
            _lastCallTimes.TryRemove(key, out _);
    }

    private async Task LoadLastCallTimesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var records = await db.EsiCallRecords.AsNoTracking().ToListAsync(ct);
        foreach (var r in records)
            _lastCallTimes[$"{r.Endpoint}:{r.OwnerId}:{r.OwnerType}"] = r.LastCalledAt;
    }

    private async Task PersistCallRecordAsync(
        long ownerId, string ownerType, string endpoint,
        DateTimeOffset calledAt, int statusCode, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.EsiCallRecords
            .FindAsync([ownerId, ownerType, endpoint], ct);

        if (existing is null)
        {
            db.EsiCallRecords.Add(new ApiCallRecord
            {
                OwnerId        = ownerId,
                OwnerType      = ownerType,
                Endpoint       = endpoint,
                LastCalledAt   = calledAt,
                LastStatusCode = statusCode,
            });
        }
        else
        {
            existing.LastCalledAt   = calledAt;
            existing.LastStatusCode = statusCode;
        }

        await db.SaveChangesAsync(ct);
    }

    private void UpdateRateLimitState(string endpointKey, PollingResult result)
    {
        if (result.RateLimitGroup is not null)
        {
            _endpointGroups[endpointKey] = result.RateLimitGroup;

            DateTimeOffset? blockedUntil = null;
            if (result.StatusCode is 420 or 429)
            {
                var waitSecs = result.RetryAfterSeconds ?? result.ErrorLimitReset ?? 60;
                blockedUntil = DateTimeOffset.UtcNow.AddSeconds(waitSecs);
            }

            _rateLimits[result.RateLimitGroup] = new GroupState(
                result.RateLimitRemaining ?? 0,
                100,
                blockedUntil);
        }

        // Global ESI error limit — Interlocked so all parallel polling tasks see the block immediately.
        if (result.StatusCode == 420)
        {
            // Error limit exhausted — block for the full reset window plus a small buffer.
            var resetSecs = result.ErrorLimitReset ?? 30;
            Interlocked.Exchange(ref _errorLimitBlockedUntilTicks,
                DateTimeOffset.UtcNow.AddSeconds(resetSecs + 1).UtcTicks);
        }
        else if (result.ErrorLimitRemain.HasValue && result.ErrorLimitRemain.Value < 20 &&
                 result.ErrorLimitReset.HasValue)
        {
            // Pre-emptively throttle when approaching the error limit.
            Interlocked.Exchange(ref _errorLimitBlockedUntilTicks,
                DateTimeOffset.UtcNow.AddSeconds(result.ErrorLimitReset.Value).UtcTicks);
        }
        else if (result.ErrorLimitRemain.HasValue && result.ErrorLimitRemain.Value > 30)
        {
            Interlocked.Exchange(ref _errorLimitBlockedUntilTicks, 0L);
        }
    }

    private static PollingResult FromResult<T>(EsiCallResult<T> r) =>
        new(r.IsSuccess, r.StatusCode,
            r.IsSuccess ? null : r.Error,
            r.RateLimitGroup, r.RateLimitRemaining, r.RetryAfterSeconds,
            r.ErrorLimitRemain, r.ErrorLimitReset);

    // Walks the parent-chain for every asset and returns {ItemId → (RootLocationId, RootLocationType)}.
    // A terminal is reached when LocationType is not 'item', or when the LocationId is not found
    // among the asset ItemIds (meaning it's an external player structure > 1T, or an unknown ref).
    private static Dictionary<long, (long RootId, string RootType)> ComputeRootLocations(
        IReadOnlyList<EsiAsset> assets)
    {
        var byId   = assets.ToDictionary(a => a.ItemId);
        var result = new Dictionary<long, (long, string)>(assets.Count);

        foreach (var asset in assets)
        {
            var current = asset;
            int depth   = 0;
            while (true)
            {
                if (current.LocationType is "station" or "solar_system" or "other")
                {
                    result[asset.ItemId] = (current.LocationId, current.LocationType);
                    break;
                }
                if (current.LocationType == "item" && byId.TryGetValue(current.LocationId, out var parent))
                {
                    if (++depth > 12) { result[asset.ItemId] = (current.LocationId, "unknown"); break; }
                    current = parent;
                    continue;
                }
                // LocationId not in our asset list — external structure if > 1T, else unknown.
                var rootType = current.LocationId > 1_000_000_000_000L ? "other" : "unknown";
                result[asset.ItemId] = (current.LocationId, rootType);
                break;
            }
        }
        return result;
    }

    // ── Endpoint handlers ────────────────────────────────────────────────────

    private async Task<PollingResult> FetchSkillsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<EsiSkills>(charId, $"characters/{charId}/skills/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiSkills.Where(s => s.CharacterId == charId).ExecuteDeleteAsync(ct);
        db.EsiSkills.AddRange(r.Data!.Skills.Select(s => new StoredSkill
        {
            CharacterId        = charId,
            SkillId            = s.SkillId,
            TrainedSkillLevel  = s.TrainedSkillLevel,
            ActiveSkillLevel   = s.ActiveSkillLevel,
            SkillpointsInSkill = s.SkillpointsInSkill,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchSkillQueueAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<List<EsiSkillQueueItem>>(charId,
            $"characters/{charId}/skillqueue/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiSkillQueue.Where(s => s.CharacterId == charId).ExecuteDeleteAsync(ct);
        db.EsiSkillQueue.AddRange(r.Data!.Select(s => new StoredSkillQueueEntry
        {
            CharacterId    = charId,
            QueuePosition  = s.QueuePosition,
            SkillId        = s.SkillId,
            FinishedLevel  = s.FinishedLevel,
            TrainingStartSp = s.TrainingStartSp,
            LevelStartSp   = s.LevelStartSp,
            LevelEndSp     = s.LevelEndSp,
            StartDate      = s.StartDate,
            FinishDate     = s.FinishDate,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchWalletBalanceAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<double>(charId, $"characters/{charId}/wallet/", ct);
        if (!r.IsSuccess) return FromResult(r);

        var existing = await db.EsiWalletBalances.FindAsync([charId, "character", 0], ct);
        if (existing is null)
        {
            db.EsiWalletBalances.Add(new CharacterWalletBalance
            {
                OwnerId   = charId,
                OwnerType = "character",
                Division  = 0,
                Balance   = (decimal)r.Data,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Balance   = (decimal)r.Data;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    // ── Wallet backfill state (interruption hardening) ──────────────────────────
    // The fast "stop at the first already-stored page" catch-up assumes our stored history is
    // contiguous. A poll interrupted mid-way (error / rate-limit / shutdown) can break that
    // assumption and leave a hole that a later fast catch-up would skip forever. These markers make
    // the fetch re-page the whole ESI window until one full pass completes cleanly.

    private static async Task<bool> IsBackfillCompleteAsync(
        AppDbContext db, long ownerId, string ownerType, string kind, int division, CancellationToken ct)
        => await db.WalletBackfillStates.AsNoTracking().AnyAsync(
            s => s.OwnerId == ownerId && s.OwnerType == ownerType && s.Kind == kind
              && s.Division == division && s.Complete, ct);

    private static async Task SetBackfillCompleteAsync(
        AppDbContext db, long ownerId, string ownerType, string kind, int division, bool complete, CancellationToken ct)
    {
        var row = await db.WalletBackfillStates.FirstOrDefaultAsync(
            s => s.OwnerId == ownerId && s.OwnerType == ownerType && s.Kind == kind && s.Division == division, ct);
        if (row is null)
            db.WalletBackfillStates.Add(new WalletBackfillState
            {
                OwnerId = ownerId, OwnerType = ownerType, Kind = kind, Division = division, Complete = complete,
            });
        else
            row.Complete = complete;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task<PollingResult> FetchWalletJournalAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        // Fetch page-by-page. Once a clean full pass is confirmed we stop at the first all-stored
        // page (fast catch-up); until then (first run / after an interruption) we page the whole
        // window so any hole left by a partial poll is filled.
        var existingIds = await db.EsiWalletJournal
            .Where(w => w.OwnerId == charId && w.OwnerType == "character")
            .Select(w => w.EsiId)
            .ToHashSetAsync(ct);

        bool fullScan   = !await IsBackfillCompleteAsync(db, charId, "character", "journal", 0, ct);
        bool interrupted = false, reachedEnd = false;
        PollingResult? lastResult = null;

        for (int page = 1; ; page++)
        {
            ct.ThrowIfCancellationRequested();
            var r = await _esi.ExecuteAuthAsync<List<EsiWalletJournalEntry>>(
                charId, $"characters/{charId}/wallet/journal/", ct, page: page);
            lastResult = FromResult(r);
            if (!r.IsSuccess || r.Data is null) { interrupted = true; break; }
            if (r.Data.Count == 0) { reachedEnd = true; break; }

            var newEntries = r.Data
                .Where(e => !existingIds.Contains(e.Id))
                .Select(e => new WalletJournalEntry
                {
                    EsiId          = e.Id,
                    OwnerId        = charId,
                    OwnerType      = "character",
                    Date           = e.Date,
                    RefType        = e.RefType,
                    FirstPartyId   = e.FirstPartyId,
                    SecondPartyId  = e.SecondPartyId,
                    Amount         = (decimal)(e.Amount ?? 0),
                    Balance        = (decimal)(e.Balance ?? 0),
                    Description    = e.Description,
                    Reason         = e.Reason,
                    Tax            = e.Tax.HasValue ? (decimal)e.Tax.Value : null,
                    TaxReceiverId  = e.TaxReceiverId,
                    ContextId      = e.ContextId,
                    ContextIdType  = e.ContextIdType,
                })
                .ToList();

            if (newEntries.Count > 0)
            {
                db.EsiWalletJournal.AddRange(newEntries);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                foreach (var e in newEntries) existingIds.Add(e.EsiId);
            }

            // Fast catch-up (only once fully backfilled): all entries on this page already stored,
            // so everything older is stored too.
            if (!fullScan && newEntries.Count == 0) { reachedEnd = true; break; }

            // Reached the last page ESI has.
            if (page >= r.TotalPages) { reachedEnd = true; break; }
        }

        if (interrupted)
            await SetBackfillCompleteAsync(db, charId, "character", "journal", 0, false, ct);
        else if (reachedEnd && fullScan)
            await SetBackfillCompleteAsync(db, charId, "character", "journal", 0, true, ct);

        return lastResult ?? new PollingResult(true, 200);
    }

    private async Task<PollingResult> FetchWalletTransactionsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        // ESI wallet/transactions uses from_id cursor pagination (not X-Pages).
        // Each call returns at most 2500 entries ordered newest-first.
        // Cursor backwards by passing from_id = oldest transaction ID in the last batch.
        var existingIds = await db.EsiWalletTransactions
            .Where(w => w.OwnerId == charId && w.OwnerType == "character")
            .Select(w => w.TransactionId)
            .ToHashSetAsync(ct);

        bool fullScan   = !await IsBackfillCompleteAsync(db, charId, "character", "transactions", 0, ct);
        bool interrupted = false, reachedEnd = false;
        PollingResult? lastResult = null;
        long? fromId = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var path = fromId.HasValue
                ? $"characters/{charId}/wallet/transactions/?from_id={fromId.Value}"
                : $"characters/{charId}/wallet/transactions/";

            var r = await _esi.ExecuteAuthAsync<List<EsiWalletTransaction>>(charId, path, ct);
            lastResult = FromResult(r);
            if (!r.IsSuccess || r.Data is null) { interrupted = true; break; }
            if (r.Data.Count == 0) { reachedEnd = true; break; }

            var newEntries = r.Data
                .Where(t => !existingIds.Contains(t.TransactionId))
                .Select(t => new WalletTransaction
                {
                    TransactionId = t.TransactionId,
                    OwnerId       = charId,
                    OwnerType     = "character",
                    Date          = t.Date,
                    ClientId      = t.ClientId,
                    LocationId    = t.LocationId,
                    Quantity      = t.Quantity,
                    TypeId        = t.TypeId,
                    UnitPrice     = (decimal)t.UnitPrice,
                    IsBuy         = t.IsBuy,
                    IsPersonal    = t.IsPersonal,
                    JournalRefId  = t.JournalRefId,
                })
                .ToList();

            if (newEntries.Count > 0)
            {
                db.EsiWalletTransactions.AddRange(newEntries);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                foreach (var e in newEntries) existingIds.Add(e.TransactionId);
            }

            // < 2500 means we've reached the end of ESI's window — a natural stop for any pass.
            if (r.Data.Count < 2500) { reachedEnd = true; break; }
            // Fast catch-up (only once fully backfilled): caught up with stored data.
            if (!fullScan && newEntries.Count == 0) { reachedEnd = true; break; }

            fromId = r.Data.Min(t => t.TransactionId);
        }

        if (interrupted)
            await SetBackfillCompleteAsync(db, charId, "character", "transactions", 0, false, ct);
        else if (reachedEnd && fullScan)
            await SetBackfillCompleteAsync(db, charId, "character", "transactions", 0, true, ct);

        return lastResult ?? new PollingResult(true, 200);
    }

    private async Task<PollingResult> FetchIndustryJobsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<List<EsiIndustryJob>>(charId,
            $"characters/{charId}/industry/jobs/?include_completed=true", ct);
        if (!r.IsSuccess) return FromResult(r);

        // Upsert: ESI only returns jobs within the past 90 days; older rows are kept as-is.
        // Status, PauseDate, CompletedDate, CompletedCharacterId, SuccessfulRuns are mutable.
        var existingMap = (await db.EsiIndustryJobs
            .Where(j => j.OwnerId == charId && j.OwnerType == "character")
            .ToListAsync(ct))
            .ToDictionary(j => j.JobId);

        foreach (var j in r.Data!)
        {
            if (existingMap.TryGetValue(j.JobId, out var row))
            {
                row.Status               = j.Status;
                row.PauseDate            = j.PauseDate;
                row.CompletedDate        = j.CompletedDate;
                row.CompletedCharacterId = j.CompletedCharacterId;
                row.SuccessfulRuns       = j.SuccessfulRuns;
            }
            else
            {
                db.EsiIndustryJobs.Add(new IndustryJob
                {
                    JobId                = j.JobId,
                    OwnerId              = charId,
                    OwnerType            = "character",
                    InstallerId          = j.InstallerId,
                    FacilityId           = j.FacilityId,
                    StationId            = j.StationId,
                    ActivityId           = j.ActivityId,
                    BlueprintId          = j.BlueprintId,
                    BlueprintTypeId      = j.BlueprintTypeId,
                    BlueprintLocationId  = j.BlueprintLocationId,
                    OutputLocationId     = j.OutputLocationId,
                    Runs                 = j.Runs,
                    Cost                 = (decimal)j.Cost,
                    LicensedRuns         = j.LicensedRuns,
                    Probability          = j.Probability,
                    ProductTypeId        = j.ProductTypeId,
                    Status               = j.Status,
                    Duration             = j.Duration,
                    StartDate            = j.StartDate,
                    EndDate              = j.EndDate,
                    PauseDate            = j.PauseDate,
                    CompletedDate        = j.CompletedDate,
                    CompletedCharacterId = j.CompletedCharacterId,
                    SuccessfulRuns       = j.SuccessfulRuns,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchActiveOrdersAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<List<EsiMarketOrder>>(charId,
            $"characters/{charId}/orders/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiMarketOrders
            .Where(o => o.OwnerId == charId && o.OwnerType == "character" && !o.IsHistory)
            .ExecuteDeleteAsync(ct);

        db.EsiMarketOrders.AddRange(r.Data!.Select(o => new MarketOrder
        {
            OrderId      = o.OrderId,
            OwnerId      = charId,
            OwnerType    = "character",
            TypeId       = o.TypeId,
            LocationId   = o.LocationId,
            VolumeTotal  = o.VolumeTotal,
            VolumeRemain = o.VolumeRemain,
            MinVolume    = o.MinVolume,
            Price        = (decimal)o.Price,
            IsBuyOrder   = o.IsBuyOrder,
            Duration     = o.Duration,
            Issued       = o.Issued,
            Range        = o.Range,
            Escrow       = o.Escrow.HasValue ? (decimal)o.Escrow.Value : null,
            IsCorporation = o.IsCorporation,
            RegionId     = o.RegionId,
            State        = o.State,
            IsHistory    = false,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchOrderHistoryAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAllPagesAsync<EsiMarketOrder>(charId,
            $"characters/{charId}/orders/history/", ct);
        if (!r.IsSuccess) return FromResult(r);

        var existingIds = await db.EsiMarketOrders
            .Where(o => o.OwnerId == charId && o.OwnerType == "character" && o.IsHistory)
            .Select(o => o.OrderId)
            .ToHashSetAsync(ct);

        var newOrders = r.Data!
            .Where(o => !existingIds.Contains(o.OrderId))
            .Select(o => new MarketOrder
            {
                OrderId      = o.OrderId,
                OwnerId      = charId,
                OwnerType    = "character",
                TypeId       = o.TypeId,
                LocationId   = o.LocationId,
                VolumeTotal  = o.VolumeTotal,
                VolumeRemain = o.VolumeRemain,
                MinVolume    = o.MinVolume,
                Price        = (decimal)o.Price,
                IsBuyOrder   = o.IsBuyOrder,
                Duration     = o.Duration,
                Issued       = o.Issued,
                Range        = o.Range,
                State        = o.State,
                IsHistory    = true,
            });

        db.EsiMarketOrders.AddRange(newOrders);
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchAssetsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAllPagesAsync<EsiAsset>(charId,
            $"characters/{charId}/assets/", ct);
        if (!r.IsSuccess) return FromResult(r);

        var roots = ComputeRootLocations(r.Data!);
        await db.EsiAssets
            .Where(a => a.OwnerId == charId && a.OwnerType == "character")
            .ExecuteDeleteAsync(ct);
        db.EsiAssets.AddRange(r.Data!.Select(a => new CharacterAsset
        {
            ItemId           = a.ItemId,
            OwnerId          = charId,
            OwnerType        = "character",
            TypeId           = a.TypeId,
            LocationId       = a.LocationId,
            LocationType     = a.LocationType,
            LocationFlag     = a.LocationFlag,
            Quantity         = a.Quantity,
            IsSingleton      = a.IsSingleton,
            IsBlueprintCopy  = a.IsBlueprintCopy,
            RootLocationId   = roots[a.ItemId].RootId,
            RootLocationType = roots[a.ItemId].RootType,
        }));
        await db.SaveChangesAsync(ct);

        // A LocationId > 1T is a real player structure only if it doesn't appear as an
        // ItemId in this asset list. Office folders, CorpSAG divisions, ships, and
        // containers all have their own ItemId > 1T and will self-exclude here.
        var ownItemIds = new HashSet<long>(r.Data!
            .Where(a => a.ItemId > 1_000_000_000_000L)
            .Select(a => a.ItemId));
        var structureIds = r.Data!
            .Where(a => a.LocationId > 1_000_000_000_000L)
            .Select(a => a.LocationId)
            .Distinct()
            .Where(id => !ownItemIds.Contains(id))
            .ToList();
        await ResolveNewStructureNamesAsync(charId, structureIds, db, ct);

        return FromResult(r);
    }

    private async Task<PollingResult> FetchBlueprintsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAllPagesAsync<EsiBlueprintData>(charId,
            $"characters/{charId}/blueprints/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiBlueprints
            .Where(b => b.OwnerId == charId && b.OwnerType == "character")
            .ExecuteDeleteAsync(ct);
        db.EsiBlueprints.AddRange(r.Data!.Select(b => new CharacterBlueprint
        {
            ItemId             = b.ItemId,
            OwnerId            = charId,
            OwnerType          = "character",
            TypeId             = b.TypeId,
            LocationId         = b.LocationId,
            LocationFlag       = b.LocationFlag,
            Quantity           = b.Quantity,
            TimeEfficiency     = b.TimeEfficiency,
            MaterialEfficiency = b.MaterialEfficiency,
            Runs               = b.Runs,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchContractsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAllPagesAsync<EsiContractData>(charId,
            $"characters/{charId}/contracts/", ct);
        if (!r.IsSuccess) return FromResult(r);

        // Upsert: update existing rows (status/acceptance can change) and insert new ones.
        // Contracts no longer returned by ESI are retained.
        var existing = (await db.EsiContracts
                .Where(c => c.OwnerId == charId && c.OwnerType == "character")
                .ToListAsync(ct))
            .ToDictionary(c => c.ContractId);

        foreach (var c in r.Data!)
        {
            if (existing.TryGetValue(c.ContractId, out var row))
            {
                row.Status        = c.Status;
                row.AcceptorId    = c.AcceptorId;
                row.DateAccepted  = c.DateAccepted;
                row.DateCompleted = c.DateCompleted;
                row.DateExpired   = c.DateExpired;
            }
            else
            {
                db.EsiContracts.Add(new ContractRecord
                {
                    ContractId          = c.ContractId,
                    OwnerId             = charId,
                    OwnerType           = "character",
                    IssuerId            = c.IssuerId,
                    IssuerCorporationId = c.IssuerCorporationId,
                    AssigneeId          = c.AssigneeId,
                    AcceptorId          = c.AcceptorId,
                    StartLocationId     = c.StartLocationId,
                    EndLocationId       = c.EndLocationId,
                    Type                = c.Type,
                    Status              = c.Status,
                    Title               = c.Title,
                    ForCorporation      = c.ForCorporation,
                    Availability        = c.Availability,
                    DateIssued          = c.DateIssued,
                    DateExpired         = c.DateExpired,
                    DateAccepted        = c.DateAccepted,
                    DateCompleted       = c.DateCompleted,
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
        return FromResult(r);
    }

    private async Task<PollingResult> FetchAttributesAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<EsiCharacterAttributesData>(charId,
            $"characters/{charId}/attributes/", ct);
        if (!r.IsSuccess) return FromResult(r);

        var existing = await db.EsiCharacterAttributes.FindAsync([charId], ct);
        if (existing is null)
        {
            db.EsiCharacterAttributes.Add(new StoredCharacterAttributes
            {
                CharacterId              = charId,
                Charisma                 = r.Data!.Charisma,
                Intelligence             = r.Data.Intelligence,
                Memory                   = r.Data.Memory,
                Perception               = r.Data.Perception,
                Willpower                = r.Data.Willpower,
                BonusRemaps              = r.Data.BonusRemaps,
                LastRemapDate            = r.Data.LastRemapDate,
                AccruingRemapCooldownDate = r.Data.AccruingRemapCooldownDate,
                UpdatedAt                = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Charisma                 = r.Data!.Charisma;
            existing.Intelligence             = r.Data.Intelligence;
            existing.Memory                   = r.Data.Memory;
            existing.Perception               = r.Data.Perception;
            existing.Willpower                = r.Data.Willpower;
            existing.BonusRemaps              = r.Data.BonusRemaps;
            existing.LastRemapDate            = r.Data.LastRemapDate;
            existing.AccruingRemapCooldownDate = r.Data.AccruingRemapCooldownDate;
            existing.UpdatedAt                = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchClonesAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<EsiClonesData>(charId,
            $"characters/{charId}/clones/", ct);
        if (!r.IsSuccess) return FromResult(r);

        // Update clone state (single row per character)
        var cs = await db.EsiCloneStates.FindAsync([charId], ct);
        if (cs is null)
        {
            db.EsiCloneStates.Add(new CharacterCloneState
            {
                CharacterId           = charId,
                HomeLocationId        = r.Data!.HomeLocation?.LocationId,
                HomeLocationType      = r.Data.HomeLocation?.LocationType,
                LastCloneJumpDate     = r.Data.LastCloneJumpDate,
                LastStationChangeDate = r.Data.LastStationChangeDate,
                UpdatedAt             = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            cs.HomeLocationId        = r.Data!.HomeLocation?.LocationId;
            cs.HomeLocationType      = r.Data.HomeLocation?.LocationType;
            cs.LastCloneJumpDate     = r.Data.LastCloneJumpDate;
            cs.LastStationChangeDate = r.Data.LastStationChangeDate;
            cs.UpdatedAt             = DateTimeOffset.UtcNow;
        }

        // Replace jump clones (clone IDs change when clones are destroyed/created)
        var existingCloneIds = await db.EsiJumpClones
            .Where(c => c.CharacterId == charId)
            .Select(c => c.JumpCloneId)
            .ToListAsync(ct);

        // Remove clones that no longer exist
        var incomingCloneIds = r.Data!.JumpClones.Select(c => c.JumpCloneId).ToHashSet();
        var removedCloneIds = existingCloneIds.Where(id => !incomingCloneIds.Contains(id)).ToList();
        if (removedCloneIds.Count > 0)
        {
            await db.EsiJumpCloneImplants.Where(i => removedCloneIds.Contains(i.JumpCloneId)).ExecuteDeleteAsync(ct);
            await db.EsiJumpClones.Where(c => removedCloneIds.Contains(c.JumpCloneId)).ExecuteDeleteAsync(ct);
        }

        // Upsert remaining clones
        foreach (var clone in r.Data!.JumpClones)
        {
            var existing = await db.EsiJumpClones.FindAsync([clone.JumpCloneId], ct);
            if (existing is null)
            {
                db.EsiJumpClones.Add(new StoredJumpClone
                {
                    JumpCloneId  = clone.JumpCloneId,
                    CharacterId  = charId,
                    LocationId   = clone.LocationId,
                    LocationType = clone.LocationType,
                    Name         = clone.Name,
                });
            }
            else
            {
                existing.LocationId   = clone.LocationId;
                existing.LocationType = clone.LocationType;
                existing.Name         = clone.Name;
            }

            // Replace implants for this clone
            await db.EsiJumpCloneImplants
                .Where(i => i.JumpCloneId == clone.JumpCloneId)
                .ExecuteDeleteAsync(ct);

            db.EsiJumpCloneImplants.AddRange(clone.Implants.Select(typeId =>
                new StoredJumpCloneImplant { JumpCloneId = clone.JumpCloneId, TypeId = typeId }));
        }

        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchImplantsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<List<int>>(charId,
            $"characters/{charId}/implants/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiImplants.Where(i => i.CharacterId == charId).ExecuteDeleteAsync(ct);
        db.EsiImplants.AddRange(r.Data!.Select(typeId =>
            new StoredImplant { CharacterId = charId, TypeId = typeId }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchFatigueAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<EsiFatigueData>(charId,
            $"characters/{charId}/fatigue/", ct);
        if (!r.IsSuccess) return FromResult(r);

        var existing = await db.EsiCharacterFatigues.FindAsync([charId], ct);
        if (existing is null)
        {
            db.EsiCharacterFatigues.Add(new StoredCharacterFatigue
            {
                CharacterId              = charId,
                LastJumpDate             = r.Data!.LastJumpDate,
                JumpFatigueExpireDate    = r.Data.JumpFatigueExpireDate,
                LastUpdateDate           = r.Data.LastUpdateDate,
                UpdatedAt                = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.LastJumpDate          = r.Data!.LastJumpDate;
            existing.JumpFatigueExpireDate = r.Data.JumpFatigueExpireDate;
            existing.LastUpdateDate        = r.Data.LastUpdateDate;
            existing.UpdatedAt             = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchMiningAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAllPagesAsync<EsiMiningData>(charId,
            $"characters/{charId}/mining/", ct);
        if (!r.IsSuccess) return FromResult(r);

        // Upsert: ESI only returns ~30 days; older rows are preserved as-is.
        // Quantity on today's entry changes throughout the day as mining continues.
        var existing = await db.EsiMining
            .Where(m => m.CharacterId == charId)
            .ToListAsync(ct);

        var existingMap = existing.ToDictionary(m => (m.Date, m.SolarSystemId, m.TypeId));

        foreach (var m in r.Data!)
        {
            if (existingMap.TryGetValue((m.Date, m.SolarSystemId, m.TypeId), out var row))
                row.Quantity = m.Quantity;
            else
                db.EsiMining.Add(new CharacterMiningEntry
                {
                    CharacterId   = charId,
                    Date          = m.Date,
                    SolarSystemId = m.SolarSystemId,
                    TypeId        = m.TypeId,
                    Quantity      = m.Quantity,
                });
        }

        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchNotificationsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<List<EsiNotificationData>>(charId,
            $"characters/{charId}/notifications/", ct);
        if (!r.IsSuccess) return FromResult(r);

        var existingIds = await db.EsiNotifications
            .Where(n => n.CharacterId == charId)
            .Select(n => n.NotificationId)
            .ToHashSetAsync(ct);

        var newNotifs = r.Data!
            .Where(n => !existingIds.Contains(n.NotificationId))
            .Select(n => new CharacterNotification
            {
                NotificationId = n.NotificationId,
                CharacterId    = charId,
                Type           = n.Type,
                SenderId       = n.SenderId,
                SenderType     = n.SenderType,
                Timestamp      = n.Timestamp,
                IsRead         = n.IsRead ?? false,
                Text           = n.Text,
            });

        db.EsiNotifications.AddRange(newNotifs);
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchContactsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAllPagesAsync<EsiContactData>(charId,
            $"characters/{charId}/contacts/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiContacts
            .Where(c => c.OwnerId == charId && c.OwnerType == "character")
            .ExecuteDeleteAsync(ct);

        db.EsiContacts.AddRange(r.Data!.Select(c => new ContactEntry
        {
            OwnerId     = charId,
            OwnerType   = "character",
            ContactId   = c.ContactId,
            ContactType = c.ContactType,
            Standing    = c.Standing,
            IsWatched   = c.IsWatched ?? false,
            IsBlocked   = c.IsBlocked ?? false,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchKillMailsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAllPagesAsync<EsiKillMailRef>(charId,
            $"characters/{charId}/killmails/recent/", ct);
        if (!r.IsSuccess) return FromResult(r);

        var existingIds = await db.EsiKillMailRefs
            .Where(k => k.OwnerId == charId && k.OwnerType == "character")
            .Select(k => k.KillMailId)
            .ToHashSetAsync(ct);

        var newRefs = r.Data!
            .Where(k => !existingIds.Contains(k.KillMailId))
            .Select(k => new KillMailRef
            {
                OwnerId      = charId,
                OwnerType    = "character",
                KillMailId   = k.KillMailId,
                KillMailHash = k.KillMailHash,
            });

        db.EsiKillMailRefs.AddRange(newRefs);
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchPlanetsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<List<EsiPlanetaryColony>>(charId,
            $"characters/{charId}/planets/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiPlanetaryColonies.Where(p => p.CharacterId == charId).ExecuteDeleteAsync(ct);
        db.EsiPlanetaryColonies.AddRange(r.Data!.Select(p => new PlanetaryColony
        {
            CharacterId   = charId,
            PlanetId      = p.PlanetId,
            PlanetType    = p.PlanetType,
            SolarSystemId = p.SolarSystemId,
            LastUpdate    = p.LastUpdate,
            NumPins       = p.NumPins,
            UpgradeLevel  = p.UpgradeLevel,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchAgentResearchAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<List<EsiAgentResearch>>(charId,
            $"characters/{charId}/agents_research/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiAgentResearch.Where(a => a.CharacterId == charId).ExecuteDeleteAsync(ct);
        db.EsiAgentResearch.AddRange(r.Data!.Select(a => new AgentResearch
        {
            CharacterId     = charId,
            AgentId         = a.AgentId,
            SkillTypeId     = a.SkillTypeId,
            StartedAt       = a.StartedAt,
            PointsPerDay    = a.PointsPerDay,
            RemainderPoints = a.RemainderPoints,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchLoyaltyPointsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<List<EsiLoyaltyPoint>>(charId,
            $"characters/{charId}/loyalty/points/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiLoyaltyPoints.Where(l => l.CharacterId == charId).ExecuteDeleteAsync(ct);
        db.EsiLoyaltyPoints.AddRange(r.Data!.Select(l => new LoyaltyPoint
        {
            CharacterId   = charId,
            CorporationId = l.CorporationId,
            Points        = l.LoyaltyPoints,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchMedalsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<List<EsiMedalData>>(charId,
            $"characters/{charId}/medals/", ct);
        if (!r.IsSuccess) return FromResult(r);

        // Replace all medals for this character on each refresh
        await db.EsiMedals.Where(m => m.CharacterId == charId).ExecuteDeleteAsync(ct);
        db.EsiMedals.AddRange(r.Data!.Select(m => new CharacterMedal
        {
            CharacterId   = charId,
            MedalId       = m.MedalId,
            CorporationId = m.CorporationId,
            IssuerId      = m.IssuerId,
            Date          = m.Date,
            Reason        = m.Reason,
            Status        = m.Status,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchStandingsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<List<EsiStandingData>>(charId,
            $"characters/{charId}/standings/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiStandings
            .Where(s => s.OwnerId == charId && s.OwnerType == "character")
            .ExecuteDeleteAsync(ct);

        db.EsiStandings.AddRange(r.Data!.Select(s => new StandingEntry
        {
            OwnerId   = charId,
            OwnerType = "character",
            FromId    = s.FromId,
            FromType  = s.FromType,
            Standing  = s.Standing,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchTitlesAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<List<EsiTitleData>>(charId,
            $"characters/{charId}/titles/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiTitles.Where(t => t.CharacterId == charId).ExecuteDeleteAsync(ct);
        db.EsiTitles.AddRange(r.Data!.Select(t => new CharacterTitle
        {
            CharacterId = charId,
            TitleId     = t.TitleId,
            Name        = t.Name,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchRolesAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<EsiRolesData>(charId,
            $"characters/{charId}/roles/", ct);
        if (!r.IsSuccess) return FromResult(r);

        var oldRoles = await db.EsiRoles.Where(rr => rr.CharacterId == charId)
            .Select(rr => rr.Role).ToListAsync(ct);
        await db.EsiRoles.Where(rr => rr.CharacterId == charId).ExecuteDeleteAsync(ct);

        var roles = new List<CharacterRole>();
        AddRoles(roles, charId, r.Data!.Roles,       "role");
        AddRoles(roles, charId, r.Data.RolesAtHq,    "role_at_hq");
        AddRoles(roles, charId, r.Data.RolesAtBase,  "role_at_base");
        AddRoles(roles, charId, r.Data.RolesAtOther, "role_at_other");

        db.EsiRoles.AddRange(roles);
        await db.SaveChangesAsync(ct);

        // When a character's roles change, re-derive corp endpoint access for every corp that
        // polls under this character's token — a lost role starts skipping, a gained one re-opens.
        var newRoles = roles.Select(rr => rr.Role).ToHashSet();
        if (!newRoles.SetEquals(oldRoles))
        {
            var denied = ComputeDeniedCorpEndpoints(newRoles);
            await db.Corporations.Where(c => c.AuthCharacterId == charId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeniedEndpoints, denied), ct);
        }
        return FromResult(r);
    }

    private static void AddRoles(List<CharacterRole> list, long charId, List<string>? roles, string roleType)
    {
        if (roles is null) return;
        list.AddRange(roles.Select(role => new CharacterRole
        {
            CharacterId = charId,
            Role        = role,
            RoleType    = roleType,
        }));
    }

    private async Task<PollingResult> FetchFittingsAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteAuthAsync<List<EsiFittingData>>(charId,
            $"characters/{charId}/fittings/", ct);
        if (!r.IsSuccess) return FromResult(r);

        // Remove fittings that are no longer present
        var incomingIds = r.Data!.Select(f => f.FittingId).ToHashSet();
        var removedIds  = await db.EsiFittings
            .Where(f => f.CharacterId == charId && !incomingIds.Contains(f.FittingId))
            .Select(f => f.FittingId)
            .ToListAsync(ct);

        if (removedIds.Count > 0)
        {
            await db.EsiFittingItems.Where(i => removedIds.Contains(i.FittingId)).ExecuteDeleteAsync(ct);
            await db.EsiFittings
                .Where(f => f.CharacterId == charId && removedIds.Contains(f.FittingId))
                .ExecuteDeleteAsync(ct);
        }

        // Upsert current fittings
        foreach (var f in r.Data!)
        {
            var existing = await db.EsiFittings
                .FindAsync([charId, f.FittingId], ct);

            if (existing is null)
            {
                db.EsiFittings.Add(new StoredFitting
                {
                    FittingId   = f.FittingId,
                    CharacterId = charId,
                    Name        = f.Name,
                    Description = f.Description,
                    ShipTypeId  = f.ShipTypeId,
                });
            }
            else
            {
                existing.Name        = f.Name;
                existing.Description = f.Description;
                existing.ShipTypeId  = f.ShipTypeId;
            }

            // Replace items for this fitting
            await db.EsiFittingItems.Where(i => i.FittingId == f.FittingId).ExecuteDeleteAsync(ct);
            db.EsiFittingItems.AddRange(f.Items.Select(item => new FittingItem
            {
                FittingId = f.FittingId,
                TypeId    = item.TypeId,
                Flag      = item.Flag,
                Quantity  = item.Quantity,
            }));
        }

        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    // ── Eve Mail ─────────────────────────────────────────────────────────────

    private async Task<PollingResult> FetchMailAsync(long charId, AppDbContext db, CancellationToken ct)
    {
        var headersResult = await _mailService.FetchHeadersAsync(charId, db, ct);
        if (!headersResult.Success) return headersResult;
        return await _mailService.FetchLabelsAsync(charId, db, ct);
    }

    // ── Character endpoint registry ──────────────────────────────────────────

    private List<EndpointDef> BuildEndpoints() => [
        new("char.skills",          120,   900,  FetchSkillsAsync),
        new("char.skillqueue",      120,   900,  FetchSkillQueueAsync),
        new("char.wallet.balance",  120,   900,  FetchWalletBalanceAsync),
        new("char.wallet.journal",  3600, 7200,  FetchWalletJournalAsync),
        new("char.wallet.txns",     3600, 7200,  FetchWalletTransactionsAsync),
        new("char.industry.jobs",   300,   900,  FetchIndustryJobsAsync),
        new("char.orders.active",   1200,  3600, FetchActiveOrdersAsync),
        new("char.orders.history",  3600,  7200, FetchOrderHistoryAsync),
        new("char.assets",          3600,  7200, FetchAssetsAsync),
        new("char.blueprints",      3600,  7200, FetchBlueprintsAsync),
        new("char.contracts",       300,   900,  FetchContractsAsync),
        new("char.attributes",      120,   3600, FetchAttributesAsync),
        new("char.clones",          120,   3600, FetchClonesAsync),
        new("char.implants",        120,   3600, FetchImplantsAsync),
        new("char.fatigue",         300,   900,  FetchFatigueAsync),
        new("char.mining",          600,   1800, FetchMiningAsync),
        new("char.notifications",   600,   1800, FetchNotificationsAsync),
        new("char.contacts",        300,   1800, FetchContactsAsync),
        new("char.killmails",       300,   900,  FetchKillMailsAsync),
        new("char.planets",         600,   1800, FetchPlanetsAsync),
        new("char.agents_research", 3600,  7200, FetchAgentResearchAsync),
        new("char.loyalty",         3600,  7200, FetchLoyaltyPointsAsync),
        new("char.medals",          3600, 14400, FetchMedalsAsync),
        new("char.standings",       3600,  7200, FetchStandingsAsync),
        new("char.titles",          3600,  7200, FetchTitlesAsync),
        new("char.roles",           3600,  7200, FetchRolesAsync),
        new("char.fittings",        300,   1800, FetchFittingsAsync),
        new("char.mail",            300,    600, FetchMailAsync),
    ];

    // ── Token loading ────────────────────────────────────────────────────────

    private async Task LoadCharacterTokensAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chars = await db.Characters
            .Where(c => c.RefreshToken != "")
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var ch in chars)
            _esi.RegisterCharacter(ch.Id, ch.RefreshToken);
    }

    private async Task LoadCorpTokensAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var corps = await db.Corporations
            .Where(c => c.RefreshToken != "")
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var corp in corps)
            _esi.RegisterCorporation(corp.Id, corp.RefreshToken);
    }

    // ── Corp polling cycle ───────────────────────────────────────────────────

    private async Task RunCorpCycleAsync(CancellationToken ct)
    {
        List<Corporation> corps;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            corps = await db.Corporations
                .Where(c => c.RefreshToken != "")
                .AsNoTracking()
                .ToListAsync(ct);
        }

        if (corps.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        if (Interlocked.Read(ref _errorLimitBlockedUntilTicks) is var bt and > 0 && now.UtcTicks < bt)
            return;

        // Stagger corp tasks — prevents simultaneous bursts at startup.
        await Task.WhenAll(corps.Select(async (corp, i) =>
        {
            if (i > 0) await Task.Delay(i * 300, ct);
            await ProcessCorpAsync(corp, now, ct);
        }));
    }

    private async Task ProcessCorpAsync(Corporation corp, DateTimeOffset now, CancellationToken ct)
    {
        var netWorthDirty = false;

        // Endpoints the auth character has no role to poll — skipped so we stop hammering them.
        var denied = ParseDenied(corp.DeniedEndpoints);

        foreach (var ep in _corpEndpoints)
        {
            ct.ThrowIfCancellationRequested();

            if (denied.Contains(ep.Key))
                continue;

            var callKey = $"{ep.Key}:{corp.Id}:corporation";

            if (_lastCallTimes.TryGetValue(callKey, out var lastCalled) &&
                (now - lastCalled).TotalSeconds < _timerSettings.GetInterval(ep.Key, ep.DefaultIntervalSeconds))
                continue;

            if (_endpointGroups.TryGetValue(ep.Key, out var groupName) &&
                _rateLimits.TryGetValue(groupName, out var gs) &&
                gs.BlockedUntil.HasValue && now < gs.BlockedUntil.Value)
                continue;

            // Re-check global error limit — a parallel task may have tripped it since cycle start.
            if (Interlocked.Read(ref _errorLimitBlockedUntilTicks) is var bt and > 0 && DateTimeOffset.UtcNow.UtcTicks < bt)
                return;

            using var scope  = _scopeFactory.CreateScope();
            var callDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            using var handle = _log.StartCall(corp.Name, ep.Key);

            PollingResult result;
            try
            {
                result = await ep.Handler(corp.Id, callDb, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result = new PollingResult(false, 0, ex.InnerException?.Message ?? ex.Message);
                _errorLogger.Log("EsiPollingService", $"{ep.Key}:{corp.Id}", ex);
            }

            var callTime = DateTimeOffset.UtcNow;
            _lastCallTimes[callKey] = callTime;
            await PersistCallRecordAsync(corp.Id, "corporation", ep.Key, callTime, result.StatusCode, ct);

            UpdateRateLimitState(ep.Key, result);
            handle.Complete(result.Success, result.StatusCode, result.ErrorMessage);

            if (!result.Success && result.StatusCode > 0)
                _errorLogger.Log("EsiPollingService", $"{ep.Key}:{corp.Id}",
                    $"HTTP {result.StatusCode}", result.ErrorMessage);

            // Self-heal: a role-denied 403 means the auth char can't reach this endpoint — record
            // it so this and future cycles skip the call instead of re-tripping the same 403.
            if (IsRoleDenied(result.StatusCode, result.ErrorMessage) && denied.Add(ep.Key))
            {
                corp.DeniedEndpoints = string.Join(',', denied.OrderBy(k => k, StringComparer.Ordinal));
                var csv = corp.DeniedEndpoints;
                await callDb.Corporations.Where(c => c.Id == corp.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeniedEndpoints, csv), ct);
            }

            if (result.Success && s_netWorthCorpEndpoints.Contains(ep.Key))
                netWorthDirty = true;

            await Task.Delay(500, ct);
        }

        if (netWorthDirty)
            _ = _netWorth.RecalculateAsync(corp.Id, "corporation", ct);
    }

    // ── Corp endpoint handlers ───────────────────────────────────────────────

    private async Task<PollingResult> FetchCorpWalletBalancesAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAuthAsync<List<EsiCorpWalletBalance>>(
            corpId, $"corporations/{corpId}/wallets/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiWalletBalances
            .Where(b => b.OwnerId == corpId && b.OwnerType == "corporation")
            .ExecuteDeleteAsync(ct);

        db.EsiWalletBalances.AddRange(r.Data!.Select(b => new CharacterWalletBalance
        {
            OwnerId   = corpId,
            OwnerType = "corporation",
            Division  = b.Division,
            Balance   = (decimal)b.Balance,
            UpdatedAt = DateTimeOffset.UtcNow,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpDivisionsAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAuthAsync<EsiCorpDivisionsResponse>(
            corpId, $"corporations/{corpId}/divisions/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiCorpDivisions
            .Where(d => d.CorporationId == corpId)
            .ExecuteDeleteAsync(ct);

        var entries = new List<CorpDivision>();
        foreach (var w in r.Data!.Wallet ?? [])
            entries.Add(new CorpDivision { CorporationId = corpId, Division = w.Division, DivisionType = "wallet", Name = w.Name ?? "" });
        foreach (var h in r.Data.Hangar ?? [])
            entries.Add(new CorpDivision { CorporationId = corpId, Division = h.Division, DivisionType = "hangar", Name = h.Name ?? "" });

        db.EsiCorpDivisions.AddRange(entries);
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpWalletJournalAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        // ESI IDs are unique per corp across divisions; load once and share.
        // Query all corp journal IDs up front to prevent change-tracker conflicts on the shared PK.
        var existingIds = await db.EsiWalletJournal
            .Where(e => e.OwnerId == corpId && e.OwnerType == "corporation")
            .Select(e => e.EsiId)
            .ToHashSetAsync(ct);

        PollingResult? lastResult = null;

        for (int division = 1; division <= 7; division++)
        {
            bool fullScan   = !await IsBackfillCompleteAsync(db, corpId, "corporation", "journal", division, ct);
            bool interrupted = false, reachedEnd = false;

            for (int page = 1; ; page++)
            {
                ct.ThrowIfCancellationRequested();
                var r = await _esi.ExecuteCorpAuthAsync<List<EsiWalletJournalEntry>>(
                    corpId, $"corporations/{corpId}/wallets/{division}/journal/", ct, page: page);
                lastResult = FromResult(r);
                if (!r.IsSuccess || r.Data is null) { interrupted = true; break; }
                if (r.Data.Count == 0) { reachedEnd = true; break; }

                var newEntries = r.Data
                    .DistinctBy(e => e.Id)
                    .Where(e => !existingIds.Contains(e.Id))
                    .Select(e => new WalletJournalEntry
                    {
                        EsiId          = e.Id,
                        OwnerId        = corpId,
                        OwnerType      = "corporation",
                        Division       = division,
                        Date           = e.Date,
                        RefType        = e.RefType,
                        FirstPartyId   = e.FirstPartyId,
                        SecondPartyId  = e.SecondPartyId,
                        Amount         = (decimal)(e.Amount ?? 0),
                        Balance        = (decimal)(e.Balance ?? 0),
                        Description    = e.Description,
                        Reason         = e.Reason,
                        Tax            = e.Tax.HasValue ? (decimal)e.Tax.Value : null,
                        TaxReceiverId  = e.TaxReceiverId,
                        ContextId      = e.ContextId,
                        ContextIdType  = e.ContextIdType,
                    })
                    .ToList();

                if (newEntries.Count > 0)
                {
                    db.EsiWalletJournal.AddRange(newEntries);
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                    foreach (var e in newEntries) existingIds.Add(e.EsiId);
                }

                if (!fullScan && newEntries.Count == 0) { reachedEnd = true; break; }
                if (page >= r.TotalPages) { reachedEnd = true; break; }
            }

            if (interrupted)
                await SetBackfillCompleteAsync(db, corpId, "corporation", "journal", division, false, ct);
            else if (reachedEnd && fullScan)
                await SetBackfillCompleteAsync(db, corpId, "corporation", "journal", division, true, ct);
        }

        return lastResult ?? new PollingResult(true, 200);
    }

    private async Task<PollingResult> FetchCorpWalletTransactionsAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        // Corp wallet transactions use from_id cursor pagination per division, same as character.
        var existingIds = await db.EsiWalletTransactions
            .Where(t => t.OwnerId == corpId && t.OwnerType == "corporation")
            .Select(t => t.TransactionId)
            .ToHashSetAsync(ct);

        PollingResult? lastResult = null;

        for (int division = 1; division <= 7; division++)
        {
            bool fullScan   = !await IsBackfillCompleteAsync(db, corpId, "corporation", "transactions", division, ct);
            bool interrupted = false, reachedEnd = false;
            long? fromId = null;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var path = fromId.HasValue
                    ? $"corporations/{corpId}/wallets/{division}/transactions/?from_id={fromId.Value}"
                    : $"corporations/{corpId}/wallets/{division}/transactions/";

                var r = await _esi.ExecuteCorpAuthAsync<List<EsiWalletTransaction>>(corpId, path, ct);
                lastResult = FromResult(r);
                if (!r.IsSuccess || r.Data is null) { interrupted = true; break; }
                if (r.Data.Count == 0) { reachedEnd = true; break; }

                var newEntries = r.Data
                    .DistinctBy(t => t.TransactionId)
                    .Where(t => !existingIds.Contains(t.TransactionId))
                    .Select(t => new WalletTransaction
                    {
                        TransactionId = t.TransactionId,
                        OwnerId       = corpId,
                        OwnerType     = "corporation",
                        Division      = division,
                        Date          = t.Date,
                        ClientId      = t.ClientId,
                        LocationId    = t.LocationId,
                        Quantity      = t.Quantity,
                        TypeId        = t.TypeId,
                        UnitPrice     = (decimal)t.UnitPrice,
                        IsBuy         = t.IsBuy,
                        IsPersonal    = t.IsPersonal,
                        JournalRefId  = t.JournalRefId,
                    })
                    .ToList();

                if (newEntries.Count > 0)
                {
                    db.EsiWalletTransactions.AddRange(newEntries);
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                    foreach (var e in newEntries) existingIds.Add(e.TransactionId);
                }

                if (r.Data.Count < 2500) { reachedEnd = true; break; }
                if (!fullScan && newEntries.Count == 0) { reachedEnd = true; break; }

                fromId = r.Data.Min(t => t.TransactionId);
            }

            if (interrupted)
                await SetBackfillCompleteAsync(db, corpId, "corporation", "transactions", division, false, ct);
            else if (reachedEnd && fullScan)
                await SetBackfillCompleteAsync(db, corpId, "corporation", "transactions", division, true, ct);
        }

        return lastResult ?? new PollingResult(true, 200);
    }

    private async Task<PollingResult> FetchCorpIndustryJobsAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiIndustryJob>(
            corpId, $"corporations/{corpId}/industry/jobs/?include_completed=true", ct);
        if (!r.IsSuccess) return FromResult(r);

        var existingMap = (await db.EsiIndustryJobs
            .Where(j => j.OwnerId == corpId && j.OwnerType == "corporation")
            .ToListAsync(ct))
            .ToDictionary(j => j.JobId);

        foreach (var j in r.Data!)
        {
            if (existingMap.TryGetValue(j.JobId, out var row))
            {
                row.Status               = j.Status;
                row.PauseDate            = j.PauseDate;
                row.CompletedDate        = j.CompletedDate;
                row.CompletedCharacterId = j.CompletedCharacterId;
                row.SuccessfulRuns       = j.SuccessfulRuns;
            }
            else
            {
                db.EsiIndustryJobs.Add(new IndustryJob
                {
                    JobId                = j.JobId,
                    OwnerId              = corpId,
                    OwnerType            = "corporation",
                    InstallerId          = j.InstallerId,
                    FacilityId           = j.FacilityId,
                    StationId            = j.StationId,
                    ActivityId           = j.ActivityId,
                    BlueprintId          = j.BlueprintId,
                    BlueprintTypeId      = j.BlueprintTypeId,
                    BlueprintLocationId  = j.BlueprintLocationId,
                    OutputLocationId     = j.OutputLocationId,
                    Runs                 = j.Runs,
                    Cost                 = (decimal)j.Cost,
                    LicensedRuns         = j.LicensedRuns,
                    Probability          = j.Probability,
                    ProductTypeId        = j.ProductTypeId,
                    Status               = j.Status,
                    Duration             = j.Duration,
                    StartDate            = j.StartDate,
                    EndDate              = j.EndDate,
                    PauseDate            = j.PauseDate,
                    CompletedDate        = j.CompletedDate,
                    CompletedCharacterId = j.CompletedCharacterId,
                    SuccessfulRuns       = j.SuccessfulRuns,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpActiveOrdersAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiMarketOrder>(
            corpId, $"corporations/{corpId}/orders/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiMarketOrders
            .Where(o => o.OwnerId == corpId && o.OwnerType == "corporation" && !o.IsHistory)
            .ExecuteDeleteAsync(ct);

        db.EsiMarketOrders.AddRange(r.Data!.Select(o => new MarketOrder
        {
            OrderId      = o.OrderId,
            OwnerId      = corpId,
            OwnerType    = "corporation",
            TypeId       = o.TypeId,
            LocationId   = o.LocationId,
            VolumeTotal  = o.VolumeTotal,
            VolumeRemain = o.VolumeRemain,
            MinVolume    = o.MinVolume,
            Price        = (decimal)o.Price,
            IsBuyOrder   = o.IsBuyOrder,
            Duration     = o.Duration,
            Issued       = o.Issued,
            Range        = o.Range,
            Escrow       = o.Escrow.HasValue ? (decimal)o.Escrow.Value : null,
            IsCorporation = o.IsCorporation,
            RegionId     = o.RegionId,
            State        = o.State,
            IsHistory    = false,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpOrderHistoryAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiMarketOrder>(
            corpId, $"corporations/{corpId}/orders/history/", ct);
        if (!r.IsSuccess) return FromResult(r);

        var existingIds = await db.EsiMarketOrders
            .Where(o => o.OwnerId == corpId && o.OwnerType == "corporation" && o.IsHistory)
            .Select(o => o.OrderId)
            .ToHashSetAsync(ct);

        db.EsiMarketOrders.AddRange(r.Data!
            .Where(o => !existingIds.Contains(o.OrderId))
            .Select(o => new MarketOrder
            {
                OrderId      = o.OrderId,
                OwnerId      = corpId,
                OwnerType    = "corporation",
                TypeId       = o.TypeId,
                LocationId   = o.LocationId,
                VolumeTotal  = o.VolumeTotal,
                VolumeRemain = o.VolumeRemain,
                MinVolume    = o.MinVolume,
                Price        = (decimal)o.Price,
                IsBuyOrder   = o.IsBuyOrder,
                Duration     = o.Duration,
                Issued       = o.Issued,
                Range        = o.Range,
                State        = o.State,
                IsHistory    = true,
            }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpAssetsAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiAsset>(
            corpId, $"corporations/{corpId}/assets/", ct);
        if (!r.IsSuccess) return FromResult(r);

        var roots = ComputeRootLocations(r.Data!);
        await db.EsiAssets
            .Where(a => a.OwnerId == corpId && a.OwnerType == "corporation")
            .ExecuteDeleteAsync(ct);

        db.EsiAssets.AddRange(r.Data!.Select(a => new CharacterAsset
        {
            ItemId           = a.ItemId,
            OwnerId          = corpId,
            OwnerType        = "corporation",
            TypeId           = a.TypeId,
            LocationId       = a.LocationId,
            LocationType     = a.LocationType,
            LocationFlag     = a.LocationFlag,
            Quantity         = a.Quantity,
            IsSingleton      = a.IsSingleton,
            IsBlueprintCopy  = a.IsBlueprintCopy,
            RootLocationId   = roots[a.ItemId].RootId,
            RootLocationType = roots[a.ItemId].RootType,
        }));
        await db.SaveChangesAsync(ct);

        // Corp offices (OfficeFolder, TypeId=27) and SAG divisions appear as items in the
        // asset list with their own ItemId > 1T. A LocationId > 1T is the actual structure
        // only if it doesn't appear as any ItemId — the chain walks itself:
        // item→CorpSAG→OfficeFolder→structure, and only the structure escapes the filter.
        var ownItemIds = new HashSet<long>(r.Data!
            .Where(a => a.ItemId > 1_000_000_000_000L)
            .Select(a => a.ItemId));
        var structureIds = r.Data!
            .Where(a => a.LocationId > 1_000_000_000_000L)
            .Select(a => a.LocationId)
            .Distinct()
            .Where(id => !ownItemIds.Contains(id))
            .ToList();
        if (structureIds.Count > 0)
        {
            var corp = await db.Corporations.FindAsync([(int)corpId], ct);
            if (corp?.AuthCharacterId > 0)
                await ResolveNewStructureNamesAsync(corp.AuthCharacterId, structureIds, db, ct);
        }

        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpBlueprintsAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiBlueprintData>(
            corpId, $"corporations/{corpId}/blueprints/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiBlueprints
            .Where(b => b.OwnerId == corpId && b.OwnerType == "corporation")
            .ExecuteDeleteAsync(ct);

        db.EsiBlueprints.AddRange(r.Data!.Select(b => new CharacterBlueprint
        {
            ItemId             = b.ItemId,
            OwnerId            = corpId,
            OwnerType          = "corporation",
            TypeId             = b.TypeId,
            LocationId         = b.LocationId,
            LocationFlag       = b.LocationFlag,
            Quantity           = b.Quantity,
            TimeEfficiency     = b.TimeEfficiency,
            MaterialEfficiency = b.MaterialEfficiency,
            Runs               = b.Runs,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpContractsAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiContractData>(
            corpId, $"corporations/{corpId}/contracts/", ct);
        if (!r.IsSuccess) return FromResult(r);

        // Upsert: update existing rows, insert new ones, retain contracts no longer returned.
        var existing = (await db.EsiContracts
                .Where(c => c.OwnerId == corpId && c.OwnerType == "corporation")
                .ToListAsync(ct))
            .ToDictionary(c => c.ContractId);

        foreach (var c in r.Data!)
        {
            if (existing.TryGetValue(c.ContractId, out var row))
            {
                row.Status        = c.Status;
                row.AcceptorId    = c.AcceptorId;
                row.DateAccepted  = c.DateAccepted;
                row.DateCompleted = c.DateCompleted;
                row.DateExpired   = c.DateExpired;
            }
            else
            {
                db.EsiContracts.Add(new ContractRecord
                {
                    ContractId          = c.ContractId,
                    OwnerId             = corpId,
                    OwnerType           = "corporation",
                    IssuerId            = c.IssuerId,
                    IssuerCorporationId = c.IssuerCorporationId,
                    AssigneeId          = c.AssigneeId,
                    AcceptorId          = c.AcceptorId,
                    StartLocationId     = c.StartLocationId,
                    EndLocationId       = c.EndLocationId,
                    Type                = c.Type,
                    Status              = c.Status,
                    Title               = c.Title,
                    ForCorporation      = c.ForCorporation,
                    Availability        = c.Availability,
                    DateIssued          = c.DateIssued,
                    DateExpired         = c.DateExpired,
                    DateAccepted        = c.DateAccepted,
                    DateCompleted       = c.DateCompleted,
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
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpContactsAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiContactData>(
            corpId, $"corporations/{corpId}/contacts/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiContacts
            .Where(c => c.OwnerId == corpId && c.OwnerType == "corporation")
            .ExecuteDeleteAsync(ct);

        db.EsiContacts.AddRange(r.Data!.Select(c => new ContactEntry
        {
            OwnerId     = corpId,
            OwnerType   = "corporation",
            ContactId   = c.ContactId,
            ContactType = c.ContactType,
            Standing    = c.Standing,
            IsWatched   = c.IsWatched ?? false,
            IsBlocked   = c.IsBlocked ?? false,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpKillMailsAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiKillMailRef>(
            corpId, $"corporations/{corpId}/killmails/recent/", ct);
        if (!r.IsSuccess) return FromResult(r);

        var existingIds = await db.EsiKillMailRefs
            .Where(k => k.OwnerId == corpId && k.OwnerType == "corporation")
            .Select(k => k.KillMailId)
            .ToHashSetAsync(ct);

        db.EsiKillMailRefs.AddRange(r.Data!
            .Where(k => !existingIds.Contains(k.KillMailId))
            .Select(k => new KillMailRef
            {
                OwnerId      = corpId,
                OwnerType    = "corporation",
                KillMailId   = k.KillMailId,
                KillMailHash = k.KillMailHash,
            }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpStandingsAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiStandingData>(
            corpId, $"corporations/{corpId}/standings/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiStandings
            .Where(s => s.OwnerId == corpId && s.OwnerType == "corporation")
            .ExecuteDeleteAsync(ct);

        db.EsiStandings.AddRange(r.Data!.Select(s => new StandingEntry
        {
            OwnerId   = corpId,
            OwnerType = "corporation",
            FromId    = s.FromId,
            FromType  = s.FromType,
            Standing  = s.Standing,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    // Fetches and caches names for structure IDs missing from EsiStructureNames or older than 30 days.
    // Tries primaryAuthCharId first, then falls back to any other character in the DB.
    // ESI requires esi-universe.read_structures.v1 and docking access to the structure.
    private async Task ResolveNewStructureNamesAsync(
        long primaryAuthCharId, IReadOnlyList<long> candidateIds, AppDbContext db, CancellationToken ct)
    {
        if (candidateIds.Count == 0) return;

        // Pull only the ID + timestamp columns and do the date comparison in memory —
        // EF Core's SQLite provider cannot translate DateTimeOffset comparisons to SQL.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var fresh = (await db.EsiStructureNames
            .Where(s => candidateIds.Contains(s.StructureId))
            .Select(s => new { s.StructureId, s.PulledAt })
            .ToListAsync(ct))
            .Where(s => s.PulledAt > cutoff)
            .Select(s => s.StructureId)
            .ToHashSet();

        // Also skip structures we recently failed to resolve (no docking rights / gone), so
        // we don't re-poll them every cycle. Retried only after a 30-day backoff, in case
        // access is later granted. (DateTimeOffset filter done in memory — SQLite can't
        // translate it.)
        var failBackoff = DateTimeOffset.UtcNow.AddDays(-30);
        var recentlyFailed = (await db.EsiStructureNameFailures
            .Where(f => candidateIds.Contains(f.StructureId))
            .Select(f => new { f.StructureId, f.FailedAt })
            .ToListAsync(ct))
            .Where(f => f.FailedAt > failBackoff)
            .Select(f => f.StructureId)
            .ToHashSet();

        var toResolve = candidateIds
            .Where(id => !fresh.Contains(id) && !recentlyFailed.Contains(id))
            .Distinct().ToList();
        if (toResolve.Count == 0) return;

        // If a preferred structure-name character is configured, use only that character.
        // Otherwise try the primary auth char first, then fall back to all others.
        var preferredCharId = _prefs.GetLong(AppPreferencesService.StructureNameCharKey, 0);
        var allCharIds = await db.Characters.Select(c => c.Id).ToListAsync(ct);
        var charIds = new List<long>();
        if (preferredCharId > 0)
        {
            charIds.Add(preferredCharId); // single designated character only
        }
        else
        {
            if (primaryAuthCharId > 0) charIds.Add(primaryAuthCharId);
            charIds.AddRange(allCharIds.Where(id => id != primaryAuthCharId));
        }

        if (charIds.Count == 0) return;

        foreach (var structId in toResolve)
        {
            ct.ThrowIfCancellationRequested();

            // Skip if the ESI error limit is currently exhausted — avoid piling on more failures.
            if (_esi.IsErrorLimitBlocked) break;

            var ownerName = charIds.Count > 0 && _charNames.TryGetValue(charIds[0], out var n) ? n : "structure-names";
            using var handle = _log.StartCall(ownerName, $"universe/structures/{structId}/");

            EsiStructureDetail? detail = null;
            EsiCallResult<EsiStructureDetail>? lastResult = null;
            foreach (var charId in charIds)
            {
                lastResult = await _esi.GetStructureAsync(charId, structId, ct);

                // 420 = global error limit exhausted; 429 = per-route limit. Both warrant immediate stop.
                if (lastResult.StatusCode is 420 or 429)
                {
                    var delaySecs = lastResult.RetryAfterSeconds ?? lastResult.ErrorLimitReset ?? 30;
                    await Task.Delay(TimeSpan.FromSeconds(delaySecs), ct);
                    break; // don't try remaining characters — the limit is app-wide
                }

                if (lastResult.IsSuccess && lastResult.Data is not null) { detail = lastResult.Data; break; }
            }

            if (detail is null)
            {
                var sc = lastResult?.StatusCode ?? 0;
                handle.Complete(false, sc, $"all {charIds.Count} character(s) failed");

                // 403 (no docking rights) and 404 (structure gone) are persistent — flag the
                // structure so we stop re-polling it until the backoff expires. 502 is a
                // transient ESI error, so don't flag it.
                if (sc is 403 or 404)
                {
                    var fail = await db.EsiStructureNameFailures.FindAsync([structId], ct);
                    if (fail is null)
                    {
                        fail = new StructureNameFailure { StructureId = structId };
                        db.EsiStructureNameFailures.Add(fail);
                    }
                    fail.FailedAt   = DateTimeOffset.UtcNow;
                    fail.StatusCode = sc;
                    await db.SaveChangesAsync(ct);
                }
                // 403/404/502 are expected ESI responses — not app errors.
                else if (sc is not (420 or 429))
                    _errorLogger.Log(
                        "GetStructureAsync",
                        $"StructureId={structId}",
                        $"HTTP {sc}: all {charIds.Count} character(s) failed",
                        lastResult?.Error);
            }
            else
            {
                handle.Complete(true, lastResult?.StatusCode ?? 200);

                var entry = await db.EsiStructureNames.FindAsync([structId], ct);
                if (entry is null)
                {
                    db.EsiStructureNames.Add(new StructureName
                    {
                        StructureId   = structId,
                        Name          = detail.Name,
                        SolarSystemId = detail.SolarSystemId,
                        PulledAt      = DateTimeOffset.UtcNow,
                    });
                }
                else
                {
                    entry.Name          = detail.Name;
                    entry.SolarSystemId = detail.SolarSystemId;
                    entry.PulledAt      = DateTimeOffset.UtcNow;
                }

                // Access regained — clear any prior failure flag.
                var oldFail = await db.EsiStructureNameFailures.FindAsync([structId], ct);
                if (oldFail is not null) db.EsiStructureNameFailures.Remove(oldFail);

                await db.SaveChangesAsync(ct);
            }

            // Pace calls to avoid burning through ESI's error rate limit.
            await Task.Delay(200, ct);
        }
    }

    public async Task ForceResolveStructureNamesAsync(CancellationToken ct = default)
    {
        StatusText = "Resolving structure names…";
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // A LocationId > 1T is a real player structure only if it doesn't appear as
            // an ItemId in EsiAssets. Office folders, CorpSAG divisions, ships, and
            // containers all have ItemId > 1T and self-exclude. The chain walks itself:
            // item→CorpSAG→OfficeFolder→structure; only the terminal structure escapes.
            var knownItemIds = new HashSet<long>(await db.EsiAssets
                .Where(a => a.ItemId > 1_000_000_000_000L)
                .Select(a => a.ItemId)
                .ToListAsync(ct));

            var assetStructureIds = (await db.EsiAssets
                .Where(a => a.LocationId > 1_000_000_000_000L)
                .Select(a => a.LocationId)
                .Distinct()
                .ToListAsync(ct))
                .Where(id => !knownItemIds.Contains(id))
                .ToList();

            var corpStructureIds = await db.EsiCorpStructures
                .Select(s => s.StructureId)
                .Distinct()
                .ToListAsync(ct);

            var structureIds = assetStructureIds.Union(corpStructureIds).ToList();

            StatusText = $"Polling: Resolving {structureIds.Count} structure(s)…";
            await ResolveNewStructureNamesAsync(0, structureIds, db, ct);
            StatusText = structureIds.Count == 0
                ? "Polling: No structure IDs found in assets"
                : $"Polling: Structure names resolved ({structureIds.Count} IDs)";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            StatusText = "Polling: Structure name resolve failed";
            _errorLogger.Log("EsiPollingService", "ForceResolveStructureNamesAsync", ex);
        }
    }

    private async Task<PollingResult> FetchCorpStructuresAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiCorpStructureEntry>(
            corpId, $"corporations/{corpId}/structures/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiCorpStructures.Where(s => s.CorporationId == corpId).ExecuteDeleteAsync(ct);
        db.EsiCorpStructures.AddRange(r.Data!.Select(s => new CorpStructure
        {
            CorporationId      = corpId,
            StructureId        = s.StructureId,
            Name               = "",
            TypeId             = s.TypeId,
            SystemId           = s.SystemId,
            ProfileId          = s.ProfileId,
            State              = s.State,
            StateTimerStart    = s.StateTimerStart,
            StateTimerEnd      = s.StateTimerEnd,
            UnanchorsAt        = s.UnanchorsAt,
            FuelExpires        = s.FuelExpires,
            NextReinforceApply = s.NextReinforceApply,
            NextReinforceHour  = s.NextReinforceHour,
            ReinforceHour      = s.ReinforceHour,
        }));
        await db.SaveChangesAsync(ct);

        var corp = await db.Corporations.FindAsync([(int)corpId], ct);
        if (corp?.AuthCharacterId > 0)
        {
            var structureIds = r.Data!.Select(s => s.StructureId).ToList();
            await ResolveNewStructureNamesAsync(corp.AuthCharacterId, structureIds, db, ct);
        }

        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpStarbasesAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiCorpStarbaseEntry>(
            corpId, $"corporations/{corpId}/starbases/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiCorpStarbases.Where(s => s.CorporationId == corpId).ExecuteDeleteAsync(ct);
        db.EsiCorpStarbases.AddRange(r.Data!.Select(s => new CorpStarbase
        {
            CorporationId   = corpId,
            StarbaseId      = s.StarbaseId,
            TypeId          = s.TypeId,
            SystemId        = s.SystemId,
            MoonId          = s.MoonId,
            State           = s.State,
            UnanchorAt      = s.UnanchorAt,
            ReinforcedUntil = s.ReinforcedUntil,
            OnlinedSince    = s.OnlinedSince,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpFacilitiesAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAuthAsync<List<EsiCorpFacilityEntry>>(
            corpId, $"corporations/{corpId}/facilities/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiCorpFacilities.Where(f => f.CorporationId == corpId).ExecuteDeleteAsync(ct);
        db.EsiCorpFacilities.AddRange(r.Data!.Select(f => new CorpFacility
        {
            CorporationId = corpId,
            FacilityId    = f.FacilityId,
            TypeId        = f.TypeId,
            SystemId      = f.SystemId,
            RegionId      = f.RegionId,
            TaxRate       = f.TaxRate,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpMembersAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAuthAsync<List<long>>(
            corpId, $"corporations/{corpId}/members/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiCorpMembers.Where(m => m.CorporationId == corpId).ExecuteDeleteAsync(ct);
        db.EsiCorpMembers.AddRange(r.Data!.Select(charId => new CorpMember
        {
            CorporationId = corpId,
            CharacterId   = charId,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpRolesAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAuthAsync<List<EsiCorpRoleEntry>>(
            corpId, $"corporations/{corpId}/roles/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiCorpMemberRoles.Where(rr => rr.CorporationId == corpId).ExecuteDeleteAsync(ct);

        var rows = new List<CorpMemberRole>();
        foreach (var entry in r.Data!)
        {
            void Add(List<string>? roles, string roleType)
            {
                if (roles is null) return;
                rows.AddRange(roles.Select(role => new CorpMemberRole
                {
                    CorporationId = corpId,
                    CharacterId   = entry.CharacterId,
                    Role          = role,
                    RoleType      = roleType,
                }));
            }
            Add(entry.Roles,          "role");
            Add(entry.RolesAtHq,      "role_at_hq");
            Add(entry.RolesAtBase,    "role_at_base");
            Add(entry.RolesAtOther,   "role_at_other");
        }

        db.EsiCorpMemberRoles.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpTitlesAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAuthAsync<List<EsiCorpTitleEntry>>(
            corpId, $"corporations/{corpId}/titles/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiCorpTitles.Where(t => t.CorporationId == corpId).ExecuteDeleteAsync(ct);
        db.EsiCorpTitles.AddRange(r.Data!.Select(t => new CorpTitle
        {
            CorporationId = corpId,
            TitleId       = t.TitleId,
            Name          = t.Name,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private async Task<PollingResult> FetchCorpMedalsAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiCorpMedalEntry>(
            corpId, $"corporations/{corpId}/medals/", ct);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiCorpMedals.Where(m => m.CorporationId == corpId).ExecuteDeleteAsync(ct);
        db.EsiCorpMedals.AddRange(r.Data!.Select(m => new CorpMedal
        {
            CorporationId = corpId,
            MedalId       = m.MedalId,
            Title         = m.Title,
            Description   = m.Description,
            CreatorId     = m.CreatorId,
            CreatedAt     = m.CreatedAt,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    // New API: no /latest/ prefix — use absolute URL so HttpClient.BaseAddress is bypassed.
    private const string NewApiBase = "https://esi.evetech.net/";

    private async Task<PollingResult> FetchCorpMiningExtractionsAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        // 404 = corp has no active moon extractions (legitimate empty state)
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiCorpMiningExtractionEntry>(
            corpId, $"{NewApiBase}corporation/{corpId}/mining/extractions", ct,
            extraHeaders: s_miningHeaders);
        if (r.StatusCode == 404) return new PollingResult(true, 404, null);
        if (!r.IsSuccess) return FromResult(r);

        await db.EsiCorpMiningExtractions.Where(e => e.CorporationId == corpId).ExecuteDeleteAsync(ct);
        db.EsiCorpMiningExtractions.AddRange(r.Data!.Select(e => new CorpMiningExtraction
        {
            CorporationId       = corpId,
            MoonId              = e.MoonId,
            StructureId         = e.StructureId,
            ExtractionStartTime = e.ExtractionStartTime,
            ChunkArrivalTime    = e.ChunkArrivalTime,
            NaturalDecayTime    = e.NaturalDecayTime,
        }));
        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    private static readonly IReadOnlyDictionary<string, string> s_miningHeaders =
        new Dictionary<string, string> { ["X-Compatibility-Date"] = "2026-06-09" };

    private static string? ExtractConfigType(JsonElement cfg)
    {
        // The API encodes the type as the first (and only) property name of the config object,
        // e.g. { "deliver_item": { ... } } → config type is "deliver_item".
        // Fall back to a "type" string property if that pattern changes.
        if (cfg.ValueKind == JsonValueKind.Object)
        {
            using var en = cfg.EnumerateObject();
            if (en.MoveNext()) return en.Current.Name;
        }
        if (cfg.TryGetProperty("type", out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private async Task<PollingResult> FetchCorpMiningObserversAndLedgerAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        var observersUrl = $"{NewApiBase}corporation/{corpId}/mining/observers";
        var r = await _esi.ExecuteCorpAllPagesAsync<EsiCorpMiningObserverEntry>(
            corpId, observersUrl, ct, extraHeaders: s_miningHeaders);
        if (!r.IsSuccess)
        {
            _errorLogger.Log("EsiPollingService", "FetchCorpMiningObserversAndLedgerAsync",
                new Exception($"GET {observersUrl} → HTTP {r.StatusCode}: {r.Error}"));
            return FromResult(r);
        }

        await db.EsiCorpMiningObservers.Where(o => o.CorporationId == corpId).ExecuteDeleteAsync(ct);
        db.EsiCorpMiningObservers.AddRange(r.Data!.Select(o => new CorpMiningObserver
        {
            CorporationId = corpId,
            ObserverId    = o.ObserverId,
            ObserverType  = o.ObserverType,
            LastUpdated   = o.LastUpdated,
        }));

        foreach (var observer in r.Data!)
        {
            var ledger = await _esi.ExecuteCorpAllPagesAsync<EsiCorpMiningLedgerEntry>(
                corpId, $"{NewApiBase}corporation/{corpId}/mining/observers/{observer.ObserverId}", ct,
                extraHeaders: s_miningHeaders);
            if (!ledger.IsSuccess) continue;

            await db.EsiCorpMiningLedger
                .Where(l => l.CorporationId == corpId && l.ObserverId == observer.ObserverId)
                .ExecuteDeleteAsync(ct);

            var deduped = ledger.Data!
                .GroupBy(l => (l.CharacterId, l.TypeId))
                .Select(g => g.OrderByDescending(l => l.LastUpdated).First());

            db.EsiCorpMiningLedger.AddRange(deduped.Select(l => new CorpMiningLedgerEntry
            {
                CorporationId         = corpId,
                ObserverId            = observer.ObserverId,
                CharacterId           = l.CharacterId,
                TypeId                = l.TypeId,
                Quantity              = l.Quantity,
                RecordedCorporationId = l.RecordedCorporationId,
                LastUpdated           = l.LastUpdated,
            }));
        }

        await db.SaveChangesAsync(ct);
        return FromResult(r);
    }

    // This endpoint uses X-Compatibility-Date header (new ESI versioning) instead of a /v1/ URL prefix.
    private static readonly IReadOnlyDictionary<string, string> s_projectsHeaders =
        new Dictionary<string, string> { ["X-Compatibility-Date"] = "2026-06-09" };

    private async Task<PollingResult> FetchCorpProjectsAsync(long corpId, AppDbContext db, CancellationToken ct)
    {
        // ── Page through project list (limit=100 reduces list-page call count) ──
        var allProjects = new List<EsiCorpProjectEntry>();
        string? beforeCursor = null;
        EsiCallResult<EsiCorpProjectsPage>? lastListResult = null;
        int? rateLimitRemaining = null;

        do
        {
            var listUrl = beforeCursor != null
                ? $"https://esi.evetech.net/corporations/{corpId}/projects?state=All&limit=100&before={Uri.EscapeDataString(beforeCursor)}"
                : $"https://esi.evetech.net/corporations/{corpId}/projects?state=All&limit=100";

            lastListResult = await _esi.ExecuteCorpAuthAsync<EsiCorpProjectsPage>(
                corpId, listUrl, ct, extraHeaders: s_projectsHeaders);

            if (!lastListResult.IsSuccess)
            {
                if (lastListResult.StatusCode == 404)
                    return new PollingResult(true, 404, null);
                _errorLogger.Log("EsiPollingService", $"corp.projects.url:{corpId}",
                    $"HTTP {lastListResult.StatusCode}", lastListResult.Error);
                return FromResult(lastListResult);
            }

            if (lastListResult.RateLimitRemaining.HasValue)
                rateLimitRemaining = lastListResult.RateLimitRemaining;

            if (lastListResult.Data?.Projects != null)
                allProjects.AddRange(lastListResult.Data.Projects);

            beforeCursor = lastListResult.Data?.Cursor?.Before;
        }
        while (beforeCursor != null);

        if (allProjects.Count == 0)
            return FromResult(lastListResult!);

        // ── Load existing rows ─────────────────────────────────────────────
        var existingProjects = await db.EsiCorpProjects
            .Where(p => p.CorporationId == corpId)
            .ToDictionaryAsync(p => p.ProjectId, ct);

        var existingContribs = await db.EsiCorpProjectContributors
            .Where(c => c.CorporationId == corpId)
            .ToDictionaryAsync(c => (c.ProjectId, c.CharacterId), ct);

        // Track the most recent result so the outer UpdateRateLimitState gets fresh group info.
        PollingResult latestResult = FromResult(lastListResult!);

        foreach (var project in allProjects)
        {
            existingProjects.TryGetValue(project.Id, out var row);

            // Always update cheap list-level fields for existing rows — no extra API calls.
            if (row is not null)
            {
                row.Name            = project.Name;
                row.State           = project.State;
                row.LastModified    = project.LastModified;
                row.ProgressCurrent = project.Progress?.Current ?? 0;
                row.ProgressDesired = project.Progress?.Desired ?? 0;
                row.RewardInitial   = project.Reward?.Initial ?? 0;
                row.RewardRemaining = project.Reward?.Remaining ?? 0;
                row.UpdatedAt       = DateTimeOffset.UtcNow;

                // Static = terminal-state project whose detail + contributors were fully fetched.
                // DetailUnavailable = listed but its detail endpoint 404s (not visible to us).
                // Either way the per-project detail/contributor calls are pointless — skip them
                // (cheap list fields above are still kept current).
                if (row.IsStatic || row.DetailUnavailable)
                    continue;
            }

            // Stop spending tokens when approaching the rate-limit group budget (600/15 min).
            // Progress is saved at the end; next cycle skips static entries and continues further.
            if (rateLimitRemaining.HasValue && rateLimitRemaining.Value < 30)
                break;

            // ── Fetch project detail ───────────────────────────────────────
            var detailUrl    = $"https://esi.evetech.net/corporations/{corpId}/projects/{project.Id}";
            var detailResult = await _esi.ExecuteCorpAuthAsync<EsiCorpProjectDetail>(
                corpId, detailUrl, ct, extraHeaders: s_projectsHeaders);

            if (detailResult.RateLimitRemaining.HasValue)
                rateLimitRemaining = detailResult.RateLimitRemaining;
            latestResult = FromResult(detailResult);

            if (!detailResult.IsSuccess)
            {
                if (detailResult.StatusCode is 420 or 429) break; // rate-limited — stop cleanly

                // 404 = the detail endpoint doesn't serve this project (not visible to us). It's
                // listed but perpetually 404s, so mark it DetailUnavailable and stop retrying —
                // otherwise it fires a 404 every cycle and eats into ESI's error limit. Store the
                // list data we do have so the row exists. Other statuses are transient: retry.
                if (detailResult.StatusCode == 404)
                {
                    if (row is null)
                    {
                        row = new CorpProject
                        {
                            CorporationId   = corpId,
                            ProjectId       = project.Id,
                            Name            = project.Name,
                            State           = project.State,
                            LastModified    = project.LastModified,
                            ProgressCurrent = project.Progress?.Current ?? 0,
                            ProgressDesired = project.Progress?.Desired ?? 0,
                            RewardInitial   = project.Reward?.Initial ?? 0,
                            RewardRemaining = project.Reward?.Remaining ?? 0,
                            UpdatedAt       = DateTimeOffset.UtcNow,
                        };
                        db.EsiCorpProjects.Add(row);
                        existingProjects[project.Id] = row;
                    }
                    row.DetailUnavailable = true;
                    continue;
                }

                _errorLogger.Log("EsiPollingService", $"corp.projects.detail:{corpId}",
                    $"HTTP {detailResult.StatusCode} project={project.Id}", detailResult.Error);
                continue; // non-fatal — try the next project
            }

            var detail = detailResult.Data;

            // ── Upsert project row ─────────────────────────────────────────
            if (row is not null)
            {
                row.Description      = detail?.Details?.Description ?? row.Description;
                row.Career           = detail?.Details?.Career ?? row.Career;
                row.Created          = detail?.Details?.Created ?? row.Created;
                row.RewardPerContrib = detail?.Contribution?.RewardPerContribution ?? row.RewardPerContrib;
                row.CreatorId        = detail?.Creator?.Id ?? row.CreatorId;
                row.CreatorName      = detail?.Creator?.Name ?? row.CreatorName;
                if (detail?.Configuration.HasValue == true)
                {
                    var cfg = detail.Configuration.Value;
                    row.ConfigurationJson = cfg.GetRawText();
                    row.ConfigType = ExtractConfigType(cfg);
                    if (row.ConfigType is null)
                        _errorLogger.Log("EsiPollingService", "corp.projects.config",
                            $"project={project.Id} config_json={row.ConfigurationJson}", null);
                }
            }
            else
            {
                row = new CorpProject
                {
                    CorporationId    = corpId,
                    ProjectId        = project.Id,
                    Name             = project.Name,
                    State            = project.State,
                    LastModified     = project.LastModified,
                    ProgressCurrent  = project.Progress?.Current ?? 0,
                    ProgressDesired  = project.Progress?.Desired ?? 0,
                    RewardInitial    = project.Reward?.Initial ?? 0,
                    RewardRemaining  = project.Reward?.Remaining ?? 0,
                    Description      = detail?.Details?.Description ?? "",
                    Career           = detail?.Details?.Career ?? "",
                    Created          = detail?.Details?.Created,
                    RewardPerContrib  = detail?.Contribution?.RewardPerContribution ?? 0,
                    CreatorId         = detail?.Creator?.Id,
                    CreatorName       = detail?.Creator?.Name ?? "",
                    ConfigurationJson = detail?.Configuration.HasValue == true
                                        ? detail.Configuration.Value.GetRawText() : null,
                    ConfigType        = detail?.Configuration.HasValue == true
                                        ? ExtractConfigType(detail.Configuration.Value) : null,
                    UpdatedAt         = DateTimeOffset.UtcNow,
                };
                db.EsiCorpProjects.Add(row);
                existingProjects[project.Id] = row;
            }

            // ── Fetch contributors (paginated) ─────────────────────────────
            string? contribCursor = null;
            bool allContribsFetched = true;

            do
            {
                if (rateLimitRemaining.HasValue && rateLimitRemaining.Value < 30)
                {
                    allContribsFetched = false;
                    break;
                }

                var contribUrl = contribCursor != null
                    ? $"https://esi.evetech.net/corporations/{corpId}/projects/{project.Id}/contributors?before={Uri.EscapeDataString(contribCursor)}"
                    : $"https://esi.evetech.net/corporations/{corpId}/projects/{project.Id}/contributors";

                var cr = await _esi.ExecuteCorpAuthAsync<EsiCorpProjectContributorsPage>(
                    corpId, contribUrl, ct, extraHeaders: s_projectsHeaders);

                if (cr.RateLimitRemaining.HasValue)
                    rateLimitRemaining = cr.RateLimitRemaining;
                latestResult = FromResult(cr);

                if (!cr.IsSuccess)
                {
                    allContribsFetched = false;
                    break;
                }

                if (cr.Data?.Contributors != null)
                {
                    foreach (var c in cr.Data.Contributors)
                    {
                        if (existingContribs.TryGetValue((project.Id, c.Id), out var ec))
                        {
                            ec.Name        = c.Name;
                            ec.Contributed = c.Contributed;
                        }
                        else
                        {
                            var nc = new CorpProjectContributor
                            {
                                CorporationId = corpId,
                                ProjectId     = project.Id,
                                CharacterId   = c.Id,
                                Name          = c.Name,
                                Contributed   = c.Contributed,
                            };
                            db.EsiCorpProjectContributors.Add(nc);
                            existingContribs[(project.Id, c.Id)] = nc;
                        }
                    }
                }

                contribCursor = cr.Data?.Cursor?.Before;
            }
            while (contribCursor != null);

            // Once a terminal-state project has been fully fetched, mark it static so future
            // cycles skip the detail and contributor calls for it entirely.
            if (project.State != "Active" && allContribsFetched)
                row.IsStatic = true;
        }

        await db.SaveChangesAsync(ct);
        return latestResult;
    }

    // ── Corp endpoint registry ───────────────────────────────────────────────

    private List<EndpointDef> BuildCorpEndpoints() => [
        new("corp.wallet.balances",    120,   900,  FetchCorpWalletBalancesAsync),
        new("corp.divisions",          3600, 86400, FetchCorpDivisionsAsync),
        new("corp.wallet.journal",     3600,  7200, FetchCorpWalletJournalAsync),
        new("corp.wallet.txns",        3600,  7200, FetchCorpWalletTransactionsAsync),
        new("corp.industry.jobs",       300,   900, FetchCorpIndustryJobsAsync),
        new("corp.orders.active",      1200,  3600, FetchCorpActiveOrdersAsync),
        new("corp.orders.history",     3600,  7200, FetchCorpOrderHistoryAsync),
        new("corp.assets",             3600,  7200, FetchCorpAssetsAsync),
        new("corp.blueprints",         3600,  7200, FetchCorpBlueprintsAsync),
        new("corp.contracts",           300,   900, FetchCorpContractsAsync),
        new("corp.contacts",            300,  1800, FetchCorpContactsAsync),
        new("corp.killmails",           300,   900, FetchCorpKillMailsAsync),
        new("corp.standings",          3600,  7200, FetchCorpStandingsAsync),
        new("corp.structures",          300,  3600, FetchCorpStructuresAsync),
        new("corp.starbases",          3600,  7200, FetchCorpStarbasesAsync),
        new("corp.facilities",         3600, 86400, FetchCorpFacilitiesAsync),
        new("corp.members",             600,  3600, FetchCorpMembersAsync),
        new("corp.roles",              3600,  7200, FetchCorpRolesAsync),
        new("corp.titles",             3600, 86400, FetchCorpTitlesAsync),
        new("corp.medals",             3600, 86400, FetchCorpMedalsAsync),
        new("corp.projects",             3600,  7200, FetchCorpProjectsAsync),
        new("corp.mining.extractions",   3600,  7200, FetchCorpMiningExtractionsAsync),
        new("corp.mining.observers",     3600,  7200, FetchCorpMiningObserversAndLedgerAsync),
    ];

    // ── Corp endpoint role gating ────────────────────────────────────────────
    // Which in-corp roles grant access to each corp endpoint. A Director can read everything,
    // so Director is treated as a universal grant below. Endpoints absent from this map require
    // no special role (any corp member can read them, e.g. contracts / contacts / members /
    // standings / medals). This lets us skip endpoints the auth character can never poll
    // instead of repeatedly eating 403 "Character does not have required role(s)" errors.
    private static readonly Dictionary<string, string[]> s_corpEndpointRoles = new()
    {
        ["corp.wallet.balances"]    = ["Accountant", "Junior_Accountant"],
        ["corp.divisions"]          = ["Accountant", "Junior_Accountant"],
        ["corp.wallet.journal"]     = ["Accountant", "Junior_Accountant"],
        ["corp.wallet.txns"]        = ["Accountant", "Junior_Accountant"],
        ["corp.industry.jobs"]      = ["Factory_Manager"],
        ["corp.orders.active"]      = ["Accountant", "Trader"],
        ["corp.orders.history"]     = ["Accountant", "Trader"],
        ["corp.assets"]             = ["Director"],
        ["corp.blueprints"]         = ["Director"],
        ["corp.killmails"]          = ["Director"],
        ["corp.structures"]         = ["Station_Manager"],
        ["corp.starbases"]          = ["Config_Starbase_Equipment_Roles"],
        ["corp.facilities"]         = ["Factory_Manager"],
        ["corp.roles"]              = ["Director", "Personnel_Manager"],
        ["corp.titles"]             = ["Director"],
        ["corp.projects"]           = ["Brand_Manager"],
        ["corp.mining.extractions"] = ["Station_Manager"],
        ["corp.mining.observers"]   = ["Accountant"],
    };

    // Build the comma-separated list of corp endpoint keys the given roles cannot access.
    // A Director (or an empty/unknown role set that still contains Director) can access all.
    public static string ComputeDeniedCorpEndpoints(IEnumerable<string> roles)
    {
        var have = roles as HashSet<string> ?? new HashSet<string>(roles);
        if (have.Contains("Director")) return "";

        var denied = s_corpEndpointRoles
            .Where(kv => kv.Value.Length > 0 && !kv.Value.Any(have.Contains))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal);
        return string.Join(',', denied);
    }

    private static HashSet<string> ParseDenied(string? csv) =>
        string.IsNullOrEmpty(csv)
            ? new HashSet<string>()
            : new HashSet<string>(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // A 403 that names a missing role — the endpoint is out of reach for this corp's auth char.
    private static bool IsRoleDenied(int statusCode, string? error) =>
        statusCode == 403 && error is not null && error.Contains("role", StringComparison.OrdinalIgnoreCase);
}
