using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.ViewModels;

namespace EveConsole.Services;

/// <summary>
/// The meta groups the Meta Groups filter offers: what tech level or kind of item a thing is,
/// and None for items with no group at all (materials, components, ammunition…), stored as 0.
///
/// <para>⚠️ Only groups with items worth listing. Officer and Limited Time have a few buildable
/// items each, all event or drop prints whose "build cost" is mostly the market price itself,
/// so they are not offered — and an item in a group not offered never appears. Deadspace and
/// the rest have nothing buildable at all.</para>
/// </summary>
public static class OppsMetaGroups
{
    public const int None    = 0;
    public const int Faction = 4;

    /// <summary>In the order the tick boxes show them. A property, so the labels follow the
    /// interface language.</summary>
    public static IReadOnlyList<(int Id, string Label)> All =>
    [
        (1,  IndustryText.MetaTech1),
        (2,  IndustryText.MetaTech2),
        (14, IndustryText.MetaTech3),
        (Faction, IndustryText.MetaFaction),
        (3,  IndustryText.MetaStoryline),
        (15, IndustryText.MetaAbyssal),
        (54, IndustryText.MetaStructureTech1),
        (53, IndustryText.MetaStructureTech2),
        (52, IndustryText.MetaStructureFaction),
        (None, IndustryText.MetaNone),
    ];

    /// <summary>What the tool and a new section start with: everything but Faction, which is
    /// what the old "Skip faction" box did, ticked by default.</summary>
    public static int[] Default => [.. All.Select(g => g.Id).Where(id => id != Faction)];

}

/// <summary>
/// One Industry Ops calculation, as asked: the mode, where to price, and every filter.
///
/// <para>Percentages are fractions here (0.15 for 15%); the boxes that collect them take
/// percent. Group ids are the groups chosen, not yet expanded to what is nested under them.</para>
/// </summary>
public sealed record IndustryOppsQuery
{
    public IndustryMode Mode       { get; init; } = IndustryMode.BuildAndSellOrder;
    public int          ConfigId   { get; init; }
    public double?      MinIskVol30d  { get; init; }
    public double?      MinUnitVol30d { get; init; }
    public double?      MinMargin     { get; init; }
    public double?      MinMargin30d  { get; init; }
    /// <summary>The meta groups kept (<see cref="OppsMetaGroups"/>); null keeps every item, any group.</summary>
    public IReadOnlyCollection<int>? MetaGroups { get; init; }
    public bool         BpoOnly       { get; init; } = true;
    public bool         OwnedOnly     { get; init; }
    public IReadOnlyCollection<int> ExcludedGroupIds { get; init; } = [];
    public IReadOnlyCollection<int> IncludedGroupIds { get; init; } = [];

    /// <summary>Whether a filter needs the 30-day history, and so the region.</summary>
    public bool NeedsVolume => MinIskVol30d.HasValue || MinUnitVol30d.HasValue || MinMargin30d.HasValue;
}

/// <summary>
/// The meta group and market group filters of a query, worked out: what the Meta Groups,
/// Include and Exclude choices keep.
///
/// <para>⚠️ The ONE place these are applied. The service applies it to what it calculates, and the
/// tool's Detail tab to rows already calculated for the Summary tab, so the two can never keep
/// different items. An item with no meta group is 0, the None choice; one with no market group is
/// never excluded and never included.</para>
/// </summary>
public sealed class IndustryOppsFilter(IReadOnlyCollection<int>? metaGroups, HashSet<int> included, HashSet<int> excluded)
{
    private readonly HashSet<int>? _meta = metaGroups is null ? null : [.. metaGroups];

    public bool Keeps(int metaGroupId, int? marketGroupId) =>
        (_meta is null || _meta.Contains(metaGroupId))
        && (excluded.Count == 0 || marketGroupId is null || !excluded.Contains(marketGroupId.Value))
        && (included.Count == 0 || (marketGroupId is { } m && included.Contains(m)));

