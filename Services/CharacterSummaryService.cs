using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>One character, everything the summary grid shows about them.</summary>
/// <param name="PodValue">What the implants currently plugged in are worth. Zero is a real
/// answer — an empty clone — and is shown as such rather than as a blank.</param>
/// <param name="QueueEnds">When the last skill in the queue finishes, or null for an empty
/// queue. Held as the date rather than a formatted span so the column sorts by time remaining
/// instead of alphabetically by "3d".</param>
public sealed record CharacterSummaryRow(
    long   CharacterId,
    string Name,
    string CorpName,
    string AllianceName,
    bool   Online,
    DateTimeOffset? LastSeen,
    string Location,
    string Ship,
    double PodValue,
    string HomeStation,
    int    QueueLength,
    DateTimeOffset? QueueEnds,
    long   TotalSp,
    decimal Isk,
    double AssetValue,
    int ManufacturingFree, int ManufacturingTotal,
    int ReactionFree,      int ReactionTotal,
    int ScienceFree,       int ScienceTotal,
    // Whether the worklist may put that kind of job on this character. A pilot with eleven
    // manufacturing slots the tool is not allowed to use has no available slots in any sense
    // that matters here, and showing the capacity would invite planning against it.
    bool UsesManufacturing = false,
    bool UsesReaction      = false,
    bool UsesScience       = false);

/// <summary>
/// The cross-character view: one row per authorised character, gathered from what polling has
/// already stored.
///
/// <para>Reads only. Nothing here calls ESI — every field comes from a table the poller fills, so
/// opening the tab costs a handful of queries and no API budget. The cost of that is staleness,
/// which is why the row carries when each character was last seen rather than implying the numbers
/// are live.</para>
///
/// <para>Built in one pass over each table rather than per character. Eighteen characters times a
/// dozen lookups is two hundred round trips for a grid that fits on one screen.</para>
/// </summary>
public class CharacterSummaryService(IDbContextFactory<AppDbContext> dbFactory, Api.EsiClient esi)
{
    /// <summary>
    /// Alliance names, resolved once and kept for the session.
    ///
    /// <para>Nothing stores them: characters carry an alliance id and there is no alliance table,
    /// so the only way to a name is a public ESI call. There are only ever a handful of distinct
    /// alliances across one player's alts, and they change on the scale of months, so one call
    /// each per session is the whole cost. A failure leaves the id showing rather than a blank —
    /// the column is scanned to tell one alliance from another, and an id still does that.</para>
    /// </summary>
    private readonly Dictionary<int, string> _allianceNames = new();

    /// <summary>
    /// Corporation names for corps the app is not authorised into.
    ///
    /// <para>The Corporations table only holds corps with a token, so an alt sitting in someone
    /// else's corp has no name there. Same treatment as alliances: one public call, cached.</para>
    /// </summary>
    private readonly Dictionary<int, string> _corpNames = new();

    // Slot capacity skills. Every character gets one free slot of each kind, and these add to it.
    private const int MassProduction         = 3387;
    private const int AdvancedMassProduction = 24625;
    private const int MassReactions          = 45749;
    private const int AdvancedMassReactions  = 45748;
    private const int LaboratoryOperation    = 3406;
    private const int AdvancedLabOperation   = 24624;

    public async Task<List<CharacterSummaryRow>> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var chars = await db.Characters.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        if (chars.Count == 0) return [];

        var ids = chars.Select(c => c.Id).ToList();

        var corps = await db.Corporations.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var status = await db.CharacterStatuses.AsNoTracking()
            .Where(s => ids.Contains(s.CharacterId))
            .ToDictionaryAsync(s => s.CharacterId, ct);

        var clones = await db.EsiCloneStates.AsNoTracking()
            .Where(c => ids.Contains(c.CharacterId))
            .ToDictionaryAsync(c => c.CharacterId, ct);

