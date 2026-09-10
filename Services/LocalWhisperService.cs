using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace EveConsole.Services;

public sealed class LocalWhisperService : IDisposable
{
    private static readonly string ModelDir = Path.Combine(
        AppConfig.AppDataDir, "whisper-models");

    // ── The loaded model, kept between utterances ────────────────────────────
    //
    // ⚠️ WhisperFactory.FromPath reads the whole model off disk — 75 MB for tiny, 1.5 GB for
    // medium — and it was being done, and thrown away, on EVERY push-to-talk. Every dictated
    // sentence paid a full model load before a word of it was transcribed.
    //
    // Only the FACTORY is kept. A processor is still built per utterance: that is cheap next to
    // the model load, and it carries per-run state that is not worth reasoning about sharing.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string?         _loadedPath;
    private DateTime        _loadedStamp;

    public static readonly IReadOnlyList<(string Id, string Label)> Models =
    [
        ("tiny",   "Tiny (~75 MB)"),
        ("base",   "Base (~142 MB)"),
        ("small",  "Small (~466 MB)"),
        ("medium", "Medium (~1.5 GB)"),
    ];

    private static string ModelPath(string modelId) =>
        Path.Combine(ModelDir, $"ggml-{modelId}.bin");

    public bool IsModelDownloaded(string modelId) =>
        File.Exists(ModelPath(modelId));

    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelDir);
        var dest = ModelPath(modelId);
        var temp = dest + ".tmp";

        var ggmlType = modelId switch
        {
            "tiny"   => GgmlType.Tiny,
            "base"   => GgmlType.Base,
            "small"  => GgmlType.Small,
            "medium" => GgmlType.Medium,
            _        => GgmlType.Tiny,
        };

        using var http  = new HttpClient();
        var downloader  = new WhisperGgmlDownloader(http);
        var modelStream = await downloader.GetGgmlModelAsync(ggmlType);
        await using (modelStream)
        await using (var dst = File.Create(temp))
        {
            var buf  = new byte[81920];
            long got = 0;
            int  n;
            while ((n = await modelStream.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                got += n;
                // WhisperGgmlDownloader stream length not always available; report bytes
                progress?.Report(got);
            }
        }

        File.Move(temp, dest, overwrite: true);
    }

    /// <summary>
    /// Transcribes recorded audio locally.
    ///
    /// <para>⚠️ The whole body runs on the thread pool, and the Task.Run is load-bearing rather
    /// than decorative. Only the enumeration at the end is actually asynchronous —
    /// <c>WhisperFactory.FromPath</c> reads the model off disk and <c>Build</c> prepares the
    /// processor, both synchronously and both BEFORE the first await, so they ran on whatever
    /// thread called this. That caller is the push-to-talk handler on the UI thread, so releasing
    /// the key froze the window for a model load plus the length of the inference.</para>
    ///
    /// <para>An async method only leaves the caller's thread at its first suspension; work placed
    /// ahead of that runs inline however the method is named.</para>
    /// </summary>
    public Task<string?> TranscribeAsync(byte[] wavBytes, string modelId, CancellationToken ct = default)
        => Task.Run<string?>(async () =>
        {
            var path = ModelPath(modelId);
            if (!File.Exists(path)) return null;

            // ⚠️ Serialised. Two transcriptions at once would contend for the same native context,
            // and push-to-talk is inherently one at a time anyway — so the second waits rather
            // than racing. Held across the inference, not just the load, for that reason.
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Reloaded only when the model actually changed. The timestamp matters as well as
                // the path: re-downloading the SAME model writes a new file at the same location,
                // and keying on the path alone would go on using the old one for the session.
                var stamp = File.GetLastWriteTimeUtc(path);
                if (_factory is null || _loadedPath != path || _loadedStamp != stamp)
                {
                    _factory?.Dispose();
                    _factory     = WhisperFactory.FromPath(path);
                    _loadedPath  = path;
                    _loadedStamp = stamp;
                }

                await using var processor = _factory.CreateBuilder()
                    .WithLanguage("auto")
                    .Build();

                using var ms = new MemoryStream(wavBytes);
                var sb = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(ms, ct).ConfigureAwait(false))
                    sb.Append(segment.Text);

                return sb.ToString().Trim();
            }
            finally
            {
                _gate.Release();
            }
        }, ct);

    /// <summary>
    /// Releases the loaded model.
    ///
    /// <para>⚠️ Needed now that the factory outlives a call. It holds native memory measured in
    /// hundreds of megabytes, and before caching there was nothing to release because every
    /// factory was disposed at the end of the utterance that made it.</para>
    /// </summary>
    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
        _gate.Dispose();
    }
}
