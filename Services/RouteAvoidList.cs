using System.Globalization;

namespace EveConsole.Services;

/// <summary>
/// The systems routes keep out of: kept on this machine (UiState), like the rest of a pilot's
/// planning preferences, and shared by everything that edits it — the route planner's list, and
/// the right-click on a system on the map or in a route.
/// </summary>
public static class RouteAvoidList
{
    private static HashSet<int>? _ids;
    private static readonly object Gate = new();

    /// <summary>Raised after the list changed, from whichever thread changed it.</summary>
    public static event Action? Changed;

    private static HashSet<int> Load()
    {
        if (_ids is { } ids) return ids;
        var text = UiState.Get(UiState.RouteAvoid) ?? "";
        _ids = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                   .Where(id => id > 0)
                   .ToHashSet();
        return _ids;
    }

    /// <summary>The system ids on the list, as they stand now.</summary>
    public static IReadOnlyCollection<int> Ids
    {
        get { lock (Gate) return [.. Load()]; }
    }

    public static bool Contains(int systemId)
    {
        lock (Gate) return Load().Contains(systemId);
    }

    public static void Add(int systemId)    => Change(ids => ids.Add(systemId));
    public static void Remove(int systemId) => Change(ids => ids.Remove(systemId));

    /// <summary>On the list if it was not, off it if it was.</summary>
    public static void Toggle(int systemId) => Change(ids => { if (!ids.Remove(systemId)) ids.Add(systemId); return true; });

    private static void Change(Func<HashSet<int>, bool> edit)
    {
        lock (Gate)
        {
            var ids = Load();
            if (!edit(ids)) return;
            UiState.Set(UiState.RouteAvoid, string.Join(",", ids.Order().Select(i => i.ToString(CultureInfo.InvariantCulture))));
        }
        Changed?.Invoke();
    }
}
