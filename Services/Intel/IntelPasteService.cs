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

public enum IntelPasteGroupKind { Alliance, NoAlliance, Own, Unresolved }

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

    /// <summary>The names that are no character: counted as unnamed pilots.</summary>
    public IReadOnlyList<string> Unresolved { get; init; } = [];

    /// <summary>The pilots grouped by tier, alliance and corporation, worst first.</summary>
    public IReadOnlyList<IntelPasteGroup> Groups { get; init; } = [];

    /// <summary>A pasted Local takes everyone reported in the system out first: this many are now.</summary>
    public int Replaces { get; init; }

    public DscanSummary Dscan { get; init; } = DscanSummary.Empty;

    /// <summary>The user's own characters on a pasted Local. Stored like anyone, never shown as a threat.</summary>
    public int Own => Pilots.Count(p => p.Tier == StandingTier.Own);

    /// <summary>The pilots the report counts: every name on a Local, every ship on a d-scan.</summary>
    public int PlayerCount => Kind == IntelPasteKind.Local ? Names.Count : Dscan.Ships;

    public bool CanSave => SystemId > 0 && Kind switch
    {
        IntelPasteKind.Local => Names.Count > 0,
        IntelPasteKind.Dscan => Dscan.Ships > 0 || Dscan.Flags != 0,
        _ => false,
    };
}

/// <summary>One paste as the recent list shows it.</summary>
public sealed record IntelRecentPaste(
    int ReportId, DateTimeOffset At, int SystemId, string SystemName, int Source, long ReporterId, string ReporterName, int PlayerCount);

/// <summary>
/// A pasted Local member list or d-scan, turned into a sighting for a chosen system. Written by the PC
/// where the paste was made, from what the user pasted into the box (our code never reads the clipboard).
///
/// <para><b>A pasted Local</b> is a full roster: it takes everyone reported in the system out at its time
/// and places every name on it there. Every name that is a character is stored, blues and the user's own
/// characters too (the standings are the viewer's to apply, not the paste's). A name that is no character
/// counts as an unnamed pilot. Because a partial paste would clear the system just the same, the preview
/// says how many it replaces before anything is saved.</para>
///
/// <para><b>A pasted d-scan</b> is a count of ships, capsules included, with no names. It never clears the
/// system: d-scan does not see everyone. Mobile warp disruptors on it say there are bubbles, and a
/// cynosural field that a cyno is lit.</para>
/// </summary>
public sealed class IntelPasteService(
    IDbContextFactory<AppDbContext> dbFactory,
    Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, long>>> resolveCharacters,
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
    /// The pilots grouped as the preview lists them: the user's own last, names that are no character
    /// after the rest; otherwise worst tier first, then the biggest alliance.
    /// </summary>
    public static IReadOnlyList<IntelPasteGroup> Group(
        IReadOnlyList<IntelPastePilot> pilots, IReadOnlyList<string> unresolved, Func<long, string?> nameOf)
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
    /// them, and how many sightings a Local would replace. Off the UI thread.
    /// </summary>
    public async Task<IntelPastePreview> PreviewAsync(
        string text, long viewerCharacterId, int systemId, IntelSnapshot snapshot, DateTimeOffset now,
        CancellationToken ct = default)
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
        };

        if (kind == IntelPasteKind.Dscan)
            return preview with { Dscan = await SummariseAsync(db, DscanLines(text), ct).ConfigureAwait(false) };
        if (kind != IntelPasteKind.Local) return preview;

        var names = LocalNames(text);
        var ids = await resolveCharacters(names, ct).ConfigureAwait(false);
        var resolved = names.Where(ids.ContainsKey).Select(n => (Name: n, Id: ids[n])).ToList();
        var affiliations = await AffiliationsAsync(db, resolved.Select(r => r.Id).ToHashSet(), ct).ConfigureAwait(false);

        var standings = snapshot.Standings;
        var pilots = resolved.Select(r =>
        {
            var (corp, alliance) = affiliations.GetValueOrDefault(r.Id);
            return new IntelPastePilot(r.Name, r.Id, corp, alliance, standings.TierFor(viewerCharacterId, r.Id, corp, alliance));
        }).ToList();
        var unresolved = names.Where(n => !ids.ContainsKey(n)).ToList();

        var entityNames = await EntityNamesAsync(db, pilots.SelectMany(p => new[] { p.CorporationId, p.AllianceId }).Where(i => i > 0).ToHashSet(), ct)
            .ConfigureAwait(false);
        return preview with
        {
            Names = names, Pilots = pilots, Unresolved = unresolved,
            Groups = Group(pilots, unresolved, id => entityNames.GetValueOrDefault(id)),
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
    /// viewing character as who reported it. A Local also stores every pilot on it, takes earlier reports
    /// of the system out (as a clear does), and places each pilot there in last seen. Then every PC is
    /// told. Returns the report's id.
    /// </summary>
    public async Task<int> SaveAsync(IntelPastePreview preview, DateTimeOffset? at = null, CancellationToken ct = default)
    {
        if (!preview.CanSave) throw new InvalidOperationException("Nothing to save.");
        var when = at ?? DateTimeOffset.UtcNow;
        var whenText = IntelLastSeenWriter.Format(when);
        var local = preview.Kind == IntelPasteKind.Local;

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
                Message = Cap(preview.Text),
                Kind = IntelReportKind.Sighting,
                Source = local ? IntelReportSource.LocalPaste : IntelReportSource.DscanPaste,
                ChatMessageId = 0,
                Flags = local ? 0 : preview.Dscan.Flags,
                Ships = local ? null : preview.Dscan.ShipsText,
            };
            db.IntelReports.Add(report);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (local)
            {
                var pilots = preview.Pilots.GroupBy(p => p.CharacterId).Select(g => g.First())
                    .Select(p => new IntelReportCharacter { IntelReportId = report.Id, CharacterId = p.CharacterId, CharacterName = p.Name })
                    .ToList();
                db.IntelReportCharacters.AddRange(pilots);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                // Everyone reported there before is out: the roster replaces them.
                await db.IntelReports
                    .Where(r => r.SystemId == preview.SystemId && r.Kind == IntelReportKind.Sighting && !r.Obsolete
                             && r.Id != report.Id && string.Compare(r.ReportedAt, whenText) <= 0)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Obsolete, true).SetProperty(r => r.ObsoleteSetOn, when), ct)
                    .ConfigureAwait(false);

                var rows = pilots.Select(p => IntelLastSeenFeed.ReportRow(
                    report.Id, whenText, preview.SystemId, IntelReportSource.LocalPaste, false, p)).ToList();
                rows = await IntelLastSeenFeed.WithPulledAffiliationsAsync(db, rows, ct).ConfigureAwait(false);
                await IntelLastSeenWriter.UpsertAsync(db, rows, ct).ConfigureAwait(false);
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
            .Select(r => new { r.Id, r.ReportedAt, r.SystemId, r.SystemName, r.Source, r.ReporterCharacterId, r.ReporterName, r.PlayerCount })
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => new IntelRecentPaste(
            r.Id, IntelSnapshotBuilder.TryParse(r.ReportedAt, out var t) ? t : DateTimeOffset.MinValue,
            r.SystemId, r.SystemName, r.Source, r.ReporterCharacterId ?? 0, r.ReporterName, r.PlayerCount))];
    }
}
