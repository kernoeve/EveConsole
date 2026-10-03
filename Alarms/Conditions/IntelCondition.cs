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

    /// <summary>The farthest light-year range: past a jump freighter's reach with every skill.</summary>
    private const double MaxLy = 20;

    private const double MetresPerLightYear = 9.4607304725808e15;

    /// <summary>Every k-space system's position, for the light-year range. Read once: it only
    /// changes when CCP adds space.</summary>
    private static Dictionary<int, (double X, double Y, double Z)>? _positions;
    private static readonly SemaphoreSlim PositionsGate = new(1, 1);

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
        "Fires when someone reports a pilot within a jump range — or a light-year range, for what " +
        "is in jump-drive reach — of what you are watching: your characters while they are " +
        "undocked (the default), named characters wherever they are, or a list of systems. Either " +
        "range catching a report is enough. A jump range of 0 watches just those systems. Reads the intel " +
        "channels already being parsed under Settings → Chat Logs, and kills there with hostile " +
        "pilots among the attackers, which place those pilots in the system.";

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
            ly = new
            {
                type        = "number",
                beside      = "jumps",
                title       = "Light years",
                description = "Optional. Also watch every system within this many light years of each " +
                              "character or system, however many gates away — what a jump drive or a " +
                              "cyno puts in reach. A report caught by either range fires. Capped at 20.",
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
    public string ScreenDescription => Sentences.Join(AlarmsText.CheckIntelNote, AlarmsText.CheckIntelKillsNote);

    public AlarmFieldText? ScreenField(string property) => property switch
    {
        "around"           => new(AlarmsText.IntelAroundLabel,      AlarmsText.IntelAroundNote),
        "characters"       => new(AlarmsText.IntelCharactersLabel,  AlarmsText.IntelCharactersNote),
        // Examples as the game names them in the interface language: names the box takes.
        "systems"          => new(AlarmsText.IntelSystemsLabel,     string.Format(AlarmsText.IntelSystemsNote,
                                  SdeNames.SolarSystem(30000142, "Jita"), SdeNames.SolarSystem(30002187, "Amarr"))),
        "jumps"            => new(AlarmsText.IntelJumpsLabel,       AlarmsText.IntelJumpsNote),
        "ly"               => new(AlarmsText.IntelLyLabel,          AlarmsText.IntelLyNote),
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
        var ly      = ReadLy(config);
        var reach   = new List<string>();
        if (jumps > 0) reach.Add($"{jumps} jump{(jumps == 1 ? "" : "s")}");
        if (ly is { } l) reach.Add($"{l.ToString("0.#", CultureInfo.InvariantCulture)} ly");
        var range   = reach.Count > 0 ? $"within {string.Join(" or ", reach)} of " : "";

        string where;
        if (around == AroundSystems)
        {
            var systems = SystemNames(config);
            if (systems.Count == 0) return "Intel (no systems chosen)";
            where = (range.Length > 0 ? range : "in ") + string.Join(", ", systems);
        }
        else
        {
            var characters = ReadList(config, "characters");
            var who = characters.Count > 0 ? string.Join(", ", characters) : "your characters";
            if (around == AroundUndocked) who += " while undocked";
            where = (range.Length > 0 ? range : "in the system of ") + who;
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
        var ly      = ReadLy(config);
        var minimum = Math.Max(1, ReadInt(config, "min_players") ?? 1);
        var skipNv  = ReadBool(config, "ignore_no_visual");

        await using var conn = AppDb.Connect();
        await conn.OpenAsync(ct);

        var watched   = new HashSet<int>();
        var distances = new Dictionary<int, int>();      // from the nearest origin
        var nearest   = new Dictionary<int, string>();   // the character that origin is, around characters

        var lyDistances = new Dictionary<int, double>();   // light years from the nearest origin
        var lyNearest   = new Dictionary<int, string>();   // the character that is, around characters
        var positions   = ly is null ? null : await PositionsAsync(conn, ct);

        // Everything within range of one origin: by gates, and by light years when that is set.
        // The nearer figure wins when two origins reach the same system — two characters a jump
        // apart, or a system listed twice.
        async Task SpreadAsync(int origin, string? who)
        {
            foreach (var (id, hops) in await _graph.DistancesWithinAsync(origin, jumps, ct))
            {
                watched.Add(id);
                if (distances.TryGetValue(id, out var known) && hops >= known) continue;
                distances[id] = hops;
                if (who is not null) nearest[id] = who;
            }

            if (ly is not { } reach || positions is null || !positions.TryGetValue(origin, out var o)) return;
            foreach (var (id, p) in positions)
            {
                var dx = p.X - o.X; var dy = p.Y - o.Y; var dz = p.Z - o.Z;
                var far = Math.Sqrt(dx * dx + dy * dy + dz * dz) / MetresPerLightYear;
                if (far > reach) continue;
                watched.Add(id);
                if (lyDistances.TryGetValue(id, out var was) && far >= was) continue;
                lyDistances[id] = far;
                if (who is not null) lyNearest[id] = who;
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
                               DateTime At, int SystemId, int Flags, string? Gate, string? Ships)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = AppDb.CaseInsensitiveLike($"""
                SELECT "Id", "SystemName", "PlayerCount", "ReporterName", "Note", "ReportedAt", "SystemId",
                       "Flags", "Gate", "Ships"
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
                    r.IsDBNull(6) ? 0 : r.GetInt32(6),
                    r.IsDBNull(7) ? 0 : r.GetInt32(7),
                    r.IsDBNull(8) ? null : r.GetString(8),
                    r.IsDBNull(9) ? null : r.GetString(9)));
        }

        // Named pilots and their hulls, kept apart: the announcement says the hulls first, the
        // names after, and a name with the hull in brackets could do neither.
        var named = new List<(long ReportId, long CharacterId, string Name, string? Ship)>();
        if (reports.Count > 0)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = AppDb.CaseInsensitiveLike($"""
                SELECT "IntelReportId", "CharacterName", "ShipName", "CharacterId"
                FROM "IntelReportCharacters"
                WHERE "IntelReportId" IN ({string.Join(",", reports.Select(x => x.Id))})
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.IsDBNull(1) ? "" : r.GetString(1);
                if (string.IsNullOrWhiteSpace(name)) continue;
                named.Add((r.GetInt64(0), r.IsDBNull(3) ? 0 : r.GetInt64(3), name, r.IsDBNull(2) ? null : r.GetString(2)));
            }
        }

        // Friendlies are not hostiles: a blue named in the channel is neither said nor counted,
        // the same rule the live map and the killmail matches below go by.
        var friendlyIds = await FriendlyPilotsAsync(ctx, named.Select(n => n.CharacterId).Distinct().ToList(), ct);
        var pilots   = new Dictionary<long, List<string>>();
        var hulls    = new Dictionary<long, List<string>>();
        var friendly = new Dictionary<long, int>();
        foreach (var n in named)
        {
            if (friendlyIds.Contains(n.CharacterId)) { friendly[n.ReportId] = friendly.GetValueOrDefault(n.ReportId) + 1; continue; }
            if (!pilots.TryGetValue(n.ReportId, out var list)) pilots[n.ReportId] = list = [];
            list.Add(n.Name);
            if (!string.IsNullOrWhiteSpace(n.Ship))
            {
                if (!hulls.TryGetValue(n.ReportId, out var hl)) hulls[n.ReportId] = hl = [];
                hl.Add(n.Ship);
            }
        }

        var matches = new List<AlarmMatch>(reports.Count);
        foreach (var raw in reports)
        {
            // Less the friendlies it named; a report of only friendlies says nobody is there.
            var rep = raw with { Count = raw.Count - friendly.GetValueOrDefault(raw.Id) };
            if (rep.Count < Math.Max(1, minimum)) continue;

            var names_ = pilots.TryGetValue(rep.Id, out var list) ? list : [];
            // The pilots' hulls, then the hulls nobody was named in: "3 lokis" is three Lokis.
            var ships  = (hulls.TryGetValue(rep.Id, out var hl) ? hl : [])
                .Concat(EveConsole.Services.IntelDisplay.ParseShips(rep.Ships).Select(s => s.Name)).ToList();
            var who    = names_.Count > 0
                ? " — " + string.Join(", ", names_.Take(5)) + (names_.Count > 5 ? $", +{names_.Count - 5}" : "")
                : "";

            var headline = rep.Count == 1 ? "1 pilot" : $"{rep.Count} pilots";

            // How far out is only worth saying when there is a range: the systems a list names
            // at 0 jumps are simply where the report was. Around a character it is always said,
            // with the character, since they are what the distance is from.
            var (near, jumpsOut, lyOut, from) = HowFar(rep.SystemId, jumps, distances, nearest, lyDistances, lyNearest);

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
                    ["flags"]     = EveConsole.Services.IntelDisplay.FlagsEnglish(rep.Flags),
                    ["flag_bits"] = rep.Flags,
                    ["gate"]      = rep.Gate,
                    ["jumps"]     = jumpsOut,
                    ["ly"]        = lyOut,
                    ["near"]      = near,
                    ["at"]        = rep.At,
                },
            });
        }

        matches.AddRange(await KillMatchesAsync(ctx, watched, ctx.Now - lookback, minimum, jumps, distances, nearest, lyDistances, lyNearest, ct));
        return matches;
    }

    /// <summary>
    /// Kills in the watched systems with hostile pilots among the attackers, as sightings of
    /// those pilots.
    ///
    /// <para>⚠️ A kill is intel. Its attackers were in that system at that moment — the exact
    /// pilots and hulls, where a report may name neither — and a gang that kills its way through
    /// without anyone typing in the channel used to raise nothing at all. The same rules as the
    /// live map (<see cref="LiveIntelService"/>): hostile means not ours and not blue, NPC
    /// attackers are nobody, and the victim is not a sighting (a loss takes a pilot off the
    /// map). "Only no-visual" does not apply: a kill is never NV.</para>
    ///
    /// <para>Keyed like a report — system, five-minute slice, the pilots' names — so the same
    /// gang reported in the channel and seen on a kill in the same minutes is one alert.</para>
    /// </summary>
    /// <summary>Which of these characters are friendly: the user's own, in their corporations or
    /// alliances, or set to positive standing — by the corporation and alliance last fetched for
    /// them.</summary>
    private static async Task<HashSet<long>> FriendlyPilotsAsync(
        AlarmEvaluationContext ctx, List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var db = await ctx.DbFactory.CreateDbContextAsync(ct);
        var friends = await LiveIntelService.FriendlyAsync(db, ct);
        var flyFor  = await db.CharacterAffiliations.AsNoTracking()
            .Where(a => ids.Contains(a.CharacterId)).ToListAsync(ct);
        var result = ids.Where(friends.Characters.Contains).ToHashSet();
        foreach (var a in flyFor)
            if ((a.CorporationId > 0 && friends.Corporations.Contains(a.CorporationId))
                || (a.AllianceId > 0 && friends.Alliances.Contains(a.AllianceId)))
                result.Add(a.CharacterId);
        return result;
    }

    private static async Task<List<AlarmMatch>> KillMatchesAsync(
        AlarmEvaluationContext ctx, HashSet<int> watched, DateTimeOffset since, int minimum, int jumps,
        IReadOnlyDictionary<int, int> distances, IReadOnlyDictionary<int, string> nearest,
        IReadOnlyDictionary<int, double> lyDistances, IReadOnlyDictionary<int, string> lyNearest, CancellationToken ct)
    {
        await using var db = await ctx.DbFactory.CreateDbContextAsync(ct);
        var systems = watched.ToList();

        // ⚠️ Raw SQL for the time: SQLite cannot translate a DateTimeOffset comparison in LINQ.
        var kills = await db.KillMailDetails
            .FromSqlRaw("""SELECT * FROM "KillMailDetails" WHERE "KillMailTime" >= {0}""", since)
            .AsNoTracking()
            .Where(k => systems.Contains(k.SolarSystemId))
            .Select(k => new { k.KillMailId, k.KillMailTime, k.SolarSystemId })
            .ToListAsync(ct);
        if (kills.Count == 0) return [];

        var killIds = kills.Select(k => k.KillMailId).ToList();
        var attackers = await db.KillMailAttackers.AsNoTracking()
            .Where(a => killIds.Contains(a.KillMailId) && a.CharacterId != null)
            .Select(a => new { a.KillMailId, CharacterId = a.CharacterId!.Value, a.CorporationId, a.AllianceId, a.ShipTypeId })
            .ToListAsync(ct);

        var friendly = await LiveIntelService.FriendlyAsync(db, ct);
        var hostile = attackers
            .Where(a => !friendly.Characters.Contains(a.CharacterId)
                     && !(a.CorporationId is > 0 && friendly.Corporations.Contains(a.CorporationId.Value))
                     && !(a.AllianceId    is > 0 && friendly.Alliances.Contains(a.AllianceId.Value)))
            .ToLookup(a => a.KillMailId);
        if (!kills.Any(k => hostile[k.KillMailId].Any())) return [];

        // Names as the app knows them; a pilot it has not met yet is counted but not named.
        var pilotIds = hostile.SelectMany(g => g).Select(a => a.CharacterId).Distinct().ToList();
        var names = (await db.UniverseNames.AsNoTracking()
                .Where(n => pilotIds.Contains(n.EntityId))
                .Select(n => new { n.EntityId, n.Name })
                .ToListAsync(ct))
            .GroupBy(n => n.EntityId)
            .ToDictionary(g => g.Key, g => g.First().Name);

        var shipIds = hostile.SelectMany(g => g).Where(a => a.ShipTypeId is > 0).Select(a => a.ShipTypeId!.Value).Distinct().ToList();
        var shipNames = await db.SdeTypes.AsNoTracking()
            .Where(t => shipIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);
        var killSystems = kills.Select(k => k.SolarSystemId).Distinct().ToList();
        var systemNames = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => killSystems.Contains(s.SolarSystemId))
            .ToDictionaryAsync(s => s.SolarSystemId, s => s.Name, ct);

        var matches = new List<AlarmMatch>();
        foreach (var k in kills.OrderByDescending(k => k.KillMailTime))
        {
            var gang = hostile[k.KillMailId].ToList();
            var count = gang.Select(a => a.CharacterId).Distinct().Count();
            if (count == 0 || count < minimum) continue;

            var pilotNames = gang.Select(a => names.GetValueOrDefault(a.CharacterId))
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct().ToList();
            var hullNames = gang.Where(a => a.ShipTypeId is > 0)
                .Select(a => SdeNames.Type(a.ShipTypeId!.Value, shipNames.GetValueOrDefault(a.ShipTypeId!.Value) ?? ""))
                .Where(h => h.Length > 0).Distinct().ToList();
            var system = SdeNames.SolarSystem(k.SolarSystemId, systemNames.GetValueOrDefault(k.SolarSystemId) ?? "");
            var at     = k.KillMailTime.UtcDateTime;

            var (near, jumpsOut, lyOut, from) = HowFar(k.SolarSystemId, jumps, distances, nearest, lyDistances, lyNearest);
            var who = pilotNames.Count > 0
                ? " — " + string.Join(", ", pilotNames.Take(5)) + (pilotNames.Count > 5 ? $", +{pilotNames.Count - 5}" : "")
                : "";
            var headline = count == 1 ? "1 pilot" : $"{count} pilots";

            matches.Add(new AlarmMatch(
                MatchKey(k.SolarSystemId, at, pilotNames, count),
                $"{headline} on a kill in {system}{from}{who}")
            {
                Detail = new Dictionary<string, object?>
                {
                    ["killmail_id"] = k.KillMailId,
                    ["system"]      = system,
                    ["system_id"]   = k.SolarSystemId,
                    ["count"]       = count,
                    ["pilots"]      = pilotNames,
                    ["hulls"]       = hullNames,
                    ["note"]        = AlarmsText.IntelSaidOnKill,
                    ["jumps"]       = jumpsOut,
                    ["ly"]          = lyOut,
                    ["near"]        = near,
                    ["at"]          = at,
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
                Flags  = g.Aggregate(0, (f, m) => f | (m.Detail!.TryGetValue("flag_bits", out var b) && b is int bits ? bits : 0)),
                Gate   = g.Select(m => Str(m.Detail!, "gate")).FirstOrDefault(x => x.Length > 0),
                Jumps  = g.Select(m => m.Detail!.TryGetValue("jumps", out var j) && j is int hops ? hops : (int?)null).Where(j => j is not null).Min(),
                Ly     = g.Select(m => m.Detail!.TryGetValue("ly", out var l) && l is double far ? far : (double?)null).Where(l => l is not null).Min(),
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
                : s.Ly is { } ly
                ? Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidReportedAtOther), count, s.System,
                                 s.Near is { } lyNear ? string.Format(AlarmsText.IntelSaidLyFrom, ly.ToString("0.0", CultureInfo.CurrentCulture), lyNear)
                                                      : string.Format(AlarmsText.IntelSaidLyOut, ly.ToString("0.0", CultureInfo.CurrentCulture)))
                : Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidReportedOther), count, s.System));

            // What else was said — "Bubbles, gate camp · on the QZ-X77 gate." — before the
            // hulls: it decides how to go in.
            if (EveConsole.Services.IntelDisplay.Facts(s.Flags, s.Gate) is { } facts)
                sb.Append(' ').Append(string.Format(AlarmsText.SaidSentence, char.ToUpper(facts[0], CultureInfo.CurrentCulture) + facts[1..]));
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

    /// <summary>
    /// How far a system is, for the summary and the announcement: the gate jumps when the jump
    /// range caught it — that is how a gang comes — else the light years the light-year range
    /// caught it at. English, for the summary; the announcement says it from the detail.
    /// </summary>
    private static (string? Near, int? Jumps, double? Ly, string From) HowFar(
        int systemId, int jumps, IReadOnlyDictionary<int, int> distances, IReadOnlyDictionary<int, string> nearest,
        IReadOnlyDictionary<int, double> lyDistances, IReadOnlyDictionary<int, string> lyNearest)
    {
        var near = nearest.GetValueOrDefault(systemId);
        if (distances.TryGetValue(systemId, out var hops) && (jumps > 0 || near is not null))
            return (near, hops, null, near is null ? "" : hops == 0 ? $" (where {near} is)" : $" ({hops} jump{(hops == 1 ? "" : "s")} from {near})");
        if (distances.ContainsKey(systemId)) return (near, null, null, "");

        if (!lyDistances.TryGetValue(systemId, out var far)) return (null, null, null, "");
        var lyNear = lyNearest.GetValueOrDefault(systemId);
        var ly     = Math.Round(far, 1);
        var text   = ly.ToString("0.0", CultureInfo.InvariantCulture);
        return (lyNear, null, ly, lyNear is null ? $" ({text} ly out)" : $" ({text} ly from {lyNear})");
    }

    /// <summary>Every k-space system's position, read once.</summary>
    private static async Task<Dictionary<int, (double X, double Y, double Z)>> PositionsAsync(
        System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        if (_positions is { } cached) return cached;
        await PositionsGate.WaitAsync(ct);
        try
        {
            if (_positions is { } raced) return raced;
            var map = new Dictionary<int, (double, double, double)>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """SELECT "SolarSystemId", "X", "Y", "Z" FROM "SdeSolarSystems" WHERE "IsWormhole" = FALSE""";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                map[r.GetInt32(0)] = (r.GetDouble(1), r.GetDouble(2), r.GetDouble(3));
            return _positions = map;
        }
        finally { PositionsGate.Release(); }
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
    /// <summary>The light-year range, or null when it is not set (or 0).</summary>
    private static double? ReadLy(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("ly", out var p)) return null;
        double v;
        if (p.ValueKind == JsonValueKind.Number) v = p.GetDouble();
        else if (p.ValueKind == JsonValueKind.String
                 && double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) v = s;
        else return null;
        return v > 0 ? Math.Min(v, MaxLy) : null;
    }

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
