using System.Globalization;
using EveCortex.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EveCortex.Services;

// Records a daily point-in-time snapshot of each type's market value, build cost and contract
// price. Like NetWorthService, the current UTC day's rows are recomputed/overwritten each time
// prices refresh; once the day rolls over, prior days are left untouched (a frozen history).
//
// Market value uses the configured asset-value market config (Settings → Market) and its price
// type. Build cost and contract price are config-independent per-type values. A row is written
// for every type that has any of the three on the day (union of the three source tables).
public class TypePriceHistoryService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
{
    public async Task RecalculateAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var settings = await db.MarketDefaultSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
            int? configId = settings?.AssetValueConfigId;

            // Map the configured price type to the corresponding MarketItemPrices column.
            var marketCol = (settings?.AssetValuePriceType) switch
            {
                "Buy"  => "mp.\"BuyPrice\"",
                "Sell" => "mp.\"SellPrice\"",
                _      => "mp.\"Midpoint\"",
            };

            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var now   = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

            // One INSERT OR REPLACE upserts the whole current-day slice. Prices of 0 (and absent
            // rows) become NULL. The contract expression mirrors ContractPricing.EffectivePrice:
            // best price unless it is >50% above the 30-day average, in which case the average.
            var sql = $"""
                INSERT OR REPLACE INTO "TypePriceSnapshots"
                    ("TypeId", "Date", "MarketValue", "BuildCost", "ContractPrice", "ComputedAt")
                SELECT ids."TypeId", @today,
                       NULLIF({marketCol}, 0),
                       NULLIF(CAST(bc."TotalCost" AS REAL), 0),
                       CASE
                           WHEN cp."TypeId"    IS NULL THEN NULL
                           WHEN cp."BestPrice" IS NULL THEN CAST(cp."Avg30Best" AS REAL)
                           WHEN cp."Avg30Best" IS NULL THEN CAST(cp."BestPrice" AS REAL)
                           WHEN CAST(cp."BestPrice" AS REAL) > 1.5 * CAST(cp."Avg30Best" AS REAL)
                                THEN CAST(cp."Avg30Best" AS REAL)
                           ELSE CAST(cp."BestPrice" AS REAL)
                       END,
                       @now
                FROM (
                    SELECT "TypeId" FROM "MarketItemPrices" WHERE "ConfigId" = @cfg
                    UNION SELECT "TypeId" FROM "BuildCosts"
                    UNION SELECT "TypeId" FROM "ContractPrices"
                ) ids
                LEFT JOIN "MarketItemPrices" mp ON mp."ConfigId" = @cfg AND mp."TypeId" = ids."TypeId"
                LEFT JOIN "BuildCosts"       bc ON bc."TypeId"   = ids."TypeId"
                LEFT JOIN "ContractPrices"   cp ON cp."TypeId"   = ids."TypeId"
                """;

            await db.Database.ExecuteSqlRawAsync(sql,
            [
                new SqliteParameter("@today", today),
                new SqliteParameter("@now",   now),
                new SqliteParameter("@cfg",   (object?)configId ?? DBNull.Value),
            ], ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log("TypePriceHistoryService", "Recalculate", ex);
        }
    }
}
