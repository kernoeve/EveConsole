using System.Text.Json;
using System.Text.RegularExpressions;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Battle;

/// <summary>A hull's names and class, for the report's rows.</summary>
public sealed record BattleShipType(int TypeId, string Name, int GroupId, string GroupName, BattleHullClass Class);

/// <summary>What is kept of a battle: where and when, and how the user arranged it.</summary>
public sealed class BattleDefinition
{
    public List<BattleWindow> Windows { get; set; } = [];
    public int TeamCount { get; set; } = BattleReportEngine.AutomaticTeams;
    public Dictionary<long, int> Moves { get; set; } = [];

    /// <summary>Whether to watch it live when opened, where any of it is still open.</summary>
    public bool WatchLive { get; set; } = true;
}

/// <summary>A saved battle, read back.</summary>
public sealed record SavedBattleRow(int Id, string Name, BattleDefinition Definition, DateTimeOffset UpdatedAt);

/// <summary>A constellation and its systems, named for the screen.</summary>
public sealed record BattleConstellation(string Name, string Region, IReadOnlyList<IndexSystem> Systems);

/// <summary>One system in a battle, over its own stretch of time. No end means it is still going.</summary>
public sealed record BattleWindow(int SystemId, DateTimeOffset From, DateTimeOffset? To)
{
    /// <summary>Can more of this still happen: no end, or an end yet to come. Only these are worth
    /// watching; the rest of a battle is already written.</summary>
    public bool IsOpenAt(DateTimeOffset now) => To is not { } end || end > now;
}

