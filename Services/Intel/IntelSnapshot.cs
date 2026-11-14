using System.Globalization;
using EveConsole.Models;
using EveConsole.Monitoring;

namespace EveConsole.Services.Intel;

/// <summary>What a system looks like from intel right now, for one viewer.</summary>
public enum IntelSystemState
{
    /// <summary>Someone is there and was seen.</summary>
    Hostile,
    /// <summary>Someone is there, but every sighting was "no visual".</summary>
    NoVisual,
    /// <summary>Nobody counted, but something was said: a camp, bubbles, a cyno.</summary>
    FlagsOnly,
    /// <summary>Called clear, and nothing newer.</summary>
    Clear,
}

public enum IntelEventKind { Report, FollowUp, Clear, LocalPaste, Dscan, Kill, CampFormed }

/// <summary>Where one of the user's characters is, and how that is known.</summary>
public enum OwnPositionSource { Esi, ChatLog }

/// <summary>
/// A pilot placed in a system: their last sighting from any source, with how long it lasts.
/// Expiry is the reader's to judge, at its own "now": the panels fade every second, the snapshot
/// only every five.
/// </summary>
public sealed record PilotIntel(
    long CharacterId, string? Name, int SystemId, DateTimeOffset At, int Source, TimeSpan Lifetime,
    bool NoVisual, bool ConfirmedByKill, int? ShipTypeId, string? Ship,
    long CorporationId, long AllianceId, int? ReportId, int? KillMailId,
    int? PrevSystemId, DateTimeOffset? PrevSeenAt, DateTimeOffset InSystemSince, int? HeadingSystemId)
{
    public DateTimeOffset ExpiresAt => At + Lifetime;
    public bool FromKill => Source is IntelLastSeenSource.KillAttacker or IntelLastSeenSource.KillVictim;
}

/// <summary>Pilots a report counted but did not name, in the newest report for the system.</summary>
/// <param name="EpisodeStart">When this run of counts in the system began: reports one after
/// another, each within the unnamed lifetime of the last, with no clear between. Kept by the
/// <see cref="IntelEpisodeRegister"/> once its first reports leave the window.</param>
/// <param name="Peak">The most this run has counted at once (also kept by the register): the intel
/// alarm's band, so a count that falls is not news.</param>
public sealed record UnnamedIntel(
    int Count, string? Ships, bool NoVisual, DateTimeOffset At, TimeSpan Lifetime, int ReportId, int Source,
    DateTimeOffset EpisodeStart, int Peak)
{
    public DateTimeOffset ExpiresAt => At + Lifetime;
}

/// <summary>Something said about a system besides who: a camp, bubbles, a cyno, a spike.</summary>
/// <param name="EpisodeStart">When this run of the flag began, kept by the <see cref="IntelEpisodeRegister"/>
/// once its first reports leave the window.</param>
public sealed record IntelFact(
    IntelRules.IntelFlags Flag, DateTimeOffset At, TimeSpan Lifetime, int ReportId, DateTimeOffset EpisodeStart)
{
    public DateTimeOffset ExpiresAt => At + Lifetime;
}

public sealed record ClearIntel(DateTimeOffset At, TimeSpan Lifetime, int ReportId)
{
    public DateTimeOffset ExpiresAt => At + Lifetime;
}

/// <summary>A report saying where someone came from: a solid arrow on the map.</summary>
public sealed record IntelMove(int FromSystemId, int ToSystemId, DateTimeOffset At, int ReportId);

/// <summary>
/// Everything intel holds about one system. Raw: nothing is filtered by standings or distance,
/// and nothing expired by the reader's clock (the lens does both).
/// </summary>
public sealed record SystemIntel(
    int SystemId,
    IReadOnlyList<long> PilotIds,
    UnnamedIntel? Unnamed,
    IReadOnlyList<IntelFact> Facts,
    string? Gate,
    ClearIntel? Clear,
    IReadOnlyList<GateCamp> Camps,
    IReadOnlyList<IntelMove> Moves,
    int? HeadingSystemId);

/// <summary>A pilot named on a feed event, with what the event said they flew.</summary>
public sealed record IntelEventPilot(long CharacterId, string? Name, int? ShipTypeId, string? Ship,
                                     long CorporationId, long AllianceId);