    public bool Keeps(IndustryRow row) => Keeps(row.MetaGroupId, row.MarketGroupId);
}

/// <summary>What a calculation found. <see cref="NoRegion"/>: a 30-day filter was asked for and
/// the market's region could not be worked out, so nothing was calculated.</summary>
public sealed record IndustryOppsResult(List<IndustryRow> Rows, int? RegionId, string RegionName, bool NoRegion);

/// <summary>
/// The Industry Ops calculation: every item with a cached build cost, priced at a market, kept
/// when building it shows a profit and it passes the filters.
///
/// <para>⚠️ One place for it. The Industry Ops tool and a scheduled post's Industry Ops section
/// both ask here, so a post can never quote figures the screen would not.</para>
/// </summary>
public sealed class IndustryOpportunitiesService(MarketHistoryService history, BatchAddService batch)
{
    // Per-unit build time is computed and cached in BuildCosts.BuildSeconds by
    // BuildCostService (using the default park's blueprint TE, skills, and structure
    // role/rig time bonuses) — we just read it here and convert to slot-days.
    private const double SecondsPerDay = 86_400.0;

    /// <summary>The enabled market configs, in their sort order — what "Price At" offers.</summary>
    public static async Task<List<IndustryMarketConfig>> LoadMarketConfigsAsync()
    {
        using var conn = AppDb.Connect();
        await conn.OpenAsync();

        using var cmd = conn.Command("""
            SELECT "Id", "LocationName", "Method", "LocationId"
            FROM "MarketPricingConfigs"
            WHERE "IsEnabled" = TRUE
            ORDER BY "SortOrder"
            """);

        var list = new List<IndustryMarketConfig>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(new IndustryMarketConfig(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        return list;
    }

    /// <summary>
    /// Runs the calculation. Null when the market config does not exist or is switched off.
    /// The rows come in the tool's default order: Profit 30d, then profit per slot-day.
    /// </summary>
    public async Task<IndustryOppsResult?> CalculateAsync(IndustryOppsQuery q, CancellationToken ct = default)
    {
        var config = (await LoadMarketConfigsAsync()).FirstOrDefault(c => c.ConfigId == q.ConfigId);
        if (config is null) return null;

        await SdeNames.EnsureLoadedAsync();   // the rows and the status line carry the names shown
        var filter     = await FilterAsync(q);
        var candidates = (await FetchCandidatesAsync(q)).Where(c => filter.Keeps(c.MetaGroupId, c.MarketGroupId)).ToList();

        // Region is needed for the volume filters AND to price items that have no
        // sell orders off their 30-day history average. Resolve it best-effort.
        int?   regionId   = await ResolveRegionAsync(config);
        string regionName = regionId.HasValue ? await GetRegionNameAsync(regionId.Value) : IndustryText.OppsRegionUnresolved;
        if (q.NeedsVolume && !regionId.HasValue)
            return new IndustryOppsResult([], null, regionName, NoRegion: true);

        var (typesWithSell, typesWithBuy, configHasRawOrders) = await LoadOrderPresenceAsync(config.ConfigId);

        var rows = await BuildRowsAsync(q, candidates, regionId, typesWithSell, typesWithBuy, configHasRawOrders, ct);
        return new IndustryOppsResult(
            [.. rows.OrderByDescending(r => r.Profit30d).ThenByDescending(r => r.ProfitPerSlotDay)],
            regionId, regionName, NoRegion: false);
    }

    /// <summary>
    /// Every item the query's other filters keep, in any meta group offered and any market group:
    /// the Meta Groups, Include and Exclude choices left out. What the tool's Summary tab ranks,
    /// and what its Detail tab then filters with <see cref="FilterAsync"/>, so one calculation
    /// serves both.
    /// </summary>
    public Task<IndustryOppsResult?> CalculateAllAsync(IndustryOppsQuery q, CancellationToken ct = default) =>
        CalculateAsync(q with { MetaGroups = [.. OppsMetaGroups.All.Select(g => g.Id)], IncludedGroupIds = [], ExcludedGroupIds = [] }, ct);

    /// <summary>The query's meta and market group choices, with the groups expanded to what is
    /// nested under them.</summary>
    public async Task<IndustryOppsFilter> FilterAsync(IndustryOppsQuery q) =>
        new(q.MetaGroups, await GroupIdsRecursiveAsync(q.IncludedGroupIds), await GroupIdsRecursiveAsync(q.ExcludedGroupIds));

    /// <summary>Market group names in the interface language, for the ids that exist.</summary>
    public static async Task<Dictionary<int, string>> MarketGroupNamesAsync(IReadOnlyCollection<int> ids)
    {
        var names = new Dictionary<int, string>();
        if (ids.Count == 0) return names;

        await SdeNames.EnsureLoadedAsync();
        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        // Ids only, never text: they are interpolated.
        using var cmd = conn.Command($"""SELECT "MarketGroupId", "Name" FROM "SdeMarketGroups" WHERE "MarketGroupId" IN ({string.Join(",", ids)})""");
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names[reader.GetInt32(0)] = SdeNames.MarketGroup(reader.GetInt32(0), reader.GetString(1));
        return names;
    }

    /// <summary>The groups chosen, and every group nested under them.</summary>
    public async Task<HashSet<int>> GroupIdsRecursiveAsync(IEnumerable<int> groupIds)
    {
        var result = new HashSet<int>();
        foreach (var id in groupIds)
            result.UnionWith(await batch.GetDescendantGroupIdsAsync(id));
        return result;
    }

    // ── Core algorithm ────────────────────────────────────────────────────────

    /// <summary>An item with a build cost and a price, and what it is: its meta group (0 for
    /// none), market group, and inventory group and category, the group by its English name.</summary>
    private record Candidate(
        int TypeId, string TypeName,
        double BuildCost, double SellOrderPrice, double BuyOrderPrice,
        double BuildSeconds,
        int MetaGroupId, int? MarketGroupId, int GroupId, int CategoryId, string GroupName);

    /// <summary>Every candidate the BPO and owned choices keep. The meta and market group
    /// choices are applied afterwards, by <see cref="IndustryOppsFilter"/>.</summary>
    private async Task<List<Candidate>> FetchCandidatesAsync(IndustryOppsQuery q)
    {

        // BPO-only: keep items whose (published) manufacturing/reaction blueprint either is
        // sold on the market (a buyable BPO) OR is invented from a source blueprint that is
        // sold on the market (e.g. a T2 BPC invented from a T1 BPO). Excludes items whose BPC
        // has no BPO and cannot be derived from one (faction/limited-run BPCs), plus
        // "Limited Time" items (MetaGroupId 19) — event blueprints that have a market group
        // in the SDE but can no longer be bought in game.
        //
        // ⚠️ A market group is not proof of a BPO. Storyline (3), Officer (5) and Deadspace (6)
        // products are skipped outright, the same loot tiers BuildCostService refuses to treat
        // as BPO-sourced: the one officer module with a blueprint — an event reward, never sold —
        // files it beside the ordinary T1 weapon-upgrade BPOs, and topped the list priced at
        // officer-module prices. Faction (4) keeps its own checkbox.
        var bpoClause = q.BpoOnly
            ? """
              AND (t."MetaGroupId" IS NULL OR t."MetaGroupId" NOT IN (3, 5, 6, 19))
              AND EXISTS (
                SELECT 1 FROM "SdeBlueprintProducts" bp
                JOIN "SdeTypes" bpt ON bpt."TypeId" = bp."TypeId"
                WHERE bp."ProductTypeId" = bc."TypeId"
                  AND bp."Activity" IN ('manufacturing','reaction')
                  AND bpt."Published" = TRUE
                  AND (
                    bpt."MarketGroupId" IS NOT NULL
                    OR EXISTS (
                      SELECT 1 FROM "SdeBlueprintProducts" inv
                      JOIN "SdeTypes" src ON src."TypeId" = inv."TypeId"
                      WHERE inv."Activity" = 'invention'
                        AND inv."ProductTypeId" = bp."TypeId"
                        AND src."MarketGroupId" IS NOT NULL)
                  ))
              """
            : "";

        // Owned: the item's own manufacturing or reaction blueprint held by one of the user's
        // characters, or by a corporation marked personal — the Asset Browser's "characters and
        // personal corps". A copy counts as much as an original; an original that could only be
        // invented into this item does not, since that is a further job before this one.
        // ⚠️ IsPersonal stands on its own: INTEGER on SQLite, BOOLEAN on PostgreSQL.
        var ownedClause = q.OwnedOnly
            ? """
              AND EXISTS (
                SELECT 1 FROM "SdeBlueprintProducts" obp
                JOIN "EsiBlueprints" ob ON ob."TypeId" = obp."TypeId"
                WHERE obp."ProductTypeId" = bc."TypeId"
                  AND obp."Activity" IN ('manufacturing','reaction')
                  AND (ob."OwnerType" = 'character'
                       OR (ob."OwnerType" = 'corporation'
                           AND ob."OwnerId" IN (SELECT pc."Id" FROM "Corporations" pc WHERE pc."IsPersonal"))))
              """
            : "";

        using var conn = AppDb.Connect();
        await conn.OpenAsync();

        using var cmd = conn.Command(CandidateSql
            .Replace("/*BPO*/", bpoClause)
            .Replace("/*OWNED*/", ownedClause));
        cmd.AddWithValue("@configId", q.ConfigId);

        var list = new List<Candidate>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new Candidate(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetString(10)));
        }
        return list;
    }

