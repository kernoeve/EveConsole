using System.Globalization;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>What a paste looks like, before anything is resolved.</summary>
public enum IntelPasteKind
{
    /// <summary>Nothing pasted yet.</summary>
    Empty,
    /// <summary>The Local member list: one name per line.</summary>
    Local,
    /// <summary>A d-scan result: tab-separated columns, a line per thing on scan.</summary>
    Dscan,
    /// <summary>Neither.</summary>
    Unknown,
}

/// <summary>One line of a d-scan as the client copies it: [type id,] name, type, distance.</summary>
public sealed record DscanLine(int? TypeId, string Name, string TypeName, string Distance);

/// <summary>What one d-scan line's type is, from the SDE.</summary>
public sealed record DscanType(int TypeId, string Name, int GroupId);

/// <summary>Ships of one hull on a d-scan.</summary>
public sealed record DscanHull(int TypeId, string Name, int Count);

/// <summary>
/// A d-scan read for intel: the ships (a capsule is a ship: someone is in it), and the deployables
/// that say something on their own. Nothing else on scan matters here.
/// </summary>
/// <param name="Unknown">Lines whose type the SDE did not know.</param>
public sealed record DscanSummary(IReadOnlyList<DscanHull> Hulls, int Bubbles, int Cynos, int Unknown)
{
    public int Ships => Hulls.Sum(h => h.Count);

    public int Flags =>
        (Bubbles > 0 ? (int)IntelRules.IntelFlags.Bubbles : 0) | (Cynos > 0 ? (int)IntelRules.IntelFlags.Cyno : 0);

    /// <summary>The hulls as a report stores them for a reader: "8× Muninn, 2× Scimitar, Sabre".</summary>
    public string? ShipsText => Hulls.Count == 0 ? null
        : string.Join(", ", Hulls.Select(h => h.Count > 1 ? h.Count.ToString(CultureInfo.InvariantCulture) + "× " + h.Name : h.Name));

    public static readonly DscanSummary Empty = new([], 0, 0, 0);
}

/// <summary>A pasted name that is a character, with whom they fly for and how the viewer sees them.</summary>
public sealed record IntelPastePilot(string Name, long CharacterId, long CorporationId, long AllianceId, StandingTier Tier);

/// <summary>The pilots of one corporation in a pasted Local.</summary>
public sealed record IntelPasteCorp(long CorporationId, string Name, IReadOnlyList<IntelPastePilot> Pilots);

/// <summary>The pilots of one alliance (or of none) and one tier in a pasted Local.</summary>
/// <param name="Kind">What the group is besides an alliance: none, no alliance, the user's own, or names
/// that are no character.</param>
public sealed record IntelPasteGroup(
    StandingTier Tier, long AllianceId, IntelPasteGroupKind Kind, string Name, IReadOnlyList<IntelPasteCorp> Corps)
{
    public int Count => Corps.Sum(c => c.Pilots.Count);
}

/// <summary>What a group is besides an alliance. <see cref="Unchecked"/>: names not yet looked up in full (ESI did not
/// answer for the name, or for the pilot's corporation), so nobody can say whether they are friends.</summary>
public enum IntelPasteGroupKind { Alliance, NoAlliance, Own, Unresolved, Unchecked }

