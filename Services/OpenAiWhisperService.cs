using System.Net.Http.Headers;

namespace EveConsole.Services;

public sealed class OpenAiWhisperService
{
    private static readonly HttpClient _http = new();

    /// <param name="model">As OpenAI's list names it, chosen on the settings tab — no name is written
    /// here (see ModelListing).</param>
    public async Task<string?> TranscribeAsync(byte[] wavBytes, string apiKey, string model, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(apiKey) || wavBytes.Length == 0) return null;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("No transcription model is chosen — choose one in Settings → Speech Input.");

        using var content = new MultipartFormDataContent();

        var fileContent = new ByteArrayContent(wavBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "recording.wav");
        content.Add(new StringContent(model.Trim()), "model");
        content.Add(new StringContent("text"), "response_format");

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.openai.com/v1/audio/transcriptions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = content;

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var text = await resp.Content.ReadAsStringAsync(ct);
        return text.Trim();
    }
}
