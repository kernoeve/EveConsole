using System.Text.Json;
using EveConsole.Agent.Providers;
using EveConsole.Services;

namespace EveConsole.Agent;

/// <summary>
/// A model's published price, looked up in LiteLLM's price list: a community-kept JSON of the
/// providers' prices — per token, per character, per second — each entry naming the pricing page
/// it was taken from.
///
/// <para>⚠️ The providers publish no price through their APIs; their model lists carry none. So
/// this is the capsuleer's choice of a third-party source, and what it gives is marked as such:
/// the note on the row names the list, the date, and the provider's page to check it against.
/// It only ever fills a row still holding the copied "(any model)" rate — never one set by hand,
/// nor one already looked up.</para>
/// </summary>
public static class PublishedRates
{
    public const string ListUrl  = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
    public const string ListName = "LiteLLM's price list";

    /// <summary>Per single unit, as ServiceRate keeps them; the page the price was taken from.</summary>
    public sealed record Rate(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite, string Source);

    // ⚠️ Not readonly, and only so a harness can put a client over a fake list in its place (see
    // ElevenLabsTtsService._http). Nothing in the application assigns it.
    private static HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>About 3 MB; kept on this PC and fetched again once it is a day old.</summary>
    private static string CachePath => Path.Combine(AppConfig.AppDataDir, "price-lists", "litellm.json");
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// The list: this PC's copy while it is under a day old, else fetched afresh — and the old copy
    /// if the fetch fails. Null when there is neither.
    /// </summary>
    public static async Task<JsonDocument?> LoadAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var cached = new FileInfo(CachePath);
            if (!cached.Exists || DateTime.UtcNow - cached.LastWriteTimeUtc > KeepFor)
            {
                try
                {
                    var bytes = await _http.GetByteArrayAsync(ListUrl, ct);
                    using (JsonDocument.Parse(bytes)) { }          // a whole list, or keep the old one
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                    var temp = CachePath + ".download";
                    await File.WriteAllBytesAsync(temp, bytes, ct);
                    File.Move(temp, CachePath, overwrite: true);
                }
                catch (Exception) when (!ct.IsCancellationRequested && File.Exists(CachePath)) { /* the copy we have */ }
            }
            return File.Exists(CachePath) ? JsonDocument.Parse(await File.ReadAllBytesAsync(CachePath, ct)) : null;
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// The published rate for one rate row — kind, provider as usage is recorded, model — in the
    /// row's own units; null when the list has none in that unit.
    /// </summary>
    public static Rate? Find(JsonDocument list, string kind, string provider, string model)
    {
        var (vendor, unit) = (kind, provider) switch
        {
            ("llm", ClaudeProvider.ServiceName)                  => ("anthropic", "input_cost_per_token"),
            ("llm", OpenAiCompatibleProvider.OpenAiName)         => ("openai",    "input_cost_per_token"),
            ("tts", nameof(TtsProvider.OpenAi))                  => ("openai",    "input_cost_per_character"),
            ("stt", nameof(SpeechInputProvider.OpenAiWhisper))   => ("openai",    "input_cost_per_second"),
            _                                                    => ("", ""),
        };
        if (vendor.Length == 0 || model.Length == 0) return null;

        // The model under its own name, as the provider's API gives it — not a reseller's copy of it
        // under another ("anthropic.claude-…" is Amazon's, at Amazon's price).
        foreach (var key in new[] { model, $"{vendor}/{model}" })
        {
            if (!list.RootElement.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Object) continue;
            if (Text(e, "litellm_provider") != vendor) continue;
            if (Number(e, unit) is not { } input) continue;            // priced in another unit: nothing to give
            return kind == "llm"
                ? new Rate(input, Number(e, "output_cost_per_token") ?? 0,
                           Number(e, "cache_read_input_token_cost") ?? 0,
                           Number(e, "cache_creation_input_token_cost") ?? 0,   // the five-minute write, as the seed has it
                           Text(e, "source"))
                : new Rate(input, 0, 0, 0, Text(e, "source"));
        }
        return null;
    }

    /// <summary>What a row priced from the list says of itself.</summary>
    public static string Note(Rate rate, DateTimeOffset when) =>
        $"From {ListName}, {when:yyyy-MM-dd}" + (rate.Source.Length > 0 ? $" — check it against {rate.Source}" : "") + ".";

    /// <summary>Whether a row's rate came from the list — and so is not the capsuleer's own.</summary>
    public static bool IsFromList(string notes) => notes.StartsWith($"From {ListName}", StringComparison.Ordinal);

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static decimal? Number(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        if (v.TryGetDecimal(out var d)) return d;
        return v.TryGetDouble(out var x) ? (decimal)x : null;       // "5e-7" and the like
    }
}
