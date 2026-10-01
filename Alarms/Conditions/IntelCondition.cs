using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using EveConsole.Localization;
using EveConsole.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using EveConsole.Data;
using EveConsole.Models;

namespace EveConsole.Alarms.Conditions;

/// <summary>
/// Fires when a pilot is reported within a jump range of what is being watched: the capsuleer's
/// characters while they are undocked (the default), the named characters wherever they are,
/// or a list of systems. One range covers all of them; 0 is the system itself.
///
/// <para>Matches are keyed on the system, the pilots and a five-minute slice of time (see
/// <see cref="MatchKey"/>), so the same sighting called by three people is one alert, and a
/// pilot re-reported later is news again.</para>
/// </summary>
public sealed class IntelCondition : IAlarmCondition
{
    /// <summary>
    /// How far back a check looks. Bounded because stale intel is worthless — an alarm that has
    /// been off for a day should not wake up and recite yesterday's sightings.
    /// </summary>
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(2);

    /// <summary>
    /// How far back a check around characters looks. Shorter, because the watched systems move
    /// with the pilot: a report an hour old in a system they have just jumped next to is not a
    /// hostile near them, and would be announced as if it were.
    /// </summary>
    private static readonly TimeSpan CharacterLookback = TimeSpan.FromMinutes(10);

    private const int MaxReports = 100;

    /// <summary>
    /// Range ceiling. Beyond this the watched set stops being "around me" and becomes most of
    /// the region, and the system list goes into the query as an IN clause.
    /// </summary>
    private const int MaxJumps = 15;

    /// <summary>What a new alarm starts on. Around a pilot, their own system alone is too
    /// little warning; the systems a list names are watched as they are unless asked.</summary>
    private const int DefaultJumps = 5;

    // What the range is drawn around. The strings are what the editor shows and what the config
    // stores; the reader is lenient about case and a few synonyms.
    internal const string AroundUndocked   = "Undocked characters";
    internal const string AroundCharacters = "Characters";
    internal const string AroundSystems    = "Systems";
    private static readonly string[] AroundChoices = [AroundUndocked, AroundCharacters, AroundSystems];

    private readonly SystemGraph _graph;

    public IntelCondition(SystemGraph graph) => _graph = graph;

    public string TypeKey     => "intel";
    public string DisplayName => "Intel report";

    public string Description =>
        "Fires when someone reports a pilot within a jump range of what you are watching: your " +
        "characters while they are undocked (the default), named characters wherever they are, " +
        "or a list of systems. A range of 0 watches just those systems. Reads the intel " +
        "channels already being parsed under Settings → Chat Logs.";