/// <summary>One line of the feed: a report, a clear, a paste, a kill, or a camp forming.</summary>
/// <param name="SourceKey">What the sources filter matches: the channel's name for a chat report,
/// <see cref="IntelSnapshot.SourceKills"/> or <see cref="IntelSnapshot.SourcePaste"/>.</param>
/// <param name="Count">Pilots counted but not named.</param>
/// <param name="NearGate">For a kill: the gate it was on, when within the camp distance of one.</param>
public sealed record IntelEvent(
    string Key, IntelEventKind Kind, DateTimeOffset At, TimeSpan Lifetime, int SystemId, string SourceKey,
    IReadOnlyList<IntelEventPilot> Pilots, int Count, int Flags, bool NoVisual, bool Superseded,
    int? ReportId = null, int? ParentReportId = null, string? Channel = null, string? Reporter = null,
    long? ReporterId = null, string? Message = null, string? Gate = null, string? Ships = null,
    int? FromSystemId = null, int? HeadingSystemId = null,
    int? KillMailId = null, long VictimId = 0, string? VictimName = null, int VictimShipTypeId = 0,
    string? VictimShip = null, long VictimCorporationId = 0, long VictimAllianceId = 0, int AttackerCount = 0,
    IReadOnlyList<int>? TopHullTypeIds = null, IntelGateNear? NearGate = null, string? CampKey = null,
    IReadOnlyList<IntelEcho>? Echoes = null)
{
    public DateTimeOffset ExpiresAt => At + Lifetime;

    /// <summary>The other places this was heard: relayed copies of a report, or relays of a clear. Never information of
    /// their own; a line shows them as "×N" with the channels in its tooltip.</summary>
    public IReadOnlyList<IntelEcho> EchoList => Echoes ?? [];

    /// <summary>Whether it was heard in one of <paramref name="sources"/>, itself or by a relay.</summary>
    public bool HeardIn(IReadOnlySet<string> sources) =>
        sources.Contains(SourceKey) || EchoList.Any(e => sources.Contains(e.Channel));
}

/// <summary>
/// A line that repeated an intel line from another channel within the copy window: the same report relayed. Kept only
/// to say where else it was heard.
/// </summary>
public sealed record IntelEcho(int ReportId, string Channel, string Reporter, long? ReporterId, DateTimeOffset At);

/// <summary>
/// A line in an intel channel that gave no intel: talk, a question, or a follow-up with nothing to
/// attach to. Not intel and never stored as such; the tool's Feed can show it, muted, for context.
/// </summary>
public sealed record IntelChatter(int ChatMessageId, DateTimeOffset At, string Channel, string Sender, string Message);

/// <summary>One of the user's characters and where it is, the newer of ESI's location and the chat
/// log's "Channel changed to Local" line.</summary>
/// <param name="SystemSince">When it came into this system, as near as is known.</param>
public sealed record OwnPosition(
    long CharacterId, string Name, int SystemId, DateTimeOffset SystemSince, OwnPositionSource Source,
    long CorporationId, long AllianceId, int? ShipTypeId = null, string? Hull = null, string? ShipName = null,
    bool Docked = false, string? Place = null);

