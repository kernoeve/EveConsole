namespace EveConsole.Services;

/// <summary>
/// Every key the app listens for from anywhere (push-to-talk for the agent, each Mumble server's keys), wherever in
/// the app it is chosen. ⚠️ Each of those keys has a hook of its own, and every hook hears every press: one key
/// chosen in two places does both at once. So each place a key is chosen asks here whether another already has it.
/// </summary>
public static class GlobalKeys
{
    /// <summary>
    /// A key in use. <paramref name="Owner"/> says whose it is, so a place can leave out its own keys
    /// ("agent", or "mumble:" and the server's id). <paramref name="What"/> is shown: what it does and where it is set.
    /// </summary>
    public sealed record Use(string Owner, int Vk, string What);

    private static readonly object Gate = new();
    private static readonly List<Func<IEnumerable<Use>>> Sources = [];

    /// <summary>Adds where some keys are kept, read again at each question. Disposing it takes them away.</summary>
    public static IDisposable AddSource(Func<IEnumerable<Use>> source)
    {
        lock (Gate) Sources.Add(source);
        return new Removal(source);
    }

    /// <summary>Every key in use now, keys of none left out.</summary>
    public static IReadOnlyList<Use> All()
    {
        Func<IEnumerable<Use>>[] sources;
        lock (Gate) sources = [.. Sources];
        return [.. sources.SelectMany(s => s()).Where(u => u.Vk != 0)];
    }

    /// <summary>What else uses this key, leaving out the owners <paramref name="mine"/> says are the asker's own. Null if nothing does.</summary>
    public static Use? Holder(int vk, Func<string, bool> mine) =>
        vk == 0 ? null : All().FirstOrDefault(u => u.Vk == vk && !mine(u.Owner));

    /// <summary>The owner of a Mumble server's keys.</summary>
    public static string MumbleOwner(int serverId) => $"mumble:{serverId}";

    public const string AgentOwner = "agent";

    private sealed class Removal(Func<IEnumerable<Use>> source) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate) Sources.Remove(source);
        }
    }
}
