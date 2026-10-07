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
/// <para><b>Picked up and not yet delivered</b> goods stay on the shelf they came off, in every
/// count but the hauling plan's, until they are put down. They are in the pilot's hold: still
/// owned, still in reach, just in a ship, which no stock count holds. ⚠️ Taken out of the counts —
/// as the asset poll of the hangar they left does, an hour or less later — they would read as
/// missing and be bought or built again while the pilot is carrying them across the grid. Only the
/// hauling plan sees them where they are (<see cref="BasisAsync"/>), as cargo already aboard.</para>
///
/// <para>A delivery is measured against the asset clock of the owner whose hangar it is, the way
/// contracts and delivered jobs are, and stops counting once that owner's poll has run since. What
/// is in a hold is measured against the pilot's own poll, which is the one that lists the ship.</para>
/// </summary>
public static class HaulLag
{
    /// <summary>How long a mark is kept at all. Long past any asset poll; only so the table does
    /// not grow without end when an owner is never polled again.</summary>
    internal static readonly TimeSpan KeepFor = TimeSpan.FromDays(2);

    /// <summary>
    /// The ticked legs' moves since each owner's asset poll, as contract moves (with no contract):
    /// what was delivered, and what is aboard off a shelf its owner's poll has since seen empty —
    /// put back on that shelf until it is put down.
    /// </summary>
    /// <param name="clocks">Each owner's asset clock, as ContractLag reads them.</param>
    internal static async Task<List<ContractMove>> MovesAsync(
        AppDbContext db, IReadOnlyDictionary<long, (string Type, DateTimeOffset At)> clocks,
        IReadOnlyCollection<int>? typeIds, CancellationToken ct)
    {
        if (!await db.HaulMarks.AnyAsync(ct)) return [];
        // ⚠️ Read whole and filtered here: a DateTimeOffset in a LINQ Where does not translate on
        // SQLite, and the table holds an hour or two of ticks.
        var marks = await db.HaulMarks.AsNoTracking().Where(m => m.DeliveredAt != null).ToListAsync(ct);
        var set   = typeIds as IReadOnlySet<int> ?? typeIds?.ToHashSet();

        var moves = new List<ContractMove>();
        foreach (var m in marks)
        {
            if (set is not null && !set.Contains(m.TypeId)) continue;
            if (!clocks.TryGetValue(m.OwnerId, out var clock)) continue;   // never polled: nothing to correct

            // Put down out of a hold with no pick-up ticked: it left a ship, which no stock count
            // holds, so only the landing moves anything.
            if (m.PickedAt > clock.At && !m.FromHold)
                moves.Add(new ContractMove(0, m.FromId, m.OwnerType, m.OwnerId, m.TypeId, -m.Units, false, m.PickedAt));
            if (m.DeliveredAt is { } at && at > clock.At)
                moves.Add(new ContractMove(0, m.ToId, m.OwnerType, m.OwnerId, m.TypeId, +m.Units, false, at));
        }

        // Aboard, off a shelf the owner's poll has seen empty since: back on it, until it is down.
        var ships = (await db.CharacterStatuses.AsNoTracking()
                .Select(s => new { s.CharacterId, s.ShipItemId }).ToListAsync(ct))
            .ToDictionary(s => s.CharacterId, s => s.ShipItemId);
        foreach (var (m, units) in (await BasisAsync(db, ships, ct)).Shelved)
            if ((set is null || set.Contains(m.TypeId)) && clocks.ContainsKey(m.OwnerId))
                moves.Add(new ContractMove(0, m.FromId, m.OwnerType, m.OwnerId, m.TypeId, +units, false, m.PickedAt));
        return moves;
    }

    /// <summary>So many of an item ticked off from one place to another, and not yet seen by the
    /// asset poll. <c>From</c> below zero is a pilot's hold (HaulLoopPlanner.HoldOf).</summary>
    public sealed record Leg(long PilotId, long From, long To, int TypeId, long Units);

