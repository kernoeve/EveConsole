using System.Collections.Concurrent;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Loads shared by every generator in one worklist build, fetched once.
/// </summary>
/// <remarks>
/// <para>The ten generators run under one <c>Task.WhenAll</c>, and each independently loads
/// the same things — the production context, the delivery-lag snapshot, the container
/// exclusions, the industry characters, the blueprint stock. Measured on the live server after
/// the per-item repeats were removed: 581 commands for a build, of which the great majority
/// were the same query issued by four to seven generators. At 180 ms a round trip over a remote
/// link, that repetition was most of what remained of the load.</para>
///
/// <para>The cache is an <see cref="AsyncLocal{T}"/> opened by <c>WorklistService.BuildAsync</c>
/// and flowing into every generator it starts — the same mechanism the ESI client uses to tell
/// a poll from a click. A shared load asks <see cref="GetOrAddAsync{T}"/> for its result by key;
/// the first generator to ask runs the query, and the rest await the same task. Outside a build
/// there is no cache and every call runs as it always did, so the calculator screen, the
/// inventory tool and the sale posting see no change.</para>
///
/// <para>⚠️ A cached result is one object handed to several generators running in parallel, so
/// only loads whose results are READ are cached — every caller checked before its load was
/// added here. Calculate copies the context's price table before overlaying it, for instance,
/// which is what makes the context shareable. A load that hands back something a caller
/// mutates must not be cached, or it would need to hand back a copy.</para>
///
/// <para>⚠️ The first caller's cancellation token is the one the shared query runs under. Inside
/// a build every generator holds the build's own token, so that is the same token; the cache
/// must not be opened around callers with tokens of their own.</para>
/// </remarks>
public sealed class BuildCache
{
    private static readonly AsyncLocal<BuildCache?> _current = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _entries = new();

    /// <summary>Whether a build is open on the current async flow.</summary>
    public static bool IsActive => _current.Value is not null;

    /// <summary>Opens a cache for the current async flow. Dispose to close it.</summary>
    public static IDisposable Begin()
    {
        var previous = _current.Value;
        _current.Value = new BuildCache();
        return new Scope(previous);
    }

    /// <summary>
    /// The result for <paramref name="key"/>, loading it with <paramref name="factory"/> the
    /// first time it is asked for inside this build — or every time, when there is no build.
    /// </summary>
    public static Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory)
    {
        var cache = _current.Value;
        if (cache is null) return factory();

        // Lazy so two generators asking at once start ONE query: ConcurrentDictionary may
        // invoke the value factory twice and keep one, but only the kept Lazy is ever forced.
        var lazy = cache._entries.GetOrAdd(key,
            _ => new Lazy<Task<object?>>(async () => await factory().ConfigureAwait(false)));
        return Unwrap<T>(lazy.Value);
    }

    private static async Task<T> Unwrap<T>(Task<object?> task) => (T)(await task.ConfigureAwait(false))!;

    private sealed class Scope(BuildCache? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