        var wallets = await db.EsiWalletBalances.AsNoTracking()
            .Where(w => w.OwnerType != "corporation" && ids.Contains(w.OwnerId))
            .ToDictionaryAsync(w => w.OwnerId, w => w.Balance, ct);

        // The most recent snapshot per character. Net worth is written daily, so the latest row
        // is the answer and older ones are history the summary has no use for.
        var assets = (await db.NetWorthSnapshots.AsNoTracking()
                .Where(n => n.OwnerType == "character" && ids.Contains(n.OwnerId))
                .Select(n => new { n.OwnerId, n.Date, n.AssetValue })
                .ToListAsync(ct))
            .GroupBy(n => n.OwnerId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Date).First().AssetValue);

        var queue = (await db.EsiSkillQueue.AsNoTracking()
                .Where(q => ids.Contains(q.CharacterId))
                .Select(q => new { q.CharacterId, q.FinishDate })
                .ToListAsync(ct))
            .GroupBy(q => q.CharacterId)
            .ToDictionary(g => g.Key, g => (
                Length: g.Count(),
                // Max rather than the last position: a queue can hold an entry with no finish
                // date when nothing is training, and Max ignores those instead of returning null.
                Ends: g.Max(x => x.FinishDate)));

        var slots = await SlotsAsync(db, ids, ct);
        var pods  = await PodValuesAsync(db, ids, ct);

        // What the worklist is allowed to schedule on each character. A character absent from
        // this list is not off-limits by accident — it is opt-in, so absence means no.
        var indy = await db.WorklistIndyChars.AsNoTracking()
            .Where(c => ids.Contains(c.CharacterId))
            .ToDictionaryAsync(c => c.CharacterId, ct);

        // Every place any of them might be, resolved together.
        var places = await PlacesAsync(db, status.Values, clones.Values, ct);
        var ships  = await ShipNamesAsync(db, status.Values, ct);

        await ResolveNamesAsync(chars, corps, ct);

        return chars.Select(c =>
        {
            var s = status.GetValueOrDefault(c.Id);
            var q = queue.GetValueOrDefault(c.Id);
            var (mf, mt, rf, rt, sf, st) = slots.GetValueOrDefault(c.Id, (1, 1, 1, 1, 1, 1));

            return new CharacterSummaryRow(
                c.Id,
                c.Name,
                corps.GetValueOrDefault(c.CorporationId)
                    ?? _corpNames.GetValueOrDefault(c.CorporationId, $"Corp {c.CorporationId}"),
                c.AllianceId is { } a ? _allianceNames.GetValueOrDefault(a, $"Alliance {a}") : "",
                s?.Online ?? false,
                s?.OnlineCheckedAt,
                Where(s, places),
                s?.ShipTypeId is { } shipType
                    ? ships.GetValueOrDefault(shipType, $"Type {shipType}") is var hull
                      && !string.IsNullOrWhiteSpace(s.ShipName) && s.ShipName != hull
                        ? $"{hull} · {s.ShipName}"
                        : hull
                    : "",
                pods.GetValueOrDefault(c.Id),
                clones.GetValueOrDefault(c.Id)?.HomeLocationId is { } home
                    ? places.GetValueOrDefault(home, $"Location {home}")
                    : "",
                q.Length,
                q.Ends,
                c.TotalSp,
                wallets.GetValueOrDefault(c.Id),
                assets.GetValueOrDefault(c.Id),
                mf, mt, rf, rt, sf, st,
                indy.GetValueOrDefault(c.Id)?.Manufacturing ?? false,
                indy.GetValueOrDefault(c.Id)?.Reactions     ?? false,
                indy.GetValueOrDefault(c.Id)?.Science       ?? false);
        }).ToList();
    }

    /// <summary>
    /// Fills in alliance and corporation names that nothing local knows. One public call each,
    /// only for ids seen for the first time, and every failure swallowed — the grid is worth
    /// rendering with an id in one column, and not worth failing over a network hiccup.
    /// </summary>
    private async Task ResolveNamesAsync(
        List<Character> chars, Dictionary<int, string> known, CancellationToken ct)
    {
        foreach (var id in chars.Where(c => c.AllianceId is not null)
                                .Select(c => c.AllianceId!.Value).Distinct()
                                .Where(id => !_allianceNames.ContainsKey(id)))
        {
            try
            {
                var a = await esi.GetPublicAsync<EsiPublicAlliance>($"alliances/{id}/", ct);
                if (!string.IsNullOrWhiteSpace(a?.Name)) _allianceNames[id] = a.Name;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* the id shows instead */ }
        }

        foreach (var id in chars.Select(c => c.CorporationId).Distinct()
                                .Where(id => !known.ContainsKey(id) && !_corpNames.ContainsKey(id)))
        {
            try
            {
                var c = await esi.GetPublicAsync<EsiPublicCorporation>($"corporations/{id}/", ct);
                if (!string.IsNullOrWhiteSpace(c?.Name)) _corpNames[id] = c.Name;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* the id shows instead */ }
        }
    }

    /// <summary>Where a character is: the station or structure if docked, the system if not.</summary>
    private static string Where(CharacterStatus? s, Dictionary<long, string> places)
    {
        if (s is null) return "";
        if (s.StationId   is { } st) return places.GetValueOrDefault(st, $"Station {st}");
        if (s.StructureId is { } sr) return places.GetValueOrDefault(sr, $"Structure {sr}");
        if (s.SolarSystemId is { } sys)
            return places.TryGetValue(sys, out var n) ? $"{n} (in space)" : $"System {sys}";
        return "";
    }

    /// <summary>
    /// Slots free and total per pool. The same arithmetic the worklist's assignment service uses —
    /// one base slot plus the two capacity skills — but for every character rather than only the
    /// ones configured to run industry, since this grid is about who could.
    /// </summary>
    private static async Task<Dictionary<long, (int, int, int, int, int, int)>> SlotsAsync(
        AppDbContext db, List<long> ids, CancellationToken ct)
    {
        var wanted = new[] { MassProduction, AdvancedMassProduction, MassReactions,
                             AdvancedMassReactions, LaboratoryOperation, AdvancedLabOperation };

        var skills = (await db.EsiSkills.AsNoTracking()
                .Where(s => ids.Contains(s.CharacterId) && wanted.Contains(s.SkillId))
                .Select(s => new { s.CharacterId, s.SkillId, s.ActiveSkillLevel })
                .ToListAsync(ct))
            .GroupBy(s => s.CharacterId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(s => s.SkillId, s => s.ActiveSkillLevel));

        // "delivered" is finished and collected; everything else still holds its slot, including
        // "ready" — the output is waiting to be picked up and the slot is not free until it is.
        var running = (await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => j.Status == "active" || j.Status == "paused" || j.Status == "ready")
                .Select(j => new { j.InstallerId, j.ActivityId })
                .ToListAsync(ct))
            .GroupBy(j => (j.InstallerId, Pool: PoolOf(j.ActivityId)))
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new Dictionary<long, (int, int, int, int, int, int)>(ids.Count);
        foreach (var id in ids)
        {
            var mine = skills.GetValueOrDefault(id) ?? [];

            var mfg = 1 + mine.GetValueOrDefault(MassProduction) + mine.GetValueOrDefault(AdvancedMassProduction);
            var rxn = 1 + mine.GetValueOrDefault(MassReactions)  + mine.GetValueOrDefault(AdvancedMassReactions);
            var sci = 1 + mine.GetValueOrDefault(LaboratoryOperation) + mine.GetValueOrDefault(AdvancedLabOperation);

            result[id] = (
                Math.Max(0, mfg - running.GetValueOrDefault((id, 0))), mfg,
                Math.Max(0, rxn - running.GetValueOrDefault((id, 1))), rxn,
                Math.Max(0, sci - running.GetValueOrDefault((id, 2))), sci);
        }
        return result;
    }

    /// <summary>0 manufacturing, 1 reactions, 2 science — matching the tuple above.</summary>
    private static int PoolOf(int activityId) => activityId switch
    {
        1 => 0,
        9 => 1,
        _ => 2,   // 3 TE, 4 ME, 5 copying, 8 invention all share the science slots
    };

    /// <summary>
    /// What each character's plugged-in implants are worth, at the same prices the asset
    /// valuation uses, so a pod and a hangar are not priced two different ways.
    /// </summary>
    private static async Task<Dictionary<long, double>> PodValuesAsync(
        AppDbContext db, List<long> ids, CancellationToken ct)
    {
        var implants = await db.EsiImplants.AsNoTracking()
            .Where(i => ids.Contains(i.CharacterId))
            .Select(i => new { i.CharacterId, i.TypeId })
            .ToListAsync(ct);
        if (implants.Count == 0) return [];

        var settings = await db.MarketDefaultSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings?.AssetValueConfigId is not int configId) return [];

        var types = implants.Select(i => i.TypeId).Distinct().ToList();
        var rows  = await db.MarketItemPrices.AsNoTracking()
            .Where(p => p.ConfigId == configId && types.Contains(p.TypeId))
            .ToListAsync(ct);

        // Whichever price the asset valuation is configured to use, so a pod and a hangar are
        // never priced two different ways.
        var prices = rows.ToDictionary(p => p.TypeId, p => settings.AssetValuePriceType switch
        {
            MarketPriceType.Buy  => p.BuyPrice,
            MarketPriceType.Sell => p.SellPrice,
            _                    => p.Midpoint,
        });

        return implants
            .GroupBy(i => i.CharacterId)
            .ToDictionary(g => g.Key, g => g.Sum(i => prices.GetValueOrDefault(i.TypeId)));
    }

    /// <summary>Station, structure and system names for everywhere the grid has to name.</summary>
    private static async Task<Dictionary<long, string>> PlacesAsync(
        AppDbContext db, IEnumerable<CharacterStatus> statuses,
        IEnumerable<CharacterCloneState> clones, CancellationToken ct)
    {
        var wanted = new HashSet<long>();
        foreach (var s in statuses)
        {
            if (s.StationId     is { } a) wanted.Add(a);
            if (s.StructureId   is { } b) wanted.Add(b);
            if (s.SolarSystemId is { } c) wanted.Add(c);
        }
        foreach (var c in clones)
            if (c.HomeLocationId is { } h) wanted.Add(h);

        var names = new Dictionary<long, string>();
        if (wanted.Count == 0) return names;

        var stationIds = wanted.Where(id => id is > 60_000_000 and < 70_000_000).Select(id => (int)id).ToList();
        foreach (var s in await db.SdeStations.AsNoTracking()
                     .Where(s => stationIds.Contains(s.StationId)).ToListAsync(ct))
            names[s.StationId] = s.Name;

        var systemIds = wanted.Where(id => id < 40_000_000).Select(id => (int)id).ToList();
        foreach (var s in await db.SdeSolarSystems.AsNoTracking()
                     .Where(s => systemIds.Contains(s.SolarSystemId)).ToListAsync(ct))
            names[s.SolarSystemId] = s.Name;

        foreach (var s in await db.EsiStructureNames.AsNoTracking()
                     .Where(s => wanted.Contains(s.StructureId)).ToListAsync(ct))
            names[s.StructureId] = s.Name;

        return names;
    }

    private static async Task<Dictionary<int, string>> ShipNamesAsync(
        AppDbContext db, IEnumerable<CharacterStatus> statuses, CancellationToken ct)
    {
        var types = statuses.Where(s => s.ShipTypeId is not null)
                            .Select(s => s.ShipTypeId!.Value).Distinct().ToList();
        if (types.Count == 0) return [];

        return await db.SdeTypes.AsNoTracking()
            .Where(t => types.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);
    }
}
