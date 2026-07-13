using System.Globalization;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;

namespace EveCortex.ViewModels;

public sealed record SalesLoadResult(
    List<SaleRowVm> Rows,
    IReadOnlyList<(long Id, string Name)> Chars,
    IReadOnlyList<(long Id, string Name)> Corps);

// Shared sales loader used by the Sales Tracker and the two Sale Listing tools. Lists market
// sales (wallet transactions) and contract sales (item-exchange contracts sold for ISK), with
// build/market value pulled from TypePriceSnapshots (nearest day).
internal static class SalesQuery
{
    // Market sales: one row per sell transaction. Location = station/structure; buyer = the client.
    private const string MarketSql =
        """
        SELECT t."TransactionId" AS SaleId, t."OwnerId" AS OwnerId, t."OwnerType" AS OwnerType,
               t."Date" AS DateStr, t."TypeId" AS TypeId, t."Quantity" AS Quantity,
               CAST(t."UnitPrice" AS REAL) AS UnitPrice, t."ClientId" AS BuyerId,
               COALESCE((SELECT "Name" FROM "SdeStations"       WHERE "StationId"   = t."LocationId"),
                        (SELECT "Name" FROM "EsiStructureNames" WHERE "StructureId" = t."LocationId")) AS Location
        FROM "EsiWalletTransactions" t
        WHERE t."IsBuy" = 0
          -- A corp trade a character executes is stored under both the character (is_personal=0)
          -- and the corporation. Keep the corp row; drop the character's duplicate.
          AND (t."OwnerType" = 'corporation' OR t."IsPersonal" = 1)
        """;

    // Contract sales: item-exchange contracts finished for ISK, issued BY the tracked owner (so an
    // accepted purchase is excluded). Buyer = the acceptor; location = the items' location.
    private const string ContractSql =
        """
        SELECT c."ContractId" AS SaleId, c."OwnerId" AS OwnerId, c."OwnerType" AS OwnerType,
               c."DateCompleted" AS DateStr, CAST(c."Price" AS REAL) AS Price, COALESCE(c."AcceptorId", 0) AS BuyerId,
               COALESCE((SELECT "Name" FROM "SdeStations"       WHERE "StationId"   = c."StartLocationId"),
                        (SELECT "Name" FROM "EsiStructureNames" WHERE "StructureId" = c."StartLocationId")) AS Location
        FROM "EsiContracts" c
        WHERE c."Type" = 'item_exchange' AND c."Status" = 'finished' AND CAST(c."Price" AS REAL) > 0
          AND ( (c."OwnerType" = 'character'   AND c."IssuerId" = c."OwnerId" AND c."ForCorporation" = 0)
             OR (c."OwnerType" = 'corporation' AND c."IssuerCorporationId" = c."OwnerId") )
        """;

    private const string ContractItemSql =
        """
        SELECT ci."ContractId" AS ContractId, ci."TypeId" AS TypeId, ci."Quantity" AS Quantity
        FROM "EsiContractItems" ci
        JOIN "EsiContracts" c ON c."ContractId" = ci."ContractId"
        WHERE ci."IsIncluded" = 1
          AND c."Type" = 'item_exchange' AND c."Status" = 'finished' AND CAST(c."Price" AS REAL) > 0
          AND ( (c."OwnerType" = 'character'   AND c."IssuerId" = c."OwnerId" AND c."ForCorporation" = 0)
             OR (c."OwnerType" = 'corporation' AND c."IssuerCorporationId" = c."OwnerId") )
        """;

    public static async Task<SalesLoadResult> LoadAsync(
        IDbContextFactory<AppDbContext> dbFactory, CorpActivityService names, AppErrorLogger errorLogger)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var market    = await db.Database.SqlQueryRaw<MarketSaleDto>(MarketSql).ToListAsync();
        var contracts = (await db.Database.SqlQueryRaw<ContractSaleDto>(ContractSql).ToListAsync())
                        .DistinctBy(c => c.SaleId).ToList();
        var citems    = await db.Database.SqlQueryRaw<ContractItemDto>(ContractItemSql).ToListAsync();

