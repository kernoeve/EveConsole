using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// What our courier contracts are carrying, as the user entered it — see <see cref="CourierCargo"/>
/// — counted for planning, never as assets.
///
/// <para><b>Out, and not yet delivered:</b> in transit to the destination. Served through
/// <see cref="ContractLag.MovesAsync"/> flagged <see cref="ContractMove.InTransit"/>, so the
/// planners that ask whether the goods exist — demand, purchasing, inventory levels, and the
/// hauling plan's cover for the destination's wants — count them and buy or haul nothing again,
/// while those that ask whether the goods are on a shelf now — a job's readiness, an order's
/// stock — leave them out. Taken off the start in the meantime, until the issuer's asset poll
/// shows them gone from it.</para>
///
/// <para><b>Delivered:</b> at the destination, as a finished contract's goods are, until the
/// issuer's asset poll shows them there.</para>
///
/// <para><b>Ticked before the contract was seen</b> (a worklist tick, ahead of the five-minute
/// contracts poll): matched to the courier from the same start to the same end made nearest the
/// tick — see <see cref="Match"/> — and counted as out from the moment of the tick meanwhile. A
/// tick no courier turns up for within <see cref="MatchWindow"/> lapses, and the trip comes back.</para>
/// </summary>
public static class CourierCargoLag
{
    /// <summary>How long a tick waits for its courier to turn up before it lapses.</summary>
    internal static readonly TimeSpan MatchWindow = TimeSpan.FromHours(24);

    /// <summary>How long before a tick its courier may have been made: the contract written in
    /// game first, the trip ticked off after.</summary>
    internal static readonly TimeSpan MadeBefore = TimeSpan.FromHours(12);

    /// <summary>A courier's cargo and where it is in its run.</summary>
    /// <param name="ContractId">0 for a tick not yet matched to a contract.</param>
    /// <param name="Out">Out and not yet delivered.</param>
    /// <param name="CompletedAt">When it was delivered, for a finished one.</param>
    public sealed record Run(
        int ContractId, long From, long To, string OwnerType, long OwnerId,
        DateTimeOffset IssuedAt, bool Out, DateTimeOffset? CompletedAt,
        IReadOnlyList<(int TypeId, long Quantity)> Lines);

    /// <summary>A courier of ours as the contracts poll has it, the most settled owner row speaking.</summary>
    internal sealed record Contract(
        int ContractId, long From, long To, string OwnerType, long OwnerId, string Status,
        DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? CompletedAt)
    {
        /// <summary>The contract's volume as ESI gives it; 0 when it does not.</summary>
        public double Volume { get; init; }
    }

    /// <summary>
    /// The goods' moves since the issuer's asset poll, as ContractLag's own moves.
    /// </summary>
    /// <param name="clocks">Each owner's asset clock, as ContractLag reads them.</param>
    internal static async Task<List<ContractMove>> MovesAsync(
        AppDbContext db, IReadOnlyDictionary<long, (string Type, DateTimeOffset At)> clocks,
        IReadOnlyCollection<int>? typeIds, CancellationToken ct)
    {
        var set   = typeIds as IReadOnlySet<int> ?? typeIds?.ToHashSet();
        var moves = new List<ContractMove>();
        foreach (var run in await RunsAsync(db, ct))
        {
            // Never polled: nothing to correct, as for any contract.
            if (!clocks.TryGetValue(run.OwnerId, out var clock)) continue;
            foreach (var (typeId, qty) in run.Lines)
            {
                if (set is not null && !set.Contains(typeId)) continue;
                if (run.IssuedAt > clock.At)
                    moves.Add(new ContractMove(run.ContractId, run.From, clock.Type, run.OwnerId, typeId, -qty, false, run.IssuedAt));
                if (run.Out)
                    moves.Add(new ContractMove(run.ContractId, run.To, clock.Type, run.OwnerId, typeId, +qty, false, run.IssuedAt)
                              { InTransit = true });
                else if (run.CompletedAt is { } done && done > clock.At)
                    moves.Add(new ContractMove(run.ContractId, run.To, clock.Type, run.OwnerId, typeId, +qty, false, done));
            }
        }
        return moves;
    }

