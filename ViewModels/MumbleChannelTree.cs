namespace EveConsole.ViewModels;

/// <summary>
/// Which channels of one server are open in the list.
///
/// <para>A channel is open when someone is in it or anywhere under it, and closed otherwise, so on a server of hundreds
/// of channels what shows is where the people are.</para>
///
/// <para>The person can open or close any of them by hand, and that holds until the channel changes between having
/// people and not: someone arriving opens what they arrived in, and emptying closes it and everything under it. ⚠️ Not on
/// each arrival: a section closed by hand on a busy server would keep springing back open. Their own move is the
/// exception, as they should always be able to see where they are.</para>
/// </summary>
public sealed class MumbleChannelTree
{
    /// <summary>However a server describes its channels, walking up them cannot go on for ever.</summary>
    private const int MostDepth = 64;

    private readonly Dictionary<uint, bool> _byHand = [];
    private Dictionary<uint, uint?> _parents = [];
    private HashSet<uint> _occupied = [];
    private uint? _mine;

    /// <summary>Takes on how the server stands now.</summary>
    /// <param name="channels">Every channel, with the one it is under.</param>
    /// <param name="peopleIn">The channel of each person.</param>
    /// <param name="mine">The channel of the person using this, once known.</param>
    public void Update(IEnumerable<(uint Id, uint? Parent)> channels, IEnumerable<uint> peopleIn, uint? mine)
    {
        _parents = [];
        foreach (var (id, parent) in channels) _parents[id] = parent;

        var occupied = new HashSet<uint>();
        foreach (var channel in peopleIn)
            foreach (var up in UpFrom(channel))
                if (!occupied.Add(up)) break;   // those above were marked when this one was

        // Only emptying needs to say anything: what is kept by hand is either open with nobody there, which someone
        // arriving leaves open anyway, or closed with people there, which cannot be arrived at until it has emptied.
        if (_byHand.Count > 0)
        {
            var emptied = _occupied.Where(c => !occupied.Contains(c)).ToHashSet();
            if (emptied.Count > 0)
                foreach (var channel in _byHand.Keys.ToList())
                    if (UpFrom(channel).Any(emptied.Contains)) _byHand.Remove(channel);
        }
        _occupied = occupied;

        if (mine != _mine)
        {
            _mine = mine;
            if (mine is { } here)
                foreach (var up in UpFrom(here)) _byHand.Remove(up);
        }
    }

    /// <summary>The channels given and every one above them: where a search's finds are, and the way down to them.</summary>
    public HashSet<uint> Holding(IEnumerable<uint> channels)
    {
        var holding = new HashSet<uint>();
        foreach (var channel in channels)
            foreach (var up in UpFrom(channel))
                if (!holding.Add(up)) break;
        return holding;
    }

    public bool IsOpen(uint channel) => _byHand.TryGetValue(channel, out var open) ? open : _occupied.Contains(channel);

    /// <summary>The person's own click. Back to what it would be anyway, nothing needs remembering.</summary>
    public void Toggle(uint channel)
    {
        var open = !IsOpen(channel);
        if (open == _occupied.Contains(channel)) _byHand.Remove(channel);
        else _byHand[channel] = open;
    }

    private IEnumerable<uint> UpFrom(uint channel)
    {
        uint? at = channel;
        for (var depth = 0; at is { } here && depth <= MostDepth; depth++)
        {
            yield return here;
            at = _parents.GetValueOrDefault(here);
        }
    }
}
