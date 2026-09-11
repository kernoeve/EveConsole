using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EveConsole.Agent.Tools;

namespace EveConsole.Agent.Providers;

/// <summary>
/// The OpenAI Chat Completions API, and everything that speaks it.
///
/// <para>One provider for two settings. OpenAI's own service and a local model server (Ollama,
/// LM Studio, vLLM) present the same wire protocol — <c>/v1/chat/completions</c>, the same
/// message shapes, the same streamed chunks, the same tool-call encoding — and differ in a base
/// URL, a key, and what they can be trusted to report. Writing them twice would be two copies of
/// one parser drifting apart.</para>
///
/// <para>⚠️ Behaviour matches <see cref="ClaudeProvider"/> where the capsuleer would notice a
/// difference: the same round budget, the same wrap-up round when it runs out, the same message
/// when the output ceiling is hit, the same stamps on the capsuleer's turns, usage reported once
/// per round trip in call order. What differs is what the API differs in.</para>
///
/// <para>Prompt caching here is automatic — the service caches a matching prefix on its own,
/// nothing is marked — so the only thing this provider has to get right is ORDER: the stable
/// system text first, the live app state after it, history appended in order. That is the same
/// discipline the Claude provider needs, for the same reason.</para>
/// </summary>
public sealed class OpenAiCompatibleProvider : IAgentProvider
{
    private const int MaxToolRounds   = 20;
    private const int MaxOutputTokens = 16384;

    /// <summary>Same wording as the Claude provider's; the capsuleer should not be able to tell.</summary>
    private const string BudgetExhausted =
        "NOT RUN: the tool budget for this turn is used up, and no further tool calls will be run. " +
        "Answer the capsuleer NOW from what you have already found — say what it shows, and say " +
        "plainly what you did not get to — so that a follow-up can pick up from there. Do not " +
        "apologise at length; one sentence on what is missing is enough.";

    // ⚠️ Not readonly, for the same reason as ClaudeProvider._http: tools/AgentStreamCheck swaps
    // it for a client over a fake server, and .NET 9 refuses a reflection write to an initonly
    // static. Nothing in the application assigns it.
    private static HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly bool   _isLocal;

    public string ProviderName { get; }
    public bool   IsConfigured => _isLocal
        ? !string.IsNullOrWhiteSpace(_baseUrl) && !string.IsNullOrWhiteSpace(_model)
        : !string.IsNullOrWhiteSpace(_apiKey);

    private OpenAiCompatibleProvider(string providerName, string baseUrl, string apiKey, string model, bool isLocal)
    {
        ProviderName = providerName;
        _baseUrl     = baseUrl;
        _apiKey      = apiKey;
        _model       = model;
        _isLocal     = isLocal;
    }

    /// <summary>OpenAI's own service.</summary>
    public static OpenAiCompatibleProvider OpenAi(string apiKey, string model)
        => new("OpenAI", "https://api.openai.com/v1/", apiKey ?? "", string.IsNullOrWhiteSpace(model) ? "gpt-4o" : model.Trim(), isLocal: false);

    /// <summary>
    /// A model server on this machine or the network.
    ///
    /// <para>The setting is the server's root — <c>http://localhost:11434</c> for Ollama — and
    /// the OpenAI-compatible surface lives under <c>/v1/</c>. A user who already typed the /v1
    /// (LM Studio's own examples do) is not made to have it twice. The bearer token is a
    /// placeholder: Ollama ignores it, and some servers refuse a request without one.</para>
    /// </summary>
    public static OpenAiCompatibleProvider Local(string endpoint, string model)
    {
        var root = (endpoint ?? "").Trim().TrimEnd('/');
        if (!root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root += "/v1";
        return new("Local", root + "/", "ollama", string.IsNullOrWhiteSpace(model) ? "llama3.1" : model.Trim(), isLocal: true);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string                      systemPrompt,
        IReadOnlyList<AgentMessage> history,
        IReadOnlyList<IAgentTool>?  tools           = null,
        Action<UsageReport>?        onUsage         = null,
        string?                     volatileContext = null,
        [EnumeratorCancellation]
        CancellationToken           ct              = default)
    {
        // ── The prompt, in cache order ────────────────────────────────────────
        //
        // The service caches whatever prefix it has seen before, so the stable text goes first
        // and the history is appended after it exactly as it was last time.
        //
        // ⚠️ The live app state goes LAST, as its own system message after the newest user turn
        // — not inside the first one. It carries the clock, so it differs every turn, and the
        // first version of this provider folded it into the system prompt: the cache then ended
        // at that line, and the tools and the whole history after it — 12,500 tokens — were
        // re-sent uncached on every turn. Measured: 11,136 cached of 23,700 on each new turn.
        // Trailing, only it changes; the prefix through the newest user message is byte-identical
        // to the previous request and hits.
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var m in history)
            messages.Add(new
            {
                role    = m.Role == MessageRole.User ? "user" : "assistant",
                content = m.ContentForModel,
            });
        if (!string.IsNullOrWhiteSpace(volatileContext))
            messages.Add(new { role = "system", content = "## Current App State\n" + volatileContext });

        var toolMap = tools?.ToDictionary(t => t.Name) ?? new Dictionary<string, IAgentTool>();

        // ⚠️ ConfigureAwait(false) on the enumeration — see ClaudeProvider for what happens
        // without it: the outer enumeration captured the UI context and the whole round ran on
        // the UI thread. tools/AgentStreamCheck runs this provider too.
        await foreach (var chunk in StreamRoundAsync(messages, toolMap, MaxToolRounds, onUsage, ct)
                       .ConfigureAwait(false))
            yield return chunk;
    }