    // Which types have live buy/sell orders for this config (from the raw order snapshot),
    // and whether the config has any raw orders at all (Fuzzwork configs have none — for
    // those we trust the stored prices and treat every item as having sell orders).
    private static async Task<(HashSet<int> WithSell, HashSet<int> WithBuy, bool HasRawOrders)>
        LoadOrderPresenceAsync(int configId)
    {
        var withSell = new HashSet<int>();
        var withBuy  = new HashSet<int>();

        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        using var cmd = conn.Command("""
            SELECT DISTINCT "TypeId", "IsBuyOrder"
            FROM "MarketRawOrders"
            WHERE "ConfigId" = @configId
            """);
        cmd.AddWithValue("@configId", configId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            int typeId = reader.GetInt32(0);
            if (reader.GetBoolean(1)) withBuy.Add(typeId);
            else                      withSell.Add(typeId);
        }
        return (withSell, withBuy, withSell.Count > 0 || withBuy.Count > 0);
    }

    private async Task<List<IndustryRow>> BuildRowsAsync(
        IndustryOppsQuery q, List<Candidate> candidates, int? regionId,
        HashSet<int> typesWithSell, HashSet<int> typesWithBuy, bool configHasRawOrders, CancellationToken ct)
    {
        var result = new List<IndustryRow>();

        // Every candidate's 30-day trade in one read: the same sums as asking type by type.
        var volumes = regionId.HasValue
            ? await history.Get30DayVolumesAsync(regionId.Value, [.. candidates.Select(c => c.TypeId)], ct)
            : [];

        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (c.BuildCost <= 0) continue;

            double sellInto;
            bool   hasSellOrders = true;

            if (q.Mode == IndustryMode.BuildAndSellToBuyOrder)
            {
                // No buy orders for this item → nobody to sell to → skip.
                if (configHasRawOrders && !typesWithBuy.Contains(c.TypeId)) continue;
                sellInto = c.BuyOrderPrice;
            }
            else
            {
                bool sellOrders = !configHasRawOrders || typesWithSell.Contains(c.TypeId);
                if (sellOrders)
                {
                    sellInto = c.SellOrderPrice; // real lowest sell
                }
                else
                {
                    // No sell orders — these are often the most lucrative if in demand.
                    // Price them off the 30-day history average (what they actually trade
                    // at) rather than the build-cost gap-fill, and flag them with a "*".
                    sellInto = regionId.HasValue
                        ? await history.Get30DayAveragePriceAsync(regionId.Value, c.TypeId)
                        : 0;
                    hasSellOrders = false;
                }
            }

            if (sellInto <= 0) continue;

            double profit = sellInto - c.BuildCost;
            if (profit <= 0) continue; // only surface opportunities
            // The Margin column's own figure: profit over build cost.
            if (q.MinMargin.HasValue && profit / c.BuildCost < q.MinMargin.Value) continue;

            // 30-day volumes (config region), from history cached by the background sweep —
            // no ESI here. Always read them for the columns; also apply them as filters.
            double unitVol = 0, iskVol = 0;
            if (regionId.HasValue)
            {
                (unitVol, iskVol) = volumes.GetValueOrDefault(c.TypeId);
                if (q.MinUnitVol30d.HasValue && unitVol < q.MinUnitVol30d.Value) continue;
                if (q.MinIskVol30d.HasValue  && iskVol  < q.MinIskVol30d.Value)  continue;
                // The Margin 30d column's figure; no sales, no margin, not kept.
                if (q.MinMargin30d.HasValue
                    && !(unitVol > 0 && (iskVol / unitVol - c.BuildCost) / c.BuildCost >= q.MinMargin30d.Value)) continue;
            }

            double slotDays = c.BuildSeconds / SecondsPerDay;

            result.Add(new IndustryRow
            {
                TypeId           = c.TypeId,
                TypeName         = SdeNames.Type(c.TypeId, c.TypeName),   // shown only; the row goes by TypeId
                BuildCost        = c.BuildCost,
                SellPrice        = sellInto,
                HasSellOrders    = hasSellOrders,
                ProfitPerUnit    = profit,
                Margin           = profit / c.BuildCost,
                BuildSeconds     = c.BuildSeconds,
                SlotDays         = slotDays,
                ProfitPerSlotDay = slotDays > 0 ? profit / slotDays : 0,
                UnitVol30d       = unitVol,
                IskVol30d        = iskVol,
                MetaGroupId      = c.MetaGroupId,
                MarketGroupId    = c.MarketGroupId,
                GroupId          = c.GroupId,
                CategoryId       = c.CategoryId,
                GroupName        = c.GroupName,
            });
        }

