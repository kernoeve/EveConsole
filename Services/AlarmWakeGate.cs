using EveConsole.Alarms;

namespace EveConsole.Services;

/// <summary>
/// The wake cooldown and memory alarms of one <see cref="IAlarmCondition.WakeGroup"/> share, on the
/// worker that evaluates them.
///
/// <list type="bullet">
/// <item><b>Said once.</b> A match another alarm of the group woke someone for in the last
/// <see cref="Remember"/> is only shown: a "near me" alarm and a "home system" alarm that both catch
/// one sighting say it once.</item>
/// <item><b>Held, not dropped.</b> Within the group's cooldown after a wake, the matches that would
/// wake are held: not shown, not banked, so the next evaluation judges them again. Still news then,
/// they wake together; gone stale, the check marks them quiet and they are only shown.</item>
/// <item><b>Urgent goes at once.</b> A wake set with an urgent match in it is never held.</item>
/// </list>
///
/// <para>Memory only: after a restart or a move of the worker to another PC it starts empty, so one
/// sighting may be said once more. The same accepted risk as the intel registers.</para>
/// </summary>
public sealed class AlarmWakeGate
{
    /// <summary>How long a woken match stays "already said" for the rest of the group.</summary>
    public static readonly TimeSpan Remember = TimeSpan.FromMinutes(30);

    public const int DefaultCooldownSeconds = 15;
    public const int MaxCooldownSeconds     = 600;

    /// <summary>The shared preference holding a group's cooldown in seconds: read from the database by
    /// the worker, never from one PC's startup copy.</summary>
    public static string CooldownKey(string group) => "alarms.wake_cooldown_s." + group;

    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastWake = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _woken = new(StringComparer.Ordinal);

    /// <summary>What becomes of one alarm's would-wake matches.</summary>
    /// <param name="Wake">Wake for these now.</param>
    /// <param name="Quietened">Another alarm of the group already woke for these: show them only.</param>
    /// <param name="Held">Within the cooldown: neither shown nor banked, judged again next time.</param>
    public sealed record Decision(IReadOnlyList<AlarmMatch> Wake, IReadOnlyList<AlarmMatch> Quietened, IReadOnlyList<AlarmMatch> Held);

    /// <summary>Judges the matches an alarm of <paramref name="group"/> would wake for, at
    /// <paramref name="now"/>, under a cooldown of <paramref name="cooldown"/> (zero for none). Changes
    /// nothing: <see cref="Woke"/> records a wake once it is decided on.</summary>
    public Decision Judge(string group, IReadOnlyList<AlarmMatch> wake, DateTimeOffset now, TimeSpan cooldown)
    {
        lock (_lock)
        {
            var said = _woken.TryGetValue(group, out var keys) ? keys : null;
            bool Said(AlarmMatch m) => said is not null && said.TryGetValue(m.Key, out var at) && now - at < Remember;

            var quietened = wake.Where(Said).ToList();
            var rest      = wake.Where(m => !Said(m)).ToList();
            if (rest.Count == 0) return new Decision([], quietened, []);

            var cooling = cooldown > TimeSpan.Zero && _lastWake.TryGetValue(group, out var last)
                          && now >= last && now - last < cooldown;
            return cooling && !rest.Any(m => m.Urgent)
                ? new Decision([], quietened, rest)
                : new Decision(rest, quietened, []);
        }
    }

    /// <summary>Records that an alarm of <paramref name="group"/> woke someone for these keys: the
    /// cooldown starts, and the rest of the group only shows them.</summary>
    public void Woke(string group, IEnumerable<string> keys, DateTimeOffset now)
    {
        lock (_lock)
        {
            _lastWake[group] = now;
            if (!_woken.TryGetValue(group, out var said)) _woken[group] = said = new(StringComparer.Ordinal);
            foreach (var key in keys) said[key] = now;
            foreach (var old in said.Where(kv => now - kv.Value >= Remember).Select(kv => kv.Key).ToList())
                said.Remove(old);
        }
    }
}
