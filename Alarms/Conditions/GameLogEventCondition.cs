using System.Globalization;
using System.Text;
using System.Text.Json;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Alarms.Conditions;

/// <summary>
/// Fires on something the game log says happened to one of the capsuleer's characters: the
/// cloak dropped near a gate or structure, a warp scramble or disruption landed on them, they
/// are taking damage. Read from the imported game log, so it hears about it as soon as the tail
/// does — seconds — and only for clients whose log folder is being read.
///
/// <para>A decloak is one event. A scramble or an attack is a run of lines — a fight writes a
/// line per hit — so those are grouped: lines of one kind for one character with no more than
/// <see cref="FightGap"/> between them are one episode, keyed by its first line, and fire once.
/// A fight that pauses longer than that and starts again is a new one.</para>
///
/// <para>⚠️ The rules that parse the log are English: a client running in another language writes
/// lines none of them match, and this check hears nothing from it.</para>
/// </summary>
public sealed class GameLogEventCondition : IAlarmCondition
{
    /// <summary>
    /// How far back an event counts. Wider than the alarm's poll so nothing falls between two
    /// checks; short, because a decloak five minutes ago is history, not an alarm.
    /// </summary>
    private const int MinLookbackSeconds = 180;

    /// <summary>The quiet that ends a fight. Split this way, the 3,095 player hits in every log
    /// read were 26 fights.</summary>
    internal static readonly TimeSpan FightGap = TimeSpan.FromMinutes(2);

    internal const string Decloaked = "decloaked";
    internal const string Scrambled = "scrambled";
    internal const string Attacked  = "attacked";

    /// <summary>Seconds count here: a scramble is a fight already started.</summary>
    public int? DefaultPollSeconds => 5;

    public string TypeKey     => "game_log_event";
    public string DisplayName => "Game log event";

    public string Description =>
        "Fires when the game log of one of your characters says it was decloaked by something " +
        "nearby, warp scrambled or disrupted, or is under attack — each ticked separately, any mix. " +
        "A scramble or an attack fires once per fight: lines with no more than two minutes between " +
        "them are one fight. NPCs can be left out. Needs that client's game log folder being read; " +
        "seen within seconds of the line being written — poll every 5 seconds.";

    public object ParameterSchema => new
    {
        type = "object",
        properties = new
        {
            decloaked = new
            {
                type        = "boolean",
                @default    = true,
                title       = "Decloaked",
                description = "\"Your cloak deactivates due to proximity to a nearby …\" — a gate, a " +
                              "structure, anything that drops a cloak.",
            },
            scrambled = new
            {
                type        = "boolean",
                @default    = true,
                title       = "Warp scrambled",
                description = "A warp scramble or warp disruption attempt on you.",
            },
            attacked = new
            {
                type        = "boolean",
                @default    = true,
                title       = "Under attack",
                description = "Taking damage.",
            },
            players_only = new
            {
                type        = "boolean",
                @default    = true,
                title       = "Players only",
                description = "Ticked: a scramble or an attack counts only from a player, not an NPC — " +
                              "ratting would otherwise set it off all evening.",
            },
            characters = new
            {
                type        = "array",
                items       = new { type = "string" },
                format      = "character-name",
                title       = "Characters",
                description = "Optional. Your own characters to watch; leave empty for all of them.",
            },
        },
    };

    // The editor's words; the three above are the agent's and stay English.
    public string ScreenName        => AlarmsText.CheckGameLogEvent;
    public string ScreenDescription => AlarmsText.CheckGameLogEventNote;

    public AlarmFieldText? ScreenField(string property) => property switch
    {
        "decloaked"    => new(AlarmsText.GameLogDecloakedLabel,   AlarmsText.GameLogDecloakedNote),
        "scrambled"    => new(AlarmsText.GameLogScrambledLabel,   AlarmsText.GameLogScrambledNote),
        "attacked"     => new(AlarmsText.GameLogAttackedLabel,    AlarmsText.GameLogAttackedNote),
        "players_only" => new(AlarmsText.GameLogPlayersOnlyLabel, AlarmsText.GameLogPlayersOnlyNote),
        "characters"   => new(AlarmsText.IntelCharactersLabel,    AlarmsText.GameLogCharactersNote),
        _              => null,
    };