    public object ParameterSchema => new
    {
        type = "object",
        properties = new
        {
            around = new
            {
                type        = "string",
                @enum       = AroundChoices,
                @default    = AroundUndocked,
                title       = "Watch around",
                description = "\"Undocked characters\": wherever your online characters are in space; " +
                              "docked ones are not watched. \"Characters\": wherever they are while " +
                              "online, docked or not. \"Systems\": the systems listed below.",
            },
            characters = new
            {
                type        = "array",
                items       = new { type = "string" },
                format      = "character-name",
                show_if     = new Dictionary<string, string[]> { ["around"] = [AroundUndocked, AroundCharacters] },
                title       = "Characters",
                description = "Optional. Names of your own characters to watch around; leave empty " +
                              "for all of them. Only characters that are online count.",
            },
            systems = new
            {
                type        = "array",
                items       = new { type = "string" },
                format      = "system-name",
                show_if     = new Dictionary<string, string[]> { ["around"] = [AroundSystems] },
                title       = "Systems",
                description = "System names to watch around, e.g. [\"Jita\", \"Amarr\"]. Used when " +
                              "'around' is \"Systems\".",
            },
            jumps = new
            {
                type        = "integer",
                @default    = DefaultJumps,
                description = "How many gate jumps out from each character or system to watch. 0 " +
                              "means their own system only. Capped at 15 — past that it stops being " +
                              "a neighbourhood.",
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

    // The editor's words; the three above are the agent's and stay English.
    public string ScreenName        => AlarmsText.CheckIntel;
    public string ScreenDescription => AlarmsText.CheckIntelNote;

    public AlarmFieldText? ScreenField(string property) => property switch
    {
        "around"           => new(AlarmsText.IntelAroundLabel,      AlarmsText.IntelAroundNote),
        "characters"       => new(AlarmsText.IntelCharactersLabel,  AlarmsText.IntelCharactersNote),
        // Examples as the game names them in the interface language: names the box takes.
        "systems"          => new(AlarmsText.IntelSystemsLabel,     string.Format(AlarmsText.IntelSystemsNote,
                                  SdeNames.SolarSystem(30000142, "Jita"), SdeNames.SolarSystem(30002187, "Amarr"))),
        "jumps"            => new(AlarmsText.IntelJumpsLabel,       AlarmsText.IntelJumpsNote),
        "min_players"      => new(AlarmsText.IntelMinPlayersLabel,  AlarmsText.IntelMinPlayersNote),
        "ignore_no_visual" => new(AlarmsText.IntelIgnoreNvLabel,    AlarmsText.IntelIgnoreNvNote),
        _                  => null,
    };

    public string? ScreenOption(string property, string value) => value switch
    {
        AroundUndocked   => AlarmsText.OptionUndockedCharacters,
        AroundCharacters => AlarmsText.OptionCharacters,
        AroundSystems    => AlarmsText.OptionSystems,
        _                => null,
    };

    public string Describe(JsonElement config)
    {
        var around  = ReadAround(config);
        var jumps   = ReadJumps(config, around);
        var minimum = ReadInt(config, "min_players") ?? 1;
        var range   = jumps > 0 ? $"within {jumps} jump{(jumps == 1 ? "" : "s")} of " : "";

        string where;
        if (around == AroundSystems)
        {
            var systems = SystemNames(config);
            if (systems.Count == 0) return "Intel (no systems chosen)";
            where = (jumps > 0 ? range : "in ") + string.Join(", ", systems);
        }
        else
        {
            var characters = ReadList(config, "characters");
            var who = characters.Count > 0 ? string.Join(", ", characters) : "your characters";
            if (around == AroundUndocked) who += " while undocked";
            where = (jumps > 0 ? range : "in the system of ") + who;
        }

        return minimum > 1 ? $"Intel {where}, {minimum}+ pilots" : $"Intel {where}";
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

        // One report is said without its number; more are counted, in the language's plural.
        var headline = matches.Count == 1
            ? AlarmsText.IntelHostileReported
            : Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelHostileReportsOther), matches.Count);

        return ($"{headline}{where}", IAlarmCondition.JoinSummaries(matches));
    }

