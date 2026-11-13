namespace EveConsole.Alarms.Conditions;

/// <summary>
/// When each system came into an intel alarm's range, and when each kill was first caught by it: what
/// makes a match news to the capsuleer, as opposed to merely unseen by the alarm.
///
/// <para>News is the later of when a thing was seen and when it came into reach. A hostile reported
/// ten minutes ago next door to where a character just jumped is news to that alarm now; the same
/// hostile caught ten minutes late because a cooldown held it, the worker restarted or the alarm was
/// switched back on, is not. A kill's time is when it happened, and the kill feed can bring it minutes
/// later, so a kill is news from when this alarm first caught it too.</para>
///
/// <para>Two reaches, remembered apart: the range (what the alarm watches) and the watch list's farther
/// reach (where only its Always entries fire). A system crossing from the watch list's reach into the
/// range is news from that moment, though the farther reach held it all along; one leaving the range for
/// the farther reach is not news again.</para>
///
/// <para>Kept on the worker, per alarm, in memory. An alarm with nothing remembered (its first look
/// since the start, or none for <see cref="Stale"/>, as while every character was docked) takes
/// everything in reach as having been there all along: its news ages from when it was seen.</para>
/// </summary>
public sealed class IntelWatchMemory
{
    /// <summary>Past this without a look, an alarm's memory is begun again.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    /// <summary>An alarm not looked at for this long is forgotten altogether.</summary>
    private static readonly TimeSpan Forget = TimeSpan.FromHours(1);

    private sealed class Entry
    {
        public Dictionary<int, DateTimeOffset>    Entered { get; } = [];
        public Dictionary<int, DateTimeOffset>    Reached { get; } = [];
        public Dictionary<string, DateTimeOffset> Caught  { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset                     At      { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<long, Entry> _alarms = [];

    /// <summary>What one look found: when each system came into the range, when each came into the watch
    /// list's reach, and when each kill was first caught, <see cref="DateTimeOffset.MinValue"/> for "all along".</summary>
    public sealed record Look(IReadOnlyDictionary<int, DateTimeOffset> Entered, IReadOnlyDictionary<string, DateTimeOffset> Caught)
    {
        /// <summary>When each system came into the watch list's reach (the range's own included).</summary>
        public IReadOnlyDictionary<int, DateTimeOffset> Reached { get; init; } = new Dictionary<int, DateTimeOffset>();

        public DateTimeOffset EnteredAt(int systemId) => Entered.GetValueOrDefault(systemId, DateTimeOffset.MinValue);
        public DateTimeOffset ReachedAt(int systemId) => Reached.GetValueOrDefault(systemId, DateTimeOffset.MinValue);
        public DateTimeOffset CaughtAt(string key) => Caught.GetValueOrDefault(key, DateTimeOffset.MinValue);

        /// <summary>When a system became news to the alarm: when it came into the range for one the range
        /// holds, else when it came into the watch list's reach.</summary>
        public DateTimeOffset NewsAt(int systemId, bool inRange) => inRange ? EnteredAt(systemId) : ReachedAt(systemId);

        /// <summary>A look with nothing remembered: everything there all along.</summary>
        public static readonly Look AllAlong = new(new Dictionary<int, DateTimeOffset>(), new Dictionary<string, DateTimeOffset>());
    }

    /// <summary>
    /// Records one look of an alarm at <paramref name="now"/>: the systems in its range, the kills it
    /// matched, and the systems in the watch list's reach (<paramref name="reach"/>, the range's own
    /// included; none when the alarm does not follow the watch list). A system or kill new since its last
    /// look is new from <paramref name="now"/>; one that left is dropped, so coming back is news again.
    /// The range and the reach are kept apart: a system moving from the reach into the range is new to
    /// the range then.
    /// </summary>
    public Look Observe(long alarmId, IEnumerable<int> systems, IEnumerable<string> killKeys, DateTimeOffset now,
                        IEnumerable<int>? reach = null)
    {
        lock (_lock)
        {
            foreach (var gone in _alarms.Where(kv => now - kv.Value.At > Forget).Select(kv => kv.Key).ToList())
                _alarms.Remove(gone);

            var fresh = !_alarms.TryGetValue(alarmId, out var entry) || now - entry.At > Stale || now < entry.At;
            if (fresh) _alarms[alarmId] = entry = new Entry();
            var since = fresh ? DateTimeOffset.MinValue : now;

            Keep(entry!.Entered, systems.ToHashSet(), since);
            Keep(entry.Reached, reach?.ToHashSet() ?? [], since);
            Keep(entry.Caught, killKeys.ToHashSet(StringComparer.Ordinal), since);
            entry.At = now;
            return new Look(new Dictionary<int, DateTimeOffset>(entry.Entered),
                            new Dictionary<string, DateTimeOffset>(entry.Caught, StringComparer.Ordinal))
            {
                Reached = new Dictionary<int, DateTimeOffset>(entry.Reached),
            };
        }

        static void Keep<T>(Dictionary<T, DateTimeOffset> held, HashSet<T> now, DateTimeOffset since) where T : notnull
        {
            foreach (var gone in held.Keys.Where(k => !now.Contains(k)).ToList()) held.Remove(gone);
            foreach (var k in now) held.TryAdd(k, since);
        }
    }
}
