using System.Globalization;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

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
/// <para><b>Only while the choice is still open.</b> A wrap whose full timer has passed has already
/// been delivered wherever the game decided, and opening it is not a decision anyone can get wrong
/// — so it raises nothing. What earns a task is the window in between, where a destination can
/// still be picked and picking it is worth real ISK. That makes this list short by design, and
/// empty most of the time: it fills when a structure dies, not before.</para>
/// </summary>
public class AssetSafetyGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    IndustryAssignmentService assignment,
    WorklistSettings settings) : IWorklistGenerator
{
    public string Id          => "asset_safety";
    public string DisplayName => "Asset Safety";

    /// <summary>The Asset Safety Wrap container itself, not anything worth acting on alone.</summary>
    public const int WrapTypeId = 60;

    private const string SafetyFlag = "AssetSafety";

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
        // only when the user has opted their non-personal ones in.
        var wraps = await db.EsiAssets.AsNoTracking()
            .Where(a => a.TypeId == WrapTypeId && a.LocationFlag == SafetyFlag)
            .Where(a => a.OwnerType != "corporation" || corps == null || corps.Contains(a.OwnerId))
            .Select(a => new { a.ItemId, a.LocationId, a.OwnerId, a.OwnerType })
            .ToListAsync(ct);

        if (wraps.Count == 0) return [];

        var wrapIds = wraps.Select(w => w.ItemId).ToList();

        var contents = (await db.EsiAssets.AsNoTracking()
                .Where(a => a.LocationType == "item" && wrapIds.Contains(a.LocationId))
                .Select(a => new { a.LocationId, a.TypeId, a.Quantity })
                .ToListAsync(ct))
            .GroupBy(a => a.LocationId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var timers    = await TimersAsync(db, ct);
        var places    = await PlaceNamesAsync(db, ct);
        var owners    = await OwnerNamesAsync(db, ct);
        var typeNames = await TypeNamesAsync(db,
            contents.Values.SelectMany(v => v).Select(c => c.TypeId).Distinct().ToList(), ct);

        var now   = DateTimeOffset.UtcNow;
        var items = new List<WorklistItem>();

        // One task per owner per station. Grouping any wider would hide that two wraps in the same
        // station belong to different characters and so are two separate logins, which is the whole
        // of the work; grouping any narrower would raise three tasks for one trip.
        foreach (var group in wraps.GroupBy(w => (w.OwnerId, w.OwnerType, w.LocationId)))
        {
            var (ownerId, ownerType, locationId) = group.Key;
            var isCorp = ownerType == "corporation";

            // Only while the choice is still open. Before the minimum nothing can be picked; after
            // the full timer the game has already picked, and a wrap sitting at the station it was
            // delivered to is not a decision any more — it is just something to go and open, which
            // is not what this list is for. A wrap with no timer at all predates the notification
            // history ESI still serves, so it is long past both dates and drops out here too.
            var timer = BestTimer(timers, locationId, isCorp);
            if (timer is null || now < timer.Minimum || now >= timer.Full) continue;

            var lines = group
                .SelectMany(w => contents.GetValueOrDefault(w.ItemId) ?? [])
                .GroupBy(c => c.TypeId)
                // Widened before summing, not after: asset quantities are int, and a wrap holding
                // several billion units of a mineral overflows the accumulator on the way in.
                .Select(g => new WorklistLine(
                    g.Key, typeNames.GetValueOrDefault(g.Key, $"Type {g.Key}"), g.Sum(c => (long)c.Quantity)))
                .OrderByDescending(l => l.Quantity)
                .ToList();

            var place   = places.GetValueOrDefault(locationId, Unnamed(locationId));
            var owner   = owners.GetValueOrDefault(ownerId, (isCorp ? "Corp " : "Character ") + ownerId);
            var wrapped = group.Count();

            var (what, detail) = Describe(timer, now, wrapped, lines.Count, places);

            items.Add(new WorklistItem
            {
                // Owner and station only. The wrap ids change every time one is opened, and a key
                // that moved would lose the snooze and the age with it.
                Key           = $"asset_safety:{ownerId}:{locationId}",
                Source        = Id,
                Kind          = WorklistKind.AssetSafety,
                Title         = $"{place} — {what}",
                Detail        = $"{owner}. {detail}",
                Readiness     = WorklistReadiness.Ready,
                CharacterId   = isCorp ? 0 : ownerId,
                CharacterName = isCorp ? "" : owner,
                LocationId    = locationId,
                LocationName  = place,
                Lines         = lines,
                Priority      = WorklistPriority.AssetSafety,
            });
        }

        return items;
    }

    /// <param name="wrapped">Wraps in the group. Worth saying, because choosing for ten of them at
    /// one station is a different afternoon from choosing for one.</param>
    private static (string What, string Detail) Describe(
        SafetyTimer timer, DateTimeOffset now, int wrapped, int distinctTypes,
        IReadOnlyDictionary<long, string> places)
    {
        var count = wrapped == 1
            ? $"{distinctTypes:N0} item type{(distinctTypes == 1 ? "" : "s")} in 1 wrap"
            : $"{distinctTypes:N0} item types across {wrapped:N0} wraps";

        var left = timer.Full - now;
        var dest = places.GetValueOrDefault(timer.Destination, $"station {timer.Destination}");

        return ($"choose a destination for {wrapped:N0} asset safety wrap{(wrapped == 1 ? "" : "s")}",
                $"{count}. Delivers itself to {dest} in {(int)left.TotalDays}d {left.Hours}h " +
                $"({timer.Full.ToLocalTime():d MMM HH:mm}) at the higher fee if left.");
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
        id >= 100_000_000_000L ? $"Unnamed structure {id}" : $"Location {id}";

    private sealed record SafetyTimer(DateTimeOffset Minimum, DateTimeOffset Full, long Destination);

    /// <summary>
    /// Matches a wrap's location to a safety notification, for the deadline only.
    ///
    /// <para>Keyed on location because that is all the two records share — the notification names
    /// the structure the items left and the station they are bound for, and a wrap is at one or the
    /// other. Where several match, the one that expires last wins: it is the only one that could
    /// still be open, and being early about a deadline is cheaper than being late.</para>
    /// </summary>
    private static SafetyTimer? BestTimer(
        IReadOnlyList<(SafetyTimer Timer, long Structure, bool IsCorp)> timers, long locationId, bool isCorp) =>
        timers
            .Where(t => t.IsCorp == isCorp &&
                        (t.Structure == locationId || t.Timer.Destination == locationId))
            .OrderByDescending(t => t.Timer.Full)
            .Select(t => t.Timer)
            .FirstOrDefault();

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

    private static async Task<List<(SafetyTimer, long, bool)>> TimersAsync(
        AppDbContext db, CancellationToken ct)
    {
        var rows = await db.EsiNotifications.AsNoTracking()
            .Where(n => n.Type == SafetyNotification)
            .Select(n => n.Text)
            .ToListAsync(ct);

        var timers = new List<(SafetyTimer, long, bool)>(rows.Count);

        foreach (var text in rows)
        {
            if (string.IsNullOrEmpty(text)) continue;

            var min  = FileTime(Field(text, "assetSafetyMinimumTimestamp"));
            var full = FileTime(Field(text, "assetSafetyFullTimestamp"));
            if (min is null || full is null) continue;

            var dest      = (long?)Field(text, "newStationID") ?? 0;
            var structure = (long?)Field(text, "structureID")  ?? 0;
            var isCorp    = text.Contains("isCorpOwned: true", StringComparison.Ordinal);

            timers.Add((new SafetyTimer(min.Value, full.Value, dest), structure, isCorp));
        }

        return timers;
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

    private static async Task<Dictionary<long, string>> PlaceNamesAsync(
        AppDbContext db, CancellationToken ct)
    {
        var map = (await db.SdeStations.AsNoTracking()
                .Select(s => new { Id = (long)s.StationId, s.Name }).ToListAsync(ct))
            .ToDictionary(s => s.Id, s => s.Name);

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
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);
}
