using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>The seven things the entity tools can show.</summary>
public enum EntityKind { Pilot, PlayerCorp, Alliance, Agent, NpcCorp, Faction, Station }

/// <summary>A dropdown candidate: enough to identify the entity and tell two similar names apart.</summary>
public record EntityMatch(long Id, string Name, string Subtitle)
{
    // AutoCompleteBox writes ToString() into the text box when an item is picked, so this
    // must be the bare name — anything else has to be stripped back off before lookup.
    public override string ToString() => Name;
}

/// <summary>
/// One labelled fact in the header. A fact may link to another entity — a corporation,
/// an alliance, a pilot — or out to a web page, and the header renders it as a link when
/// it does.
/// </summary>
public record EntityFact(string Label, string Value,
                         EntityKind? LinkKind = null, long LinkId = 0, string? Url = null,
                         int SystemId = 0, int RegionId = 0)
{
    public bool IsEntityLink => LinkKind is not null && LinkId > 0;
    public bool IsUrlLink    => !string.IsNullOrWhiteSpace(Url);

    // A place is not an entity — it has no viewer of its own — so these route to the map
    // rather than through LinkKind.
    public bool IsSystemLink => SystemId > 0;
    public bool IsRegionLink => RegionId > 0;

    public bool IsPlain => !IsEntityLink && !IsUrlLink && !IsSystemLink && !IsRegionLink;
}

/// <summary>Everything the About pane shows for one entity.</summary>
public record EntityDetail(
    long Id, string Name, string Subtitle, string Description,
    IReadOnlyList<EntityFact> Facts, string? ImageUrl);

public record EntityKillRow(long KillMailId, string When, string System, string Ship,
                            string Counterparty, string Role);

public record EntityMemberRow(long Id, string Name, string Subtitle = "",
                              int Level = 0, string Division = "", string Station = "",
                              long StationId = 0);
public record EntityHistoryRow(string Alliance, string From, string Until, string Duration, bool Closed,
                               long LinkId = 0);
public record EntityStationRow(string Name, string System, string Region, double Security, int Agents,
                               long StationId = 0);

/// <summary>
/// One item an NPC corporation trades, across every one of its stations in the market data
/// pulled so far. Collapsed to the item rather than listed order by order: the same blueprint
/// is sold from dozens of stations at near-identical prices, so the per-order list was mostly
/// repetition. The price spread is what actually varies, and the Item Browser has the detail.
/// </summary>
public record NpcOrderItemRow(bool IsBuyOrder, int TypeId, string Item,
                              double LowPrice, double HighPrice)
{
    public string LowText  => LowPrice.ToString("N2");
    public string HighText => HighPrice.ToString("N2");
}
public record LpOfferRow(string Item, int TypeId, int Quantity, string LpCost, string IskCost, string Required);
public record FactionWarfareRow(string System, string Region, string Contested, int Points, int Threshold,
                                string Role, string Owner, string Occupier)
{
    /// <summary>Progress toward a flip. The raw pair means little without the ratio.</summary>
    public string ContestedPercent => Threshold > 0 ? $"{(double)Points / Threshold * 100:0.#}%" : "";
    public string PointsText       => Points.ToString("N0");
    public string ThresholdText    => Threshold.ToString("N0");

    /// <summary>A system held by someone other than its owner has been taken and not reset.</summary>
    public bool   IsOccupied    => Occupier.Length > 0 && Owner.Length > 0 && Occupier != Owner;
    public string OccupierColor => IsOccupied ? "#c85a5a" : "#7a8896";
}

public record IntelSightingRow(string When, string System, string Channel, string Ship, string Reporter);

/// <summary>
/// Backs the Player Entities and NPC Entities tools: a name search that feeds a dropdown,
/// then per-entity detail once something is picked.
///
/// Player entities come from UniverseNames — the cache the app fills whenever it resolves
/// an id met in a killmail, contract or chat log — which is 57,000 characters and 10,700
/// corporations. Nothing is ever listed wholesale; the search is the entry point.
///
/// Raw SQL rather than LINQ: the detail rows draw counts from several tables at once, which
/// EF turns into either a cartesian join or a query per row.
/// </summary>
public class EntityBrowserService(IDbContextFactory<AppDbContext> dbFactory, EsiClient? esi = null)
{
    /// <summary>Dropdown candidates. Deliberately small — this is a picker, not a report.</summary>
    public const int MaxMatches = 300;

    /// <summary>Rows on the Kills / Losses and Intel panes.</summary>
    public const int MaxDetailRows = 200;

    private const int MinSearch = 2;

    // ── Search ───────────────────────────────────────────────────────────────

    public async Task<List<EntityMatch>> SearchAsync(EntityKind kind, string text,
                                                     CancellationToken ct = default)
    {
        var q = (text ?? "").Trim();
        if (q.Length < MinSearch) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sql = kind switch
        {
            EntityKind.Pilot or EntityKind.PlayerCorp or EntityKind.Alliance => """
                SELECT "EntityId" AS "Id", "Name",
                       '' AS "Subtitle"
                FROM "UniverseNames"
                WHERE "Category" = @cat AND "Name" LIKE @q
                ORDER BY CASE WHEN "Name" LIKE @prefix THEN 0 ELSE 1 END, LENGTH("Name"), "Name"
                LIMIT @lim
                """,

            // Agents are searched by their own name, their corporation or their station:
            // "who is in this station" is as common a question as "where is this agent".
            EntityKind.Agent => """
                SELECT a."AgentId" AS "Id", a."Name",
                       'L' || a."Level" || ' · ' || COALESCE(d."Name",'') || ' · ' || COALESCE(s."Name",'') AS "Subtitle"
                FROM "SdeAgents" a
                LEFT JOIN "SdeCorpDivisions"   d ON d."DivisionId"    = a."DivisionId"
                LEFT JOIN "SdeNpcCorporations" n ON n."CorporationId" = a."CorporationId"
                LEFT JOIN "SdeStations"        s ON s."StationId"     = a."LocationId"
                WHERE a."Name" LIKE @q OR COALESCE(n."Name",'') LIKE @q OR COALESCE(s."Name",'') LIKE @q
                ORDER BY CASE WHEN a."Name" LIKE @prefix THEN 0 ELSE 1 END, a."Level" DESC, a."Name"
                LIMIT @lim
                """,

            EntityKind.NpcCorp => """
                SELECT n."CorporationId" AS "Id", n."Name",
                       COALESCE(f."Name",'') AS "Subtitle"
                FROM "SdeNpcCorporations" n
                LEFT JOIN "SdeFactions" f ON f."FactionId" = n."FactionId"
                WHERE n."Name" LIKE @q OR COALESCE(f."Name",'') LIKE @q
                ORDER BY CASE WHEN n."Name" LIKE @prefix THEN 0 ELSE 1 END, n."Name"
                LIMIT @lim
                """,

            // Station names embed their system ("Jita IV - Moon 4 - ..."), so a name search
            // already finds "everything in Jita". The region is matched too, for the times you
            // know the neighbourhood but not the name.
            EntityKind.Station => """
                SELECT s."StationId" AS "Id", s."Name",
                       COALESCE(n."Name",'') AS "Subtitle"
                FROM "SdeStations" s
                LEFT JOIN "SdeNpcCorporations" n ON n."CorporationId" = s."CorporationId"
                LEFT JOIN "SdeRegions"         r ON r."RegionId"      = s."RegionId"
                WHERE s."Name" LIKE @q OR COALESCE(r."Name",'') LIKE @q
                ORDER BY CASE WHEN s."Name" LIKE @prefix THEN 0 ELSE 1 END, s."Name"
                LIMIT @lim
                """,

            _ => """
                SELECT "FactionId" AS "Id", "Name", '' AS "Subtitle"
                FROM "SdeFactions"
                WHERE "Name" LIKE @q
                ORDER BY CASE WHEN "Name" LIKE @prefix THEN 0 ELSE 1 END, "Name"
                LIMIT @lim
                """,
        };

        return await db.Database.SqlQueryRaw<EntityMatch>(sql,
            new SqliteParameter("@cat",    CategoryOf(kind)),
            new SqliteParameter("@q",      $"%{q}%"),
            new SqliteParameter("@prefix", $"{q}%"),
            new SqliteParameter("@lim",    MaxMatches)).ToListAsync(ct);
    }

