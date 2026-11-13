using EveConsole.Models;

namespace EveConsole.Services.Intel;

/// <summary>A hostile pilot in a system, as every intel list shows one: who, in what, for whom, since when.</summary>
/// <param name="OnKill">They were on a kill here: an attacker on it, or named in a report a kill confirmed.</param>
public sealed record IntelHostile(
    long CharacterId, string? Name, int? ShipTypeId, string? Ship, long CorporationId, long AllianceId,
    DateTimeOffset SeenAt, StandingTier Tier, IntelWatchMark Watch, bool OnKill, bool NoVisual);

/// <summary>How a group of hostiles is known: by their alliance, or by their corporation when they have none.</summary>
public enum IntelWhoKind { Alliance, Corporation }

/// <summary>An alliance (or a corporation with no alliance) and how many of the system's named hostiles fly for it.</summary>
public sealed record IntelWhoGroup(IntelWhoKind Kind, long Id, string? Name, int Count);

/// <summary>A hull and how many fly it. A hull said by a name that is no type has type 0.</summary>
public sealed record IntelHullCount(int TypeId, string Name, int Count, bool IsCapsule);

/// <summary>
/// Who is in a system, what they fly, and whether they killed there: what a system's line leads with.
/// </summary>
/// <param name="Pilots">The named hostiles, newest first. Kill victims are never among them.</param>
/// <param name="Unnamed">Pilots a report counted but did not name.</param>
/// <param name="Groups">The named pilots by alliance, or by corporation when they have none: the largest first.
/// A pilot whose affiliation is not known is in no group.</param>
/// <param name="Hulls">The hulls flown, most common first; capsules count, but come last.</param>
/// <param name="Kills">Live kills in the system with one of these hostiles on them.</param>
/// <param name="NewestKill">The newest of them, for the kill marker's time.</param>
public sealed record IntelSystemHostiles(
    IReadOnlyList<IntelHostile> Pilots, int Unnamed, IReadOnlyList<IntelWhoGroup> Groups,
    IReadOnlyList<IntelHullCount> Hulls, int Kills, DateTimeOffset? NewestKill)
{
    /// <summary>Every hostile, named or counted.</summary>
    public int Count => Pilots.Count + Unnamed;

    /// <summary>The one pilot there, shown by their portrait; null with none, several, or one named among others only
    /// counted (one portrait beside a count of 16 would read as one person).</summary>
    public IntelHostile? Single => Pilots.Count == 1 && Unnamed == 0 ? Pilots[0] : null;

    /// <summary>The most flown hull, capsules last; null when no hull is known.</summary>
    public IntelHullCount? PrimaryHull => Hulls.Count > 0 ? Hulls[0] : null;

    public static readonly IntelSystemHostiles Empty = new([], 0, [], [], 0, null);
}

/// <summary>
/// The hostiles of one system row, summed up for every intel list: the panel's line, its popup and the tool's
/// Board. Pure: tools/IntelCheck builds rows by hand and reads these back.
///
/// <para>Who is there: the pilots the row already carries (named in live reports, or attackers on a kill there:
/// both were there, and hostile), never a kill's victim, who is no threat. Each with what they flew when last
/// seen, and their corporation and alliance as the snapshot has them.</para>
/// </summary>
public static class IntelHostiles
{
    /// <summary>A capsule's type ids: a pilot in one still counts, but their hull is never the one shown first.</summary>
    private static readonly HashSet<int> Capsules = [670, 33328];

    public static bool IsCapsule(int typeId) => Capsules.Contains(typeId);

    /// <summary>A hull said by a name the snapshot's English hull names (<see cref="IntelSnapshot.HullId"/>, the parser's
    /// vocabulary, capsules among them) do not know: its capsule's type id when the name is a capsule's in the interface
    /// language's SDE names; else 0. A capsule still counts, but is never the hull shown first.</summary>
    public static int CapsuleId(string? name)
    {
        if (name is not { Length: > 0 }) return 0;
        var trimmed = name.Trim();
        foreach (var id in Capsules)
            if (EveConsole.Localization.SdeNames.Type(id, "") is { Length: > 0 } local
                && string.Equals(local, trimmed, StringComparison.CurrentCultureIgnoreCase)) return id;
        return 0;
    }

    /// <summary>Each feed's kills by system, built once per snapshot (the feed is the snapshot's, never changed), so a
    /// list of rows looks only at its own system's kills rather than the whole feed for every row.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<IntelEvent>, ILookup<int, IntelEvent>> KillIndex = new();

