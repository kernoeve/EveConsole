using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Intel;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Alarms.Conditions;

/// <summary>
/// Fires when a hostile is reported within a jump range of what is being watched: the capsuleer's
/// characters while they are undocked (the default), the named characters wherever they are, or a
/// list of systems. One range covers all of them; 0 is the system itself.
///
/// <para><b>Current state, not raw reports.</b> The check reads the shared intel snapshot
/// (<see cref="IntelSnapshotService"/>), read again before every evaluation, so a stale snapshot is
/// never judged. Whatever the snapshot no longer holds is gone from the alarm too: a pilot seen since
/// somewhere else, a pilot called clear, a report past its lifetime, a count said to have moved on.</para>
///
/// <para><b>Keyed by episode</b>, so each thing is news once while it lasts:</para>
/// <list type="bullet">
/// <item>a named pilot once per stay in a system (system + when the stay began, from the last-seen
/// table's InSystemSince): called again by three people, or seen again on a kill there, it is not news;
/// seen in another system, it is;</item>
/// <item>a count of unnamed pilots once per size band (1, 2–4, 5–9, 10–19, 20+) in its episode: three
/// rising to six is news, six to seven is not;</item>
/// <item>a flag (a camp, bubbles, a cyno, a hot drop, a spike, combat probes) once per episode, even
/// when the report counted nobody;</item>
/// <item>a kill once, said as a kill. Its attackers are banked with it and not said again as a
/// sighting.</item>
/// </list>
/// </summary>
public sealed class IntelCondition : IAlarmCondition
{
    /// <summary>
    /// Range ceiling. Beyond this the watched set stops being "around me" and becomes most of
    /// the region.
    /// </summary>
    private const int MaxJumps = 15;

    /// <summary>What a new alarm starts on. Around a pilot, their own system alone is too
    /// little warning; the systems a list names are watched as they are unless asked.</summary>
    private const int DefaultJumps = 5;

    /// <summary>The farthest light-year range: past a jump freighter's reach with every skill.</summary>
    private const double MaxLy = 20;

    private const double MetresPerLightYear = 9.4607304725808e15;

    /// <summary>A kill this many jumps or fewer from what is watched is urgent: said first.</summary>
    internal const int UrgentKillJumps = 1;

    /// <summary>How long one evaluation keeps the snapshot reading on its own: longer than the poll,
    /// so it stays warm between evaluations and goes idle soon after the last intel alarm stops.</summary>
    private static readonly TimeSpan KeepWarm = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The flags that fire with nobody counted: each changes how to go in, or whether to. The others
    /// (a wormhole, someone at the ESS or a skyhook) are said with a sighting, never alone.
    /// </summary>
    internal static readonly IntelRules.IntelFlags[] FiringFlags =
    [
        IntelRules.IntelFlags.Camp, IntelRules.IntelFlags.Bubbles, IntelRules.IntelFlags.Cyno,
        IntelRules.IntelFlags.Hotdrop, IntelRules.IntelFlags.Spike, IntelRules.IntelFlags.Probes,
    ];

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

    private readonly SystemGraph           _graph;
    private readonly IntelSnapshotService? _snapshots;
    private readonly IntelDistanceService? _distances;

    /// <param name="snapshots">The shared intel snapshot this check matches against; without it the
    /// check cannot evaluate, and says so.</param>
    /// <param name="distances">Threat distances (stargates plus Thera and Turnur's open holes); without
    /// them the range is walked over stargates alone.</param>
    public IntelCondition(SystemGraph graph, IntelSnapshotService? snapshots = null, IntelDistanceService? distances = null)
    {
        _graph     = graph;
        _snapshots = snapshots;
        _distances = distances;
    }

    public string TypeKey     => "intel";
    public string DisplayName => "Intel report";

