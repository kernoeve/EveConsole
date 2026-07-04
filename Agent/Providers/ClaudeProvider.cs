using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EveCortex.Agent;
using EveCortex.Agent.Tools;

namespace EveCortex.Agent.Providers;

public sealed class ClaudeProvider : IAgentProvider
{
    private const string Endpoint         = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    private const int    MaxToolRounds    = 5;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly string _apiKey;
    private readonly string _model;

    public string ProviderName => "Claude (Anthropic)";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public ClaudeProvider(string apiKey, string model = "claude-sonnet-4-6")
    {
        _apiKey = apiKey;
        _model  = model;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string                     systemPrompt,
        IReadOnlyList<AgentMessage> history,
        IReadOnlyList<IAgentTool>? tools = null,
        [EnumeratorCancellation]
        CancellationToken          ct    = default)
    {
        var rawMessages = history
            .Select(m => (object)new
            {
                role    = m.Role == MessageRole.User ? "user" : "assistant",
                content = m.Content,
            })
            .ToList();

        var toolMap = tools?.ToDictionary(t => t.Name)
                      ?? new Dictionary<string, IAgentTool>();

        await foreach (var chunk in StreamRoundAsync(systemPrompt, rawMessages, toolMap, MaxToolRounds, ct))
            yield return chunk;
    }

    private async IAsyncEnumerable<string> StreamRoundAsync(
        string                         systemPrompt,
        List<object>                   rawMessages,
        Dictionary<string, IAgentTool> toolMap,
        int                            maxRounds,
        [EnumeratorCancellation]
        CancellationToken              ct)
    {
        using var request  = BuildRequest(systemPrompt, rawMessages, toolMap);
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using  var reader      = new StreamReader(stream);

        var blockTypes  = new Dictionary<int, string>();
        var textBuffers = new Dictionary<int, StringBuilder>();
        var toolIds     = new Dictionary<int, string>();
        var toolNames   = new Dictionary<int, string>();
        var toolInputs  = new Dictionary<int, StringBuilder>();
        var stopReason  = "";

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null || !line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            JsonElement root;
            try   { using var doc = JsonDocument.Parse(data); root = doc.RootElement.Clone(); }
            catch { continue; }

            if (!root.TryGetProperty("type", out var typeProp)) continue;
            switch (typeProp.GetString())
            {
                case "content_block_start":
                {
                    var idx = root.GetProperty("index").GetInt32();
                    var cb  = root.GetProperty("content_block");
                    var bt  = cb.GetProperty("type").GetString() ?? "";
                    blockTypes[idx] = bt;
                    if (bt == "text")
                        textBuffers[idx] = new StringBuilder();
                    else if (bt == "tool_use")
                    {
                        toolIds[idx]    = cb.TryGetProperty("id",   out var tid)   ? tid.GetString()   ?? "" : "";
                        toolNames[idx]  = cb.TryGetProperty("name", out var tname) ? tname.GetString() ?? "" : "";
                        toolInputs[idx] = new StringBuilder();
                    }
                    break;
                }

                case "content_block_delta":
                {
                    var idx   = root.GetProperty("index").GetInt32();
                    var delta = root.GetProperty("delta");
                    switch (delta.TryGetProperty("type", out var dt) ? dt.GetString() : "")
                    {
                        case "text_delta":
                        {
                            var text = delta.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                            if (textBuffers.TryGetValue(idx, out var tb)) tb.Append(text);
                            if (text.Length > 0) yield return text;
                            break;
                        }
                        case "input_json_delta":
                        {
                            var partial = delta.TryGetProperty("partial_json", out var pj) ? pj.GetString() ?? "" : "";
                            if (toolInputs.TryGetValue(idx, out var ti)) ti.Append(partial);
                            break;
                        }
                    }
                    break;
                }

                case "message_delta":
                {
                    var delta = root.GetProperty("delta");
                    if (delta.TryGetProperty("stop_reason", out var sr))
                        stopReason = sr.GetString() ?? "";
                    break;
                }
            }
        }

        // ── Tool use follow-up round ─────────────────────────────────────────
        if (stopReason == "tool_use" && maxRounds > 0
            && toolInputs.Count > 0 && !ct.IsCancellationRequested)
        {
            // Reconstruct assistant content blocks for the follow-up request
            var contentParts = new List<object>();
            foreach (var idx in blockTypes.Keys.Order())
            {
                if (blockTypes[idx] == "text"
                    && textBuffers.TryGetValue(idx, out var tb) && tb.Length > 0)
                {
                    contentParts.Add(new { type = "text", text = tb.ToString() });
                }
                else if (blockTypes[idx] == "tool_use" && toolIds.TryGetValue(idx, out var tid))
                {
                    contentParts.Add(new
                    {
                        type  = "tool_use",
                        id    = tid,
                        name  = toolNames.GetValueOrDefault(idx, ""),
                        input = ParseJsonElement(toolInputs.GetValueOrDefault(idx, new StringBuilder()).ToString()),
                    });
                }
            }
            rawMessages.Add(new { role = "assistant", content = contentParts });

            // Execute tools
            var toolResults = new List<object>();
            foreach (var idx in toolInputs.Keys)
            {
                var toolName = toolNames.GetValueOrDefault(idx, "");
                var toolId   = toolIds.GetValueOrDefault(idx, "");
                AgentToolResult result;
                try
                {
                    var inputEl = ParseJsonElement(toolInputs[idx].ToString());
                    result = toolMap.TryGetValue(toolName, out var tool)
                        ? await tool.ExecuteWithResultAsync(inputEl, ct)
                        : (AgentToolResult)$"Tool '{toolName}' is not available.";
                }
                catch (Exception ex) { result = $"Tool error: {ex.Message}"; }

                if (result.ImageBase64 is not null)
                {
                    toolResults.Add(new
                    {
                        type = "tool_result",
                        tool_use_id = toolId,
                        content = new object[]
                        {
                            new { type = "text", text = result.Text },
                            new { type = "image", source = new
                            {
                                type       = "base64",
                                media_type = result.ImageMediaType,
                                data       = result.ImageBase64,
                            }},
                        },
                    });
                }
                else
                {
                    toolResults.Add(new { type = "tool_result", tool_use_id = toolId, content = result.Text });
                }
            }
            rawMessages.Add(new { role = "user", content = toolResults });

            await foreach (var chunk in StreamRoundAsync(systemPrompt, rawMessages, toolMap, maxRounds - 1, ct))
                yield return chunk;
        }
    }

    private HttpRequestMessage BuildRequest(
        string systemPrompt, List<object> messages, Dictionary<string, IAgentTool> toolMap)
    {
        var toolDefs = toolMap.Count > 0
            ? (object)toolMap.Values.Select(t => new
            {
                name         = t.Name,
                description  = t.Description,
                input_schema = t.InputSchema,
            }).ToArray()
            : null;

        var bodyObj = toolDefs is not null
            ? (object)new { model = _model, max_tokens = 4096, system = systemPrompt, messages, tools = toolDefs, stream = true }
            : new { model = _model, max_tokens = 4096, system = systemPrompt, messages, stream = true };

        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(bodyObj), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key",         _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Headers.Add("Accept",            "text/event-stream");
        return request;
    }

    private static JsonElement ParseJsonElement(string json)
    {
        var source = string.IsNullOrWhiteSpace(json) ? "{}" : json;
        try   { using var doc = JsonDocument.Parse(source); return doc.RootElement.Clone(); }
        catch { using var doc = JsonDocument.Parse("{}");   return doc.RootElement.Clone(); }
    }
}
