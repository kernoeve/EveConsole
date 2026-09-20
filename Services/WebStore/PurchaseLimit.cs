using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.WebStore;

/// <summary>
/// The store's per-buyer purchase limit in dates and in words — shared by the booking check, the
/// push and the Config tab, so all three say the same thing.
///
/// <para>The limit is so many units of each item type, of each item group, or of anything in the
/// store, counted over the buyer's orders in the store that were not cancelled, within a rolling
/// period or ever. Off by default; a programme that hands out the first hull of each kind is what
/// it exists for.</para>
/// </summary>
public static class PurchaseLimit
{
    /// <summary>The start of the period, or null for all time. Months and years step the calendar, not 30 or 365 days.</summary>
    public static DateTimeOffset? Since(Store store, DateTimeOffset now)
    {
        var n = Math.Max(1, store.LimitPeriodCount);
        return store.LimitPeriod switch
        {
            "days"   => now.AddDays(-n),
            "months" => now.AddMonths(-n),
            "years"  => now.AddYears(-n),
            _        => null,
        };
    }

    public static string ScopeWords(Store store) => store.LimitScope switch
    {
        "group" => "of each item group",
        "store" => "from the store",
        _       => "of each item",
    };

    public static string PeriodWords(Store store)
    {
        var n = Math.Max(1, store.LimitPeriodCount);
        return store.LimitPeriod switch
        {
            "days"   => n == 1 ? "per day"   : $"per {n} days",
            "months" => n == 1 ? "per month" : $"per {n} months",
            "years"  => n == 1 ? "per year"  : $"per {n} years",
            _        => "ever",
        };
    }

    /// <summary>"Each buyer may order 1 unit of each item ever."</summary>
    public static string Describe(Store store)
    {
        var units = Math.Max(1, store.LimitUnits);
        return $"Each buyer may order {units:N0} {(units == 1 ? "unit" : "units")} {ScopeWords(store)} {PeriodWords(store)}.";
    }

    /// <summary>
    /// Why the order would take the buyer past the store's limit, or null. Counts the buyer's
    /// orders in this store that were not cancelled — by type, group or altogether, within the
    /// period — which is the same sum the site shows them. One check for every doorway: the web
    /// sync when it books, the mail store when it books, the site before it even asks.
    /// </summary>
    public static async Task<string?> OverLimitAsync(
        AppDbContext db, Store store, long buyerId, List<(int TypeId, long Units)> lines,
        Func<int, string> nameOf, CancellationToken ct)
    {
        var since   = Since(store, DateTimeOffset.UtcNow);
        var history = (await db.TrackedOrders.AsNoTracking()
                .Where(o => o.StoreId == store.Id && o.BuyerId == buyerId && o.Status != "canceled")
                .Select(o => new { o.TypeId, o.Units, o.CreatedAt })
                .ToListAsync(ct))
            .Where(o => since is null || o.CreatedAt >= since)   // compared here: a DateTimeOffset in a Where does not translate on SQLite
            .ToList();

        var typeIds = history.Select(o => o.TypeId).Concat(lines.Select(l => l.TypeId)).Distinct().ToList();
        var groups  = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.GroupId, ct);
        string Key(int typeId) => store.LimitScope switch
        {
            "group" => $"g:{groups.GetValueOrDefault(typeId)}",
            "store" => "store",
            _       => $"t:{typeId}",
        };

        var taken = new Dictionary<string, long>();
        foreach (var o in history) taken[Key(o.TypeId)] = taken.GetValueOrDefault(Key(o.TypeId)) + o.Units;

        var limit = Math.Max(1, store.LimitUnits);
        foreach (var (typeId, units) in lines)
        {
            var key = Key(typeId);
            var had = taken.GetValueOrDefault(key);
            if (had + units > limit)
                return $"Over the store's limit of {limit:N0} {ScopeWords(store)} {PeriodWords(store)}: "
                     + $"{units:N0} × {nameOf(typeId)} on top of {had:N0} already ordered.";
            taken[key] = had + units;   // two lines against one key add up within the order
        }
        return null;
    }
}
