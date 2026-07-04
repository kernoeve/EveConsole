using System.Net.Http.Headers;

namespace EveCortex.Services;

public sealed class OpenAiWhisperService
{
    private static readonly HttpClient _http = new();

    public async Task<string?> TranscribeAsync(byte[] wavBytes, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(apiKey) || wavBytes.Length == 0) return null;

        using var content = new MultipartFormDataContent();

        var fileContent = new ByteArrayContent(wavBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "recording.wav");
        content.Add(new StringContent("whisper-1"), "model");
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