    public string Description =>
        "Fires when someone reports a hostile pilot within a jump range — or a light-year range, for what " +
        "is in jump-drive reach — of what you are watching: your characters while they are " +
        "undocked (the default), named characters wherever they are, or a list of systems. Either " +
        "range catching a report is enough. A jump range of 0 watches just those systems. Reads the intel " +
        "channels already being parsed under Settings → Chat Logs, as they stand now: a pilot seen since " +
        "elsewhere, or called clear, is gone. Each pilot is said once while they stay in a system, a count " +
        "of unnamed pilots again when it grows past 1, 4, 9 or 19, and a report of only a gate camp, bubbles, " +
        "a cyno, a hot drop, a spike or combat probes fires too (flag_reports). A kill there with hostile " +
        "attackers always fires, said as a kill, and first when it is within a jump; its attackers are not " +
        "said again as a sighting.";

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
                description = "How many jumps out from each character or system to watch, through " +
                              "stargates and Thera's and Turnur's open wormholes. 0 means their own system " +
                              "only. Capped at 15 — past that it stops being a neighbourhood.",
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
                description = "Only fire when at least this many hostiles are in the system. Default 1. " +
                              "Kills and flag-only reports fire whatever the number.",
            },
            ignore_no_visual = new
            {
                type        = "boolean",
                description = "Skip pilots and counts reported NV (no visual) — someone relaying a contact " +
                              "they cannot actually see.",
            },
            flag_reports = new
            {
                type        = "boolean",
                @default    = true,
                title       = "Flag-only reports",
                description = "Also fire for a report that counts nobody but says a gate camp, bubbles, a " +
                              "cyno, a hot drop, a spike or combat probes. Each is said once while it lasts. " +
                              "Default true.",
            },
        },
        required = Array.Empty<string>(),
    };

    // The editor's words; the three above are the agent's and stay English.
    public string ScreenName        => AlarmsText.CheckIntel;
    public string ScreenDescription => Sentences.Join(
        Sentences.Join(AlarmsText.CheckIntelNote, AlarmsText.CheckIntelKillsNote), AlarmsText.CheckIntelChangedNote);

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
        "flag_reports"     => new(AlarmsText.IntelFlagReportsLabel, AlarmsText.IntelFlagReportsNote),
        _                  => null,
    };

    public string? ScreenOption(string property, string value) => value switch
    {
        AroundUndocked   => AlarmsText.OptionUndockedCharacters,
        AroundCharacters => AlarmsText.OptionCharacters,
        AroundSystems    => AlarmsText.OptionSystems,
        _                => null,
    };

    /// <summary>A new intel alarm looks every 15 seconds: often enough that a lifetime ending or a
    /// character moving is noticed without anything nudging it.</summary>
    public int? DefaultPollSeconds => 15;

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

        var text = minimum > 1 ? $"Intel {where}, {minimum}+ pilots" : $"Intel {where}";
        return ReadBool(config, "flag_reports", true) ? text : text + ", no flag-only reports";
    }

    public (string Title, string Body) DefaultText(
        string alarmName, JsonElement config, IReadOnlyList<AlarmMatch> matches)
    {
        matches = Ordered(matches);

        // Where matters more than how many, so the systems go in the title — that is what is
        // readable at a glance on a dialog that has just appeared over the game.
        var systems = matches
            .Select(m => Str(m.Detail, "system"))
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();
        var where = systems.Count switch
        {
            0 => "",
            _ => " — " + string.Join(", ", systems.Take(3)) + (systems.Count > 3 ? ", …" : ""),
        };

        // A kill is said as one; sightings by the systems they are in.
        var kills    = matches.Count(m => KindOf(m) == KindKill);
        var sighted  = matches.Where(m => KindOf(m) != KindKill).Select(m => Str(m.Detail, "system")).Distinct().Count();
        var headline = sighted == 0
            ? (kills == 1 ? AlarmsText.IntelKillReported : AlarmsText.IntelKillsReported)
            : sighted == 1
            ? AlarmsText.IntelHostileReported
            : Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelHostileReportsOther), sighted);

        return ($"{headline}{where}", IAlarmCondition.JoinSummaries(matches));
    }

    public async Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default)
    {
        var snapshots = _snapshots
            ?? throw new InvalidOperationException("The intel check has no intel snapshot to read.");

        // Kept reading between evaluations here, on the worker, while an intel alarm is on; idle
        // again soon after the last one stops.
        snapshots.KeepWarmFor(KeepWarm);

        // ⚠️ Read now, never whatever was read last: an evaluation nudged by intel just written must
        // see it. One read per pass: a snapshot begun after this pass began already holds everything
        // written before it, so the other intel alarms in the same pass take that one.
        var snapshot = snapshots.Current.At >= ctx.Now ? snapshots.Current : await snapshots.RefreshNowAsync(ct);

        var zone = await WatchedSystemsAsync(config, _graph, ctx.DbFactory, _distances, OwnOf(snapshot), ct);

        // No resolvable system means nothing to watch. Returning empty rather than everything
        // matters: a typo in a system name must go quiet, not alert on the whole cluster — and
        // every character docked must go quiet too, which is the point of watching undocked ones.
        if (zone.Systems.Count == 0) return [];

        return Match(snapshot, zone, Options(config), ctx.Now);
    }

    /// <summary>Where the user's characters are, as the snapshot has it (the newer of ESI and the chat
    /// log); null before the first read, when the characters' rows are asked instead.</summary>
    private static IReadOnlyList<OwnPosition>? OwnOf(IntelSnapshot snapshot) =>
        snapshot.At == DateTimeOffset.MinValue ? null : snapshot.Own;

    // ── Matching ─────────────────────────────────────────────────────────────

    /// <summary>What one alarm's config asks for, read once.</summary>
    internal sealed record IntelAlarmOptions(int Jumps, int MinPlayers = 1, bool SkipNoVisual = false, bool FlagReports = true);

    internal static IntelAlarmOptions Options(JsonElement config)
    {
        var around = ReadAround(config);
        return new IntelAlarmOptions(
            ReadJumps(config, around),
            Math.Max(1, ReadInt(config, "min_players") ?? 1),
            ReadBool(config, "ignore_no_visual"),
            ReadBool(config, "flag_reports", true));
    }

    // What a match is about, in its detail.
    internal const string KindPilot = "pilot";
    internal const string KindCount = "count";
    internal const string KindFlag  = "flag";
    internal const string KindKill  = "kill";

    /// <summary>
    /// Everything in the watched systems now, as matches keyed by episode (see the class summary).
    /// Pure: tools/IntelCheck runs it on snapshots built by hand.
    ///
    /// <para>Hostile is "neutral or worse" as the character the system is nearest sees it (the
    /// standings rule every intel view uses); the user's own characters never are. Urgent matches
    /// come first, then the nearest, then the newest.</para>
    /// </summary>
    internal static IReadOnlyList<AlarmMatch> Match(
        IntelSnapshot snapshot, IntelWatchedSystems zone, IntelAlarmOptions options, DateTimeOffset now)
    {
        var standings = snapshot.Standings;
        long? ViewerOf(int systemId) =>
            zone.NearestCharacter.TryGetValue(systemId, out var id) ? id
            : zone.LightYearsNearestCharacter.TryGetValue(systemId, out var lyId) ? lyId : null;
        bool Hostile(int systemId, long pilot, long corp, long alliance) =>
            StandingTiers.IsHostile(standings.TierFor(ViewerOf(systemId), pilot, corp, alliance));
        string English(int systemId) =>
            snapshot.SystemNames.TryGetValue(systemId, out var name) ? name : systemId.ToString(CultureInfo.InvariantCulture);

        var matches = new List<AlarmMatch>();

        // ── Kills: always news, said as a kill ───────────────────────────────
        // Who they placed is said with them: an attacker is not said again as a sighting.
        var onKills = new HashSet<(long Pilot, int System)>();
        foreach (var e in snapshot.Feed)
        {
            if (e.Kind != IntelEventKind.Kill || e.KillMailId is not int killId) continue;
            if (!zone.Systems.Contains(e.SystemId) || e.ExpiresAt <= now) continue;

            // A kill by blues alone is no threat; anyone else among the attackers is.
            var gang = e.Pilots.Where(p => p.CharacterId > 0 && Hostile(e.SystemId, p.CharacterId, p.CorporationId, p.AllianceId))
                .ToList();
            if (gang.Count == 0) continue;
            foreach (var p in gang) onKills.Add((p.CharacterId, e.SystemId));

            var hulls = gang.Where(p => p.ShipTypeId is > 0 && !string.IsNullOrWhiteSpace(p.Ship))
                .GroupBy(p => p.ShipTypeId!.Value)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => g.First().Ship!).Take(3).ToList();
            var names  = gang.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct().ToList();
            var victim = string.IsNullOrWhiteSpace(e.VictimShip) ? null : e.VictimShip;
            var (near, jumpsOut, lyOut, from) = HowFar(e.SystemId, options.Jumps, zone);
            var urgent = zone.Jumps.TryGetValue(e.SystemId, out var j) && j <= UrgentKillJumps;

            matches.Add(new AlarmMatch(
                "intel:k:" + killId.ToString(CultureInfo.InvariantCulture),
                $"Kill in {English(e.SystemId)}{from}: {victim ?? "a ship"} lost to " +
                (hulls.Count > 0 ? string.Join(", ", hulls) : gang.Count == 1 ? "1 pilot" : $"{gang.Count} pilots"))
            {
                Urgent = urgent,
                Detail = new Dictionary<string, object?>
                {
                    ["kind"]          = KindKill,
                    ["killmail_id"]   = killId,
                    ["system"]        = snapshot.SystemName(e.SystemId),
                    ["system_id"]     = e.SystemId,
                    ["count"]         = gang.Count,
                    ["pilots"]        = names,
                    ["hulls"]         = hulls,
                    ["victim_hull"]   = victim,
                    ["victim_id"]     = e.VictimId,
                    ["gate"]          = e.NearGate is { } g ? snapshot.SystemName(g.DestinationSystemId) : null,
                    ["jumps"]         = jumpsOut,
                    ["ly"]            = lyOut,
                    ["near"]          = near,
                    ["urgent"]        = urgent,
                    ["at"]            = e.At.UtcDateTime,
                },
            });
        }

        // ── Each watched system as it stands ─────────────────────────────────
        foreach (var system in snapshot.Systems.Values)
        {
            var id = system.SystemId;
            if (!zone.Systems.Contains(id)) continue;

            var facts = system.Facts.Where(f => f.ExpiresAt > now).ToList();
            var flagBits = facts.Aggregate(0, (bits, f) => bits | (int)f.Flag);
            var (near, jumpsOut, lyOut, from) = HowFar(id, options.Jumps, zone);

            Dictionary<string, object?> Detail(string kind, int count, IReadOnlyList<string> pilots, IReadOnlyList<string> hulls,
                                               int flags, DateTimeOffset at) => new()
            {
                ["kind"]      = kind,
                ["system"]    = snapshot.SystemName(id),
                ["system_id"] = id,
                ["count"]     = count,
                ["pilots"]    = pilots,
                ["hulls"]     = hulls,
                ["flags"]     = IntelDisplay.FlagsEnglish(flags),
                ["flag_bits"] = flags,
                ["gate"]      = system.Gate,
                ["jumps"]     = jumpsOut,
                ["ly"]        = lyOut,
                ["near"]      = near,
                ["at"]        = at.UtcDateTime,
            };

            // Who is there: alive, not a loss (a loss takes a pilot off the map), hostile to the
            // character nearest, and seen rather than relayed when asked.
            var pilots = system.PilotIds
                .Select(p => snapshot.Pilots.GetValueOrDefault(p))
                .Where(p => p is not null && p.SystemId == id && p.ExpiresAt > now
                            && p.Source != IntelLastSeenSource.KillVictim
                            && !(options.SkipNoVisual && p.NoVisual)
                            && Hostile(id, p.CharacterId, p.CorporationId, p.AllianceId))
                .Select(p => p!)
                .ToList();
            var unnamed = system.Unnamed is { Count: > 0 } u && u.ExpiresAt > now && !(options.SkipNoVisual && u.NoVisual) ? u : null;

            if (pilots.Count + (unnamed?.Count ?? 0) >= options.MinPlayers)
            {
                foreach (var p in pilots)
                {
                    var name = string.IsNullOrWhiteSpace(p.Name) ? null : p.Name;
                    var detail = Detail(KindPilot, 1, name is null ? [] : [name],
                                        string.IsNullOrWhiteSpace(p.Ship) ? [] : [p.Ship!], flagBits, p.At);
                    detail["character_id"] = p.CharacterId;
                    detail["no_visual"]    = p.NoVisual;
                    if (p.FromKill) detail["note"] = AlarmsText.IntelSaidOnKill;
                    matches.Add(new AlarmMatch(
                        $"intel:p:{p.CharacterId.ToString(CultureInfo.InvariantCulture)}|{id.ToString(CultureInfo.InvariantCulture)}|{Stamp(p.InSystemSince)}",
                        $"{name ?? "1 pilot"} in {English(id)}{from}{(string.IsNullOrWhiteSpace(p.Ship) ? "" : $" ({p.Ship})")}")
                    {
                        // Said with the kill that placed them, never again on its own.
                        Silent = onKills.Contains((p.CharacterId, id)),
                        Detail = detail,
                    });
                }

                if (unnamed is not null)
                {
                    var detail = Detail(KindCount, unnamed.Count, [],
                                        [.. IntelDisplay.ParseShips(unnamed.Ships).Select(s => s.Name)], flagBits, unnamed.At);
                    detail["no_visual"] = unnamed.NoVisual;
                    matches.Add(new AlarmMatch(
                        $"intel:n:{id.ToString(CultureInfo.InvariantCulture)}|{Stamp(unnamed.EpisodeStart)}|{Band(unnamed.Count).ToString(CultureInfo.InvariantCulture)}",
                        $"{(unnamed.Count == 1 ? "1 pilot" : $"{unnamed.Count} pilots")} in {English(id)}{from}")
                    { Detail = detail });
                }
            }

            // What was said besides who, on its own: each once per episode, whoever is there.
            if (options.FlagReports)
                foreach (var fact in facts.Where(f => FiringFlags.Contains(f.Flag)))
                    matches.Add(new AlarmMatch(
                        $"intel:f:{id.ToString(CultureInfo.InvariantCulture)}|{fact.Flag}|{Stamp(fact.EpisodeStart)}",
                        $"{string.Join(", ", IntelDisplay.FlagsEnglish((int)fact.Flag))} in {English(id)}{from}")
                    { Detail = Detail(KindFlag, 0, [], [], flagBits, fact.At) });
        }

        return Ordered(matches);
    }

    /// <summary>The unnamed count's band: its lowest number, so the key changes only when the count
    /// crosses into another (1, 2–4, 5–9, 10–19, 20+).</summary>
    internal static int Band(int count) => count switch
    {
        <= 1  => 1,
        <= 4  => 2,
        <= 9  => 5,
        <= 19 => 10,
        _     => 20,
    };

    /// <summary>An episode's start in a key: to the second, in UTC, the same on every PC.</summary>
    private static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    /// <summary>Urgent first, then the nearest, then the newest: the order they are said in.</summary>
    internal static IReadOnlyList<AlarmMatch> Ordered(IReadOnlyList<AlarmMatch> matches) =>
        [.. matches
            .Select((m, i) => (m, i))
            .OrderByDescending(x => x.m.Urgent)
            .ThenBy(x => JumpsOf(x.m) ?? int.MaxValue)
            .ThenByDescending(x => AtOf(x.m))
            .ThenBy(x => x.i)
            .Select(x => x.m)];

    private static string KindOf(AlarmMatch m) => Str(m.Detail, "kind");
    private static int? JumpsOf(AlarmMatch m) => m.Detail is { } d && d.TryGetValue("jumps", out var j) && j is int hops ? hops : null;
    private static DateTime AtOf(AlarmMatch m) => m.Detail is { } d && d.TryGetValue("at", out var a) && a is DateTime at ? at : DateTime.MinValue;

    private static string Str(IReadOnlyDictionary<string, object?>? d, string key)
        => d is not null && d.TryGetValue(key, out var v) && v is string s ? s : "";

    // ── The watched systems ──────────────────────────────────────────────────

    /// <summary>
    /// The systems one intel alarm watches, and how far each is from what it is watched around.
    /// <para>⚠️ The one place the watched set is worked out: the alarm's own check matches against it,
    /// and the Universe Map's alert zone outline draws it, so the outline is exactly what would fire.</para>
    /// <para>Everything within range of each origin — the characters (or systems) the alarm is around
    /// — by threat distance (stargates, and Thera's and Turnur's open holes, when
    /// <paramref name="distances"/> is given), and by light years when that is set. The nearer figure
    /// wins when two origins reach the same system: two characters a jump apart, or a system listed
    /// twice. Empty when nothing resolves (a misspelt system, every character docked), never
    /// everything.</para>
    /// <para>The characters are where <paramref name="own"/> places them (the snapshot's own positions,
    /// the newer of ESI and the chat log); without it, where their last location poll did.</para>
    /// </summary>
    public static async Task<IntelWatchedSystems> WatchedSystemsAsync(
        JsonElement config, SystemGraph graph, IDbContextFactory<AppDbContext> dbFactory,
        IntelDistanceService? distances = null, IReadOnlyList<OwnPosition>? own = null, CancellationToken ct = default)
    {
        var around = ReadAround(config);
        var jumps  = ReadJumps(config, around);
        var ly     = ReadLy(config);

        var watched     = new HashSet<int>();
        var hopsTo      = new Dictionary<int, int>();      // from the nearest origin
        var via         = new HashSet<int>();              // the nearest way there goes through a hole
        var nearest     = new Dictionary<int, string>();   // the character that origin is, around characters
        var nearestId   = new Dictionary<int, long>();
        var lyDistances = new Dictionary<int, double>();   // light years from the nearest origin
        var lyNearest   = new Dictionary<int, string>();   // the character that is, around characters
        var lyNearestId = new Dictionary<int, long>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        DbConnection? conn = null;
        async Task<DbConnection> ConnectionAsync()
        {
            conn ??= db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
            return conn;
        }
        var positions = ly is null ? null : await PositionsAsync(await ConnectionAsync(), ct);

        async Task SpreadAsync(int origin, string? who, long whoId)
        {
            IEnumerable<(int Id, int Hops, bool Via)> reach = distances is not null
                ? (await distances.JumpsFromAsync(origin, IntelGraphMode.Threat, null, ct))
                    .Where(kv => kv.Value.Jumps <= jumps).Select(kv => (kv.Key, kv.Value.Jumps, kv.Value.ViaWormhole))
                : (await graph.DistancesWithinAsync(origin, jumps, ct)).Select(kv => (kv.Key, kv.Value, false));
            foreach (var (id, hops, hole) in reach)
            {
                watched.Add(id);
                if (hopsTo.TryGetValue(id, out var known) && (hops > known || (hops == known && (hole || !via.Contains(id))))) continue;
                hopsTo[id] = hops;
                if (hole) via.Add(id); else via.Remove(id);
                if (who is not null) { nearest[id] = who; nearestId[id] = whoId; }
            }

            if (ly is not { } range || positions is null || !positions.TryGetValue(origin, out var o)) return;
            foreach (var (id, p) in positions)
            {
                var dx = p.X - o.X; var dy = p.Y - o.Y; var dz = p.Z - o.Z;
                var far = Math.Sqrt(dx * dx + dy * dy + dz * dz) / MetresPerLightYear;
                if (far > range) continue;
                watched.Add(id);
                if (lyDistances.TryGetValue(id, out var was) && far >= was) continue;
                lyDistances[id] = far;
                if (who is not null) { lyNearest[id] = who; lyNearestId[id] = whoId; }
            }
        }

        if (around == AroundSystems)
        {
            foreach (var id in await ResolveSystemsAsync(await ConnectionAsync(), SystemNames(config), ct))
                await SpreadAsync(id, null, 0);
        }
        else
        {
            var origins = own is not null
                ? OriginsFrom(own, around, ReadList(config, "characters"))
                : await OriginsAsync(db, around, ReadList(config, "characters"), ct);
            foreach (var (characterId, name, systemId) in origins)
                await SpreadAsync(systemId, name, characterId);
        }

        return new IntelWatchedSystems(watched, hopsTo, nearest, lyDistances, lyNearest)
        {
            NearestCharacter           = nearestId,
            LightYearsNearestCharacter = lyNearestId,
            ViaWormhole                = via,
        };
    }

    /// <summary>
    /// The characters to draw the range around, from the snapshot's own positions: online, and —
    /// around undocked characters — in space. Named ones only when names are given.
    /// </summary>
    internal static List<(long CharacterId, string Name, int SystemId)> OriginsFrom(
        IReadOnlyList<OwnPosition> own, string around, IReadOnlyList<string> named)
    {
        var wanted = named.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. own
            .Where(o => o.SystemId > 0)
            .Where(o => wanted.Count == 0 || wanted.Contains(o.Name))
            .Where(o => around != AroundUndocked || !o.Docked)
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Select(o => (o.CharacterId, o.Name, o.SystemId))];
    }

    /// <summary>
    /// The characters to draw the range around, with the system each is in: the capsuleer's own,
    /// online, and — around undocked characters — in space. Named ones only when names are
    /// given. A character logged off is not watched wherever they were left: nobody is there to
    /// warn. Where their last location poll put them: for a reader with no snapshot.
    /// </summary>
    private static async Task<List<(long CharacterId, string Name, int SystemId)>> OriginsAsync(
        AppDbContext db, string around, IReadOnlyList<string> named, CancellationToken ct)
    {
        var rows = await (
            from s in db.CharacterStatuses.AsNoTracking()
            join c in db.Characters.AsNoTracking() on s.CharacterId equals c.Id
            where s.Online && s.SolarSystemId != null
            select new { c.Id, c.Name, s.SolarSystemId, s.StationId, s.StructureId }).ToListAsync(ct);

        var wanted = named.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. rows
            .Where(r => wanted.Count == 0 || wanted.Contains(r.Name))
            .Where(r => around != AroundUndocked || (r.StationId is null && r.StructureId is null))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => (r.Id, r.Name, r.SolarSystemId!.Value))];
    }

    // ── What is said ─────────────────────────────────────────────────────────

    /// <summary>
    /// The sentence to be spoken, exactly. A kill as a kill: where, then what was lost to what.
    /// A sighting by system: count and system first, because that is what decides whether to warp
    /// now; then what else was said; then the hulls; then the names. Never who reported it. Urgent
    /// first, then newest.
    /// </summary>
    public string? Announcement(JsonElement config, IReadOnlyList<AlarmMatch> matches)
        => ComposeAnnouncement(matches);

    internal static string? ComposeAnnouncement(IReadOnlyList<AlarmMatch> matches)
    {
        var said = matches.Where(m => m.Detail is not null && !m.Silent).ToList();
        if (said.Count == 0) return null;

        var items = new List<(bool Urgent, DateTime At, string Text)>();

        foreach (var k in said.Where(m => KindOf(m) == KindKill))
        {
            var d      = k.Detail!;
            var system = Str(d, "system") is { Length: > 0 } s ? s : AlarmsText.UnknownSystem;
            var victim = Str(d, "victim_hull") is { Length: > 0 } v ? v : AlarmsText.HullShip;
            var hulls  = Names(d, "hulls").ToList();
            var by     = hulls.Count > 0 ? AlarmWords.List(hulls) : AlarmsText.IntelSaidKillHostiles;
            var where  = Where(JumpsOf(k), d.TryGetValue("ly", out var l) && l is double far ? far : null, Str(d, "near"));
            items.Add((k.Urgent, AtOf(k), where is null
                ? string.Format(AlarmsText.IntelSaidKill, system, victim, by)
                : string.Format(AlarmsText.IntelSaidKillAt, system, where, victim, by)));
        }

        foreach (var g in said.Where(m => KindOf(m) != KindKill)
                              .GroupBy(m => m.Detail!.TryGetValue("system_id", out var s) && s is int id ? id : 0))
        {
            var list   = g.ToList();
            var system = list.Select(m => Str(m.Detail, "system")).FirstOrDefault(s => s.Length > 0) ?? AlarmsText.UnknownSystem;
            var pilots = list.Where(m => KindOf(m) == KindPilot).ToList();
            var names  = pilots.SelectMany(m => Names(m.Detail!, "pilots")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var count  = names.Count
                       + pilots.Count(m => !Names(m.Detail!, "pilots").Any())
                       + list.Where(m => KindOf(m) == KindCount).Select(m => m.Detail!.TryGetValue("count", out var c) && c is int n ? n : 0)
                             .DefaultIfEmpty(0).Max();
            var hulls  = list.SelectMany(m => Names(m.Detail!, "hulls")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var notes  = list.Select(m => Str(m.Detail, "note").Trim().TrimEnd('.')).Where(n => n.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var flags  = list.Aggregate(0, (f, m) => f | (m.Detail!.TryGetValue("flag_bits", out var b) && b is int bits ? bits : 0));
            var gate   = list.Select(m => Str(m.Detail, "gate")).FirstOrDefault(x => x.Length > 0);
            var jumps  = list.Select(JumpsOf).Where(j => j is not null).Min();
            var ly     = list.Select(m => m.Detail!.TryGetValue("ly", out var l) && l is double far ? far : (double?)null).Where(l => l is not null).Min();
            var near   = list.Select(m => Str(m.Detail, "near")).FirstOrDefault(n => n.Length > 0);
            var where  = Where(jumps, ly, near);
            var facts  = IntelDisplay.Facts(flags, gate);

            var sb = new System.Text.StringBuilder();
            if (count > 0)
            {
                sb.Append(where is null
                    ? Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidReportedOther), count, system)
                    : Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidReportedAtOther), count, system, where));
                // What else was said — "Bubbles, gate camp · on the QZ-X77 gate." — before the
                // hulls: it decides how to go in.
                if (facts is not null)
                    sb.Append(' ').Append(string.Format(AlarmsText.SaidSentence, char.ToUpper(facts[0], CultureInfo.CurrentCulture) + facts[1..]));
            }
            else
            {
                // Something said with nobody counted: where, then what.
                var what = facts ?? "";
                sb.Append(where is null
                    ? string.Format(AlarmsText.IntelSaidFlags, system, what)
                    : string.Format(AlarmsText.IntelSaidFlagsAt, system, where, what));
            }
            if (hulls.Count > 0)
                sb.Append(' ').Append(string.Format(AlarmsText.IntelSaidFlying,
                    hulls.Count == 1 ? AlarmWords.Hull(hulls[0]) : AlarmWords.List(hulls)));
            if (names.Count > 0)
            {
                var shown = string.Join(", ", names.Take(5));
                sb.Append(' ').Append(names.Count > 5
                    ? string.Format(AlarmsText.IntelSaidPilotsAndMore, shown, names.Count - 5)
                    : string.Format(AlarmsText.SaidSentence, shown));
            }
            foreach (var note in notes.Take(3))
                sb.Append(' ').Append(string.Format(AlarmsText.SaidSentence, note));

            items.Add((list.Any(m => m.Urgent), list.Max(AtOf), sb.ToString()));
        }

        return string.Join(" ", items
            .Select((x, i) => (x, i))
            .OrderByDescending(x => x.x.Urgent).ThenByDescending(x => x.x.At).ThenBy(x => x.i)
            .Select(x => x.x.Text));

        static IEnumerable<string> Names(IReadOnlyDictionary<string, object?> d, string key)
            => d.TryGetValue(key, out var v) && v is IEnumerable<string> list ? list.Where(n => !string.IsNullOrWhiteSpace(n)) : [];
    }

    /// <summary>How far out, in the interface language: "2 jumps from Alpha", "where Alpha is",
    /// "3 jumps out", "4.2 light years from Alpha"; null when there is no range to say it by.</summary>
    private static string? Where(int? jumps, double? ly, string? near)
    {
        near = string.IsNullOrEmpty(near) ? null : near;
        if (jumps is { } hops)
            return near is not null
                ? hops == 0 ? string.Format(AlarmsText.IntelSaidWhereIs, near)
                            : Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidJumpsFromOther), hops, near)
                : hops == 0 ? AlarmsText.IntelSaidHere
                            : Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidJumpsOutOther), hops);
        if (ly is { } far)
        {
            var text = far.ToString("0.0", CultureInfo.CurrentCulture);
            return near is not null ? string.Format(AlarmsText.IntelSaidLyFrom, text, near)
                                    : string.Format(AlarmsText.IntelSaidLyOut, text);
        }
        return null;
    }

    /// <summary>
    /// How far a system is, for the summary and the announcement: the jumps when the jump range
    /// caught it — that is how a gang comes — else the light years the light-year range caught it
    /// at. English, for the summary; the announcement says it from the detail.
    /// </summary>
    private static (string? Near, int? Jumps, double? Ly, string From) HowFar(int systemId, int jumps, IntelWatchedSystems zone)
    {
        var near = zone.Nearest.GetValueOrDefault(systemId);
        if (zone.Jumps.TryGetValue(systemId, out var hops) && (jumps > 0 || near is not null))
            return (near, hops, null, near is null ? "" : hops == 0 ? $" (where {near} is)" : $" ({hops} jump{(hops == 1 ? "" : "s")} from {near})");
        if (zone.Jumps.ContainsKey(systemId)) return (near, null, null, "");

        if (!zone.LightYears.TryGetValue(systemId, out var far)) return (null, null, null, "");
        var lyNear = zone.LightYearsNearest.GetValueOrDefault(systemId);
        var ly     = Math.Round(far, 1);
        var text   = ly.ToString("0.0", CultureInfo.InvariantCulture);
        return (lyNear, null, ly, lyNear is null ? $" ({text} ly out)" : $" ({text} ly from {lyNear})");
    }

    /// <summary>Every k-space system's position, read once.</summary>
    private static async Task<Dictionary<int, (double X, double Y, double Z)>> PositionsAsync(
        DbConnection conn, CancellationToken ct)
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
    /// The one-time step when the match keys changed shape (to episodes): every intel alarm is
    /// re-primed, so its first evaluation banks what is live now instead of announcing all of it, and
    /// its keys of the old shape go. Once per database, marked by <see cref="KeysVersionKey"/>; the
    /// mark is written first and only by the PC that writes it, so two PCs starting together do it
    /// once. True when this call did it.
    /// </summary>
    /// <remarks>Synchronous: it runs with the startup statements, which are.</remarks>
    public static bool RePrimeForEpisodeKeys(AppDbContext db)
    {
        var marked = db.Database.ExecuteSqlRaw(
            """INSERT INTO "AppPreferences" ("Key", "Value") VALUES ({0}, '1') ON CONFLICT ("Key") DO NOTHING""",
            KeysVersionKey);
        if (marked == 0) return false;

        db.Database.ExecuteSqlRaw(
            """DELETE FROM "AlarmSeenKeys" WHERE "AlarmId" IN (SELECT "Id" FROM "Alarms" WHERE "ConditionType" = 'intel')""");
        db.Database.ExecuteSqlRaw(
            """UPDATE "Alarms" SET "Primed" = FALSE WHERE "ConditionType" = 'intel'""");
        // An intel alarm left on the editor's general default (60 s) now looks every 15 s, as a new one
        // does: a lifetime ending is noticed then. One set to anything else keeps its own.
        db.Database.ExecuteSqlRaw(
            """UPDATE "Alarms" SET "PollSeconds" = 15 WHERE "ConditionType" = 'intel' AND "PollSeconds" = 60""");
        return true;
    }

    /// <summary>Marks, in the shared preferences, that intel alarms were re-primed for episode keys.</summary>
    public const string KeysVersionKey = "alarms.intel_keys_v2";

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

    /// <summary>A switch, or <paramref name="otherwise"/> when the config does not carry it (an
    /// alarm saved before the switch existed reads as its default).</summary>
    private static bool ReadBool(JsonElement e, string name, bool otherwise = false) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.True
            : otherwise;
}

