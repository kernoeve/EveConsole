using EveConsole.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>The six things the entity tools can show.</summary>
public enum EntityKind { Pilot, PlayerCorp, Alliance, Agent, NpcCorp, Faction }

/// <summary>A dropdown candidate: enough to identify the entity and tell two similar names apart.</summary>
public record EntityMatch(long Id, string Name, string Subtitle)
{
    // AutoCompleteBox writes ToString() into the text box when an item is picked, so this
    // must be the bare name — anything else has to be stripped back off before lookup.
    public override string ToString() => Name;
}

/// <summary>One labelled fact on an About pane.</summary>
public record EntityFact(string Label, string Value);

/// <summary>Everything the About pane shows for one entity.</summary>
public record EntityDetail(
    long Id, string Name, string Subtitle, string Description,
    IReadOnlyList<EntityFact> Facts, string? ImageUrl);

public record EntityKillRow(long KillMailId, string When, string System, string Ship,
                            string Counterparty, string Role);

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
public class EntityBrowserService(IDbContextFactory<AppDbContext> dbFactory)
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
            _ => """SELECT COUNT(*) AS "Value" FROM "SdeFactions" WHERE "Name" LIKE @q""",
        };

        return (await db.Database.SqlQueryRaw<int>(sql,
            new SqliteParameter("@cat", CategoryOf(kind)),
            new SqliteParameter("@q",   $"%{q}%")).ToListAsync(ct)).FirstOrDefault();
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
        _                     => null,
    };

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
                        new("Security status", r.SecStatus == 0 ? "—" : r.SecStatus.ToString("0.0")),
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
                           COALESCE(f."Name",'')  AS "Faction"
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
                        new("Corporation",  r.Corporation),
                        new("Faction",      r.Faction),
                        new("Station",      r.Station),
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
                           COALESCE((SELECT v."IskPerLp" FROM "LpCorpValues" v WHERE v."CorporationId" = n."CorporationId"), 0) AS "IskPerLp"
                    FROM "SdeNpcCorporations" n
                    LEFT JOIN "SdeFactions" f ON f."FactionId" = n."FactionId"
                    WHERE n."CorporationId" = @id
                    """, new SqliteParameter("@id", id)).ToListAsync(ct)).FirstOrDefault();
                if (r is null) return null;

                return new EntityDetail(id, r.Name, $"NPC corporation{(r.Faction.Length > 0 ? " · " + r.Faction : "")}", "",
                    [
                        new("Corporation ID", id.ToString("N0")),
                        new("Faction",        r.Faction),
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
                           (SELECT COUNT(*) FROM "SdeNpcCorporations" n WHERE n."FactionId" = f."FactionId") AS "Corporations"
                    FROM "SdeFactions" f
                    LEFT JOIN "SdeNpcCorporations" mc ON mc."CorporationId" = f."MilitiaCorporationId"
                    LEFT JOIN "SdeSolarSystems"    ss ON ss."SolarSystemId" = f."SolarSystemId"
                    WHERE f."FactionId" = @id
                    """, new SqliteParameter("@id", id)).ToListAsync(ct)).FirstOrDefault();
                if (r is null) return null;

                return new EntityDetail(id, r.Name, "Faction", r.Description,
                    [
                        new("Faction ID",   id.ToString("N0")),
                        new("Militia corp", r.MilitiaCorp.Length > 0 ? r.MilitiaCorp : "—"),
                        new("Home system",  r.HomeSystem),
                        new("Corporations", r.Corporations.ToString("N0")),
                    ], url);
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
                                  string Division, string Corporation, string Station, string Faction);
    private record NpcCorpDetailRaw(string Name, string Faction, int Stations, int Agents,
                                    int LpOffers, int LpHeld, double IskPerLp);
    private record FactionDetailRaw(string Name, string Description, string MilitiaCorp,
                                    string HomeSystem, int Corporations);

    private static string Pretty(string iso) =>
        DateTimeOffset.TryParse(iso, out var d) ? d.ToLocalTime().ToString("d MMM yyyy HH:mm") : "—";
}
