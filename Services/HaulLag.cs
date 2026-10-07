using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Hauls ticked off at a loop's stops that the asset polls have not caught up with — see
/// <see cref="HaulMark"/>.
///
/// <para><b>Delivered</b> goods move like a contract's: out of the hangar they were picked up from,
/// into the one they were put down in. They are served through <see cref="ContractLag.MovesAsync"/>,
/// so every count that already corrects for contracts — stock levels, the order tracker, the job
/// and purchase planners, the hauling plan — sees a hauled leg the moment it is ticked, and the
/// job it was for can start before the asset poll would have said so.</para>
///
/// <para><b>Picked up and not yet delivered</b> goods are left out of that, on purpose. They are in
/// the pilot's hold: still owned, still in reach, just not on a shelf. Taken out of the stock counts
/// they would read as missing — and be bought again — while the pilot is carrying them across the
/// grid. Only the hauling plan uses them (<see cref="InTransitAsync"/>), as cargo already aboard.</para>
///
/// <para>Each side is measured against the asset clock of the owner whose hangar it is, the way
/// contracts and delivered jobs are, and stops counting once that owner's poll has run since.</para>
/// </summary>
public static class HaulLag
{
    /// <summary>How long a mark is kept at all. Long past any asset poll; only so the table does
    /// not grow without end when an owner is never polled again.</summary>
    internal static readonly TimeSpan KeepFor = TimeSpan.FromDays(2);

    /// <summary>
    /// The delivered legs' moves since each owner's asset poll, as contract moves (with no contract).
    /// </summary>
    /// <param name="clocks">Each owner's asset clock, as ContractLag reads them.</param>
    internal static async Task<List<ContractMove>> DeliveredMovesAsync(
        AppDbContext db, IReadOnlyDictionary<long, (string Type, DateTimeOffset At)> clocks,
        IReadOnlyCollection<int>? typeIds, CancellationToken ct)
    {
        // ⚠️ Read whole and filtered here: a DateTimeOffset in a LINQ Where does not translate on
        // SQLite, and the table holds an hour or two of ticks.
        var marks = await db.HaulMarks.AsNoTracking().Where(m => m.DeliveredAt != null).ToListAsync(ct);
        var set   = typeIds as IReadOnlySet<int> ?? typeIds?.ToHashSet();

        var moves = new List<ContractMove>();
        foreach (var m in marks)
        {
            if (set is not null && !set.Contains(m.TypeId)) continue;
            if (!clocks.TryGetValue(m.OwnerId, out var clock)) continue;   // never polled: nothing to correct

            if (m.PickedAt > clock.At)
                moves.Add(new ContractMove(0, m.FromId, m.OwnerType, m.OwnerId, m.TypeId, -m.Units, false, m.PickedAt));
            if (m.DeliveredAt is { } at && at > clock.At)
                moves.Add(new ContractMove(0, m.ToId, m.OwnerType, m.OwnerId, m.TypeId, +m.Units, false, at));
        }
        return moves;
    }

    /// <summary>
    /// What pilots are carrying: picked up at a stop, not yet put down, and not yet seen by the
    /// owner's asset poll — after which the snapshot has it, in the ship.
    /// </summary>
    public static async Task<List<HaulMark>> InTransitAsync(AppDbContext db, CancellationToken ct)
    {
        var open = await db.HaulMarks.AsNoTracking().Where(m => m.DeliveredAt == null).ToListAsync(ct);
        if (open.Count == 0) return open;

        var clocks = await AssetClocksAsync(db, ct);
        return open.Where(m => !clocks.TryGetValue(m.OwnerId, out var at) || m.PickedAt > at).ToList();
    }

    /// <summary>Each owner's last asset poll.</summary>
    internal static async Task<Dictionary<long, DateTimeOffset>> AssetClocksAsync(AppDbContext db, CancellationToken ct) =>
        (await db.EsiCallRecords.AsNoTracking()
            .Where(r => r.Endpoint == "char.assets" || r.Endpoint == "corp.assets")
            .Select(r => new { r.OwnerId, r.LastCalledAt })
            .ToListAsync(ct))
        .GroupBy(r => r.OwnerId)
        .ToDictionary(g => g.Key, g => g.Max(r => r.LastCalledAt));
}