        // Item names.
        var typeIds = market.Select(m => m.TypeId).Concat(citems.Select(i => i.TypeId)).Distinct().ToList();
        var typeNames = await db.SdeTypes.AsNoTracking().Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name);

        // Market group two levels up (item → its group [1 up] → that group's parent [2 up]),
        // e.g. Revelation → Amarr → "Standard Dreadnoughts". Used by the Sales Tracker rollups.
        var typeMg = await db.SdeTypes.AsNoTracking().Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.MarketGroupId);
        var mgAll = await db.SdeMarketGroups.AsNoTracking()
            .ToDictionaryAsync(g => g.MarketGroupId, g => new { g.ParentGroupId, g.Name });

        string GroupTwoUp(int typeId)
        {
            if (!typeMg.TryGetValue(typeId, out var mgId) || mgId is null) return "—";
            if (!mgAll.TryGetValue(mgId.Value, out var mg)) return "—";
            if (mg.ParentGroupId is int pid && mgAll.TryGetValue(pid, out var parent)) return parent.Name;
            return mg.Name;   // item's group is already top-level
        }

        // Nearest-day price snapshots for the sold types (resolved in memory — a correlated
        // "nearest date" subquery can't reference the outer sale date in SQLite).
        var snaps = await db.TypePriceSnapshots.AsNoTracking().Where(s => typeIds.Contains(s.TypeId))
            .Select(s => new { s.TypeId, s.Date, s.BuildCost, s.MarketValue }).ToListAsync();
        var snapByType = snaps
            .Select(s => (s.TypeId, Date: ParseDay(s.Date), s.BuildCost, s.MarketValue))
            .GroupBy(s => s.TypeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        (double? Build, double? Market) Snap(int typeId, DateTimeOffset when)
        {
            if (!snapByType.TryGetValue(typeId, out var list) || list.Count == 0) return (null, null);
            var target = when.UtcDateTime.Date;
            var best = list[0]; var bestDist = double.MaxValue;
            foreach (var s in list)
            {
                var d = Math.Abs((s.Date - target).TotalDays);
                if (d < bestDist) { bestDist = d; best = s; }
            }
            return (best.BuildCost, best.MarketValue);
        }

        // Owner names + the personal-corp flag.
        var allChars = await db.Characters.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync();
        var allCorps = await db.Corporations.AsNoTracking().Select(c => new { c.Id, c.Name, c.IsPersonal }).ToListAsync();
        var charNames    = allChars.ToDictionary(c => (long)c.Id, c => c.Name);
        var corpNames    = allCorps.ToDictionary(c => (long)c.Id, c => c.Name);
        var corpPersonal = allCorps.ToDictionary(c => (long)c.Id, c => c.IsPersonal);
        bool IsPersonal(long id, string type) => type == "corporation" && corpPersonal.TryGetValue(id, out var p) && p;

        string OwnerName(long id, string type) => type == "corporation"
            ? (corpNames.TryGetValue(id, out var cn) ? cn : $"Corp {id}")
            : (charNames.TryGetValue(id, out var pn) ? pn : $"Char {id}");
        string TypeName(int id) => typeNames.TryGetValue(id, out var n) ? n : $"Type {id}";

        // Buyer names — external players. Resolve from local caches, fall back to ESI once and
        // persist to the shared UniverseNames cache so later loads stay offline.
        var buyerIds = market.Select(m => m.BuyerId)
            .Concat(contracts.Select(c => c.BuyerId)).Where(id => id > 0).Distinct().ToList();
        var buyerNames = await ResolveBuyersAsync(db, buyerIds, charNames, corpNames, names, errorLogger);
        string BuyerName(long id) => id <= 0 ? "" : (buyerNames.TryGetValue(id, out var n) ? n : id.ToString());

        var itemsByContract = citems.GroupBy(i => i.ContractId).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<SaleRowVm>(market.Count + contracts.Count);

        foreach (var m in market)
        {
            var (bu, mv) = Snap(m.TypeId, ParseDate(m.DateStr));
            rows.Add(new SaleRowVm(
                ParseDate(m.DateStr), "Market", m.OwnerType, m.OwnerId, IsPersonal(m.OwnerId, m.OwnerType),
                OwnerName(m.OwnerId, m.OwnerType), m.Location ?? "", BuyerName(m.BuyerId),
                TypeName(m.TypeId), m.Quantity.ToString("N0"), m.Quantity * m.UnitPrice,
                bu is double b ? b * m.Quantity : null, mv is double v ? v * m.Quantity : null,
                m.TypeId, GroupTwoUp(m.TypeId)));
        }

        foreach (var c in contracts)
        {
            var when = ParseDate(c.DateStr);
            var its  = itemsByContract.TryGetValue(c.SaleId, out var list) ? list : [];
            string namesText, units;
            if (its.Count == 0)      { namesText = "(no items)"; units = ""; }
            else if (its.Count == 1) { namesText = TypeName(its[0].TypeId); units = its[0].Quantity.ToString("N0"); }
            else                     { namesText = $"{TypeName(its[0].TypeId)} +{its.Count - 1} more items"; units = "Multiple"; }
            var build = SumOrNull(its.Select(i => Snap(i.TypeId, when).Build is double b ? b * i.Quantity : (double?)null));
            var mkt   = SumOrNull(its.Select(i => Snap(i.TypeId, when).Market is double m ? m * i.Quantity : (double?)null));
            var firstType = its.Count > 0 ? its[0].TypeId : 0;
            rows.Add(new SaleRowVm(
                when, "Contract", c.OwnerType, c.OwnerId, IsPersonal(c.OwnerId, c.OwnerType),
                OwnerName(c.OwnerId, c.OwnerType), c.Location ?? "", BuyerName(c.BuyerId),
                namesText, units, c.Price, build, mkt,
                firstType, firstType > 0 ? GroupTwoUp(firstType) : "—"));
        }

        return new SalesLoadResult(
            rows.OrderByDescending(r => r.WhenSort).ToList(),
            allChars.Select(c => ((long)c.Id, c.Name)).ToList(),
            allCorps.Select(c => ((long)c.Id, c.Name)).ToList());
    }

    private static async Task<Dictionary<long, string>> ResolveBuyersAsync(
        AppDbContext db, List<long> ids, Dictionary<long, string> chars, Dictionary<long, string> corps,
        CorpActivityService names, AppErrorLogger errorLogger)
    {
        var resolvedNames = new Dictionary<long, string>();
        if (ids.Count == 0) return resolvedNames;

        foreach (var u in await db.UniverseNames.AsNoTracking().Where(u => ids.Contains(u.EntityId)).ToListAsync())
            resolvedNames[u.EntityId] = u.Name;
        foreach (var id in ids)
            if (!resolvedNames.ContainsKey(id) && chars.TryGetValue(id, out var cn)) resolvedNames[id] = cn;
        foreach (var id in ids)
            if (!resolvedNames.ContainsKey(id) && corps.TryGetValue(id, out var on)) resolvedNames[id] = on;

        var missing = ids.Where(id => !resolvedNames.ContainsKey(id)).ToList();
        if (missing.Count == 0) return resolvedNames;

        try
        {
            var resolved = await names.ResolveNamesAsync(missing);
            foreach (var kv in resolved)
            {
                resolvedNames[kv.Key] = kv.Value;
                // INSERT OR IGNORE: the Sales Tracker and Sale Listing tools (and their refresh
                // timers) run SalesQuery concurrently, so two loads can resolve the same buyer at
                // once — a plain Add + SaveChanges then races on the unique EntityId. This makes the
                // shared-cache write idempotent and race-safe.
                await db.Database.ExecuteSqlAsync(
                    $"INSERT OR IGNORE INTO UniverseNames (EntityId, Name, Category) VALUES ({kv.Key}, {kv.Value}, '')");
            }
        }
        catch (Exception ex) { errorLogger.Log("SalesQuery", "ResolveBuyers", ex); }

        return resolvedNames;
    }

    // Sum of the present values; null only when every item lacked a snapshot.
    private static double? SumOrNull(IEnumerable<double?> values)
    {
        double sum = 0; var any = false;
        foreach (var v in values) if (v.HasValue) { sum += v.Value; any = true; }
        return any ? sum : null;
    }

    private static DateTimeOffset ParseDate(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d : DateTimeOffset.MinValue;

    private static DateTime ParseDay(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : DateTime.MinValue;

    private sealed class MarketSaleDto
    {
        public long SaleId { get; set; } public long OwnerId { get; set; } public string OwnerType { get; set; } = "";
        public string DateStr { get; set; } = ""; public int TypeId { get; set; } public int Quantity { get; set; }
        public double UnitPrice { get; set; } public long BuyerId { get; set; } public string? Location { get; set; }
    }
    private sealed class ContractSaleDto
    {
        public long SaleId { get; set; } public long OwnerId { get; set; } public string OwnerType { get; set; } = "";
        public string DateStr { get; set; } = ""; public double Price { get; set; }
        public long BuyerId { get; set; } public string? Location { get; set; }
    }
    private sealed class ContractItemDto
    {
        public long ContractId { get; set; } public int TypeId { get; set; } public long Quantity { get; set; }
    }
}
