using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EveConsole.Agent;
using EveConsole.Agent.Providers;
using EveConsole.Agent.Tools;

// ═══════════════════════════════════════════════════════════════════════════════════════════
// Does a streamed agent turn stay off the UI thread? — for EVERY provider.
//
// ⚠️ This has failed twice, the same way each time: one await somewhere in the streaming path
// without ConfigureAwait(false). The first time it was the provider's read loop, and the window
// stopped responding while the agent thought. The second time it was the OUTER enumeration in
// StreamAsync — the only await left without it — and it undid every other one: the enumeration
// first suspended while still inline on the UI thread, captured Avalonia's synchronization
// context, and from the first chunk onward every resumption was a dispatcher job on the UI
// thread. Once there, the inner loop read a socket that already had data and never yielded the
// thread again for the rest of the round. A tool call the model took forty seconds to write
// froze the window for forty seconds, and the streaming-text posts queued behind that job could
// not run while speech, fed straight from the loop, carried on — the capsuleer HEARD text they
// could not yet SEE.
//
// Neither failure is visible in a review. Every await in the file can carry ConfigureAwait(false)
// and one new one without it, added in a later change, silently reintroduces the whole thing.
//
// So this runs each REAL provider against a fake server that speaks its wire protocol — a text
// round, slow tool-use rounds and a closing round — starting the turn from a thread that has a
// SynchronizationContext installed, exactly as the click that starts a real turn does. The
// context counts every Post() it receives. The correct number is zero.
//
// The same fakes also check what each provider puts on the wire: the Anthropic cache markers
// (count, position, TTL order — at both TTL settings) and the OpenAI tool-call round trip
// (fragments reassembled, the assistant turn echoed, tool results keyed by id, usage split into
// paid and cached).
// ═══════════════════════════════════════════════════════════════════════════════════════════

var failures = new List<string>();

failures.AddRange(Scenario.Run("Claude, 5-minute cache",
    handler => Seam(typeof(ClaudeProvider), handler),
    () => new ClaudeProvider("not-a-real-key"),
    new FakeAnthropic(), (h, _) => ((FakeAnthropic)h).Verify()));

failures.AddRange(Scenario.Run("Claude, 1-hour cache",
    handler => Seam(typeof(ClaudeProvider), handler),
    () => new ClaudeProvider("not-a-real-key", cacheTtl: "1h"),
    new FakeAnthropic(), (h, _) => ((FakeAnthropic)h).Verify(expectHourOnStable: true)));

failures.AddRange(Scenario.Run("OpenAI-compatible",
    handler => Seam(typeof(OpenAiCompatibleProvider), handler),
    () => OpenAiCompatibleProvider.OpenAi("not-a-real-key", "gpt-4o"),
    new FakeOpenAi(), (h, _) => ((FakeOpenAi)h).Verify()));

failures.AddRange(Scenario.Run("Local (Ollama)",
    handler => Seam(typeof(OpenAiCompatibleProvider), handler),
    () => OpenAiCompatibleProvider.Local("http://fake-ollama:11434", "qwen2.5:7b"),
    new FakeOllama(), (h, usage) => ((FakeOllama)h).VerifyLocal(usage)));

if (failures.Count == 0)
{
    Console.WriteLine();
    Console.WriteLine("Agent stream check: every provider ran its whole turn off the UI thread and put the right things on the wire.");
    return 0;
}

Console.WriteLine();
Console.WriteLine("Agent stream check FAILED.");
foreach (var f in failures) Console.WriteLine("  " + f);
return 1;

static void Seam(Type provider, HttpMessageHandler handler)
{
    var field = provider.GetField("_http", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"{provider.Name}._http not found — the seam this check depends on has moved.");
    field.SetValue(null, new HttpClient(handler));
}

// ─────────────────────────────────────────────────────────────────────────────────────────

