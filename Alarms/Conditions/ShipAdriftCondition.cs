using System.Globalization;
using System.Text;
using System.Text.Json;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Alarms.Conditions;

/// <summary>
/// The wake-up call. Fires, in up to three stages, when one of the capsuleer's characters in a
/// named hull or ship class undocks and is still sitting in that same system after a number of
/// seconds — a jump freighter left on a tether while its pilot dozes off at the keyboard, which
/// has cost a hundred billion ISK in one night. Nothing says whether a ship is moving; what can
/// be said is that it undocked, has not docked, and has not left the system, and that is enough.
///
/// <para>An <em>episode</em> is one undock: it begins at the instant the location poll saw the
/// character go from docked to in space (<c>UndockedAt</c>) and ends when they dock, change
/// system or log off. Each stage fires once per episode, keyed on the episode and the stage, so
/// the ordinary seen-key ledger gives "once per stage" for free. When several stages are due at
/// once — the app was away — only the highest is raised.</para>
///
/// <para>An acknowledgement — the dialog's button, or any reply at all to the agent — quiets the
/// episode for a number of minutes (they may be waiting on a bridge). When that lapses and the
/// ship is still there, the first stage fires again at once and the later ones follow at their
/// usual distances after it. The snooze belongs to the episode: a pilot who docks and undocks
/// again is awake, and the next undock gets its own stages.</para>
///
/// <para>Repeat and cooldown do not apply to a staged alarm; the stages are its cadence.</para>
/// </summary>
public sealed class ShipAdriftCondition : IAlarmCondition
{
    public const int StageCount = 3;

    private const int DefaultStage1 = 180;
    private const int DefaultStage2 = 240;
    private const int DefaultStage3 = 300;
    private const int DefaultSnooze = 30;

    private const int CapsuleGroupId = 29;

    public string TypeKey     => "undocked_too_long";
    public string DisplayName => "Undocked too long (wake-up call)";

    public string Description =>
        "Wakes you up. Fires in up to three stages when one of your characters in a named hull or " +
        "ship class undocks and is still sitting in that same system after a number of seconds — a " +
        "freighter or jump freighter on a tether while its pilot dozes off. Docking, leaving the " +
        "system or logging off ends it. Pressing the dialog's button, or replying anything at all " +
        "to the agent, quiets it for a while; when that lapses and the ship is still there, the " +
        "stages start over. Each action can be tied to a stage, and a Sound action can repeat " +
        "until acknowledged. Repeat and cooldown do not apply.";

    public int Stages => StageCount;

    public object ParameterSchema => new
    {
        type = "object",
        properties = new
        {
            ships = new
            {
                type        = "array",
                items       = new { type = "string" },
                format      = "ship-name",
                title       = "Flying",
                description = "Hull names (\"Rhea\") or ship classes (\"Freighter\", \"Jump Freighter\", " +
                              "\"Hauler\"), any mix. Only these are watched.",
            },
            stage1_seconds = new
            {
                type        = "integer",
                optional    = true,
                @default    = DefaultStage1,
                title       = "Stage 1 after",
                suffix      = "seconds undocked",
                description = "The first, gentler stage. Untick to skip it.",
            },
            stage2_seconds = new
            {
                type        = "integer",
                optional    = true,
                @default    = DefaultStage2,
                title       = "Stage 2 after",
                suffix      = "seconds undocked",
                description = "Untick to skip it.",
            },
            stage3_seconds = new
            {
                type        = "integer",
                optional    = true,
                @default    = DefaultStage3,
                title       = "Stage 3 after",
                suffix      = "seconds undocked",
                description = "The last resort — pair it with a Sound action set to repeat until " +
                              "acknowledged. Untick to skip it.",
            },
            snooze_minutes = new
            {
                type        = "integer",
                @default    = DefaultSnooze,
                title       = "Quiet after an acknowledgement",
                suffix      = "minutes",
                description = "How long a reply or the dialog's button keeps this quiet while the ship " +
                              "stays undocked — long enough to wait on a titan bridge. Then the stages " +
                              "start over.",
            },
        },
        required = new[] { "ships" },
    };

    public string Describe(JsonElement config)
    {
        var ships  = ReadList(config, "ships");
        var stages = StageSeconds(config);
        var sb     = new StringBuilder();
        sb.Append(ships.Count == 0 ? "No ship classes set" : Few(ships)).Append(" undocked too long");
        if (stages.Count > 0)
            sb.Append(" — ").Append(string.Join(", ", stages.Select(s => $"{s.Seconds}s"))).Append(" after undock");
        sb.Append($"; quiet {Snooze(config)} min after a reply");
        return sb.ToString();

        static string Few(List<string> items) =>
            items.Count <= 3 ? string.Join(", ", items) : $"{string.Join(", ", items.Take(3))} +{items.Count - 3}";
    }

