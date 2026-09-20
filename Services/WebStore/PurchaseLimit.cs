using EveConsole.Models;

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
}