    private async IAsyncEnumerable<string> StreamRoundAsync(
        List<object>                   messages,
        Dictionary<string, IAgentTool> toolMap,
        int                            maxRounds,
        Action<UsageReport>?           onUsage,
        [EnumeratorCancellation]
        CancellationToken              ct)
    {
        var roundStarted = System.Diagnostics.Stopwatch.StartNew();

        using var request  = BuildRequest(messages, toolMap);
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The service's own words: a wrong model name, a key without access, a local server
            // that does not know the model. Those are what the capsuleer needs to see.
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"{ProviderName} returned {(int)response.StatusCode}: {Trim(body, 400)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using  var reader      = new StreamReader(stream);

        var text       = new StringBuilder();
        var calls      = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        var stopReason = "";
        long promptTok = 0, completionTok = 0, cachedTok = 0;
        var usageSeen  = false;

        // ⚠️ No EndOfStream — a synchronous, blocking read. ReadLineAsync returns null at the end.
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..].Trim();
            if (data == "[DONE]") break;

            JsonElement root;
            try   { using var doc = JsonDocument.Parse(data); root = doc.RootElement.Clone(); }
            catch { continue; }

            // ── Usage: the final chunk, when the request asked for it ────────
            //
            // ⚠️ prompt_tokens INCLUDES the cached part; Anthropic's input_tokens excludes it.
            // Split here so the ledger means the same thing for both: input is what was paid for
            // in full, cache reads are what was paid for at the discount.
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                promptTok     = ReadLong(u, "prompt_tokens");
                completionTok = ReadLong(u, "completion_tokens");
                if (u.TryGetProperty("prompt_tokens_details", out var d) && d.ValueKind == JsonValueKind.Object)
                    cachedTok = ReadLong(d, "cached_tokens");
                usageSeen = promptTok > 0 || completionTok > 0;
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                stopReason = fr.GetString() switch
                {
                    "tool_calls"     => "tool_use",
                    "length"         => "max_tokens",
                    "stop"           => "end_turn",
                    var other        => other ?? "",
                };

