using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using EveConsole.Services;
using Microsoft.Data.Sqlite;
using EveConsole.Data;

namespace EveConsole.Alarms.Conditions;

/// <summary>
/// Fires when a pilot is reported in a watched system — either a named list of systems, or
/// everything within a jump range of one system.
///
/// <para>Matches are keyed on the intel report's own id, so a hostile who sits in a system and
/// gets re-reported every few minutes is one alert per report rather than a repeat of the same
/// one, and a genuinely new report is always news.</para>
/// </summary>
public sealed class IntelCondition : IAlarmCondition
{
    /// <summary>
    /// How far back a check looks. Bounded because stale intel is worthless — an alarm that has
    /// been off for a day should not wake up and recite yesterday's sightings.
    /// </summary>
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(2);

    private const int MaxReports = 100;

    /// <summary>
    /// Range ceiling. Beyond this the watched set stops being "around me" and becomes most of
    /// the region, and the system list goes into the query as an IN clause.
    /// </summary>
    private const int MaxJumps = 15;

    private readonly SystemGraph _graph;

    public IntelCondition(SystemGraph graph) => _graph = graph;

    public string TypeKey     => "intel";
    public string DisplayName => "Intel report";

    public string Description =>
        "Fires when someone reports a pilot in a system you are watching. Give a list of " +
        "systems, or one system and a jump range to cover everything around it. Reads the " +
        "intel channels already being parsed under Settings → Chat Logs.";

    public object ParameterSchema => new
    {
        type = "object",
        properties = new
        {
            systems = new
            {
                type        = "string",
                description = "Comma-separated system names to watch, e.g. \"C-FD0D, Y-ORBJ\". " +
                              "May be left empty if a jump range is given instead.",
            },
            within_jumps_of = new
            {
                type        = "string",
                description = "Optional. A system name; everything within the jump range below is " +
                              "watched. Combined with the list above rather than replacing it.",
            },
            jumps = new
            {
                type        = "integer",
                description = "How many gate jumps out from 'within_jumps_of' to watch. 0 means that " +
                              "system only. Capped at 15 — past that it stops being a neighbourhood.",
            },
            min_players = new
            {
                type        = "integer",
                description = "Only fire when the report is of at least this many pilots. Default 1.",
            },
            ignore_no_visual = new
            {
                type        = "boolean",
                description = "Skip reports flagged NV (no visual) — someone relaying a contact they " +
                              "cannot actually see.",
            },
        },
        required = Array.Empty<string>(),
    };

    public string Describe(JsonElement config)
    {
        var systems = ReadCsv(config, "systems");
        var origin  = ReadString(config, "within_jumps_of");
        var jumps   = ReadInt(config, "jumps") ?? 0;
        var minimum = ReadInt(config, "min_players") ?? 1;

        var parts = new List<string>();
        if (systems.Count > 0) parts.Add(string.Join(", ", systems));
        if (!string.IsNullOrWhiteSpace(origin))
            parts.Add(jumps > 0 ? $"within {jumps} jump{(jumps == 1 ? "" : "s")} of {origin}" : origin);

        if (parts.Count == 0) return "Intel (no systems chosen)";

        var where = string.Join(" or ", parts);
        return minimum > 1 ? $"Intel in {where}, {minimum}+ pilots" : $"Intel in {where}";
    }