/// <summary>
/// Pasted names looked up: those that are characters, and those confirmed to be nobody (ESI said so now, or recently).
/// ⚠️ A name in neither could not be looked up (ESI failed or did not answer): it is not known to be nobody, and may be
/// a friend, so a Local holding one is never saved.
/// </summary>
public sealed record IntelNameLookup(IReadOnlyDictionary<string, long> Found, IReadOnlySet<string> Misses)
{
    public static readonly IntelNameLookup Empty = new(
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>Why a pasted Local cannot be saved as it stands.</summary>
public enum IntelPasteBlock
{
    /// <summary>Nothing stops it.</summary>
    None,
    /// <summary>Every "Save:" chip is off: nothing would be saved, and a clear would be wrong.</summary>
    NoTiers,
    /// <summary>Some names are not looked up in full yet: whether they are friends is not known.</summary>
    Lookup,
    /// <summary>The chips leave out pilots who may be hostile (Red, Orange or Neutral): a Local is a replace, and saving
    /// it would take them out of the system as though they had left.</summary>
    Partial,
}

/// <summary>
/// What a paste would store, worked out without storing anything: shown under the box as it is typed,
/// and what Save then writes.
/// </summary>
public sealed record IntelPastePreview
{
    public required IntelPasteKind Kind { get; init; }
    public required string Text { get; init; }
    public required int SystemId { get; init; }
    public string SystemName { get; init; } = "";
    public long ViewerCharacterId { get; init; }
    public string ViewerName { get; init; } = "";

    /// <summary>Every name on a pasted Local, as pasted, once each.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>The names that are characters.</summary>
    public IReadOnlyList<IntelPastePilot> Pilots { get; init; } = [];

    /// <summary>The names confirmed to be no character: counted as unnamed pilots.</summary>
    public IReadOnlyList<string> Unresolved { get; init; } = [];

    /// <summary>The names not looked up in full: ESI did not answer for the name, or for the pilot's corporation and
    /// alliance. Neither a pilot nor an unnamed count: while there is one, a Local is not saved
    /// (<see cref="IntelPasteBlock.Lookup"/>).</summary>
    public IReadOnlyList<string> Unchecked { get; init; } = [];

    /// <summary>The pilots grouped by tier, alliance and corporation, worst first.</summary>
    public IReadOnlyList<IntelPasteGroup> Groups { get; init; } = [];

    /// <summary>A pasted Local takes everyone reported in the system out first: this many are now.</summary>
    public int Replaces { get; init; }

    public DscanSummary Dscan { get; init; } = DscanSummary.Empty;

    /// <summary>
    /// The standings tiers a Local saves (the Paste tab's "Save:" chips): the pilots of any other tier, and the
    /// user's own characters always, are not logged at all. A name that is no character goes with Neutral, the
    /// tier of anyone nobody has a standing on. A d-scan names nobody, so it is not affected.
    /// </summary>
    public IReadOnlySet<StandingTier> Keep { get; init; } = StandingTiers.Hostile;

    /// <summary>Whether a pilot of this tier is saved: one of the kept tiers, and never the user's own.</summary>
    public bool Keeps(StandingTier tier) => tier != StandingTier.Own && Keep.Contains(tier);

    /// <summary>The characters a Local saves: those of the kept tiers, each once.</summary>
    public IReadOnlyList<IntelPastePilot> Kept => [.. Pilots.Where(p => Keeps(p.Tier)).DistinctBy(p => p.CharacterId)];

    /// <summary>The names that are no character a Local saves as unnamed pilots: all of them when Neutral is kept,
    /// else none.</summary>
    public IReadOnlyList<string> KeptUnresolved => Keeps(StandingTier.Neutral) ? Unresolved : [];

    /// <summary>The user's own characters on a pasted Local: never saved, never a threat.</summary>
    public int Own => Pilots.Where(p => p.Tier == StandingTier.Own).DistinctBy(p => p.CharacterId).Count();

    /// <summary>The pilots of each tier switched off, worst first, with the names that are no character under
    /// Neutral when it is off. The user's own characters are counted apart (<see cref="Own"/>).</summary>
    public IReadOnlyList<(StandingTier Tier, int Count)> LeftOutByTier
    {
        get
        {
            var list = new List<(StandingTier, int)>();
            if (Kind != IntelPasteKind.Local) return list;
            var pilots = Pilots.DistinctBy(p => p.CharacterId).ToList();
            foreach (var tier in StandingTiers.Five)
            {
                if (Keeps(tier)) continue;
                var n = pilots.Count(p => p.Tier == tier) + (tier == StandingTier.Neutral ? Unresolved.Count : 0);
                if (n > 0) list.Add((tier, n));
            }
            return list;
        }
    }

    /// <summary>Everyone on a pasted Local that is not saved: the tiers switched off and the user's own.</summary>
    public int LeftOut => LeftOutByTier.Sum(t => t.Count) + (Kind == IntelPasteKind.Local ? Own : 0);

    /// <summary>The pilots the report counts: every kept name on a Local, every ship on a d-scan.</summary>
    public int PlayerCount => Kind == IntelPasteKind.Local ? Kept.Count + KeptUnresolved.Count : Dscan.Ships;

    /// <summary>The pilots (and names that are nobody) left out who may be hostile: those of Red, Orange or Neutral
    /// switched off.</summary>
    public int HostileLeftOut => LeftOutByTier.Where(t => StandingTiers.IsHostile(t.Tier)).Sum(t => t.Count);

    /// <summary>
    /// Why a pasted Local cannot be saved, if it cannot. ⚠️ A Local is a replace: it takes out everyone reported in the
    /// system. That is right only when what it leaves out is known to be friendly (Light blue, Blue, our own), so a
    /// Local with a name not looked up yet, or with a hostile tier switched off and someone of it on the Local, or with
    /// every chip off, is not saved at all.
    /// </summary>
    public IntelPasteBlock Block =>
        Kind != IntelPasteKind.Local || Names.Count == 0 ? IntelPasteBlock.None
        : !StandingTiers.Five.Any(Keeps) ? IntelPasteBlock.NoTiers
        : Unchecked.Count > 0 ? IntelPasteBlock.Lookup
        : HostileLeftOut > 0 ? IntelPasteBlock.Partial
        : IntelPasteBlock.None;

    /// <summary>
    /// A pasted Local with nobody of the kept tiers on it, and nobody left out who may be hostile: the system is clear,
    /// and is saved as a clear (it takes out what was reported there, as any Local does), not as nothing.
    /// </summary>
    public bool IsClear => Kind == IntelPasteKind.Local && Names.Count > 0 && Block == IntelPasteBlock.None && PlayerCount == 0;

    public bool CanSave => SystemId > 0 && Kind switch
    {
        IntelPasteKind.Local => Names.Count > 0 && Block == IntelPasteBlock.None,
        IntelPasteKind.Dscan => Dscan.Ships > 0 || Dscan.Flags != 0,
        _ => false,
    };
}

/// <summary>One paste as the recent list shows it.</summary>
/// <param name="IsClear">A Local with nobody of the kept tiers on it, saved as a clear.</param>
public sealed record IntelRecentPaste(
    int ReportId, DateTimeOffset At, int SystemId, string SystemName, int Source, long ReporterId, string ReporterName, int PlayerCount,
    bool IsClear = false);

/// <summary>
/// A pasted Local member list or d-scan, turned into a sighting for a chosen system. Written by the PC
/// where the paste was made, from what the user pasted into the box (our code never reads the clipboard).
///
/// <para><b>A pasted Local</b> is a full roster: it takes everyone reported in the system out at its time
/// and places the pilots of the kept standings tiers there (the Paste tab's "Save:" chips; by default Red,
/// Orange and Neutral). A player copies all of Local for speed, blues too, and mostly does not care about
/// those: the tiers switched off, and the user's own characters always, are not logged at all (no report
/// pilot, no last seen, no count, no alarm). Their standings are judged as every intel view judges them:
/// the "Pasted by" character as the viewer, the forced standings character when one is set. A name that is
/// no character counts as an unnamed pilot when Neutral is kept. Because a partial paste would clear the
/// system just the same, the preview says how many it replaces before anything is saved.</para>
///
/// <para>⚠️ Still a replace with blues left out: what is saved is the full set of the kept tiers in the
/// system, so taking out what was reported there is still right. A Local with nobody of the kept tiers
/// on it says the system is clear of them, and is saved as a clear (a row of
/// <see cref="IntelReportKind.Clear"/> from the paste), never as nothing, which would leave the old
/// sightings standing. The replace is right only while everyone left out is a friend: a Local is not
/// saved at all while the chips leave out someone of Red, Orange or Neutral, while every chip is off, or
/// while a name could not be looked up (ESI failing: a name not looked up may be a blue, and is never
/// counted, stored or written into the report) (<see cref="IntelPastePreview.Block"/>).</para>
///
/// <para><b>A pasted d-scan</b> is a count of ships, capsules included, with no names. It never clears the
/// system: d-scan does not see everyone. Mobile warp disruptors on it say there are bubbles, and a
/// cynosural field that a cyno is lit.</para>
/// </summary>
public sealed class IntelPasteService(
    IDbContextFactory<AppDbContext> dbFactory,
    Func<IReadOnlyCollection<string>, CancellationToken, Task<IntelNameLookup>> resolveCharacters,
    Func<IReadOnlyCollection<long>, CancellationToken, Task>? fetchAffiliations = null,
    Func<IReadOnlyCollection<long>, CancellationToken, Task>? resolveEntityNames = null,
    ClientSignals? signals = null,
    IntelSnapshotService? snapshots = null,
    AppErrorLogger? errors = null)
{
    /// <summary>The most text a paste keeps of what was pasted.</summary>
    public const int MaxMessageChars = 64 * 1024;

    /// <summary>The most names a Local takes: the busiest systems in the game hold a few thousand.</summary>
    public const int MaxNames = 5_000;

    /// <summary>How many recent pastes the tab lists.</summary>
    public const int RecentCount = 10;

    // ── Reading the text (pure) ──────────────────────────────────────────────

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l));

