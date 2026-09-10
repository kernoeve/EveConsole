using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EveConsole.Agent;
using EveConsole.Agent.Tools;

namespace EveConsole.Agent.Providers;

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
        IReadOnlyList<IAgentTool>? tools   = null,
        Action<UsageReport>?       onUsage = null,
        string?                    volatileContext = null,
        [EnumeratorCancellation]
        CancellationToken          ct      = default)
    {
        // ── The second cache breakpoint: the conversation so far ─────────────
        //
        // Measured before this existed: system and tools cached at 14,636 tokens, and a further
        // ~18,500 uncached on EVERY round of EVERY turn — the history, which is allowed to grow
        // to SummarizationThreshold and persists across restarts. A six-character question paid
        // 18,870 input tokens to answer in eight.
        //
        // A marker on the LAST history message caches everything before it too, so the prefix is
        // tools + system + the whole conversation. Within a turn the follow-up rounds read all of
        // it and pay only for the tool results appended after; the next turn reads it again and
        // moves its own marker forward. History only ever grows by appending, which is what makes
        // the prefix stable enough for this to hit.
        var rawMessages = new List<object>();
        for (var i = 0; i < history.Count; i++)
        {
            var m    = history[i];
            var role = m.Role == MessageRole.User ? "user" : "assistant";

            // ⚠️ Only the last one, and only when it has text. An empty text block is rejected by
            // the API, and a marker on every message would spend all four breakpoints on the
            // cheapest possible saving.
            if (i == history.Count - 1 && !string.IsNullOrEmpty(m.Content))
            {
                rawMessages.Add(new
                {
                    role,
                    content = new object[]
                    {
                        new { type = "text", text = m.Content, cache_control = new { type = "ephemeral" } },
                    },
                });
            }
            else
            {
                rawMessages.Add(new { role, content = m.Content });
            }
        }

        var toolMap = tools?.ToDictionary(t => t.Name)
                      ?? new Dictionary<string, IAgentTool>();

        await foreach (var chunk in StreamRoundAsync(
                           systemPrompt, volatileContext, rawMessages, toolMap, MaxToolRounds, onUsage, ct))
            yield return chunk;
    }

    private async IAsyncEnumerable<string> StreamRoundAsync(
        string                         systemPrompt,
        string?                        volatileContext,
        List<object>                   rawMessages,
        Dictionary<string, IAgentTool> toolMap,
        int                            maxRounds,
        Action<UsageReport>?           onUsage,
        [EnumeratorCancellation]
        CancellationToken              ct)
    {
        var roundStarted = System.Diagnostics.Stopwatch.StartNew();

        using var request  = BuildRequest(systemPrompt, volatileContext, rawMessages, toolMap);
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

        // Usage arrives split across two events, which is why it is easy to miss half of it:
        // message_start carries the input and cache counts, message_delta the output count.
        long inTok = 0, outTok = 0, cacheRead = 0, cacheWrite = 0;

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
                // ⚠️ The input side of the bill, and the only place it appears. Without this the
                // prompt — system prompt, schema, tool definitions, history, every round — is
                // counted as zero, which is the larger half of the spend on a tool-using turn.
                case "message_start":
                {
                    if (root.TryGetProperty("message", out var msg)
                        && msg.TryGetProperty("usage", out var u))
                    {
                        inTok      = ReadLong(u, "input_tokens");
                        cacheRead  = ReadLong(u, "cache_read_input_tokens");
                        cacheWrite = ReadLong(u, "cache_creation_input_tokens");
                    }
                    break;
                }

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

                    // The output side. Cumulative for the message, so assign rather than add.
                    if (root.TryGetProperty("usage", out var u))
                        outTok = ReadLong(u, "output_tokens");
                    break;
                }
            }
        }

        // ⚠️ Reported here, before the tool round below recurses, so the reports arrive in the
        // order the calls were made. Reporting after the recursion would nest them backwards and
        // make a turn's round trips read last-first.
        onUsage?.Invoke(new UsageReport
        {
            Provider         = ProviderName,
            Model            = _model,
            IsLocal          = false,
            InputTokens      = inTok,
            OutputTokens     = outTok,
            CacheReadTokens  = cacheRead,
            CacheWriteTokens = cacheWrite,
            IsEstimated      = false,   // Anthropic reports these exactly.
            StopReason       = stopReason,
            DurationMs       = (int)roundStarted.ElapsedMilliseconds,
        });

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

            await foreach (var chunk in StreamRoundAsync(
                               systemPrompt, volatileContext, rawMessages, toolMap, maxRounds - 1, onUsage, ct))
                yield return chunk;
        }
    }

    private HttpRequestMessage BuildRequest(
        string systemPrompt, string? volatileContext,
        List<object> messages, Dictionary<string, IAgentTool> toolMap)
    {
        var toolDefs = toolMap.Count > 0
            ? (object)toolMap.Values.Select(t => new
            {
                name         = t.Name,
                description  = t.Description,
                input_schema = t.InputSchema,
            }).ToArray()
            : null;

        // ── Prompt caching ───────────────────────────────────────────────────
        //
        // The request's cacheable prefix runs tools → system → messages, and a cache_control
        // breakpoint caches everything up to and INCLUDING the block it sits on. So one marker on
        // the stable system block covers the tool definitions as well, which is most of the
        // weight: seventeen schemas and the app reference come to roughly 33k tokens, and before
        // this they were re-sent at full price on every round trip of every turn. A single
        // question that used two tools paid for all of it three times.
        //
        // ⚠️ Any changing text must come AFTER the breakpoint. A cache hit needs a byte-identical
        // prefix, so folding live UI state into the stable prompt would invalidate the entry every
        // turn — caching would look enabled and never once be read. That is why volatileContext is
        // a separate argument rather than something the caller concatenates.
        var systemBlocks = new List<object>
        {
            new
            {
                type          = "text",
                text          = systemPrompt,
                cache_control = new { type = "ephemeral" },
            },
        };

        if (!string.IsNullOrWhiteSpace(volatileContext))
            systemBlocks.Add(new { type = "text", text = "\n\n## Current App State\n" + volatileContext });

        var bodyObj = toolDefs is not null
            ? (object)new { model = _model, max_tokens = 4096, system = systemBlocks, messages, tools = toolDefs, stream = true }
            : new { model = _model, max_tokens = 4096, system = systemBlocks, messages, stream = true };

        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(bodyObj), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key",         _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Headers.Add("Accept",            "text/event-stream");
        return request;
    }

    /// <summary>
    /// A usage count, or zero when the provider did not send it.
    ///
    /// <para>⚠️ Absent rather than zero is the normal case for the cache fields — they appear only
    /// when prompt caching is actually in play — so a missing property must read as nothing
    /// consumed, not as a parse failure that loses the whole report.</para>
    /// </summary>
    private static long ReadLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : 0;

    private static JsonElement ParseJsonElement(string json)
    {
        var source = string.IsNullOrWhiteSpace(json) ? "{}" : json;
        try   { using var doc = JsonDocument.Parse(source); return doc.RootElement.Clone(); }
        catch { using var doc = JsonDocument.Parse("{}");   return doc.RootElement.Clone(); }
    }
}
