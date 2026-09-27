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

    /// <summary>A local reasoning model may think before it answers; false tells it not to.</summary>
    private readonly bool   _think;

    // The loaded model's context window, from Ollama's /api/ps. Asked after each local round —
    // one small GET to a server on the LAN, and the answer can change if the server is restarted
    // with a different length — and never again once a server has said it has no such route.
    private int? _contextLength;
    private bool _contextProbeSupported = true;

    public string ProviderName { get; }
    public bool   IsConfigured => _isLocal
        ? !string.IsNullOrWhiteSpace(_baseUrl) && !string.IsNullOrWhiteSpace(_model)
        : !string.IsNullOrWhiteSpace(_apiKey);

    private OpenAiCompatibleProvider(string providerName, string baseUrl, string apiKey, string model, bool isLocal,
                                     bool think = true)
    {
        ProviderName = providerName;
        _baseUrl     = baseUrl;
        _apiKey      = apiKey;
        _model       = model;
        _isLocal     = isLocal;
        _think       = think;
    }

    /// <summary>OpenAI's own service.</summary>
    public static OpenAiCompatibleProvider OpenAi(string apiKey, string model)
        => new("OpenAI", "https://api.openai.com/v1/", apiKey ?? "", string.IsNullOrWhiteSpace(model) ? "gpt-5" : model.Trim(), isLocal: false);

    /// <summary>
    /// A model server on this machine or the network.
    ///
    /// <para>The setting is the server's root — <c>http://localhost:11434</c> for Ollama — and
    /// the OpenAI-compatible surface lives under <c>/v1/</c>. A user who already typed the /v1
    /// (LM Studio's own examples do) is not made to have it twice. The bearer token is a
    /// placeholder: Ollama ignores it, and some servers refuse a request without one.</para>
    /// </summary>
    /// <param name="think">
    /// Whether a reasoning model — Qwen3 — may think before it answers. The thinking is hidden
    /// from the answer either way (see <see cref="ThinkingFilter"/>), but it is waited for. False
    /// ends the system prompt with Qwen3's own switch, "/no_think", which other models pass over.
    /// </param>
    public static OpenAiCompatibleProvider Local(string endpoint, string model, bool think = true)
    {
        var root = (endpoint ?? "").Trim().TrimEnd('/');
        if (!root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root += "/v1";
        return new("Local", root + "/", "ollama", string.IsNullOrWhiteSpace(model) ? "llama3.1" : model.Trim(),
                   isLocal: true, think: think);
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
        // Qwen3's switch goes at the END of the stable text: the same on every call, so the cached
        // prefix is unchanged by it, and last is where Qwen3 looks for the latest instruction.
        if (_isLocal && !_think) systemPrompt += "\n\n/no_think";

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
            // With the status, so a role can tell a server that is down from a request it refused.
            throw new HttpRequestException(
                $"{ProviderName} returned {(int)response.StatusCode}: {Trim(body, 400)}", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using  var reader      = new StreamReader(stream);

        var text       = new StringBuilder();
        var calls      = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        var stopReason = "";
        long promptTok = 0, completionTok = 0, cachedTok = 0;
        var usageSeen  = false;

        // A local reasoning model's thinking, where the server streams it inside the answer.
        var thinking   = _isLocal ? new ThinkingFilter() : null;

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
                if (thinking is not null) piece = thinking.Push(piece);
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

        // What the thinking filter held back at the end in case it began a tag, and did not.
        if (thinking?.Flush() is { Length: > 0 } rest) { text.Append(rest); yield return rest; }

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

        // The window is worth knowing only where the prompt can silently outgrow it, which is a
        // local server; and only once a round has completed, which is what guarantees the model
        // is loaded and therefore listed.
        if (_isLocal && usageSeen)
            await ProbeContextLengthAsync(ct).ConfigureAwait(false);

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
            ContextLength    = _contextLength,
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

    /// <summary>
    /// The context window the model is currently loaded with, from Ollama's <c>/api/ps</c>,
    /// which sits beside the OpenAI-compatible surface on the same server. Best-effort: a server
    /// that is not Ollama answers 404 and is not asked again; any failure leaves the last answer.
    ///
    /// <para>⚠️ The window is a property of the LOADED model, not of the model file — Ollama
    /// sizes it from OLLAMA_CONTEXT_LENGTH (or the request) at load time, and the same file
    /// can be running at 4k on one machine and 64k on another. That is why this is asked of the
    /// running server rather than looked up.</para>
    /// </summary>
    private async Task ProbeContextLengthAsync(CancellationToken ct)
    {
        if (!_contextProbeSupported) return;
        try
        {
            // _baseUrl is the server root plus "v1/"; Ollama's own API is the sibling "api/".
            var root = _baseUrl[..^"v1/".Length];
            using var response = await _http.GetAsync(root + "api/ps", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { _contextProbeSupported = false; return; }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return;

            foreach (var m in models.EnumerateArray())
            {
                var name  = m.TryGetProperty("name",  out var n)  && n.ValueKind  == JsonValueKind.String ? n.GetString()  ?? "" : "";
                var model = m.TryGetProperty("model", out var mm) && mm.ValueKind == JsonValueKind.String ? mm.GetString() ?? "" : "";
                if (!IsOurModel(name) && !IsOurModel(model)) continue;
                if (m.TryGetProperty("context_length", out var c) && c.ValueKind == JsonValueKind.Number)
                    _contextLength = c.GetInt32();
                return;
            }
        }
        catch { /* the window is a courtesy; the turn already succeeded without it */ }
    }

    /// <summary>
    /// Takes a local reasoning model's thinking out of its answer. Qwen3 and DeepSeek-R1 think
    /// between &lt;think&gt; tags before they answer, and over Ollama's OpenAI-compatible surface
    /// the thinking can arrive inside the answer text, where it would fill the chat and be read
    /// aloud. The tags may be split across streamed pieces, so a piece that ends in what could be
    /// the start of one is held back until the next shows whether it is. It is also kept out of
    /// the assistant turn echoed back after a tool call, as the model's own makers advise.
    /// </summary>
    private sealed class ThinkingFilter
    {
        private const string Open  = "<think>";
        private const string Close = "</think>";

        private bool   _inside;
        private bool   _answered;   // anything shown yet; the blank lines after thinking are not
        private string _carry = "";

        public string Push(string piece)
        {
            var text = _carry + piece;
            _carry   = "";
            var shown = new StringBuilder();
            var i     = 0;
            while (i < text.Length)
            {
                var tag = _inside ? Close : Open;
                var at  = text.IndexOf(tag, i, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    // Hold back a tail that may be the start of the tag.
                    var keep = PartialTag(text, i, tag);
                    if (!_inside) shown.Append(text, i, text.Length - i - keep);
                    _carry = text[(text.Length - keep)..];
                    break;
                }
                if (!_inside) shown.Append(text, i, at - i);
                i       = at + tag.Length;
                _inside = !_inside;
            }

            var result = shown.ToString();
            if (!_answered)
            {
                result = result.TrimStart();
                _answered = result.Length > 0;
            }
            return result;
        }

        /// <summary>What was held back, at the end of the stream: text that did not start a tag.</summary>
        public string Flush()
        {
            var rest = _inside ? "" : _carry;
            _carry = "";
            return _answered ? rest : rest.TrimStart();
        }

        /// <summary>How much of the end of <paramref name="text"/> could be the start of <paramref name="tag"/>.</summary>
        private static int PartialTag(string text, int from, string tag)
        {
            for (var n = Math.Min(tag.Length - 1, text.Length - from); n > 0; n--)
                if (string.Compare(text, text.Length - n, tag, 0, n, StringComparison.OrdinalIgnoreCase) == 0)
                    return n;
            return 0;
        }
    }

    /// <summary>
    /// The service's model list, which costs nothing to read. OpenAI's own service: that the key
    /// is accepted. A server of our own: that it lists this model — Ollama answers a request for a
    /// model it has not pulled with a 404, so a server that is up without it cannot answer either.
    /// ⚠️ Not a request for an answer: that would load the model onto the GPU every half minute
    /// while a role waits to go back to it, and keep it there.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "models");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                                            .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            if (!_isLocal) return true;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                && data.EnumerateArray().Any(m => m.TryGetProperty("id", out var id)
                                                 && id.ValueKind == JsonValueKind.String
                                                 && IsOurModel(id.GetString() ?? ""));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
        catch (JsonException)        { return false; }
    }

    /// <summary>
    /// Ollama lists "qwen2.5:7B" for a setting of "qwen2.5:7b", and "llama3.1:latest" for a
    /// setting that names no tag at all.
    /// </summary>
    private bool IsOurModel(string loaded)
        => loaded.Equals(_model, StringComparison.OrdinalIgnoreCase)
        || (!_model.Contains(':') && loaded.Equals(_model + ":latest", StringComparison.OrdinalIgnoreCase));

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
