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
    /// <summary>
    /// How many times a single question may go back to the model after using a tool.
    ///
    /// <para>⚠️ Raised from 5, which was set when the agent had a schema handed to it and could
    /// go more or less straight to a query. It now discovers: a describe_tables costs a round, a
    /// failed query costs another, and a recovery costs a third. Measured on a real question —
    /// count and value the killmails in a region — it spent its budget on describe, a failed
    /// query, a recovery, a successful query, a second failed query and a describe, and ran out
    /// one step from the answer.</para>
    /// </summary>
    /// <summary>
    /// Tool calls per turn before the model is told to stop and answer.
    ///
    /// <para>⚠️ Twelve was not enough for a real listing question. Kills, their attackers, five
    /// kinds of name to resolve, values to compute, one SQL mistake to recover from — that is
    /// fourteen calls done well, and the capsuleer saw "I ran out of steps" twice. show_query
    /// collapses most of that into one call, but the ceiling is for the questions it does not.</para>
    /// </summary>
    private const int    MaxToolRounds    = 20;

    /// <summary>
    /// What a tool call receives instead of a result once the budget is spent.
    ///
    /// <para>⚠️ The budget used to end the turn on a canned apology, and everything the model had
    /// found in twelve rounds of queries was lost with it — "continue" started from nothing,
    /// because tool results live only inside the turn. Now the last call is answered with this
    /// instead of being run, and the model gets ONE more round to write an answer from what it
    /// already holds. That answer is what goes into the history, so a follow-up has it.</para>
    /// </summary>
    private const string BudgetExhausted =
        "NOT RUN: the tool budget for this turn is used up, and no further tool calls will be run. " +
        "Answer the capsuleer NOW from what you have already found — say what it shows, and say " +
        "plainly what you did not get to — so that a follow-up can pick up from there. Do not " +
        "apologise at length; one sentence on what is missing is enough.";

    /// <summary>
    /// The output ceiling per round.
    ///
    /// <para>⚠️ Was 4,096, which the output tools made a real limit: a table is written INTO the
    /// tool call, at roughly a hundred tokens a row, so a ninety-row answer was cut off inside
    /// the JSON and never shown. A ceiling costs nothing unless it is reached — output is billed
    /// as generated — so this is set for the answers these tools exist to carry, not for chat.</para>
    /// </summary>
    private const int    MaxOutputTokens  = 16384;

    // ── Cache lifetime ───────────────────────────────────────────────────────
    //
    // The default entry lives five minutes from the start of the last request that touched
    // it; the one-hour entry costs 2× to write against 1.25× and reads the same. Measured on
    // one capsuleer's day: eleven turns began after a gap, five in the 5–60 minute window
    // where the hour turns a re-write into a read, six after gaps of 1.5 to 11 hours where it
    // makes no difference — close enough that it is a setting, not a decision made here.
    //
    // ⚠️ When the hour is chosen it goes only on the stable entries (system, history), never
    // on the per-round tool-result marker; the API requires longer-lived markers to PRECEDE
    // shorter ones. tools/AgentStreamCheck asserts that at both settings.
    private readonly bool _hourCache;

    /// <summary>The marker for a stable entry, at whichever lifetime is configured.</summary>
    private object StableMarker => _hourCache
        ? new { type = "ephemeral", ttl = "1h" }
        : new { type = "ephemeral" };

    // ⚠️ Not readonly, and only for one reason: tools/AgentStreamCheck replaces it with a client
    // over a fake server so the real streaming path can be run headless, and .NET 9 refuses a
    // reflection write to an initonly static once the type is initialised. Nothing in the
    // application assigns it.
    private static HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly string _apiKey;
    private readonly string _model;

    public string ProviderName => "Claude (Anthropic)";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    /// <param name="cacheTtl">"5m" or "1h"; anything else is the default.</param>
    public ClaudeProvider(string apiKey, string model = "claude-sonnet-4-6", string cacheTtl = "5m")
    {
        _apiKey    = apiKey;
        _model     = model;
        _hourCache = string.Equals(cacheTtl, "1h", StringComparison.OrdinalIgnoreCase);
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

            // ⚠️ Each of the capsuleer's messages carries when it was sent. Without it the model
            // has no way to tell a question asked five minutes ago from one asked five weeks ago
            // — the history reads as one continuous present — and "what did I just ask" or "since
            // we last spoke" cannot be answered. The stamp is fixed at the moment the message was
            // written, so the cached prefix is unchanged by it; and only the capsuleer's turns are
            // stamped, because a stamp on the model's own past replies teaches it to write one.
            var text = m.ContentForModel;

            // ⚠️ Only the last one, and only when it has text. An empty text block is rejected by
            // the API, and a marker on every message would spend all four breakpoints on the
            // cheapest possible saving.
            if (i == history.Count - 1 && !string.IsNullOrEmpty(text))
            {
                rawMessages.Add(new
                {
                    role,
                    content = new object[]
                    {
                        new { type = "text", text, cache_control = StableMarker },
                    },
                });
            }
            else
            {
                rawMessages.Add(new { role, content = text });
            }
        }

        var toolMap = tools?.ToDictionary(t => t.Name)
                      ?? new Dictionary<string, IAgentTool>();

        // ⚠️ ConfigureAwait(false) here too — this was the one await in the provider without it,
        // and it undid every other one. This enumeration first suspends while still inline on the
        // UI thread (the click that started the turn), so it captured Avalonia's synchronization
        // context, and from the first chunk onward every resumption of this method was posted
        // back to the UI thread as a dispatcher job. Once there it called the inner enumerator
        // synchronously, and the inner loop — reading a socket that already had data — never
        // yielded the thread again for the rest of the round. A tool call that took the model
        // forty seconds to write froze the window for forty seconds, and the streaming-text posts
        // queued behind the job could not run while speech, fed directly from the loop, carried
        // on: the capsuleer heard text they could not yet see. The UI-thread stack during a
        // freeze read WndProc → DispatcherOperation → StreamAsync.MoveNext → … → Winsock.recv.
        await foreach (var chunk in StreamRoundAsync(
                           systemPrompt, volatileContext, rawMessages, toolMap, MaxToolRounds, onUsage, ct)
                       .ConfigureAwait(false))
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

        // ⚠️ ConfigureAwait(false) on everything below, and it is not a stylistic preference.
        // This is reached from a UI command, so without it every await resumes on the UI thread —
        // including ReadLineAsync in the loop, which runs ONCE PER SERVER-SENT EVENT. A streamed
        // answer is hundreds of them, each dragging its JSON parse and string building onto the
        // thread that is trying to draw, and tool execution with them: query_database ran its SQL
        // on the UI thread. That is what made the window stop responding while the agent thought.
        //
        // The caller marshals its own UI updates through the Dispatcher, which is where that
        // belongs — one hop per visible change rather than one per network packet.
        using var request  = BuildRequest(systemPrompt, volatileContext, rawMessages, toolMap);
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
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

        // ⚠️ No EndOfStream. It is a synchronous property that answers by doing a BLOCKING read
        // on the underlying socket — which on a response that is still streaming means sitting in
        // Winsock.recv until the next event arrives, on whatever thread asked. ReadLineAsync
        // returns null at the end of the stream, which is the same question asked properly.
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data: ")) continue;
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

        // ⚠️ Running out of rounds used to end the turn in silence. The model had asked for
        // another tool, this method simply returned, and the capsuleer was left with whatever text
        // preceded the last call — usually "let me check that" and then nothing, indistinguishable
        // from the app having hung. Whatever else happens, the loop gets closed.
        // Reached only when the model was given its wrap-up round — the one whose tool calls
        // are answered with BudgetExhausted — and asked for a tool anyway.
        if (stopReason == "tool_use" && maxRounds < 0 && !ct.IsCancellationRequested)
        {
            yield return "\n\n(I ran out of steps before I could finish that one — I was still " +
                         "working through it rather than stuck. Ask again and I will pick up from " +
                         "what I already found.)";
            yield break;
        }

        // ⚠️ Same silence, different cause. When the model hits the output-token ceiling the
        // stop reason is max_tokens, and if it was inside a tool call at the time — writing a
        // ninety-row table into show_table, say — the tool never runs, the JSON is unfinished,
        // and the turn ends on whatever text came before: "let me assemble the table", then
        // nothing. The telemetry showed one of these as eleven rounds and two minutes of work
        // that reached the capsuleer as no result at all.
        if (stopReason == "max_tokens" && !ct.IsCancellationRequested)
        {
            yield return toolInputs.Count > 0
                ? "\n\n(That answer was too long to finish in one go — I ran out of room while writing " +
                  "it out, so the tab never opened. Ask for it narrowed down, or for it in parts, and I " +
                  "will have the data ready.)"
                : "\n\n(I ran out of room before I could finish that answer. Ask me to continue and I " +
                  "will pick up where I left off.)";
            yield break;
        }

        if (stopReason == "tool_use" && maxRounds >= 0
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
            var toolResults = new List<ToolResultBlock>();
            foreach (var idx in toolInputs.Keys)
            {
                var toolName = toolNames.GetValueOrDefault(idx, "");
                var toolId   = toolIds.GetValueOrDefault(idx, "");
                AgentToolResult result;
                if (maxRounds == 0)
                {
                    // The budget is spent: the call is answered, not run, and the round that
                    // follows is the model's chance to answer from what it has.
                    result = BudgetExhausted;
                }
                else
                {
                    try
                    {
                        var inputEl = ParseJsonElement(toolInputs[idx].ToString());
                        result = toolMap.TryGetValue(toolName, out var tool)
                            ? await tool.ExecuteWithResultAsync(inputEl, ct).ConfigureAwait(false)
                            : (AgentToolResult)$"Tool '{toolName}' is not available.";
                    }
                    catch (Exception ex) { result = $"Tool error: {ex.Message}"; }
                }

                toolResults.Add(new ToolResultBlock(toolId, result));
            }
            rawMessages.Add(new ToolResultsMessage(toolResults));

            await foreach (var chunk in StreamRoundAsync(
                               systemPrompt, volatileContext, rawMessages, toolMap, maxRounds - 1, onUsage, ct)
                           .ConfigureAwait(false))
                yield return chunk;
        }
    }

    /// <summary>One tool's answer, kept typed until the request is built.</summary>
    private sealed record ToolResultBlock(string ToolUseId, AgentToolResult Result);

    /// <summary>
    /// The user-role message carrying a round's tool results.
    ///
    /// <para>A record rather than an anonymous block so <see cref="BuildRequest"/> can find the
    /// LAST one and put the third cache breakpoint on it. Marking it where it is created would
    /// leave every earlier round's marker in place too, and the API allows four in total.</para>
    /// </summary>
    private sealed record ToolResultsMessage(List<ToolResultBlock> Results);

    /// <summary>
    /// A tool-results message as the API wants it, with the cache marker on its final block when
    /// asked.
    ///
    /// <para>⚠️ The third breakpoint, and it moves. Measured on a ten-round turn before it existed:
    /// the static prefix and the history were cache hits, but everything after them — every
    /// earlier round's tool call and result — was re-sent at full price on each round, growing
    /// from 392 tokens to 8,080. With the marker on the newest result, the next round reads all
    /// of that from cache and writes only its own delta. The lookup also checks the block
    /// boundaries before the marker, so last round's entry is found even though its marker is
    /// gone.</para>
    /// </summary>
    private static object ToApiMessage(ToolResultsMessage message, bool markLast)
    {
        var blocks = new List<object>(message.Results.Count);
        for (int i = 0; i < message.Results.Count; i++)
        {
            var (toolId, result) = message.Results[i];
            var mark = markLast && i == message.Results.Count - 1;

            object content = result.ImageBase64 is not null
                ? new object[]
                {
                    new { type = "text", text = result.Text },
                    new { type = "image", source = new
                    {
                        type       = "base64",
                        media_type = result.ImageMediaType,
                        data       = result.ImageBase64,
                    }},
                }
                : result.Text;

            blocks.Add(mark
                ? new { type = "tool_result", tool_use_id = toolId, content, cache_control = new { type = "ephemeral" } }
                : new { type = "tool_result", tool_use_id = toolId, content });
        }
        return new { role = "user", content = blocks };
    }

    private HttpRequestMessage BuildRequest(
        string systemPrompt, string? volatileContext,
        List<object> rawMessages, Dictionary<string, IAgentTool> toolMap)
    {
        // Only the newest tool-results message carries a marker; see ToApiMessage.
        var lastResults = rawMessages.LastOrDefault(m => m is ToolResultsMessage);
        var messages    = rawMessages
            .Select(m => m is ToolResultsMessage t ? ToApiMessage(t, markLast: ReferenceEquals(t, lastResults)) : m)
            .ToList();

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
                cache_control = StableMarker,
            },
        };

        if (!string.IsNullOrWhiteSpace(volatileContext))
            systemBlocks.Add(new { type = "text", text = "\n\n## Current App State\n" + volatileContext });

        var bodyObj = toolDefs is not null
            ? (object)new { model = _model, max_tokens = MaxOutputTokens, system = systemBlocks, messages, tools = toolDefs, stream = true }
            : new { model = _model, max_tokens = MaxOutputTokens, system = systemBlocks, messages, stream = true };

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