    /// <summary>
    /// What a paste looks like: a d-scan when every line has two tabs or more (three or four columns), a
    /// Local when no line has a tab and every line could be a character's name, else neither.
    /// </summary>
    public static IntelPasteKind Classify(string? text)
    {
        var lines = Lines(text).ToList();
        if (lines.Count == 0) return IntelPasteKind.Empty;
        if (lines.All(l => l.Count(ch => ch == '\t') >= 2)) return IntelPasteKind.Dscan;
        if (lines.All(l => !l.Contains('\t') && IsNameLike(l.Trim()))) return IntelPasteKind.Local;
        return IntelPasteKind.Unknown;
    }

    /// <summary>Could be a character's name: 3 to 37 characters of letters, digits, spaces, hyphens,
    /// apostrophes, dots and underscores, starting with a letter or digit.</summary>
    public static bool IsNameLike(string name)
    {
        if (name.Length is < 3 or > 37 || !char.IsLetterOrDigit(name[0])) return false;
        foreach (var ch in name)
            if (!(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '\'' or '.' or '_')) return false;
        return true;
    }

    /// <summary>The names on a pasted Local, as pasted, each once.</summary>
    public static IReadOnlyList<string> LocalNames(string? text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var line in Lines(text))
        {
            var name = line.Trim();
            if (name.Length > 0 && seen.Add(name)) names.Add(name);
            if (names.Count >= MaxNames) break;
        }
        return names;
    }

    /// <summary>
    /// The lines of a d-scan. Four columns start with the type's id, which is preferred: the client copies
    /// the type's name in its own language. Three columns are name, type and distance.
    /// </summary>
    public static IReadOnlyList<DscanLine> DscanLines(string? text)
    {
        var lines = new List<DscanLine>();
        foreach (var line in Lines(text))
        {
            var cols = line.Split('\t').Select(c => c.Trim()).ToArray();
            if (cols.Length >= 4 && int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                lines.Add(new DscanLine(id, cols[1], cols[2], cols[3]));
            else if (cols.Length >= 3)
                lines.Add(new DscanLine(null, cols[0], cols[1], cols[2]));
        }
        return lines;
    }

    /// <summary>
    /// The ships and deployables on a d-scan. <paramref name="typeOf"/> answers a line's type, by its id
    /// or its name; a line it cannot answer is counted as unknown and otherwise left out.
    /// </summary>
    public static DscanSummary Summarise(IReadOnlyList<DscanLine> lines, Func<DscanLine, DscanType?> typeOf, IntelSdeGroups groups)
    {
        var hulls = new Dictionary<int, (string Name, int Count)>();
        int bubbles = 0, cynos = 0, unknown = 0;
        foreach (var line in lines)
        {
            if (typeOf(line) is not { } type) { unknown++; continue; }
            if (groups.Is(type.GroupId, IntelSdeGroups.MobileDisruptorName)) bubbles++;
            else if (groups.Is(type.GroupId, IntelSdeGroups.CynoFieldName)) cynos++;
            else if (groups.IsShip(type.GroupId))
                hulls[type.TypeId] = hulls.TryGetValue(type.TypeId, out var h) ? (h.Name, h.Count + 1) : (type.Name, 1);
        }
        return new DscanSummary(
            [.. hulls.Select(kv => new DscanHull(kv.Key, kv.Value.Name, kv.Value.Count))
                     .OrderByDescending(h => h.Count).ThenBy(h => h.Name, StringComparer.Ordinal)],
            bubbles, cynos, unknown);
    }

    /// <summary>
    /// How many sightings a pasted Local of <paramref name="systemId"/> takes out: the pilots placed there
    /// now and the unnamed count, as the snapshot holds them at <paramref name="now"/>.
    /// </summary>
    public static int ReplacedIn(IntelSnapshot snapshot, int systemId, DateTimeOffset now)
    {
        if (!snapshot.Systems.TryGetValue(systemId, out var sys)) return 0;
        var named = sys.PilotIds.Count(id => snapshot.Pilots.TryGetValue(id, out var p) && p.SystemId == systemId && p.ExpiresAt > now);
        var unnamed = sys.Unnamed is { } u && u.ExpiresAt > now ? u.Count : 0;
        return named + unnamed;
    }

    /// <summary>
    /// The pilots grouped as the preview lists them: names not looked up yet first, the user's own last,
    /// names that are no character after the rest; otherwise worst tier first, then the biggest alliance.
    /// </summary>
    public static IReadOnlyList<IntelPasteGroup> Group(
        IReadOnlyList<IntelPastePilot> pilots, IReadOnlyList<string> unresolved, Func<long, string?> nameOf,
        IReadOnlyList<string>? notLookedUp = null)
    {
        var groups = new List<IntelPasteGroup>();
        foreach (var byAlliance in pilots.Where(p => p.Tier != StandingTier.Own).GroupBy(p => (p.Tier, p.AllianceId)))
        {
            var (tier, alliance) = byAlliance.Key;
            groups.Add(new IntelPasteGroup(tier, alliance,
                alliance > 0 ? IntelPasteGroupKind.Alliance : IntelPasteGroupKind.NoAlliance,
                alliance > 0 ? nameOf(alliance) ?? "" : "",
                Corps(byAlliance)));
        }
        groups = [.. groups.OrderBy(g => g.Tier).ThenByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];

        // What stops Save goes first, where it is seen.
        if (notLookedUp is { Count: > 0 })
            groups.Insert(0, new IntelPasteGroup(StandingTier.Neutral, 0, IntelPasteGroupKind.Unchecked, "",
                [new IntelPasteCorp(0, "", [.. notLookedUp.Select(n => new IntelPastePilot(n, 0, 0, 0, StandingTier.Neutral))])]));

        if (unresolved.Count > 0)
            groups.Add(new IntelPasteGroup(StandingTier.Neutral, 0, IntelPasteGroupKind.Unresolved, "",
                [new IntelPasteCorp(0, "", [.. unresolved.Select(n => new IntelPastePilot(n, 0, 0, 0, StandingTier.Neutral))])]));

        var own = pilots.Where(p => p.Tier == StandingTier.Own).ToList();
        if (own.Count > 0)
            groups.Add(new IntelPasteGroup(StandingTier.Own, 0, IntelPasteGroupKind.Own, "", Corps(own)));
        return groups;

        IReadOnlyList<IntelPasteCorp> Corps(IEnumerable<IntelPastePilot> list) =>
            [.. list.GroupBy(p => p.CorporationId)
                    .Select(g => new IntelPasteCorp(g.Key, g.Key > 0 ? nameOf(g.Key) ?? "" : "",
                                                    [.. g.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)]))
                    .OrderByDescending(c => c.Pilots.Count).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    // ── The preview ──────────────────────────────────────────────────────────

    /// <summary>
    /// What the paste would store, resolved without storing it: the names looked up (the name cache, then
    /// ESI), their corporations and alliances, their tiers as <paramref name="viewerCharacterId"/> sees
    /// them, and how many sightings a Local would replace. Off the UI thread. <paramref name="keep"/> is the
    /// tiers a Local saves (by default Red, Orange and Neutral); the preview still lists everyone.
    /// </summary>
    public async Task<IntelPastePreview> PreviewAsync(
        string text, long viewerCharacterId, int systemId, IntelSnapshot snapshot, DateTimeOffset now,
        IReadOnlySet<StandingTier>? keep = null, CancellationToken ct = default)
    {
        var kind = Classify(text);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var systemName = systemId > 0
            ? await db.SdeSolarSystems.AsNoTracking().Where(s => s.SolarSystemId == systemId).Select(s => s.Name)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? snapshot.SystemNames.GetValueOrDefault(systemId) ?? ""
            : "";
        var viewerName = viewerCharacterId > 0
            ? await db.Characters.AsNoTracking().Where(c => c.Id == viewerCharacterId).Select(c => c.Name)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? ""
            : "";
        var preview = new IntelPastePreview
        {
            Kind = kind, Text = text, SystemId = systemId, SystemName = systemName,
            ViewerCharacterId = viewerCharacterId, ViewerName = viewerName,
            Keep = keep ?? StandingTiers.Hostile,
        };

        if (kind == IntelPasteKind.Dscan)
            return preview with { Dscan = await SummariseAsync(db, DscanLines(text), ct).ConfigureAwait(false) };
        if (kind != IntelPasteKind.Local) return preview;

        var names = LocalNames(text);
        IntelNameLookup lookup;
        try { lookup = await resolveCharacters(names, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Nothing looked up: every name stays not looked up, and the Local is not saved until it is.
            errors?.Log(nameof(IntelPasteService), "pasted names", ex);
            lookup = IntelNameLookup.Empty;
        }
        var ids = lookup.Found;
        var resolved = names.Where(ids.ContainsKey).Select(n => (Name: n, Id: ids[n])).ToList();
        var affiliations = await AffiliationsAsync(db, resolved.Select(r => r.Id).ToHashSet(), ct).ConfigureAwait(false);

        // ⚠️ A pilot whose corporation is not known is not a neutral: TierOf would say Neutral for a blue whose
        // affiliation lookup failed, and they would be stored as a hostile. Not looked up, like a name ESI did not answer for.
        var standings = snapshot.Standings;
        var pilots = resolved.Where(r => affiliations.ContainsKey(r.Id)).Select(r =>
        {
            var (corp, alliance) = affiliations[r.Id];
            return new IntelPastePilot(r.Name, r.Id, corp, alliance, standings.TierFor(viewerCharacterId, r.Id, corp, alliance));
        }).ToList();
        var noAffiliation = resolved.Where(r => !affiliations.ContainsKey(r.Id)).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unresolved = names.Where(n => !ids.ContainsKey(n) && lookup.Misses.Contains(n)).ToList();
        var notLookedUp = names.Where(n => noAffiliation.Contains(n) || (!ids.ContainsKey(n) && !lookup.Misses.Contains(n))).ToList();

        var entityNames = await EntityNamesAsync(db, pilots.SelectMany(p => new[] { p.CorporationId, p.AllianceId }).Where(i => i > 0).ToHashSet(), ct)
            .ConfigureAwait(false);
        return preview with
        {
            Names = names, Pilots = pilots, Unresolved = unresolved, Unchecked = notLookedUp,
            Groups = Group(pilots, unresolved, id => entityNames.GetValueOrDefault(id), notLookedUp),
            Replaces = ReplacedIn(snapshot, systemId, now),
        };
    }

    /// <summary>Each pilot's corporation and alliance: the last kill or pull held for them, and an ESI
    /// fetch for the ones with none.</summary>
    private async Task<Dictionary<long, (long Corp, long Alliance)>> AffiliationsAsync(AppDbContext db, HashSet<long> ids, CancellationToken ct)
    {
        var found = new Dictionary<long, (long, long)>();
        async Task ReadAsync()
        {
            foreach (var chunk in ids.Where(id => !found.ContainsKey(id)).Chunk(1000))
            {
                foreach (var a in await db.CharacterAffiliations.AsNoTracking().Where(a => chunk.Contains(a.CharacterId))
                             .Select(a => new { a.CharacterId, a.CorporationId, a.AllianceId }).ToListAsync(ct).ConfigureAwait(false))
                    found[a.CharacterId] = (a.CorporationId, a.AllianceId);
                // A kill's affiliation is newer than any pull, where the pilot has one.
                foreach (var s in await db.IntelLastSeens.AsNoTracking()
                             .Where(s => chunk.Contains(s.CharacterId) && s.AffiliationSource == IntelAffiliationSource.Kill && s.CorporationId != null)
                             .Select(s => new { s.CharacterId, s.CorporationId, s.AllianceId }).ToListAsync(ct).ConfigureAwait(false))
                    found[s.CharacterId] = (s.CorporationId ?? 0, s.AllianceId ?? 0);
                foreach (var c in await db.Characters.AsNoTracking().Where(c => chunk.Contains(c.Id))
                             .Select(c => new { c.Id, c.CorporationId, c.AllianceId }).ToListAsync(ct).ConfigureAwait(false))
                    found[c.Id] = (c.CorporationId, c.AllianceId ?? 0);
            }
        }

        await ReadAsync().ConfigureAwait(false);
        var missing = ids.Where(id => !found.ContainsKey(id)).ToList();
        if (missing.Count > 0 && fetchAffiliations is not null)
        {
            try
            {
                await fetchAffiliations(missing, ct).ConfigureAwait(false);
                await ReadAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors?.Log(nameof(IntelPasteService), "pasted pilots' corporations", ex); }
        }
        return found;
    }

    /// <summary>Corporation and alliance names from the name cache, with an ESI lookup for the rest.</summary>
    private async Task<Dictionary<long, string>> EntityNamesAsync(AppDbContext db, HashSet<long> ids, CancellationToken ct)
    {
        var names = new Dictionary<long, string>();
        async Task ReadAsync()
        {
            foreach (var chunk in ids.Where(id => !names.ContainsKey(id)).Chunk(1000))
                foreach (var n in await db.UniverseNames.AsNoTracking().Where(u => chunk.Contains(u.EntityId))
                             .Select(u => new { u.EntityId, u.Name }).ToListAsync(ct).ConfigureAwait(false))
                    names[n.EntityId] = n.Name;
        }

        await ReadAsync().ConfigureAwait(false);
        var missing = ids.Where(id => !names.ContainsKey(id)).ToList();
        if (missing.Count > 0 && resolveEntityNames is not null)
        {
            try
            {
                await resolveEntityNames(missing, ct).ConfigureAwait(false);
                await ReadAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors?.Log(nameof(IntelPasteService), "pasted pilots' corporation names", ex); }
        }
        return names;
    }

    /// <summary>The d-scan's lines with their types: by id where the line has one, else by the name in
    /// English or the client's language.</summary>
    private static async Task<DscanSummary> SummariseAsync(AppDbContext db, IReadOnlyList<DscanLine> lines, CancellationToken ct)
    {
        if (lines.Count == 0) return DscanSummary.Empty;
        var groups = await IntelSdeGroups.LoadAsync(db, ct).ConfigureAwait(false);

        var byId = new Dictionary<int, DscanType>();
        var ids = lines.Where(l => l.TypeId is > 0).Select(l => l.TypeId!.Value).Distinct().ToList();
        foreach (var chunk in ids.Chunk(1000))
            foreach (var t in await db.SdeTypes.AsNoTracking().Where(t => chunk.Contains(t.TypeId))
                         .Select(t => new { t.TypeId, t.Name, t.GroupId }).ToListAsync(ct).ConfigureAwait(false))
                byId[t.TypeId] = new DscanType(t.TypeId, t.Name, t.GroupId);

        var byName = new Dictionary<string, DscanType>(StringComparer.OrdinalIgnoreCase);
        var names = lines.Where(l => l.TypeId is not > 0 || !byId.ContainsKey(l.TypeId.Value))
            .Select(l => l.TypeName).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count > 0)
        {
            foreach (var chunk in names.Chunk(500))
                foreach (var t in await db.SdeTypes.AsNoTracking().Where(t => chunk.Contains(t.Name))
                             .Select(t => new { t.TypeId, t.Name, t.GroupId }).ToListAsync(ct).ConfigureAwait(false))
                    byName.TryAdd(t.Name, new DscanType(t.TypeId, t.Name, t.GroupId));
            var left = names.Where(n => !byName.ContainsKey(n)).ToList();
            foreach (var chunk in left.Chunk(500))
            {
                var local = await db.SdeNames.AsNoTracking()
                    .Where(n => n.Kind == SdeNameKind.Type && chunk.Contains(n.Name))
                    .Select(n => new { n.Id, n.Name }).ToListAsync(ct).ConfigureAwait(false);
                var typeIds = local.Select(n => (int)n.Id).Distinct().ToList();
                var types = await db.SdeTypes.AsNoTracking().Where(t => typeIds.Contains(t.TypeId))
                    .Select(t => new { t.TypeId, t.Name, t.GroupId }).ToListAsync(ct).ConfigureAwait(false);
                var typeById = types.ToDictionary(t => t.TypeId);
                foreach (var n in local)
                    if (typeById.TryGetValue((int)n.Id, out var t)) byName.TryAdd(n.Name, new DscanType(t.TypeId, t.Name, t.GroupId));
            }
        }

        return Summarise(lines,
            l => l.TypeId is int id && byId.TryGetValue(id, out var t) ? t : byName.GetValueOrDefault(l.TypeName),
            groups);
    }

    // ── Saving ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores the paste as a report of its own, in one transaction: no chat message behind it (id 0), the
    /// viewing character as who reported it. A Local also stores the pilots of the kept tiers on it (and
    /// only their names as its text: nobody left out is logged), takes earlier reports of the system out
    /// (as a clear does), and places each kept pilot there in last seen. A Local with nobody kept on it is
    /// stored as a clear of the system. Then every PC is told. Returns the report's id.
    /// </summary>
    public async Task<int> SaveAsync(IntelPastePreview preview, DateTimeOffset? at = null, CancellationToken ct = default)
    {
        // ⚠️ CanSave holds every reason a Local is not saved (a name not looked up, a hostile tier left out, every
        // chip off): past it, everything left out is a friend, so the replace below is right, and the text below
        // holds only names kept or confirmed to be nobody.
        if (!preview.CanSave) throw new InvalidOperationException("Nothing to save.");
        var when = at ?? DateTimeOffset.UtcNow;
        var whenText = IntelLastSeenWriter.Format(when);
        var local = preview.Kind == IntelPasteKind.Local;
        var clear = preview.IsClear;
        var kept = local ? preview.Kept : [];
        string message;
        if (local)
        {
            var keptNames = kept.Select(p => p.Name).Concat(preview.KeptUnresolved).ToHashSet(StringComparer.OrdinalIgnoreCase);
            message = string.Join("\n", preview.Names.Where(keptNames.Contains));
        }
        else message = preview.Text;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var strategy = db.Database.CreateExecutionStrategy();
        var reportId = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var report = new IntelReport
            {
                ReportedAt = whenText, ChannelName = "", ReporterName = preview.ViewerName,
                ReporterCharacterId = preview.ViewerCharacterId > 0 ? preview.ViewerCharacterId : null,
                SystemId = preview.SystemId, SystemName = preview.SystemName,
                PlayerCount = preview.PlayerCount,
                Message = Cap(message),
                Kind = clear ? IntelReportKind.Clear : IntelReportKind.Sighting,
                Source = local ? IntelReportSource.LocalPaste : IntelReportSource.DscanPaste,
                ChatMessageId = 0,
                Flags = local ? 0 : preview.Dscan.Flags,
                Ships = local ? null : preview.Dscan.ShipsText,
            };
            db.IntelReports.Add(report);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (local)
            {
                var pilots = kept
                    .Select(p => new IntelReportCharacter { IntelReportId = report.Id, CharacterId = p.CharacterId, CharacterName = p.Name })
                    .ToList();
                if (pilots.Count > 0)
                {
                    db.IntelReportCharacters.AddRange(pilots);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                // Everyone reported there before is out: the roster of the kept tiers replaces them (a clear, nobody).
                await db.IntelReports
                    .Where(r => r.SystemId == preview.SystemId && r.Kind == IntelReportKind.Sighting && !r.Obsolete
                             && r.Id != report.Id && string.Compare(r.ReportedAt, whenText) <= 0)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Obsolete, true).SetProperty(r => r.ObsoleteSetOn, when), ct)
                    .ConfigureAwait(false);

                if (pilots.Count > 0)
                {
                    var rows = pilots.Select(p => IntelLastSeenFeed.ReportRow(
                        report.Id, whenText, preview.SystemId, IntelReportSource.LocalPaste, false, p)).ToList();
                    rows = await IntelLastSeenFeed.WithPulledAffiliationsAsync(db, rows, ct).ConfigureAwait(false);
                    await IntelLastSeenWriter.UpsertAsync(db, rows, ct).ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return report.Id;
        }).ConfigureAwait(false);

        await TellAsync(ct).ConfigureAwait(false);
        return reportId;
    }

    /// <summary>Every PC hears that intel was written (this one too); with nobody to tell, this PC's
    /// snapshot reads it at once.</summary>
    private async Task TellAsync(CancellationToken ct)
    {
        if (signals is not null)
        {
            try { await signals.PublishAsync(IntelSignals.Payload(IntelSignals.Changed), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors?.Log(nameof(IntelPasteService), "telling the other PCs", ex); }
        }
        snapshots?.Request();
    }

    private static string Cap(string text)
    {
        if (text.Length <= MaxMessageChars) return text;
        var cut = MaxMessageChars;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut];
    }

    // ── Recent ───────────────────────────────────────────────────────────────

    /// <summary>The last pastes made on any PC, newest first.</summary>
    public async Task<IReadOnlyList<IntelRecentPaste>> RecentAsync(int count = RecentCount, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.IntelReports.AsNoTracking()
            .Where(r => r.Source == IntelReportSource.LocalPaste || r.Source == IntelReportSource.DscanPaste)
            .OrderByDescending(r => r.ReportedAt).ThenByDescending(r => r.Id)
            .Take(count)
            .Select(r => new { r.Id, r.ReportedAt, r.SystemId, r.SystemName, r.Source, r.ReporterCharacterId, r.ReporterName, r.PlayerCount, r.Kind })
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => new IntelRecentPaste(
            r.Id, IntelSnapshotBuilder.TryParse(r.ReportedAt, out var t) ? t : DateTimeOffset.MinValue,
            r.SystemId, r.SystemName, r.Source, r.ReporterCharacterId ?? 0, r.ReporterName, r.PlayerCount,
            r.Kind == IntelReportKind.Clear))];
    }
}
