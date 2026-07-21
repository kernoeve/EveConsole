using System.IO;
using System.Net.Http;
using Avalonia.Media.Imaging;

namespace EveConsole.Views;

internal static class EveImageLoader
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Dictionary<string, Bitmap?> _cache = [];

    // variant: "bp" (blueprint original), "bpc" (blueprint copy), "icon", "render"
    public static async Task<Bitmap?> LoadTypeAsync(long typeId, string variant = "icon")
    {
        if (typeId <= 0) return null;
        var url = $"https://images.evetech.net/types/{typeId}/{variant}?size=64";
        return await LoadAsync(url);
    }

    private static async Task<Bitmap?> LoadAsync(string url)
    {
        if (_cache.TryGetValue(url, out var cached)) return cached;

        try
        {
            var bytes = await _http.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            _cache[url] = bmp;
            return bmp;
        }
        catch
        {
            _cache[url] = null;
            return null;
        }
    }
}
