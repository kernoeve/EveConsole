using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace EveConsole.Services;

/// <summary>One archived hourly snapshot, as listed in a day's index.json.</summary>
public sealed record ArchiveFile(string Name, string Url, long Size, DateTimeOffset FileTime);

/// <summary>
/// Reads EVE Ref's published archives at data.everef.net.
///
/// This exists because ESI serves only the current hour for the map statistics endpoints:
/// miss an hour and it is gone from ESI permanently. EVE Ref keeps hourly snapshots back to
/// 2017, which is what makes the app survivable being closed overnight.
///
/// The archived payloads are ESI's own JSON, verbatim, so the same DTOs deserialise both
/// sources. What the payload does NOT contain is a timestamp — the hour a snapshot belongs to
/// comes from the index's file_time, and nothing else.
/// </summary>
public class EveRefArchiveClient(IHttpClientFactory httpFactory, AppErrorLogger? errors = null)
{
    public const string BaseUrl = "https://data.everef.net/";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private HttpClient Client => httpFactory.CreateClient("everef");

    private sealed class IndexDto
    {
        [JsonPropertyName("files")] public List<IndexFileDto> Files { get; set; } = [];
    }

    private sealed class IndexFileDto
    {
        [JsonPropertyName("name")]      public string Name     { get; set; } = "";
        [JsonPropertyName("url")]       public string Url      { get; set; } = "";
        [JsonPropertyName("size")]      public long   Size     { get; set; }
        [JsonPropertyName("file_time")] public DateTimeOffset? FileTime { get; set; }
    }

    /// <summary>
    /// Lists the hourly snapshots published for one dataset on one UTC day.
    ///
    /// Filenames are read from the index rather than constructed: the capture second varies,
    /// and hours are occasionally absent altogether — 2026-08-01 has 24 files for system-jumps
    /// but only 22 for system-kills. A missing hour is normal, not an error.
    /// </summary>
    public async Task<List<ArchiveFile>> ListDayAsync(
        string dataset, DateOnly day, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}{dataset}/history/{day:yyyy}/{day:yyyy-MM-dd}/index.json";
        try
        {
            using var resp = await Client.GetAsync(url, ct);
            // A day before the dataset existed, or today before the first capture, simply
            // has no index yet.
            if (resp.StatusCode == HttpStatusCode.NotFound) return [];
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var index = await JsonSerializer.DeserializeAsync<IndexDto>(stream, Json, ct);

            return index?.Files
                .Where(f => f.FileTime.HasValue && f.Name.EndsWith(".json.bz2"))
                .Select(f => new ArchiveFile(f.Name, f.Url, f.Size, f.FileTime!.Value))
                .OrderBy(f => f.FileTime)
                .ToList() ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors?.Log("EveRef", $"index {dataset} {day:yyyy-MM-dd}", ex);
            return [];
        }
    }

    /// <summary>
    /// Downloads and decompresses one snapshot. Returns null on any failure, since a single
    /// unreadable hour should not abort a backfill spanning hundreds of them.
    /// </summary>
    public async Task<T?> GetSnapshotAsync<T>(ArchiveFile file, CancellationToken ct = default)
        where T : class
    {
        try
        {
            using var resp = await Client.GetAsync(file.Url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            await using var raw = await resp.Content.ReadAsStreamAsync(ct);

            // .NET has no bzip2 support (System.IO.Compression covers gzip, deflate, Brotli
            // and zlib only), hence SharpCompress. As of 0.50 BZip2Stream has no public
            // constructor — it is built through these factories.
            await using var bz = await BZip2Stream.CreateAsync(
                raw,
                CompressionMode.Decompress,
                decompressConcatenated: false,
                leaveOpen: true,              // `raw` is owned by the await using above
                tolerateTruncatedStream: false,
                ct);

            return await JsonSerializer.DeserializeAsync<T>(bz, Json, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors?.Log("EveRef", $"snapshot {file.Name}", ex);
            return null;
        }
    }

    /// <summary>The current uncompressed snapshot, for datasets where only "now" is wanted.</summary>
    public async Task<T?> GetLatestAsync<T>(string dataset, CancellationToken ct = default)
        where T : class
    {
        try
        {
            var url = $"{BaseUrl}{dataset}/{dataset}-latest.json";
            await using var stream = await Client.GetStreamAsync(url, ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors?.Log("EveRef", $"latest {dataset}", ex);
            return null;
        }
    }
}