    /// <summary>
    /// Every courier with cargo entered that still matters to a count: out, or delivered — and
    /// every tick still waiting for its courier, as out.
    /// </summary>
    public static async Task<List<Run>> RunsAsync(AppDbContext db, CancellationToken ct)
    {
        var cargo = await db.CourierCargo.AsNoTracking().ToListAsync(ct);
        if (cargo.Count == 0) return [];

        var now      = DateTimeOffset.UtcNow;
        var couriers = await CouriersAsync(db, cargo, ct);
        var matched  = Match(cargo, couriers.Values, now, await UnitVolumesAsync(db, cargo, ct));

        var runs = new List<Run>();
        foreach (var g in cargo.GroupBy(c => c.ContractId != 0 ? c.ContractId : matched.GetValueOrDefault(c.Id)))
        {
            var lines = g.GroupBy(c => c.TypeId).Select(x => (x.Key, x.Sum(c => c.Quantity))).Where(l => l.Item2 > 0).ToList();
            if (lines.Count == 0) continue;

            if (g.Key == 0)
            {
                // Ticked, no courier yet: out from the tick, for as long as one may still turn up.
                foreach (var tick in g.GroupBy(c => (c.FromId, c.ToId, c.AddedAt, c.OwnerType, c.OwnerId))
                                      .Where(t => now - t.Key.AddedAt <= MatchWindow))
                    runs.Add(new Run(0, tick.Key.FromId, tick.Key.ToId, tick.Key.OwnerType, tick.Key.OwnerId,
                                     tick.Key.AddedAt, true, null,
                                     [.. tick.GroupBy(c => c.TypeId).Select(x => (x.Key, x.Sum(c => c.Quantity)))]));
                continue;
            }

            if (!couriers.TryGetValue(g.Key, out var k)) continue;
            var expired = k.Status == "outstanding" && k.ExpiresAt is { } exp && exp < now;
            var outNow  = k.Status is "outstanding" or "in_progress" && !expired;
            // Deleted, failed, rejected or left to expire: not coming. Its goods are back on the
            // shelf, or lost, as the asset poll will say.
            if (!outNow && k.Status != "finished") continue;
            runs.Add(new Run(k.ContractId, k.From, k.To, k.OwnerType, k.OwnerId, k.IssuedAt,
                             outNow, k.Status == "finished" ? k.CompletedAt : null, lines));
        }
        return runs;
    }

    /// <summary>
    /// Which courier each tick waiting for one belongs to, by line id: the one of ours from the
    /// same start to the same end, made from <see cref="MadeBefore"/> before the tick to
    /// <see cref="MatchWindow"/> after, and not holding cargo of its own already — of those, one
    /// whose volume agrees with the tick's cargo first, then the one made nearest the tick. Ticks
    /// are matched oldest first.
    ///
    /// <para>⚠️ Volume before time: a trip too big for one contract is made as several, all on
    /// one route within a minute or two, and ticked off one by one after. Nearest in time alone
    /// pairs the first tick with the LAST contract made; the cargo the tick was corrected to is
    /// what tells them apart.</para>
    /// </summary>
    /// <param name="unitM3">Each item's volume, to size a tick's cargo; without it, time alone.</param>
    internal static Dictionary<long, int> Match(
        IReadOnlyList<CourierCargo> cargo, IEnumerable<Contract> couriers, DateTimeOffset now,
        IReadOnlyDictionary<int, double>? unitM3 = null)
    {
        var taken = cargo.Where(c => c.ContractId != 0).Select(c => c.ContractId).ToHashSet();
        var open  = couriers.Where(k => k.Status is not ("deleted" or "rejected" or "failed")).ToList();
        var found = new Dictionary<long, int>();

        foreach (var tick in cargo.Where(c => c.ContractId == 0)
                                  .GroupBy(c => (c.FromId, c.ToId, c.AddedAt))
                                  .OrderBy(t => t.Key.AddedAt).ThenBy(t => t.Min(c => c.Id)))
        {
            var (from, to, at) = tick.Key;
            var m3  = unitM3 is null ? 0 : tick.Sum(c => c.Quantity * unitM3.GetValueOrDefault(c.TypeId));
            var hit = open
                .Where(k => k.From == from && k.To == to && !taken.Contains(k.ContractId)
                            && k.IssuedAt >= at - MadeBefore && k.IssuedAt <= at + MatchWindow)
                .OrderBy(k => Agrees(m3, k.Volume) ? 0 : 1)
                .ThenBy(k => Math.Abs((k.IssuedAt - at).Ticks)).ThenBy(k => k.ContractId)
                .FirstOrDefault();
            if (hit is null) continue;
            taken.Add(hit.ContractId);
            foreach (var c in tick) found[c.Id] = hit.ContractId;
        }
        return found;
    }