        return result;
    }

    /// <summary>The region as the status line shows it, in the interface language.</summary>
    private static async Task<string> GetRegionNameAsync(int regionId)
    {
        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        using var cmd = conn.Command("""SELECT "Name" FROM "SdeRegions" WHERE "RegionId" = @id""");
        cmd.AddWithValue("@id", regionId);
        return (await cmd.ExecuteScalarAsync()) is string name
            ? SdeNames.Region(regionId, name)
            : string.Format(IndustryText.OppsRegionNumbered, regionId);
    }

    // Resolves the region id used for the 30-day volume lookups from a market config.
    public static async Task<int?> ResolveRegionAsync(IndustryMarketConfig cfg)
    {
        // ESI Region configs store the region id directly in LocationId.
        if (cfg.Method == MarketMethod.EsiRegion) return (int)cfg.LocationId;

        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();

        // Maybe LocationId is already a region id (Fuzzwork region configs).
        cmd.CommandText = AppDb.CaseInsensitiveLike("""SELECT "RegionId" FROM "SdeRegions" WHERE "RegionId" = @loc""");
        cmd.AddWithValue("@loc", cfg.LocationId);
        var region = ToRegionId(await cmd.ExecuteScalarAsync());
        if (region.HasValue) return region;

        // NPC station: resolve via its solar system. SdeStations.RegionId is populated now
        // (by the importer, and by a startup repair for databases imported before that fix),
        // but the join is kept deliberately — it is correct whatever state the column is in.
        cmd.CommandText = AppDb.CaseInsensitiveLike("""
            SELECT ss."RegionId"
            FROM "SdeStations"     s
            JOIN "SdeSolarSystems" ss ON ss."SolarSystemId" = s."SolarSystemId"
            WHERE s."StationId" = @sid AND s."SolarSystemId" != 0
            """);
        cmd.Parameters.Clear();
        cmd.AddWithValue("@sid", (int)Math.Min(cfg.LocationId, int.MaxValue));
        region = ToRegionId(await cmd.ExecuteScalarAsync());
        if (region.HasValue) return region;

        // Player structure: resolved name record already has SolarSystemId.
        cmd.CommandText = AppDb.CaseInsensitiveLike("""
            SELECT ss."RegionId"
            FROM "EsiStructureNames" sn
            JOIN "SdeSolarSystems"   ss ON ss."SolarSystemId" = sn."SolarSystemId"
            WHERE sn."StructureId" = @lid AND sn."SolarSystemId" != 0
            """);
        cmd.Parameters.Clear();
        cmd.AddWithValue("@lid", cfg.LocationId);
        region = ToRegionId(await cmd.ExecuteScalarAsync());
        if (region.HasValue) return region;

        // Fallback: derive from any cached order at that location.
        cmd.CommandText = AppDb.CaseInsensitiveLike("""
            SELECT ss."RegionId"
            FROM "MarketRawOrders" o
            JOIN "SdeSolarSystems" ss ON ss."SolarSystemId" = o."SystemId"
            WHERE o."LocationId" = @lid AND o."SystemId" != 0
            LIMIT 1
            """);
        cmd.Parameters.Clear();
        cmd.AddWithValue("@lid", cfg.LocationId);
        region = ToRegionId(await cmd.ExecuteScalarAsync());
        return region;
    }

    // Treats NULL/DBNull and a 0 region id as "unresolved" so callers fall through.
    private static int? ToRegionId(object? scalar)
    {
        if (scalar is null or DBNull) return null;
        var id = Convert.ToInt32(scalar);
        return id != 0 ? id : null;
    }

    // ── SQL ───────────────────────────────────────────────────────────────────

    // Joins cached build cost + build time (from BuildCostService) with market prices
    // for the chosen config. One BuildCosts row per item (TypeId is PK), so no dedupe.
    private const string CandidateSql = """
        SELECT
            bc."TypeId",
            bc."TypeName",
            CAST(bc."TotalCost"    AS DOUBLE PRECISION)   AS "BuildCost",
            CAST(mip."SellPrice"   AS DOUBLE PRECISION)   AS "SellPrice",
            CAST(mip."BuyPrice"    AS DOUBLE PRECISION)   AS "BuyPrice",
            CAST(bc."BuildSeconds" AS DOUBLE PRECISION)   AS "BuildSeconds",
            COALESCE(t."MetaGroupId", 0)                  AS "MetaGroupId",
            t."MarketGroupId",
            t."GroupId",
            g."CategoryId",
            g."Name"                                      AS "GroupName"
        FROM "BuildCosts" bc
        JOIN "MarketItemPrices" mip
              ON mip."TypeId" = bc."TypeId" AND mip."ConfigId" = @configId
        JOIN "SdeTypes" t ON t."TypeId" = bc."TypeId"
        JOIN "SdeGroups" g ON g."GroupId" = t."GroupId"
        WHERE bc."TotalCost" > 0 AND bc."BuildSeconds" > 0
        /*BPO*/
        /*OWNED*/
        """;
}