            if (!choice.TryGetProperty("delta", out var delta)) continue;

            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var piece = c.GetString() ?? "";
                if (piece.Length > 0) { text.Append(piece); yield return piece; }
            }

            // Tool calls arrive as fragments keyed by index: the first fragment carries the id
            // and name, every fragment carries a slice of the JSON arguments. Some local servers
            // send the whole call in one fragment; the same accumulation handles both.
            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var idx = tc.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : calls.Count;
                    if (!calls.TryGetValue(idx, out var call))
                        call = calls[idx] = ("", "", new StringBuilder());

                    var id = tc.TryGetProperty("id", out var tid) && tid.ValueKind == JsonValueKind.String ? tid.GetString() ?? "" : "";
                    if (id.Length > 0) call.Id = id;

                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && (n.GetString() ?? "").Length > 0)
                            call.Name = n.GetString()!;
                        if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                            call.Args.Append(a.GetString());
                    }
                    calls[idx] = call;
                }
            }
        }

        // A model that produced tool calls but no finish_reason (seen from some local servers)
        // still wants them run.
        if (stopReason.Length == 0 && calls.Count > 0) stopReason = "tool_use";

        // ── Usage, measured or estimated — never a guess filed as a measurement ──
        //
        // OpenAI reports usage when stream_options asks for it; a local server may report it,
        // may not, or may report zeros. Absent that, a character count divided by four is a
        // serviceable estimate, and IsEstimated says that is what it is.
        long inTok, outTok;
        var  estimated = !usageSeen;
        if (usageSeen)
        {
            inTok  = Math.Max(0, promptTok - cachedTok);
            outTok = completionTok;
        }
        else
        {
            inTok  = EstimateTokens(messages) + EstimateTokens(toolMap);
            outTok = (text.Length + calls.Values.Sum(v => v.Args.Length + v.Name.Length)) / 4;
        }

        onUsage?.Invoke(new UsageReport
        {
            Provider         = ProviderName,
            Model            = _model,
            IsLocal          = _isLocal,
            InputTokens      = inTok,
            OutputTokens     = outTok,
            CacheReadTokens  = cachedTok,
            CacheWriteTokens = 0,        // the service does not distinguish a write from a plain read-miss
            IsEstimated      = estimated,
            StopReason       = stopReason,
            DurationMs       = (int)roundStarted.ElapsedMilliseconds,
        });

        // ── Ending conditions, worded exactly as the Claude provider words them ──

        if (stopReason == "tool_use" && maxRounds < 0 && !ct.IsCancellationRequested)
        {
            yield return "\n\n(I ran out of steps before I could finish that one — I was still " +
                         "working through it rather than stuck. Ask again and I will pick up from " +
                         "what I already found.)";
            yield break;
        }

        if (stopReason == "max_tokens" && !ct.IsCancellationRequested)
        {
            yield return calls.Count > 0
                ? "\n\n(That answer was too long to finish in one go — I ran out of room while writing " +
                  "it out, so the tab never opened. Ask for it narrowed down, or for it in parts, and I " +
                  "will have the data ready.)"
                : "\n\n(I ran out of room before I could finish that answer. Ask me to continue and I " +
                  "will pick up where I left off.)";
            yield break;
        }

        if (stopReason != "tool_use" || calls.Count == 0 || maxRounds < 0 || ct.IsCancellationRequested)
            yield break;

        // ── Tool use follow-up round ──────────────────────────────────────────

        // The assistant turn as the API wants it echoed back: any text, then the calls.
        var toolCalls = calls.Select((kv, i) => new
        {
            id       = kv.Value.Id.Length > 0 ? kv.Value.Id : $"call_{i}",
            type     = "function",
            function = new { name = kv.Value.Name, arguments = kv.Value.Args.Length > 0 ? kv.Value.Args.ToString() : "{}" },
        }).ToList();
        messages.Add(new { role = "assistant", content = text.Length > 0 ? text.ToString() : null, tool_calls = toolCalls });

        // One tool message per call, in order. An image result cannot ride in a tool message —
        // the API takes only text there — so it follows as a user message the model can see,
        // on a service that can look at pictures; a local model is told what it missed.
        var images = new List<object>();
        foreach (var (call, i) in toolCalls.Select((c, i) => (c, i)))
        {
            AgentToolResult result;
            if (maxRounds == 0)
            {
                result = BudgetExhausted;
            }
            else
            {
                try
                {
                    var inputEl = ParseJsonElement(call.function.arguments);
                    result = toolMap.TryGetValue(call.function.name, out var tool)
                        ? await tool.ExecuteWithResultAsync(inputEl, ct).ConfigureAwait(false)
                        : (AgentToolResult)$"Tool '{call.function.name}' is not available.";
                }
                catch (Exception ex) { result = $"Tool error: {ex.Message}"; }
            }

            messages.Add(new { role = "tool", tool_call_id = call.id, content = result.Text });

            if (result.ImageBase64 is not null)
            {
                if (_isLocal)
                    messages.Add(new { role = "user", content = $"(The {call.function.name} result included an image, which this provider cannot see.)" });
                else
                    images.Add(new
                    {
                        type      = "image_url",
                        image_url = new { url = $"data:{result.ImageMediaType};base64,{result.ImageBase64}" },
                    });
            }
        }
        if (images.Count > 0)
            messages.Add(new
            {
                role    = "user",
                content = new List<object> { new { type = "text", text = "The image from the tool result:" } }.Concat(images).ToList(),
            });

        await foreach (var chunk in StreamRoundAsync(messages, toolMap, maxRounds - 1, onUsage, ct)
                       .ConfigureAwait(false))
            yield return chunk;
    }

    private HttpRequestMessage BuildRequest(List<object> messages, Dictionary<string, IAgentTool> toolMap)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"]          = _model,
            ["messages"]       = messages,
            ["stream"]         = true,
            ["stream_options"] = new { include_usage = true },
        };

        // ⚠️ Two names for one limit. OpenAI retired max_tokens in favour of max_completion_tokens
        // and its newer models reject the old name; the local servers mostly know only the old one.
        if (_isLocal) body["max_tokens"] = MaxOutputTokens;
        else          body["max_completion_tokens"] = MaxOutputTokens;

        // Omitted rather than empty when there are none: the summariser passes no tools, and an
        // empty array is not accepted everywhere.
        if (toolMap.Count > 0)
            body["tools"] = toolMap.Values.Select(t => new
            {
                type     = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.InputSchema },
            }).ToArray();

        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Add("Accept", "text/event-stream");
        return request;
    }

    /// <summary>Roughly four characters to a token — the estimate used only when the service reports nothing.</summary>
    private static long EstimateTokens(object graph) => JsonSerializer.Serialize(graph).Length / 4;

    private static long ReadLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static JsonElement ParseJsonElement(string json)
    {
        var source = string.IsNullOrWhiteSpace(json) ? "{}" : json;
        try   { using var doc = JsonDocument.Parse(source); return doc.RootElement.Clone(); }
        catch { using var doc = JsonDocument.Parse("{}");   return doc.RootElement.Clone(); }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