    public string Describe(JsonElement config)
    {
        var events = Wanted(config).Select(e => e switch
        {
            Decloaked => "decloaked",
            Scrambled => "warp scrambled",
            _         => "under attack",
        }).ToList();
        var chars = ReadList(config, "characters");

        var sb = new StringBuilder(events.Count == 0 ? "No events ticked" : Cap(string.Join(", ", events)));
        sb.Append(chars.Count == 0 ? ", any character" : $", {Few(chars)}");
        if (ReadBool(config, "players_only", true) && (events.Contains("warp scrambled") || events.Contains("under attack")))
            sb.Append("; players only");
        return sb.ToString();

        static string Few(List<string> items) =>
            items.Count <= 3 ? string.Join(", ", items) : $"{string.Join(", ", items.Take(3))} +{items.Count - 3}";
        static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    public (string Title, string Body) DefaultText(
        string alarmName, JsonElement config, IReadOnlyList<AlarmMatch> matches)
    {
        var title = matches.Count == 1 && matches[0].Detail is { } d
            ? Said(d).TrimEnd('.')
            : string.Format(AlarmsText.GameLogEventsTitle, matches.Count);
        return (title, IAlarmCondition.JoinSummaries(matches));
    }

    /// <summary>Said as written: who, and what happened to them — seconds count.</summary>
    public string? Announcement(JsonElement config, IReadOnlyList<AlarmMatch> matches)
    {
        if (matches.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var m in matches.Take(5))
        {
            if (m.Detail is not { } d) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Said(d));
        }
        if (matches.Count > 5) sb.Append(' ').Append(string.Format(AlarmsText.SaidAndMore, matches.Count - 5));
        return sb.ToString();
    }

    /// <summary>One event in the interface language. The names in it are the game's own, as logged.</summary>
    private static string Said(IReadOnlyDictionary<string, object?> d) => Str(d, "event") switch
    {
        Decloaked => string.Format(AlarmsText.GameLogSaidDecloaked, Str(d, "character"), Str(d, "near")),
        Scrambled => string.Format(AlarmsText.GameLogSaidScrambled, Str(d, "character"), Str(d, "by")),
        _         => string.Format(AlarmsText.GameLogSaidAttacked,  Str(d, "character"), Str(d, "by")),
    };