    public (string Title, string Body) DefaultText(
        string alarmName, JsonElement config, IReadOnlyList<AlarmMatch> matches)
    {
        // Where matters more than how many, so the systems go in the title — that is what is
        // readable at a glance on a dialog that has just appeared over the game.
        var systems = matches
            .Select(m => m.Detail?.TryGetValue("system", out var s) == true ? s?.ToString() : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .Take(3)
            .ToList();

        var where = systems.Count switch
        {
            0 => "",
            _ => " — " + string.Join(", ", systems) +
                 (matches.Select(m => m.Detail?["system"]?.ToString()).Distinct().Count() > 3 ? ", …" : ""),
        };

        var headline = matches.Count == 1 ? "Hostile reported" : $"{matches.Count} hostile reports";

        return ($"{headline}{where}", IAlarmCondition.JoinSummaries(matches));
    }

    public async Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default)
    {
        var names   = ReadCsv(config, "systems");
        var origin  = ReadString(config, "within_jumps_of");
        var jumps   = Math.Clamp(ReadInt(config, "jumps") ?? 0, 0, MaxJumps);
        var minimum = Math.Max(1, ReadInt(config, "min_players") ?? 1);
        var skipNv  = ReadBool(config, "ignore_no_visual");

        await using var conn = AppDb.Connect();
        await conn.OpenAsync(ct);

        var watched   = new HashSet<int>();
        var distances = new Dictionary<int, int>();   // from the origin, when there is one

        foreach (var id in await ResolveSystemsAsync(conn, names, ct)) watched.Add(id);

        if (!string.IsNullOrWhiteSpace(origin))
        {
            var originIds = await ResolveSystemsAsync(conn, [origin], ct);
            foreach (var oid in originIds)
                foreach (var (id, hops) in await _graph.DistancesWithinAsync(oid, jumps, ct))
                {
                    watched.Add(id);
                    // The nearer figure when two origins resolve — a name given twice.
                    if (!distances.TryGetValue(id, out var known) || hops < known) distances[id] = hops;
                }
        }

        // No resolvable system means nothing to watch. Returning empty rather than everything
        // matters: a typo in a system name must go quiet, not alert on the whole cluster.
        if (watched.Count == 0) return [];

        var cutoff = (ctx.Now - Lookback).ToUniversalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "+00:00";

        var idList = string.Join(",", watched);

        var reports = new List<(long Id, string System, int Count, string Reporter, string? Note,
                               DateTime At, int SystemId)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = AppDb.CaseInsensitiveLike($"""
                SELECT "Id", "SystemName", "PlayerCount", "ReporterName", "Note", "ReportedAt", "SystemId"
                FROM "IntelReports"
                WHERE "ReportedAt" >= @cutoff
                  AND "SystemId" IN ({idList})
                  AND "PlayerCount" >= @minimum
                  {(skipNv ? """AND "NoVisual" = FALSE""" : "")}
                ORDER BY "Id" DESC
                LIMIT {MaxReports}
                """);
            cmd.AddWithValue("@cutoff", cutoff);
            cmd.AddWithValue("@minimum", minimum);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                reports.Add((
                    r.GetInt64(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? 0 : r.GetInt32(2),
                    r.IsDBNull(3) ? "" : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    // ⚠️ ReportedAt is TEXT on both engines — "2026-09-11T23:19:09Z" — and Npgsql
                    // refuses GetDateTime on a text column, where SQLite quietly parsed it. Read
                    // as the string it is and parse it as the UTC instant it is; the seen-key is
                    // built from this, so it must not depend on the machine's clock setting.
                    r.IsDBNull(5) ? default : ParseUtc(r.GetString(5)),
                    r.IsDBNull(6) ? 0 : r.GetInt32(6)));
        }

        if (reports.Count == 0) return [];

        // Named pilots and their hulls, kept apart: the announcement says the hulls first, the
        // names after, and a name with the hull in brackets could do neither.
        var pilots = new Dictionary<long, List<string>>();
        var hulls  = new Dictionary<long, List<string>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = AppDb.CaseInsensitiveLike($"""
                SELECT "IntelReportId", "CharacterName", "ShipName"
                FROM "IntelReportCharacters"
                WHERE "IntelReportId" IN ({string.Join(",", reports.Select(x => x.Id))})
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var reportId = r.GetInt64(0);
                var name     = r.IsDBNull(1) ? "" : r.GetString(1);
                var ship     = r.IsDBNull(2) ? null : r.GetString(2);
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (!pilots.TryGetValue(reportId, out var list)) pilots[reportId] = list = [];
                list.Add(name);
                if (!string.IsNullOrWhiteSpace(ship))
                {
                    if (!hulls.TryGetValue(reportId, out var hl)) hulls[reportId] = hl = [];
                    hl.Add(ship);
                }
            }
        }

        var matches = new List<AlarmMatch>(reports.Count);
        foreach (var rep in reports)
        {
            var names_ = pilots.TryGetValue(rep.Id, out var list) ? list : [];
            var ships  = hulls.TryGetValue(rep.Id, out var hl) ? hl : [];
            var who    = names_.Count > 0
                ? " — " + string.Join(", ", names_.Take(5)) + (names_.Count > 5 ? $", +{names_.Count - 5}" : "")
                : "";

            var headline = rep.Count == 1 ? "1 pilot" : $"{rep.Count} pilots";
            var jumpsOut = distances.TryGetValue(rep.SystemId, out var d) ? d : (int?)null;

            // Keyed on WHERE, WHO was seen and a five-minute slice of WHEN — and not on the
            // report's row id, nor on who said it. A row id changes whenever a chat log is
            // re-read (routine on a synced share), which is what once produced the same nine
            // alerts three times over. The reporter is left out and the time is coarse on
            // purpose: the same pilots called out in the same system by three people inside a
            // minute are one sighting, and were being announced three times. Five minutes on,
            // the same call is a new sighting — pilots circle back, and a sighting that is not
            // repeated reads as a sighting that ended.
            matches.Add(new AlarmMatch(
                MatchKey(rep.SystemId, rep.At, names_, rep.Count),
                $"{headline} in {rep.System}{who}")
            {
                Detail = new Dictionary<string, object?>
                {
                    ["report_id"] = rep.Id,
                    ["system"]    = rep.System,
                    ["system_id"] = rep.SystemId,
                    ["count"]     = rep.Count,
                    ["pilots"]    = names_,
                    ["hulls"]     = ships,
                    ["note"]      = rep.Note,
                    ["jumps"]     = jumpsOut,
                    ["at"]        = rep.At,
                },
            });
        }

        return matches;
    }

    /// <summary>
    /// The sentence to be spoken, exactly. Count and system first, because that is what decides
    /// whether to warp now; then the hulls; then the names; then whatever else the report said.
    /// Never who reported it. One sighting per system: three people calling the same pilot in
    /// the same system inside a minute is one fact, said once, with everything any of them added.
    /// Newest system first when there are several.
    /// </summary>
    public string? Announcement(JsonElement config, IReadOnlyList<AlarmMatch> matches)
        => ComposeAnnouncement(matches);

    internal static string? ComposeAnnouncement(IReadOnlyList<AlarmMatch> matches)
    {
        if (matches.Count == 0) return null;

        var perSystem = matches
            .Where(m => m.Detail is not null)
            .GroupBy(m => m.Detail!.TryGetValue("system_id", out var s) && s is int id ? id : 0)
            .Select(g => new
            {
                System = g.Select(m => Str(m.Detail!, "system")).FirstOrDefault(s => s.Length > 0) ?? "an unknown system",
                Newest = g.Max(m => m.Detail!.TryGetValue("at", out var a) && a is DateTime at ? at : DateTime.MinValue),
                Count  = g.Max(m => m.Detail!.TryGetValue("count", out var c) && c is int n ? n : 0),
                Pilots = g.SelectMany(m => Names(m.Detail!, "pilots")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Hulls  = g.SelectMany(m => Names(m.Detail!, "hulls")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Notes  = g.Select(m => Str(m.Detail!, "note").Trim().TrimEnd('.')).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Jumps  = g.Select(m => m.Detail!.TryGetValue("jumps", out var j) && j is int hops ? hops : (int?)null).Where(j => j is not null).Min(),
            })
            .OrderByDescending(s => s.Newest)
            .ToList();

        var sb = new System.Text.StringBuilder();
        foreach (var s in perSystem)
        {
            var count = Math.Max(s.Count, s.Pilots.Count);
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(count == 1 ? "1 hostile" : $"{count} hostiles").Append(" reported in ").Append(s.System);
            if (s.Jumps is { } jumps)
                sb.Append(jumps switch { 0 => ", here", 1 => ", 1 jump out", _ => $", {jumps} jumps out" });
            sb.Append('.');

            if (s.Hulls.Count > 0)
                sb.Append(" Flying ").Append(s.Hulls.Count == 1 ? Article(s.Hulls[0]) : Join(s.Hulls)).Append('.');
            if (s.Pilots.Count > 0)
                sb.Append(' ').Append(string.Join(", ", s.Pilots.Take(5)))
                  .Append(s.Pilots.Count > 5 ? $" and {s.Pilots.Count - 5} more." : ".");
            foreach (var note in s.Notes.Take(3))
                sb.Append(' ').Append(note).Append('.');
        }
        return sb.ToString();

        static string Str(IReadOnlyDictionary<string, object?> d, string key)
            => d.TryGetValue(key, out var v) && v is string s ? s : "";
        static IEnumerable<string> Names(IReadOnlyDictionary<string, object?> d, string key)
            => d.TryGetValue(key, out var v) && v is IEnumerable<string> list ? list.Where(n => !string.IsNullOrWhiteSpace(n)) : [];
        static string Article(string hull)
            => ("aeiou".Contains(char.ToLowerInvariant(hull[0])) ? "an " : "a ") + hull;
        static string Join(List<string> items)
            => items.Count <= 3
                ? string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1]
                : string.Join(", ", items.Take(3)) + " and more";
    }

    internal static string MatchKey(int systemId, DateTime at, IReadOnlyList<string> names, int count)
    {
        var bucket    = new DateTime(at.Ticks - at.Ticks % TimeSpan.FromMinutes(5).Ticks, DateTimeKind.Utc);
        var signature = names.Count > 0
            ? string.Join(",", names.Select(n => n.ToLowerInvariant()).Distinct().Order())
            : $"n{count}";
        return $"intel:{systemId}|{bucket:yyyy-MM-ddTHH:mm}|{signature}";
    }

    private static DateTime ParseUtc(string text)
        => DateTime.TryParse(text, CultureInfo.InvariantCulture,
                             DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
           ? at : default;

    private static async Task<List<int>> ResolveSystemsAsync(
        DbConnection conn, IReadOnlyList<string> names, CancellationToken ct)
    {
        var ids = new List<int>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            await using var cmd = conn.Command("""SELECT "SolarSystemId" FROM "SdeSolarSystems" WHERE upper("Name") = upper(@n) LIMIT 1""");
            cmd.AddWithValue("@n", name.Trim());

            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is not null and not DBNull) ids.Add(Convert.ToInt32(result));
        }
        return ids;
    }

    private static List<string> ReadCsv(JsonElement config, string name) =>
        ReadString(config, name) is { } s && !string.IsNullOrWhiteSpace(s)
            ? [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];

    private static string? ReadString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? ReadInt(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static bool ReadBool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.True;
}
