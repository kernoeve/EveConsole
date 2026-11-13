using System.Globalization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>
/// A route as it is being flown: its steps, and the step the character was last seen at. The steps after
/// that one are ahead; the ones before it are behind and no longer watched.
/// </summary>
/// <param name="SetAt">When the route was sent: part of a gate camp's key, so a new route through a known
/// camp is said again.</param>
/// <param name="AtIndex">The step the character was last seen at; when they are off the route, the last
/// step they were seen at; -1 while they have not been seen on it (sent from somewhere else).</param>
/// <param name="OnRoute">The character is known to be on the route now, at <see cref="AtIndex"/>. Only then
/// is the jump count along the route how far they are, and the step they are on known.</param>
public sealed record CharacterRouteAhead(long CharacterId, DateTimeOffset SetAt, IReadOnlyList<int> Steps, int AtIndex,
                                         bool OnRoute = true)
{
    /// <summary>At the destination: the route is done.</summary>
    public bool Arrived => AtIndex >= Steps.Count - 1;

    /// <summary>The step the character is on now; null when they are not known to be on the route.</summary>
    public int? Here => OnRoute && AtIndex >= 0 ? Steps[AtIndex] : null;

    /// <summary>The step after the one the character is on: the gate they leave by. Null when they are not on
    /// the route: the game picks its own gates from wherever they are.</summary>
    public int? Next => Here is not null && AtIndex + 1 < Steps.Count ? Steps[AtIndex + 1] : null;

    /// <summary>Every step still ahead, with how many jumps along the route it is. Before the character is seen
    /// on the route, every step is ahead, the first included.</summary>
    public IEnumerable<(int SystemId, int Jumps)> Ahead =>
        Steps.Skip(AtIndex + 1).Select((s, i) => (s, i + 1));
}

/// <summary>A system on a watched character's route ahead, as an intel alarm watches it.</summary>
/// <param name="Jumps">How many jumps along the route it is from the step the character is on (0 for that
/// step); for a character off the route, how far it is from where they are when that is known, else how many
/// jumps along the route from the step last seen at (from before the first step when none was).</param>
/// <param name="NextSystemId">For the step the character is on: the system the route leaves for, whose gate
/// is on the route.</param>
/// <param name="Measured"><paramref name="Jumps"/> is how far the character is. False when it is only a count
/// along the route from where they are not: it makes nothing urgent.</param>
public sealed record IntelRouteStep(long CharacterId, string Name, int Jumps, DateTimeOffset SetAt, int? NextSystemId = null,
                                    bool Measured = true);

/// <summary>
/// The routes the Route Planner sent to the game (<see cref="CharacterRoute"/>): written when one is sent,
/// read by the intel alarms for the part still ahead.
///
/// <list type="bullet">
/// <item>One row per character; a route sent replaces the one before, with the character not yet seen on it
/// (a plan can be sent to a character somewhere else than its first step): every step is ahead.</item>
/// <item>The step the character was last seen at moves with them (from the intel snapshot's positions on
/// every read, and from each ESI location poll): only the steps after it are ahead. Off the route, the last
/// step they were seen at holds, so the whole remainder is watched, but they are not on any step and no gate
/// out is theirs.</item>
/// <item>Cleared when the character reaches the destination; ignored six hours after it was sent.</item>
/// <item>A route set only in the game is not known: the game tells nobody what its autopilot is set to.</item>
/// </list>
/// </summary>
public static class CharacterRouteStore
{
    /// <summary>How long a route is watched at most: a route not flown by then was given up.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>A route was sent (on this PC): the intel alarms should look again.</summary>
    public static event Action? Saved;

