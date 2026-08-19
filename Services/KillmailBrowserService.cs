using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

public sealed record KillmailListRow(
    int KillMailId, DateTimeOffset KillMailTime,
    int VictimShipTypeId, string ShipName,
    int SystemId, string SystemName, string ConstellationName, int RegionId, string RegionName,
    double SecurityStatus, double TotalIsk,
    long VictimCharId, long VictimCorpId, long VictimAllianceId,
    string VictimName, string VictimCorp, string VictimAlliance,
    long FbCharId, long FbCorpId, long FbAllianceId,
    string FbName, string FbCorp, string FbAlliance);

public sealed record KillmailDetailData(
    int KillMailId, DateTimeOffset KillMailTime,
    string ShipName, int SystemId, string SystemName, string RegionName, string LocationText,
    long VictimCharId, long VictimCorpId, long VictimAllianceId, int VictimShipTypeId,
    string VictimName, string VictimCorp, string VictimAlliance,
    int VictimDamageTaken,
    List<KillmailSlotGroupRow> SlotGroups,
    List<KillmailAttackerRow> Attackers,
    double DestroyedIsk, double DroppedIsk);

/// <summary>A slot section (High Slots, Cargo Hold, ...). Every group has at least one
/// sub-group — for anything but Cargo Hold there's exactly one with an empty
/// SubGroupName (meaning "don't render a sub-header"); Cargo Hold is split further by
/// market group so a large cargo list (fuel, ore, ammo, blueprints, ...) isn't one huge
/// undifferentiated list.</summary>
public sealed record KillmailSlotGroupRow(string GroupName, List<KillmailSubGroupRow> SubGroups);

public sealed record KillmailSubGroupRow(string SubGroupName, List<KillmailItemRow> Items);

public sealed record KillmailItemRow(
    int TypeId, string TypeName, long QtyDestroyed, long QtyDropped, double EstValue,
    bool IsBpo, bool IsBpc);

public sealed record KillmailAttackerRow(
    string CharName, string CorpName, string AllianceName,
    int DamageDone, bool FinalBlow, string ShipName, string WeaponName,
    long CharacterId, int ShipTypeId, int WeaponTypeId,
    long CorporationId = 0, long AllianceId = 0);

public sealed record KillmailListPage(List<KillmailListRow> Rows, bool HasMore);

