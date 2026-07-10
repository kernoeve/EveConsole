using EveCortex.Data;
using EveCortex.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EveCortex.Services;

public class NetWorthService(IDbContextFactory<AppDbContext> dbFactory)
{
    // ── Public API ────────────────────────────────────────────────────────────

    public async Task RecalculateAsync(long ownerId, string ownerType, CancellationToken ct = default)
    {
        try
        {
            await using var db   = await dbFactory.CreateDbContextAsync(ct);
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            var assets      = Math.Round(await ScalarAsync(conn, AssetValueSql,        ownerId, ownerType, ct), 2);
            var industry    = Math.Round(await ScalarAsync(conn, IndustryJobValueSql,   ownerId, ownerType, ct), 2);
            var wallet      = Math.Round(await ScalarAsync(conn, WalletBalanceSql,      ownerId, ownerType, ct), 2);
            var sellOrders  = Math.Round(await ScalarAsync(conn, SellOrderValueSql,     ownerId, ownerType, ct), 2);
            var buyEscrow   = Math.Round(await ScalarAsync(conn, BuyOrderEscrowSql,     ownerId, ownerType, ct), 2);
            var collateral  = Math.Round(await ScalarAsync(conn, ContractCollateralSql, ownerId, ownerType, ct), 2);
            var contracts   = Math.Round(await ScalarAsync(conn, ContractValueSql,      ownerId, ownerType, ct), 2);
            var total       = Math.Round(assets + industry + wallet + sellOrders + buyEscrow + collateral + contracts, 2);

            var existing = await db.NetWorthSnapshots
                .FirstOrDefaultAsync(n => n.OwnerId == ownerId && n.OwnerType == ownerType && n.Date == today, ct);

            if (existing is null)
            {
                db.NetWorthSnapshots.Add(new NetWorthSnapshot
                {
                    OwnerId            = ownerId,
                    OwnerType          = ownerType,
                    Date               = today,
                    AssetValue         = assets,
                    IndustryJobValue   = industry,
                    WalletBalance      = wallet,
                    SellOrderValue     = sellOrders,
                    BuyOrderEscrow     = buyEscrow,
                    ContractCollateral = collateral,
                    ContractValue      = contracts,
                    Total              = total,
                    ComputedAt         = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.AssetValue         = assets;
                existing.IndustryJobValue   = industry;
                existing.WalletBalance      = wallet;
                existing.SellOrderValue     = sellOrders;
                existing.BuyOrderEscrow     = buyEscrow;
                existing.ContractCollateral = collateral;
                existing.ContractValue      = contracts;
                existing.Total              = total;
                existing.ComputedAt         = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* non-fatal — next cycle will retry */ }
    }

    // ── Scalar query helper ───────────────────────────────────────────────────

    private static async Task<double> ScalarAsync(
        SqliteConnection conn, string sql, long ownerId, string ownerType, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@ownerId",   ownerId);
        cmd.Parameters.AddWithValue("@ownerType", ownerType);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? 0.0 : Convert.ToDouble(result);
    }

    // ── SQL queries ───────────────────────────────────────────────────────────

    // All items in EsiAssets priced via MarketDefaultSettings.
    // BPCs = 0; BPOs = SDE base price; everything else = configured market price.
    private const string AssetValueSql = """
        SELECT COALESCE(SUM(
            CAST(a."Quantity" AS REAL) * CASE
                WHEN a."IsBlueprintCopy" = 1 THEN 0.0
                WHEN a."IsBlueprintCopy" = 0 THEN COALESCE(t."BasePrice", 0.0)
                ELSE COALESCE(
                    NULLIF(
                        CASE WHEN mds."AssetValueConfigId" IS NOT NULL AND p."TypeId" IS NOT NULL THEN
                            CASE COALESCE(mds."AssetValuePriceType", 'Midpoint')
                                WHEN 'Buy'  THEN p."BuyPrice"
                                WHEN 'Sell' THEN p."SellPrice"
                                ELSE             p."Midpoint"
                            END
                        ELSE NULL END,
                    0.0),
                    CASE WHEN bc."TotalCost" > 0
                         THEN bc."TotalCost" * (1.0 + COALESCE(mds."MissingPriceMarkupPct", 15.0) / 100.0)
                         ELSE 0.0 END
                )
            END
        ), 0.0)
        FROM "EsiAssets" a
        LEFT JOIN "SdeTypes" t
               ON t."TypeId" = a."TypeId"
        LEFT JOIN "MarketDefaultSettings" mds ON mds."Id" = 1
        LEFT JOIN "MarketItemPrices" p
               ON mds."AssetValueConfigId" IS NOT NULL
              AND p."ConfigId" = mds."AssetValueConfigId"
              AND p."TypeId"   = a."TypeId"
        LEFT JOIN "BuildCosts" bc ON bc."TypeId" = a."TypeId"
        WHERE a."OwnerId" = @ownerId AND a."OwnerType" = @ownerType
        """;

    // Active/paused/ready industry jobs: source blueprint value (BPO base price, returned on
    // completion) + product value. Copying (5) and invention (8) produce blueprint COPIES, valued
    // as BPCs from contracts (ContractPrices effective price) — NOT as the source BPO's market price.
    private const string IndustryJobValueSql = """
        SELECT COALESCE(SUM(
            COALESCE(CASE WHEN ebp."Quantity" = -1 THEN COALESCE(bt."BasePrice", 0.0) ELSE 0.0 END, 0.0)
            +
            CASE
                -- ME/TE research (3, 4) produces no new item: the BPO is both input
                -- and output, so it is valued once above via the BPO base price and
                -- must not add any output value here (would double-count).
                WHEN j."ActivityId" IN (3, 4) THEN 0.0
                WHEN j."ActivityId" IN (5, 8) THEN
                    CAST(j."Runs" AS REAL) * COALESCE(
                        CASE
                            WHEN cp."BestPrice" IS NULL THEN CAST(cp."Avg30Best" AS REAL)
                            WHEN cp."Avg30Best" IS NULL THEN CAST(cp."BestPrice" AS REAL)
                            WHEN CAST(cp."BestPrice" AS REAL) > 1.5 * CAST(cp."Avg30Best" AS REAL)
                                 THEN CAST(cp."Avg30Best" AS REAL)
                            ELSE CAST(cp."BestPrice" AS REAL)
                        END, 0.0)
                ELSE CAST(COALESCE(bp."Quantity", 1) * j."Runs" AS REAL) *
                     COALESCE(
                         NULLIF(
                             CASE WHEN p."TypeId" IS NOT NULL THEN
                                 CASE COALESCE(mds."AssetValuePriceType", 'Midpoint')
                                     WHEN 'Buy'  THEN p."BuyPrice"
                                     WHEN 'Sell' THEN p."SellPrice"
                                     ELSE             p."Midpoint"
                                 END
                             ELSE NULL END,
                         0.0),
                         CASE WHEN bc."TotalCost" > 0
                              THEN bc."TotalCost" * (1.0 + COALESCE(mds."MissingPriceMarkupPct", 15.0) / 100.0)
                              ELSE 0.0 END
                     )
            END
        ), 0.0)
        FROM "EsiIndustryJobs" j
        LEFT JOIN "EsiBlueprints" ebp
               ON ebp."ItemId"    = j."BlueprintId"
              AND ebp."OwnerId"   = j."OwnerId"
              AND ebp."OwnerType" = j."OwnerType"
        LEFT JOIN "SdeTypes" bt
               ON bt."TypeId" = j."BlueprintTypeId"
        LEFT JOIN "SdeBlueprintProducts" bp
               ON bp."TypeId"        = j."BlueprintTypeId"
              AND bp."Activity"      = j."ActivityId"
              AND bp."ProductTypeId" = j."ProductTypeId"
        LEFT JOIN "MarketDefaultSettings" mds ON mds."Id" = 1
        LEFT JOIN "MarketItemPrices" p
               ON mds."AssetValueConfigId" IS NOT NULL
              AND p."ConfigId" = mds."AssetValueConfigId"
              AND p."TypeId"   = j."ProductTypeId"
        LEFT JOIN "BuildCosts" bc ON bc."TypeId" = j."ProductTypeId"
        LEFT JOIN "ContractPrices" cp ON cp."TypeId" = j."ProductTypeId"
        WHERE j."OwnerId"  = @ownerId AND j."OwnerType" = @ownerType
          AND j."Status" NOT IN ('delivered', 'cancelled', 'failed', 'reverted')
        """;

    // Sum of all wallet divisions.
    // Characters have one row (Division=0); corps have up to 7 (Divisions 1-7).
    private const string WalletBalanceSql = """
        SELECT COALESCE(SUM(CAST("Balance" AS REAL)), 0.0)
        FROM "EsiWalletBalances"
        WHERE "OwnerId" = @ownerId AND "OwnerType" = @ownerType
        """;

    // Active sell order value = remaining quantity × listed price.
    private const string SellOrderValueSql = """
        SELECT COALESCE(SUM(CAST("VolumeRemain" AS REAL) * CAST("Price" AS REAL)), 0.0)
        FROM "EsiMarketOrders"
        WHERE "OwnerId"    = @ownerId AND "OwnerType" = @ownerType
          AND "IsBuyOrder" = 0
          AND "IsHistory"  = 0
        """;

    // Buy order escrow = ISK currently locked up in active buy orders.
    private const string BuyOrderEscrowSql = """
        SELECT COALESCE(SUM(CAST("Escrow" AS REAL)), 0.0)
        FROM "EsiMarketOrders"
        WHERE "OwnerId"    = @ownerId AND "OwnerType" = @ownerType
          AND "IsBuyOrder" = 1
          AND "IsHistory"  = 0
        """;

    // Courier contracts issued by this owner that are outstanding or in progress.
    // Collateral is the amount the courier must pay if they fail to deliver.
    private const string ContractCollateralSql = """
        SELECT COALESCE(SUM(CAST("Collateral" AS REAL)), 0.0)
        FROM "EsiContracts"
        WHERE "OwnerId"  = @ownerId AND "OwnerType" = @ownerType
          AND "Type"     = 'courier'
          AND "Status"   IN ('outstanding', 'in_progress')
          AND (
              (@ownerType = 'character'   AND "IssuerId"            = @ownerId AND "ForCorporation" = 0)
           OR (@ownerType = 'corporation' AND "IssuerCorporationId" = @ownerId)
          )
        """;

    // Item-exchange and auction contracts issued by this owner that are still outstanding.
    // Price = ISK the buyer pays (value of items on offer).
    private const string ContractValueSql = """
        SELECT COALESCE(SUM(CAST("Price" AS REAL)), 0.0)
        FROM "EsiContracts"
        WHERE "OwnerId"  = @ownerId AND "OwnerType" = @ownerType
          AND "Type"     IN ('item_exchange', 'auction')
          AND "Status"   = 'outstanding'
          AND (
              (@ownerType = 'character'   AND "IssuerId"            = @ownerId AND "ForCorporation" = 0)
           OR (@ownerType = 'corporation' AND "IssuerCorporationId" = @ownerId)
          )
        """;
}