/// <summary>
/// The one shared, viewer-independent picture of who is where now, built every few seconds from the
/// shared database (<see cref="IntelSnapshotService"/>). Immutable: a reader takes
/// <see cref="IntelSnapshotService.Current"/> and never waits.
/// </summary>
public sealed record IntelSnapshot(
    DateTimeOffset At, long Version, TimeSpan Window, IntelSettingsValues Settings,
    IReadOnlyDictionary<int, SystemIntel> Systems,
    IReadOnlyDictionary<long, PilotIntel> Pilots,
    IReadOnlyList<OwnPosition> Own,
    IReadOnlyDictionary<int, IReadOnlyList<OfficerSighting>> Officers,
    IReadOnlyList<IntelEvent> Feed,
    IReadOnlyList<GateCamp> Camps,
    IntelFeedStatus FeedStatus,
    IntelStandings Standings)
{
    /// <summary>The sources filter's names for the two sources that are not channels.</summary>
    public const string SourceKills = "Kills";
    public const string SourcePaste = "Paste";

    /// <summary>Every system's English name, by id, read once with the stargates: a view shows one
    /// in the interface language through SdeNames.SolarSystem, and names a kill's gate by the system
    /// it leads to.</summary>
    public IReadOnlyDictionary<int, string> SystemNames { get; init; } = new Dictionary<int, string>();

    /// <summary>The intel channels' lines in the window that gave no intel, newest first.</summary>
    public IReadOnlyList<IntelChatter> Chatter { get; init; } = [];

    /// <summary>The intel channels set, chat and rooms: what the sources filter lists besides the
    /// channels the feed happens to hold.</summary>
    public IReadOnlyList<string> Channels { get; init; } = [];

    /// <summary>How each channel is shown ("EVE Chat - …", "&lt;server&gt; - …"), read every minute with the settings.</summary>
    public IntelChannelLabels ChannelLabels { get; init; } = IntelChannelLabels.Empty;

    /// <summary>A channel as shown; see <see cref="IntelChannelLabels"/>. Blank stays blank.</summary>
    public string ChannelLabel(string? channel) => ChannelLabels.Label(channel);

    /// <summary>The watch list as last read (every minute, and at once after a change on this PC):
    /// what the lens marks rows Always (bold) or Ignored (dimmed) by.</summary>
    public IntelWatchSet Watch { get; init; } = IntelWatchSet.Empty;

    /// <summary>The group of each hull in the snapshot, read only while the watch list has a group on it.</summary>
    public IReadOnlyDictionary<int, int> TypeGroups { get; init; } = new Dictionary<int, int>();

    /// <summary>A hull's group, when known.</summary>
    public int? GroupOf(int? typeId) => typeId is int t && TypeGroups.TryGetValue(t, out var g) ? g : null;

    /// <summary>A pilot's watch-list mark.</summary>
    public IntelWatchMark WatchOf(long characterId, long corporationId, long allianceId, int? shipTypeId) =>
        Watch.Pilot(characterId, corporationId, allianceId, shipTypeId, GroupOf(shipTypeId));

    /// <summary>Every published hull's type id by its English name, as reports store hulls said with
    /// nobody named ("8× Muninn") and a d-scan's paste stores its ships: a view shows each with its icon.</summary>
    public IReadOnlyDictionary<string, int> HullIds { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A hull's type id from its English name; 0 for a name that is no hull (a class such as
    /// "Interdictor", or words the parser kept).</summary>
    public int HullId(string? englishName) =>
        englishName is { Length: > 0 } n && HullIds.TryGetValue(n.Trim(), out var id) ? id : 0;

    /// <summary>A system's name in the interface language; its id when the names are not loaded.</summary>
    public string SystemName(int systemId) =>
        SystemNames.TryGetValue(systemId, out var english)
            ? EveConsole.Localization.SdeNames.SolarSystem(systemId, english)
            : systemId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Names from the name caches: pilots the rows lack, and the corporations and alliances of the
    /// pilots in the snapshot, for who is where.</summary>
    public IReadOnlyDictionary<long, string> Names { get; init; } = new Dictionary<long, string>();

    /// <summary>A corporation's, alliance's or pilot's name, when the caches have it.</summary>
    public string? EntityName(long id) => id > 0 && Names.TryGetValue(id, out var name) ? name : null;

    /// <summary>Each system's region, by its id and English name, read once with the stargates.</summary>
    public IReadOnlyDictionary<int, (int RegionId, string Name)> Regions { get; init; } = new Dictionary<int, (int, string)>();

    /// <summary>A system's region in the interface language; null when it is not known.</summary>
    public string? RegionName(int systemId) =>
        Regions.TryGetValue(systemId, out var region) ? EveConsole.Localization.SdeNames.Region(region.RegionId, region.Name) : null;

    public static readonly IntelSnapshot Empty = new(
        DateTimeOffset.MinValue, 0, IntelSettings.MinimumWindow, new IntelSettingsValues(),
        new Dictionary<int, SystemIntel>(), new Dictionary<long, PilotIntel>(), [],
        new Dictionary<int, IReadOnlyList<OfficerSighting>>(), [], [],
        IntelFeedStatus.From(new IntelSettingsValues()), IntelStandings.Empty);
}

/// <summary>Everything the snapshot is built from, as read from the database.</summary>
public sealed record IntelSnapshotInput
{
    public required DateTimeOffset Now { get; init; }
    public required IntelSettingsValues Settings { get; init; }
    public IReadOnlyList<IntelReport> Reports { get; init; } = [];
    public IReadOnlyList<IntelReportCharacter> ReportPilots { get; init; } = [];
    public IReadOnlyCollection<IntelLastSeen> LastSeen { get; init; } = [];
    public IReadOnlyList<IntelKill> Kills { get; init; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<IntelGate>> Gates { get; init; } = new Dictionary<int, IReadOnlyList<IntelGate>>();
    public IntelSdeGroups Groups { get; init; } = IntelSdeGroups.Empty;
    public IReadOnlyList<OwnPosition> Own { get; init; } = [];

    /// <summary>Pilot names the reports and last-seen rows lack, from the name caches.</summary>
    public IReadOnlyDictionary<long, string> Names { get; init; } = new Dictionary<long, string>();

    /// <summary>Hull names in the interface language, by type.</summary>
    public IReadOnlyDictionary<int, string> TypeNames { get; init; } = new Dictionary<int, string>();

    /// <summary>System names in English, for matching a report's "on the X gate" to a gate.</summary>
    public IReadOnlyDictionary<int, string> SystemNames { get; init; } = new Dictionary<int, string>();

    /// <summary>Published hulls' type ids by English name (case ignored).</summary>
    public IReadOnlyDictionary<string, int> HullIds { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each system's region, by id and English name.</summary>
    public IReadOnlyDictionary<int, (int RegionId, string Name)> Regions { get; init; } = new Dictionary<int, (int, string)>();

    /// <summary>Pulled affiliations, for pilots no last-seen row gives one.</summary>
    public IReadOnlyDictionary<long, (long Corporation, long Alliance)> Affiliations { get; init; } =
        new Dictionary<long, (long, long)>();

    public IReadOnlyDictionary<int, IReadOnlyList<OfficerSighting>> Officers { get; init; } =
        new Dictionary<int, IReadOnlyList<OfficerSighting>>();

    public IntelStandings Standings { get; init; } = IntelStandings.Empty;

    public IReadOnlyList<IntelChatter> Chatter { get; init; } = [];
    public IReadOnlyList<string> Channels { get; init; } = [];
    public IntelChannelLabels ChannelLabels { get; init; } = IntelChannelLabels.Empty;

    public IntelWatchSet Watch { get; init; } = IntelWatchSet.Empty;
    public IReadOnlyDictionary<int, int> TypeGroups { get; init; } = new Dictionary<int, int>();
}

/// <summary>
/// Builds the snapshot from what was read: pure, so tools/IntelCheck can feed it rows by hand.
///
/// <para>The rules:</para>
/// <list type="bullet">
/// <item>A pilot is where their last sighting put them (<see cref="IntelLastSeen"/>), for the named
/// lifetime from a report or a Local paste, the attacker lifetime from a kill, the victim lifetime
/// from a loss. A clear, or a Local paste of the system, newer than the sighting takes them out.</item>
/// <item>The unnamed count is the newest report's for the system: its count less the pilots it named
/// (a d-scan: less the pilots placed there). A newer report elsewhere saying they came FROM this
/// system takes it away: they moved.</item>
/// <item>The flags are every live report's since the last clear, each with its own lifetime: a camp and
/// bubbles the camp lifetime, a cyno the cyno lifetime, the rest the unnamed lifetime.</item>
/// <item>The feed keeps every report and clear in the window and every kill by players, uncapped:
/// a view filters by distance and source first, and caps after. Capped here, a busy kill feed from
/// across New Eden would push every local report out before any distance filter ran.</item>
/// </list>
/// </summary>
public static class IntelSnapshotBuilder
{
    private static readonly IntelRules.IntelFlags[] AllFlags =
        [.. Enum.GetValues<IntelRules.IntelFlags>().Where(f => f != IntelRules.IntelFlags.None)];

    /// <summary>
    /// Leaves out each clear that relays an earlier one (the same system, from another channel, within the copy window):
    /// the clear is read once, at its first time. Stamped with the relay's later time it would also wipe every sighting
    /// called between the two, and the Feed would show it twice.
    /// </summary>
    /// <returns>Each clear left out, by the id of the clear it relays: the Feed says where else the clear was heard.</returns>
    private static List<(int KeptId, IntelReport Relay, DateTimeOffset At)> DropRelayedClears(List<(IntelReport R, DateTimeOffset At)> reports)
    {
        var relayed = new List<(int KeptId, IntelReport Relay, DateTimeOffset At)>();
        foreach (var system in reports.Where(x => x.R.Kind == IntelReportKind.Clear).GroupBy(x => x.R.SystemId))
        {
            if (system.Count() < 2) continue;
            var kept = new List<(IntelReport R, DateTimeOffset At)>();
            foreach (var x in system.OrderBy(x => x.At).ThenBy(x => x.R.Id))
                if (kept.FirstOrDefault(y => EveConsole.Monitoring.IntelDuplicates.RelaysClear(x.R, x.At, y.R, y.At)) is { R: not null } first)
                    relayed.Add((first.R.Id, x.R, x.At));
                else kept.Add(x);
        }
        if (relayed.Count > 0)
        {
            var gone = relayed.Select(x => x.Relay).ToHashSet(ReferenceEqualityComparer.Instance);
            reports.RemoveAll(x => gone.Contains(x.R));
        }
        return relayed;
    }

    /// <summary>
    /// Where else each report was heard: its relayed copies (<see cref="IntelReportKind.Copy"/>, read only for this) and
    /// the relays of a clear, by the id of the report they repeat. Never a channel the report itself was said in.
    /// </summary>
    private static Dictionary<int, IReadOnlyList<IntelEcho>> Echoes(
        IEnumerable<(IntelReport R, DateTimeOffset At)> copies, IEnumerable<(int KeptId, IntelReport Relay, DateTimeOffset At)> relayedClears,
        IReadOnlyDictionary<int, (IntelReport R, DateTimeOffset At)> reportById)
    {
        var all = copies.Where(x => x.R.ParentReportId is int).Select(x => (Of: x.R.ParentReportId!.Value, x.R, x.At))
            .Concat(relayedClears.Select(x => (Of: x.KeptId, R: x.Relay, x.At)));
        var echoes = new Dictionary<int, IReadOnlyList<IntelEcho>>();
        foreach (var group in all.GroupBy(x => x.Of))
        {
            if (!reportById.TryGetValue(group.Key, out var original)) continue;
            var list = group
                .Where(x => !string.Equals(x.R.ChannelName, original.R.ChannelName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.At).ThenBy(x => x.R.Id)
                .Select(x => new IntelEcho(x.R.Id, x.R.ChannelName, x.R.ReporterName, x.R.ReporterCharacterId, x.At))
                .ToList();
            if (list.Count > 0) echoes[group.Key] = list;
        }
        return echoes;
    }

    /// <param name="episodes">What keeps a run of counts or of a flag going past the window's edge; without
    /// one, a run begins at its oldest report read back.</param>
    public static IntelSnapshot Build(IntelSnapshotInput input, IntelCampRegister register, long version,
                                      IntelEpisodeRegister? episodes = null)
    {
        var s   = input.Settings;
        var now = input.Now;
        var from = now - s.Window;

        // ── Reports, with their times read once ──────────────────────────────
        // ⚠️ A relayed copy is never a report here: read apart, only to say where else its original was heard.
        var reports = new List<(IntelReport R, DateTimeOffset At)>();
        var copies  = new List<(IntelReport R, DateTimeOffset At)>();
        foreach (var r in input.Reports)
            if (TryParse(r.ReportedAt, out var at) && at >= from)
                (r.Kind == IntelReportKind.Copy ? copies : reports).Add((r, at));
        var relayedClears = DropRelayedClears(reports);
        var reportById = reports.ToDictionary(x => x.R.Id);
        var echoes = Echoes(copies, relayedClears, reportById);
        var pilotsOf = input.ReportPilots.Where(p => reportById.ContainsKey(p.IntelReportId))
            .ToLookup(p => p.IntelReportId);

        var sightings = reports.Where(x => x.R.Kind == IntelReportKind.Sighting).ToList();
        var clears    = reports.Where(x => x.R.Kind == IntelReportKind.Clear).ToList();

        var lastClear = clears.GroupBy(x => x.R.SystemId).ToDictionary(g => g.Key, g => g.Max(x => x.At));
        // What takes a pilot out of a system: a clear, or a pasted Local that did not list them.
        var lastWipe = new Dictionary<int, DateTimeOffset>(lastClear);
        foreach (var x in sightings.Where(x => x.R.Source == IntelReportSource.LocalPaste))
            if (!lastWipe.TryGetValue(x.R.SystemId, out var w) || x.At > w) lastWipe[x.R.SystemId] = x.At;

        // ── Affiliations: the last-seen row's, else the pulled one ───────────
        var lastSeenById = input.LastSeen.GroupBy(r => r.CharacterId).ToDictionary(g => g.Key, g => g.First());
        (long Corp, long Alliance) Affiliation(long id) =>
            lastSeenById.TryGetValue(id, out var row) && (row.CorporationId is > 0 || row.AllianceId is > 0)
                ? (row.CorporationId ?? 0, row.AllianceId ?? 0)
                : input.Affiliations.TryGetValue(id, out var a) ? a : (0, 0);
        string? NameOf(long id, string? given) =>
            !string.IsNullOrWhiteSpace(given) ? given
            : lastSeenById.TryGetValue(id, out var row) && !string.IsNullOrWhiteSpace(row.Name) ? row.Name
            : input.Names.GetValueOrDefault(id);
        string? ShipOf(int? typeId, string? given) =>
            typeId is int t && input.TypeNames.TryGetValue(t, out var name) ? name : given;

        // ── Kills by player attackers ────────────────────────────────────────
        var kills = input.Kills.Where(k => k.At >= from && k.HasPlayerAttacker).ToList();
        var attackerTimes = new Dictionary<(long, int), List<DateTimeOffset>>();
        foreach (var k in kills)
            foreach (var a in k.Attackers.Where(a => a.CharacterId > 0))
            {
                var key = (a.CharacterId, k.SystemId);
                if (!attackerTimes.TryGetValue(key, out var list)) attackerTimes[key] = list = [];
                list.Add(k.At);
            }
        var reportTimes = new Dictionary<(long, int), List<DateTimeOffset>>();
        foreach (var x in sightings)
            foreach (var p in pilotsOf[x.R.Id])
            {
                var key = (p.CharacterId, x.R.SystemId);
                if (!reportTimes.TryGetValue(key, out var list)) reportTimes[key] = list = [];
                list.Add(x.At);
            }
        bool Near(Dictionary<(long, int), List<DateTimeOffset>> times, long pilot, int system, DateTimeOffset at) =>
            times.TryGetValue((pilot, system), out var list) && list.Any(t => (t - at).Duration() <= s.Named);

        // ── Pilots ───────────────────────────────────────────────────────────
        var pilots = new Dictionary<long, PilotIntel>();
        foreach (var row in input.LastSeen)
        {
            if (!TryParse(row.SeenAt, out var at) || at < from) continue;
            if (lastWipe.TryGetValue(row.SystemId, out var wipe) && wipe > at) continue;

            var (corp, alliance) = Affiliation(row.CharacterId);
            var fromKill = row.Source is IntelLastSeenSource.KillAttacker or IntelLastSeenSource.KillVictim;
            var confirmed = fromKill
                ? Near(reportTimes, row.CharacterId, row.SystemId, at)
                : Near(attackerTimes, row.CharacterId, row.SystemId, at);
            int? heading = row.ReportId is int rid && reportById.TryGetValue(rid, out var rep) ? rep.R.HeadingSystemId : null;

            pilots[row.CharacterId] = new PilotIntel(
                row.CharacterId, NameOf(row.CharacterId, row.Name), row.SystemId, at, row.Source,
                s.PilotLifetime(row.Source), row.NoVisual, confirmed, row.ShipTypeId, ShipOf(row.ShipTypeId, row.ShipName),
                corp, alliance, row.ReportId, row.KillMailId,
                row.PrevSystemId, TryParse(row.PrevSeenAt, out var prev) ? prev : null,
                TryParse(row.InSystemSince, out var since) ? since : at, heading);
        }

        // ── Per system: unnamed, facts, gate, heading, clear, moves ──────────
        var live = sightings
            .Where(x => !x.R.Obsolete && (!lastClear.TryGetValue(x.R.SystemId, out var c) || x.At > c))
            .GroupBy(x => x.R.SystemId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.At).ThenByDescending(x => x.R.Id).ToList());

        var movedFrom = sightings.Where(x => x.R.FromSystemId is > 0 && x.R.FromSystemId != x.R.SystemId)
            .GroupBy(x => x.R.FromSystemId!.Value).ToDictionary(g => g.Key, g => g.Max(x => x.At));
        var pilotsIn = pilots.Values.GroupBy(p => p.SystemId).ToDictionary(g => g.Key, g => g.Select(p => p.CharacterId).ToList());

        int CountOf(IntelReport r) =>
            r.Source == IntelReportSource.DscanPaste
                ? Math.Max(0, r.PlayerCount - (pilotsIn.GetValueOrDefault(r.SystemId)?.Count ?? 0))
                : Math.Max(0, r.PlayerCount - pilotsOf[r.Id].Count());

        var systems = new Dictionary<int, SystemIntel>();
        var systemIds = new HashSet<int>(pilotsIn.Keys);
        systemIds.UnionWith(live.Keys);
        systemIds.UnionWith(clears.Select(x => x.R.SystemId));
        systemIds.UnionWith(sightings.Where(x => x.R.FromSystemId is > 0).Select(x => x.R.SystemId));

        // ── Camps ────────────────────────────────────────────────────────────
        var clusters = IntelCampDetector.Detect(kills, input.Gates, input.Groups, s.CampGateKm, s.CampWindow, s.CampMinVictims);
        // The register also gives back a camp whose earlier kills have left the window, until the Camps tab
        // lets it go: the kills read back alone would drop it long before then.
        // A clear in the system after a camp's last kill ends the camp there and then: every view reads its end,
        // so the system line, the map and the alarms let it go, and the Camps tab shows it ended. A kill on
        // the gate after the clear makes it active again (user, 2026-11-13: a camp still showed under a clear).
        var camps = register.Apply(clusters, now, s.Camp, s.CampWindow, IntelLens.CampKept)
            .Select(c => c with { Reported = Reported(c) })
            .Select(c => lastClear.TryGetValue(c.SystemId, out var cleared) && cleared > c.LastKill && cleared < c.ExpiresAt
                ? c with { ExpiresAt = cleared } : c)
            .ToList();
        bool Reported(GateCamp c) =>
            live.TryGetValue(c.SystemId, out var list)
            && input.SystemNames.TryGetValue(c.GateSystemId, out var gateName)
            && list.Any(x => (x.R.Flags & (int)IntelRules.IntelFlags.Camp) != 0
                             && string.Equals(x.R.Gate?.Trim(), gateName, StringComparison.OrdinalIgnoreCase));
        var campsIn = camps.GroupBy(c => c.SystemId).ToDictionary(g => g.Key, g => (IReadOnlyList<GateCamp>)[.. g]);
        systemIds.UnionWith(campsIn.Keys);

        // ── Runs: each system's run of counts and of each flag, as far back as the window reads ──
        // The register carries a run's start (and a count's peak) on once its first reports leave the window.
        var runs = new List<IntelEpisodeRun>();
        foreach (var (id, reps) in live)
        {
            var (newest, at) = reps[0];
            var count = CountOf(newest);
            if (count > 0)
            {
                var (start, peak) = (at, count);
                for (var i = 1; i < reps.Count; i++)
                {
                    var n = CountOf(reps[i].R);
                    if (n <= 0 || start - reps[i].At > s.Unnamed) break;
                    start = reps[i].At;
                    if (n > peak) peak = n;
                }
                runs.Add(new IntelEpisodeRun(id, IntelEpisodeRegister.Counts, start, at, peak));
            }
            foreach (var flag in AllFlags)
            {
                var with = reps.Where(x => (x.R.Flags & (int)flag) != 0).ToList();
                if (with.Count == 0) continue;
                var life = s.FlagLifetime(flag);
                var start = with[0].At;
                for (var i = 1; i < with.Count && start - with[i].At <= life; i++) start = with[i].At;
                runs.Add(new IntelEpisodeRun(id, (int)flag, start, with[0].At, 0));
            }
        }
        var carried = (episodes ?? new IntelEpisodeRegister()).Apply(runs, from);

        foreach (var id in systemIds)
        {
            live.TryGetValue(id, out var reps);
            reps ??= [];

            UnnamedIntel? unnamed = null;
            if (reps.Count > 0)
            {
                var (newest, at) = reps[0];
                var count = CountOf(newest);
                var moved = movedFrom.TryGetValue(id, out var left) && left > at;
                if (count > 0 && !moved && carried.TryGetValue((id, IntelEpisodeRegister.Counts), out var run))
                    unnamed = new UnnamedIntel(count, newest.Ships, newest.NoVisual, at, s.Unnamed, newest.Id, newest.Source,
                                               run.Start, run.Peak);
            }

            var facts = new List<IntelFact>();
            foreach (var flag in AllFlags)
            {
                var newest = reps.FirstOrDefault(x => (x.R.Flags & (int)flag) != 0);
                if (newest.R is null || !carried.TryGetValue((id, (int)flag), out var run)) continue;
                facts.Add(new IntelFact(flag, newest.At, s.FlagLifetime(flag), newest.R.Id, run.Start));
            }

            var clear = clears.Where(x => x.R.SystemId == id).OrderByDescending(x => x.At).ThenByDescending(x => x.R.Id)
                .Select(x => new ClearIntel(x.At, s.Clear, x.R.Id)).FirstOrDefault();
            var moves = sightings.Where(x => x.R.SystemId == id && x.R.FromSystemId is > 0 && x.R.FromSystemId != id)
                .OrderByDescending(x => x.At)
                .Select(x => new IntelMove(x.R.FromSystemId!.Value, id, x.At, x.R.Id)).ToList();

            systems[id] = new SystemIntel(
                id,
                pilotsIn.TryGetValue(id, out var here) ? here : [],
                unnamed, facts,
                reps.Select(x => x.R.Gate).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g)),
                clear,
                campsIn.TryGetValue(id, out var cs) ? cs : [],
                moves,
                reps.Count > 0 ? reps[0].R.HeadingSystemId : null);
        }

        // ── The feed ─────────────────────────────────────────────────────────
        var feed = new List<IntelEvent>(reports.Count + kills.Count + camps.Count);
        foreach (var (r, at) in reports)
        {
            var named = pilotsOf[r.Id].Select(p =>
            {
                var (corp, alliance) = Affiliation(p.CharacterId);
                return new IntelEventPilot(p.CharacterId, NameOf(p.CharacterId, p.CharacterName), p.ShipTypeId,
                                           ShipOf(p.ShipTypeId, p.ShipName), corp, alliance);
            }).ToList();
            var kind = r.Kind == IntelReportKind.Clear ? IntelEventKind.Clear
                     : r.Source == IntelReportSource.LocalPaste ? IntelEventKind.LocalPaste
                     : r.Source == IntelReportSource.DscanPaste ? IntelEventKind.Dscan
                     : r.ParentReportId is not null ? IntelEventKind.FollowUp
                     : IntelEventKind.Report;
            var count = r.Kind == IntelReportKind.Clear ? 0 : CountOf(r);
            feed.Add(new IntelEvent(
                "r" + r.Id.ToString(CultureInfo.InvariantCulture), kind, at, LifetimeOf(r, named.Count, count, s), r.SystemId,
                r.Source == IntelReportSource.Chat ? r.ChannelName : IntelSnapshot.SourcePaste,
                named, count, r.Flags, r.NoVisual, r.Obsolete,
                ReportId: r.Id, ParentReportId: r.ParentReportId, Channel: r.ChannelName, Reporter: r.ReporterName,
                ReporterId: r.ReporterCharacterId, Message: r.Message, Gate: r.Gate, Ships: r.Ships,
                FromSystemId: r.FromSystemId, HeadingSystemId: r.HeadingSystemId,
                Echoes: echoes.GetValueOrDefault(r.Id)));
        }

        foreach (var k in kills)
        {
            var attackers = k.Attackers.Where(a => a.CharacterId > 0).GroupBy(a => a.CharacterId).Select(g => g.First())
                .Select(a => new IntelEventPilot(a.CharacterId, NameOf(a.CharacterId, null), a.ShipTypeId,
                                                 ShipOf(a.ShipTypeId, null), a.CorporationId, a.AllianceId))
                .ToList();
            var hulls = attackers.Where(a => a.ShipTypeId is > 0).GroupBy(a => a.ShipTypeId!.Value)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).Take(3).ToList();
            var camp = camps.FirstOrDefault(c => c.KillMailIds.Contains(k.KillMailId));
            feed.Add(new IntelEvent(
                "k" + k.KillMailId.ToString(CultureInfo.InvariantCulture), IntelEventKind.Kill, k.At, s.KillAttacker,
                k.SystemId, IntelSnapshot.SourceKills, attackers, 0, 0, false, false,
                KillMailId: k.KillMailId, VictimId: k.VictimId,
                VictimName: k.VictimId > 0 ? NameOf(k.VictimId, null) : null,
                VictimShipTypeId: k.VictimShipTypeId, VictimShip: ShipOf(k.VictimShipTypeId, null),
                VictimCorporationId: k.VictimCorporationId, VictimAllianceId: k.VictimAllianceId,
                AttackerCount: attackers.Count, TopHullTypeIds: hulls,
                NearGate: IntelCampDetector.GateOf(k, input.Gates, s.CampGateKm), CampKey: camp?.Key));
        }

        foreach (var c in camps)
        {
            var attackers = c.Attackers.Select(a => new IntelEventPilot(
                a.CharacterId, NameOf(a.CharacterId, null), a.ShipTypeId, ShipOf(a.ShipTypeId, null), a.CorporationId, a.AllianceId)).ToList();
            // The camp's line spans the camp: as new as its last kill, lasting until the camp ends (the
            // camp lifetime after that kill), so it sorts, ages, fades and goes with the camp rather than
            // with when it formed. The key keeps the camp's identity.
            feed.Add(new IntelEvent(
                "c" + c.Key, IntelEventKind.CampFormed, c.LastKill, c.ExpiresAt - c.LastKill, c.SystemId, IntelSnapshot.SourceKills,
                attackers, 0, (int)IntelRules.IntelFlags.Camp, false, false,
                AttackerCount: attackers.Count, TopHullTypeIds: [.. c.AttackerShipTypeIds.Take(3)],
                NearGate: new IntelGateNear(c.StargateId, c.GateSystemId, 0), CampKey: c.Key));
        }
        feed.Sort((a, b) => b.At != a.At ? b.At.CompareTo(a.At) : string.CompareOrdinal(b.Key, a.Key));

        return new IntelSnapshot(
            now, version, s.Window, s, systems, pilots, input.Own, input.Officers, feed, camps,
            IntelFeedStatus.From(s), input.Standings)
        {
            SystemNames = input.SystemNames,
            HullIds = input.HullIds,
            Names = input.Names,
            Regions = input.Regions,
            Chatter =[.. input.Chatter.Where(l => l.At >= from).OrderByDescending(l => l.At).ThenByDescending(l => l.ChatMessageId)],
            Channels = input.Channels,
            ChannelLabels = input.ChannelLabels,
            Watch = input.Watch,
            TypeGroups = input.TypeGroups,
        };
    }

    /// <summary>How long a report's line lasts in the feed: as long as the longest thing it said.</summary>
    private static TimeSpan LifetimeOf(IntelReport r, int named, int count, IntelSettingsValues s)
    {
        if (r.Kind == IntelReportKind.Clear) return s.Clear;
        var life = TimeSpan.Zero;
        if (named > 0) life = s.Named;
        if (count > 0 && s.Unnamed > life) life = s.Unnamed;
        foreach (var flag in AllFlags)
            if ((r.Flags & (int)flag) != 0 && s.FlagLifetime(flag) > life) life = s.FlagLifetime(flag);
        return life == TimeSpan.Zero ? s.Unnamed : life;
    }

    /// <summary>An ISO time column ("yyyy-MM-ddTHH:mm:ssZ") as a time.</summary>
    public static bool TryParse(string? text, out DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(text)) { at = default; return false; }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);
    }
}
