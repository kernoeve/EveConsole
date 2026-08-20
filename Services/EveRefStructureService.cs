using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Imports EVE Ref's published structure snapshot, and fills our own blanks from it.
///
/// <para><c>/universe/structures/{id}/</c> returns 403 without docking access, and for a private
/// structure that is permanent rather than a cache gap — no amount of re-polling changes it. Name,
/// system, type and owner all sit behind that one call. EVE Ref assembles what people whose
/// characters DO have access can see, which is the only route to any of it.</para>
///
/// <para>⚠️ Measured before building: of 861 ids we could not place, the snapshot resolves the
/// system for 76 — about one in eleven. 506 are present but unknown to EVE Ref too, and 279 it has
/// never seen. Names are the larger win, being published for 1,446 structures. Worth having, and
/// worth nobody expecting it to empty the unknown list.</para>
/// </summary>
public class EveRefStructureService(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory              httpFactory,
    AppErrorLogger                  errorLogger)
{
    /// <summary>Rewritten hourly. We read it daily — a structure's name and system change on the
    /// scale of months, and the snapshot is ~855 KB every time.</summary>
    public const string SnapshotUrl = "https://data.everef.net/structures/structures-latest.v2.json";

    /// <summary>Entries in the last snapshot, and how many of our rows it filled.</summary>
    public int LastImported { get; private set; }
    public int LastFilled   { get; private set; }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// One entry as published.
    ///
    /// <para>⚠️ Every field nullable but the id. The documented shape and the actual shape differ:
    /// name, solar_system_id, type_id, owner_id and position are all frequently absent, so a
    /// non-nullable int here would silently read a missing system as system 0.</para>
    /// </summary>
    private sealed class Dto
    {
        [JsonPropertyName("structure_id")]         public long?   StructureId   { get; set; }
        [JsonPropertyName("name")]                 public string? Name          { get; set; }
        [JsonPropertyName("owner_id")]             public long?   OwnerId       { get; set; }
        [JsonPropertyName("solar_system_id")]      public int?    SolarSystemId { get; set; }
        [JsonPropertyName("region_id")]            public int?    RegionId      { get; set; }
        [JsonPropertyName("type_id")]              public int?    TypeId        { get; set; }
        [JsonPropertyName("position")]             public Pos?    Position      { get; set; }
        [JsonPropertyName("is_public_structure")]  public bool?   IsPublic      { get; set; }
        [JsonPropertyName("is_market_structure")]  public bool?   IsMarket      { get; set; }
        [JsonPropertyName("first_seen")]           public string? FirstSeen     { get; set; }
    }

    private sealed class Pos
    {
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
        [JsonPropertyName("z")] public double Z { get; set; }
    }

    /// <summary>
    /// Fetches the snapshot, records it, and fills blanks in our own table from it.
    /// </summary>
    public async Task<(int Imported, int Filled)> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var client = httpFactory.CreateClient("everef");

            // Keyed by id at the top level, so the whole document is one object rather than a
            // list. At ~855 KB that is small enough to hold; streaming would buy nothing.
            var payload = await client.GetStringAsync(SnapshotUrl, ct);
            var entries = JsonSerializer.Deserialize<Dictionary<string, Dto>>(payload, Json);
            if (entries is null || entries.Count == 0) return (0, 0);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var now      = DateTimeOffset.UtcNow;
            var existing = await db.EveRefStructures.ToDictionaryAsync(s => s.StructureId, ct);
            var imported = 0;

            foreach (var (key, dto) in entries)
            {
                // The key is the id; the body repeats it, but not always.
                var id = dto.StructureId ?? (long.TryParse(key, out var k) ? k : 0);
                if (id <= 0) continue;

                if (!existing.TryGetValue(id, out var row))
                {
                    row = new EveRefStructure { StructureId = id };
                    db.EveRefStructures.Add(row);
                    existing[id] = row;
                }

                row.Name          = dto.Name ?? "";
                row.OwnerId       = dto.OwnerId ?? 0;
                row.SolarSystemId = dto.SolarSystemId ?? 0;
                row.RegionId      = dto.RegionId ?? 0;
                row.TypeId        = dto.TypeId ?? 0;
                row.X             = dto.Position?.X ?? 0;
                row.Y             = dto.Position?.Y ?? 0;
                row.Z             = dto.Position?.Z ?? 0;
                row.IsPublic      = dto.IsPublic ?? false;
                row.IsMarket      = dto.IsMarket ?? false;
                row.FirstSeen     = dto.FirstSeen ?? "";
                row.FetchedAt     = now;
                imported++;
            }

            await db.SaveChangesAsync(ct);

            var filled = await FillBlanksAsync(db, ct);

            LastImported = imported;
            LastFilled   = filled;
            return (imported, filled);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(EveRefStructureService), nameof(RefreshAsync), ex);
            return (0, 0);
        }
    }

    /// <summary>
    /// Copies EVE Ref's values into our table wherever ours are empty.
    ///
    /// <para>⚠️ Blanks only, and each field independently. EVE Ref must never overwrite a value
    /// ESI returned or a user typed: it is an unverifiable third-party observation, it can be
    /// months stale, and the structure may have been renamed, unanchored or destroyed since —
    /// possibly by us. Filling an empty field cannot destroy anything; replacing a full one can.
    /// </para>
    ///
    /// <para>⚠️ UpdatedBy is deliberately left alone, matching the market-order backfill. Filling
    /// a blank is not rewriting the row, and stamping it would tell someone their hand-written
    /// description had been overwritten when it had not. Where a value came from is answered by
    /// the EveRefStructures row still being there to compare against.</para>
    /// </summary>
    private static async Task<int> FillBlanksAsync(AppDbContext db, CancellationToken ct)
    {
        var theirs = await db.EveRefStructures.AsNoTracking()
            .ToDictionaryAsync(s => s.StructureId, ct);
        if (theirs.Count == 0) return 0;

        var ours = await db.Structures.ToListAsync(ct);
        var filled = 0;

        foreach (var row in ours)
        {
            if (!theirs.TryGetValue(row.StructureId, out var t)) continue;

            var before = (row.Name, row.SolarSystemId, row.TypeId, row.OwnerId);

            if (row.Name.Length == 0     && t.Name.Length > 0)     row.Name          = t.Name;
            if (row.SolarSystemId == 0   && t.SolarSystemId > 0)   row.SolarSystemId = t.SolarSystemId;
            if (row.TypeId == 0          && t.TypeId > 0)          row.TypeId        = t.TypeId;
            if (row.OwnerId == 0         && t.OwnerId > 0)         row.OwnerId       = t.OwnerId;

            // Position is all-or-nothing: a structure at 0,0,0 has no position rather than one at
            // the system's centre, and filling one axis of three would be meaningless.
            if (row.X == 0 && row.Y == 0 && row.Z == 0 && (t.X != 0 || t.Y != 0 || t.Z != 0))
            {
                row.X = t.X; row.Y = t.Y; row.Z = t.Z;
            }

            if (before != (row.Name, row.SolarSystemId, row.TypeId, row.OwnerId)) filled++;
        }

        if (filled > 0) await db.SaveChangesAsync(ct);
        return filled;
    }
}
