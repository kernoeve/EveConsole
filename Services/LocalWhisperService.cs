using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace EveConsole.Services;

public sealed class LocalWhisperService
{
    private static readonly string ModelDir = Path.Combine(
        AppConfig.AppDataDir, "whisper-models");

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

    public async Task<string?> TranscribeAsync(byte[] wavBytes, string modelId, CancellationToken ct = default)
    {
        var path = ModelPath(modelId);
        if (!File.Exists(path)) return null;

        using var factory   = WhisperFactory.FromPath(path);
        await using var processor = factory.CreateBuilder()
            .WithLanguage("auto")
            .Build();

        using var ms = new MemoryStream(wavBytes);
        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(ms, ct))
            sb.Append(segment.Text);

        return sb.ToString().Trim();
    }
}