    public async Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default)
    {
        var wanted = Wanted(config);
        if (wanted.Count == 0) return [];
        var playersOnly = ReadBool(config, "players_only", true);

        var lookback = TimeSpan.FromSeconds(Math.Max(MinLookbackSeconds, 2 * ctx.Alarm.PollSeconds));
        var since    = GameLogRules.FormatTimestamp(ctx.Now - lookback);

        await using var db = await ctx.DbFactory.CreateDbContextAsync(ct);

        // Own characters only, by name when the alarm names some. Names are matched as the
        // editor stored them, ignoring case.
        var own   = await db.Characters.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var named = ReadList(config, "characters").Select(n => n.ToLowerInvariant()).ToHashSet();
        var ids   = own.Where(c => named.Count == 0 || named.Contains((c.Name ?? "").ToLowerInvariant()))
                       .Select(c => c.Id).ToList();
        if (ids.Count == 0) return [];
        var names = own.ToDictionary(c => c.Id, c => c.Name);

        var rows = new List<(string Event, GameLogEvent Row)>();
        foreach (var ev in wanted)
            foreach (var r in await Of(db, ev, playersOnly)
                         .Where(e => e.CharacterId != null && ids.Contains(e.CharacterId.Value)
                                  && string.Compare(e.OccurredAt, since) >= 0)
                         .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
                         .ToListAsync(ct))
                rows.Add((ev, r));
        if (rows.Count == 0) return [];

        // Where each character is now, for the written summary.
        var charIds = rows.Select(r => r.Row.CharacterId!.Value).Distinct().ToList();
        var where   = await (from s in db.CharacterStatuses.AsNoTracking()
                             join sys in db.SdeSolarSystems.AsNoTracking() on s.SolarSystemId equals sys.SolarSystemId
                             where charIds.Contains(s.CharacterId)
                             select new { s.CharacterId, sys.Name })
                            .ToDictionaryAsync(x => x.CharacterId, x => x.Name, ct);

        var matches = new List<AlarmMatch>();
        foreach (var group in rows.GroupBy(r => (r.Event, Char: r.Row.CharacterId!.Value)))
        {
            var (ev, charId) = group.Key;
            var character    = names.GetValueOrDefault(charId) ?? $"Character {charId}";
            var system       = where.GetValueOrDefault(charId);

            if (ev == Decloaked)
            {
                foreach (var (_, r) in group)
                    matches.Add(Make(ev, charId, character, system, r.OccurredAt, [r], r.LocationName ?? ""));
                continue;
            }

            // Fights: split on quiet, and find where the first one in the window really began.
            var fights = SplitFights(group.Select(g => g.Row).ToList());
            fights[0] = [.. await EarlierOfSameFightAsync(db, ev, playersOnly, charId, fights[0][0], ct), .. fights[0]];
            foreach (var fight in fights)
                matches.Add(Make(ev, charId, character, system, fight[0].OccurredAt, fight, ""));
        }
        return matches;
    }

    /// <summary>The rows of one event type.</summary>
    private static IQueryable<GameLogEvent> Of(AppDbContext db, string ev, bool playersOnly)
    {
        var q = db.GameLogEvents.AsNoTracking();
        return ev switch
        {
            Decloaked => q.Where(e => e.Kind == GameLogRules.KindDecloaked),
            // "… to you!": aimed at this character. A scramble written between two others — a
            // fleet member's, seen on the overview — is not.
            Scrambled => q.Where(e => e.Kind == GameLogRules.KindEwar
                                   && e.Quality != null && e.Quality.StartsWith("Warp")
                                   && (e.TargetName == "you!" || e.TargetName == "you" || e.TargetName == "You!" || e.TargetName == "You")
                                   && (!playersOnly || e.SourceCorp != null)),
            _         => q.Where(e => e.Kind == GameLogRules.KindDamageTaken
                                   && (!playersOnly || e.SourceCorp != null)),
        };
    }

    internal static List<List<GameLogEvent>> SplitFights(List<GameLogEvent> ordered)
    {
        var fights = new List<List<GameLogEvent>>();
        DateTimeOffset? last = null;
        foreach (var r in ordered)
        {
            var at = Parse(r.OccurredAt);
            if (last is null || at - last > FightGap) fights.Add([]);
            fights[^1].Add(r);
            last = at;
        }
        return fights;
    }

    /// <summary>
    /// The lines of the same fight from before the window, oldest first. The window cuts a long
    /// fight anywhere, and a fight keyed by the first line the window happened to hold would get
    /// a new key — and fire again — on every pass while it lasted.
    /// </summary>
    private static async Task<List<GameLogEvent>> EarlierOfSameFightAsync(
        AppDbContext db, string ev, bool playersOnly, long charId, GameLogEvent first, CancellationToken ct)
    {
        // Walk back to the start a fight-gap at a time — the earliest line within one gap of the
        // head becomes the head — then read everything from there in one go.
        var head = first;
        while (true)
        {
            var from  = GameLogRules.FormatTimestamp(Parse(head.OccurredAt) - FightGap);
            var until = head.OccurredAt;
            var prior = await Before(Of(db, ev, playersOnly), charId, head)
                .Where(e => string.Compare(e.OccurredAt, from) >= 0)
                .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
                .FirstOrDefaultAsync(ct);
            if (prior is null) break;
            head = prior;
        }
        if (head.Id == first.Id) return [];

        var start = head.OccurredAt;
        return await Before(Of(db, ev, playersOnly), charId, first)
            .Where(e => string.Compare(e.OccurredAt, start) >= 0)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .ToListAsync(ct);
    }

    /// <summary>A character's lines strictly before <paramref name="row"/>: earlier, or the same second and written first.</summary>
    private static IQueryable<GameLogEvent> Before(IQueryable<GameLogEvent> q, long charId, GameLogEvent row)
    {
        var at = row.OccurredAt;
        var id = row.Id;
        return q.Where(e => e.CharacterId == charId
                         && (string.Compare(e.OccurredAt, at) < 0 || (e.OccurredAt == at && e.Id < id)));
    }

    private static AlarmMatch Make(string ev, long charId, string character, string? system,
                                   string startedAt, IReadOnlyList<GameLogEvent> rows, string near)
    {
        // Who did it: players by name and ship, NPCs by name; the busiest first.
        var by = rows.Where(r => !string.IsNullOrEmpty(r.SourceName))
                     .GroupBy(r => r.SourceName!)
                     .OrderByDescending(g => g.Count())
                     .Select(g => g.First().SourceShip is { Length: > 0 } ship ? $"{g.Key} ({ship})" : g.Key)
                     .ToList();
        var byText = by.Count <= 3 ? string.Join(", ", by) : $"{string.Join(", ", by.Take(3))} +{by.Count - 3}";
        var damage = ev == Attacked ? rows.Sum(r => r.Amount ?? 0) : 0;

        var detail = new Dictionary<string, object?>
        {
            ["event"]        = ev,
            ["character_id"] = charId,
            ["character"]    = character,
            ["system"]       = system,
            ["near"]         = ev == Decloaked ? near : null,
            ["by"]           = ev == Decloaked ? null : byText,
            ["lines"]        = rows.Count,
            ["damage"]       = ev == Attacked ? damage : null,
            ["started_at"]   = startedAt,
            ["last_at"]      = rows[^1].OccurredAt,
        };

        var at = system is null ? "" : $" in {system}";
        var summary = ev switch
        {
            Decloaked => $"{character} decloaked near {near}{at}",
            Scrambled => $"{character} warp scrambled by {byText}{at}",
            _         => $"{character} under attack by {byText}{at} — {damage:N0} damage in {rows.Count} hits",
        };
        return new AlarmMatch($"{ev}:{charId.ToString(CultureInfo.InvariantCulture)}|{startedAt}", summary) { Detail = detail };
    }

    // ── Config ─────────────────────────────────────────────────────────────────

    private static List<string> Wanted(JsonElement config)
    {
        var list = new List<string>();
        if (ReadBool(config, "decloaked", true)) list.Add(Decloaked);
        if (ReadBool(config, "scrambled", true)) list.Add(Scrambled);
        if (ReadBool(config, "attacked",  true)) list.Add(Attacked);
        return list;
    }

    private static DateTimeOffset Parse(string occurredAt) =>
        DateTimeOffset.Parse(occurredAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string Str(IReadOnlyDictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) && v is string s ? s : "";

    /// <summary>A missing box reads as its default: an alarm the agent wrote may leave some out.</summary>
    private static bool ReadBool(JsonElement e, string name, bool missing) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.True
            : missing;

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
}