    /// <summary>
    /// How many entities the search actually matched, so the UI can say when the dropdown
    /// was truncated. A picker that silently stops at 300 hides the one you wanted.
    /// </summary>
    public async Task<int> CountMatchesAsync(EntityKind kind, string text, CancellationToken ct = default)
    {
        var q = (text ?? "").Trim();
        if (q.Length < MinSearch) return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sql = kind switch
        {
            EntityKind.Pilot or EntityKind.PlayerCorp or EntityKind.Alliance =>
                """SELECT COUNT(*) AS "Value" FROM "UniverseNames" WHERE "Category" = @cat AND "Name" LIKE @q""",
            EntityKind.Agent => """
                SELECT COUNT(*) AS "Value" FROM "SdeAgents" a
                LEFT JOIN "SdeNpcCorporations" n ON n."CorporationId" = a."CorporationId"
                LEFT JOIN "SdeStations"        s ON s."StationId"     = a."LocationId"
                WHERE a."Name" LIKE @q OR COALESCE(n."Name",'') LIKE @q OR COALESCE(s."Name",'') LIKE @q
                """,
            EntityKind.NpcCorp => """
                SELECT COUNT(*) AS "Value" FROM "SdeNpcCorporations" n
                LEFT JOIN "SdeFactions" f ON f."FactionId" = n."FactionId"
                WHERE n."Name" LIKE @q OR COALESCE(f."Name",'') LIKE @q
                """,
            EntityKind.Station => """
                SELECT COUNT(*) AS "Value" FROM "SdeStations" s
                LEFT JOIN "SdeRegions" r ON r."RegionId" = s."RegionId"
                WHERE s."Name" LIKE @q OR COALESCE(r."Name",'') LIKE @q
                """,
            _ => """SELECT COUNT(*) AS "Value" FROM "SdeFactions" WHERE "Name" LIKE @q""",
        };

        return (await db.Database.SqlQueryRaw<int>(sql,
            new SqliteParameter("@cat", CategoryOf(kind)),
            new SqliteParameter("@q",   $"%{q}%")).ToListAsync(ct)).FirstOrDefault();
    }

