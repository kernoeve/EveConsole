namespace EveConsole.Services.ClientLayouts;

/// <summary>
/// For each monitor, which of its clients the user was last in, and so which character controls on
/// that monitor follow. Plain logic: the panel host tells it what's on screen and what came to the
/// front. It reads window titles and nothing else.
///
/// <para>The selection follows the client: bringing another client on the monitor to the front
/// changes it, and so does that client logging in as someone else. Picking a name by hand holds
/// until either of those next happens. A client at its login screen has no name, so the last name
/// stays until a new one appears.</para>
///
/// <para>The names offered include everyone the layout sets for that client's place, logged in or
/// not, so the user can see whether another character on that account needs them first.</para>
/// </summary>
public sealed class MonitorFocus
{
    private readonly Dictionary<long, ClientOnMonitor> _clients = [];
    private readonly Dictionary<string, long> _lastClient = [];
    private readonly Dictionary<string, string?> _lastCharacter = [];
    private readonly Dictionary<string, string?> _selected = [];

    /// <summary>A monitor's names or its selection changed. Named by monitor key.</summary>
    public event Action<string>? Changed;

    /// <summary>
    /// The names to choose from on a monitor: everyone the layout sets for the place of the client
    /// last used there, logged in or not, so their tasks can be looked at before switching to them;
    /// then whoever else is logged in on that monitor.
    /// </summary>
    public IReadOnlyList<string> NamesOn(string monitor)
    {
        var here = _clients.Values.Where(c => c.MonitorKey == monitor).ToList();
        // ⚠️ Before any client has been used, the one the selection belongs to, by its place's
        // characters too: by its logged-in name alone, picking someone not logged in lost the place,
        // its characters left the list, and the dropdown went blank on the name just picked.
        var selected = _selected.GetValueOrDefault(monitor);
        var last = _lastClient.TryGetValue(monitor, out var handle) && _clients.TryGetValue(handle, out var known) && known.MonitorKey == monitor
            ? known
            : here.FirstOrDefault(c => SameName(c.Character, selected))
              is { Handle: not 0 } own ? own
            : here.FirstOrDefault(c => c.GroupNames.Contains(selected ?? "", StringComparer.OrdinalIgnoreCase));
        // At its login screen a client has no name, and so no place's characters: those it had last.
        var group = last.GroupNames.Count > 0 ? last.GroupNames
            : last.Character == null && last.Handle != 0 && _lastGroup.TryGetValue(monitor, out var kept) ? kept : [];
        var others = LoggedInOn(monitor);
        return [.. group.Concat(others).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private readonly Dictionary<string, IReadOnlyList<string>> _lastGroup = [];

    /// <summary>Keeps the last used client's place's characters, for while it's logged out.</summary>
    private void KeepGroup(string monitor, ClientOnMonitor client)
    {
        if (client.GroupNames.Count > 0) _lastGroup[monitor] = client.GroupNames;
        else if (client.Character != null) _lastGroup.Remove(monitor);
    }

    /// <summary>Who is logged in on a monitor, by name.</summary>
    private IReadOnlyList<string> LoggedInOn(string monitor) =>
        [.. _clients.Values.Where(c => c.MonitorKey == monitor && c.Character != null).Select(c => c.Character!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];

    public string? SelectedOn(string monitor) => _selected.GetValueOrDefault(monitor);

    /// <summary>What's on screen now.</summary>
    public void Update(IEnumerable<ClientOnMonitor> clients)
    {
        var monitors = _clients.Values.Select(c => c.MonitorKey).ToHashSet();
        var namesBefore = monitors.ToDictionary(m => m, NamesOn);
        var selectedBefore = new Dictionary<string, string?>(_selected);

        _clients.Clear();
        foreach (var client in clients)
        {
            _clients[client.Handle] = client;
            monitors.Add(client.MonitorKey);
        }

        foreach (var monitor in monitors)
        {
            if (_lastClient.TryGetValue(monitor, out var last))
            {
                if (_clients.TryGetValue(last, out var client) && client.MonitorKey == monitor)
                {
                    // Logged out and back in as someone else, or just arrived with a name.
                    if (client.Character != null && !SameName(client.Character, _lastCharacter.GetValueOrDefault(monitor)))
                        _selected[monitor] = client.Character;
                    if (client.Character != null) _lastCharacter[monitor] = client.Character;
                    KeepGroup(monitor, client);
                }
                else
                {
                    _lastClient.Remove(monitor);    // closed, or moved to another monitor
                    _lastCharacter.Remove(monitor);
                    _lastGroup.Remove(monitor);
                }
            }

            var names = NamesOn(monitor);
            var selected = _selected.GetValueOrDefault(monitor);
            // Chosen for the user: someone logged in, never one only listed with them.
            if (selected == null || !names.Contains(selected, StringComparer.OrdinalIgnoreCase))
                _selected[monitor] = LoggedInOn(monitor).FirstOrDefault();

            var before = namesBefore.GetValueOrDefault(monitor) ?? [];
            if (!before.SequenceEqual(names) || !SameName(selectedBefore.GetValueOrDefault(monitor), _selected[monitor]))
                Changed?.Invoke(monitor);
        }
    }

    /// <summary>A window came to the front. Nothing happens unless it's a client on a monitor we know.</summary>
    public void Foreground(long window)
    {
        if (!_clients.TryGetValue(window, out var client)) return;
        var monitor = client.MonitorKey;
        var namesBefore = NamesOn(monitor);
        var selectedBefore = _selected.GetValueOrDefault(monitor);
        if (!_lastClient.TryGetValue(monitor, out var before) || before != window) _lastGroup.Remove(monitor);
        _lastClient[monitor] = window;
        _lastCharacter[monitor] = client.Character;
        KeepGroup(monitor, client);
        if (client.Character != null) _selected[monitor] = client.Character;
        // Another client's place can have other characters to choose from, with the same one selected.
        if (!SameName(selectedBefore, _selected.GetValueOrDefault(monitor)) || !namesBefore.SequenceEqual(NamesOn(monitor)))
            Changed?.Invoke(monitor);
    }

    /// <summary>Picked by hand from the dropdown. Holds until the client in use changes or logs in again.</summary>
    public void Choose(string monitor, string name)
    {
        if (SameName(name, _selected.GetValueOrDefault(monitor))) return;
        if (!NamesOn(monitor).Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        _selected[monitor] = name;
        Changed?.Invoke(monitor);
    }

    private static bool SameName(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