    public (string Title, string Body) DefaultText(
        string alarmName, JsonElement config, IReadOnlyList<AlarmMatch> matches)
    {
        if (matches.Count == 1 && matches[0].Detail is { } d && d.ContainsKey("character"))
        {
            var minutes = Minutes(d);
            return ($"Wake up, {Str(d, "character")}",
                    $"Your {HullWord(d)} has been undocked in {Str(d, "system")} for {minutes} " +
                    $"{(minutes == 1 ? "minute" : "minutes")} and has not docked. " +
                    $"Press I'm awake to keep this quiet for {Snooze(config)} minutes while the ship stays out.");
        }
        return (alarmName, IAlarmCondition.JoinSummaries(matches));
    }

    /// <summary>
    /// The whole prompt for the agent: what to say for this stage, word for word, and that the
    /// reply is the acknowledgement. Asking "is everything all right" needs a reply, which is
    /// the one thing the generic prompt forbids.
    /// </summary>
    public string? AgentPrompt(JsonElement config, IReadOnlyList<AlarmMatch> matches)
    {
        if (matches.Count == 0 || matches[0].Detail is not { } d) return null;

        var stage = StageOf(matches[0]);
        var line  = StageLine(stage, d);
        return
            $"""
             ALARM FIRED — a wake-up call, not a request to look anything up.

             {Str(d, "character")}'s {HullWord(d)} undocked in {Str(d, "system")} {Minutes(d)} minutes ago
             and is still there, not docked. Stage {stage} of {StageCount}.

             Say exactly this, word for word, and nothing before it: {line}
             Then stop and wait. The capsuleer may be asleep at the keyboard. Any reply from them,
             whatever it says, resets this alarm by itself — you need not do anything for that. If
             they say all is well, acknowledge in a few words. Do not call any tools.
             """;
    }

    internal static string StageLine(int stage, IReadOnlyDictionary<string, object?> d)
    {
        var name    = Str(d, "character");
        var hull    = HullWord(d);
        var system  = Str(d, "system");
        var minutes = Minutes(d);
        var span    = minutes == 1 ? "1 minute" : $"{minutes} minutes";
        return stage switch
        {
            1 => $"{name}, your {hull} has been undocked in {system} for {span}. Is everything all right?",
            2 => $"Wake up, {name}. Your {hull} undocked in {system} {span} ago and still is not docked. Dock up, or answer me.",
            _ => $"{name}! Your {hull} is still undocked in {system} after {span}. Wake up and dock now.",
        };
    }

