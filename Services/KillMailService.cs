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
            try
            {
                var full = await esi.GetKillMailAsync(item.KillMailId, item.KillMailHash, ct);
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
                await Task.Delay(150, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                errorLogger.Log("KillMailService", $"KillMail {item.KillMailId}", ex);
            }
        }
    }
}
