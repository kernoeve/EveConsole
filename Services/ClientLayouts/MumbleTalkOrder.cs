namespace EveConsole.Services.ClientLayouts;

/// <summary>
/// The order of a Mumble channel list: whoever is talking now, the latest to start at the top, then
/// whoever talked most recently, then everyone else by name.
///
/// <para>⚠️ By when someone STARTED talking, not when they were last heard: two people talking at
/// once are both "heard just now" at every look, and would swap places all the time.</para>
/// </summary>
public sealed class MumbleTalkOrder
{
    private readonly Dictionary<uint, DateTime> _started = [];
    private readonly Dictionary<uint, DateTime> _lastHeard = [];
    private readonly HashSet<uint> _talking = [];

    /// <summary>Who is talking at this moment. Only those in the list passed to <see cref="Order{T}"/> show.</summary>
    public void Heard(IEnumerable<uint> talkingNow, DateTime now)
    {
        var talking = talkingNow.ToHashSet();
        foreach (var session in talking)
        {
            if (!_talking.Contains(session)) _started[session] = now;
            _lastHeard[session] = now;
        }
        foreach (var session in _talking.Where(s => !talking.Contains(s))) _lastHeard[session] = now;
        _talking.Clear();
        _talking.UnionWith(talking);
    }

    public IReadOnlyList<T> Order<T>(IEnumerable<T> users, Func<T, uint> session, Func<T, string> name) =>
        [.. users
            .OrderBy(u => _talking.Contains(session(u)) ? 0 : _lastHeard.ContainsKey(session(u)) ? 1 : 2)
            .ThenByDescending(u => _talking.Contains(session(u))
                ? _started.GetValueOrDefault(session(u))
                : _lastHeard.GetValueOrDefault(session(u)))
            .ThenBy(name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Forgets anyone not in this list, as sessions are reused after people leave.</summary>
    public void KeepOnly(IEnumerable<uint> sessions)
    {
        var keep = sessions.ToHashSet();
        foreach (var gone in _lastHeard.Keys.Where(s => !keep.Contains(s)).ToList())
        {
            _lastHeard.Remove(gone);
            _started.Remove(gone);
        }
    }
}
