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

        var watched = new HashSet<int>();

        foreach (var id in await ResolveSystemsAsync(conn, names, ct)) watched.Add(id);

        if (!string.IsNullOrWhiteSpace(origin))
        {
            var originIds = await ResolveSystemsAsync(conn, [origin], ct);
            foreach (var oid in originIds)
                foreach (var id in await _graph.WithinJumpsAsync(oid, jumps, ct))
                    watched.Add(id);
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
            cmd.CommandText = $"""
                SELECT "Id", "SystemName", "PlayerCount", "ReporterName", "Note", "ReportedAt", "SystemId"
                FROM "IntelReports"
                WHERE "ReportedAt" >= $cutoff
                  AND "SystemId" IN ({idList})
                  AND "PlayerCount" >= $minimum
                  {(skipNv ? """AND "NoVisual" = FALSE""" : "")}
                ORDER BY "Id" DESC
                LIMIT {MaxReports}
                """;
            cmd.AddWithValue("$cutoff", cutoff);
            cmd.AddWithValue("$minimum", minimum);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                reports.Add((
                    r.GetInt64(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? 0 : r.GetInt32(2),
                    r.IsDBNull(3) ? "" : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? default : r.GetDateTime(5),
                    r.IsDBNull(6) ? 0 : r.GetInt32(6)));
        }

        if (reports.Count == 0) return [];

        // Named pilots, so the alert can say who rather than just how many.
        var pilots = new Dictionary<long, List<string>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT "IntelReportId", "CharacterName", "ShipName"
                FROM "IntelReportCharacters"
                WHERE "IntelReportId" IN ({string.Join(",", reports.Select(x => x.Id))})
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var reportId = r.GetInt64(0);
                var name     = r.IsDBNull(1) ? "" : r.GetString(1);
                var ship     = r.IsDBNull(2) ? null : r.GetString(2);
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (!pilots.TryGetValue(reportId, out var list)) pilots[reportId] = list = [];
                list.Add(string.IsNullOrWhiteSpace(ship) ? name : $"{name} ({ship})");
            }
        }

        var matches = new List<AlarmMatch>(reports.Count);
        foreach (var rep in reports)
        {
            var who = pilots.TryGetValue(rep.Id, out var list) && list.Count > 0
                ? " — " + string.Join(", ", list.Take(5)) + (list.Count > 5 ? $", +{list.Count - 5}" : "")
                : "";

            var headline = rep.Count == 1 ? "1 pilot" : $"{rep.Count} pilots";

            // Keyed on what identifies the sighting — when, who said it, where — and NOT on the
            // report's row id. A chat log re-read (routine on a synced share, where the file
            // length appears to go backwards) deletes and re-inserts every report with a fresh
            // id, and an id-based key would make hours-old sightings look new every time that
            // happened. That is what produced the same nine alerts three times over.
            matches.Add(new AlarmMatch(
                $"intel:{rep.At:yyyy-MM-ddTHH:mm:ss}|{rep.Reporter}|{rep.SystemId}",
                $"{headline} in {rep.System}{who} (reported by {rep.Reporter})")
            {
                Detail = new Dictionary<string, object?>
                {
                    ["report_id"] = rep.Id,
                    ["system"]    = rep.System,
                    ["count"]     = rep.Count,
                    ["reporter"]  = rep.Reporter,
                    ["pilots"]    = list,
                    ["note"]      = rep.Note,
                },
            });
        }

        return matches;
    }

    private static async Task<List<int>> ResolveSystemsAsync(
        DbConnection conn, IReadOnlyList<string> names, CancellationToken ct)
    {
        var ids = new List<int>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """SELECT "SolarSystemId" FROM "SdeSolarSystems" WHERE upper("Name") = upper($n) LIMIT 1""";
            cmd.AddWithValue("$n", name.Trim());

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
