using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EveConsole.Data;

namespace EveConsole.Agent.Tools.Data;

/// <summary>
/// Who is logged in right now, where, and in what — answered in sentences, from the status the
/// poller keeps.
/// </summary>
/// <remarks>
/// <para>⚠️ The one fixed-query tool brought back after the get_* tools were withdrawn, and for
/// the opposite reason. Those wrapped questions query_database answers better; this one is asked
/// constantly, needs four joins and three tables just to name a structure, and is exactly what a
/// small model does NOT manage to write. Without it a small model answered "is everything all
/// right?" from the alarm text in its own history — "still undocked" about a pilot who had docked
/// minutes before. So it is offered to the conversation model as well: the
/// capsuleer's whereabouts are what a companion is expected to know without handing off.</para>
/// <para>The answer is prose, not JSON, and short: a small model repeats prose faithfully and
/// loses its way in nested objects.</para>
/// </remarks>
public sealed class GetPilotStatusTool : IAgentTool
{
    private readonly Func<DbConnection> _connect;

    /// <param name="connect">The app's own database unless told otherwise — a harness passes a
    /// read-only connection.</param>
    public GetPilotStatusTool(Func<DbConnection>? connect = null) => _connect = connect ?? AppDb.Connect;

    public string Name        => "get_pilot_status";
    public string Description => "Which of the capsuleer's characters are logged in RIGHT NOW, where each one is " +
                                 "(system, region, docked station or structure, or in space) and what ship they are in. " +
                                 "Call it before saying anything about where a character is or what they fly — " +
                                 "earlier messages and alarms go out of date within minutes. " +
                                 "Offline characters are counted; pass include_offline or a character_name for their last known place.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            character_name  = new { type = "string",  description = "Optional: one character (partial name), online or not." },
            include_offline = new { type = "boolean", description = "Optional: also list offline characters with their last known location." },
        },
    };

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var name = input.TryGetProperty("character_name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()?.Trim() ?? "" : "";
        var withOffline = input.TryGetProperty("include_offline", out var o) && o.ValueKind == JsonValueKind.True;

        // The docked place from every table that names one — three for player structures, the
        // first to name it winning, in the order the header reads them.
        const string sql = """
            SELECT c."Name", cs."Online", cs."LastLogin", cs."LastLogout",
                   sys."Name", reg."Name", sys."Security",
                   cs."StationId", cs."StructureId", st."Name",
                   COALESCE(
                       (SELECT NULLIF(x."Name", '') FROM "Structures" x WHERE x."StructureId" = cs."StructureId" LIMIT 1),
                       (SELECT NULLIF(x."Name", '') FROM "EsiStructureNames" x WHERE x."StructureId" = cs."StructureId" LIMIT 1),
                       (SELECT NULLIF(x."Name", '') FROM "EsiCorpStructures" x WHERE x."StructureId" = cs."StructureId" LIMIT 1)),
                   hull."Name", cs."ShipName",
                   cs."LocationCheckedAt", cs."UndockedAt"
            FROM   "CharacterStatuses" cs
            JOIN   "Characters" c          ON c."Id" = cs."CharacterId"
            LEFT JOIN "SdeSolarSystems" sys ON sys."SolarSystemId" = cs."SolarSystemId"
            LEFT JOIN "SdeRegions" reg      ON reg."RegionId" = sys."RegionId"
            LEFT JOIN "SdeStations" st      ON st."StationId" = cs."StationId"
            LEFT JOIN "SdeTypes" hull       ON hull."TypeId" = cs."ShipTypeId"
            ORDER  BY c."Name"
            """;

        var pilots = new List<Pilot>();
        await using (var conn = _connect())
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.Command(sql);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                pilots.Add(new Pilot(
                    Name:       r.GetString(0),
                    Online:     Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture),
                    LastLogin:  Date(r, 2),
                    LastLogout: Date(r, 3),
                    System:     Str(r, 4),
                    Region:     Str(r, 5),
                    Security:   r.IsDBNull(6) ? null : Convert.ToDouble(r.GetValue(6), CultureInfo.InvariantCulture),
                    Docked:     !r.IsDBNull(7) || !r.IsDBNull(8),
                    Place:      Str(r, 9) ?? Str(r, 10),
                    Hull:       Str(r, 11),
                    ShipName:   Str(r, 12),
                    Checked:    Date(r, 13),
                    Undocked:   Date(r, 14)));
        }

        if (pilots.Count == 0)
            return "No character has a status yet — none is set up, or the first poll has not run.";

        var now = DateTimeOffset.UtcNow;
        var sb  = new StringBuilder();

        if (name.Length > 0)
        {
            // An exact name wins outright: one pilot's name is often the start of several others'.
            var matched = pilots.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matched.Count == 0)
                matched = pilots.Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matched.Count == 0)
                return $"No character matching '{name}'. Characters: {string.Join(", ", pilots.Select(p => p.Name))}.";
            foreach (var p in matched) sb.AppendLine(Describe(p, now));
            return sb.ToString().TrimEnd();
        }

        var online  = pilots.Where(p => p.Online).ToList();
        var offline = pilots.Where(p => !p.Online).ToList();

        sb.AppendLine($"Now {now:HH:mm} EVE time (UTC). {online.Count} of {pilots.Count} characters online.");
        foreach (var p in online) sb.AppendLine("- " + Describe(p, now));

        if (withOffline)
            foreach (var p in offline) sb.AppendLine("- " + Describe(p, now));
        else if (offline.Count > 0)
            sb.AppendLine($"{offline.Count} offline (not listed; pass include_offline for their last known places).");

        return sb.ToString().TrimEnd();
    }

    private sealed record Pilot(
        string Name, bool Online, DateTimeOffset? LastLogin, DateTimeOffset? LastLogout,
        string? System, string? Region, double? Security, bool Docked, string? Place,
        string? Hull, string? ShipName, DateTimeOffset? Checked, DateTimeOffset? Undocked);

    private static string Describe(Pilot p, DateTimeOffset now)
    {
        var system = p.System is null ? "an unknown system"
                   : $"{p.System} ({(p.Region is null ? "" : p.Region + ", ")}sec {Math.Round(p.Security ?? 0, 1).ToString("0.0", CultureInfo.InvariantCulture)})";

        var where = !p.Docked        ? $"in space in {system}"
                  : p.Place is not null ? $"docked at {p.Place}, {system}"
                  :                    $"docked at a structure in {system}";

        // The hull is what the ship IS; the name only when the pilot chose it. The game names a
        // new ship "<hull> - <pilot>", which says nothing the line does not already.
        var ship = p.Hull is null ? "an unknown ship" : p.Hull == "Capsule" ? "a capsule (no ship)" : p.Hull;
        if (!string.IsNullOrWhiteSpace(p.ShipName)
            && !string.Equals(p.ShipName, p.Hull, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(p.ShipName, $"{p.Hull} - {p.Name}", StringComparison.OrdinalIgnoreCase))
            ship += $" named \"{p.ShipName}\"";

        if (!p.Online)
        {
            var since = p.LastLogout is { } lo ? $", logged off {Ago(lo, now)}" : "";
            return $"{p.Name}: OFFLINE{since}. Last known: {where}, in {ship}.";
        }

        var text = $"{p.Name}: online, {where}, in {ship}.";

        // An undock is only worth saying when it belongs to this session: a pilot who logged in
        // already in space never undocked as far as this session goes.
        if (!p.Docked && p.Undocked is { } u && (p.LastLogin is null || u >= p.LastLogin))
            text += $" Undocked {Ago(u, now)}.";

        if (p.Checked is { } c && now - c > TimeSpan.FromMinutes(5))
            text += $" (Location last confirmed {Ago(c, now)}.)";

        return text;
    }

    private static string Ago(DateTimeOffset t, DateTimeOffset now)
    {
        var d = now - t;
        var span = d.TotalMinutes < 1  ? "just now"
                 : d.TotalMinutes < 90 ? $"{(int)d.TotalMinutes} min ago"
                 : d.TotalHours   < 48 ? $"{(int)d.TotalHours} h ago"
                 :                       $"{(int)d.TotalDays} days ago";
        return $"at {t.UtcDateTime:yyyy-MM-dd HH:mm} ({span})";
    }

    private static string? Str(DbDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetString(i) is { Length: > 0 } s ? s : null;

    /// <summary>A timestamp from either engine: PostgreSQL hands back a DateTime or
    /// DateTimeOffset, SQLite the text EF Core wrote.</summary>
    private static DateTimeOffset? Date(DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        return r.GetValue(i) switch
        {
            DateTimeOffset dto => dto.ToUniversalTime(),
            DateTime dt        => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                              DateTimeStyles.AssumeUniversal, out var p) => p.ToUniversalTime(),
            _ => null,
        };
    }
}