    public async Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default)
    {
        var around  = ReadAround(config);
        var jumps   = ReadJumps(config, around);
        var minimum = Math.Max(1, ReadInt(config, "min_players") ?? 1);
        var skipNv  = ReadBool(config, "ignore_no_visual");

        await using var conn = AppDb.Connect();
        await conn.OpenAsync(ct);

        var watched   = new HashSet<int>();
        var distances = new Dictionary<int, int>();      // from the nearest origin
        var nearest   = new Dictionary<int, string>();   // the character that origin is, around characters

        // Everything within range of one origin. The nearer figure wins when two origins reach
        // the same system — two characters a jump apart, or a system listed twice.
        async Task SpreadAsync(int origin, string? who)
        {
            foreach (var (id, hops) in await _graph.DistancesWithinAsync(origin, jumps, ct))
            {
                watched.Add(id);
                if (distances.TryGetValue(id, out var known) && hops >= known) continue;
                distances[id] = hops;
                if (who is not null) nearest[id] = who;
            }
        }

        if (around == AroundSystems)
        {
            foreach (var id in await ResolveSystemsAsync(conn, SystemNames(config), ct))
                await SpreadAsync(id, null);
        }
        else
        {
            foreach (var (name, systemId) in await OriginsAsync(ctx, around, ReadList(config, "characters"), ct))
                await SpreadAsync(systemId, name);
        }

        // No resolvable system means nothing to watch. Returning empty rather than everything
        // matters: a typo in a system name must go quiet, not alert on the whole cluster — and
        // every character docked must go quiet too, which is the point of watching undocked ones.
        if (watched.Count == 0) return [];

        var lookback = around == AroundSystems ? Lookback : CharacterLookback;
        // ⚠️ In the column's own format. ReportedAt is TEXT, "2026-09-11T23:19:09Z", compared as
        // text; a cutoff written "2026-09-11 22:19:09+00:00" sorts below every report of the same
        // day (' ' before 'T'), so the window was really "since midnight".
        var cutoff = (ctx.Now - lookback).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

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

            // How far out is only worth saying when there is a range: the systems a list names
            // at 0 jumps are simply where the report was. Around a character it is always said,
            // with the character, since they are what the distance is from.
            var near     = nearest.GetValueOrDefault(rep.SystemId);
            var jumpsOut = (jumps > 0 || near is not null) && distances.TryGetValue(rep.SystemId, out var d)
                ? d : (int?)null;
            var from = near is null || jumpsOut is not { } hops ? ""
                     : hops == 0 ? $" (where {near} is)"
                     : $" ({hops} jump{(hops == 1 ? "" : "s")} from {near})";

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
                $"{headline} in {rep.System}{from}{who}")
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
                    ["near"]      = near,
                    ["at"]        = rep.At,
                },
            });
        }

        return matches;
    }

    /// <summary>
    /// The characters to draw the range around, with the system each is in: the capsuleer's own,
    /// online, and — around undocked characters — in space. Named ones only when names are
    /// given. A character logged off is not watched wherever they were left: nobody is there to
    /// warn.
    /// </summary>
    private static async Task<List<(string Name, int SystemId)>> OriginsAsync(
        AlarmEvaluationContext ctx, string around, IReadOnlyList<string> named, CancellationToken ct)
    {
        await using var db = await ctx.DbFactory.CreateDbContextAsync(ct);
        var rows = await (
            from s in db.CharacterStatuses.AsNoTracking()
            join c in db.Characters.AsNoTracking() on s.CharacterId equals c.Id
            where s.Online && s.SolarSystemId != null
            select new { c.Name, s.SolarSystemId, s.StationId, s.StructureId }).ToListAsync(ct);

        var wanted = named.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. rows
            .Where(r => wanted.Count == 0 || wanted.Contains(r.Name))
            .Where(r => around != AroundUndocked || (r.StationId is null && r.StructureId is null))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => (r.Name, r.SolarSystemId!.Value))];
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
                System = g.Select(m => Str(m.Detail!, "system")).FirstOrDefault(s => s.Length > 0) ?? AlarmsText.UnknownSystem,
                Newest = g.Max(m => m.Detail!.TryGetValue("at", out var a) && a is DateTime at ? at : DateTime.MinValue),
                Count  = g.Max(m => m.Detail!.TryGetValue("count", out var c) && c is int n ? n : 0),
                Pilots = g.SelectMany(m => Names(m.Detail!, "pilots")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Hulls  = g.SelectMany(m => Names(m.Detail!, "hulls")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Notes  = g.Select(m => Str(m.Detail!, "note").Trim().TrimEnd('.')).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Jumps  = g.Select(m => m.Detail!.TryGetValue("jumps", out var j) && j is int hops ? hops : (int?)null).Where(j => j is not null).Min(),
                Near   = g.Select(m => Str(m.Detail!, "near")).FirstOrDefault(n => n.Length > 0),
            })
            .OrderByDescending(s => s.Newest)
            .ToList();

        // Said to the person, so in the interface language, a whole sentence per fact: how many
        // and where, how far out, the hulls, the names, then each note as its own sentence.
        var sb = new System.Text.StringBuilder();
        foreach (var s in perSystem)
        {
            var count = Math.Max(s.Count, s.Pilots.Count);
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s.Jumps is { } jumps
                ? Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidReportedAtOther), count, s.System,
                                 s.Near is { } near ? JumpsFrom(jumps, near) : JumpsOut(jumps))
                : Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidReportedOther), count, s.System));

            if (s.Hulls.Count > 0)
                sb.Append(' ').Append(string.Format(AlarmsText.IntelSaidFlying,
                    s.Hulls.Count == 1 ? AlarmWords.Hull(s.Hulls[0]) : AlarmWords.List(s.Hulls)));
            if (s.Pilots.Count > 0)
            {
                var names = string.Join(", ", s.Pilots.Take(5));
                sb.Append(' ').Append(s.Pilots.Count > 5
                    ? string.Format(AlarmsText.IntelSaidPilotsAndMore, names, s.Pilots.Count - 5)
                    : string.Format(AlarmsText.SaidSentence, names));
            }
            foreach (var note in s.Notes.Take(3))
                sb.Append(' ').Append(string.Format(AlarmsText.SaidSentence, note));
        }
        return sb.ToString();

        static string Str(IReadOnlyDictionary<string, object?> d, string key)
            => d.TryGetValue(key, out var v) && v is string s ? s : "";
        static IEnumerable<string> Names(IReadOnlyDictionary<string, object?> d, string key)
            => d.TryGetValue(key, out var v) && v is IEnumerable<string> list ? list.Where(n => !string.IsNullOrWhiteSpace(n)) : [];
        static string JumpsOut(int jumps) => jumps == 0
            ? AlarmsText.IntelSaidHere
            : Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidJumpsOutOther), jumps);
        static string JumpsFrom(int jumps, string near) => jumps == 0
            ? string.Format(AlarmsText.IntelSaidWhereIs, near)
            : Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidJumpsFromOther), jumps, near);
    }

    internal static string MatchKey(int systemId, DateTime at, IReadOnlyList<string> names, int count)
    {
        var bucket    = new DateTime(at.Ticks - at.Ticks % TimeSpan.FromMinutes(5).Ticks, DateTimeKind.Utc);
        var signature = names.Count > 0
            ? string.Join(",", names.Select(n => n.ToLowerInvariant()).Distinct().Order())
            : $"n{count}";
        return $"intel:{systemId}|{bucket:yyyy-MM-ddTHH:mm}|{signature}";
    }

    public string? NormaliseConfig(string? json) => UpgradeConfig(json);

    /// <summary>
    /// Rewrites a config from the first shape of this check — <c>systems</c> as a comma list,
    /// and one <c>within_jumps_of</c> system with the range around it — into "around Systems"
    /// with one list and one range. Null when there is nothing to do. Run once at startup over
    /// every alarm of this type, so an alarm made before characters could be watched keeps
    /// watching its systems rather than turning into the new default; and on what the agent
    /// writes, which may name systems without saying "around".
    ///
    /// <para>The one change of meaning is deliberate: a list given beside a ranged system was
    /// watched at 0 jumps, and is now watched at the range too.</para>
    /// </summary>
    internal static string? UpgradeConfig(string? json)
    {
        System.Text.Json.Nodes.JsonObject config;
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json)
                is not System.Text.Json.Nodes.JsonObject o) return null;
            config = o;
        }
        catch { return null; }

        if (config.ContainsKey("around")) return null;

        // An array without the choice is the agent's: Systems, range and all, as the reader
        // takes it. Without systems either, the default is what was meant, and left implicit.
        if (config["systems"] is System.Text.Json.Nodes.JsonArray { Count: > 0 } && !config.ContainsKey("within_jumps_of"))
        {
            config["around"] = AroundSystems;
            return config.ToJsonString();
        }

        // The first shape: a comma list, or a ranged system.
        var origin = config["within_jumps_of"] is System.Text.Json.Nodes.JsonValue ov
                  && ov.TryGetValue<string>(out var os) && !string.IsNullOrWhiteSpace(os) ? os.Trim() : null;
        var csv = config["systems"] is System.Text.Json.Nodes.JsonValue sv && sv.TryGetValue<string>(out var ss) ? ss : null;
        if (origin is null && csv is null) return null;

        var systems = SplitList(csv);
        if (origin is not null && !systems.Contains(origin, StringComparer.OrdinalIgnoreCase)) systems.Add(origin);
        // A first-shape alarm naming no system matched nothing; it is left to match nothing.
        config["around"]  = AroundSystems;
        config["systems"] = new System.Text.Json.Nodes.JsonArray(systems.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray());
        // The range applied to the one system only: with none, the list was watched as it is.
        if (origin is null) config["jumps"] = 0;
        config.Remove("within_jumps_of");
        return config.ToJsonString();
    }

    /// <summary>
    /// What the range is drawn around. A config without the choice was written before there was
    /// one, by the editor or by the agent: its systems, if it names any, else the default.
    /// </summary>
    internal static string ReadAround(JsonElement config)
    {
        var text = ReadString(config, "around")?.Trim() ?? "";
        if (text.Length > 0)
        {
            if (AroundChoices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase)) is { } exact)
                return exact;
            if (text.Contains("undock", StringComparison.OrdinalIgnoreCase)) return AroundUndocked;
            if (text.StartsWith("char", StringComparison.OrdinalIgnoreCase))  return AroundCharacters;
            if (text.StartsWith("sys", StringComparison.OrdinalIgnoreCase))   return AroundSystems;
        }
        return SystemNames(config).Count > 0 ? AroundSystems : AroundUndocked;
    }

    /// <summary>The range, capped. Left out, a list of systems is watched as it is — what the
    /// first shape of this check did — and characters at the default.</summary>
    private static int ReadJumps(JsonElement config, string around) =>
        Math.Clamp(ReadInt(config, "jumps") ?? (around == AroundSystems ? 0 : DefaultJumps), 0, MaxJumps);

    /// <summary>The systems to watch: the list, and the one ranged system of the first shape if
    /// a config still carries it.</summary>
    private static List<string> SystemNames(JsonElement config)
    {
        var names = ReadList(config, "systems");
        if (ReadString(config, "within_jumps_of") is { } origin && !string.IsNullOrWhiteSpace(origin)
            && !names.Contains(origin.Trim(), StringComparer.OrdinalIgnoreCase))
            names.Add(origin.Trim());
        return names;
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

            // The English first, then the client's other languages: 吉他 is Jita.
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is not null and not DBNull) ids.Add(Convert.ToInt32(result));
            else if (await OtherLanguageNames.IdAsync(conn, SdeNameKind.SolarSystem, name, ct) is { } other) ids.Add((int)other);
        }
        return ids;
    }

    // A full-width comma and 、 separate names too: typed in Chinese, "Jita，Amarr" was one name.
    private static readonly char[] ListSeparators = [',', '，', '、'];

    private static List<string> SplitList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>A list as the editor writes it — an array — or as a comma list, as the first
    /// shape of this check and a hand-written config have it.</summary>
    private static List<string> ReadList(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return [];
        return p.ValueKind switch
        {
            JsonValueKind.Array  => [.. p.EnumerateArray()
                                        .Where(x => x.ValueKind == JsonValueKind.String)
                                        .Select(x => x.GetString()?.Trim() ?? "")
                                        .Where(x => x.Length > 0)],
            JsonValueKind.String => SplitList(p.GetString()),
            _                    => [],
        };
    }

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