/// <summary>The systems an intel alarm watches (<see cref="IntelCondition.WatchedSystemsAsync"/>): every one
/// in range, the jumps and light years from the nearest origin, and the character that origin is when
/// the alarm is around characters.</summary>
public sealed record IntelWatchedSystems(
    IReadOnlySet<int> Systems,
    IReadOnlyDictionary<int, int> Jumps, IReadOnlyDictionary<int, string> Nearest,
    IReadOnlyDictionary<int, double> LightYears, IReadOnlyDictionary<int, string> LightYearsNearest)
{
    /// <summary>The nearest origin's character id by jumps: whose standings judge who is hostile there.</summary>
    public IReadOnlyDictionary<int, long> NearestCharacter { get; init; } = new Dictionary<int, long>();

    /// <summary>The same by light years, for a system only the light-year range caught.</summary>
    public IReadOnlyDictionary<int, long> LightYearsNearestCharacter { get; init; } = new Dictionary<int, long>();

    /// <summary>The systems whose nearest way goes through a Thera or Turnur hole.</summary>
    public IReadOnlySet<int> ViaWormhole { get; init; } = new HashSet<int>();

    public static readonly IntelWatchedSystems None = new(new HashSet<int>(), new Dictionary<int, int>(),
        new Dictionary<int, string>(), new Dictionary<int, double>(), new Dictionary<int, string>());
}