static class Scenario
{
    /// <summary>One provider, one fake server: the threading assertions, then the fake's own.</summary>
    public static List<string> Run(string name, Action<HttpMessageHandler> seam, Func<IAgentProvider> make,
                                   IFakeServer server, Func<IFakeServer, IReadOnlyList<UsageReport>, List<string>> verifyWire)
    {
        var failures = new List<string>();
        Console.WriteLine($"══ {name} ══");

        var ui = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        var uiThread = Environment.CurrentManagedThreadId;

        seam((HttpMessageHandler)server);
        var provider = make();
        var tool     = new ThreadRecordingTool();
        var chunkThreads = new ConcurrentBag<int>();
        var text   = new StringBuilder();
        var usage  = new List<UsageReport>();

        // Consumed the way AgentPanelViewModel.SendAsync consumes it: started on the UI thread,
        // with ConfigureAwait(false) on the enumeration. The pump then runs the "UI thread"
        // until the turn completes, executing anything posted to it — and counting it.
        async Task Consume()
        {
            await foreach (var chunk in provider.StreamAsync("system", [new AgentMessage(MessageRole.User, "hi")], [tool],
                                                             onUsage: u => usage.Add(u),
                                                             volatileContext: "Now: 12:00")
                                                .ConfigureAwait(false))
            {
                text.Append(chunk);
                chunkThreads.Add(Environment.CurrentManagedThreadId);
            }
        }
        ui.RunUntil(Consume());
        SynchronizationContext.SetSynchronizationContext(null);

        var chunksOnUi = chunkThreads.Count(t => t == uiThread);
        var toolOnUi   = tool.ExecutedOn == uiThread;

        Console.WriteLine($"  rounds served         : {server.Requests}");
        Console.WriteLine($"  text streamed         : \"{text.ToString().Trim()}\"");
        Console.WriteLine($"  tool ran on UI thread : {toolOnUi}");
        Console.WriteLine($"  chunks on UI thread   : {chunksOnUi} of {chunkThreads.Count}");
        Console.WriteLine($"  posts to UI context   : {ui.Posts}");
        Console.WriteLine($"  usage reports         : {string.Join(", ", usage.Select(u => $"in={u.InputTokens} cached={u.CacheReadTokens} out={u.OutputTokens}{(u.IsEstimated ? " est" : "")} {u.StopReason}"))}");

        if (server.Requests != IFakeServer.ToolRounds + 1)
            failures.Add($"{name}: the fake server did not see every round — the turn did not complete.");
        if (!text.ToString().Contains("in the tab"))
            failures.Add($"{name}: the closing text never arrived.");
        if (ui.Posts > 0)
            failures.Add($"{name}: {ui.Posts} continuation(s) were posted to the UI context — an await in the streaming path has lost its ConfigureAwait(false).");
        if (toolOnUi)
            failures.Add($"{name}: the tool executed on the UI thread.");
        if (chunksOnUi > 0)
            failures.Add($"{name}: chunks were delivered on the UI thread.");
        if (usage.Count != IFakeServer.ToolRounds + 1)
            failures.Add($"{name}: expected one usage report per round, got {usage.Count}.");
        if (tool.Executions != IFakeServer.ToolRounds)
            failures.Add($"{name}: the tool ran {tool.Executions} time(s), expected {IFakeServer.ToolRounds}.");

        foreach (var f in verifyWire(server, usage)) failures.Add($"{name}: {f}");
        return failures;
    }
}

/// <summary>
/// A single-threaded context in the shape of a UI dispatcher: Post() queues work for the thread
/// that owns it, and nothing runs until that thread pumps.
/// </summary>
sealed class PumpContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private int _posts;

    public int Posts => _posts;

    public override void Post(SendOrPostCallback d, object? state)
    {
        Interlocked.Increment(ref _posts);
        _queue.Add((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("Send is not part of what this check models.");

    public void RunUntil(Task t)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!t.IsCompleted)
        {
            if (_queue.TryTake(out var item, 50)) item.Callback(item.State);
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The turn did not complete within 30 s.");
        }
        t.GetAwaiter().GetResult();
    }
}

sealed class ThreadRecordingTool : IAgentTool
{
    public int ExecutedOn = -1;
    public int Executions;

    public string Name        => "show_table";
    public string Description => "test";
    public object InputSchema => new { type = "object", properties = new { title = new { type = "string" } } };

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        ExecutedOn = Environment.CurrentManagedThreadId;
        Interlocked.Increment(ref Executions);
        return Task.FromResult("Opened a tab.");
    }
}

interface IFakeServer
{
    /// <summary>
    /// Tool-use rounds before the closing text round. Three, not one: the Anthropic cache-marker
    /// rule is about a marker being REMOVED from an earlier round, which two requests cannot show.
    /// </summary>
    const int ToolRounds = 3;

    int Requests { get; }
}