    /// <summary>A tick's cargo and a contract the same size, to within a cubic metre or a
    /// hundredth — ESI rounds a contract's volume, and an item's may be off a little.</summary>
    private static bool Agrees(double tick, double contract) =>
        tick > 0 && contract > 0 && Math.Abs(tick - contract) <= Math.Max(1, contract * 0.01);

    /// <summary>The packaged volume of each item in the cargo, as everywhere else that hauls.</summary>
    private static async Task<Dictionary<int, double>> UnitVolumesAsync(
        AppDbContext db, IReadOnlyList<CourierCargo> cargo, CancellationToken ct)
    {
        var ids = cargo.Where(c => c.ContractId == 0).Select(c => c.TypeId).Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.SdeTypes.AsNoTracking()
            .Where(t => ids.Contains(t.TypeId))
            .Select(t => new { t.TypeId, t.PackagedVolume, t.Volume })
            .ToDictionaryAsync(t => t.TypeId, t => t.PackagedVolume > 0 ? t.PackagedVolume : t.Volume, ct);
    }

    /// <summary>
    /// Writes the matches <see cref="Match"/> finds onto the ticks, so the Contracts tool shows
    /// them on their contract and they can be edited there; and lets go of ticks no courier ever
    /// turned up for. Returns how many lines were attached.
    /// </summary>
    public static async Task<int> AttachAsync(AppDbContext db, CancellationToken ct)
    {
        var cargo = await db.CourierCargo.ToListAsync(ct);
        if (cargo.All(c => c.ContractId != 0)) return 0;

        var now     = DateTimeOffset.UtcNow;
        var matched = Match(cargo, (await CouriersAsync(db, cargo, ct)).Values, now, await UnitVolumesAsync(db, cargo, ct));
        foreach (var c in cargo.Where(c => c.ContractId == 0))
        {
            if (matched.TryGetValue(c.Id, out var id)) c.ContractId = id;
            else if (now - c.AddedAt > 2 * MatchWindow) db.CourierCargo.Remove(c);
        }
        await db.SaveChangesAsync(ct);
        return matched.Count;
    }

    /// <summary>Our couriers the cargo could be in: those it names, and those from a tick's start
    /// to its end. Keyed by contract.</summary>
    private static async Task<Dictionary<int, Contract>> CouriersAsync(
        AppDbContext db, IReadOnlyList<CourierCargo> cargo, CancellationToken ct)
    {
        var ids   = cargo.Where(c => c.ContractId != 0).Select(c => c.ContractId).Distinct().ToList();
        var froms = cargo.Where(c => c.ContractId == 0).Select(c => (long?)c.FromId).Distinct().ToList();

        // ⚠️ Dates filtered after reading: a DateTimeOffset in a LINQ Where does not translate on SQLite.
        var rows = await db.EsiContracts.AsNoTracking()
            .Where(c => c.Type == "courier" && c.OwnerType != "public"
                        && (ids.Contains(c.ContractId) || froms.Contains(c.StartLocationId)))
            .Select(c => new
            {
                c.ContractId, c.Status, c.ForCorporation, c.IssuerId, c.IssuerCorporationId,
                c.StartLocationId, c.EndLocationId, c.DateIssued, c.DateExpired, c.DateCompleted, c.Volume,
            })
            .ToListAsync(ct);

        return rows.GroupBy(r => r.ContractId).ToDictionary(g => g.Key, g =>
        {
            var c = g.OrderBy(r => OrderFulfilmentService.Settledness(r.Status)).First();
            return new Contract(
                g.Key, c.StartLocationId ?? 0, c.EndLocationId ?? 0,
                c.ForCorporation ? "corporation" : "character",
                c.ForCorporation ? c.IssuerCorporationId : c.IssuerId,
                c.Status, c.DateIssued, g.Max(r => r.DateExpired), g.Max(r => r.DateCompleted))
            {
                Volume = (double)g.Max(r => r.Volume),
            };
        });
    }
}
