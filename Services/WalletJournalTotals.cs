using System.Runtime.CompilerServices;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Wallet-journal sums for the player's own wallets, with ISK moved between those wallets left
/// out. One implementation for the Overview pies, the Wallet tool's charts and the Income &amp;
/// Expense tool, which had three copies of the same query and the same blind spot.
///
/// <para>The player is every character plus every personal corporation. A donation from one of
/// their characters to another, a corporation paying one of its own characters, a contract
/// between the two — none of it is income or expense, it is the same ISK changing pockets. It
/// was counted as both: measured over a year on one account, 251B of "expense" and 251B of
/// "income" from donations alone, 159B of "expense" from a corporation moving ISK between its
/// own divisions. Nor does it net out on its own, because the receiving wallet is often one the
/// app does not poll — so the debit lands and the matching credit never does.</para>
///
/// <para>⚠️ What makes a row internal is that BOTH parties belong to the player and the ISK
/// actually changed wallets. The same-party rows are deliberately kept: a market escrow names
/// the character on both sides and is the only journal trace a buy order leaves, so dropping it
/// would erase every purchase from the expense side. The one same-party row that is a transfer
/// is a corporation's own division move, which the journal files as a withdrawal from the
/// corporation to itself.</para>
///
/// <para>Membership is by party id, not by which wallets are polled: a character without a
/// token is still the player's, and ISK sent to it is still internal.</para>
/// </summary>
public static class WalletJournalTotals
{
    /// <summary>Every id that is the player: all characters and all personal corporations.</summary>
    public static async Task<HashSet<long>> PlayerPartyIdsAsync(AppDbContext db, CancellationToken ct = default)
    {
        var ids = new HashSet<long>();
        foreach (var id in await db.Characters.AsNoTracking().Select(c => c.Id).ToListAsync(ct)) ids.Add(id);
        foreach (var id in await db.Corporations.AsNoTracking().Where(c => c.IsPersonal).Select(c => c.Id).ToListAsync(ct)) ids.Add(id);
        return ids;
    }

    /// <summary>
    /// A WHERE fragment, beginning with AND, that leaves out transfers between the player's own
    /// wallets. Empty when the player has fewer than two wallets, since nothing can then be
    /// internal. The ids are the database's own integers, spliced rather than bound, as every
    /// owner list in the app already is.
    /// </summary>
    public static string ExcludeInternalTransfers(IReadOnlyCollection<long> partyIds)
    {
        if (partyIds.Count < 2) return "";
        var ids = string.Join(",", partyIds);
        // COALESCE, because NOT (NULL AND …) is NULL and would drop every row with a missing
        // party — and a row with no party on one side cannot be a transfer between two of ours.
        return $"""
             AND NOT (COALESCE("FirstPartyId", 0) IN ({ids}) AND COALESCE("SecondPartyId", 0) IN ({ids})
                      AND ("FirstPartyId" <> "SecondPartyId" OR "RefType" = 'corporation_account_withdrawal'))
            """;
    }

    /// <summary>Signed totals per RefType across the given wallets since the cutoff.</summary>
    public static async Task<Dictionary<string, decimal>> ByRefTypeAsync(
        AppDbContext db, IEnumerable<(string OwnerType, long OwnerId)> owners, DateTimeOffset cutoff,
        CancellationToken ct = default)
    {
        var filter = ExcludeInternalTransfers(await PlayerPartyIdsAsync(db, ct));
        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ot, oid) in owners)
        {
            // Amount is TEXT on SQLite; the CAST is what makes SUM arithmetic on both engines.
            var rows = await db.Database.SqlQuery<RefRow>(FormattableStringFactory.Create(
                """
                SELECT "RefType", COALESCE(SUM(CAST("Amount" AS DOUBLE PRECISION)), 0.0) AS "Total"
                FROM "EsiWalletJournal"
                WHERE "OwnerType" = {0} AND "OwnerId" = {1} AND "Date" >= {2}
                """ + filter + """

                GROUP BY "RefType"
                """, ot, oid, cutoff)).ToListAsync(ct);
            foreach (var r in rows)
                totals[r.RefType] = totals.GetValueOrDefault(r.RefType) + (decimal)r.Total;
        }
        return totals;
    }

    /// <summary>Income and expense per calendar day (UTC, "yyyy-MM-dd") across the given wallets.</summary>
    public static async Task<Dictionary<string, (decimal Income, decimal Expense)>> ByDayAsync(
        AppDbContext db, IEnumerable<(string OwnerType, long OwnerId)> owners, DateTimeOffset cutoff,
        CancellationToken ct = default)
    {
        var filter = ExcludeInternalTransfers(await PlayerPartyIdsAsync(db, ct));
        var days = new Dictionary<string, (decimal Income, decimal Expense)>();
        foreach (var (ot, oid) in owners)
        {
            var rows = await db.Database.SqlQuery<DayRow>(FormattableStringFactory.Create(
                """
                SELECT substr(CAST("Date" AS TEXT), 1, 10) AS "Day",
                       COALESCE(SUM(CASE WHEN CAST("Amount" AS DOUBLE PRECISION) > 0 THEN CAST("Amount" AS DOUBLE PRECISION) ELSE 0 END), 0.0) AS "Income",
                       COALESCE(SUM(CASE WHEN CAST("Amount" AS DOUBLE PRECISION) < 0 THEN -CAST("Amount" AS DOUBLE PRECISION) ELSE 0 END), 0.0) AS "Expense"
                FROM "EsiWalletJournal"
                WHERE "OwnerType" = {0} AND "OwnerId" = {1} AND "Date" >= {2}
                """ + filter + """

                GROUP BY substr(CAST("Date" AS TEXT), 1, 10)
                """, ot, oid, cutoff)).ToListAsync(ct);
            foreach (var d in rows)
            {
                var cur = days.GetValueOrDefault(d.Day);
                days[d.Day] = (cur.Income + (decimal)d.Income, cur.Expense + (decimal)d.Expense);
            }
        }
        return days;
    }

    private sealed class RefRow { public string RefType { get; set; } = ""; public double Total   { get; set; } }
    private sealed class DayRow { public string Day     { get; set; } = ""; public double Income  { get; set; } public double Expense { get; set; } }
}