    /// <summary>
    /// What the hauling plan is made from, given the legs ticked off since the asset poll.
    ///
    /// <para><b>⚠️ The plan is made as if none of them had been flown yet, and they are then taken
    /// off it.</b> Planning afresh from the stock as it now stands — part of it on a shelf, part in
    /// a hold, part delivered — is a different question from the one the loop was planned to
    /// answer, and gets a different answer: a source half emptied by a full hold now "holds the
    /// whole amount" somewhere else, a level topped up past its trigger stops asking for the rest,
    /// and the stops reshuffle under a pilot who has done exactly what was asked. Rewound, the same
    /// stock gives the same plan, less what has been done — refresh after refresh, until an asset
    /// poll brings in what is actually there, and the plan is made from that.</para>
    /// </summary>
    /// <param name="Rewind">Added to the stock counts to undo the ticked legs: back on the shelf
    /// they came off, out of the hangar they went to.</param>
    /// <param name="Hauled">The legs to take off the plan once it is made: delivered, or aboard.</param>
    /// <param name="Aboard">Cargo in a pilot's hold with a tick saying where it is going.</param>
    /// <param name="Loose">Cargo in a pilot's hold with no tick saying where — loaded without one,
    /// or there before — as it stood before the ticked legs: what has been put down out of it
    /// since is still in it here, and comes off the plan with the other hauled legs.</param>
    /// <param name="Shelved">Picked-up marks still aboard whose shelf the owner's poll has seen
    /// empty since, and how much of each: the stock counts put it back there (see
    /// <see cref="MovesAsync"/>), so nothing reads it as missing while it is carried.</param>
    public sealed record Basis(
        IReadOnlyList<(long Site, int TypeId, long Units)> Rewind,
        IReadOnlyList<Leg> Hauled,
        IReadOnlyList<Leg> Aboard,
        IReadOnlyList<(long PilotId, int TypeId, long Units)> Loose,
        IReadOnlyList<(HaulMark Mark, long Units)> Shelved);

    /// <summary>
    /// The plan's basis: see <see cref="Basis"/>.
    ///
    /// <para><b>What is in a hold</b> is the ship's cargo in the pilot's last asset poll, plus what
    /// has been picked up since and less what has been put down since. The ticks say where it is
    /// going; where they and the hold disagree, the newest are taken to be what is still aboard.</para>
    ///
    /// <para><b>⚠️ A pick-up stays aboard until it is put down, whoever's asset poll runs.</b> It
    /// used to settle on the poll of the hangar it came from — and a corporation polled three
    /// minutes after a pick-up took the goods off its shelves while the pilot's own poll, twenty
    /// minutes older, still showed the hold empty: the cargo was in nobody's count, and the loop
    /// went on past the structure it was for. The shelf is reckoned against its owner's poll, the
    /// hold against the pilot's.</para>
    /// </summary>
    /// <param name="ships">The ship each pilot whose hold is wanted is sitting in: the lane's
    /// pilots, and whoever has ticked something off. Null where it is not known.</param>
    public static async Task<Basis> BasisAsync(
        AppDbContext db, IReadOnlyDictionary<long, long?> ships, CancellationToken ct)
    {
        var now    = DateTimeOffset.UtcNow;
        var marks  = (await db.HaulMarks.AsNoTracking().ToListAsync(ct))
            .Where(m => now - m.PickedAt <= KeepFor).ToList();
        var clocks = await AssetClocksAsync(db, ct);

        var rewind = new List<(long Site, int TypeId, long Units)>();
        var hauled = new List<Leg>();
        var aboard = new List<Leg>();
        var loose  = new List<(long PilotId, int TypeId, long Units)>();
        var shelved = new List<(HaulMark Mark, long Units)>();

        // Delivered, and the owner's poll has not seen it land: back where it was, then off the
        // plan. Put down out of a hold with no pick-up ticked, it goes back into the hold (below).
        var dropped = new List<HaulMark>();
        foreach (var m in marks.Where(m => m.DeliveredAt is not null))
        {
            if (!clocks.TryGetValue(m.OwnerId, out var at) || m.DeliveredAt <= at) continue;
            if (m.FromHold) dropped.Add(m);
            else rewind.Add((m.FromId, m.TypeId, +m.Units));
            rewind.Add((m.ToId, m.TypeId, -m.Units));
            hauled.Add(new Leg(m.PilotId, m.FromId, m.ToId, m.TypeId, m.Units));
        }

        // Each pilot's hold, from their ship's cargo and the ticks since.
        var pilots  = ships.Keys.Concat(marks.Select(m => m.PilotId)).Distinct().OrderBy(p => p).ToList();
        var shipIds = ships.Values.OfType<long>().Distinct().ToList();
        var cargo   = shipIds.Count == 0 ? [] : await ShipCargoAsync(db, shipIds, ct);

        foreach (var p in pilots)
        {
            var mine = marks.Where(m => m.PilotId == p).ToList();
            var open = mine.Where(m => m.DeliveredAt is null && !m.FromHold).ToList();

            // What is in the hold now, by type. Without a poll of this pilot's ship, the ticks alone.
            Dictionary<int, long> hold;
            if (ships.GetValueOrDefault(p) is { } ship && clocks.TryGetValue(p, out var polled))
            {
                hold = cargo.Where(c => c.Ship == ship).GroupBy(c => c.TypeId)
                            .ToDictionary(g => g.Key, g => g.Sum(c => c.Units));
                foreach (var m in mine)
                {
                    if (m.PickedAt > polled && !m.FromHold) hold[m.TypeId] = hold.GetValueOrDefault(m.TypeId) + m.Units;
                    if (m.DeliveredAt > polled)             hold[m.TypeId] = hold.GetValueOrDefault(m.TypeId) - m.Units;
                }
            }
            else
                hold = open.GroupBy(m => m.TypeId).ToDictionary(g => g.Key, g => g.Sum(m => m.Units));

            foreach (var (typeId, units) in hold.OrderBy(h => h.Key))
            {
                var left = Math.Max(0, units);

                // The ticked pick-ups, newest first: what came aboard last is likeliest still there.
                foreach (var m in open.Where(m => m.TypeId == typeId)
                                      .OrderByDescending(m => m.PickedAt).ThenByDescending(m => m.Id))
                {
                    var take = Math.Min(left, m.Units);
                    if (take <= 0) break;
                    left -= take;
                    aboard.Add(new Leg(p, m.FromId, m.ToId, typeId, take));
                    hauled.Add(new Leg(p, m.FromId, m.ToId, typeId, take));
                    // Off a shelf the owner's poll has since seen empty: the stock counts put it
                    // back there (MovesAsync), so the plan's rewind has nothing to add.
                    if (clocks.TryGetValue(m.OwnerId, out var seen) && seen > m.PickedAt)
                        shelved.Add((m, take));
                }

                // The rest has no tick saying where — and what was put down out of it since the
                // poll is in it again here, for the plan to make and then take off.
                var back = left + dropped.Where(m => m.PilotId == p && m.TypeId == typeId).Sum(m => m.Units);
                if (back > 0) loose.Add((p, typeId, back));
            }
        }

        return new Basis(rewind, hauled, aboard, loose, shelved);
    }

