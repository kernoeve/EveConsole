using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

public sealed record KillmailListRow(
    int KillMailId, DateTimeOffset KillMailTime,
    int VictimShipTypeId, string ShipName,
    string SystemName, string ConstellationName, string RegionName,
    double SecurityStatus, double TotalIsk,
    long VictimCorpId, long VictimAllianceId,
    string VictimName, string VictimCorp, string VictimAlliance,
    long FbCorpId, long FbAllianceId,
    string FbName, string FbCorp, string FbAlliance);

public sealed record KillmailDetailData(
    int KillMailId, DateTimeOffset KillMailTime,
    string ShipName, string SystemName, string RegionName,
    string VictimName, string VictimCorp, string VictimAlliance,
    int VictimDamageTaken,
    List<(string SlotGroup, List<KillmailItemRow> Items)> SlotGroups,
    List<KillmailAttackerRow> Attackers,
    double DestroyedIsk, double DroppedIsk);

public sealed record KillmailItemRow(
    int TypeId, string TypeName, long QtyDestroyed, long QtyDropped, double EstValue);

public sealed record KillmailAttackerRow(
    string CharName, string CorpName, string AllianceName,
    int DamageDone, bool FinalBlow, string ShipName, string WeaponName,
    long CharacterId, int ShipTypeId, int WeaponTypeId);

