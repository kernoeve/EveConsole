using System.Collections.Concurrent;
using System.IO;
using Avalonia.Media.Imaging;

namespace EveConsole.Services;

internal static class EveImageCache
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();

    public static Task<Bitmap?> GetAsync(string url)
        => _cache.GetOrAdd(url, static u => FetchAsync(u));

    private static async Task<Bitmap?> FetchAsync(string url)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
            await using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch { return null; }
    }
}