/// <summary>
/// Streams SSE the way each service does, with a pause between chunks. Genuinely asynchronous —
/// a handler returning a completed task lets the provider's first await complete synchronously,
/// so its loop runs inline on the CALLING thread until the pipe is empty, and whether the first
/// chunks land before that is a race. A real response never arrives before the request has left.
/// </summary>
abstract class FakeSse : HttpMessageHandler, IFakeServer
{
    private int _requests;
    public int Requests => _requests;

    protected abstract Task WriteRoundAsync(Func<string, Task> write, int round, string body, CancellationToken ct);

    /// <summary>
    /// Anything that is not a chat round — the provider asking a local server about itself. A
    /// server that has no such route says so, which is what every non-Ollama server does.
    /// </summary>
    protected virtual Task<HttpResponseMessage> SideRequestAsync(HttpRequestMessage req, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    protected sealed override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        await Task.Delay(20, ct).ConfigureAwait(false);
        if (req.Method == HttpMethod.Get) return await SideRequestAsync(req, ct).ConfigureAwait(false);
        var round = Interlocked.Increment(ref _requests);
        var body  = await req.Content!.ReadAsStringAsync(ct);
        var pipe  = new Pipe();

        _ = Task.Run(async () =>
        {
            async Task Write(string data)
            {
                await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes($"data: {data}\n\n"), ct);
                await pipe.Writer.FlushAsync(ct);
            }
            await WriteRoundAsync(Write, round, body, ct);
            await pipe.Writer.CompleteAsync();
        }, ct);

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
        response.Content.Headers.ContentType = new("text/event-stream");
        return response;
    }
}

/// <summary>Anthropic's event stream, and a record of the cache markers each request carried.</summary>
sealed class FakeAnthropic : FakeSse
{
    /// <summary>Per request, in document order: each marker's TTL in minutes, and whether it sat on a tool_result.</summary>
    private readonly List<List<(int TtlMinutes, bool OnToolResult)>> _markers = [];

    protected override async Task WriteRoundAsync(Func<string, Task> write, int round, string body, CancellationToken ct)
    {
        lock (_markers)
            _markers.Add(Regex.Matches(body, "(\"type\":\"tool_result\"[^}]*)?\"cache_control\":\\{[^}]*\\}")
                              .Select(m => (m.Value.Contains("\"ttl\":\"1h\"") ? 60 : 5, m.Groups[1].Success))
                              .ToList());

        await write("""{"type":"message_start","message":{"usage":{"input_tokens":10,"cache_read_input_tokens":100,"cache_creation_input_tokens":5}}}""");
        await write("""{"type":"content_block_start","index":0,"content_block":{"type":"text"}}""");
        var sentence = round <= IFakeServer.ToolRounds ? "Let me assemble the table." : "Done, it is in the tab.";
        foreach (var word in sentence.Split(' '))
        {
            await write("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(word + " ") + "}}");
            await Task.Delay(10, ct);
        }
        await write("""{"type":"content_block_stop","index":0}""");

        if (round <= IFakeServer.ToolRounds)
        {
            await write("""{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"t1","name":"show_table"}}""");
            var input = "{\"title\":\"" + new string('x', 300) + "\"}";
            foreach (var piece in input.Chunk(6))
            {
                await write("{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(new string(piece)) + "}}");
                await Task.Delay(8, ct);
            }
            await write("""{"type":"content_block_stop","index":1}""");
            await write("""{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":50}}""");
        }
        else
            await write("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}""");

        await write("""{"type":"message_stop"}""");
    }

    /// <summary>
    /// ⚠️ At most four markers per request, or the whole request is rejected. The third marker
    /// moves forward with the newest tool result each round, so every EARLIER round's marker has
    /// to be gone — a mistake there is invisible on a one-round answer and fatal to every longer
    /// one. The API also requires longer-lived markers to PRECEDE shorter ones, and when the hour
    /// is chosen it must land on the stable entries and never on the per-round tool result.
    /// </summary>
    public List<string> Verify(bool expectHourOnStable = false)
    {
        var failures = new List<string>();
        Console.WriteLine("  cache markers         : " + string.Join(", ", _markers.Select(r =>
            $"{r.Count}{(r.Any(m => m.OnToolResult) ? " (one on a tool_result)" : "")} [{string.Join("/", r.Select(m => m.TtlMinutes + "m"))}]")));

        if (_markers.Count != IFakeServer.ToolRounds + 1) failures.Add("did not capture every request's markers");
        if (_markers.Count > 0 && _markers[0].Count != 2)
            failures.Add($"the first request carried {_markers[0].Count} cache markers, expected 2 (system, last history message)");
        foreach (var (r, i) in _markers.Skip(1).Select((r, i) => (r, i + 2)))
            if (r.Count != 3 || !r.Last().OnToolResult)
                failures.Add($"request {i} carried {r.Count} cache markers, expected exactly 3 with the last on the newest tool result");

        foreach (var (r, i) in _markers.Select((r, i) => (r, i + 1)))
        {
            for (int k = 1; k < r.Count; k++)
                if (r[k].TtlMinutes > r[k - 1].TtlMinutes)
                    failures.Add($"request {i}: a longer-lived cache marker follows a shorter one, which the API rejects");
            foreach (var m in r)
            {
                if (m.OnToolResult && m.TtlMinutes != 5) failures.Add($"request {i}: the tool-result marker must stay on the 5-minute default");
                if (!m.OnToolResult && m.TtlMinutes != (expectHourOnStable ? 60 : 5))
                    failures.Add($"request {i}: a stable marker has the wrong TTL for this setting");
            }
        }
        return failures;
    }
}

