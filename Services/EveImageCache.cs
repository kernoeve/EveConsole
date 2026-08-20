using System.Collections.Concurrent;
using System.IO;
using Avalonia.Media.Imaging;

namespace EveConsole.Services;

internal static class EveImageCache
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();

    /// <summary>
    /// How many images may be in flight at once.
    ///
    /// The callers ask for everything at the same instant — a system page requests portraits,
    /// corp logos, alliance logos and hulls for every pilot on every intel row, which runs to
    /// well over a thousand distinct URLs. Launching them all together does not make any of them
    /// arrive sooner: they queue on the connection pool and on the far end, and the ones at the
    /// back of a queue that deep start hitting the ten-second timeout — at which point they fail
    /// and, before the fix below, failed permanently.
    ///
    /// Twelve is enough to saturate the link without building a queue long enough to time out.
    /// </summary>
    private static readonly SemaphoreSlim _gate = new(12, 12);

    /// <summary>
    /// Cached by URL, storing the Task rather than the result, so fifty rows wanting the same
    /// alliance logo share a single fetch instead of racing for it.
    /// </summary>
    public static Task<Bitmap?> GetAsync(string url)
        => _cache.GetOrAdd(url, static u => FetchAsync(u));

    private static async Task<Bitmap?> FetchAsync(string url)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
            await using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            // Drop the failed attempt from the cache. It holds the Task, so a timeout or a
            // transient error would otherwise be remembered as "this image does not exist" for
            // the rest of the session, and the icon would stay blank until a restart even once
            // the network recovered.
            _cache.TryRemove(url, out _);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