    public static string Encode(IEnumerable<int> systemIds) =>
        string.Join(",", systemIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));

    public static List<int> Decode(string? text)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0) list.Add(id);
        return list;
    }

    /// <summary>
    /// Stores the route just sent for a character, replacing theirs: not cleared, and with the character not yet
    /// seen on it (AtIndex -1), since one plan can be sent to characters wherever they are.
    /// A route of one system (already there) goes nowhere, and only clears the one before.
    /// </summary>
    public static async Task SaveAsync(IDbContextFactory<AppDbContext> dbFactory, long characterId, IReadOnlyList<int> steps,
                                       DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await SaveAsync(db, characterId, steps, now, ct).ConfigureAwait(false);
        Saved?.Invoke();
    }

    public static async Task SaveAsync(AppDbContext db, long characterId, IReadOnlyList<int> steps, DateTimeOffset now,
                                       CancellationToken ct = default)
    {
        var at = IntelLastSeenWriter.Format(now);
        if (steps.Count < 2)
        {
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "CharacterRoutes" SET "ClearedAt" = {0} WHERE "CharacterId" = {1} AND "ClearedAt" IS NULL""",
                [at, characterId], ct).ConfigureAwait(false);
            return;
        }
        // ⚠️ One statement for insert or replace, the same on both engines: two PCs sending a route for the same
        // character at once leave one of them, never an error.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "CharacterRoutes" ("CharacterId", "DestinationSystemId", "SystemIds", "AtIndex", "SetAt", "ClearedAt")
            VALUES ({0}, {1}, {2}, -1, {3}, NULL)
            ON CONFLICT ("CharacterId") DO UPDATE SET
                "DestinationSystemId" = excluded."DestinationSystemId", "SystemIds" = excluded."SystemIds",
                "AtIndex" = -1, "SetAt" = excluded."SetAt", "ClearedAt" = NULL
            """,
            [characterId, steps[^1], Encode(steps), at], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A stored route as it stands for a character seen in <paramref name="systemId"/> (null when not known):
    /// null when it is cleared, older than <see cref="MaxAge"/>, or has no step to go. Pure:
    /// tools/IntelCheck runs it on rows built by hand.
    /// </summary>
    public static CharacterRouteAhead? Follow(CharacterRoute row, int? systemId, DateTimeOffset now)
    {
        if (row.ClearedAt is not null || !TryParse(row.SetAt, out var setAt) || now - setAt > MaxAge) return null;
        var steps = Decode(row.SystemIds);
        if (steps.Count < 2) return null;
        // ⚠️ On the route only when seen on a step now. Otherwise the last step seen holds (or none yet: -1), but
        // the character is not there: that step's gate out is not theirs, and the jumps along the route are not
        // how far they are.
        if (systemId is int here && steps.IndexOf(here) is var i and >= 0)
            return new CharacterRouteAhead(row.CharacterId, setAt, steps, i);
        return new CharacterRouteAhead(row.CharacterId, setAt, steps, Math.Clamp(row.AtIndex, -1, steps.Count - 1), OnRoute: false);
    }

    /// <summary>
    /// The routes being flown now, each followed to where its character is (<paramref name="positions"/>, by
    /// character id). What moved is written back: the step a character was seen at, and a route whose
    /// character has arrived is cleared and not returned.
    /// </summary>
    public static async Task<IReadOnlyList<CharacterRouteAhead>> ReadAsync(
        AppDbContext db, IReadOnlyDictionary<long, int> positions, DateTimeOffset now, CancellationToken ct = default)
    {
        var cutoff = IntelLastSeenWriter.Format(now - MaxAge);
        var rows = await db.CharacterRoutes.AsNoTracking()
            .Where(r => r.ClearedAt == null && string.Compare(r.SetAt, cutoff) >= 0)
            .ToListAsync(ct).ConfigureAwait(false);

        var live = new List<CharacterRouteAhead>();
        foreach (var row in rows)
        {
            var route = Follow(row, positions.TryGetValue(row.CharacterId, out var s) ? s : null, now);
            if (route is null) continue;
            await KeepAsync(db, row, route, now, ct).ConfigureAwait(false);
            if (!route.Arrived) live.Add(route);
        }
        return live;
    }

    /// <summary>
    /// Follows one character's route to the system their location poll put them in: on every ESI move, so a
    /// route is cleared on arrival even while no intel alarm is reading it.
    /// </summary>
    public static async Task FollowStatusAsync(IDbContextFactory<AppDbContext> dbFactory, long characterId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.CharacterRoutes.AsNoTracking().FirstOrDefaultAsync(r => r.CharacterId == characterId && r.ClearedAt == null, ct)
            .ConfigureAwait(false);
        if (row is null) return;
        var systemId = await db.CharacterStatuses.AsNoTracking().Where(s => s.CharacterId == characterId)
            .Select(s => s.SolarSystemId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (Follow(row, systemId, now) is { } route) await KeepAsync(db, row, route, now, ct).ConfigureAwait(false);
    }

    /// <summary>Writes back what moved: the step the character was seen at, or the arrival. Only onto the route
    /// it was read from, so a route sent meanwhile on another PC is never touched.</summary>
    private static async Task KeepAsync(AppDbContext db, CharacterRoute row, CharacterRouteAhead route, DateTimeOffset now,
                                        CancellationToken ct)
    {
        if (route.Arrived)
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "CharacterRoutes" SET "ClearedAt" = {0} WHERE "CharacterId" = {1} AND "SetAt" = {2} AND "ClearedAt" IS NULL""",
                [IntelLastSeenWriter.Format(now), row.CharacterId, row.SetAt], ct).ConfigureAwait(false);
        else if (route.AtIndex != row.AtIndex)
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "CharacterRoutes" SET "AtIndex" = {0} WHERE "CharacterId" = {1} AND "SetAt" = {2} AND "ClearedAt" IS NULL""",
                [route.AtIndex, row.CharacterId, row.SetAt], ct).ConfigureAwait(false);
    }

    private static bool TryParse(string? text, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);
}