    /// <summary>The snapshot's kills in one system, live or not.</summary>
    public static IEnumerable<IntelEvent> KillsIn(IntelSnapshot snapshot, int systemId) =>
        KillIndex.GetValue(snapshot.Feed, feed => feed.Where(e => e.Kind == IntelEventKind.Kill).ToLookup(e => e.SystemId))[systemId];

    /// <summary>The hostiles of one system row.</summary>
    /// <param name="killsShown">Whether the view shows kills at all (<see cref="IntelView.KillsShown"/>): a view whose
    /// sources leave the kills out shows no kill marker either.</param>
    public static IntelSystemHostiles Of(IntelSystemRow row, IntelSnapshot snapshot, DateTimeOffset now, bool killsShown = true)
    {
        var pilots = row.Pilots
            .Where(p => p.Pilot.Source != IntelLastSeenSource.KillVictim)
            .Select(p => new IntelHostile(
                p.Pilot.CharacterId, p.Pilot.Name, p.Pilot.ShipTypeId, p.Pilot.Ship, p.Pilot.CorporationId, p.Pilot.AllianceId,
                p.Pilot.At, p.Tier, p.Watch,
                p.Pilot.Source == IntelLastSeenSource.KillAttacker || p.Pilot.ConfirmedByKill, p.Pilot.NoVisual))
            .OrderByDescending(p => p.SeenAt).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.CharacterId)
            .ToList();

        var groups = Groups(pilots, snapshot);
        var hulls = Hulls(pilots, row.Unnamed > 0 ? row.Intel.Unnamed?.Ships : null, snapshot);

        // A kill marks the system only when one of these hostiles was on it: blues killing a red here are no threat.
        var ids = pilots.Select(p => p.CharacterId).ToHashSet();
        List<IntelEvent> kills = !killsShown || ids.Count == 0 ? [] :
            [.. KillsIn(snapshot, row.SystemId)
                .Where(e => e.ExpiresAt > now && e.At <= now && e.Pilots.Any(a => ids.Contains(a.CharacterId)))];

        return new IntelSystemHostiles(pilots, row.Unnamed, groups, hulls, kills.Count,
                                       kills.Count > 0 ? kills.Max(k => k.At) : null);
    }

    /// <summary>Pilots by alliance, or by corporation when they have none; the largest group first, then the one
    /// seen last, then by id so the order never flickers.</summary>
    public static IReadOnlyList<IntelWhoGroup> Groups(IEnumerable<IntelHostile> pilots, IntelSnapshot snapshot) =>
        [.. pilots
            .Where(p => p.AllianceId > 0 || p.CorporationId > 0)
            .GroupBy(p => p.AllianceId > 0 ? (IntelWhoKind.Alliance, p.AllianceId) : (IntelWhoKind.Corporation, p.CorporationId))
            .Select(g => (Group: new IntelWhoGroup(g.Key.Item1, g.Key.Item2, snapshot.EntityName(g.Key.Item2), g.Count()),
                          Newest: g.Max(p => p.SeenAt)))
            .OrderByDescending(x => x.Group.Count).ThenByDescending(x => x.Newest).ThenBy(x => x.Group.Id)
            .Select(x => x.Group)];

    /// <summary>The hulls flown: the named pilots' and those said with a count, most first, capsules last.</summary>
    public static IReadOnlyList<IntelHullCount> Hulls(IEnumerable<IntelHostile> pilots, string? said, IntelSnapshot snapshot)
    {
        var counts = new Dictionary<string, (int TypeId, string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
        void Add(int typeId, string name, int n)
        {
            var key = typeId > 0 ? "#" + typeId.ToString(System.Globalization.CultureInfo.InvariantCulture) : name;
            counts[key] = counts.TryGetValue(key, out var had) ? had with { Count = had.Count + n } : (typeId, name, n);
        }
        foreach (var p in pilots)
        {
            if (p.Ship is not { Length: > 0 } ship) continue;
            var id = p.ShipTypeId is > 0 and int t ? t : snapshot.HullId(ship) is > 0 and int h ? h : CapsuleId(ship);
            Add(id, ship, 1);
        }
        foreach (var (n, english) in EveConsole.Services.IntelDisplay.ParseShips(said))
        {
            var id = snapshot.HullId(english) is > 0 and int h ? h : CapsuleId(english);
            Add(id, id > 0 ? EveConsole.Localization.SdeNames.Type(id, english) : english, n);
        }
        return [.. counts.Values
            .Select(h => new IntelHullCount(h.TypeId, h.Name, h.Count, IsCapsule(h.TypeId)))
            .OrderBy(h => h.IsCapsule ? 1 : 0).ThenByDescending(h => h.Count)
            .ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}
