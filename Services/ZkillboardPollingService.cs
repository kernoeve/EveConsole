using System.Collections.Concurrent;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace EveConsole.Services;

/// <summary>
/// Live "Mine + Corp" kill capture — a lightweight interval poll of zKillboard's
/// per-character/per-corp filtered API, active only while
/// <see cref="ZkillboardSettings.Scope"/> is <see cref="ZkbScope.MineAndCorp"/>
/// (ZkillboardFirehoseService covers the "All kills" scope instead — see that class for
/// why the two scopes use different live mechanisms).
///
/// Only inserts EsiKillMailRefs rows (id+hash) — full-detail hydration is left to the
/// existing KillMailService.FetchMissingAsync, exactly as it already does for
/// ESI-sourced refs. Additive-only: every insert checks for an existing ref first.
///
/// This only catches kills going forward from whenever the app is running — a gap while
/// the app was closed is the automatic backfill's job (ZkillboardBackfillService), not
/// this loop's, so a fresh in-memory poll cursor on every app start is intentional.
/// </summary>
public sealed class ZkillboardPollingService(
    IServiceScopeFactory scopeFactory,
    ZkillboardSettings   settings,
    ZkillboardApiClient  api,
    AppErrorLogger       errorLogger) : ReactiveObject
{
    private const int TickSeconds        = 15;
    private const int InterOwnerDelayMs  = 500;

    private CancellationTokenSource? _cts;
    private Task?                    _runTask;

    // In-memory only — see class remarks on why a gap here is the backfill's job, not this loop's.
    private readonly ConcurrentDictionary<(long OwnerId, string OwnerType), DateTimeOffset> _lastPolled = new();

    private string _statusText = "zKillboard live poll: not started";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts     = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        await _cts.CancelAsync();
        if (_runTask is not null)
            try { await _runTask; } catch (OperationCanceledException) { }

        _cts     = null;
        _runTask = null;
        StatusText = "zKillboard live poll: stopped";
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (settings.Enabled && settings.Scope == ZkbScope.MineAndCorp)
            {
                try
                {
                    await PollDueOwnersAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    StatusText = $"zKillboard live poll: error — {Truncate(ex.Message)}";
                    errorLogger.Log(nameof(ZkillboardPollingService), nameof(RunAsync), ex);
                }
            }
            else
            {
                StatusText = !settings.Enabled
                    ? "zKillboard live poll: disabled"
                    : "zKillboard live poll: idle (All-kills scope uses the firehose instead)";
            }

            await Task.Delay(TimeSpan.FromSeconds(TickSeconds), ct);
        }
    }

    private async Task PollDueOwnersAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var characterIds = await db.Characters.Where(c => c.RefreshToken != "")
            .Select(c => c.Id).ToListAsync(ct);
        var corpIds = await db.Corporations.Where(c => c.RefreshToken != "")
            .Select(c => c.Id).ToListAsync(ct);

        var owners = characterIds.Select(id => (OwnerId: id, OwnerType: "character"))
            .Concat(corpIds.Select(id => (OwnerId: (long)id, OwnerType: "corporation")))
            .ToList();

        if (owners.Count == 0)
        {
            StatusText = "zKillboard live poll: no tracked characters/corps";
            return;
        }

        var now       = DateTimeOffset.UtcNow;
        var addedTotal = 0;
        var polledAny   = false;

        foreach (var owner in owners)
        {
            ct.ThrowIfCancellationRequested();

            _lastPolled.TryGetValue(owner, out var last);
            if (last != default && (now - last).TotalSeconds < settings.PollIntervalSeconds)
                continue;

            polledAny = true;
            var pastSeconds = PastSecondsSince(last == default ? null : last, now);
            var refs = await api.GetKillRefsAsync(owner.OwnerType, owner.OwnerId, pastSeconds, ct);
            _lastPolled[owner] = now;

            if (refs.Count > 0)
                addedTotal += await InsertNewRefsAsync(db, owner.OwnerId, owner.OwnerType, refs, ct);

            await Task.Delay(InterOwnerDelayMs, ct);
        }

        StatusText = addedTotal > 0
            ? $"zKillboard live poll: +{addedTotal} new kill ref(s) across {owners.Count} owner(s)"
            : polledAny
                ? $"zKillboard live poll: watching {owners.Count} owner(s)"
                : $"zKillboard live poll: watching {owners.Count} owner(s) (next check pending)";
    }

    private static async Task<int> InsertNewRefsAsync(
        AppDbContext db, long ownerId, string ownerType,
        List<(int KillmailId, string Hash)> refs, CancellationToken ct)
    {
        var existingIds = await db.EsiKillMailRefs
            .Where(k => k.OwnerId == ownerId && k.OwnerType == ownerType)
            .Select(k => k.KillMailId)
            .ToHashSetAsync(ct);

        var added = 0;
        foreach (var r in refs)
        {
            // Flagged regardless of whether the ref is new — zKillboard returning the kill
            // is the evidence, and a ref we already had is the common case here.
            await ZkillboardKillImportService.MarkSeenOnZkbAsync(db, r.KillmailId, ct);

            if (existingIds.Contains(r.KillmailId)) continue;
            db.EsiKillMailRefs.Add(new KillMailRef
            {
                OwnerId      = ownerId,
                OwnerType    = ownerType,
                KillMailId   = r.KillmailId,
                KillMailHash = r.Hash,
            });
            added++;
        }

        await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>Smallest valid pastSeconds (multiple of 3600, capped at 7 days) that
    /// covers the time since this owner was last polled. First poll for an owner just
    /// looks back one hour — anything older is the backfill's responsibility.</summary>
    private static int PastSecondsSince(DateTimeOffset? last, DateTimeOffset now)
    {
        if (last is null) return 3600;
        var gapSeconds = (now - last.Value).TotalSeconds;
        var rounded    = (int)Math.Ceiling(gapSeconds / 3600.0) * 3600;
        return Math.Clamp(rounded, 3600, 604800);
    }

    private static string Truncate(string s, int max = 80) => s.Length <= max ? s : s[..max];
}