    /// <summary>Each ship's cargo hold in the asset snapshot: what is in it, and one level into
    /// the containers in it.</summary>
    private static async Task<List<(long Ship, int TypeId, long Units)>> ShipCargoAsync(
        AppDbContext db, List<long> ships, CancellationToken ct)
    {
        var top = await db.EsiAssets.AsNoTracking()
            .Where(a => ships.Contains(a.LocationId) && a.LocationFlag == CargoFlag)
            .Select(a => new { a.ItemId, a.LocationId, a.TypeId, a.Quantity })
            .ToListAsync(ct);
        var boxes = top.Select(a => a.ItemId).ToList();
        var inner = boxes.Count == 0 ? [] : await db.EsiAssets.AsNoTracking()
            .Where(a => boxes.Contains(a.LocationId))
            .Select(a => new { a.LocationId, a.TypeId, a.Quantity })
            .ToListAsync(ct);
        var shipOf = top.ToDictionary(a => a.ItemId, a => a.LocationId);

        return [.. top.Select(a => (a.LocationId, a.TypeId, (long)a.Quantity)),
                .. inner.Select(a => (shipOf[a.LocationId], a.TypeId, (long)a.Quantity))];
    }

    /// <summary>The ship's cargo hold, as the assets list flags it.</summary>
    private const string CargoFlag = "Cargo";

    /// <summary>Each owner's last asset poll.</summary>
    internal static async Task<Dictionary<long, DateTimeOffset>> AssetClocksAsync(AppDbContext db, CancellationToken ct) =>
        (await db.EsiCallRecords.AsNoTracking()
            .Where(r => r.Endpoint == "char.assets" || r.Endpoint == "corp.assets")
            .Select(r => new { r.OwnerId, r.LastCalledAt })
            .ToListAsync(ct))
        .GroupBy(r => r.OwnerId)
        .ToDictionary(g => g.Key, g => g.Max(r => r.LastCalledAt));
}
