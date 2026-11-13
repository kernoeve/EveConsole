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
/// <item>a count of unnamed pilots once per size band (1, 2–4, 5–9, 10–19, 20+) in its episode, by the
/// highest the episode has counted: three rising to six is news, six to seven is not, and six falling
/// to three is not;</item>
/// <item>a flag (a camp, bubbles, a cyno, a hot drop, a spike, combat probes) once per episode, even
/// when the report counted nobody;</item>
/// <item>an episode that goes on keeps its first start after its first reports leave the snapshot's
/// window (<see cref="IntelEpisodeRegister"/>), so a long camp or count is not said again;</item>
/// <item>a kill once, said as a kill. Its attackers are banked with it and not said again as a
/// sighting;</item>
/// <item>a gate camp worked out from kills once per camp episode, per character and per route: near a
/// watched character, or on a route ahead. A new route sent through a camp already said says it again.</item>
/// </list>
///
/// <para><b>The route ahead.</b> Around characters, the systems still ahead on the last route the Route
/// Planner sent for one of them are watched too, however far (<see cref="CharacterRouteStore"/>): only the
/// part not yet flown, until arrival or six hours. A route set only in the game is not known.</para>
///
/// <para><b>Showing versus waking.</b> Every match is recorded and shown on the Alert row; only news
/// wakes anyone (<see cref="Waking"/>): seen, or come into range, within the fresh window of Settings →
/// Intel, and inside the wake range when one is set. Every intel alarm is in one wake group, so they
/// share a wake cooldown and say a sighting two of them catch once (<see cref="Services.AlarmWakeGate"/>).</para>
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

    /// <summary>Forgets the positions read, so the next light-year range reads them again: for
    /// tools/IntelCheck, whose scratch databases each have space of their own.</summary>
    internal static void ForgetPositions() => _positions = null;

    // What the range is drawn around. The strings are what the editor shows and what the config
    // stores; the reader is lenient about case and a few synonyms.
    internal const string AroundUndocked   = "Undocked characters";
    internal const string AroundCharacters = "Characters";
    internal const string AroundSystems    = "Systems";
    private static readonly string[] AroundChoices = [AroundUndocked, AroundCharacters, AroundSystems];

    // Who counts as hostile, by the standings of the character nearest: the worst tier each lets through.
    internal const string HostileNeutral  = "Neutral or worse";
    internal const string HostileNegative = "Negative only";
    internal const string HostileRed      = "Red only";
    private static readonly string[] HostileChoices = [HostileNeutral, HostileNegative, HostileRed];

    /// <summary>How far the watch list's Always entries fire by default, when farther than the range.</summary>
    internal const int DefaultWatchReach = 10;

    /// <summary>Without the distance service, how much farther than the reach the stargate walk goes, so a pilot
    /// seen a few jumps outside it before is still known to have come nearer.</summary>
    private const int ApproachMargin = 5;

    private readonly SystemGraph           _graph;
    private readonly IntelSnapshotService? _snapshots;
    private readonly IntelDistanceService? _distances;
    private readonly IntelWatchMemory      _memory;

    /// <param name="snapshots">The shared intel snapshot this check matches against; without it the
    /// check cannot evaluate, and says so.</param>
    /// <param name="distances">Threat distances (stargates plus Thera and Turnur's open holes); without
    /// them the range is walked over stargates alone.</param>
    /// <param name="memory">When each system came into each alarm's reach (what makes a match news);
    /// one of its own when not given.</param>
    public IntelCondition(SystemGraph graph, IntelSnapshotService? snapshots = null, IntelDistanceService? distances = null,
                          IntelWatchMemory? memory = null)
    {
        _graph     = graph;
        _snapshots = snapshots;
        _distances = distances;
        _memory    = memory ?? new IntelWatchMemory();
    }

    /// <summary>The wake group every intel alarm is in: one shared wake cooldown, and a sighting two of
    /// them catch said once.</summary>
    public const string WakeGroupName = "intel";
    public string? WakeGroup => WakeGroupName;

    /// <summary>The Sound action's share of its volume at the edge of the range; 1 within a jump.</summary>
    internal const double FarLoudness = 0.4;

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
        "said again as a sighting. Only news wakes the capsuleer (sound, speech, the agent, a dialog): seen, or " +
        "come into range, within the last two minutes by default (Settings → Intel), and within wake_jumps when " +
        "that is set; older or farther matches are only written to Alerts. Intel alarms share one wake cooldown " +
        "(15 s by default; urgent news skips it) and a sighting two of them catch is said once. The sound plays " +
        "quieter further out (quieter_far). Hostile is neutral or worse by default (hostile_from), by the standings " +
        "of the watched character nearest. Urgent, said first and never held back: anything in a priority system " +
        "(priority_systems, always watched), anyone on the watch list's Always alert (within watch_list_jumps, " +
        "whatever the range or minimum; Ignore never fires), and a pilot approaching (seen closer than before, or " +
        "reported heading toward a watched character), which wakes beyond wake_jumps unless approach_wakes is off. " +
        "A pilot heading away is said so. A distance through Thera or Turnur is said so. Around characters, the systems " +
        "still ahead on the last route the Route Planner sent for one of them are watched too, however far (route; dropped " +
        "on arrival or after six hours; a route set only in the game is not known). A gate camp worked out from kills " +
        "fires when it is in what is watched or on a route ahead (camps): once per camp, per character and per route, " +
        "so a new route through a known camp is said again; one on a route ahead within the wake range (5 jumps when " +
        "none is set) is urgent.";

    public object ParameterSchema => new
    {
        type = "object",
        properties = new
        {
            around = new
            {
                section     = SectionWhere,
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
                section     = SectionWhere,
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
                section     = SectionWhere,
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
                section     = SectionWhere,
                type        = "integer",
                @default    = DefaultJumps,
                description = "How many jumps out from each character or system to watch, through " +
                              "stargates and Thera's and Turnur's open wormholes. 0 means their own system " +
                              "only. Capped at 15 — past that it stops being a neighbourhood.",
            },
            ly = new
            {
                section     = SectionWhere,
                type        = "number",
                beside      = "jumps",
                title       = "Light years",
                description = "Optional. Also watch every system within this many light years of each " +
                              "character or system, however many gates away — what a jump drive or a " +
                              "cyno puts in reach. A report caught by either range fires. Capped at 20.",
            },
            route = new
            {
                section     = SectionWhere,
                type        = "boolean",
                @default    = true,
                show_if     = new Dictionary<string, string[]> { ["around"] = [AroundUndocked, AroundCharacters] },
                title       = "Watch the route ahead",
                description = "Also watch the systems still ahead on the last route the Route Planner sent to the game " +
                              "for a watched character, however far: what is seen there fires, said as so many jumps " +
                              "ahead on the route. Only the part not yet flown; the route is dropped on arrival or six " +
                              "hours after it was sent. A route set only in the game is not known. Default true.",
            },
            min_players = new
            {
                section     = SectionWhat,
                type        = "integer",
                description = "Only fire when at least this many hostiles are in the system. Default 1. " +
                              "Kills and flag-only reports fire whatever the number.",
            },
            ignore_no_visual = new
            {
                section     = SectionWhat,
                type        = "boolean",
                description = "Skip pilots and counts reported NV (no visual) — someone relaying a contact " +
                              "they cannot actually see.",
            },
            flag_reports = new
            {
                section     = SectionWhat,
                type        = "boolean",
                @default    = true,
                title       = "Flag-only reports",
                description = "Also fire for a report that counts nobody but says a gate camp, bubbles, a " +
                              "cyno, a hot drop, a spike or combat probes. Each is said once while it lasts. " +
                              "Default true.",
            },
            camps = new
            {
                section     = SectionWhat,
                type        = "boolean",
                @default    = true,
                title       = "Gate camps",
                description = "Fire for a gate camp worked out from kills on a gate (two or more pilots lost to the same " +
                              "attackers within half an hour), in what is watched or on a route ahead. Said once per camp, " +
                              "per character and per route, so a new route through a known camp is said again. One on a " +
                              "route ahead within wake_jumps (5 jumps when that is not set) is urgent. Default true.",
            },
            hostile_from = new
            {
                section     = SectionWhat,
                type        = "string",
                @enum       = HostileChoices,
                @default    = HostileNeutral,
                title       = "Hostile",
                description = "Which named pilots count as hostile, by the standings of the watched character " +
                              "nearest them (or the forced standings character of Settings → Intel): \"Neutral or " +
                              "worse\" (the default), \"Negative only\" or \"Red only\". Blues and your own " +
                              "characters never count. Unnamed counts and flag-only reports always do.",
            },
            watch_list = new
            {
                section     = SectionWhat,
                type        = "boolean",
                @default    = true,
                title       = "Use the watch list",
                description = "Follow the Intel tool's watch list: pilots, corporations, alliances, hulls and " +
                              "ship groups on Always alert fire within watch_list_jumps whatever the range, the " +
                              "hostility choice or min_players, said first and never held back; those on Ignore " +
                              "never fire. Default true.",
            },
            watch_list_jumps = new
            {
                section     = SectionWhat,
                type        = "integer",
                @default    = DefaultWatchReach,
                show_if     = new Dictionary<string, string[]> { ["watch_list"] = ["true"] },
                title       = "Watch list reach",
                description = "How many jumps out the watch list's Always entries fire, when that is farther than " +
                              "jumps. Default 10, capped at 15.",
            },
            wake_jumps = new
            {
                section     = SectionWaking,
                type        = "integer",
                optional    = true,
                title       = "Wake within",
                description = "Optional. Only matches this many jumps out or nearer wake you (sound, speech, the " +
                              "agent, a dialog); farther ones are only written to Alerts. Kills and urgent news " +
                              "always wake. Left out, the whole range wakes.",
            },
            approach_wakes = new
            {
                section     = SectionWaking,
                type        = "boolean",
                @default    = true,
                title       = "Approaching pilots still wake",
                description = "With wake_jumps set: a pilot coming nearer (seen closer than before, or reported " +
                              "heading toward a watched character) wakes you anywhere in the range. Default true.",
            },
            quieter_far = new
            {
                section     = SectionWaking,
                type        = "boolean",
                @default    = true,
                title       = "Quieter further out",
                description = "The sound plays at full volume within a jump, and more quietly further out: down to " +
                              "40% at the edge of the jump range. Speech keeps its own volume. Default true.",
            },
            priority_systems = new
            {
                section     = SectionWaking,
                type        = "array",
                items       = new { type = "string" },
                format      = "system-name",
                title       = "Priority systems",
                description = "Optional. Systems always watched, wherever the characters are; what is seen there " +
                              "always wakes you, is never held back by another intel alarm, and is said first.",
            },
        },
        required = Array.Empty<string>(),
    };

    // The editor's sections: where to watch, what fires, and what wakes.
    internal const string SectionWhere  = "where";
    internal const string SectionWhat   = "what";
    internal const string SectionWaking = "waking";

    // The editor's words; the three above are the agent's and stay English.
    public string ScreenName        => AlarmsText.CheckIntel;
    public string ScreenDescription => Sentences.Join(Sentences.Join(Sentences.Join(
        Sentences.Join(AlarmsText.CheckIntelNote, AlarmsText.CheckIntelKillsNote), AlarmsText.CheckIntelChangedNote),
        AlarmsText.CheckIntelRouteNote), AlarmsText.CheckIntelWakeNote);

    public string? ScreenSection(string section) => section switch
    {
        SectionWhere  => AlarmsText.IntelSectionWhere,
        SectionWhat   => AlarmsText.IntelSectionWhat,
        SectionWaking => AlarmsText.IntelSectionWaking,
        _             => null,
    };

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
        "route"            => new(AlarmsText.IntelRouteLabel,       AlarmsText.IntelRouteNote),
        "camps"            => new(AlarmsText.IntelCampsLabel,       AlarmsText.IntelCampsNote),
        "wake_jumps"       => new(AlarmsText.IntelWakeJumpsLabel,   AlarmsText.IntelWakeJumpsNote, AlarmsText.IntelWakeJumpsSuffix),
        "quieter_far"      => new(AlarmsText.IntelQuieterFarLabel,  AlarmsText.IntelQuieterFarNote),
        "hostile_from"     => new(AlarmsText.IntelHostileFromLabel, AlarmsText.IntelHostileFromNote),
        "watch_list"       => new(AlarmsText.IntelWatchListLabel,   AlarmsText.IntelWatchListNote),
        "watch_list_jumps" => new(AlarmsText.IntelWatchReachLabel,  AlarmsText.IntelWatchReachNote, AlarmsText.IntelWakeJumpsSuffix),
        "approach_wakes"   => new(AlarmsText.IntelApproachWakesLabel, AlarmsText.IntelApproachWakesNote),
        "priority_systems" => new(AlarmsText.IntelPrioritySystemsLabel, AlarmsText.IntelPrioritySystemsNote),
        _                  => null,
    };

    public string? ScreenOption(string property, string value) => value switch
    {
        AroundUndocked   => AlarmsText.OptionUndockedCharacters,
        AroundCharacters => AlarmsText.OptionCharacters,
        AroundSystems    => AlarmsText.OptionSystems,
        HostileNeutral   => AlarmsText.OptionNeutralOrWorse,
        HostileNegative  => AlarmsText.OptionNegativeOnly,
        HostileRed       => AlarmsText.OptionRedOnly,
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
        if (!ReadBool(config, "flag_reports", true)) text += ", no flag-only reports";
        if (ReadWakeJumps(config) is { } wakes) text += $", wakes within {wakes} jump{(wakes == 1 ? "" : "s")}";
        if (!ReadBool(config, "quieter_far", true)) text += ", same loudness near or far";
        var hostile = ReadHostileFrom(config);
        if (hostile != StandingTier.Neutral) text += hostile == StandingTier.Red ? ", red only" : ", negative only";
        if (ReadWatchReach(config) is not { } watchReach) text += ", no watch list";
        else if (watchReach != DefaultWatchReach) text += $", watch list within {watchReach} jump{(watchReach == 1 ? "" : "s")}";
        if (ReadWakeJumps(config) is not null && !ReadBool(config, "approach_wakes", true)) text += ", approaching pilots wake only within that";
        var priority = ReadList(config, "priority_systems");
        if (priority.Count > 0) text += $", {priority.Count} priority system{(priority.Count == 1 ? "" : "s")}";
        if (around != AroundSystems && !ReadBool(config, "route", true)) text += ", not the route ahead";
        if (!ReadBool(config, "camps", true)) text += ", no gate camps";
        return text;
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

        var options = Options(config);
        var matches = Match(snapshot, zone, options, ctx.Now);

        // What is news to the capsuleer, as opposed to unseen by this alarm: remembered only while
        // something is watched, so a long dock (or every character logged off) begins it again.
        var look = Observe(_memory, ctx.Alarm.Id, zone, options, matches, ctx.Now);
        return Waking(matches, zone, options, ctx.Now, TimeSpan.FromSeconds(snapshot.Settings.FreshSeconds), look);
    }

    /// <summary>
    /// One look of an alarm into its memory: the range, the kills it matched, and — kept apart, when it
    /// follows the watch list — the watch list's farther reach, where an Always pilot is news when its system
    /// comes into that reach. ⚠️ The range is remembered on its own: the reach (ten jumps by default) is wider
    /// than the range (five), so a system crossing into the range was almost always in the reach already, and
    /// must still be news to the range then. Pure but for the memory: tools/IntelCheck runs it across a move.
    /// </summary>
    internal static IntelWatchMemory.Look Observe(IntelWatchMemory memory, long alarmId, IntelWatchedSystems zone,
                                                  IntelAlarmOptions options, IReadOnlyList<AlarmMatch> matches, DateTimeOffset now) =>
        memory.Observe(alarmId, zone.Systems, matches.Where(m => KindOf(m) is KindKill or KindCamp).Select(m => m.Key), now,
                       options.WatchList ? zone.Systems.Union(zone.WatchReach) : null);

    /// <summary>
    /// Which matches wake the capsuleer and how loud, as against only being shown (see
    /// <see cref="AlarmMatch.Quiet"/>). Pure: tools/IntelCheck runs it on matches built by hand.
    /// <list type="bullet">
    /// <item><b>News only.</b> Quiet when older than <paramref name="fresh"/> (Settings → Intel, two
    /// minutes by default), counted from the later of when it was seen and when its system came into
    /// the range, or for a system beyond it into the watch list's reach (<paramref name="look"/>) — and, for a kill, when this alarm first caught it, since the
    /// kill feed brings a kill minutes after it happened. Urgent news is held to this too: what makes
    /// it urgent is how near it is, not how new.</item>
    /// <item><b>Within the wake range</b>, when the alarm sets one. A kill, a priority system and the watch
    /// list's Always always wake; a pilot approaching does too unless the alarm turns that off.</item>
    /// <item><b>Loudness</b>, when the alarm plays far news quieter: 1 within a jump, down to
    /// <see cref="FarLoudness"/> at the edge of the range (<see cref="Loudness"/>); urgent news at 1.</item>
    /// </list>
    /// </summary>
    internal static IReadOnlyList<AlarmMatch> Waking(
        IReadOnlyList<AlarmMatch> matches, IntelWatchedSystems zone, IntelAlarmOptions options, DateTimeOffset now,
        TimeSpan fresh, IntelWatchMemory.Look look)
    {
        return [.. matches.Select(m =>
        {
            var system = m.Detail is { } d && d.TryGetValue("system_id", out var s) && s is int id ? id : 0;
            var seen   = AtOf(m) is var at && at != DateTime.MinValue
                ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : DateTimeOffset.MinValue;
            var kill   = KindOf(m) == KindKill;
            // From when the range took the system in; for one only the watch list's reach holds, from when that did.
            var news   = Later(seen, look.NewsAt(system, zone.Systems.Contains(system)));
            // A kill, and a gate camp, from when this alarm first caught it: the kill feed is minutes late, and a
            // camp is new to a route sent through it, or to another character, when its key is.
            if (kill || KindOf(m) == KindCamp) news = Later(news, look.CaughtAt(m.Key));

            int? jumps = zone.Jumps.TryGetValue(system, out var hops) ? hops : null;
            double? ly = zone.LightYears.TryGetValue(system, out var far) ? far : null;

            var anywhere = kill || Flag(m, "priority") || Flag(m, "watch") || (options.ApproachWakes && Flag(m, "approaching"));
            var quiet = now - news > fresh
                     || (!anywhere && options.WakeJumps is int within && (jumps is not int j || j > within));
            var loud  = m.Urgent || !options.QuieterFar ? 1.0 : Loudness(jumps, ly, options.Jumps, options.Ly);
            return m with { Quiet = quiet, Loudness = loud };
        })];

        static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
    }

    /// <summary>
    /// The Sound action's share of its volume for news this far out: 1 within a jump, falling evenly to
    /// <see cref="FarLoudness"/> at <paramref name="range"/> jumps; a system only the light-year range
    /// caught by its light years against that range. Relative to the alarm's own range, because ranges
    /// differ: the edge of a three-jump alarm is as quiet as the edge of a fifteen-jump one.
    /// </summary>
    internal static double Loudness(int? jumps, double? ly, int range, double? lyRange)
    {
        if (jumps is int hops)
            return hops <= 1 || range <= 1 ? 1 : 1 - (1 - FarLoudness) * (Math.Min(hops, range) - 1) / (range - 1.0);
        if (ly is double far && lyRange is double reach && reach > 0)
            return 1 - (1 - FarLoudness) * Math.Clamp(far / reach, 0, 1);
        return 1;
    }

    /// <summary>Where the user's characters are, as the snapshot has it (the newer of ESI and the chat
    /// log); null before the first read, when the characters' rows are asked instead.</summary>
    private static IReadOnlyList<OwnPosition>? OwnOf(IntelSnapshot snapshot) =>
        snapshot.At == DateTimeOffset.MinValue ? null : snapshot.Own;

    // ── Matching ─────────────────────────────────────────────────────────────

    /// <summary>What one alarm's config asks for, read once.</summary>
    /// <param name="WakeJumps">The wake range: farther matches only show. Null for the whole range.</param>
    /// <param name="QuieterFar">The sound plays quieter further out.</param>
    /// <param name="Ly">The light-year range, when set: what a system only it caught is as far as.</param>
    /// <param name="HostileFrom">The worst tier a named pilot may be and still not count: Neutral lets
    /// red, orange and neutral count, Orange red and orange, Red red alone.</param>
    /// <param name="WatchList">The watch list is followed: Always fires farther and first, Ignore never.</param>
    /// <param name="ApproachWakes">A pilot approaching wakes beyond the wake range.</param>
    /// <param name="Camps">Gate camps worked out from kills fire, in what is watched or on a route ahead.</param>
    internal sealed record IntelAlarmOptions(int Jumps, int MinPlayers = 1, bool SkipNoVisual = false, bool FlagReports = true,
                                             int? WakeJumps = null, bool QuieterFar = true, double? Ly = null,
                                             StandingTier HostileFrom = StandingTier.Neutral, bool WatchList = true,
                                             bool ApproachWakes = true, bool Camps = true);

    internal static IntelAlarmOptions Options(JsonElement config)
    {
        var around = ReadAround(config);
        return new IntelAlarmOptions(
            ReadJumps(config, around),
            Math.Max(1, ReadInt(config, "min_players") ?? 1),
            ReadBool(config, "ignore_no_visual"),
            ReadBool(config, "flag_reports", true),
            ReadWakeJumps(config),
            ReadBool(config, "quieter_far", true),
            ReadLy(config),
            ReadHostileFrom(config),
            ReadBool(config, "watch_list", true),
            ReadBool(config, "approach_wakes", true),
            ReadBool(config, "camps", true));
    }

    /// <summary>How many jumps ahead on a route a gate camp is urgent when the alarm sets no wake range.</summary>
    internal const int CampUrgentJumps = 5;

    /// <summary>The wake range, capped like the range; null when it is not set (the whole range wakes).</summary>
    private static int? ReadWakeJumps(JsonElement config) =>
        ReadInt(config, "wake_jumps") is int n ? Math.Clamp(n, 0, MaxJumps) : null;

    /// <summary>How far the watch list's Always entries fire, capped like the range; null when the alarm
    /// does not follow the watch list.</summary>
    internal static int? ReadWatchReach(JsonElement config) =>
        ReadBool(config, "watch_list", true) ? Math.Clamp(ReadInt(config, "watch_list_jumps") ?? DefaultWatchReach, 0, MaxJumps) : null;

    /// <summary>The worst tier that still counts as hostile. Lenient, as the agent may word it: anything
    /// saying red is red only, anything saying negative (or orange) negative only, else neutral or worse.</summary>
    internal static StandingTier ReadHostileFrom(JsonElement config)
    {
        var text = ReadString(config, "hostile_from")?.Trim() ?? "";
        if (text.Contains("red", StringComparison.OrdinalIgnoreCase)) return StandingTier.Red;
        if (text.Contains("neg", StringComparison.OrdinalIgnoreCase) || text.Contains("orange", StringComparison.OrdinalIgnoreCase))
            return StandingTier.Orange;
        return StandingTier.Neutral;
    }

    // What a match is about, in its detail.
    internal const string KindPilot = "pilot";
    internal const string KindCount = "count";
    internal const string KindFlag  = "flag";
    internal const string KindKill  = "kill";
    internal const string KindCamp  = "camp";

    /// <summary>
    /// Everything in the watched systems now, as matches keyed by episode (see the class summary).
    /// Pure: tools/IntelCheck runs it on snapshots built by hand.
    ///
    /// <list type="bullet">
    /// <item><b>Hostile</b> is the alarm's choice (neutral or worse by default, or negative only, or red only)
    /// as the character the system is nearest sees it: the standings rule every intel view uses. Blues and
    /// the user's own characters never are; unnamed counts and flags always count.</item>
    /// <item><b>The watch list</b>, when the alarm follows it: Ignore never fires. Always fires whatever the
    /// hostility choice or the minimum (a blue still never), out to the watch list's reach, and is urgent.</item>
    /// <item><b>Priority systems</b>: whatever fires there is urgent.</item>
    /// <item><b>Approaching</b>: a pilot, or a count, seen nearer than where it was seen before, or reported
    /// heading toward the character nearest, is urgent. One heading away is said so, and nothing more.</item>
    /// </list>
    /// <para>Said first: priority systems, then the watch list, then other urgent news; then the nearest,
    /// then the newest (<see cref="Ordered"/>).</para>
    /// </summary>
    internal static IReadOnlyList<AlarmMatch> Match(
        IntelSnapshot snapshot, IntelWatchedSystems zone, IntelAlarmOptions options, DateTimeOffset now)
    {
        var standings = snapshot.Standings;
        long? ViewerOf(int systemId) =>
            zone.NearestCharacter.TryGetValue(systemId, out var id) ? id
            : zone.LightYearsNearestCharacter.TryGetValue(systemId, out var lyId) ? lyId : null;
        IntelWatchMark Mark(long pilot, long corp, long alliance, int? ship) =>
            options.WatchList ? snapshot.WatchOf(pilot, corp, alliance, ship) : IntelWatchMark.None;
        // Whether a named pilot fires: never on Ignore, never a friend (blue, or one of the user's own); on
        // Always whatever the alarm's hostility choice, otherwise only at that tier or worse.
        bool Fires(int systemId, long pilot, long corp, long alliance, IntelWatchMark mark) =>
            FiresFor(ViewerOf(systemId), pilot, corp, alliance, mark);
        bool FiresFor(long? viewer, long pilot, long corp, long alliance, IntelWatchMark mark)
        {
            if (mark == IntelWatchMark.Ignored) return false;
            var tier = standings.TierFor(viewer, pilot, corp, alliance);
            return StandingTiers.IsHostile(tier) && (mark == IntelWatchMark.Always || tier <= options.HostileFrom);
        }
        // On a watched character's route ahead: how many jumps along it, and whose it is.
        void RouteInto(Dictionary<string, object?> detail, int systemId)
        {
            if (!zone.Route.TryGetValue(systemId, out var step)) return;
            detail["route_jumps"] = step.Jumps;
            detail["route_of"]    = step.Name;
        }
        // The range (with the light years and the priority systems), and the watch list's reach beyond it.
        bool Watched(int systemId) => zone.Systems.Contains(systemId);
        bool InReach(int systemId) => zone.Systems.Contains(systemId) || (options.WatchList && zone.WatchReach.Contains(systemId));
        string English(int systemId) =>
            snapshot.SystemNames.TryGetValue(systemId, out var name) ? name : systemId.ToString(CultureInfo.InvariantCulture);

        // Which way a sighting goes, from the character nearest it: by where it was seen before (only lately: within
        // twice the named lifetime), and by where a report said it was heading.
        var lately = snapshot.Settings.Named + snapshot.Settings.Named;
        IntelApproach Approach(int systemId, int? before, DateTimeOffset? beforeAt, int? heading)
        {
            if (!zone.Jumps.TryGetValue(systemId, out var here)) return default;
            int? was = null;
            if (before is int b && b != systemId && beforeAt is { } at && now - at <= lately && zone.JumpsBetween(systemId, b) is int then)
                was = then;
            int? to = heading is int h && h != systemId ? zone.JumpsBetween(systemId, h) : null;
            return new IntelApproach(was is int w && w > here ? w : null, to is not null ? heading : null, to,
                                     was > here, to < here, to > here);
        }

        var matches = new List<AlarmMatch>();

        // ── Kills: always news, said as a kill ───────────────────────────────
        // Who they placed is said with them: an attacker is not said again as a sighting.
        var onKills = new HashSet<(long Pilot, int System)>();
        foreach (var e in snapshot.Feed)
        {
            if (e.Kind != IntelEventKind.Kill || e.KillMailId is not int killId) continue;
            if (!InReach(e.SystemId) || e.ExpiresAt <= now) continue;

            // A kill by blues alone is no threat, nor one by pilots the watch list ignores; anyone else among
            // the attackers is. Beyond the range, only an attacker the watch list always alerts on fires it.
            var gang = e.Pilots.Where(p => p.CharacterId > 0)
                .Select(p => (Pilot: p, Mark: Mark(p.CharacterId, p.CorporationId, p.AllianceId, p.ShipTypeId)))
                .Where(x => Fires(e.SystemId, x.Pilot.CharacterId, x.Pilot.CorporationId, x.Pilot.AllianceId, x.Mark))
                .ToList();
            var watch = gang.Any(x => x.Mark == IntelWatchMark.Always);
            if (gang.Count == 0 || (!Watched(e.SystemId) && !watch)) continue;
            foreach (var (p, _) in gang) onKills.Add((p.CharacterId, e.SystemId));

            var hulls = gang.Select(x => x.Pilot).Where(p => p.ShipTypeId is > 0 && !string.IsNullOrWhiteSpace(p.Ship))
                .GroupBy(p => p.ShipTypeId!.Value)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => g.First().Ship!).Take(3).ToList();
            var names  = gang.Select(x => x.Pilot.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct().ToList();
            var victim = string.IsNullOrWhiteSpace(e.VictimShip) ? null : e.VictimShip;
            var (near, jumpsOut, lyOut, from) = HowFar(e.SystemId, options.Jumps, zone);
            var priority = zone.Priority.Contains(e.SystemId);
            var urgent = priority || watch || (zone.Jumps.TryGetValue(e.SystemId, out var j) && j <= UrgentKillJumps);

            var killDetail = new Dictionary<string, object?>
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
                ["via"]           = zone.ViaWormhole.Contains(e.SystemId),
                ["priority"]      = priority,
                ["watch"]         = watch,
                ["urgent"]        = urgent,
                ["at"]            = e.At.UtcDateTime,
            };
            RouteInto(killDetail, e.SystemId);
            matches.Add(new AlarmMatch(
                "intel:k:" + killId.ToString(CultureInfo.InvariantCulture),
                $"Kill in {English(e.SystemId)}{from}: {victim ?? "a ship"} lost to " +
                (hulls.Count > 0 ? string.Join(", ", hulls) : gang.Count == 1 ? "1 pilot" : $"{gang.Count} pilots"))
            {
                Urgent = urgent,
                Detail = killDetail,
            });
        }

        // ── Gate camps: near a watched character, or on a route ahead ─────────
        // Once per camp, per character and per route: the key carries whose it is and which route, so a new route
        // through a camp already said is said again, and so is the camp near another character.
        if (options.Camps && snapshot.Camps.Count > 0)
        {
            // The attackers' hulls as the kill feed names them.
            var hullNames = new Dictionary<int, string>();
            foreach (var e in snapshot.Feed)
                if (e.Kind == IntelEventKind.Kill)
                    foreach (var p in e.Pilots)
                        if (p.ShipTypeId is int t and > 0 && !string.IsNullOrWhiteSpace(p.Ship)) hullNames.TryAdd(t, p.Ship!);

            foreach (var camp in snapshot.Camps)
            {
                if (!camp.IsActive(now)) continue;
                var id = camp.SystemId;
                // On a route: a system still to come, or the one the character is on when the camp is on the gate
                // the route leaves by.
                IntelRouteStep? step = zone.Route.TryGetValue(id, out var ahead) ? ahead
                    : zone.RouteHere.TryGetValue(id, out var here) && here.NextSystemId == camp.GateSystemId ? here : null;
                if (step is null && !Watched(id)) continue;

                // Judged by whose route it is on, else by the character nearest. A camp of blues, or of pilots the
                // watch list ignores, is no threat.
                var viewer = step?.CharacterId ?? ViewerOf(id);
                var gang = camp.Attackers
                    .Select(a => (Attacker: a, Mark: Mark(a.CharacterId, a.CorporationId, a.AllianceId, a.ShipTypeId)))
                    .Where(x => FiresFor(viewer, x.Attacker.CharacterId, x.Attacker.CorporationId, x.Attacker.AllianceId, x.Mark))
                    .ToList();
                if (gang.Count == 0) continue;

                var watch    = gang.Any(x => x.Mark == IntelWatchMark.Always);
                var priority = zone.Priority.Contains(id);
                var urgent   = priority || watch || (step is not null && step.Jumps <= (options.WakeJumps ?? CampUrgentJumps));
                var who      = viewer ?? 0;
                var hulls = gang.Where(x => x.Attacker.ShipTypeId is > 0)
                    .GroupBy(x => x.Attacker.ShipTypeId!.Value)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                    .Select(g => hullNames.GetValueOrDefault(g.Key)).Where(n => n is not null).Select(n => n!).Take(3).ToList();
                var kills   = camp.KillMailIds.Count;
                var minutes = Math.Max(1, (int)Math.Ceiling((camp.LastKill - camp.FirstKill).TotalMinutes));
                var (near, jumpsOut, lyOut, from) = HowFar(id, options.Jumps, zone);

                var detail = new Dictionary<string, object?>
                {
                    ["kind"]         = KindCamp,
                    ["camp_key"]     = camp.Key,
                    ["system"]       = snapshot.SystemName(id),
                    ["system_id"]    = id,
                    ["gate"]         = snapshot.SystemName(camp.GateSystemId),
                    ["gate_id"]      = camp.GateSystemId,
                    ["kills"]        = kills,
                    ["minutes"]      = minutes,
                    ["count"]        = gang.Count,
                    ["hulls"]        = hulls,
                    ["character_id"] = who,
                    ["on_route"]     = step is not null,
                    ["jumps"]        = jumpsOut,
                    ["ly"]           = lyOut,
                    ["near"]         = near,
                    ["via"]          = zone.ViaWormhole.Contains(id),
                    ["priority"]     = priority,
                    ["watch"]        = watch,
                    ["urgent"]       = urgent,
                    ["at"]           = camp.LastKill.UtcDateTime,
                };
                RouteInto(detail, id);
                matches.Add(new AlarmMatch(
                    $"intel:c:{camp.Key}|{who.ToString(CultureInfo.InvariantCulture)}|{(step is null ? "near" : Stamp(step.SetAt))}",
                    $"Gate camp in {English(id)} on the {English(camp.GateSystemId)} gate{from}: {kills} kills in {minutes} min" +
                    (hulls.Count > 0 ? $" ({string.Join(", ", hulls)})" : ""))
                {
                    Urgent = urgent,
                    Detail = detail,
                });
            }
        }

        // ── Each watched system as it stands ─────────────────────────────────
        foreach (var system in snapshot.Systems.Values)
        {
            var id = system.SystemId;
            if (!InReach(id)) continue;
            var watched  = Watched(id);
            var priority = zone.Priority.Contains(id);

            var facts = system.Facts.Where(f => f.ExpiresAt > now).ToList();
            var flagBits = facts.Aggregate(0, (bits, f) => bits | (int)f.Flag);
            var (near, jumpsOut, lyOut, from) = HowFar(id, options.Jumps, zone);

            Dictionary<string, object?> Detail(string kind, int count, IReadOnlyList<string> pilots, IReadOnlyList<string> hulls,
                                               int flags, DateTimeOffset at)
            {
                var detail = SystemDetail(kind, count, pilots, hulls, flags, at);
                RouteInto(detail, id);
                return detail;
            }
            Dictionary<string, object?> SystemDetail(string kind, int count, IReadOnlyList<string> pilots, IReadOnlyList<string> hulls,
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
                ["via"]       = zone.ViaWormhole.Contains(id),
                ["priority"]  = priority,
                ["at"]        = at.UtcDateTime,
            };

            // Who is there: alive, not a loss (a loss takes a pilot off the map), firing for the character
            // nearest, and seen rather than relayed when asked. Beyond the range, only the watch list's Always.
            var pilots = system.PilotIds
                .Select(p => snapshot.Pilots.GetValueOrDefault(p))
                .Where(p => p is not null && p.SystemId == id && p.ExpiresAt > now
                            && p.Source != IntelLastSeenSource.KillVictim
                            && !(options.SkipNoVisual && p.NoVisual))
                .Select(p => (Pilot: p!, Mark: Mark(p!.CharacterId, p.CorporationId, p.AllianceId, p.ShipTypeId)))
                .Where(x => Fires(id, x.Pilot.CharacterId, x.Pilot.CorporationId, x.Pilot.AllianceId, x.Mark)
                            && (watched || x.Mark == IntelWatchMark.Always))
                .ToList();
            var unnamed = system.Unnamed is { Count: > 0 } u && u.ExpiresAt > now && !(options.SkipNoVisual && u.NoVisual) ? u : null;
            // A count by the hulls it was said in: always alerted on when any is, ignored only when all are.
            var countMark = unnamed is null || !options.WatchList ? IntelWatchMark.None : CountMark(snapshot, unnamed.Ships);
            if (countMark == IntelWatchMark.Ignored || (!watched && countMark != IntelWatchMark.Always)) unnamed = null;

            // The minimum counts everyone who fires; the watch list's Always fires below it.
            var enough = pilots.Count + (unnamed?.Count ?? 0) >= options.MinPlayers;

            foreach (var (p, mark) in pilots)
            {
                var watch = mark == IntelWatchMark.Always;
                if (!enough && !watch) continue;
                var name = string.IsNullOrWhiteSpace(p.Name) ? null : p.Name;
                var detail = Detail(KindPilot, 1, name is null ? [] : [name],
                                    string.IsNullOrWhiteSpace(p.Ship) ? [] : [p.Ship!], flagBits, p.At);
                detail["character_id"] = p.CharacterId;
                detail["no_visual"]    = p.NoVisual;
                detail["watch"]        = watch;
                if (p.FromKill) detail["note"] = AlarmsText.IntelSaidOnKill;
                var approach = Approach(id, p.PrevSystemId, p.PrevSeenAt, p.HeadingSystemId);
                approach.Write(detail, snapshot);
                matches.Add(new AlarmMatch(
                    $"intel:p:{p.CharacterId.ToString(CultureInfo.InvariantCulture)}|{id.ToString(CultureInfo.InvariantCulture)}|{Stamp(p.InSystemSince)}",
                    $"{name ?? "1 pilot"} in {English(id)}{from}{(string.IsNullOrWhiteSpace(p.Ship) ? "" : $" ({p.Ship})")}{approach.English(English)}")
                {
                    // Said with the kill that placed them, never again on its own.
                    Silent = onKills.Contains((p.CharacterId, id)),
                    Urgent = priority || watch || approach.Approaching,
                    Detail = detail,
                });
            }

            if (unnamed is not null && (enough || countMark == IntelWatchMark.Always))
            {
                var detail = Detail(KindCount, unnamed.Count, [],
                                    [.. IntelDisplay.ParseShips(unnamed.Ships).Select(s => s.Name)], flagBits, unnamed.At);
                detail["no_visual"] = unnamed.NoVisual;
                detail["watch"]     = countMark == IntelWatchMark.Always;
                // Where the report said they came from, and where it said they were heading.
                var move = system.Moves.FirstOrDefault(m => m.ReportId == unnamed.ReportId);
                var approach = Approach(id, move?.FromSystemId, move?.At, system.HeadingSystemId);
                approach.Write(detail, snapshot);
                matches.Add(new AlarmMatch(
                    // The band of the most counted in the episode: a count that falls is not news.
                    $"intel:n:{id.ToString(CultureInfo.InvariantCulture)}|{Stamp(unnamed.EpisodeStart)}|{Band(Math.Max(unnamed.Peak, unnamed.Count)).ToString(CultureInfo.InvariantCulture)}",
                    $"{(unnamed.Count == 1 ? "1 pilot" : $"{unnamed.Count} pilots")} in {English(id)}{from}{approach.English(English)}")
                {
                    Urgent = priority || countMark == IntelWatchMark.Always || approach.Approaching,
                    Detail = detail,
                });
            }

            // What was said besides who, on its own: each once per episode, whoever is there. In the range only:
            // the watch list says nothing about a flag.
            if (options.FlagReports && watched)
                foreach (var fact in facts.Where(f => FiringFlags.Contains(f.Flag)))
                    matches.Add(new AlarmMatch(
                        $"intel:f:{id.ToString(CultureInfo.InvariantCulture)}|{fact.Flag}|{Stamp(fact.EpisodeStart)}",
                        $"{string.Join(", ", IntelDisplay.FlagsEnglish((int)fact.Flag))} in {English(id)}{from}")
                    { Urgent = priority, Detail = Detail(KindFlag, 0, [], [], flagBits, fact.At) });
        }

        return Ordered(matches);
    }

    /// <summary>A count's watch-list mark by the hulls it was said in ("8× Hull"), as the views mark it: Always
    /// when any is on Always; Ignored only when every hull said is ignored; otherwise nothing, since a count of
    /// nobody in particular is something the list says nothing about.</summary>
    private static IntelWatchMark CountMark(IntelSnapshot snapshot, string? ships)
    {
        var marks = IntelLens.SaidHullMarks(snapshot, ships);
        if (marks.Contains(IntelWatchMark.Always)) return IntelWatchMark.Always;
        return marks.Count > 0 && marks.All(m => m == IntelWatchMark.Ignored) ? IntelWatchMark.Ignored : IntelWatchMark.None;
    }

    /// <summary>
    /// Which way a sighting is going, from the watched character nearest it.
    /// </summary>
    /// <param name="WasJumps">Seen nearer than before: how far out it was then.</param>
    /// <param name="Heading">The system a report said it was heading for, when its distance is known.</param>
    /// <param name="HeadingJumps">How far that system is from the same character.</param>
    /// <param name="Closer">Seen nearer than where it was seen before.</param>
    /// <param name="Toward">Heading for a system nearer the character.</param>
    /// <param name="Away">Heading for a system farther from it.</param>
    internal readonly record struct IntelApproach(int? WasJumps, int? Heading, int? HeadingJumps, bool Closer, bool Toward, bool Away)
    {
        /// <summary>Coming nearer, either way: urgent.</summary>
        public bool Approaching => Closer || Toward;

        public void Write(Dictionary<string, object?> detail, IntelSnapshot snapshot)
        {
            detail["approaching"] = Approaching;
            detail["approach"]    = Approaching ? "closer" : Away ? "away" : null;
            detail["away"]        = Away;
            if (Closer) detail["was_jumps"] = WasJumps;
            if (Heading is int h)
            {
                detail["heading"]       = snapshot.SystemName(h);
                detail["heading_jumps"] = HeadingJumps;
                detail["toward"]        = Toward;
            }
        }

        /// <summary>For the English summary: ", approaching (was 4 jumps out)", ", heading away to X".</summary>
        public string English(Func<int, string> systemName)
        {
            var text = "";
            if (Toward && Heading is int to) text = $", approaching (heading to {systemName(to)})";
            else if (Closer) text = $", approaching (was {WasJumps} jump{(WasJumps == 1 ? "" : "s")} out)";
            if (Away && Heading is int away) text += $", heading away to {systemName(away)}";
            return text;
        }
    }

    /// <summary>The unnamed count's band: its lowest number, so the key changes only when the episode's
    /// highest count crosses into a higher one (1, 2–4, 5–9, 10–19, 20+).</summary>
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

    /// <summary>Priority systems first, then the watch list, then other urgent news, then the nearest, then
    /// the newest: the order they are said in.</summary>
    internal static IReadOnlyList<AlarmMatch> Ordered(IReadOnlyList<AlarmMatch> matches) =>
        [.. matches
            .Select((m, i) => (m, i))
            .OrderByDescending(x => Rank(x.m))
            .ThenBy(x => JumpsOf(x.m) ?? int.MaxValue)
            .ThenByDescending(x => AtOf(x.m))
            .ThenBy(x => x.i)
            .Select(x => x.m)];

    /// <summary>How early a match is said: 3 in a priority system, 2 on the watch list's Always, 1 other
    /// urgent news (a kill within a jump, a pilot approaching), 0 the rest.</summary>
    internal static int Rank(AlarmMatch m) => Flag(m, "priority") ? 3 : Flag(m, "watch") ? 2 : m.Urgent ? 1 : 0;

    private static bool Flag(AlarmMatch m, string key) => m.Detail is { } d && d.TryGetValue(key, out var v) && v is true;

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
    /// <paramref name="distances"/> is given), and by light years when that is set; and the alarm's
    /// priority systems, wherever the characters are. The nearer figure wins when two origins reach the
    /// same system: two characters a jump apart, or a system listed twice. Empty when nothing resolves (a
    /// misspelt system, every character docked, no priority system), never everything.</para>
    /// <para>Beside the watched set: the systems out to the watch list's reach (where only its Always
    /// entries fire, so the outline leaves them out), and each origin's walk, so how far a system a sighting
    /// came from or is heading for is known (<see cref="IntelWatchedSystems.JumpsBetween"/>).</para>
    /// <para>The characters are where <paramref name="own"/> places them (the snapshot's own positions,
    /// the newer of ESI and the chat log); without it, where their last location poll did.</para>
    /// <para>Around characters, with the route switch on (the default), the systems still ahead on each watched
    /// character's last route from the Route Planner are watched too, however far; reading the routes moves each
    /// along to where its character is now, and clears one whose character has arrived.</para>
    /// </summary>
    public static async Task<IntelWatchedSystems> WatchedSystemsAsync(
        JsonElement config, SystemGraph graph, IDbContextFactory<AppDbContext> dbFactory,
        IntelDistanceService? distances = null, IReadOnlyList<OwnPosition>? own = null, CancellationToken ct = default,
        DateTimeOffset? now = null)
    {
        var around = ReadAround(config);
        var jumps  = ReadJumps(config, around);
        var ly     = ReadLy(config);
        var reach  = Math.Max(jumps, ReadWatchReach(config) ?? 0);

        var watched     = new HashSet<int>();
        var hopsTo      = new Dictionary<int, int>();      // from the nearest origin
        var via         = new HashSet<int>();              // the nearest way there goes through a hole
        var nearest     = new Dictionary<int, string>();   // the character that origin is, around characters
        var nearestId   = new Dictionary<int, long>();
        var nearestFrom = new Dictionary<int, int>();      // the origin's system
        var lyDistances = new Dictionary<int, double>();   // light years from the nearest origin
        var lyNearest   = new Dictionary<int, string>();   // the character that is, around characters
        var lyNearestId = new Dictionary<int, long>();
        var walks       = new Dictionary<int, IReadOnlyDictionary<int, IntelHop>>();
        var originWho   = new Dictionary<int, (string? Name, long Id)>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        DbConnection? conn = null;
        async Task<DbConnection> ConnectionAsync()
        {
            conn ??= db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
            return conn;
        }
        var positions = ly is null ? null : await PositionsAsync(await ConnectionAsync(), ct);

        void Place(int id, IntelHop hop, int origin)
        {
            if (hopsTo.TryGetValue(id, out var known)
                && (hop.Jumps > known || (hop.Jumps == known && (hop.ViaWormhole || !via.Contains(id))))) return;
            hopsTo[id] = hop.Jumps;
            nearestFrom[id] = origin;
            if (hop.ViaWormhole) via.Add(id); else via.Remove(id);
            if (originWho.TryGetValue(origin, out var who) && who.Name is not null) { nearest[id] = who.Name; nearestId[id] = who.Id; }
        }

        async Task SpreadAsync(int origin, string? who, long whoId)
        {
            originWho.TryAdd(origin, (who, whoId));
            if (!walks.TryGetValue(origin, out var walk))
            {
                // The whole walk from the distance service (it has no cap); over the stargates alone, a few jumps
                // past the reach, for where a sighting came from.
                walk = distances is not null
                    ? await distances.JumpsFromAsync(origin, IntelGraphMode.Threat, null, ct)
                    : (await graph.DistancesWithinAsync(origin, reach + ApproachMargin, ct))
                        .ToDictionary(kv => kv.Key, kv => new IntelHop(kv.Value, false));
                walks[origin] = walk;
            }
            foreach (var (id, hop) in walk)
            {
                if (hop.Jumps > reach) continue;
                if (hop.Jumps <= jumps) watched.Add(id);
                Place(id, hop, origin);
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

        // Within the watch list's reach: every system placed so far (each is within it).
        var watchReach = new HashSet<int>(hopsTo.Keys);

        // ⚠️ A system only the light-year range caught keeps its light years: it is said, played and judged
        // (whose standings) by them, as when the reach is no wider than the range. The walk placed it by jumps
        // only because the watch list's reach goes farther; without this the same system would be said in jumps
        // or light years depending on that reach.
        foreach (var id in lyDistances.Keys)
        {
            if (!hopsTo.TryGetValue(id, out var hops) || hops <= jumps) continue;
            hopsTo.Remove(id);
            nearest.Remove(id);
            nearestId.Remove(id);
            nearestFrom.Remove(id);
            via.Remove(id);
        }

        // The priority systems, watched wherever the characters are, at their distance from the nearest
        // origin when any walk reaches them.
        var priority = new HashSet<int>();
        var priorityNames = ReadList(config, "priority_systems");
        if (priorityNames.Count > 0)
            foreach (var id in await ResolveSystemsAsync(await ConnectionAsync(), priorityNames, ct))
            {
                priority.Add(id);
                watched.Add(id);
                if (hopsTo.ContainsKey(id)) continue;
                foreach (var (origin, walk) in walks)
                    if (walk.TryGetValue(id, out var hop)) Place(id, hop, origin);
            }

        // The routes ahead, around characters: every system still to come on the last route the Route Planner sent
        // for a watched character (online, docked or not: a route is flown from a dock), however far. A system no
        // range reaches is placed by its jumps along the route and judged by that character's standings; where the
        // nearest way there is not the route, where it came from or is heading is not measured (no origin's walk).
        var route = new Dictionary<int, IntelRouteStep>();
        var routeHere = new Dictionary<int, IntelRouteStep>();
        if (around != AroundSystems && ReadBool(config, "route", true))
        {
            var flying = own is not null
                ? OriginsFrom(own, AroundCharacters, ReadList(config, "characters"))
                : await OriginsAsync(db, AroundCharacters, ReadList(config, "characters"), ct);
            if (flying.Count > 0)
            {
                var names = flying.ToDictionary(f => f.CharacterId, f => f.Name);
                var at = flying.ToDictionary(f => f.CharacterId, f => f.SystemId);
                foreach (var r in await CharacterRouteStore.ReadAsync(db, at, now ?? DateTimeOffset.UtcNow, ct))
                {
                    if (!names.TryGetValue(r.CharacterId, out var name)) continue;
                    foreach (var (id, hops) in r.Ahead) KeepNewest(route, id, new IntelRouteStep(r.CharacterId, name, hops, r.SetAt));
                    KeepNewest(routeHere, r.Here, new IntelRouteStep(r.CharacterId, name, 0, r.SetAt, r.Next));
                }
            }
            foreach (var (id, step) in route)
            {
                watched.Add(id);
                if (hopsTo.ContainsKey(id) || lyDistances.ContainsKey(id)) continue;
                hopsTo[id] = step.Jumps;
                nearest[id] = step.Name;
                nearestId[id] = step.CharacterId;
            }
        }

        return new IntelWatchedSystems(watched, hopsTo, nearest, lyDistances, lyNearest)
        {
            Route                      = route,
            RouteHere                  = routeHere,
            NearestCharacter           = nearestId,
            LightYearsNearestCharacter = lyNearestId,
            ViaWormhole                = via,
            Priority                   = priority,
            WatchReach                 = watchReach,
            NearestOrigin              = nearestFrom,
            Walks                      = walks,
        };
    }

    /// <summary>Two routes through one system: the newest is the one said (a route just sent through a known camp
    /// says it again), then the one it is nearer on.</summary>
    private static void KeepNewest(Dictionary<int, IntelRouteStep> into, int systemId, IntelRouteStep step)
    {
        if (into.TryGetValue(systemId, out var had)
            && (had.SetAt > step.SetAt || (had.SetAt == step.SetAt && had.Jumps <= step.Jumps))) return;
        into[systemId] = step;
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
    /// now; then which way they are going; then what else was said; then the hulls; then the names.
    /// Never who reported it. A priority system first, said as one, then the watch list's, said as
    /// such; then other urgent news; then newest.
    /// </summary>
    public string? Announcement(JsonElement config, IReadOnlyList<AlarmMatch> matches)
        => ComposeAnnouncement(matches);

    internal static string? ComposeAnnouncement(IReadOnlyList<AlarmMatch> matches)
    {
        var said = matches.Where(m => m.Detail is not null && !m.Silent).ToList();
        if (said.Count == 0) return null;

        var items = new List<(int Rank, DateTime At, string Text)>();

        foreach (var k in said.Where(m => KindOf(m) == KindKill))
        {
            var d      = k.Detail!;
            var system = Str(d, "system") is { Length: > 0 } s ? s : AlarmsText.UnknownSystem;
            var victim = Str(d, "victim_hull") is { Length: > 0 } v ? v : AlarmsText.HullShip;
            var hulls  = Names(d, "hulls").ToList();
            var by     = hulls.Count > 0 ? AlarmWords.List(hulls) : AlarmsText.IntelSaidKillHostiles;
            var where  = OnRoute(d) ?? Where(JumpsOf(k), d.TryGetValue("ly", out var l) && l is double far ? far : null, Str(d, "near"), Flag(k, "via"));
            var rank   = Rank(k);
            items.Add((rank, AtOf(k), Marked(rank, where is null
                ? string.Format(AlarmsText.IntelSaidKill, system, victim, by)
                : string.Format(AlarmsText.IntelSaidKillAt, system, where, victim, by))));
        }

        // A gate camp as one: where and on which gate, then how many kills over how long, then the hulls.
        foreach (var k in said.Where(m => KindOf(m) == KindCamp))
        {
            var d      = k.Detail!;
            var system = Str(d, "system") is { Length: > 0 } s ? s : AlarmsText.UnknownSystem;
            var gate   = Str(d, "gate") is { Length: > 0 } gs ? gs : AlarmsText.UnknownSystem;
            var where  = OnRoute(d) ?? Where(JumpsOf(k), d.TryGetValue("ly", out var l) && l is double far ? far : null, Str(d, "near"), Flag(k, "via"));
            var kills  = d.TryGetValue("kills", out var n) && n is int kc ? kc : 0;
            var span   = d.TryGetValue("minutes", out var mn) && mn is int minutes ? minutes : 1;
            var hulls  = Names(d, "hulls").ToList();
            var rank   = Rank(k);
            var sb = new System.Text.StringBuilder(where is null
                ? string.Format(AlarmsText.IntelSaidCamp, system, gate)
                : string.Format(AlarmsText.IntelSaidCampAt, system, gate, where));
            if (kills > 0)
                sb.Append(' ').Append(Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidCampKillsOther), kills,
                    Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidMinutesOther), span)));
            if (hulls.Count > 0)
                sb.Append(' ').Append(string.Format(AlarmsText.IntelSaidFlying,
                    hulls.Count == 1 ? AlarmWords.Hull(hulls[0]) : AlarmWords.List(hulls)));
            items.Add((rank, AtOf(k), Marked(rank, sb.ToString())));
        }

        foreach (var g in said.Where(m => KindOf(m) is not (KindKill or KindCamp))
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
            var where  = list.Select(m => OnRoute(m.Detail!)).FirstOrDefault(w => w is not null)
                      ?? Where(jumps, ly, near, list.Any(m => Flag(m, "via")));
            var facts  = IntelDisplay.Facts(flags, gate);
            var rank   = list.Max(Rank);

            var sb = new System.Text.StringBuilder();
            if (count > 0)
            {
                sb.Append(where is null
                    ? Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidReportedOther), count, system)
                    : Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidReportedAtOther), count, system, where));
                Way(sb, list, near);
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
                Way(sb, list, near);
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

            items.Add((rank, list.Max(AtOf), Marked(rank, sb.ToString())));
        }

        return string.Join(" ", items
            .Select((x, i) => (x, i))
            .OrderByDescending(x => x.x.Rank).ThenByDescending(x => x.x.At).ThenBy(x => x.i)
            .Select(x => x.x.Text));

        // A priority system's news said as one, the watch list's as its.
        static string Marked(int rank, string text) => rank switch
        {
            3 => string.Format(AlarmsText.IntelSaidPriority, text),
            2 => string.Format(AlarmsText.IntelSaidWatchList, text),
            _ => text,
        };

        // Which way they are going, after where they are: coming nearer (by a heading toward the character, else
        // by where they were seen before), then a heading away.
        static void Way(System.Text.StringBuilder sb, List<AlarmMatch> list, string? near)
        {
            var toward = list.FirstOrDefault(m => Flag(m, "toward") && Str(m.Detail, "heading").Length > 0);
            var closer = list.Where(m => Flag(m, "approaching") && m.Detail!.TryGetValue("was_jumps", out var w) && w is int)
                             .Select(m => (int)m.Detail!["was_jumps"]!).DefaultIfEmpty(-1).Max();
            if (toward is not null)
            {
                var then = toward.Detail!.TryGetValue("heading_jumps", out var h) && h is int hj ? Where(hj, null, near) : null;
                sb.Append(' ').Append(then is null
                    ? string.Format(AlarmsText.IntelSaidApproachingTo, Str(toward.Detail, "heading"))
                    : string.Format(AlarmsText.IntelSaidApproachingToAt, Str(toward.Detail, "heading"), then));
            }
            else if (closer >= 0 && Where(closer, null, near) is { } was)
                sb.Append(' ').Append(string.Format(AlarmsText.IntelSaidApproachingWas, was));

            var away = list.FirstOrDefault(m => Flag(m, "away") && Str(m.Detail, "heading").Length > 0);
            if (away is not null && toward is null)
                sb.Append(' ').Append(string.Format(AlarmsText.IntelSaidHeadingAway, Str(away.Detail, "heading")));
        }

        static IEnumerable<string> Names(IReadOnlyDictionary<string, object?> d, string key)
            => d.TryGetValue(key, out var v) && v is IEnumerable<string> list ? list.Where(n => !string.IsNullOrWhiteSpace(n)) : [];
    }

    /// <summary>How far along a watched character's route, for a system still ahead on it: "3 jumps ahead on Alpha's
    /// route"; null for one on no route.</summary>
    private static string? OnRoute(IReadOnlyDictionary<string, object?> d) =>
        d.TryGetValue("route_jumps", out var j) && j is int hops && Str(d, "route_of") is { Length: > 0 } who
            ? Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.IntelSaidOnRouteOther), hops, who)
            : null;

    /// <summary>How far out, in the interface language: "2 jumps from Alpha", "where Alpha is",
    /// "3 jumps out", "4.2 light years from Alpha"; null when there is no range to say it by. A way that
    /// goes through Thera or Turnur says so: "3 jumps from Alpha, through Thera or Turnur".</summary>
    private static string? Where(int? jumps, double? ly, string? near, bool via = false)
    {
        near = string.IsNullOrEmpty(near) ? null : near;
        if (jumps is { } hops)
            return via && hops > 0 ? string.Format(AlarmsText.IntelSaidViaWormhole, Where(hops, null, near)) : near is not null
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
        var far = HowFarAround(systemId, jumps, zone);
        // On a route ahead, said along the route: that is how the character will come to it.
        return zone.Route.TryGetValue(systemId, out var step)
            ? far with { From = $" ({step.Jumps} jump{(step.Jumps == 1 ? "" : "s")} ahead on {step.Name}'s route)" }
            : far;
    }

    private static (string? Near, int? Jumps, double? Ly, string From) HowFarAround(int systemId, int jumps, IntelWatchedSystems zone)
    {
        var near = zone.Nearest.GetValueOrDefault(systemId);
        if (zone.Jumps.TryGetValue(systemId, out var hops) && (jumps > 0 || near is not null || hops > 0))
        {
            var through = hops > 0 && zone.ViaWormhole.Contains(systemId) ? " through Thera or Turnur" : "";
            return (near, hops, null, near is null ? "" : hops == 0 ? $" (where {near} is)"
                : $" ({hops} jump{(hops == 1 ? "" : "s")} from {near}{through})");
        }
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
/// the alarm is around characters. <see cref="Jumps"/> holds the systems within the jump range, the
/// priority systems a walk reaches, and those out to the watch list's reach — but never one only the
/// light-year range caught, which is said and judged by its <see cref="LightYears"/>.</summary>
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

    /// <summary>The alarm's priority systems: watched (in <see cref="Systems"/>) wherever the characters are.</summary>
    public IReadOnlySet<int> Priority { get; init; } = new HashSet<int>();

    /// <summary>Every system within the watch list's reach (the range's own included): where the watch
    /// list's Always entries fire. Not part of the outline: nothing else fires there.</summary>
    public IReadOnlySet<int> WatchReach { get; init; } = new HashSet<int>();

    /// <summary>The system of the origin each placed system is nearest, by jumps.</summary>
    public IReadOnlyDictionary<int, int> NearestOrigin { get; init; } = new Dictionary<int, int>();

    /// <summary>The systems still ahead on a watched character's route (the Route Planner's last sent), with how many
    /// jumps along it and whose it is. Watched (in <see cref="Systems"/>) however far.</summary>
    public IReadOnlyDictionary<int, IntelRouteStep> Route { get; init; } = new Dictionary<int, IntelRouteStep>();

    /// <summary>The step each routed character is on, with the system the route leaves for: a gate camp on that gate
    /// is on the route.</summary>
    public IReadOnlyDictionary<int, IntelRouteStep> RouteHere { get; init; } = new Dictionary<int, IntelRouteStep>();

    /// <summary>Each origin's walk, by its system: every system it reaches (with the distance service, all of them).</summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<int, IntelHop>> Walks { get; init; } =
        new Dictionary<int, IReadOnlyDictionary<int, IntelHop>>();

    /// <summary>How far <paramref name="other"/> is from the origin <paramref name="system"/> is nearest: where a
    /// sighting there came from, or is heading, measured from the same character. Null when not known.</summary>
    public int? JumpsBetween(int system, int other) =>
        NearestOrigin.TryGetValue(system, out var origin) && Walks.TryGetValue(origin, out var walk)
        && walk.TryGetValue(other, out var hop) ? hop.Jumps : null;

    public static readonly IntelWatchedSystems None = new(new HashSet<int>(), new Dictionary<int, int>(),
        new Dictionary<int, string>(), new Dictionary<int, double>(), new Dictionary<int, string>());
}
