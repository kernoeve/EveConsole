using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Services;

public class KillMailService(
    IServiceScopeFactory scopeFactory,
    EsiClient            esi,
    AppErrorLogger       errorLogger)
{
    private const int FetchBatchSize = 200;

    /// <summary>
    /// Gap between kill mail fetches. Named rather than inline so it reads as a deliberate rate
    /// and not a magic number — this loop is the app's heaviest consumer of ESI's error budget,
    /// since it is the only one that walks thousands of ids in sequence.
    /// </summary>
    private const int FetchSpacingMs = 150;

    public async Task FetchMissingAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existingIds = await db.KillMailDetails
            .Select(d => d.KillMailId)
            .ToHashSetAsync(ct);

        var allRefs = await db.EsiKillMailRefs
            .Select(r => new { r.KillMailId, r.KillMailHash })
            .ToListAsync(ct);

        var unfetched = allRefs
            .DistinctBy(r => r.KillMailId)
            .Where(r => !existingIds.Contains(r.KillMailId))
            .Take(FetchBatchSize)
            .ToList();

        if (unfetched.Count == 0) return;

        for (int i = 0; i < unfetched.Count; i++)
        {
            var item = unfetched[i];
            progress?.Report($"Fetching kill mail details ({i + 1} / {unfetched.Count})...");
            if (ct.IsCancellationRequested) break;

            // Ask before spending a request, the way every other caller does. The backlog runs to
            // thousands of ids, so this loop is the app's largest consumer of the error budget and
            // the one most able to exhaust it for everything else.
            if (esi.IsErrorLimitBlocked)
            {
                progress?.Report("Kill mail details paused — ESI error limit reached.");
                break;
            }

            try
            {
                var result = await esi.GetKillMailResultAsync(item.KillMailId, item.KillMailHash, ct);

                // Being refused is not a per-item failure to log and move past. Walking the rest of
                // the batch into the same wall is what turned one rate limit into 607 of them, so
                // the batch ends and the refs wait for the next cycle — they are not going
                // anywhere, and the work resumes exactly where it stopped.
                if (result.StatusCode is 429 or 420)
                {
                    var wait = result.RetryAfterSeconds ?? result.ErrorLimitReset ?? 60;
                    errorLogger.Log("KillMailService", $"KillMail {item.KillMailId}",
                        new InvalidOperationException(
                            $"ESI returned {result.StatusCode}; stopping this batch and waiting {wait}s. "
                          + $"{unfetched.Count - i} of {unfetched.Count} left for the next cycle."));
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 1, 300)), ct);
                    break;
                }

                var full = result.Data;
                if (full == null) continue;

                if (await db.KillMailDetails.AnyAsync(d => d.KillMailId == item.KillMailId, ct))
                    continue;

                var victim = full.Victim;
                db.KillMailDetails.Add(new KillMailDetail
                {
                    KillMailId        = full.KillMailId,
                    KillMailHash      = item.KillMailHash,
                    KillMailTime      = full.KillMailTime,
                    SolarSystemId     = full.SolarSystemId,
                    MoonId            = full.MoonId,
                    WarId             = full.WarId,
                    VictimCharId      = victim?.CharacterId    ?? 0L,
                    VictimCorpId      = victim?.CorporationId  ?? 0L,
                    VictimAllianceId  = victim?.AllianceId,
                    VictimFactionId   = victim?.FactionId,
                    VictimShipTypeId  = victim?.ShipTypeId     ?? 0,
                    VictimDamageTaken = victim?.DamageTaken    ?? 0,
                    VictimPosX        = victim?.Position?.X,
                    VictimPosY        = victim?.Position?.Y,
                    VictimPosZ        = victim?.Position?.Z,
                });

                if (full.Attackers != null)
                    foreach (var a in full.Attackers)
                        db.KillMailAttackers.Add(new KillMailAttacker
                        {
                            KillMailId     = full.KillMailId,
                            CharacterId    = a.CharacterId,
                            CorporationId  = a.CorporationId,
                            AllianceId     = a.AllianceId,
                            FactionId      = a.FactionId,
                            DamageDone     = a.DamageDone,
                            FinalBlow      = a.FinalBlow,
                            SecurityStatus = a.SecurityStatus,
                            ShipTypeId     = a.ShipTypeId,
                            WeaponTypeId   = a.WeaponTypeId,
                        });

                if (victim?.Items != null)
                    foreach (var it in victim.Items)
                        db.KillMailItems.Add(new KillMailItem
                        {
                            KillMailId        = full.KillMailId,
                            Flag              = it.Flag,
                            ItemTypeId        = it.ItemTypeId,
                            QuantityDestroyed = it.QuantityDestroyed,
                            QuantityDropped   = it.QuantityDropped,
                            Singleton         = it.Singleton,
                        });

                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                errorLogger.Log("KillMailService", $"KillMail {item.KillMailId}", ex);
            }
            finally
            {
                // In a finally, because it used to sit at the end of the try — so any failure
                // skipped it and the loop ran flat out precisely when it should have slowed down.
                // A failing request needs pacing more than a succeeding one, not less.
                try { await Task.Delay(FetchSpacingMs, ct); } catch (OperationCanceledException) { }
            }
        }
    }
}