/// <summary>
/// The OpenAI Chat Completions stream: content deltas, a tool call delivered as fragments, a
/// usage chunk at the end. Records each request so the round trip can be checked.
/// </summary>
class FakeOpenAi : FakeSse
{
    protected readonly List<string> _bodies = [];

    protected override async Task WriteRoundAsync(Func<string, Task> write, int round, string body, CancellationToken ct)
    {
        lock (_bodies) _bodies.Add(body);

        var sentence = round <= IFakeServer.ToolRounds ? "Let me assemble the table." : "Done, it is in the tab.";
        foreach (var word in sentence.Split(' '))
        {
            await write("{\"choices\":[{\"index\":0,\"delta\":{\"content\":" + JsonSerializer.Serialize(word + " ") + "},\"finish_reason\":null}]}");
            await Task.Delay(10, ct);
        }

        if (round <= IFakeServer.ToolRounds)
        {
            // The first fragment names the call; the rest carry slices of its JSON arguments.
            await write("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_abc","type":"function","function":{"name":"show_table","arguments":""}}]},"finish_reason":null}]}""");
            var input = "{\"title\":\"" + new string('x', 300) + "\"}";
            foreach (var piece in input.Chunk(6))
            {
                await write("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":" + JsonSerializer.Serialize(new string(piece)) + "}}]},\"finish_reason\":null}]}");
                await Task.Delay(8, ct);
            }
            await write("""{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""");
        }
        else
            await write("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");

        // Usage: the final chunk, with no choices, as the service sends it when asked.
        await write("""{"choices":[],"usage":{"prompt_tokens":1200,"completion_tokens":40,"prompt_tokens_details":{"cached_tokens":1000}}}""");
        await write("[DONE]");
    }

    public List<string> Verify()
    {
        var failures = new List<string>();
        if (_bodies.Count < 2) { failures.Add("fewer than two requests captured"); return failures; }

        using var second = JsonDocument.Parse(_bodies[1]);
        var msgs = second.RootElement.GetProperty("messages").EnumerateArray().ToList();

        // The echoed assistant turn: its text, and the tool call reassembled from its fragments.
        var assistant = msgs.LastOrDefault(m => m.GetProperty("role").GetString() == "assistant" && m.TryGetProperty("tool_calls", out _));
        var toolMsg   = msgs.LastOrDefault(m => m.GetProperty("role").GetString() == "tool");
        if (assistant.ValueKind == JsonValueKind.Undefined) failures.Add("the assistant turn with its tool_calls was not echoed back");
        else
        {
            var call = assistant.GetProperty("tool_calls")[0];
            var args = call.GetProperty("function").GetProperty("arguments").GetString() ?? "";
            if (call.GetProperty("id").GetString() != "call_abc")                               failures.Add("the tool call's id was not carried through");
            if (call.GetProperty("function").GetProperty("name").GetString() != "show_table")   failures.Add("the tool call's name was not carried through");
            if (!args.StartsWith("{\"title\":\"xxx") || !args.EndsWith("\"}"))               failures.Add("the tool call's arguments were not reassembled from their fragments");
            if (assistant.GetProperty("content").GetString()?.Contains("assemble") != true)     failures.Add("the assistant's text was not echoed alongside its tool call");
        }
        if (toolMsg.ValueKind == JsonValueKind.Undefined)                                        failures.Add("no tool message was sent back");
        else if (toolMsg.GetProperty("tool_call_id").GetString() != "call_abc")                  failures.Add("the tool message is not keyed by the call's id");
        else if (toolMsg.GetProperty("content").GetString() != "Opened a tab.")                  failures.Add("the tool message does not carry the tool's result");

        using var firstDoc = JsonDocument.Parse(_bodies[0]);
        var first = firstDoc.RootElement;
        if (!first.TryGetProperty("stream_options", out var so) || !so.GetProperty("include_usage").GetBoolean())
            failures.Add("usage was not requested (stream_options.include_usage)");
        // ⚠️ The live app state must be the LAST message and must not be inside the first: it
        // changes every turn, and anything after it in the prefix is uncached. Measured before
        // this rule: 12,500 tokens re-sent on every turn.
        var firstMsgs = first.GetProperty("messages").EnumerateArray().ToList();
        if (firstMsgs[0].GetProperty("role").GetString() != "system" || firstMsgs[0].GetProperty("content").GetString() != "system")
            failures.Add("the first message is not the stable system prompt on its own");
        if (firstMsgs[^1].GetProperty("role").GetString() != "system" || !firstMsgs[^1].GetProperty("content").GetString()!.Contains("Now: 12:00"))
            failures.Add("the live app state is not the last message — everything after it would be uncached every turn");
        if (!first.GetProperty("messages")[1].GetProperty("content").GetString()!.EndsWith(" EVE] hi"))
            failures.Add("the capsuleer's message was not stamped with its time");
        if (!first.TryGetProperty("tools", out var tools) || tools[0].GetProperty("function").GetProperty("name").GetString() != "show_table")
            failures.Add("tools were not sent in the function-calling shape");

        Console.WriteLine("  wire                  : assistant turn echoed with reassembled call, tool result keyed by id, usage requested, user turn stamped, live state last");
        return failures;
    }
}

/// <summary>
/// Ollama: the same stream, plus the /api/ps route beside it that says what the loaded model's
/// window is. Records how often it was asked.
///
/// <para>⚠️ The window matters because Ollama does not refuse a prompt that outgrows it — it
/// drops the OLDEST messages, and the oldest message this provider sends is the system prompt.
/// The panel summarises before that point only if the provider has reported the window, so a
/// provider that stops asking, or asks before the model is loaded and never again, is a silent
/// loss of the whole instruction set on some later turn.</para>
/// </summary>
sealed class FakeOllama : FakeOpenAi
{
    private int _psRequests;

    protected override Task<HttpResponseMessage> SideRequestAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (req.RequestUri?.AbsolutePath != "/api/ps") return base.SideRequestAsync(req, ct);
        Interlocked.Increment(ref _psRequests);
        // As the server lists it: the tag's case is Ollama's own, not the setting's.
        var body = """{"models":[{"name":"qwen2.5:7B","model":"qwen2.5:7B","size":5500000000,"size_vram":5500000000,"context_length":32768}]}""";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    public List<string> VerifyLocal(IReadOnlyList<UsageReport> usage)
    {
        var failures = new List<string>();
        if (_bodies.Count == 0) { failures.Add("no requests captured"); return failures; }

        using var firstDoc = JsonDocument.Parse(_bodies[0]);
        var first = firstDoc.RootElement;
        if (!first.TryGetProperty("max_tokens", out _) || first.TryGetProperty("max_completion_tokens", out _))
            failures.Add("a local server must be sent max_tokens, not max_completion_tokens");
        if (!_bodies[0].Contains("\"tools\""))
            failures.Add("tools were not sent to the local server");

        // Once per round, and the model matched despite the tag's case.
        if (_psRequests != Requests)
            failures.Add($"/api/ps was asked {_psRequests} time(s) over {Requests} round(s); the window must be asked after every round, since the server can be restarted with another length");
        if (usage.Count == 0 || usage.Any(u => u.ContextLength != 32768))
            failures.Add("the usage reports do not carry the loaded window (expected 32768 on every one)");
        if (usage.Any(u => !u.IsLocal))
            failures.Add("usage from a local server was not marked local");

        Console.WriteLine($"  wire                  : max_tokens sent, tools sent, /api/ps asked after each of {Requests} rounds, window {usage.FirstOrDefault()?.ContextLength} reported on every usage report");
        return failures;
    }
}