    public async Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default)
    {
        var wantShips = ReadList(config, "ships").Select(Norm).ToHashSet();
        if (wantShips.Count == 0) return [];

        var stages = StageSeconds(config);
        if (stages.Count == 0) return [];
        var snoozeMinutes = Snooze(config);
        var now           = ctx.Now;

        await using var db = await ctx.DbFactory.CreateDbContextAsync(ct);

        // Every character in space right now who has an undock on record. The same-system test
        // is done here rather than in SQL only because it is one row per character.
        var adrift = (await db.CharacterStatuses.AsNoTracking()
                .Where(s => s.Online && s.UndockedAt != null && s.ShipTypeId != null
                         && s.StationId == null && s.StructureId == null)
                .ToListAsync(ct))
            .Where(s => s.UndockedSystemId != null && s.SolarSystemId == s.UndockedSystemId)
            .ToList();
        if (adrift.Count == 0) return [];

        var typeIds = adrift.Select(s => s.ShipTypeId!.Value).Distinct().ToList();
        var hulls   = await (from t in db.SdeTypes.AsNoTracking()
                             join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                             where typeIds.Contains(t.TypeId)
                             select new { t.TypeId, t.Name, g.GroupId, Group = g.Name })
                            .ToDictionaryAsync(x => x.TypeId, ct);

        var charIds = adrift.Select(s => s.CharacterId).ToList();
        var names   = await db.Characters.AsNoTracking()
            .Where(c => charIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var sysIds  = adrift.Select(s => s.UndockedSystemId!.Value).Distinct().ToList();
        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => sysIds.Contains(s.SolarSystemId)).ToDictionaryAsync(s => s.SolarSystemId, s => s.Name, ct);
        var snoozes = await db.AlarmSnoozes.AsNoTracking()
            .Where(s => s.AlarmId == ctx.Alarm.Id).ToDictionaryAsync(s => s.ScopeKey, ct);

        var firstStage = stages[0].Seconds;
        var matches    = new List<AlarmMatch>();
        foreach (var s in adrift)
        {
            if (!hulls.TryGetValue(s.ShipTypeId!.Value, out var hull)) continue;
            var isPod = hull.GroupId == CapsuleGroupId;
            if (!wantShips.Contains(Norm(hull.Name)) && !wantShips.Contains(Norm(hull.Group))
                && !(isPod && wantShips.Contains("pod")))
                continue;

            var undockedAt = s.UndockedAt!.Value.ToUniversalTime();
            var episode    = EpisodeKey(undockedAt);
            var scopeKey   = s.CharacterId.ToString(CultureInfo.InvariantCulture);

            // A snooze counts only for the episode it was given in.
            var snooze = snoozes.TryGetValue(scopeKey, out var sn) && sn.Episode == episode ? sn : null;
            if (snooze is not null && now < snooze.Until) continue;

            // After a snooze lapses the first stage is due at once and the rest keep their
            // distances from it; the clock is set back so the arithmetic says the same thing.
            var clockStart = snooze is not null ? snooze.Until.AddSeconds(-firstStage) : undockedAt;
            var elapsed    = (now - clockStart).TotalSeconds;
            var due        = stages.Where(st => elapsed >= st.Seconds).Select(st => st.Stage).DefaultIfEmpty(0).Max();
            if (due == 0) continue;

            var generation = snooze is not null ? snooze.Until.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss") : "-";
            var minutes    = (int)Math.Round((now - undockedAt).TotalMinutes);
            var detail     = new Dictionary<string, object?>
            {
                ["character_id"]   = s.CharacterId,
                ["character"]      = names.GetValueOrDefault(s.CharacterId) ?? $"Character {s.CharacterId}",
                ["hull"]           = hull.Name,
                ["ship_class"]     = hull.Group,
                ["is_pod"]         = isPod,
                ["ship_name"]      = s.ShipName,
                ["system"]         = systems.GetValueOrDefault(s.UndockedSystemId!.Value) ?? "an unknown system",
                ["system_id"]      = s.UndockedSystemId,
                ["undocked_at"]    = undockedAt,
                ["minutes"]        = minutes,
                ["stage"]          = due,
                ["stage_seconds"]  = stages.First(st => st.Stage == due).Seconds,
                ["scope_key"]      = scopeKey,
                ["episode"]        = episode,
                ["snooze_minutes"] = snoozeMinutes,
            };

            matches.Add(new AlarmMatch(
                $"adrift:{scopeKey}|{episode}|{generation}|{due}",
                $"{detail["character"]} — {hull.Name} undocked in {detail["system"]} for {minutes} min, not docked (stage {due})")
            {
                Detail = detail,
            });
        }
        return matches;
    }

    /// <summary>
    /// Whether the episode a firing was about is still going on: the character is still online,
    /// in space, in the system they undocked in, on the same undock, and nobody has acknowledged
    /// it. What a repeating sound asks between plays.
    /// </summary>
    public async Task<bool> StillHoldsAsync(
        long alarmId, string scopeKey, string episode,
        IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct)
    {
        if (!long.TryParse(scopeKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var characterId)) return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.CharacterStatuses.AsNoTracking().FirstOrDefaultAsync(x => x.CharacterId == characterId, ct);
        if (s is null || !s.Online || s.IsDocked || s.UndockedAt is null
            || s.UndockedSystemId is null || s.SolarSystemId != s.UndockedSystemId
            || EpisodeKey(s.UndockedAt.Value) != episode)
            return false;

        var snooze = await db.AlarmSnoozes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AlarmId == alarmId && x.ScopeKey == scopeKey, ct);
        return snooze is null || snooze.Episode != episode || snooze.Until <= DateTimeOffset.UtcNow;
    }

    internal static string EpisodeKey(DateTimeOffset undockedAt)
        => undockedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss");

    internal static int StageOf(AlarmMatch m)
        => m.Detail is { } d && d.TryGetValue("stage", out var s) && s is int stage ? stage : 0;

    // ── Words ──────────────────────────────────────────────────────────────────

    private static int Minutes(IReadOnlyDictionary<string, object?> d)
        => d.TryGetValue("minutes", out var m) && m is int n ? n : 0;

    private static string HullWord(IReadOnlyDictionary<string, object?> d)
        => d.TryGetValue("is_pod", out var p) && p is true ? "pod" : Str(d, "hull");

    private static string Norm(string s) => s.Trim().ToLowerInvariant();

    private static string Str(IReadOnlyDictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) && v is string s ? s : "";

    // ── Config ─────────────────────────────────────────────────────────────────

    /// <summary>The stages that are switched on, in order, each with its seconds after undock.</summary>
    private static List<(int Stage, int Seconds)> StageSeconds(JsonElement config)
    {
        var list = new List<(int, int)>();
        for (var i = 1; i <= StageCount; i++)
            if (ReadInt(config, $"stage{i}_seconds") is { } seconds && seconds > 0)
                list.Add((i, seconds));
        return list;
    }

    private static int Snooze(JsonElement config)
        => ReadInt(config, "snooze_minutes") is { } m && m > 0 ? m : DefaultSnooze;

    private static List<string> ReadList(JsonElement config, string name)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(name, out var p)) return [];
        IEnumerable<string> list = p.ValueKind switch
        {
            JsonValueKind.Array  => p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? ""),
            JsonValueKind.String => (p.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
            _                    => [],
        };
        return list.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    private static int? ReadInt(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)) return v;
        if (p.ValueKind == JsonValueKind.String
            && int.TryParse(p.GetString()?.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            return s;
        return null;
    }
}