    /// <summary>
    /// Falls back to ESI when the local cache does not know the name. UniverseNames only
    /// holds entities the app has already met — a pilot who has never appeared in one of
    /// your killmails, contracts or chat logs simply is not there — so a name search that
    /// only reads it can never find anyone new.
    ///
    /// ESI's search is authenticated and needs a character token, so this is best-effort:
    /// with no characters signed in, or the scope withheld, the local result stands.
    /// Anything found is written back to the cache, so the next search finds it locally.
    /// </summary>
    public async Task<List<EntityMatch>> SearchWithEsiAsync(EntityKind kind, string text,
                                                            CancellationToken ct = default)
    {
        var local = await SearchAsync(kind, text, ct);

        var q = (text ?? "").Trim();
        if (esi is null || q.Length < MinSearch) return local;
        if (kind is not (EntityKind.Pilot or EntityKind.PlayerCorp or EntityKind.Alliance)) return local;
        // A full local page is already more than the dropdown shows; going to ESI as well
        // would add latency to a search that is not short of answers.
        if (local.Count >= MaxMatches) return local;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var searchAs = await db.Characters.AsNoTracking()
                .Select(c => (long)c.Id).FirstOrDefaultAsync(ct);
            if (searchAs == 0) return local;

            var ids = kind switch
            {
                EntityKind.Pilot      => await esi.SearchCharacterIdsAsync(searchAs, q, ct),
                EntityKind.PlayerCorp => await esi.SearchCorporationIdsAsync(searchAs, q, ct),
                _                     => await esi.SearchAllianceIdsAsync(searchAs, q, ct),
            };

            var known = local.Select(m => m.Id).ToHashSet();
            var fresh = ids.Select(i => (long)i).Where(i => !known.Contains(i)).Take(MaxMatches).ToList();
            if (fresh.Count == 0) return local;

            var names = await esi.GetNamesAsync(fresh, ct);
            if (names.Count == 0) return local;

            var category = CategoryOf(kind);
            var now      = DateTimeOffset.UtcNow.ToString("O");
            foreach (var n in names)
            {
                // INSERT OR IGNORE: another lookup may have cached the same id already,
                // and the name is not worth overwriting a fresher one for.
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT OR IGNORE INTO "UniverseNames" ("EntityId", "Name", "Category", "PulledAt")
                    VALUES (@id, @name, @cat, @at)
                    """,
                    [new SqliteParameter("@id", n.Id), new SqliteParameter("@name", n.Name),
                     new SqliteParameter("@cat", category), new SqliteParameter("@at", now)], ct);
            }

            return local
                .Concat(names.Select(n => new EntityMatch(n.Id, n.Name, "via ESI")))
                .OrderBy(m => m.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(m => m.Name.Length)
                .ThenBy(m => m.Name)
                .Take(MaxMatches)
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch { return local; /* ESI is an enhancement here, not the source of truth */ }
    }

    /// <summary>
    /// Public ESI detail for a player entity, fetched when it is opened. Adds the things
    /// no local table has — member counts, descriptions, founding dates — and, for a
    /// character, the authoritative security status.
    /// </summary>
    public async Task<(List<EntityFact> Facts, string Description, string? Name)> EnrichAsync(
        EntityKind kind, long id, CancellationToken ct = default)
    {
        var facts = new List<EntityFact>();
        string? name = null;
        if (esi is null) return (facts, "", null);

        try
        {
            switch (kind)
            {
                case EntityKind.Pilot:
                {
                    var c = await esi.GetPublicAsync<EsiPublicCharacter>($"characters/{id}/", ct);
                    if (c is null) break;

                    var corpName = await NameOfAsync(c.CorporationId, ct);
                    facts.Add(new("Corporation", corpName ?? c.CorporationId.ToString("N0"),
                                  EntityKind.PlayerCorp, c.CorporationId));
                    if (c.AllianceId is { } aid)
                        facts.Add(new("Alliance", await NameOfAsync(aid, ct) ?? aid.ToString("N0"),
                                      EntityKind.Alliance, aid));
                    if (c.SecurityStatus is { } sec)
                        facts.Add(new("Security status", sec.ToString("0.00")));
                    if (c.FactionId is { } fid)
                        facts.Add(new("Faction", await NameOfAsync(fid, ct) ?? fid.ToString("N0"),
                                      EntityKind.Faction, fid));
                    if (!string.IsNullOrWhiteSpace(c.Gender))
                        facts.Add(new("Gender", char.ToUpper(c.Gender![0]) + c.Gender[1..]));
                    if (c.RaceId is { } race)
                        facts.Add(new("Race", await RaceNameAsync(race, ct) ?? race.ToString()));
                    if (c.BloodlineId is { } bl)
                        facts.Add(new("Bloodline", await BloodlineNameAsync(bl, ct) ?? bl.ToString()));
                    if (c.AchievementScore is { } score)
                        facts.Add(new("Achievement score", score.ToString("N0")));
                    if (c.Birthday is { } b)
                        facts.Add(new("Born", b.ToLocalTime().ToString("d MMM yyyy")));
                    if (!string.IsNullOrWhiteSpace(c.Title))
                        facts.Add(new("Title", c.Title!));

                    await CacheNameAsync(c.Name, id, CategoryOf(kind), ct);
                    return (facts, StripHtml(c.Description), c.Name);
                }

                case EntityKind.PlayerCorp:
                {
                    var c = await esi.GetPublicAsync<EsiPublicCorporation>($"corporations/{id}/", ct);
                    if (c is null) break;

                    if (!string.IsNullOrWhiteSpace(c.Ticker)) facts.Add(new("Ticker", $"[{c.Ticker}]"));
                    facts.Add(new("Members", c.MemberCount.ToString("N0")));
                    facts.Add(new("CEO", await NameOfAsync(c.CeoId, ct) ?? c.CeoId.ToString("N0"),
                                  EntityKind.Pilot, c.CeoId));
                    if (c.AllianceId is { } aid)
                        facts.Add(new("Alliance", await NameOfAsync(aid, ct) ?? aid.ToString("N0"),
                                      EntityKind.Alliance, aid));
                    if (c.DateFounded is { } d)
                        facts.Add(new("Founded", d.ToLocalTime().ToString("d MMM yyyy")));
                    if (c.TaxRate is { } t) facts.Add(new("Tax rate", $"{t * 100:0.#}%"));
                    if (c.WarEligible is true) facts.Add(new("War eligible", "Yes"));
                    if (!string.IsNullOrWhiteSpace(c.Url) && c.Url != "http://")
                        facts.Add(new("URL", c.Url!, Url: c.Url));

                    await CacheNameAsync(c.Name, id, CategoryOf(kind), ct);
                    return (facts, StripHtml(c.Description), c.Name);
                }

                case EntityKind.Alliance:
                {
                    var a = await esi.GetPublicAsync<EsiPublicAlliance>($"alliances/{id}/", ct);
                    if (a is null) break;

                    if (!string.IsNullOrWhiteSpace(a.Ticker)) facts.Add(new("Ticker", $"[{a.Ticker}]"));
                    facts.Add(new("Creator", await NameOfAsync(a.CreatorId, ct) ?? a.CreatorId.ToString("N0"),
                                  EntityKind.Pilot, a.CreatorId));
                    facts.Add(new("Creator corp",
                                  await NameOfAsync(a.CreatorCorporationId, ct) ?? a.CreatorCorporationId.ToString("N0"),
                                  EntityKind.PlayerCorp, a.CreatorCorporationId));
                    if (a.ExecutorCorporationId is { } ex)
                        facts.Add(new("Executor corp", await NameOfAsync(ex, ct) ?? ex.ToString("N0"),
                                      EntityKind.PlayerCorp, ex));
                    if (a.DateFounded is { } d)
                        facts.Add(new("Founded", d.ToLocalTime().ToString("d MMM yyyy")));

                    await CacheNameAsync(a.Name, id, CategoryOf(kind), ct);
                    name = a.Name;
                    break;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* enrichment is additive — the local pane already stands on its own */ }

        return (facts, "", name);
    }

    /// <summary>
    /// Corporations currently in an alliance, from /alliances/{id}/corporations/. Ids only,
    /// so each is resolved to a name — and cached, since an alliance roster is a large batch
    /// of names the app will likely meet again.
    /// </summary>
    public async Task<List<EntityMemberRow>> AllianceCorpsAsync(long allianceId, CancellationToken ct = default)
    {
        if (esi is null) return [];
        try
        {
            var ids = await esi.GetPublicAsync<List<int>>($"alliances/{allianceId}/corporations/", ct);
            if (ids is null || ids.Count == 0) return [];

            var names = await ResolveNamesAsync(ids.Select(i => (long)i).ToList(), "corporation", ct);
            return ids
                .Select(i => new EntityMemberRow(i, names.GetValueOrDefault(i, $"Corporation {i}"), ""))
                .OrderBy(r => r.Name)
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    /// <summary>
    /// A corporation's alliance history, from /corporations/{id}/alliancehistory/. Newest
    /// first, with the periods the corporation was unaffiliated shown as such rather than
    /// dropped — leaving them out would imply a continuous run of memberships.
    /// </summary>
    public async Task<List<EntityHistoryRow>> CorpAllianceHistoryAsync(long corpId, CancellationToken ct = default)
    {
        if (esi is null) return [];
        try
        {
            var rows = await esi.GetPublicAsync<List<EsiAllianceHistory>>(
                $"corporations/{corpId}/alliancehistory/", ct);
            if (rows is null || rows.Count == 0) return [];

            var ids = rows.Where(r => r.AllianceId is > 0).Select(r => (long)r.AllianceId!.Value)
                          .Distinct().ToList();
            var names = await ResolveNamesAsync(ids, "alliance", ct);

            var ordered = rows.OrderByDescending(r => r.StartDate).ToList();
            var result  = new List<EntityHistoryRow>(ordered.Count);

            for (int i = 0; i < ordered.Count; i++)
            {
                var r    = ordered[i];
                var name = r.AllianceId is > 0
                    ? names.GetValueOrDefault(r.AllianceId.Value, $"Alliance {r.AllianceId}")
                    : "— no alliance —";

                // The record has no end date; a membership ran until the next one began.
                var until = i == 0 ? "present" : ordered[i - 1].StartDate.ToLocalTime().ToString("d MMM yyyy");
                var from  = r.StartDate.ToLocalTime().ToString("d MMM yyyy");
                var days  = ((i == 0 ? DateTimeOffset.UtcNow : ordered[i - 1].StartDate) - r.StartDate).Days;

                result.Add(new EntityHistoryRow(name, from, until, days < 0 ? "" : $"{days:N0} day(s)",
                                                r.IsDeleted == true, r.AllianceId ?? 0));
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    /// <summary>Resolves ids to names, caching anything new so later lookups stay local.</summary>
    private async Task<Dictionary<long, string>> ResolveNamesAsync(
        List<long> ids, string category, CancellationToken ct)
    {
        var map = new Dictionary<long, string>();
        if (ids.Count == 0) return map;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var idList = string.Join(",", ids);
        foreach (var row in await db.Database.SqlQueryRaw<CachedName>(
                     $"""SELECT "EntityId" AS "Id", "Name" FROM "UniverseNames" WHERE "EntityId" IN ({idList})""")
                     .ToListAsync(ct))
            map[row.Id] = row.Name;

        var missing = ids.Where(i => !map.ContainsKey(i)).ToList();
        if (missing.Count == 0 || esi is null) return map;

        // /universe/names/ takes up to 1000 per call.
        foreach (var chunk in missing.Chunk(1000))
        {
            var names = await esi.GetNamesAsync(chunk.ToList(), ct);
            var now   = DateTimeOffset.UtcNow.ToString("O");
            foreach (var n in names)
            {
                map[n.Id] = n.Name;
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT OR IGNORE INTO "UniverseNames" ("EntityId", "Name", "Category", "PulledAt")
                    VALUES (@id, @name, @cat, @at)
                    """,
                    [new SqliteParameter("@id", n.Id), new SqliteParameter("@name", n.Name),
                     new SqliteParameter("@cat", category), new SqliteParameter("@at", now)], ct);
            }
        }
        return map;
    }

    private record CachedName(long Id, string Name);

    /// <summary>
    /// A character's corporation history, newest first. ESI gives only start dates, so each
    /// end is the next record's start.
    /// </summary>
    public async Task<List<EntityHistoryRow>> CharacterCorpHistoryAsync(long charId, CancellationToken ct = default)
    {
        if (esi is null) return [];
        try
        {
            var rows = await esi.GetPublicAsync<List<EsiCorpHistory>>(
                $"characters/{charId}/corporationhistory/", ct);
            if (rows is null || rows.Count == 0) return [];

            var names = await ResolveNamesAsync(
                rows.Select(r => (long)r.CorporationId).Distinct().ToList(), "corporation", ct);

            var ordered = rows.OrderByDescending(r => r.StartDate).ToList();
            return ordered.Select((r, i) => new EntityHistoryRow(
                names.GetValueOrDefault(r.CorporationId, $"Corporation {r.CorporationId}"),
                r.StartDate.ToLocalTime().ToString("d MMM yyyy"),
                i == 0 ? "present" : ordered[i - 1].StartDate.ToLocalTime().ToString("d MMM yyyy"),
                $"{((i == 0 ? DateTimeOffset.UtcNow : ordered[i - 1].StartDate) - r.StartDate).Days:N0} day(s)",
                r.IsDeleted == true,
                r.CorporationId)).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    /// <summary>Every agent working for an NPC corporation.</summary>
    public async Task<List<EntityMemberRow>> NpcCorpAgentsAsync(long corpId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Database.SqlQueryRaw<EntityMemberRow>("""
            SELECT a."AgentId" AS "Id", a."Name", '' AS "Subtitle",
                   a."Level", COALESCE(d."Name",'') AS "Division",
                   COALESCE(s."Name",'') AS "Station", COALESCE(s."StationId", 0) AS "StationId"
            FROM "SdeAgents" a
            LEFT JOIN "SdeCorpDivisions" d ON d."DivisionId" = a."DivisionId"
            LEFT JOIN "SdeStations"      s ON s."StationId"  = a."LocationId"
            WHERE a."CorporationId" = @id
            ORDER BY a."Level" DESC, a."Name"
            """, new SqliteParameter("@id", corpId)).ToListAsync(ct);
    }

    /// <summary>Stations an NPC corporation owns.</summary>
    public async Task<List<EntityStationRow>> NpcCorpStationsAsync(long corpId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Database.SqlQueryRaw<EntityStationRow>("""
            SELECT s."Name", COALESCE(ss."Name",'') AS "System", COALESCE(r."Name",'') AS "Region",
                   ROUND(s."Security", 1) AS "Security",
                   (SELECT COUNT(*) FROM "SdeAgents" a WHERE a."LocationId" = s."StationId") AS "Agents",
                   s."StationId"
            FROM "SdeStations" s
            LEFT JOIN "SdeSolarSystems" ss ON ss."SolarSystemId" = s."SolarSystemId"
            LEFT JOIN "SdeRegions"      r  ON r."RegionId"       = s."RegionId"
            WHERE s."CorporationId" = @id
            ORDER BY r."Name", ss."Name", s."Name"
            """, new SqliteParameter("@id", corpId)).ToListAsync(ct);
    }

    /// <summary>
    /// The agents working out of one station. A station tab with no sub-tab at all would be an
    /// empty pane, and "who can I talk to here" is the question a station actually answers.
    /// </summary>
    public async Task<List<EntityMemberRow>> StationAgentsAsync(long stationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Database.SqlQueryRaw<EntityMemberRow>("""
            SELECT a."AgentId" AS "Id", a."Name",
                   COALESCE(n."Name",'')  AS "Subtitle",
                   a."Level",
                   COALESCE(d."Name",'')  AS "Division",
                   ''                     AS "Station",
                   0                      AS "StationId"
            FROM "SdeAgents" a
            LEFT JOIN "SdeCorpDivisions"   d ON d."DivisionId"    = a."DivisionId"
            LEFT JOIN "SdeNpcCorporations" n ON n."CorporationId" = a."CorporationId"
            WHERE a."LocationId" = @id
            ORDER BY a."Level" DESC, a."Name"
            """, new SqliteParameter("@id", stationId)).ToListAsync(ct);
    }

    /// <summary>
    /// The market orders an NPC corporation is running out of its own stations — how the game
    /// sells BPOs and skill books, and buys tags and other rat loot.
    ///
    /// Raw orders carry no issuer, so ownership is inferred from two facts together: a player
    /// order cannot be listed for longer than 90 days, and an order sitting in an NPC station
    /// belongs to whoever owns that station. Neither alone is enough — a player order in an NPC
    /// station is ordinary, and the duration test on its own says nothing about who placed it.
    ///
    /// Only what has actually been pulled is shown. Seeing every NPC order would mean pulling
    /// market data for every NPC region, which is the user's call, so the caller reports the
    /// region coverage alongside the rows rather than implying the list is complete.
    /// </summary>
    public async Task<List<NpcOrderItemRow>> NpcCorpOrdersAsync(long corpId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Database.SqlQueryRaw<NpcOrderItemRow>("""
            SELECT o."IsBuyOrder", o."TypeId",
                   COALESCE(t."Name", 'Type ' || o."TypeId") AS "Item",
                   MIN(o."Price") AS "LowPrice",
                   MAX(o."Price") AS "HighPrice"
            FROM (SELECT DISTINCT "OrderId", "TypeId", "IsBuyOrder", "Price", "LocationId"
                  FROM "MarketRawOrders" WHERE "Duration" > 90) o
            JOIN "SdeStations" s ON s."StationId" = o."LocationId"
            LEFT JOIN "SdeTypes" t ON t."TypeId" = o."TypeId"
            WHERE s."CorporationId" = @id
            GROUP BY o."IsBuyOrder", o."TypeId"
            ORDER BY "Item"
            """, new SqliteParameter("@id", corpId)).ToListAsync(ct);
    }

    /// <summary>
    /// Which regions the order list above could have drawn on — the market configs actually
    /// pulled, versus the regions this corporation holds stations in. Without this the tab
    /// cannot tell "this corp runs no orders" apart from "you have not pulled that market".
    /// </summary>
    public async Task<(int Covered, int Total)> NpcCorpOrderCoverageAsync(
        long corpId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var r = (await db.Database.SqlQueryRaw<CoverageRaw>("""
            SELECT (SELECT COUNT(DISTINCT s."RegionId") FROM "SdeStations" s
                    WHERE s."CorporationId" = @id) AS "Total",
                   (SELECT COUNT(DISTINCT s."RegionId") FROM "SdeStations" s
                    WHERE s."CorporationId" = @id
                      AND EXISTS (SELECT 1 FROM "MarketRawOrders" o
                                  WHERE o."LocationId" = s."StationId")) AS "Covered"
            """, new SqliteParameter("@id", corpId)).ToListAsync(ct)).FirstOrDefault();
        return (r?.Covered ?? 0, r?.Total ?? 0);
    }

    private sealed class CoverageRaw
    {
        public int Covered { get; set; }
        public int Total   { get; set; }
    }

    /// <summary>
    /// Whether a corporation id is an NPC one. The Player Entities viewer can land on an NPC
    /// corp — every capsuleer starts in one and plenty stay — and it should show that corp's
    /// orders when it does.
    /// </summary>
    public async Task<bool> IsNpcCorpAsync(long corpId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var n = (await db.Database.SqlQueryRaw<int>(
            """SELECT COUNT(*) AS "Value" FROM "SdeNpcCorporations" WHERE "CorporationId" = @id""",
            new SqliteParameter("@id", corpId)).ToListAsync(ct)).FirstOrDefault();
        return n > 0;
    }

    /// <summary>
    /// An NPC corporation's LP store, priced the same way the LP Market Values tool prices
    /// it so the two never disagree.
    /// </summary>
    public async Task<List<LpOfferRow>> NpcCorpLpOffersAsync(long corpId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var offers = await db.Database.SqlQueryRaw<LpOfferRaw>("""
            SELECT o."OfferId", COALESCE(t."Name", 'Type ' || o."TypeId") AS "Item",
                   o."TypeId", o."Quantity", o."LpCost", o."IskCost"
            FROM "EsiLpStoreOffers" o
            LEFT JOIN "SdeTypes" t ON t."TypeId" = o."TypeId"
            WHERE o."CorporationId" = @id
            ORDER BY o."LpCost"
            """, new SqliteParameter("@id", corpId)).ToListAsync(ct);
        if (offers.Count == 0) return [];

        var required = (await db.Database.SqlQueryRaw<LpReqRaw>("""
            SELECT i."OfferId", i."Quantity", COALESCE(t."Name", 'Type ' || i."TypeId") AS "Item"
            FROM "EsiLpStoreOfferItems" i
            LEFT JOIN "SdeTypes" t ON t."TypeId" = i."TypeId"
            WHERE i."CorporationId" = @id
            """, new SqliteParameter("@id", corpId)).ToListAsync(ct))
            .GroupBy(r => r.OfferId)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => $"{x.Quantity:N0} × {x.Item}")));

        return offers.Select(o => new LpOfferRow(
            o.Item, o.TypeId, o.Quantity,
            o.LpCost.ToString("N0"),
            o.IskCost > 0 ? o.IskCost.ToString("N0") : "—",
            required.GetValueOrDefault(o.OfferId, "—"))).ToList();
    }

    /// <summary>Corporations belonging to a faction.</summary>
    public async Task<List<EntityMemberRow>> FactionCorpsAsync(long factionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Database.SqlQueryRaw<EntityMemberRow>("""
            SELECT n."CorporationId" AS "Id", n."Name",
                   (SELECT COUNT(*) FROM "SdeStations" s WHERE s."CorporationId" = n."CorporationId")
                       || ' station(s)' AS "Subtitle"
            FROM "SdeNpcCorporations" n
            WHERE n."FactionId" = @id
            ORDER BY n."Name"
            """, new SqliteParameter("@id", factionId)).ToListAsync(ct);
    }

    /// <summary>
    /// Faction warfare systems, from the same snapshot the map overlay uses. Held systems
    /// are those this faction occupies; the contested ones are the interesting rows, so
    /// they sort first.
    /// </summary>
    public async Task<List<FactionWarfareRow>> FactionWarfareAsync(long factionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // MapFactionWarfares is a time series — one row per system per hourly bucket — so an
        // unqualified query returns every snapshot ever taken and the same system appears once
        // per hour with a different point total. This wants current standings, so it reads the
        // newest bucket only.
        return await db.Database.SqlQueryRaw<FactionWarfareRow>("""
            SELECT COALESCE(ss."Name", 'System ' || fw."SystemId") AS "System",
                   COALESCE(r."Name",'')       AS "Region",
                   fw."ContestedState"          AS "Contested",
                   fw."VictoryPoints"           AS "Points",
                   fw."VictoryPointsThreshold"  AS "Threshold",
                   CASE WHEN fw."OwnerFactionId" = @id THEN 'Owner' ELSE 'Occupier' END AS "Role",
                   COALESCE(fo."Name",'')       AS "Owner",
                   COALESCE(fc."Name",'')       AS "Occupier"
            FROM "MapFactionWarfares" fw
            LEFT JOIN "SdeSolarSystems" ss ON ss."SolarSystemId"   = fw."SystemId"
            LEFT JOIN "SdeConstellations" c ON c."ConstellationId" = ss."ConstellationId"
            LEFT JOIN "SdeRegions" r        ON r."RegionId"        = c."RegionId"
            LEFT JOIN "SdeFactions" fo      ON fo."FactionId"      = fw."OwnerFactionId"
            LEFT JOIN "SdeFactions" fc      ON fc."FactionId"      = fw."OccupierFactionId"
            WHERE fw."Bucket" = (SELECT MAX("Bucket") FROM "MapFactionWarfares")
              AND (fw."OwnerFactionId" = @id OR fw."OccupierFactionId" = @id)
            ORDER BY CASE WHEN fw."ContestedState" IN ('contested','captured') THEN 0 ELSE 1 END,
                     r."Name", ss."Name"
            """, new SqliteParameter("@id", factionId)).ToListAsync(ct);
    }

    private record LpOfferRaw(int OfferId, string Item, int TypeId, int Quantity, int LpCost, long IskCost);
    private record LpReqRaw(int OfferId, int Quantity, string Item);

    /// <summary>Race name from the SDE.</summary>
    private async Task<string?> RaceNameAsync(int raceId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.Database.SqlQueryRaw<string>(
            """SELECT "Name" AS "Value" FROM "SdeRaces" WHERE "RaceId" = @id""",
            new SqliteParameter("@id", raceId)).ToListAsync(ct)).FirstOrDefault();
    }

    /// <summary>
    /// Bloodline name from ESI. The SDE import has no bloodline table, and the list is
    /// small and fixed, so it is fetched once and held for the session.
    /// </summary>
    private static Dictionary<int, string>? _bloodlines;

    private async Task<string?> BloodlineNameAsync(int id, CancellationToken ct)
    {
        if (esi is null) return null;
        try
        {
            if (_bloodlines is null)
            {
                var rows = await esi.GetPublicAsync<List<EsiBloodline>>("universe/bloodlines/", ct);
                _bloodlines = rows?.ToDictionary(b => b.BloodlineId, b => b.Name) ?? [];
            }
            return _bloodlines.GetValueOrDefault(id);
        }
        catch { return null; }
    }

    /// <summary>Name from the local cache, or from ESI if it is not there yet.</summary>
    private async Task<string?> NameOfAsync(long id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cached = (await db.Database.SqlQueryRaw<string>(
            """SELECT "Name" AS "Value" FROM "UniverseNames" WHERE "EntityId" = @id""",
            new SqliteParameter("@id", id)).ToListAsync(ct)).FirstOrDefault();
        if (!string.IsNullOrEmpty(cached)) return cached;

        if (esi is null) return null;
        try
        {
            var names = await esi.GetNamesAsync(new List<long> { id }, ct);
            return names.FirstOrDefault()?.Name;
        }
        catch { return null; }
    }

    /// <summary>
    /// EVE bios and corp descriptions are markup — font tags, colours, links. The viewer
    /// shows plain text, so the tags come out rather than being displayed literally.
    /// </summary>
    private static string StripHtml(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var text = System.Text.RegularExpressions.Regex.Replace(s, "<br\\s*/?>", "\n",
                       System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    private static string CategoryOf(EntityKind kind) => kind switch
    {
        EntityKind.Pilot      => "character",
        EntityKind.PlayerCorp => "corporation",
        EntityKind.Alliance   => "alliance",
        _                     => "",
    };

    /// <summary>
    /// Portrait or logo from CCP's image server. Factions and agents have no endpoint of
    /// their own — an agent is a character, and a faction has nothing — so those fall back
    /// to what does exist.
    /// </summary>
    public static string? ImageUrlFor(EntityKind kind, long id) => kind switch
    {
        EntityKind.Pilot      => $"https://images.evetech.net/characters/{id}/portrait?size=128",
        EntityKind.Agent      => $"https://images.evetech.net/characters/{id}/portrait?size=128",
        EntityKind.PlayerCorp => $"https://images.evetech.net/corporations/{id}/logo?size=128",
        EntityKind.NpcCorp    => $"https://images.evetech.net/corporations/{id}/logo?size=128",
        EntityKind.Alliance   => $"https://images.evetech.net/alliances/{id}/logo?size=128",
        EntityKind.Faction    => $"https://images.evetech.net/corporations/{id}/logo?size=128",
        _                     => null,
    };

    /// <summary>
    /// Back-fill a name we happened to learn. INSERT OR IGNORE, so an existing row wins —
    /// this fills gaps, it does not refresh.
    /// </summary>
    private async Task CacheNameAsync(string? name, long id, string category, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT OR IGNORE INTO "UniverseNames" ("EntityId", "Name", "Category", "PulledAt")
                VALUES (@id, @name, @cat, @at)
                """,
                [new SqliteParameter("@id", id), new SqliteParameter("@name", name),
                 new SqliteParameter("@cat", category),
                 new SqliteParameter("@at", DateTimeOffset.UtcNow.ToString("O"))], ct);
        }
        catch { /* a missing name row is cosmetic — never fail the viewer over it */ }
    }

    // ── Detail ───────────────────────────────────────────────────────────────

    public async Task<EntityDetail?> DetailAsync(EntityKind kind, long id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var url = ImageUrlFor(kind, id);

        switch (kind)
        {
            case EntityKind.Pilot:
            {
                var r = (await db.Database.SqlQueryRaw<PilotDetailRaw>("""
                    SELECT COALESCE(u."Name", 'Unknown ' || @id) AS "Name",
                           (SELECT COUNT(*) FROM "KillMailAttackers" a WHERE a."CharacterId" = @id)  AS "Kills",
                           (SELECT COUNT(*) FROM "KillMailDetails"   d WHERE d."VictimCharId" = @id) AS "Losses",
                           (SELECT COUNT(*) FROM "Characters" c WHERE c."Id" = @id)                  AS "IsOurs",
                           COALESCE((SELECT MAX(a."SecurityStatus") FROM "KillMailAttackers" a WHERE a."CharacterId" = @id), 0) AS "SecStatus",
                           COALESCE((SELECT MAX(k."KillMailTime") FROM "KillMailDetails" k
                                     LEFT JOIN "KillMailAttackers" a ON a."KillMailId" = k."KillMailId"
                                     WHERE a."CharacterId" = @id OR k."VictimCharId" = @id), '') AS "LastSeen"
                    FROM (SELECT 1) x
                    LEFT JOIN "UniverseNames" u ON u."EntityId" = @id
                    """, new SqliteParameter("@id", id)).ToListAsync(ct)).FirstOrDefault();
                if (r is null) return null;

                return new EntityDetail(id, r.Name,
                    r.IsOurs > 0 ? "One of your characters" : "Player character",
                    "",
                    [
                        new("Character ID",   id.ToString("N0")),
                        new("Killmails on",   $"{r.Kills:N0} kill(s), {r.Losses:N0} loss(es)"),
                        new("Last seen",      Pretty(r.LastSeen)),
                    ], url);
            }

            case EntityKind.PlayerCorp:
            case EntityKind.Alliance:
            {
                bool corp = kind == EntityKind.PlayerCorp;
                var sql = corp
                    ? """
                      SELECT COALESCE(u."Name", 'Unknown ' || @id) AS "Name",
                             (SELECT COUNT(DISTINCT a."CharacterId") FROM "KillMailAttackers" a WHERE a."CorporationId" = @id) AS "Members",
                             (SELECT COUNT(*) FROM "KillMailAttackers" a WHERE a."CorporationId" = @id) AS "Kills",
                             (SELECT COUNT(*) FROM "KillMailDetails"   d WHERE d."VictimCorpId"  = @id) AS "Losses",
                             (SELECT COUNT(*) FROM "Corporations" c WHERE c."Id" = @id)                 AS "IsOurs"
                      FROM (SELECT 1) x LEFT JOIN "UniverseNames" u ON u."EntityId" = @id
                      """
                    : """
                      SELECT COALESCE(u."Name", 'Unknown ' || @id) AS "Name",
                             (SELECT COUNT(DISTINCT a."CorporationId") FROM "KillMailAttackers" a WHERE a."AllianceId" = @id) AS "Members",
                             (SELECT COUNT(*) FROM "KillMailAttackers" a WHERE a."AllianceId"       = @id) AS "Kills",
                             (SELECT COUNT(*) FROM "KillMailDetails"   d WHERE d."VictimAllianceId" = @id) AS "Losses",
                             0 AS "IsOurs"
                      FROM (SELECT 1) x LEFT JOIN "UniverseNames" u ON u."EntityId" = @id
                      """;

                var r = (await db.Database.SqlQueryRaw<GroupDetailRaw>(sql,
                    new SqliteParameter("@id", id)).ToListAsync(ct)).FirstOrDefault();
                if (r is null) return null;

                return new EntityDetail(id, r.Name,
                    corp ? (r.IsOurs > 0 ? "One of your corporations" : "Player corporation")
                         : "Player alliance",
                    "",
                    [
                        new(corp ? "Corporation ID" : "Alliance ID", id.ToString("N0")),
                        new(corp ? "Pilots seen"    : "Corporations seen", $"{r.Members:N0}"),
                        new("Killmails on", $"{r.Kills:N0} kill(s), {r.Losses:N0} loss(es)"),
                    ], url);
            }

            case EntityKind.Agent:
            {
                var r = (await db.Database.SqlQueryRaw<AgentDetailRaw>("""
                    SELECT a."Name", a."Level", a."IsLocator",
                           COALESCE(ty."Name",'') AS "AgentType",
                           COALESCE(d."Name",'')  AS "Division",
                           COALESCE(n."Name",'')  AS "Corporation",
                           COALESCE(s."Name",'')  AS "Station",
                           COALESCE(f."Name",'')  AS "Faction",
                           a."CorporationId", COALESCE(f."FactionId", 0) AS "FactionId",
                           COALESCE(a."LocationId", 0) AS "StationId"
                    FROM "SdeAgents" a
                    LEFT JOIN "SdeAgentTypes"      ty ON ty."AgentTypeId"  = a."AgentTypeId"
                    LEFT JOIN "SdeCorpDivisions"   d  ON d."DivisionId"    = a."DivisionId"
                    LEFT JOIN "SdeNpcCorporations" n  ON n."CorporationId" = a."CorporationId"
                    LEFT JOIN "SdeStations"        s  ON s."StationId"     = a."LocationId"
                    LEFT JOIN "SdeFactions"        f  ON f."FactionId"     = n."FactionId"
                    WHERE a."AgentId" = @id
                    """, new SqliteParameter("@id", id)).ToListAsync(ct)).FirstOrDefault();
                if (r is null) return null;

                return new EntityDetail(id, r.Name, $"Level {r.Level} {r.Division} agent", "",
                    [
                        new("Agent ID",     id.ToString("N0")),
                        new("Corporation",  r.Corporation, EntityKind.NpcCorp, r.CorporationId),
                        new("Faction",      r.Faction,     EntityKind.Faction, r.FactionId),
                        new("Station",      r.Station, EntityKind.Station, r.StationId),
                        new("Division",     r.Division),
                        new("Agent type",   r.AgentType),
                        new("Locator",      r.IsLocator > 0 ? "Yes" : "No"),
                    ], url);
            }

            case EntityKind.NpcCorp:
            {
                var r = (await db.Database.SqlQueryRaw<NpcCorpDetailRaw>("""
                    SELECT n."Name", COALESCE(f."Name",'') AS "Faction",
                           (SELECT COUNT(*) FROM "SdeStations" s WHERE s."CorporationId" = n."CorporationId") AS "Stations",
                           (SELECT COUNT(*) FROM "SdeAgents"   a WHERE a."CorporationId" = n."CorporationId") AS "Agents",
                           (SELECT COUNT(*) FROM "EsiLpStoreOffers" o WHERE o."CorporationId" = n."CorporationId") AS "LpOffers",
                           COALESCE((SELECT MAX(l."Points") FROM "EsiLoyaltyPoints" l WHERE l."CorporationId" = n."CorporationId"), 0) AS "LpHeld",
                           COALESCE((SELECT v."IskPerLp" FROM "LpCorpValues" v WHERE v."CorporationId" = n."CorporationId"), 0) AS "IskPerLp",
                           COALESCE(n."FactionId", 0) AS "FactionId"
                    FROM "SdeNpcCorporations" n
                    LEFT JOIN "SdeFactions" f ON f."FactionId" = n."FactionId"
                    WHERE n."CorporationId" = @id
                    """, new SqliteParameter("@id", id)).ToListAsync(ct)).FirstOrDefault();
                if (r is null) return null;

                return new EntityDetail(id, r.Name, $"NPC corporation{(r.Faction.Length > 0 ? " · " + r.Faction : "")}", "",
                    [
                        new("Corporation ID", id.ToString("N0")),
                        new("Faction",        r.Faction, EntityKind.Faction, r.FactionId),
                        new("Stations",       r.Stations.ToString("N0")),
                        new("Agents",         r.Agents.ToString("N0")),
                        new("LP store",       r.LpOffers > 0 ? $"{r.LpOffers:N0} offer(s)" : "none"),
                        new("Your LP",        r.LpHeld > 0 ? $"{r.LpHeld:N0}" : "—"),
                        new("ISK per LP",     r.IskPerLp > 0 ? r.IskPerLp.ToString("N0") : "—"),
                    ], url);
            }

            default:
            {
                var r = (await db.Database.SqlQueryRaw<FactionDetailRaw>("""
                    SELECT f."Name", f."Description",
                           COALESCE(mc."Name",'') AS "MilitiaCorp",
                           COALESCE(ss."Name",'') AS "HomeSystem",
                           (SELECT COUNT(*) FROM "SdeNpcCorporations" n WHERE n."FactionId" = f."FactionId") AS "Corporations",
                           COALESCE(f."MilitiaCorporationId", 0) AS "MilitiaCorpId",
                           COALESCE(f."SolarSystemId", 0) AS "HomeSystemId"
                    FROM "SdeFactions" f
                    LEFT JOIN "SdeNpcCorporations" mc ON mc."CorporationId" = f."MilitiaCorporationId"
                    LEFT JOIN "SdeSolarSystems"    ss ON ss."SolarSystemId" = f."SolarSystemId"
                    WHERE f."FactionId" = @id
                    """, new SqliteParameter("@id", id)).ToListAsync(ct)).FirstOrDefault();
                if (r is null) return null;

                return new EntityDetail(id, r.Name, "Faction", r.Description,
                    [
                        new("Faction ID",   id.ToString("N0")),
                        new("Militia corp", r.MilitiaCorp.Length > 0 ? r.MilitiaCorp : "—",
                            r.MilitiaCorpId > 0 ? EntityKind.NpcCorp : null, r.MilitiaCorpId),
                        new("Home system",  r.HomeSystem, SystemId: r.HomeSystemId),
                        new("Corporations", r.Corporations.ToString("N0")),
                    ], url);
            }

            case EntityKind.Station:
            {
                var r = (await db.Database.SqlQueryRaw<StationDetailRaw>("""
                    SELECT s."Name",
                           COALESCE(ss."Name",'')  AS "System",
                           COALESCE(rg."Name",'')  AS "Region",
                           COALESCE(cn."Name",'')  AS "Constellation",
                           COALESCE(n."Name",'')   AS "Corporation",
                           COALESCE(f."Name",'')   AS "Faction",
                           COALESCE(ty."Name",'')  AS "StationType",
                           s."SolarSystemId", s."RegionId",
                           COALESCE(s."CorporationId", 0) AS "CorporationId",
                           COALESCE(f."FactionId", 0)     AS "FactionId",
                           COALESCE(s."StationTypeId", 0) AS "StationTypeId",
                           s."Security", s."ReprocessingEfficiency", s."ReprocessingTax",
                           (SELECT COUNT(*) FROM "SdeAgents" a WHERE a."LocationId" = s."StationId") AS "Agents"
                    FROM "SdeStations" s
                    LEFT JOIN "SdeSolarSystems"    ss ON ss."SolarSystemId"   = s."SolarSystemId"
                    LEFT JOIN "SdeConstellations"  cn ON cn."ConstellationId" = s."ConstellationId"
                    LEFT JOIN "SdeRegions"         rg ON rg."RegionId"        = s."RegionId"
                    LEFT JOIN "SdeNpcCorporations" n  ON n."CorporationId"    = s."CorporationId"
                    LEFT JOIN "SdeFactions"        f  ON f."FactionId"        = n."FactionId"
                    LEFT JOIN "SdeTypes"           ty ON ty."TypeId"          = s."StationTypeId"
                    WHERE s."StationId" = @id
                    """, new SqliteParameter("@id", id)).ToListAsync(ct)).FirstOrDefault();
                if (r is null) return null;

                // A station has no portrait, but its hull does — and the render is how you
                // recognise a station in the client.
                var stationImg = r.StationTypeId > 0
                    ? $"https://images.evetech.net/types/{r.StationTypeId}/render?size=128"
                    : null;

                return new EntityDetail(id, r.Name, "NPC station", "",
                    [
                        new("Station ID",    id.ToString("N0")),
                        new("System",        r.System,        SystemId: r.SolarSystemId),
                        new("Constellation", r.Constellation),
                        new("Region",        r.Region,        RegionId: r.RegionId),
                        new("Security",      SecurityColors.Text(r.Security)),
                        new("Corporation",   r.Corporation,   EntityKind.NpcCorp, r.CorporationId),
                        new("Faction",       r.Faction,       EntityKind.Faction, r.FactionId),
                        new("Type",          r.StationType),
                        new("Agents",        r.Agents.ToString("N0")),
                        new("Reprocessing",  $"{r.ReprocessingEfficiency * 100:0.#}% · {r.ReprocessingTax * 100:0.#}% tax"),
                    ], stationImg);
            }
        }
    }

    // ── Kills and losses ─────────────────────────────────────────────────────

    /// <summary>
    /// Killmails the entity took part in, most recent first, labelled by which side it was
    /// on. Only meaningful for player entities — NPC corporations and factions do appear on
    /// killmails, but as the aggressor of record rather than as anyone you can look up.
    /// </summary>
    public async Task<List<EntityKillRow>> KillsAsync(EntityKind kind, long id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var (attackerCol, victimCol) = kind switch
        {
            EntityKind.Pilot      => ("CharacterId",   "VictimCharId"),
            EntityKind.PlayerCorp => ("CorporationId", "VictimCorpId"),
            _                     => ("AllianceId",    "VictimAllianceId"),
        };

        var sql = $"""
            WITH involved AS (
                SELECT k."KillMailId", k."KillMailTime", k."SolarSystemId", k."VictimShipTypeId",
                       k."VictimCharId", 'Kill' AS "Role"
                FROM "KillMailDetails" k
                WHERE EXISTS (SELECT 1 FROM "KillMailAttackers" a
                              WHERE a."KillMailId" = k."KillMailId" AND a."{attackerCol}" = @id)
                UNION
                SELECT k."KillMailId", k."KillMailTime", k."SolarSystemId", k."VictimShipTypeId",
                       k."VictimCharId", 'Loss' AS "Role"
                FROM "KillMailDetails" k
                WHERE k."{victimCol}" = @id)
            SELECT i."KillMailId",
                   substr(i."KillMailTime", 1, 16)        AS "When",
                   COALESCE(ss."Name", '')                AS "System",
                   COALESCE(t."Name", '')                 AS "Ship",
                   COALESCE(u."Name", '')                 AS "Counterparty",
                   i."Role"
            FROM involved i
            LEFT JOIN "SdeSolarSystems" ss ON ss."SolarSystemId" = i."SolarSystemId"
            LEFT JOIN "SdeTypes"        t  ON t."TypeId"         = i."VictimShipTypeId"
            LEFT JOIN "UniverseNames"   u  ON u."EntityId"       = i."VictimCharId"
            ORDER BY i."KillMailTime" DESC
            LIMIT @lim
            """;

        return await db.Database.SqlQueryRaw<EntityKillRow>(sql,
            new SqliteParameter("@id",  id),
            new SqliteParameter("@lim", MaxDetailRows)).ToListAsync(ct);
    }

    // ── Intel ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Intel-channel sightings of a pilot. Populated by the chat log parser, so this stays
    /// empty until intel channels are configured and messages have been imported.
    /// </summary>
    public async Task<List<IntelSightingRow>> IntelAsync(long characterId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Database.SqlQueryRaw<IntelSightingRow>("""
            SELECT substr(r."ReportedAt", 1, 16) AS "When",
                   r."SystemName"                AS "System",
                   r."ChannelName"               AS "Channel",
                   COALESCE(c."ShipName", '')    AS "Ship",
                   r."ReporterName"              AS "Reporter"
            FROM "IntelReportCharacters" c
            JOIN "IntelReports" r ON r."Id" = c."IntelReportId"
            WHERE c."CharacterId" = @id
            ORDER BY r."ReportedAt" DESC
            LIMIT @lim
            """,
            new SqliteParameter("@id",  characterId),
            new SqliteParameter("@lim", MaxDetailRows)).ToListAsync(ct);
    }

    // Raw row shapes — property names match the SELECT aliases.
    private record PilotDetailRaw(string Name, int Kills, int Losses, int IsOurs, double SecStatus, string LastSeen);
    private record GroupDetailRaw(string Name, int Members, int Kills, int Losses, int IsOurs);
    private record AgentDetailRaw(string Name, int Level, int IsLocator, string AgentType,
                                  string Division, string Corporation, string Station, string Faction,
                                  long CorporationId, long FactionId, long StationId);
    private record NpcCorpDetailRaw(string Name, string Faction, int Stations, int Agents,
                                    int LpOffers, int LpHeld, double IskPerLp, long FactionId);
    private record FactionDetailRaw(string Name, string Description, string MilitiaCorp,
                                    string HomeSystem, int Corporations, long MilitiaCorpId,
                                    int HomeSystemId);

    private record StationDetailRaw(string Name, string System, string Region, string Constellation,
                                    string Corporation, string Faction, string StationType,
                                    int SolarSystemId, int RegionId, long CorporationId, long FactionId,
                                    int StationTypeId, double Security,
                                    double ReprocessingEfficiency, double ReprocessingTax, int Agents);

    private static string Pretty(string iso) =>
        DateTimeOffset.TryParse(iso, out var d) ? d.ToLocalTime().ToString("d MMM yyyy HH:mm") : "—";
}