/// <summary>
/// Reads a battle's kills from this database, the same store every kill tool reads. What it holds
/// is whatever the zKillboard and retention settings have kept; this asks for nothing more.
/// </summary>
public sealed partial class BattleReportService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>The most kills one report takes. A week of a trade hub is not a battle, and holding
    /// every attacker of it in memory to find that out helps nobody.</summary>
    public const int MaxKills = 20_000;

    private const int ChunkSize = 5_000;

    private const int CategoryStarbase = 23, CategorySovereignty = 40, CategoryOrbitals = 46,
                      CategoryDeployable = 22, CategoryStructure = 65, CategoryInfrastructure = 39;
    private const int GroupCapsule = 29;

    /// <summary>The kills in these windows that are not in <paramref name="known"/>, and whether the
    /// windows hold more than <see cref="MaxKills"/>. Windows may overlap, even on one system: a kill
    /// inside two of them is still one kill.
    ///
    /// <para>⚠️ By id against what is held, never "newer than the last kill": a mail often lands
    /// well after the kill it records, and those are the ones a time check would lose.</para>
    ///
    /// <para>⚠️ Asked of the database every time rather than told by the importer: with PostgreSQL
    /// another client may be the one importing, and it cannot tell this process anything.</para></summary>
    public async Task<(List<BattleKill> Kills, bool TooMany)> LoadNewAsync(
        IReadOnlyCollection<BattleWindow> windows, IReadOnlySet<int> known, CancellationToken ct = default)
    {
        if (windows.Count == 0) return ([], false);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var args = new List<object>();
        string P(object value) { args.Add(value); return $"@p{args.Count - 1}"; }

        // The system ids are the SDE's numbers from the picker, not typed text, so they are inlined.
        // ⚠️ The times are passed as values: a timestamptz on a server, and a string shaped like a
        // date is still a string to it.
        var each = windows.Select(w =>
        {
            var upTo = w.To is { } end ? $""" AND d."KillMailTime" <= {P(end.ToUniversalTime())}""" : "";
            return $"""(d."SolarSystemId" = {w.SystemId} AND d."KillMailTime" >= {P(w.From.ToUniversalTime())}{upTo})""";
        }).ToList();

#pragma warning disable EF1002
        var ids = await db.Database.SqlQueryRaw<int>($"""
            SELECT d."KillMailId" AS "Value"
            FROM "KillMailDetails" d
            WHERE {string.Join(" OR ", each)}
            ORDER BY d."KillMailTime", d."KillMailId"
            LIMIT {MaxKills + 1}
            """, [.. args]).ToListAsync(ct).ConfigureAwait(false);
#pragma warning restore EF1002

        var tooMany = ids.Count > MaxKills;
        if (tooMany) ids.RemoveAt(ids.Count - 1);

        var fresh = ids.Where(id => !known.Contains(id)).ToList();
        if (fresh.Count == 0) return ([], tooMany);

        var kills = new List<BattleKill>(fresh.Count);
        foreach (var chunk in fresh.Chunk(ChunkSize))
            kills.AddRange(await LoadChunkAsync(db, chunk, ct).ConfigureAwait(false));
        return (kills, tooMany);
    }

    private static async Task<List<BattleKill>> LoadChunkAsync(AppDbContext db, int[] ids, CancellationToken ct)
    {
        var details = await db.KillMailDetails.AsNoTracking()
            .Where(d => ids.Contains(d.KillMailId))
            .Select(d => new
            {
                d.KillMailId, d.KillMailTime, d.SolarSystemId, d.VictimCharId, d.VictimCorpId,
                d.VictimAllianceId, d.VictimShipTypeId,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var attackers = (await db.KillMailAttackers.AsNoTracking()
                .Where(a => ids.Contains(a.KillMailId))
                .Select(a => new { a.KillMailId, a.CharacterId, a.CorporationId, a.AllianceId, a.DamageDone, a.FinalBlow, a.ShipTypeId })
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(a => a.KillMailId)
            .ToDictionary(g => g.Key, g => g
                .Select(a => new BattleAttacker(a.CharacterId ?? 0, a.CorporationId ?? 0, a.AllianceId ?? 0,
                    a.DamageDone, a.FinalBlow, a.ShipTypeId ?? 0))
                .ToList());

        // Our own market valuation, the same as every kill tool shows for these kills.
        var values = await KillmailValuation.ValueKillsAsync(
            db, details.ToDictionary(d => d.KillMailId, d => d.VictimShipTypeId), ct).ConfigureAwait(false);

        var classes = (await ShipTypesAsync(db, [.. details.Select(d => d.VictimShipTypeId).Distinct()], ct).ConfigureAwait(false));

        return [.. details.Select(d => new BattleKill(
            d.KillMailId, d.KillMailTime, d.SolarSystemId,
            d.VictimCharId, d.VictimCorpId, d.VictimAllianceId ?? 0,
            d.VictimShipTypeId,
            classes.TryGetValue(d.VictimShipTypeId, out var type) ? type.Class : BattleHullClass.Ship,
            values.GetValueOrDefault(d.KillMailId),
            attackers.TryGetValue(d.KillMailId, out var list) ? list : []))];
    }

    /// <summary>The most fights one search lists, the largest by ISK lost.</summary>
    public const int MaxFights = 100;

    private sealed class KillTimeRaw
    {
        public int            KillMailId    { get; set; }
        public DateTimeOffset KillMailTime  { get; set; }
        public int            SolarSystemId { get; set; }
    }

    /// <summary>The large fights in a period, the largest by ISK lost. See <see cref="BattleFightFinder"/>.
    ///
    /// <para>In two steps because a period can hold a million kills: every kill's place and time
    /// first, and the attackers and value only of those in a stretch worth looking at.</para></summary>
    public async Task<List<FoundFight>> FindFightsAsync(
        DateTimeOffset from, DateTimeOffset to, int minPilots, bool onlyOurs, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // ⚠️ The times are passed as values: a timestamptz on a server.
        var raw = await db.Database.SqlQueryRaw<KillTimeRaw>("""
            SELECT d."KillMailId", d."KillMailTime", d."SolarSystemId"
            FROM "KillMailDetails" d
            WHERE d."KillMailTime" >= @p0 AND d."KillMailTime" <= @p1
            """, from.ToUniversalTime(), to.ToUniversalTime()).ToListAsync(ct).ConfigureAwait(false);

        var runs = BattleFightFinder.Runs(raw.Select(k => new FightKillTime(k.KillMailId, k.KillMailTime, k.SolarSystemId)));
        var wanted = runs.SelectMany(r => r.KillIds).ToList();

        var participants = new Dictionary<int, IReadOnlyList<FightParticipant>>(wanted.Count);
        var hulls = new Dictionary<int, int>(wanted.Count);
        foreach (var chunk in wanted.Chunk(ChunkSize))
        {
            var victims = await db.KillMailDetails.AsNoTracking()
                .Where(d => chunk.Contains(d.KillMailId))
                .Select(d => new { d.KillMailId, d.VictimCharId, d.VictimCorpId, d.VictimAllianceId, d.VictimShipTypeId })
                .ToListAsync(ct).ConfigureAwait(false);
            var attackers = (await db.KillMailAttackers.AsNoTracking()
                    .Where(a => chunk.Contains(a.KillMailId))
                    .Select(a => new { a.KillMailId, a.CharacterId, a.CorporationId, a.AllianceId })
                    .ToListAsync(ct).ConfigureAwait(false))
                .ToLookup(a => a.KillMailId);

            foreach (var victim in victims)
            {
                hulls[victim.KillMailId] = victim.VictimShipTypeId;
                var list = new List<FightParticipant>();
                Add(victim.VictimCharId, victim.VictimCorpId, victim.VictimAllianceId ?? 0);
                foreach (var a in attackers[victim.KillMailId]) Add(a.CharacterId ?? 0, a.CorporationId ?? 0, a.AllianceId ?? 0);
                participants[victim.KillMailId] = list;

                void Add(long character, long corporation, long alliance)
                {
                    var (side, _) = BattleReportEngine.SideOf(character, corporation, alliance);
                    if (side != 0 && character > 0) list.Add(new FightParticipant(character, side));
                }
            }
        }

        var (ourCharacters, ourCorporations) = await OursAsync(ct).ConfigureAwait(false);
        var fights = BattleFightFinder.Fights(runs, participants, minPilots, now, ourCharacters.ToHashSet(), ourCorporations.ToHashSet());
        if (onlyOurs) fights = [.. fights.Where(f => f.IsOurs)];

        // Our own market valuation, the same as a report of the fight will show.
        var values = new Dictionary<int, double>();
        foreach (var chunk in fights.SelectMany(f => f.KillIds).Chunk(ChunkSize))
            foreach (var (id, value) in await KillmailValuation.ValueKillsAsync(db, chunk.ToDictionary(k => k, k => hulls[k]), ct).ConfigureAwait(false))
                values[id] = value;

        return [.. fights.Select(f => f with { IskLost = f.KillIds.Sum(id => values.GetValueOrDefault(id)) })
                         .OrderByDescending(f => f.IskLost).ThenByDescending(f => f.Pilots).Take(MaxFights)];
    }

    /// <summary>The saved battles, most recently changed first.</summary>
    public async Task<List<SavedBattleRow>> ListSavedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.SavedBattles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        // Sorted here: SQLite cannot order by a DateTimeOffset.
        return [.. rows.Select(r => new SavedBattleRow(r.Id, r.Name, ReadDefinition(r.Definition), r.UpdatedAt))
                       .OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.Id)];
    }

    private static BattleDefinition ReadDefinition(string json)
    {
        try { return JsonSerializer.Deserialize<BattleDefinition>(json) ?? new BattleDefinition(); }
        catch (JsonException) { return new BattleDefinition(); } // listed still, so it can be deleted
    }

    /// <summary>Keeps a battle: a new one with no id, otherwise that one. One deleted meanwhile from
    /// another machine is kept anew rather than lost. Returns its id.</summary>
    public async Task<int> SaveBattleAsync(int? id, string name, BattleDefinition definition, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = id is { } existing
            ? await db.SavedBattles.FirstOrDefaultAsync(b => b.Id == existing, ct).ConfigureAwait(false)
            : null;
        if (row is null) db.SavedBattles.Add(row = new SavedBattle());
        row.Name       = name;
        row.Definition = JsonSerializer.Serialize(definition);
        row.UpdatedAt  = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    /// <summary>Forgets a saved battle. Its kills stay where they are.</summary>
    public async Task DeleteBattleAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.SavedBattles.FirstOrDefaultAsync(b => b.Id == id, ct).ConfigureAwait(false);
        if (row is null) return;
        db.SavedBattles.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Constellations by name, each with its systems: picking one adds them all, since a
    /// sov fight can run across one or several.</summary>
    public async Task<List<BattleConstellation>> SearchConstellationsAsync(string text, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length < 2) return [];
        await SdeNames.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var shown = SdeNames.Find(SdeNameKind.Constellation, text).Select(id => (int)id).ToList();

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var found = await db.SdeConstellations.AsNoTracking()
            .Where(c => EF.Functions.Like(c.Name, $"{text}%") || shown.Contains(c.ConstellationId))
            .OrderBy(c => c.Name).Take(10)
            .Select(c => new { c.ConstellationId, c.Name, c.RegionId })
            .ToListAsync(ct).ConfigureAwait(false);
        if (found.Count == 0) return [];

        var ids = found.Select(c => c.ConstellationId).ToList();
        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => ids.Contains(s.ConstellationId))
            .Select(s => new { s.SolarSystemId, s.Name, s.ConstellationId })
            .ToListAsync(ct).ConfigureAwait(false);
        var regionIds = found.Select(c => c.RegionId).Distinct().ToList();
        var regions = await db.SdeRegions.AsNoTracking()
            .Where(r => regionIds.Contains(r.RegionId))
            .ToDictionaryAsync(r => r.RegionId, r => r.Name, ct).ConfigureAwait(false);

        return [.. found.Select(c =>
        {
            var region = SdeNames.Region(c.RegionId, regions.GetValueOrDefault(c.RegionId, ""));
            return new BattleConstellation(SdeNames.Constellation(c.ConstellationId, c.Name), region,
                [.. systems.Where(s => s.ConstellationId == c.ConstellationId)
                    .Select(s => new IndexSystem(s.SolarSystemId, SdeNames.SolarSystem(s.SolarSystemId, s.Name), region, 0))
                    .OrderBy(s => s.Name, StringComparer.CurrentCulture)]);
        })];
    }

    /// <summary>The names and class of these hulls.</summary>
    public async Task<Dictionary<int, BattleShipType>> ShipTypesAsync(IReadOnlyCollection<int> typeIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ShipTypesAsync(db, typeIds, ct).ConfigureAwait(false);
    }

    private static async Task<Dictionary<int, BattleShipType>> ShipTypesAsync(
        AppDbContext db, IReadOnlyCollection<int> typeIds, CancellationToken ct)
    {
        if (typeIds.Count == 0) return [];
        var ids = typeIds.ToList();
        var rows = await (from t in db.SdeTypes.AsNoTracking()
                          join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                          where ids.Contains(t.TypeId)
                          select new { t.TypeId, t.Name, t.GroupId, GroupName = g.Name, g.CategoryId })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.TypeId, r => new BattleShipType(r.TypeId, r.Name, r.GroupId, r.GroupName, ClassOf(r.GroupId, r.CategoryId)));
    }

    /// <summary>What a victim was. Anything anchored for good counts as a structure, so a citadel's
    /// millions of damage are not read as the fleet's damage to ships.
    ///
    /// <para>⚠️ A deployable is not a structure. A real fight drops dozens of mobile tractor units,
    /// and counted together they buried the one Tatara that was actually lost.</para></summary>
    public static BattleHullClass ClassOf(int groupId, int categoryId) =>
        groupId == GroupCapsule ? BattleHullClass.Pod
        : categoryId == CategoryDeployable ? BattleHullClass.Deployable
        : categoryId is CategoryStarbase or CategorySovereignty or CategoryOrbitals
                     or CategoryStructure or CategoryInfrastructure
            ? BattleHullClass.Structure
            : BattleHullClass.Ship;

    /// <summary>The user's own characters and corporations, which their side is told apart by. Every
    /// row counts, token or not: a lapsed token does not change whose pilot it is.</summary>
    public async Task<(List<long> Characters, List<long> Corporations)> OursAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var characters = await db.Characters.AsNoTracking().Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        var corporations = await db.Corporations.AsNoTracking().Select(c => (long)c.Id).ToListAsync(ct).ConfigureAwait(false);
        return (characters, corporations);
    }

    /// <summary>Where and when a kill happened, to start a report from it.</summary>
    public async Task<(int SystemId, DateTimeOffset Time)?> KillAnchorAsync(int killMailId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.KillMailDetails.AsNoTracking()
            .Where(d => d.KillMailId == killMailId)
            .Select(d => new { d.SolarSystemId, d.KillMailTime })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : (row.SolarSystemId, row.KillMailTime);
    }

    /// <summary>The kill id in a pasted kill link, or in a bare number; null if there is none.</summary>
    public static int? ParseKillId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = KillIdPattern().Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) && id > 0 ? id : null;
    }

    [GeneratedRegex(@"(?:^|/kill/|/killmails?/)\s*(\d{1,10})(?:[/\s]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex KillIdPattern();
}
