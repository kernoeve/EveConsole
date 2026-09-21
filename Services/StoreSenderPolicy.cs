using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Who a store will serve: the one rule, applied to a mail's sender and to a web buyer alike.
///
/// <para>"Anyone" serves everyone. "List" serves the entries in <see cref="StoreSender"/>, which
/// may name a character, a corporation or an alliance; a character matches by their own id or
/// through their affiliation, which is looked up when the list needs it. An affiliation ESI
/// cannot produce is not permission.</para>
/// </summary>
public static class StoreSenderPolicy
{
    public static async Task<bool> IsAllowedAsync(
        AppDbContext db, EsiClient esi, Store store, long senderId, CancellationToken ct)
    {
        if (store.SenderPolicy == "Anyone") return true;

        var allowed = await db.StoreSenders.AsNoTracking()
            .Where(s => s.StoreId == store.Id)
            .Select(s => new { s.EntityId, s.EntityType })
            .ToListAsync(ct);
        if (allowed.Count == 0) return false;

        if (allowed.Any(a => a.EntityType == "character" && a.EntityId == senderId)) return true;

        var needsOrg = allowed.Any(a => a.EntityType is "corporation" or "alliance");
        if (!needsOrg) return false;

        var affiliation = await esi.GetAffiliationsAsync([senderId], ct);
        if (affiliation.Count == 0) return false;   // unknown is not permission

        var (_, corpId, allianceId) = affiliation[0];

        return allowed.Any(a =>
            (a.EntityType == "corporation" && a.EntityId == corpId) ||
            (a.EntityType == "alliance"    && allianceId is { } al && a.EntityId == al));
    }
}
