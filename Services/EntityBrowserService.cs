using EveConsole.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

// ── Row shapes ───────────────────────────────────────────────────────────────
// Property names must match the SELECT aliases: EF materialises SqlQueryRaw results
// by column name.

public record PilotRow(long EntityId, string Name, int Kills, int Losses, int IsOurs);
public record PlayerCorpRow(long EntityId, string Name, int Pilots, int Kills, int Losses, int IsOurs);
public record AllianceRow(long EntityId, string Name, int Corps, int Kills, int Losses);

public record AgentRow(long AgentId, string Name, int Level, string AgentType,
                       string Division, string Corporation, string Station, int IsLocator);
public record NpcCorpRow(long CorporationId, string Name, string Faction, int Stations,
                         int LpOffers, int LpHeld);
public record FactionRow(long FactionId, string Name, string Description, string MilitiaCorp,
                         string HomeSystem, int Corporations);

/// <summary>
/// Read-only lookups behind the Player Entities and NPC Entities tools.
///
/// Player entities come from UniverseNames — the cache the app fills whenever it resolves
/// an id it met in a killmail, a contract or a chat log. That is 57,000 characters and
/// 10,000 corporations, far too many to hand a grid, so every query is either a name search
/// or a "most active" list, and both are capped.
///
/// NPC entities come from the SDE and are small enough to page through freely.
///
/// Raw SQL rather than LINQ: each row carries counts from two or three other tables, and
/// expressing that through EF produces either a cartesian join or a query per row.
/// </summary>
public class EntityBrowserService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const int MaxRows = 300;

    private static string Like(string q) => $"%{q.Trim()}%";
    private static bool   HasQuery(string q) => q.Trim().Length >= 2;

    // ── Player entities ──────────────────────────────────────────────────────

    /// <summary>
    /// Pilots by name, or the most-seen pilots when nothing is typed. An empty search
    /// ordered alphabetically would open on whoever happens to sort first, which is no use
    /// to anyone; ordering by appearances at least opens on people who matter locally.
    /// </summary>
    public async Task<List<PilotRow>> PilotsAsync(string search, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // The page is narrowed to at most MaxRows *before* the per-row counts run, so the
        // subqueries execute a few hundred times rather than 57,000.
        var sql = HasQuery(search)
            ? """
              WITH page AS (
                  SELECT "EntityId", "Name" FROM "UniverseNames"
                  WHERE "Category" = 'character' AND "Name" LIKE @q
                  ORDER BY "Name" LIMIT @lim)
              SELECT p."EntityId", p."Name",
                     (SELECT COUNT(*) FROM "KillMailAttackers" a WHERE a."CharacterId" = p."EntityId") AS "Kills",
                     (SELECT COUNT(*) FROM "KillMailDetails"   d WHERE d."VictimCharId" = p."EntityId") AS "Losses",
                     (SELECT COUNT(*) FROM "Characters" c WHERE c."Id" = p."EntityId")                  AS "IsOurs"
              FROM page p ORDER BY p."Name"
              """
            : """
              WITH page AS (
                  SELECT "CharacterId" AS "EntityId", COUNT(*) AS "Kills"
                  FROM "KillMailAttackers" WHERE "CharacterId" IS NOT NULL
                  GROUP BY "CharacterId" ORDER BY COUNT(*) DESC LIMIT @lim)
              SELECT p."EntityId",
                     COALESCE(u."Name", 'Unknown ' || p."EntityId") AS "Name",
                     p."Kills",
                     (SELECT COUNT(*) FROM "KillMailDetails" d WHERE d."VictimCharId" = p."EntityId") AS "Losses",
                     (SELECT COUNT(*) FROM "Characters" c WHERE c."Id" = p."EntityId")                AS "IsOurs"
              FROM page p LEFT JOIN "UniverseNames" u ON u."EntityId" = p."EntityId"
              ORDER BY p."Kills" DESC
              """;

        return await db.Database.SqlQueryRaw<PilotRow>(sql,
            new SqliteParameter("@q", Like(search)),
            new SqliteParameter("@lim", MaxRows)).ToListAsync(ct);
    }

    public async Task<List<PlayerCorpRow>> PlayerCorpsAsync(string search, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sql = HasQuery(search)
            ? """
              WITH page AS (
                  SELECT "EntityId", "Name" FROM "UniverseNames"
                  WHERE "Category" = 'corporation' AND "Name" LIKE @q
                  ORDER BY "Name" LIMIT @lim)
              SELECT p."EntityId", p."Name",
                     (SELECT COUNT(DISTINCT a."CharacterId") FROM "KillMailAttackers" a WHERE a."CorporationId" = p."EntityId") AS "Pilots",
                     (SELECT COUNT(*) FROM "KillMailAttackers" a WHERE a."CorporationId" = p."EntityId") AS "Kills",
                     (SELECT COUNT(*) FROM "KillMailDetails"   d WHERE d."VictimCorpId"  = p."EntityId") AS "Losses",
                     (SELECT COUNT(*) FROM "Corporations" c WHERE c."Id" = p."EntityId")                 AS "IsOurs"
              FROM page p ORDER BY p."Name"
              """
            : """
              WITH page AS (
                  SELECT "CorporationId" AS "EntityId", COUNT(*) AS "Kills"
                  FROM "KillMailAttackers" WHERE "CorporationId" IS NOT NULL
                  GROUP BY "CorporationId" ORDER BY COUNT(*) DESC LIMIT @lim)
              SELECT p."EntityId",
                     COALESCE(u."Name", 'Unknown ' || p."EntityId") AS "Name",
                     (SELECT COUNT(DISTINCT a."CharacterId") FROM "KillMailAttackers" a WHERE a."CorporationId" = p."EntityId") AS "Pilots",
                     p."Kills",
                     (SELECT COUNT(*) FROM "KillMailDetails" d WHERE d."VictimCorpId" = p."EntityId") AS "Losses",
                     (SELECT COUNT(*) FROM "Corporations" c WHERE c."Id" = p."EntityId")              AS "IsOurs"
              FROM page p LEFT JOIN "UniverseNames" u ON u."EntityId" = p."EntityId"
              ORDER BY p."Kills" DESC
              """;

        return await db.Database.SqlQueryRaw<PlayerCorpRow>(sql,
            new SqliteParameter("@q", Like(search)),
            new SqliteParameter("@lim", MaxRows)).ToListAsync(ct);
    }

    public async Task<List<AllianceRow>> AlliancesAsync(string search, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sql = HasQuery(search)
            ? """
              WITH page AS (
                  SELECT "EntityId", "Name" FROM "UniverseNames"
                  WHERE "Category" = 'alliance' AND "Name" LIKE @q
                  ORDER BY "Name" LIMIT @lim)
              SELECT p."EntityId", p."Name",
                     (SELECT COUNT(DISTINCT a."CorporationId") FROM "KillMailAttackers" a WHERE a."AllianceId" = p."EntityId") AS "Corps",
                     (SELECT COUNT(*) FROM "KillMailAttackers" a WHERE a."AllianceId"      = p."EntityId") AS "Kills",
                     (SELECT COUNT(*) FROM "KillMailDetails"   d WHERE d."VictimAllianceId" = p."EntityId") AS "Losses"
              FROM page p ORDER BY p."Name"
              """
            : """
              WITH page AS (
                  SELECT "AllianceId" AS "EntityId", COUNT(*) AS "Kills"
                  FROM "KillMailAttackers" WHERE "AllianceId" IS NOT NULL
                  GROUP BY "AllianceId" ORDER BY COUNT(*) DESC LIMIT @lim)
              SELECT p."EntityId",
                     COALESCE(u."Name", 'Unknown ' || p."EntityId") AS "Name",
                     (SELECT COUNT(DISTINCT a."CorporationId") FROM "KillMailAttackers" a WHERE a."AllianceId" = p."EntityId") AS "Corps",
                     p."Kills",
                     (SELECT COUNT(*) FROM "KillMailDetails" d WHERE d."VictimAllianceId" = p."EntityId") AS "Losses"
              FROM page p LEFT JOIN "UniverseNames" u ON u."EntityId" = p."EntityId"
              ORDER BY p."Kills" DESC
              """;

        return await db.Database.SqlQueryRaw<AllianceRow>(sql,
            new SqliteParameter("@q", Like(search)),
            new SqliteParameter("@lim", MaxRows)).ToListAsync(ct);
    }

    // ── NPC entities ─────────────────────────────────────────────────────────

    /// <summary>Agents, matched on the agent's own name, its corporation or its station —
    /// "which agents are in this station" is as common a question as "where is this agent".</summary>
    public async Task<List<AgentRow>> AgentsAsync(string search, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var filter = HasQuery(search)
            ? """WHERE a."Name" LIKE @q OR COALESCE(n."Name",'') LIKE @q OR COALESCE(s."Name",'') LIKE @q"""
            : "";

        var sql = $"""
            SELECT a."AgentId", a."Name", a."Level",
                   COALESCE(ty."Name", '')            AS "AgentType",
                   COALESCE(d."Name",  '')            AS "Division",
                   COALESCE(n."Name",  '')            AS "Corporation",
                   COALESCE(s."Name",  '')            AS "Station",
                   a."IsLocator"
            FROM "SdeAgents" a
            LEFT JOIN "SdeAgentTypes"      ty ON ty."AgentTypeId"   = a."AgentTypeId"
            LEFT JOIN "SdeCorpDivisions"   d  ON d."DivisionId"     = a."DivisionId"
            LEFT JOIN "SdeNpcCorporations" n  ON n."CorporationId"  = a."CorporationId"
            LEFT JOIN "SdeStations"        s  ON s."StationId"      = a."LocationId"
            {filter}
            ORDER BY a."Level" DESC, a."Name"
            LIMIT @lim
            """;

        return await db.Database.SqlQueryRaw<AgentRow>(sql,
            new SqliteParameter("@q", Like(search)),
            new SqliteParameter("@lim", MaxRows)).ToListAsync(ct);
    }

    /// <summary>
    /// NPC corporations, with their station count and LP store size. Your own LP balance is
    /// the largest any one character holds — LP cannot be pooled across characters.
    /// </summary>
    public async Task<List<NpcCorpRow>> NpcCorpsAsync(string search, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var filter = HasQuery(search)
            ? """WHERE n."Name" LIKE @q OR COALESCE(f."Name",'') LIKE @q"""
            : "";

        var sql = $"""
            SELECT n."CorporationId", n."Name",
                   COALESCE(f."Name", '') AS "Faction",
                   (SELECT COUNT(*) FROM "SdeStations"     s WHERE s."CorporationId" = n."CorporationId") AS "Stations",
                   (SELECT COUNT(*) FROM "EsiLpStoreOffers" o WHERE o."CorporationId" = n."CorporationId") AS "LpOffers",
                   COALESCE((SELECT MAX(l."Points") FROM "EsiLoyaltyPoints" l
                             WHERE l."CorporationId" = n."CorporationId"), 0)                              AS "LpHeld"
            FROM "SdeNpcCorporations" n
            LEFT JOIN "SdeFactions" f ON f."FactionId" = n."FactionId"
            {filter}
            ORDER BY n."Name"
            LIMIT @lim
            """;

        return await db.Database.SqlQueryRaw<NpcCorpRow>(sql,
            new SqliteParameter("@q", Like(search)),
            new SqliteParameter("@lim", MaxRows)).ToListAsync(ct);
    }

    public async Task<List<FactionRow>> FactionsAsync(string search, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var filter = HasQuery(search) ? """WHERE f."Name" LIKE @q""" : "";

        var sql = $"""
            SELECT f."FactionId", f."Name", f."Description",
                   COALESCE(mc."Name", '') AS "MilitiaCorp",
                   COALESCE(ss."Name", '') AS "HomeSystem",
                   (SELECT COUNT(*) FROM "SdeNpcCorporations" n WHERE n."FactionId" = f."FactionId") AS "Corporations"
            FROM "SdeFactions" f
            LEFT JOIN "SdeNpcCorporations" mc ON mc."CorporationId"  = f."MilitiaCorporationId"
            LEFT JOIN "SdeSolarSystems"    ss ON ss."SolarSystemId"  = f."SolarSystemId"
            {filter}
            ORDER BY f."Name"
            LIMIT @lim
            """;

        return await db.Database.SqlQueryRaw<FactionRow>(sql,
            new SqliteParameter("@q", Like(search)),
            new SqliteParameter("@lim", MaxRows)).ToListAsync(ct);
    }
}
