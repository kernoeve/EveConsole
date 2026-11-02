using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>How the pilot or corporation on screen stands in a contract.</summary>
public enum EntityContractRole { Issuer, IssuerCorp, Assignee, Acceptor }

/// <summary>
/// One contract the entity is on, with the other party and the place resolved. The names are as
/// the caches hold them; the Player Entities tab words the rest.
/// </summary>
public sealed record EntityContractData(
    ContractRecord Contract, bool IsPublic, EntityContractRole Role,
    long OtherPartyId, string OtherPartyName, EntityKind OtherPartyKind,
    string Contents, long LocationId, string Location, bool IsStation, string Region);

/// <summary>
/// One market trade the entity made that one of our wallets holds: its own, if it is one of our
/// characters or corporations, or ours with it as the other side. <see cref="TheyBought"/> is from
/// the entity's side either way.
/// </summary>
public sealed record EntityTradeData(
    WalletTransaction Tx, bool TheyBought, string TypeName,
    long OtherPartyId, string OtherPartyName, EntityKind OtherPartyKind,
    string Location);

/// <summary>A month of the corporation taxes one member paid into one of our corporations.</summary>
public sealed record EntityTaxMonth(int Year, int Month, long CorpId, string CorpName, decimal Ratting, decimal Industry);

/// <summary>
/// What the app holds of a pilot's activity over one stretch of their corporation history.
/// The taxes are null where the app holds no wallet journal for that corporation — not ours, so
/// nothing to say, which is not the same as nothing paid.
/// </summary>
public sealed record EntityStintSummary(
    int Kills, int Losses, decimal? Ratting, decimal? Industry,
    int Contracts, decimal ContractIsk, int Trades, decimal TradeIsk);

/// <summary>
/// The Player Entities viewer's Contracts, Market and Corp Taxes tabs, and the summary under its
/// corporation history: what the app's own data says a pilot or corporation did.
///
/// <para>⚠️ Only what something brought in. Public contracts come from the region sweep; trades
/// from our own wallets, so another player's market activity shows only where they traded with
/// us; taxes from our corporations' journals; kills from whatever was stored.</para>
///
/// <para>Dates are compared here rather than in SQL, except for kills: a DateTimeOffset comparison
/// is not one SQLite can be asked for in a LINQ query, and an entity's rows are a few thousand at
/// most once the id has narrowed them.</para>
/// </summary>
public class PlayerActivityService(IDbContextFactory<AppDbContext> dbFactory, CorpActivityService corpActivity)
{
    private static readonly string[] RattingRefTypes  = ["bounty_prizes", "bounty_prize", "ess_escrow_transfer", "daily_goal_payouts"];
    private static readonly string[] IndustryRefTypes = ["industry_job_tax", "manufacturing_tax", "reprocessing_tax"];

    private static bool InRange(DateTimeOffset at, DateTimeOffset? from, DateTimeOffset? thru) =>
        (from is not { } f || at >= f) && (thru is not { } t || at < t);

    // ── Contracts ────────────────────────────────────────────────────────────

