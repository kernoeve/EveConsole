using System.Globalization;
using EveConsole.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

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

            // ⚠️ ON CONFLICT rather than INSERT OR REPLACE, which is SQLite's alone. Both
            // engines have understood this form since SQLite 3.24, and it says what the older
            // spelling only implied: a replace DELETES the row and inserts a new one, so any
            // column not listed silently reverts to its default. Here every column is listed, so
            // the two agree — but the upsert states that rather than relying on it.
            //
            // One statement upserts the whole current-day slice. Prices of 0 (and absent
            // rows) become NULL. The contract expression mirrors ContractPricing.EffectivePrice:
            // best price unless it is >50% above the 30-day average, in which case the average.
            var sql = $"""
                INSERT INTO "TypePriceSnapshots"
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
                ON CONFLICT ("TypeId", "Date") DO UPDATE SET
                    "MarketValue"   = excluded."MarketValue",
                    "BuildCost"     = excluded."BuildCost",
                    "ContractPrice" = excluded."ContractPrice",
                    "ComputedAt"    = excluded."ComputedAt"
                """;

            await db.Database.ExecuteSqlRawAsync(sql,
            [
                AppDb.Param("@today", today),
                AppDb.Param("@now",   now),
                AppDb.Param("@cfg",   (object?)configId ?? DBNull.Value),
            ], ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log("TypePriceHistoryService", "Recalculate", ex);
        }
    }

    /// <summary>
    /// The value that stood on a given day, from a type's snapshots.
    ///
    /// <para>Snapshots exist only for days the app was running, so any lookup by date has to cope
    /// with the day simply not being there. The order is: that exact day, else <b>the next day that
    /// has one</b>, else the most recent day before.</para>
    ///
    /// <para>Looking forward first is what makes a new database usable. Every snapshot it holds is
    /// dated from the day the app was installed, so a sale from last month has nothing before it —
    /// only after. Carrying backwards alone returned nothing, and the caller then fell back to
    /// market value, quietly reporting a market price where a build cost belonged. The same gap
    /// opens for an established install whenever the app is closed for a few days.</para>
    ///
    /// <para>Falling back to the last day before covers the other end: a sale later than every
    /// snapshot, which happens between a sale landing and that night's snapshot being written.</para>
    /// </summary>
    /// <param name="ascending">The type's snapshots that actually carry a value, oldest first.
    /// Rows with none must be filtered out by the caller — a day whose build cost was never
    /// computed is a day with no answer, not an answer of zero.</param>
    public static double? ValueAsOf(IReadOnlyList<(string Date, double Value)> ascending, string date)
    {
        if (ascending.Count == 0 || date.Length == 0) return null;

        foreach (var row in ascending)
            if (string.CompareOrdinal(row.Date, date) >= 0)
                return row.Value;      // the day itself, or the first one after it

        return ascending[^1].Value;    // nothing on or after: the last day that stood
    }
}
