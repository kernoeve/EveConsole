using System.Globalization;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using EveConsole.Localization;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Items in asset safety that the player is now allowed to do something with.
///
/// <para>When an Upwell structure dies or is abandoned, whatever was inside is bundled into an
/// Asset Safety Wrap and put on a clock. Nothing can be done for the first five days. After that
/// the owner may pick a destination and pay to have it delivered; after twenty, the game picks for
/// them and charges more. Both ends of that window are the game's, not the player's, which is why
/// these outrank everything else on the list — see <see cref="WorklistPriority.AssetSafety"/>.</para>
///
/// <para>The wrap itself is the signal. It appears in the assets endpoint as a real item
/// (<see cref="WrapTypeId"/>) with its contents nested inside, so what is still sitting there comes
/// from assets rather than from notifications, which only ever say what happened once. The
/// notification supplies the clock, because it is the only place ESI puts it.</para>
///
/// <para><b>Only wraps still in asset safety</b> (<see cref="SafetyLocationId"/>): a wrap at a station
/// has been delivered and decides nothing. Each is matched to the owner's own notification for its
/// deadline (<see cref="Match"/>), and wraps with different deadlines are different tasks. One
/// still waiting with no open window on record is listed as past its deadline. That makes this
/// list short by design, and empty most of the time: it fills when a structure dies.</para>
/// </summary>
public class AssetSafetyGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    IndustryAssignmentService assignment,
    WorklistSettings settings) : IWorklistGenerator
{
    public string Id          => "asset_safety";
    public string DisplayName => WorklistText.SourceAssetSafety;

    /// <summary>The Asset Safety Wrap container itself, not anything worth acting on alone.</summary>
    public const int WrapTypeId = 60;

    private const string SafetyFlag = "AssetSafety";

    /// <summary>
    /// Where ESI puts a wrap that is still in asset safety — waiting for its owner to pick a
    /// destination. ⚠️ Not a station: once delivered, by the owner or by the game, a wrap sits at
    /// the station it went to (still flagged AssetSafety, until opened into the hangar), and there
    /// is nothing left to decide. Measured 2026-10-02: the four wraps of the one spill still open
    /// were all here; 309 older ones were at stations, long delivered. A wrap leaving this location
    /// is how a destination being picked shows — its task drops off at the next asset refresh.
    /// </summary>
    public const long SafetyLocationId = 2004;

    /// <summary>
    /// The notification ESI actually sends when a structure spills its contents.
    ///
    /// <para>Public because the Overview alert needs the same string, and it previously carried its
    /// own copy spelled "StructureItemsMovedIntoSafety" — close enough to read correctly and wrong
    /// enough to match nothing, which is exactly the sort of silent miss one shared constant
    /// prevents.</para>
    /// </summary>
    public const string SafetyNotification = "StructureItemsMovedToSafety";

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var corps = await assignment.UsableCorporationsAsync(settings.IncludeNonPersonalCorps, ct);

        // Same ownership rule the rest of the worklist uses: every character always, corporations
        // only when the user has opted their non-personal ones in. And only wraps still IN asset
        // safety — see SafetyLocationId: a wrap at a station has been delivered there already.
        var wraps = await db.EsiAssets.AsNoTracking()
            .Where(a => a.TypeId == WrapTypeId && a.LocationFlag == SafetyFlag && a.LocationId == SafetyLocationId)
            .Where(a => a.OwnerType != "corporation" || corps == null || corps.Contains(a.OwnerId))
            .Select(a => new { a.ItemId, a.OwnerId, a.OwnerType })
            .ToListAsync(ct);

        if (wraps.Count == 0) return [];

        var wrapIds = wraps.Select(w => w.ItemId).ToList();

        var contents = (await db.EsiAssets.AsNoTracking()
                .Where(a => a.LocationType == "item" && wrapIds.Contains(a.LocationId))
                .Select(a => new { a.LocationId, a.TypeId, a.Quantity })
                .ToListAsync(ct))
            .GroupBy(a => a.LocationId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var events    = await EventsAsync(db, ct);
        var corpOf    = await db.Characters.AsNoTracking().Select(c => new { c.Id, c.CorporationId })
                              .ToDictionaryAsync(c => c.Id, c => (long)c.CorporationId, ct);
        var places    = await PlaceNamesAsync(db, ct);
        var stationTypes = await db.SdeStations.AsNoTracking().Where(s => s.StationTypeId != null)
                              .ToDictionaryAsync(s => (long)s.StationId, s => s.StationTypeId!.Value, ct);
        var owners    = await OwnerNamesAsync(db, ct);
        var typeNames = await TypeNamesAsync(db,
            contents.Values.SelectMany(v => v).Select(c => c.TypeId).Distinct().ToList(), ct);

        // Each wrap's own event: see Match.
        var byOwner = wraps
            .GroupBy(w => (w.OwnerId, IsCorp: w.OwnerType == "corporation"))
            .ToDictionary(g => g.Key, g => g.Select(w => w.ItemId).ToList());
        var matched = Match(byOwner, events, corpOf);

        var now   = DateTimeOffset.UtcNow;
        var items = new List<WorklistItem>();

        // One task per owner and deadline. Wraps that went into asset safety at different times
        // have different deadlines and are different decisions, so they are never one task;
        // several structures spilling at once share a deadline and are one trip.
        foreach (var group in wraps.GroupBy(w =>
                 {
                     var e = matched.GetValueOrDefault(w.ItemId);
                     return (w.OwnerId, w.OwnerType, Event: e is not null && now < e.Full ? e : null);
                 }))
        {
            var (ownerId, ownerType, ev) = group.Key;
            var isCorp = ownerType == "corporation";

            var lines = group
                .SelectMany(w => contents.GetValueOrDefault(w.ItemId) ?? [])
                .GroupBy(c => c.TypeId)
                // Widened before summing, not after: asset quantities are int, and a wrap holding
                // several billion units of a mineral overflows the accumulator on the way in.
                .Select(g => new WorklistLine(
                    g.Key,
                    typeNames.GetValueOrDefault(g.Key, string.Format(WorklistText.TypeWithId, g.Key)),
                    g.Sum(c => (long)c.Quantity)))
                .OrderByDescending(l => l.Quantity)
                .ToList();

            var owner   = owners.GetValueOrDefault(ownerId, string.Format(
                              isCorp ? WorklistText.CorpWithId : WorklistText.CharacterWithId, ownerId));
            var wrapped = group.Count();
            var count   = wrapped == 1
                ? Plurals.Format(WorklistText.ResourceManager, nameof(WorklistText.SafetyTypesInOneWrapOther), lines.Count)
                : string.Format(WorklistText.SafetyTypesAcrossWraps, lines.Count, wrapped);

            if (ev is null)
            {
                // Still in asset safety with no open window on record: the deadline has passed, or
                // the notification is older than anything ESI still serves. Still the owner's to
                // deliver, so still listed — but without a date or a destination it cannot know.
                items.Add(new WorklistItem
                {
                    Key           = $"asset_safety:{ownerId}:passed",
                    Source        = Id,
                    Kind          = WorklistKind.AssetSafety,
                    Title         = Plurals.Format(WorklistText.ResourceManager, nameof(WorklistText.SafetyPassedTitleOther), wrapped),
                    Detail        = string.Format(WorklistText.SafetyPassedDetail, owner, count),
                    Readiness     = WorklistReadiness.Ready,
                    CharacterId   = isCorp ? 0 : ownerId,
                    CharacterName = isCorp ? "" : owner,
                    Lines         = lines,
                    Priority      = WorklistPriority.AssetSafety,
                    IconUrl       = WorklistIcons.Type(WrapTypeId),
                });
                continue;
            }

            var dest = places.GetValueOrDefault(ev.Destination, Unnamed(ev.Destination));
            var left = ev.Full - now;
            items.Add(new WorklistItem
            {
                // Owner and deadline: the wrap ids change every time one is opened, and a key that
                // moved would lose the snooze and the age with it.
                Key           = $"asset_safety:{ownerId}:{ev.Full.UtcTicks}",
                Source        = Id,
                Kind          = WorklistKind.AssetSafety,
                Title         = $"{dest} — " + Plurals.Format(WorklistText.ResourceManager,
                                    nameof(WorklistText.SafetyChooseByOther), wrapped, ev.Full.ToLocalTime()),
                Detail        = string.Format(WorklistText.SafetyDetail, owner, count, dest,
                                              (int)left.TotalDays, left.Hours, ev.Full.ToLocalTime()),
                // Nothing can be picked for the first five days: listed, with the date it opens.
                Readiness     = now < ev.Minimum ? WorklistReadiness.Waiting : WorklistReadiness.Ready,
                BlockedBy     = now < ev.Minimum ? string.Format(WorklistText.SafetyOpensAt, ev.Minimum.ToLocalTime()) : "",
                CharacterId   = isCorp ? 0 : ownerId,
                CharacterName = isCorp ? "" : owner,
                LocationId    = ev.Destination,
                LocationName  = dest,
                Lines         = lines,
                Priority      = WorklistPriority.AssetSafety,
                // The station the game delivers to if left: what the decision is about.
                IconUrl       = stationTypes.TryGetValue(ev.Destination, out var st) ? WorklistIcons.Render(st) : WorklistIcons.Type(WrapTypeId),
            });
        }

        return items;
    }

    /// <summary>
    /// What to call somewhere we have no name for.
    ///
    /// <para>Player structures are named through a docking-rights-gated endpoint, so one the player
    /// can no longer dock at — which describes most structures that dumped their contents into
    /// asset safety — may never resolve. Saying so beats printing a bare id and leaving the reader
    /// to wonder whether the tool is broken.</para>
    /// </summary>
    private static string Unnamed(long id) =>
        string.Format(id >= 100_000_000_000L ? WorklistText.UnnamedStructureWithId : WorklistText.LocationWithId, id);

    /// <summary>One structure's spill into asset safety, as one owner was told of it.</summary>
    public sealed record SafetyEvent(long CharacterId, bool IsCorp, DateTimeOffset Sent,
                                       DateTimeOffset Minimum, DateTimeOffset Full, long Destination, long Structure);

    /// <summary>
    /// Which event each wrap came from. Nothing ESI returns links the two: the notification names
    /// the structure and the station, the wrap only its owner and an item id. But item ids are
    /// handed out in order, so an owner's wraps, newest id first, line up with the owner's events,
    /// newest first — one wrap per structure per owner, which is how the game wraps them.
    ///
    /// <para>⚠️ A wrap already delivered has left asset safety, so its event has no wrap here and
    /// the line-up shifts. Two guards: an event several owners were told of hands its wraps out
    /// together, so a wrap whose id is far below the others' for the same event is not that
    /// event's (<see cref="SameEventSpread"/>); and only events still open are worth getting
    /// right — an older wrap matched to a closed one is listed as past its deadline either way.</para>
    /// </summary>
    public static Dictionary<long, SafetyEvent> Match(
        IReadOnlyDictionary<(long OwnerId, bool IsCorp), List<long>> wrapsByOwner,
        IReadOnlyList<SafetyEvent> events, IReadOnlyDictionary<long, long> corpOfCharacter)
    {
        var matched = new Dictionary<long, SafetyEvent>();
        foreach (var ((ownerId, isCorp), ids) in wrapsByOwner)
        {
            // A corporation hears through its characters: every one of them is told, once each.
            var own = events
                .Where(e => e.IsCorp == isCorp && (isCorp ? corpOfCharacter.GetValueOrDefault(e.CharacterId) == ownerId : e.CharacterId == ownerId))
                .GroupBy(e => (e.Structure, Minute: e.Sent.UtcTicks / TimeSpan.TicksPerMinute))
                .Select(g => g.First())
                .OrderByDescending(e => e.Sent)
                .ToList();
            var newest = ids.OrderByDescending(id => id).ToList();
            for (var i = 0; i < newest.Count && i < own.Count; i++) matched[newest[i]] = own[i];
        }

        // The cross-check: the wraps one event made for different owners carry ids close together.
        foreach (var g in matched.GroupBy(m => (m.Value.Structure, Minute: m.Value.Sent.UtcTicks / TimeSpan.TicksPerMinute)).Where(g => g.Count() > 1))
        {
            var top = g.Max(m => m.Key);
            foreach (var m in g.Where(m => top - m.Key > SameEventSpread).ToList()) matched.Remove(m.Key);
        }
        return matched;
    }

    /// <summary>How far apart the item ids one event hands out can be. Ids run at some ten million
    /// a day; the wraps of one spill were measured within ten thousand of each other.</summary>
    public const long SameEventSpread = 50_000_000;

    /// <summary>
    /// When this notification's items stop being the player's problem — the moment the game
    /// delivers them wherever it chose. Null if the body carries no timer.
    ///
    /// <para>Public because the Overview alert needs the same cutoff. Without one it announced
    /// every safety event ESI still remembers, which for this player was 585 of them going back to
    /// 2022 — all at once, the first time the alert's notification type was spelled correctly. An
    /// event whose window shut years ago is history, not an alert.</para>
    /// </summary>
    public static DateTimeOffset? WindowEnd(string? text) =>
        string.IsNullOrEmpty(text) ? null : FileTime(Field(text, "assetSafetyFullTimestamp"));

    /// <summary>Every asset safety notification still held, as events. Read whole: a few hundred
    /// rows at most, and the dates are compared here rather than in SQL.</summary>
    public static async Task<List<SafetyEvent>> EventsAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.EsiNotifications.AsNoTracking()
            .Where(n => n.Type == SafetyNotification)
            .Select(n => new { n.CharacterId, n.Timestamp, n.Text })
            .ToListAsync(ct);

        var events = new List<SafetyEvent>(rows.Count);
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.Text)) continue;
            var min  = FileTime(Field(r.Text, "assetSafetyMinimumTimestamp"));
            var full = FileTime(Field(r.Text, "assetSafetyFullTimestamp"));
            if (min is null || full is null) continue;

            events.Add(new SafetyEvent(
                r.CharacterId,
                r.Text.Contains("isCorpOwned: true", StringComparison.Ordinal),
                r.Timestamp, min.Value, full.Value,
                (long?)Field(r.Text, "newStationID") ?? 0,
                (long?)Field(r.Text, "structureID")  ?? 0));
        }
        return events;
    }

    /// <summary>
    /// Reads one scalar out of the notification's YAML body.
    ///
    /// <para>By hand rather than with a YAML parser because the two fields that matter are plain
    /// integers on their own line, and the same body also carries an anchored alias
    /// (<c>structureID: &amp;id001 …</c>) and an HTML link that a strict parse would have to be
    /// taught to tolerate. The alias marker is skipped explicitly below.</para>
    /// </summary>
    private static long? Field(string text, string key)
    {
        var at = text.IndexOf(key + ": ", StringComparison.Ordinal);
        if (at < 0) return null;

        var i = at + key.Length + 2;
        while (i < text.Length && (text[i] == '&' || text[i] == '*'))          // anchor or alias
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        while (i < text.Length && text[i] == ' ') i++;

        var end = i;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;

        return end > i && long.TryParse(text.AsSpan(i, end - i), NumberStyles.None,
                                        CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    /// <summary>
    /// The safety timestamps are Windows FILETIME — 100ns ticks since 1601 — not the seconds since
    /// 1970 that the rest of ESI uses. Reading one as the other lands in the twenty-fifth century.
    /// </summary>
    private static DateTimeOffset? FileTime(long? ticks) =>
        ticks is > 0 and < 2_650_467_744_000_000_000
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(ticks.Value))
            : null;

    /// <summary>Places as the screen names them: the map only ever becomes a task's title, detail
    /// and location cell. The task is keyed on the ids.</summary>
    private static async Task<Dictionary<long, string>> PlaceNamesAsync(
        AppDbContext db, CancellationToken ct)
    {
        var map = (await db.SdeStations.AsNoTracking()
                .Select(s => new { Id = (long)s.StationId, s.Name }).ToListAsync(ct))
            .ToDictionary(s => s.Id, s => SdeNames.Station(s.Id, s.Name));

        foreach (var s in await db.EsiStructureNames.AsNoTracking()
                     .Where(s => s.Name != "")
                     .Select(s => new { s.StructureId, s.Name }).ToListAsync(ct))
            map[s.StructureId] = s.Name;

        return map;
    }

    private static async Task<Dictionary<long, string>> OwnerNamesAsync(
        AppDbContext db, CancellationToken ct)
    {
        var map = (await db.Characters.AsNoTracking()
                .Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => c.Name);

        foreach (var c in await db.Corporations.AsNoTracking()
                     .Select(c => new { Id = (long)c.Id, c.Name }).ToListAsync(ct))
            map[c.Id] = c.Name;

        return map;
    }

    private static async Task<Dictionary<int, string>> TypeNamesAsync(
        AppDbContext db, List<int> typeIds, CancellationToken ct) =>
        await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .Select(t => new { t.TypeId, t.Name })
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);
}