    public async Task<List<EntityContractData>> ContractsAsync(
        EntityKind kind, long id, DateTimeOffset? from, DateTimeOffset? thru, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var corp = kind == EntityKind.PlayerCorp;
        var corpId = corp ? (int)id : -1;

        var raw = (await db.EsiContracts.AsNoTracking()
                .Where(c => (corp ? c.IssuerCorporationId == corpId : c.IssuerId == id)
                         || c.AssigneeId == id || c.AcceptorId == id)
                .ToListAsync(ct))
            .Where(c => InRange(c.DateIssued, from, thru))
            .ToList();

        // One row per contract: our own copy where there is one — it knows who accepted, and how it
        // ended — else the public listing.
        var records = raw.GroupBy(c => c.ContractId)
            .Select(g => g.FirstOrDefault(c => c.OwnerType != "public") ?? g.First())
            .OrderByDescending(c => c.DateIssued)
            .ToList();
        if (records.Count == 0) return [];

        var ids   = records.Select(c => c.ContractId).ToList();
        var items = await db.EsiContractItems.AsNoTracking()
            .Where(i => ids.Contains(i.ContractId)).ToListAsync(ct);
        var byContract = items.GroupBy(i => i.ContractId).ToDictionary(g => g.Key, g => g.ToList());
        var typeIds    = items.Select(i => i.TypeId).Distinct().ToList();
        var typeNames  = typeIds.Count == 0 ? [] : await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId)).ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);
        string TypeName(int t) => SdeNames.Type(t, typeNames.GetValueOrDefault(t, string.Format(AssetsText.FallbackTypeName, t)));

        string Contents(ContractRecord c)
        {
            if (c.Title?.Trim() is { Length: > 0 } title) return title;
            var lines = byContract.GetValueOrDefault(c.ContractId) ?? [];
            var named = lines.Where(i => i.IsIncluded).GroupBy(i => i.TypeId)
                .Select(g => $"{TypeName(g.Key)} ×{g.Sum(i => i.Quantity):N0}").ToList();
            if (named.Count <= 2) return string.Join(", ", named);
            return string.Format(MarketText.ContractContentsAndMore, string.Join(", ", named.Take(2)), named.Count - 2);
        }

        EntityContractRole RoleOf(ContractRecord c) =>
            c.AcceptorId == id ? EntityContractRole.Acceptor
          : c.AssigneeId == id && (corp ? c.IssuerCorporationId != corpId : c.IssuerId != id) ? EntityContractRole.Assignee
          : corp && !(c.ForCorporation || c.IssuerId == id) ? EntityContractRole.IssuerCorp
          : EntityContractRole.Issuer;

        // The other side: whoever issued it, for one the entity accepted or was offered; else
        // whoever accepted it, else whoever it was offered to.
        long OtherOf(ContractRecord c, EntityContractRole role) => role switch
        {
            EntityContractRole.Acceptor or EntityContractRole.Assignee
                => c.ForCorporation ? c.IssuerCorporationId : c.IssuerId,
            _   => c.AcceptorId is { } a && a > 0 ? a
                 : c.AssigneeId is { } s && s > 0 && s != id ? s : 0,
        };

        var roles  = records.ToDictionary(c => c.ContractId, RoleOf);
        var others = records.ToDictionary(c => c.ContractId, c => OtherOf(c, roles[c.ContractId]));
        var (names, kinds) = await PartiesAsync(db, others.Values, ct);
        var places = await PlacesAsync(db, records.Select(c => c.StartLocationId ?? 0), ct);
        var regionIds = records.Select(c => c.RegionId).Concat(places.Values.Select(p => p.RegionId))
            .Where(r => r > 0).Distinct().ToList();
        var regions = regionIds.Count == 0 ? [] : await db.SdeRegions.AsNoTracking()
            .Where(r => regionIds.Contains(r.RegionId)).ToDictionaryAsync(r => r.RegionId, r => r.Name, ct);

        return records.Select(c =>
        {
            var other  = others[c.ContractId];
            var place  = places.GetValueOrDefault(c.StartLocationId ?? 0);
            var region = c.RegionId > 0 ? c.RegionId : place?.RegionId ?? 0;
            return new EntityContractData(
                c, c.OwnerType == "public", roles[c.ContractId],
                other, other > 0 ? names.GetValueOrDefault(other, "") : "", kinds.GetValueOrDefault(other, EntityKind.Pilot),
                Contents(c), c.StartLocationId ?? 0, place?.Name ?? "", place?.IsStation ?? false,
                regions.TryGetValue(region, out var rn) ? SdeNames.Region(region, rn) : "");
        }).ToList();
    }

    // ── Market ───────────────────────────────────────────────────────────────

    public async Task<List<EntityTradeData>> TradesAsync(
        EntityKind kind, long id, DateTimeOffset? from, DateTimeOffset? thru, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ownerType = kind == EntityKind.PlayerCorp ? "corporation" : "character";

        var txs = (await db.EsiWalletTransactions.AsNoTracking()
                .Where(t => (t.OwnerId == id && t.OwnerType == ownerType) || t.ClientId == id)
                .ToListAsync(ct))
            .Where(t => InRange(t.Date, from, thru))
            // A corporation's trade can sit in its wallet and in the character's who made it.
            .DistinctBy(t => t.TransactionId)
            .OrderByDescending(t => t.Date)
            .ToList();
        if (txs.Count == 0) return [];

        // In its own wallet the other side is the client; in ours it is us.
        bool Own(WalletTransaction t) => t.OwnerId == id && t.OwnerType == ownerType;
        long OtherOf(WalletTransaction t) => Own(t) ? t.ClientId : t.OwnerId;

        var typeIds   = txs.Select(t => t.TypeId).Distinct().ToList();
        var typeNames = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId)).ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);
        var (names, kinds) = await PartiesAsync(db, txs.Select(OtherOf), ct);
        var places = await PlacesAsync(db, txs.Select(t => t.LocationId), ct);

        return txs.Select(t =>
        {
            var other = OtherOf(t);
            return new EntityTradeData(
                t, Own(t) ? t.IsBuy : !t.IsBuy,
                SdeNames.Type(t.TypeId, typeNames.GetValueOrDefault(t.TypeId, string.Format(AssetsText.FallbackTypeName, t.TypeId))),
                other, names.GetValueOrDefault(other, ""), kinds.GetValueOrDefault(other, EntityKind.Pilot),
                places.GetValueOrDefault(t.LocationId)?.Name ?? "");
        }).ToList();
    }

    // ── Corporation taxes ────────────────────────────────────────────────────

    /// <summary>
    /// The ratting and industry tax a pilot paid into any corporation whose journal we hold, by
    /// month — the same entries, attributed the same way, as Corp Activity's tax payer lists: the
    /// ratter is the second party of a bounty payout, the builder the first party of a job tax.
    /// </summary>
    public async Task<List<EntityTaxMonth>> TaxesAsync(long pilotId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.EsiWalletJournal.AsNoTracking()
            .Where(j => j.OwnerType == "corporation"
                     && ((j.SecondPartyId == pilotId && RattingRefTypes.Contains(j.RefType))
                      || (j.FirstPartyId  == pilotId && IndustryRefTypes.Contains(j.RefType))))
            .Select(j => new { j.OwnerId, j.Date, j.RefType, j.Amount })
            .ToListAsync(ct);
        // ⚠️ Positive amounts here, not in the query: SQLite cannot compare a decimal column.
        rows = rows.Where(r => r.Amount > 0).ToList();
        if (rows.Count == 0) return [];

        var corpNames = await corpActivity.ResolveNamesAsync(rows.Select(r => r.OwnerId).Distinct(), ct);
        return rows
            .GroupBy(r => (r.Date.UtcDateTime.Year, r.Date.UtcDateTime.Month, r.OwnerId))
            .Select(g => new EntityTaxMonth(
                g.Key.Year, g.Key.Month, g.Key.OwnerId,
                corpNames.GetValueOrDefault(g.Key.OwnerId, string.Format(CorpText.FallbackCorporationName, g.Key.OwnerId)),
                g.Where(r => RattingRefTypes.Contains(r.RefType)).Sum(r => r.Amount),
                g.Where(r => IndustryRefTypes.Contains(r.RefType)).Sum(r => r.Amount)))
            .OrderByDescending(m => m.Year).ThenByDescending(m => m.Month).ThenBy(m => m.CorpName)
            .ToList();
    }

    // ── A stretch of corporation history ─────────────────────────────────────

    public async Task<EntityStintSummary> StintSummaryAsync(
        long pilotId, long corpId, DateTimeOffset start, DateTimeOffset? end, CancellationToken ct = default)
    {
        var until = end ?? DateTimeOffset.UtcNow.AddDays(1);

        int kills, losses;
        decimal? ratting = null, industry = null;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // ⚠️ Passed as values: KillMailTime is a timestamptz on a server, and a date-shaped
            // string is text to it. Same as KillmailBrowserService.
            object[] args = [pilotId, start.ToUniversalTime(), until.ToUniversalTime()];
#pragma warning disable EF1002 // structure is fixed; the values are parameters
            losses = await db.Database.SqlQueryRaw<int>("""
                SELECT CAST(COUNT(*) AS INTEGER) AS "Value" FROM "KillMailDetails" d
                WHERE d."VictimCharId" = @p0 AND d."KillMailTime" >= @p1 AND d."KillMailTime" < @p2
                """, args).SingleAsync(ct);
            kills = await db.Database.SqlQueryRaw<int>("""
                SELECT CAST(COUNT(*) AS INTEGER) AS "Value" FROM "KillMailDetails" d
                WHERE d."KillMailTime" >= @p1 AND d."KillMailTime" < @p2
                  AND EXISTS (SELECT 1 FROM "KillMailAttackers" a WHERE a."KillMailId" = d."KillMailId" AND a."CharacterId" = @p0)
                """, args).SingleAsync(ct);
#pragma warning restore EF1002

            var heldJournal = await db.EsiWalletJournal.AsNoTracking()
                .AnyAsync(j => j.OwnerId == corpId && j.OwnerType == "corporation", ct);
            if (heldJournal)
            {
                var taxes = (await db.EsiWalletJournal.AsNoTracking()
                        .Where(j => j.OwnerId == corpId && j.OwnerType == "corporation"
                                 && ((j.SecondPartyId == pilotId && RattingRefTypes.Contains(j.RefType))
                                  || (j.FirstPartyId  == pilotId && IndustryRefTypes.Contains(j.RefType))))
                        .Select(j => new { j.Date, j.RefType, j.Amount })
                        .ToListAsync(ct))
                    .Where(j => j.Amount > 0 && InRange(j.Date, start, until))
                    .ToList();
                ratting  = taxes.Where(j => RattingRefTypes.Contains(j.RefType)).Sum(j => j.Amount);
                industry = taxes.Where(j => IndustryRefTypes.Contains(j.RefType)).Sum(j => j.Amount);
            }
        }

        var contracts = await ContractsAsync(EntityKind.Pilot, pilotId, start, until, ct);
        var trades    = await TradesAsync(EntityKind.Pilot, pilotId, start, until, ct);
        return new EntityStintSummary(
            kills, losses, ratting, industry,
            contracts.Count, contracts.Sum(c => c.Contract.Price + c.Contract.Reward),
            trades.Count, trades.Sum(t => t.Tx.UnitPrice * t.Tx.Quantity));
    }

    // ── Shared lookups ───────────────────────────────────────────────────────

    /// <summary>Names for the other parties, and whether each is a pilot or a corporation — the
    /// name cache says, and our own characters are pilots; otherwise the id range.</summary>
    private async Task<(Dictionary<long, string> Names, Dictionary<long, EntityKind> Kinds)> PartiesAsync(
        AppDbContext db, IEnumerable<long> ids, CancellationToken ct)
    {
        var list  = ids.Where(i => i > 0).Distinct().ToList();
        var names = await corpActivity.ResolveNamesAsync(list, ct);
        var cats  = list.Count == 0 ? [] : await db.UniverseNames.AsNoTracking()
            .Where(u => list.Contains(u.EntityId)).ToDictionaryAsync(u => u.EntityId, u => u.Category, ct);
        var kinds = list.ToDictionary(i => i, i => cats.GetValueOrDefault(i) switch
        {
            "character"   => EntityKind.Pilot,
            "corporation" => i < 2_000_000 ? EntityKind.NpcCorp : EntityKind.PlayerCorp,
            "alliance"    => EntityKind.Alliance,
            _ => i is >= 98_000_000 and < 100_000_000 ? EntityKind.PlayerCorp
               : i is >= 1_000_000 and < 2_000_000    ? EntityKind.NpcCorp
               : EntityKind.Pilot,
        });
        return (names, kinds);
    }

    private sealed record Place(string Name, bool IsStation, int RegionId);

    /// <summary>An NPC station from the SDE, a structure from the name cache, and the region either sits in.</summary>
    private static async Task<Dictionary<long, Place>> PlacesAsync(AppDbContext db, IEnumerable<long> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i > 0).Distinct().ToList();
        if (list.Count == 0) return [];

        var stationIds = list.Where(i => i < 100_000_000L).Select(i => (int)i).ToList();
        var stations   = stationIds.Count == 0 ? [] : await db.SdeStations.AsNoTracking()
            .Where(s => stationIds.Contains(s.StationId))
            .Select(s => new { s.StationId, s.Name, s.SolarSystemId }).ToListAsync(ct);
        var structIds  = list.Where(i => i >= 100_000_000L).ToList();
        var structures = structIds.Count == 0 ? [] : await db.EsiStructureNames.AsNoTracking()
            .Where(s => structIds.Contains(s.StructureId))
            .Select(s => new { s.StructureId, s.Name, s.SolarSystemId }).ToListAsync(ct);

        var systemIds = stations.Select(s => s.SolarSystemId).Concat(structures.Select(s => s.SolarSystemId))
            .Where(s => s > 0).Distinct().ToList();
        var systems = systemIds.Count == 0 ? [] : await db.SdeSolarSystems.AsNoTracking()
            .Where(s => systemIds.Contains(s.SolarSystemId))
            .ToDictionaryAsync(s => s.SolarSystemId, s => s.RegionId, ct);

        var result = new Dictionary<long, Place>();
        foreach (var s in stations)
            result[s.StationId] = new Place(SdeNames.Station(s.StationId, s.Name), true, systems.GetValueOrDefault(s.SolarSystemId));
        foreach (var s in structures)
            result[s.StructureId] = new Place(s.Name, false, systems.GetValueOrDefault(s.SolarSystemId));
        foreach (var i in list.Where(i => !result.ContainsKey(i)))
            result[i] = new Place(i < 100_000_000L ? string.Format(AssetsText.FallbackStationName, i) : AssetsText.LocPlayerStructure, i < 100_000_000L, 0);
        return result;
    }
}