public class KillmailBrowserService(
    IDbContextFactory<AppDbContext> dbFactory,
    CorpActivityService corpActivityService)
{
    private const int ListLimit = 2000;

    private sealed class KmDetailRaw
    {
        public int            KillMailId        { get; set; }
        public DateTimeOffset KillMailTime      { get; set; }
        public long           VictimCharId      { get; set; }
        public long           VictimCorpId      { get; set; }
        public long?          VictimAllianceId  { get; set; }
        public int            VictimShipTypeId  { get; set; }
        public int            SolarSystemId     { get; set; }
    }

    private sealed class FbRaw
    {
        public int   KillMailId    { get; set; }
        public long? CharacterId   { get; set; }
        public long? CorporationId { get; set; }
        public long? AllianceId    { get; set; }
    }

    private sealed class IskRaw
    {
        public int    KillMailId { get; set; }
        public double TotalIsk   { get; set; }
    }

    public async Task<List<KillmailListRow>> GetListAsync(long corpId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        // corpId == 0 means "all corps" — no EsiKillMailRefs filter
        List<KmDetailRaw> details;
        List<FbRaw>       fbAttackers;

        if (corpId == 0)
        {
            details = await db.Database.SqlQuery<KmDetailRaw>($"""
                SELECT d."KillMailId", d."KillMailTime",
                       d."VictimCharId", d."VictimCorpId", d."VictimAllianceId",
                       d."VictimShipTypeId", d."SolarSystemId"
                FROM "KillMailDetails" d
                ORDER BY d."KillMailTime" DESC
                LIMIT {ListLimit}
                """).ToListAsync(ct);

            if (details.Count == 0) return [];

            fbAttackers = await db.Database.SqlQuery<FbRaw>($"""
                SELECT a."KillMailId", a."CharacterId", a."CorporationId", a."AllianceId"
                FROM "KillMailAttackers" a
                WHERE a."FinalBlow" = 1
                  AND a."KillMailId" IN (
                    SELECT d."KillMailId" FROM "KillMailDetails" d
                    ORDER BY d."KillMailTime" DESC LIMIT {ListLimit}
                  )
                """).ToListAsync(ct);
        }
        else
        {
            details = await db.Database.SqlQuery<KmDetailRaw>($"""
                SELECT d."KillMailId", d."KillMailTime",
                       d."VictimCharId", d."VictimCorpId", d."VictimAllianceId",
                       d."VictimShipTypeId", d."SolarSystemId"
                FROM "KillMailDetails" d
                JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                    AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
                ORDER BY d."KillMailTime" DESC
                LIMIT {ListLimit}
                """).ToListAsync(ct);

            if (details.Count == 0) return [];

            fbAttackers = await db.Database.SqlQuery<FbRaw>($"""
                SELECT a."KillMailId", a."CharacterId", a."CorporationId", a."AllianceId"
                FROM "KillMailAttackers" a
                WHERE a."FinalBlow" = 1
                  AND a."KillMailId" IN (
                    SELECT d."KillMailId"
                    FROM "KillMailDetails" d
                    JOIN "EsiKillMailRefs" r ON r."KillMailId" = d."KillMailId"
                        AND r."OwnerId" = {corpId} AND r."OwnerType" = 'corporation'
                    ORDER BY d."KillMailTime" DESC
                    LIMIT {ListLimit}
                  )
                """).ToListAsync(ct);
        }
        var fbMap = fbAttackers
            .GroupBy(a => a.KillMailId)
            .ToDictionary(g => g.Key, g => g.First());

        // Collect all entity IDs to resolve
        var entityIds = new HashSet<long>();
        foreach (var d in details)
        {
            if (d.VictimCharId != 0) entityIds.Add(d.VictimCharId);
            if (d.VictimCorpId != 0) entityIds.Add(d.VictimCorpId);
            if (d.VictimAllianceId.HasValue) entityIds.Add(d.VictimAllianceId.Value);
        }
        foreach (var a in fbAttackers)
        {
            if (a.CharacterId.HasValue) entityIds.Add(a.CharacterId.Value);
            if (a.CorporationId.HasValue) entityIds.Add(a.CorporationId.Value);
            if (a.AllianceId.HasValue) entityIds.Add(a.AllianceId.Value);
        }
        var names = await corpActivityService.ResolveNamesAsync(entityIds, ct);

        // Resolve ship/system/region from SDE
        var shipTypeIds = details.Select(d => d.VictimShipTypeId).Distinct().ToList();
        var shipNames   = await db.SdeTypes.AsNoTracking()
            .Where(t => shipTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var systemIds = details.Select(d => d.SolarSystemId).Distinct().ToList();
        var systems   = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => systemIds.Contains(s.SolarSystemId))
            .ToListAsync(ct);
        var systemMap  = systems.ToDictionary(s => s.SolarSystemId);
        var regionIds  = systems.Select(s => s.RegionId).Distinct().ToList();
        var regionMap  = await db.SdeRegions.AsNoTracking()
            .Where(r => regionIds.Contains(r.RegionId))
            .ToDictionaryAsync(r => r.RegionId, r => r.Name, ct);
        var constellationIds = systems.Select(s => s.ConstellationId).Distinct().ToList();
        var constellationMap = await db.SdeConstellations.AsNoTracking()
            .Where(c => constellationIds.Contains(c.ConstellationId))
            .ToDictionaryAsync(c => c.ConstellationId, c => c.Name, ct);

        // ISK calculation — batch query against market prices
        var iskMap = new Dictionary<int, double>();
        var killIds = details.Select(d => d.KillMailId).ToList();
        var defaultSettings = await db.MarketDefaultSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (defaultSettings?.AssetValueConfigId is int configId && killIds.Count > 0)
        {
            var priceCol = (defaultSettings.AssetValuePriceType ?? "Midpoint") switch
            {
                "Buy"  => "BuyPrice",
                "Sell" => "SellPrice",
                _      => "Midpoint"
            };
            var killIdsStr = string.Join(",", killIds);
            var iskSql = $"""
                SELECT i."KillMailId",
                       SUM((COALESCE(i."QuantityDestroyed", 0) + COALESCE(i."QuantityDropped", 0))
                           * COALESCE(p."{priceCol}", 0.0)) AS "TotalIsk"
                FROM "KillMailItems" i
                LEFT JOIN "MarketItemPrices" p ON p."TypeId" = i."ItemTypeId" AND p."ConfigId" = @p0
                WHERE i."KillMailId" IN ({killIdsStr})
                GROUP BY i."KillMailId"
                """;
#pragma warning disable EF1002
            var iskRows = await db.Database.SqlQueryRaw<IskRaw>(iskSql, configId).ToListAsync(ct);
#pragma warning restore EF1002
            iskMap = iskRows.ToDictionary(x => x.KillMailId, x => x.TotalIsk);
        }

        string Res(long? id) => id.HasValue && id.Value != 0 && names.TryGetValue(id.Value, out var n) ? n : "";

        return details.Select(d =>
        {
            fbMap.TryGetValue(d.KillMailId, out var fb);
            systemMap.TryGetValue(d.SolarSystemId, out var sys);
            var regionName        = sys is not null && regionMap.TryGetValue(sys.RegionId, out var rn) ? rn : "";
            var constellationName = sys is not null && constellationMap.TryGetValue(sys.ConstellationId, out var cn) ? cn : "";
            iskMap.TryGetValue(d.KillMailId, out var isk);

            return new KillmailListRow(
                d.KillMailId, d.KillMailTime,
                d.VictimShipTypeId,
                shipNames.TryGetValue(d.VictimShipTypeId, out var sn) ? sn : d.VictimShipTypeId.ToString(),
                sys?.Name ?? d.SolarSystemId.ToString(), constellationName, regionName,
                sys?.Security ?? 0.0, isk,
                d.VictimCorpId, d.VictimAllianceId ?? 0L,
                Res(d.VictimCharId), Res(d.VictimCorpId), Res(d.VictimAllianceId),
                fb?.CorporationId ?? 0L, fb?.AllianceId ?? 0L,
                fb is not null ? Res(fb.CharacterId) : "",
                fb is not null ? Res(fb.CorporationId) : "",
                fb is not null ? Res(fb.AllianceId) : "");
        }).ToList();
    }

    public async Task<KillmailDetailData?> GetDetailAsync(int killMailId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var detail = await db.KillMailDetails.AsNoTracking()
            .FirstOrDefaultAsync(d => d.KillMailId == killMailId, ct);
        if (detail is null) return null;

        var attackers = await db.KillMailAttackers.AsNoTracking()
            .Where(a => a.KillMailId == killMailId)
            .OrderByDescending(a => a.FinalBlow).ThenByDescending(a => a.DamageDone)
            .ToListAsync(ct);

        var items = await db.KillMailItems.AsNoTracking()
            .Where(i => i.KillMailId == killMailId)
            .ToListAsync(ct);

        // Collect all type IDs for name + price lookup
        var typeIds = items.Select(i => i.ItemTypeId)
            .Concat(attackers.Select(a => a.ShipTypeId ?? 0).Where(x => x > 0))
            .Concat(attackers.Select(a => a.WeaponTypeId ?? 0).Where(x => x > 0))
            .Append(detail.VictimShipTypeId)
            .Distinct().ToList();

        var typeNames = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        // Market prices using the asset value config
        var settings = await db.MarketDefaultSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        Dictionary<int, double> prices = [];
        if (settings?.AssetValueConfigId is int configId)
        {
            var priceType = settings.AssetValuePriceType;
            var rawPrices = await db.MarketItemPrices.AsNoTracking()
                .Where(p => p.ConfigId == configId && typeIds.Contains(p.TypeId))
                .ToListAsync(ct);
            prices = rawPrices.ToDictionary(p => p.TypeId, p => priceType switch
            {
                MarketPriceType.Buy  => p.BuyPrice,
                MarketPriceType.Sell => p.SellPrice,
                _                   => p.Midpoint,
            });
        }

        // Resolve entity names
        var entityIds = new HashSet<long>();
        if (detail.VictimCharId != 0) entityIds.Add(detail.VictimCharId);
        if (detail.VictimCorpId != 0) entityIds.Add(detail.VictimCorpId);
        if (detail.VictimAllianceId.HasValue) entityIds.Add(detail.VictimAllianceId.Value);
        foreach (var a in attackers)
        {
            if (a.CharacterId.HasValue) entityIds.Add(a.CharacterId.Value);
            if (a.CorporationId.HasValue) entityIds.Add(a.CorporationId.Value);
            if (a.AllianceId.HasValue) entityIds.Add(a.AllianceId.Value);
        }
        var names = await corpActivityService.ResolveNamesAsync(entityIds, ct);
        string Res(long? id) => id.HasValue && id.Value != 0 && names.TryGetValue(id.Value, out var n) ? n : "";

        // System/region
        var system  = await db.SdeSolarSystems.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SolarSystemId == detail.SolarSystemId, ct);
        var region  = system is not null
            ? await db.SdeRegions.AsNoTracking().FirstOrDefaultAsync(r => r.RegionId == system.RegionId, ct)
            : null;

        // Ship name
        string shipName = typeNames.TryGetValue(detail.VictimShipTypeId, out var vsn) ? vsn : detail.VictimShipTypeId.ToString();

        // Group items by slot, then append ship row at the end (always destroyed)
        double destroyedIsk = 0, droppedIsk = 0;
        var grouped = GroupItemsBySlot(items, typeNames, prices, ref destroyedIsk, ref droppedIsk);
        var shipPrice = prices.TryGetValue(detail.VictimShipTypeId, out var sp) ? sp : 0;
        destroyedIsk += shipPrice;
        grouped.Add(("Ship", [new KillmailItemRow(detail.VictimShipTypeId, shipName, 1, 0, shipPrice)]));

        // Attackers
        var attackerRows = attackers.Select(a => new KillmailAttackerRow(
            Res(a.CharacterId),
            Res(a.CorporationId),
            Res(a.AllianceId),
            a.DamageDone,
            a.FinalBlow,
            a.ShipTypeId.HasValue && typeNames.TryGetValue(a.ShipTypeId.Value, out var asn) ? asn : "",
            a.WeaponTypeId.HasValue && typeNames.TryGetValue(a.WeaponTypeId.Value, out var awn) ? awn : "",
            a.CharacterId  ?? 0,
            a.ShipTypeId   ?? 0,
            a.WeaponTypeId ?? 0
        )).ToList();

        return new KillmailDetailData(
            detail.KillMailId, detail.KillMailTime,
            shipName,
            system?.Name ?? detail.SolarSystemId.ToString(),
            region?.Name ?? "",
            Res(detail.VictimCharId),
            Res(detail.VictimCorpId),
            Res(detail.VictimAllianceId),
            detail.VictimDamageTaken,
            grouped, attackerRows,
            destroyedIsk, droppedIsk);
    }

    private static List<(string SlotGroup, List<KillmailItemRow> Items)> GroupItemsBySlot(
        List<KillMailItem> items,
        Dictionary<int, string> typeNames,
        Dictionary<int, double> prices,
        ref double destroyedIsk, ref double droppedIsk)
    {
        var groups = new Dictionary<string, List<KillmailItemRow>>();
        var order  = new List<string>();

        foreach (var item in items)
        {
            var group = FlagToSlotGroup(item.Flag);
            if (!groups.ContainsKey(group)) { groups[group] = []; order.Add(group); }

            var name     = typeNames.TryGetValue(item.ItemTypeId, out var n) ? n : item.ItemTypeId.ToString();
            var unitPrc  = prices.TryGetValue(item.ItemTypeId, out var p) ? p : 0;
            var qtyDest  = item.QuantityDestroyed ?? 0;
            var qtyDrop  = item.QuantityDropped   ?? 0;
            var valDest  = unitPrc * qtyDest;
            var valDrop  = unitPrc * qtyDrop;

            destroyedIsk += valDest;
            droppedIsk   += valDrop;

            groups[group].Add(new KillmailItemRow(item.ItemTypeId, name, qtyDest, qtyDrop, valDest + valDrop));
        }

        // Return groups in canonical EVE slot order; any unrecognised groups go at the end
        return _slotOrder
            .Where(groups.ContainsKey)
            .Select(g => (g, groups[g]))
            .Concat(groups.Where(kv => !_slotOrder.Contains(kv.Key)).Select(kv => (kv.Key, kv.Value)))
            .ToList();
    }

    private static readonly string[] _slotOrder =
    [
        "High Slots", "Mid Slots", "Low Slots", "Rig Slots",
        "Subsystem Slots", "Fighter Tubes", "Drone Bay", "Ship Hangar", "Cargo Hold", "Other",
    ];

    private static string FlagToSlotGroup(int flag) => flag switch
    {
        >= 11 and <= 18   => "High Slots",
        >= 19 and <= 26   => "Mid Slots",
        >= 27 and <= 34   => "Low Slots",
        >= 92 and <= 99   => "Rig Slots",
        >= 125 and <= 132 => "Subsystem Slots",
        >= 133 and <= 143 => "Fighter Tubes",
        57 or 87          => "Drone Bay",
        88 or 89          => "Ship Hangar",
        5                 => "Cargo Hold",
        _                 => "Other",
    };
}
