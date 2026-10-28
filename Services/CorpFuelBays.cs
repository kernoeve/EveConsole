using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Keeps <see cref="CorpFuelBay"/> current: the fuel in each bay, and the reading its current run
/// started from, so the burn since the last refuel can be measured.
/// </summary>
public static class CorpFuelBays
{
    /// <summary>
    /// Records one reading of the bays at <paramref name="locations"/>.
    ///
    /// <para>Only those locations are touched. The structures are read from the corp's assets and
    /// the towers from their own endpoint, on different polls, and neither may clear the other's
    /// rows. A location in the list with nothing in its bay loses its rows — the fuel has run out
    /// or been taken.</para>
    ///
    /// <para>A quantity that went UP is a refuel, and starts a new run from this reading. One that
    /// went down or held keeps the run's start, so the average burn is taken over the whole time
    /// since the bay was last filled: a day or more, where one poll's hourly step would be noise
    /// from ESI's caching.</para>
    /// </summary>
    public static async Task RecordAsync(
        AppDbContext db, long corpId, IReadOnlyCollection<long> locations,
        IEnumerable<(long LocationId, int TypeId, long Quantity)> fuel,
        DateTimeOffset now, CancellationToken ct)
    {
        var read = fuel
            .GroupBy(f => (f.LocationId, f.TypeId))
            .ToDictionary(g => g.Key, g => g.Sum(f => f.Quantity));

        var locationSet = locations.ToHashSet();
        var kept = (await db.EsiCorpFuelBays
                .Where(b => b.CorporationId == corpId)
                .ToListAsync(ct))
            .Where(b => locationSet.Contains(b.LocationId))
            .ToDictionary(b => (b.LocationId, b.TypeId));

        foreach (var (key, row) in kept)
            if (!read.ContainsKey(key)) db.EsiCorpFuelBays.Remove(row);

        foreach (var ((location, type), quantity) in read)
        {
            if (!kept.TryGetValue((location, type), out var row))
            {
                db.EsiCorpFuelBays.Add(new CorpFuelBay
                {
                    CorporationId    = corpId,
                    LocationId       = location,
                    TypeId           = type,
                    Quantity         = quantity,
                    SeenAt           = now,
                    BaselineQuantity = quantity,
                    BaselineAt       = now,
                });
                continue;
            }

            if (quantity > row.Quantity)
            {
                row.BaselineQuantity = quantity;
                row.BaselineAt       = now;
            }
            row.Quantity = quantity;
            row.SeenAt   = now;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The shortest run a burn is measured over. Less, and one poll's step — or an
    /// hour's cached reply — is most of the figure.</summary>
    public static readonly TimeSpan ShortestRun = TimeSpan.FromHours(6);

    /// <summary>Units an hour since the bay was last filled, or null while the run is shorter than
    /// <see cref="ShortestRun"/>.</summary>
    public static double? MeasuredPerHour(CorpFuelBay bay)
    {
        var hours = (bay.SeenAt - bay.BaselineAt).TotalHours;
        if (hours < ShortestRun.TotalHours) return null;
        return Math.Max(0, bay.BaselineQuantity - bay.Quantity) / hours;
    }
}
