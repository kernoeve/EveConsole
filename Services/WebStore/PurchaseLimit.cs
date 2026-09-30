using EveConsole.Data;
using EveConsole.Localization;
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
///
/// <para>The words are buyer text (StoreText), in the language of the scope they are written in:
/// a mail is written inside the store's (<see cref="LanguageScope"/>), so its buyers read the rule
/// in theirs, and the Stores screen outside any, so the owner reads the same rule in the app's.</para>
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

    /// <summary>What the limit counts over — "of each item" — as it sits in <see cref="Describe"/>
    /// and in <see cref="Excess.Words"/>.</summary>
    public static string ScopeWords(Store store) => store.LimitScope switch
    {
        "group" => StoreText.LimitScopeGroup,
        "store" => StoreText.LimitScopeStore,
        _       => StoreText.LimitScopeItem,
    };

    /// <summary>The period — "per day", "per 3 days", "ever" — as it sits beside <see cref="ScopeWords"/>.</summary>
    public static string PeriodWords(Store store)
    {
        var n = Math.Max(1, store.LimitPeriodCount);
        return store.LimitPeriod switch
        {
            // One is said without its number, "per day" rather than "per 1 day", so it is an entry of
            // its own. A family's One form, where a language has one, is for counts such as
            // Russian's 21, which keep their number.
            "days"   => n == 1 ? StoreText.LimitPerDay
                               : Plurals.Format(StoreText.ResourceManager, nameof(StoreText.LimitPerDaysOther), n),
            "months" => n == 1 ? StoreText.LimitPerMonth
                               : Plurals.Format(StoreText.ResourceManager, nameof(StoreText.LimitPerMonthsOther), n),
            "years"  => n == 1 ? StoreText.LimitPerYear
                               : Plurals.Format(StoreText.ResourceManager, nameof(StoreText.LimitPerYearsOther), n),
            _        => StoreText.LimitEver,
        };
    }

    /// <summary>"Each buyer may order 1 unit of each item ever."</summary>
    public static string Describe(Store store)
    {
        var units = Math.Max(1, store.LimitUnits);
        return Plurals.Format(StoreText.ResourceManager, nameof(StoreText.LimitRuleOther), units,
                              ScopeWords(store), PeriodWords(store));
    }

    /// <summary>
    /// An order line that would take a buyer past the store's limit, as sums: put into words by
    /// <see cref="Words"/> for whoever reads them — the buyer in the store's language, with the
    /// item as their list names it, and the owner in the app's.
    /// </summary>
    public sealed record Excess(Store Store, int TypeId, long Units, long Had)
    {
        /// <summary>"Over the store's limit of 1 of each item ever: 2 × Archon on top of 0 already
        /// ordered." In the language of the scope it is called in.</summary>
        /// <param name="name">The item's name, as this reader knows it.</param>
        public string Words(string name) =>
            string.Format(StoreText.LimitOver, Math.Max(1, Store.LimitUnits), ScopeWords(Store), PeriodWords(Store),
                          Units, name, Had);
    }

    /// <summary>
    /// Why the order would take the buyer past the store's limit, or null. Counts the buyer's
    /// orders in this store that were not cancelled — by type, group or altogether, within the
    /// period — which is the same sum the site shows them. One check for every doorway: the web
    /// sync when it books, the mail store when it books, the site before it even asks.
    /// </summary>
    public static async Task<Excess?> OverLimitAsync(
        AppDbContext db, Store store, long buyerId, List<(int TypeId, long Units)> lines, CancellationToken ct)
    {
        var (taken, Key) = await TakenAsync(db, store, buyerId, lines.Select(l => l.TypeId), ct);

        var limit = Math.Max(1, store.LimitUnits);
        foreach (var (typeId, units) in lines)
        {
            var key = Key(typeId);
            var had = taken.GetValueOrDefault(key);
            if (had + units > limit)
                return new Excess(store, typeId, units, had);
            taken[key] = had + units;   // two lines against one key add up within the order
        }
        return null;
    }

    /// <summary>
    /// The items among these a buyer may not order any more: what they have taken against the
    /// limit leaves nothing. The site greys these rows out; the mailed price list dims them and
    /// says so, since EVE mail has no strikethrough to draw.
    /// </summary>
    public static async Task<HashSet<int>> BlockedAsync(
        AppDbContext db, Store store, long buyerId, IReadOnlyCollection<int> typeIds, CancellationToken ct)
    {
        var (taken, key) = await TakenAsync(db, store, buyerId, typeIds, ct);
        var limit = Math.Max(1, store.LimitUnits);
        return typeIds.Where(t => taken.GetValueOrDefault(key(t)) >= limit).ToHashSet();
    }

    /// <summary>What the buyer has taken against the limit, by scope key, with the key of any type.</summary>
    private static async Task<(Dictionary<string, long> Taken, Func<int, string> Key)> TakenAsync(
        AppDbContext db, Store store, long buyerId, IEnumerable<int> typeIds, CancellationToken ct)
    {
        var since   = Since(store, DateTimeOffset.UtcNow);
        var history = (await db.TrackedOrders.AsNoTracking()
                .Where(o => o.StoreId == store.Id && o.BuyerId == buyerId && o.Status != "canceled")
                .Select(o => new { o.TypeId, o.Units, o.CreatedAt })
                .ToListAsync(ct))
            .Where(o => since is null || o.CreatedAt >= since)   // compared here: a DateTimeOffset in a Where does not translate on SQLite
            .ToList();

        var all    = history.Select(o => o.TypeId).Concat(typeIds).Distinct().ToList();
        var groups = await db.SdeTypes.AsNoTracking()
            .Where(t => all.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.GroupId, ct);
        string Key(int typeId) => store.LimitScope switch
        {
            "group" => $"g:{groups.GetValueOrDefault(typeId)}",
            "store" => "store",
            _       => $"t:{typeId}",
        };

        var taken = new Dictionary<string, long>();
        foreach (var o in history) taken[Key(o.TypeId)] = taken.GetValueOrDefault(Key(o.TypeId)) + o.Units;
        return (taken, Key);
    }
}