public class KillmailBrowserService(
    IDbContextFactory<AppDbContext> dbFactory,
    CorpActivityService corpActivityService,
    EsiClient esi)
{
    public const int PageSize = 500;

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


    /// <summary>
    /// One page of kills, most recent first. <paramref name="offset"/>/<paramref name="limit"/>
    /// drive "load more" paging from the caller rather than a single hard-capped list —
    /// with zKillboard-scale data (100K+ rows and growing) a fixed cap meant older kills
    /// were simply unreachable. Fetches <paramref name="limit"/>+1 rows to detect
    /// <see cref="KillmailListPage.HasMore"/> cheaply, without a separate COUNT(*).
    ///
    /// All filters run in the SQL query itself, not against whatever page happens to be
    /// loaded client-side — with 100K+ rows total and only a page loaded at a time, a
    /// client-side filter would silently miss anything outside the currently-loaded
    /// window. <paramref name="characterFilter"/>/<paramref name="corporationFilter"/> are
    /// the exceptions that can't be a plain SQL LIKE: arbitrary killmail
    /// participants' names are resolved live via ESI (there is no persistent name cache
    /// in this app), so each is resolved to a set of ids first — our own tracked
    /// characters/corps via a local name search, falling back to ESI's authenticated
    /// search (same pattern as CorpTop10ExcludeService.SearchAsync) — and then matched
    /// by id in SQL. Both match victim OR the final-blow attacker only, mirroring the
    /// only two identity columns the list actually displays.
    /// </summary>
    public async Task<KillmailListPage> GetListAsync(
        int offset, int limit,
        DateOnly? fromDate = null, DateOnly? thruDate = null,
        string? characterFilter = null, string? corporationFilter = null,
        string? shipFilter = null, string? systemFilter = null,
        EntityKind? entityKind = null, long entityId = 0,
        CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        List<long>? characterIds = null;
        if (!string.IsNullOrWhiteSpace(characterFilter))
        {
            characterIds = await ResolveCharacterIdsAsync(db, characterFilter, ct);
            if (characterIds.Count == 0) return new KillmailListPage([], false);
        }

        List<long>? corporationIds = null;
        if (!string.IsNullOrWhiteSpace(corporationFilter))
        {
            corporationIds = await ResolveCorporationIdsAsync(db, corporationFilter, ct);
            if (corporationIds.Count == 0) return new KillmailListPage([], false);
        }

        var args = new List<object>();
        string P(object value) { args.Add(value); return $"@p{args.Count - 1}"; }

        var conditions = new List<string>();
        if (fromDate is { } fd)
            conditions.Add($"""d."KillMailTime" >= {P(fd.ToString("yyyy-MM-dd") + " 00:00:00+00:00")}""");
        if (thruDate is { } td)
            conditions.Add($"""d."KillMailTime" <= {P(td.ToString("yyyy-MM-dd") + " 23:59:59+00:00")}""");
        if (!string.IsNullOrWhiteSpace(shipFilter))
            conditions.Add($"""st."Name" LIKE {P($"%{shipFilter.Trim()}%")}""");
        if (!string.IsNullOrWhiteSpace(systemFilter))
        {
            var sysArg = P($"%{systemFilter.Trim()}%");
            var regionArg = P($"%{systemFilter.Trim()}%");
            conditions.Add($"""(ss."Name" LIKE {sysArg} OR sr."Name" LIKE {regionArg})""");
        }
        if (characterIds is { Count: > 0 })
        {
            // Ids resolved above (our own tracked characters, or an ESI search result) —
            // not raw user text, safe to inline as a literal int list like killIdsStr below.
            var idsStr = string.Join(",", characterIds);
            conditions.Add($"""(d."VictimCharId" IN ({idsStr}) OR EXISTS (SELECT 1 FROM "KillMailAttackers" a WHERE a."KillMailId" = d."KillMailId" AND a."FinalBlow" = 1 AND a."CharacterId" IN ({idsStr})))""");
        }
        if (corporationIds is { Count: > 0 })
        {
            var idsStr = string.Join(",", corporationIds);
            conditions.Add($"""(d."VictimCorpId" IN ({idsStr}) OR EXISTS (SELECT 1 FROM "KillMailAttackers" a WHERE a."KillMailId" = d."KillMailId" AND a."FinalBlow" = 1 AND a."CorporationId" IN ({idsStr})))""");
        }

        // Entity viewer path. The id is already known, so no name resolution is needed —
        // and unlike the browser's own filters this counts every attacker rather than only
        // the final blow, because "killmails this pilot was on" is the question being asked
        // there, not "kills credited to them".
        if (entityKind is { } ek && entityId > 0)
        {
            var (victimCol, attackerCol) = ek switch
            {
                EntityKind.Pilot      => ("VictimCharId",     "CharacterId"),
                EntityKind.PlayerCorp => ("VictimCorpId",     "CorporationId"),
                _                     => ("VictimAllianceId", "AllianceId"),
            };
            conditions.Add($"""(d."{victimCol}" = {entityId} OR EXISTS (SELECT 1 FROM "KillMailAttackers" a WHERE a."KillMailId" = d."KillMailId" AND a."{attackerCol}" = {entityId}))""");
        }

        var whereSql = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var limitArg  = P(limit + 1);
        var offsetArg = P(offset);

        var listSql = $"""
            SELECT d."KillMailId", d."KillMailTime",
                   d."VictimCharId", d."VictimCorpId", d."VictimAllianceId",
                   d."VictimShipTypeId", d."SolarSystemId"
            FROM "KillMailDetails" d
            LEFT JOIN "SdeTypes" st ON st."TypeId" = d."VictimShipTypeId"
            LEFT JOIN "SdeSolarSystems" ss ON ss."SolarSystemId" = d."SolarSystemId"
            LEFT JOIN "SdeRegions" sr ON sr."RegionId" = ss."RegionId"
            {whereSql}
            ORDER BY d."KillMailTime" DESC
            LIMIT {limitArg} OFFSET {offsetArg}
            """;

#pragma warning disable EF1002 // structural SQL is built entirely from our own branches above; only values in args[] are parameterized
        var details = await db.Database.SqlQueryRaw<KmDetailRaw>(listSql, args.ToArray()).ToListAsync(ct);
#pragma warning restore EF1002

        if (details.Count == 0) return new KillmailListPage([], false);

        var hasMore = details.Count > limit;
        if (hasMore) details.RemoveAt(details.Count - 1);

        var killIds = details.Select(d => d.KillMailId).ToList();
        var killIdsStr = string.Join(",", killIds);

#pragma warning disable EF1002 // killIdsStr is built from int KillMailIds we just queried ourselves, not user input
        var fbAttackers = await db.Database.SqlQueryRaw<FbRaw>($"""
            SELECT a."KillMailId", a."CharacterId", a."CorporationId", a."AllianceId"
            FROM "KillMailAttackers" a
            WHERE a."FinalBlow" = 1 AND a."KillMailId" IN ({killIdsStr})
            """).ToListAsync(ct);
#pragma warning restore EF1002

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

        // Shared with the Overview and Corp Activity kill lists, which used to value kills their
        // own way and disagreed with this one. See KillmailValuation.
        var iskMap = await KillmailValuation.ValueKillsAsync(
            db, details.ToDictionary(d => d.KillMailId, d => d.VictimShipTypeId), ct);

        string Res(long? id) => id.HasValue && id.Value != 0 && names.TryGetValue(id.Value, out var n) ? n : "";

        // Preserves the SQL's ORDER BY KillMailTime DESC — details is mapped 1:1 in
        // place, never re-sorted, so a "Load More" page can never desync from what's
        // already rendered above it.
        var rows = details.Select(d =>
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
                d.SolarSystemId,
                sys?.Name ?? d.SolarSystemId.ToString(), constellationName, sys?.RegionId ?? 0, regionName,
                sys?.Security ?? 0.0, isk,
                d.VictimCharId, d.VictimCorpId, d.VictimAllianceId ?? 0L,
                Res(d.VictimCharId), Res(d.VictimCorpId), Res(d.VictimAllianceId),
                fb?.CharacterId ?? 0L, fb?.CorporationId ?? 0L, fb?.AllianceId ?? 0L,
                fb is not null ? Res(fb.CharacterId) : "",
                fb is not null ? Res(fb.CorporationId) : "",
                fb is not null ? Res(fb.AllianceId) : "");
        }).ToList();

        return new KillmailListPage(rows, hasMore);
    }

    /// <summary>Character-name fragment → matching character ids, same pattern as
    /// CorpTop10ExcludeService.SearchAsync: our own tracked characters first (a real
    /// local Name column), falling back to ESI's authenticated non-strict character
    /// search using whichever tracked character happens to be first — there is no
    /// persistent local cache of arbitrary killmail participants' names to search
    /// against directly. Empty (not null) return means "filter active, matched
    /// nobody" — the caller should short-circuit to zero results rather than run the
    /// main query with an empty id list (which would match everything).</summary>
    private async Task<List<long>> ResolveCharacterIdsAsync(
        AppDbContext db, string nameFragment, CancellationToken ct)
    {
        var trimmed = nameFragment.Trim();

        // ⚠️ Lower-cased both sides: string.Contains becomes SQLite's instr(), which is
        // case-sensitive, so a lower-case fragment matched nobody locally and fell through to
        // an ESI search that need not have happened.
        var needle = trimmed.ToLowerInvariant();

        var localIds = await db.Characters
            .Where(c => c.Name.ToLower().Contains(needle))
            .Select(c => c.Id)
            .ToListAsync(ct);
        if (localIds.Count > 0) return localIds;

        var searchAsCharId = await db.Characters
            .OrderBy(c => c.Id).Select(c => c.Id).FirstOrDefaultAsync(ct);
        if (searchAsCharId == 0) return [];

        var esiIds = await esi.SearchCharacterIdsAsync(searchAsCharId, trimmed, ct);
        return esiIds.Select(id => (long)id).ToList();
    }

    /// <summary>Corp-name fragment → matching corporation ids. Same shape as
    /// ResolveCharacterIdsAsync — any corp involved in a kill, not just ones we track,
    /// since the old approach (a dropdown of our own tracked corps joined through
    /// EsiKillMailRefs) could only ever show "my corp"'s kills by construction.</summary>
    private async Task<List<long>> ResolveCorporationIdsAsync(
        AppDbContext db, string nameFragment, CancellationToken ct)
    {
        var trimmed = nameFragment.Trim();
        var needle  = trimmed.ToLowerInvariant();   // see ResolveCharacterIdsAsync

        var localIds = await db.Corporations
            .Where(c => c.Name.ToLower().Contains(needle))
            .Select(c => (long)c.Id)
            .ToListAsync(ct);
        if (localIds.Count > 0) return localIds;

        var searchAsCharId = await db.Characters
            .OrderBy(c => c.Id).Select(c => c.Id).FirstOrDefaultAsync(ct);
        if (searchAsCharId == 0) return [];

        var esiIds = await esi.SearchCorporationIdsAsync(searchAsCharId, trimmed, ct);
        return esiIds.Select(id => (long)id).ToList();
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

        // Blueprint Copy awareness — same reasoning as GetListAsync's ISK total: a BPC
        // (Singleton == 2 on a blueprint type id) is priced off the cheapest known
        // per-run contract price instead of the BPO market price in `prices`, since the
        // same type id can be either a BPO or BPC line depending on this kill's items.
        var itemTypeIds = items.Select(i => i.ItemTypeId).Distinct().ToList();
        var blueprintIds = await KillmailValuation.BlueprintTypeIdsAsync(db, itemTypeIds, ct);
        var bpcTypeIds = items.Where(i => i.Singleton == 2 && blueprintIds.Contains(i.ItemTypeId))
            .Select(i => i.ItemTypeId).Distinct().ToList();
        var bpcPerRun = await KillmailValuation.CheapestBpcPerRunAsync(db, bpcTypeIds, ct);

        // Market group per item type, for the Cargo Hold sub-grouping — same value for a
        // blueprint regardless of BPO/BPC (both share the one EVE type id).
        var marketGroupNames = await GetMarketGroupNamesAsync(db, itemTypeIds, ct);

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

        // Nearest celestial + distance, e.g. "Stargate (6-IAFR) (3599.69 km)" — matches
        // what zKillboard shows. Positions are raw ESI meters on both sides, so directly
        // comparable; skipped entirely when the killmail has no recorded position.
        var locationText = "";
        if (detail.VictimPosX is double px && detail.VictimPosY is double py && detail.VictimPosZ is double pz)
        {
            var celestials = await db.SdeCelestials.AsNoTracking()
                .Where(c => c.SolarSystemId == detail.SolarSystemId)
                .ToListAsync(ct);

            SdeCelestial? nearest = null;
            var bestDistSq = double.MaxValue;
            foreach (var c in celestials)
            {
                var dx = c.X - px; var dy = c.Y - py; var dz = c.Z - pz;
                var distSq = dx * dx + dy * dy + dz * dz;
                if (distSq < bestDistSq) { bestDistSq = distSq; nearest = c; }
            }
            if (nearest is not null)
                locationText = $"{nearest.Name} ({Math.Sqrt(bestDistSq) / 1000.0:N2} km)";
        }

        // Group items by slot, then append ship row at the end (always destroyed)
        double destroyedIsk = 0, droppedIsk = 0;
        var grouped = GroupItemsBySlot(items, typeNames, prices, marketGroupNames, blueprintIds, bpcPerRun, ref destroyedIsk, ref droppedIsk);
        var shipPrice = prices.TryGetValue(detail.VictimShipTypeId, out var sp) ? sp : 0;
        destroyedIsk += shipPrice;
        grouped.Add(new KillmailSlotGroupRow("Ship",
            [new KillmailSubGroupRow("", [new KillmailItemRow(detail.VictimShipTypeId, shipName, 1, 0, shipPrice, false, false)])]));

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
            a.WeaponTypeId ?? 0,
            a.CorporationId ?? 0,
            a.AllianceId    ?? 0
        )).ToList();

        return new KillmailDetailData(
            detail.KillMailId, detail.KillMailTime,
            shipName,
            detail.SolarSystemId,
            system?.Name ?? detail.SolarSystemId.ToString(),
            region?.Name ?? "",
            locationText,
            detail.VictimCharId, detail.VictimCorpId, detail.VictimAllianceId ?? 0L, detail.VictimShipTypeId,
            Res(detail.VictimCharId),
            Res(detail.VictimCorpId),
            Res(detail.VictimAllianceId),
            detail.VictimDamageTaken,
            grouped, attackerRows,
            destroyedIsk, droppedIsk);
    }

    /// <summary>Market group per item type id, for Cargo Hold sub-grouping — deep enough
    /// to be useful (top-level alone, e.g. just "Ships", is too coarse) without the
    /// noise of the item's own often much-deeper leaf group, so this walks the market
    /// group tree and keeps only the top two levels: <c>TopLevel</c> alone (used to
    /// collapse ALL blueprints into one bucket regardless of what they're blueprints
    /// for — same EVE type id for a BPO or BPC, so this is naturally shared already) and
    /// <c>Display</c> ("TopLevel &gt; SecondLevel", or just "TopLevel" when the type's
    /// own group already IS top-level). "Other" for a type with no market group.</summary>
    private static async Task<Dictionary<int, (string TopLevel, string Display)>> GetMarketGroupNamesAsync(
        AppDbContext db, IReadOnlyCollection<int> typeIds, CancellationToken ct)
    {
        if (typeIds.Count == 0) return [];

        var typeGroups = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .Select(t => new { t.TypeId, t.MarketGroupId })
            .ToListAsync(ct);

        // The whole tree is only a few hundred rows — simpler to load it once and walk
        // ancestor chains in memory than to resolve them one id at a time.
        var allGroups = await db.SdeMarketGroups.AsNoTracking().ToListAsync(ct);
        var groupById = allGroups.ToDictionary(g => g.MarketGroupId);

        var chainCache = new Dictionary<int, (string TopLevel, string Display)>();
        (string TopLevel, string Display) ResolveChain(int marketGroupId)
        {
            if (chainCache.TryGetValue(marketGroupId, out var cached)) return cached;

            var chain = new List<SdeMarketGroup>();
            var current = groupById.GetValueOrDefault(marketGroupId);
            while (current is not null)
            {
                chain.Add(current);
                current = current.ParentGroupId.HasValue ? groupById.GetValueOrDefault(current.ParentGroupId.Value) : null;
            }
            chain.Reverse(); // root first

            var result = chain.Count switch
            {
                0 => ("Other", "Other"),
                1 => (chain[0].Name, chain[0].Name),
                _ => (chain[0].Name, $"{chain[0].Name} > {chain[1].Name}"),
            };
            chainCache[marketGroupId] = result;
            return result;
        }

        return typeGroups.ToDictionary(
            t => t.TypeId,
            t => t.MarketGroupId.HasValue ? ResolveChain(t.MarketGroupId.Value) : ("Other", "Other"));
    }

    private static List<KillmailSlotGroupRow> GroupItemsBySlot(
        List<KillMailItem> items,
        Dictionary<int, string> typeNames,
        Dictionary<int, double> prices,
        Dictionary<int, (string TopLevel, string Display)> marketGroupNames,
        HashSet<int> blueprintIds,
        Dictionary<int, double> bpcPerRun,
        ref double destroyedIsk, ref double droppedIsk)
    {
        var groups = new Dictionary<string, List<KillmailItemRow>>();

        foreach (var item in items)
        {
            var group = FlagToSlotGroup(item.Flag);
            if (!groups.TryGetValue(group, out var groupItems)) { groupItems = []; groups[group] = groupItems; }

            var name = typeNames.TryGetValue(item.ItemTypeId, out var n) ? n : item.ItemTypeId.ToString();
            var isBlueprint = blueprintIds.Contains(item.ItemTypeId);
            var isBpc = item.Singleton == 2 && isBlueprint;
            var isBpo = isBlueprint && !isBpc;
            if (isBpc) name += " (Copy)"; // the one visual cue this was missing entirely

            var unitPrc  = isBpc
                ? bpcPerRun.GetValueOrDefault(item.ItemTypeId)
                : prices.GetValueOrDefault(item.ItemTypeId);
            var qtyDest  = item.QuantityDestroyed ?? 0;
            var qtyDrop  = item.QuantityDropped   ?? 0;
            var valDest  = unitPrc * qtyDest;
            var valDrop  = unitPrc * qtyDrop;

            destroyedIsk += valDest;
            droppedIsk   += valDrop;

            groupItems.Add(new KillmailItemRow(item.ItemTypeId, name, qtyDest, qtyDrop, valDest + valDrop, isBpo, isBpc));
        }

        // Canonical EVE slot order; any unrecognised groups go at the end. Cargo Hold is
        // further split into sub-groups by market group (large cargo lists — blueprint
        // libraries, ore/fuel/ammo holds — are unreadable as one flat list); every other
        // group is one implicit sub-group with no sub-header. Sorted by name throughout.
        var orderedGroupNames = _slotOrder.Where(groups.ContainsKey)
            .Concat(groups.Keys.Where(k => !_slotOrder.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        var result = new List<KillmailSlotGroupRow>();
        foreach (var groupName in orderedGroupNames)
        {
            var groupItems = groups[groupName];
            List<KillmailSubGroupRow> subGroups;

            if (groupName == "Cargo Hold")
            {
                // Blueprints (BPO or BPC) always collapse to the one fixed bucket below,
                // regardless of what they're each blueprints for — keeping "all BPO/BPC
                // together" as the ask. Deliberately NOT resolved via each blueprint's
                // own MarketGroupId chain: verified against the SDE that most T2/faction
                // blueprint TYPES have no MarketGroupId of their own at all (965 of 1033
                // T2 blueprints, 743 of 754 for one faction tier) — only their PRODUCT
                // does — so doing this per-item the same way as regular items silently
                // dropped most T2/faction BPCs into "Other". Everything else still gets
                // the deeper "TopLevel > SecondLevel" name for more context.
                string SubGroupKey(KillmailItemRow i)
                {
                    if (i.IsBpo || i.IsBpc) return BlueprintsGroupName;
                    var (_, display) = marketGroupNames.GetValueOrDefault(i.TypeId, ("Other", "Other"));
                    return display;
                }

                subGroups = groupItems
                    .GroupBy(SubGroupKey)
                    .OrderBy(g => g.Key == "Other" ? 1 : 0)
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new KillmailSubGroupRow(
                        g.Key, g.OrderBy(i => i.TypeName, StringComparer.OrdinalIgnoreCase).ToList()))
                    .ToList();
            }
            else
            {
                subGroups = [new KillmailSubGroupRow("",
                    groupItems.OrderBy(i => i.TypeName, StringComparer.OrdinalIgnoreCase).ToList())];
            }

            result.Add(new KillmailSlotGroupRow(groupName, subGroups));
        }

        return result;
    }

    // The real top-level SDE market group name (MarketGroupId 2, confirmed via a direct
    // query — "SELECT Name FROM SdeMarketGroups WHERE MarketGroupId = 2"), used as a
    // fixed bucket for every blueprint item rather than resolved per-item (see
    // SubGroupKey above for why).
    private const string BlueprintsGroupName = "Blueprints & Reactions";

    // Slot display order. Corrected against CCP's authoritative flag list
    // (esi/eve-glue location_flag.py) after finding two real mismatches: LoSlot0-7 are
    // flags 11-18 and HiSlot0-7 are 27-34 (this code had them swapped), and flags
    // 133-143 are eleven DIFFERENT specialized holds (fuel/ore/gas/mineral/salvage/ship/
    // ammo), not "Fighter Tubes" — real fighter tubes are 159-163, fighter bay is 158.
    // Verified against a real killmail: an "ORE Expanded Cargohold" (a low-slot module,
    // confirmed via its SDE group — not the same group as the Cargohold Optimization
    // rig) at flag 11-13, and Nitrogen Isotopes (fuel) at flag 133.
    private static readonly string[] _slotOrder =
    [
        "High Slots", "Mid Slots", "Low Slots", "Rig Slots", "Subsystem Slots",
        "Drone Bay", "Fighter Bay", "Fighter Tubes", "Ship Hangar", "Fleet Hangar",
        "Fuel Bay", "Ore Hold", "Ice Hold", "Asteroid Hold", "Gas Hold", "Mineral Hold",
        "Salvage Hold", "Ship Hold", "Small Ship Hold", "Medium Ship Hold",
        "Large Ship Hold", "Industrial Ship Hold", "Ammo Hold", "Cargo Hold", "Other",
    ];

    private static string FlagToSlotGroup(int flag) => flag switch
    {
        5                 => "Cargo Hold",
        >= 11 and <= 18   => "Low Slots",
        >= 19 and <= 26   => "Mid Slots",
        >= 27 and <= 34   => "High Slots",
        87                => "Drone Bay",
        90                => "Ship Hangar",
        >= 92 and <= 99   => "Rig Slots",
        >= 125 and <= 132 => "Subsystem Slots",
        133               => "Fuel Bay",
        134               => "Ore Hold",
        135               => "Gas Hold",
        136               => "Mineral Hold",
        137               => "Salvage Hold",
        138               => "Ship Hold",
        139               => "Small Ship Hold",
        140               => "Medium Ship Hold",
        141               => "Large Ship Hold",
        142               => "Industrial Ship Hold",
        143               => "Ammo Hold",
        155               => "Fleet Hangar",
        158               => "Fighter Bay",
        >= 159 and <= 163 => "Fighter Tubes",
        181               => "Ice Hold",
        182               => "Asteroid Hold",
        _                 => "Other",
    };
}
