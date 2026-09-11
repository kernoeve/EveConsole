using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EveConsole.Agent;
using EveConsole.Agent.Providers;
using EveConsole.Agent.Tools;

// ═══════════════════════════════════════════════════════════════════════════════════════════
// Does a streamed agent turn stay off the UI thread?
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
// So this runs the REAL provider — ClaudeProvider, not a stand-in — against a fake Anthropic
// server that streams a text round, a slow tool-use round and a closing round, starting the turn
// from a thread that has a SynchronizationContext installed, exactly as the click that starts a
// real turn does. The context counts every Post() it receives. The correct number is zero.
// ═══════════════════════════════════════════════════════════════════════════════════════════

var ui = new PumpContext();
SynchronizationContext.SetSynchronizationContext(ui);
var uiThread = Environment.CurrentManagedThreadId;

var handler = new FakeAnthropic();
var field   = typeof(ClaudeProvider).GetField("_http", BindingFlags.NonPublic | BindingFlags.Static)
              ?? throw new InvalidOperationException("ClaudeProvider._http not found — the seam this check depends on has moved.");
field.SetValue(null, new HttpClient(handler));

var provider = new ClaudeProvider("not-a-real-key");
var tool     = new ThreadRecordingTool();
var chunkThreads = new ConcurrentBag<int>();
var text = new StringBuilder();

// Consumed the way AgentPanelViewModel.SendAsync consumes it: started on the UI thread, with
// ConfigureAwait(false) on the enumeration. The pump then runs the "UI thread" until the turn
// completes, executing anything posted to it — and counting it.
var turn = Consume();
ui.RunUntil(turn);

async Task Consume()
{
    await foreach (var chunk in provider.StreamAsync("system", [new AgentMessage(MessageRole.User, "hi")], [tool])
                                        .ConfigureAwait(false))
    {
        text.Append(chunk);
        chunkThreads.Add(Environment.CurrentManagedThreadId);
    }
}

var chunksOnUi = chunkThreads.Count(t => t == uiThread);
var toolOnUi   = tool.ExecutedOn == uiThread;

Console.WriteLine($"rounds served         : {handler.Requests}");
Console.WriteLine($"text streamed         : \"{text.ToString().Trim()}\"");
Console.WriteLine($"tool ran on UI thread : {toolOnUi}");
Console.WriteLine($"chunks on UI thread   : {chunksOnUi} of {chunkThreads.Count}");
Console.WriteLine($"posts to UI context   : {ui.Posts}");
Console.WriteLine();

// ── Prompt-cache markers ──────────────────────────────────────────────────────────────────
//
// ⚠️ The API allows at most four cache_control markers per request and rejects the whole
// request over a fifth. The third marker moves forward with the newest tool result each round,
// which means every EARLIER round's marker has to be gone — a mistake there is invisible on a
// one-round answer and breaks every multi-round one. So: never more than four, and the request
// that carries tool results has its marker on one of them.
// Expected: the first request carries the two fixed markers (system, last history message);
// every request after it carries exactly three — the newest tool result's, and NOT its
// predecessors'.
var markersOk = handler.Caching.Count == FakeAnthropic.ToolRounds + 1
             && handler.Caching[0].Markers == 2
             && handler.Caching.Skip(1).All(c => c.Markers == 3 && c.OnToolResult)
             && handler.Caching.All(c => c.TtlOrderOk);
Console.WriteLine($"cache markers         : {string.Join(", ", handler.Caching.Select(c => $"{c.Markers}{(c.OnToolResult ? " (one on a tool_result)" : "")}{(c.TtlOrderOk ? "" : " TTL ORDER WRONG")}"))}");

var ok = handler.Requests == FakeAnthropic.ToolRounds + 1 && text.ToString().Contains("in the tab")
      && ui.Posts == 0 && !toolOnUi && chunksOnUi == 0 && markersOk;

if (ok)
{
    Console.WriteLine("Agent stream check: the whole turn ran off the UI thread and never posted back to it.");
    return 0;
}

Console.WriteLine("Agent stream check FAILED.");
if (handler.Requests != FakeAnthropic.ToolRounds + 1)
                             Console.WriteLine("  the fake server did not see every round — the turn did not complete.");
if (ui.Posts > 0)            Console.WriteLine($"  {ui.Posts} continuation(s) were posted to the UI context: an await in the streaming path has lost its ConfigureAwait(false).");
if (toolOnUi)                Console.WriteLine("  the tool executed on the UI thread.");
if (chunksOnUi > 0)          Console.WriteLine("  chunks were delivered on the UI thread.");
if (!markersOk)              Console.WriteLine("  cache markers are wrong: expected 2 on the first request and exactly 3 on each later one, the third on the newest tool result; the stable markers must carry ttl 1h and the tool-result marker must not.");
return 1;

// ─────────────────────────────────────────────────────────────────────────────────────────

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

    public string Name        => "show_table";
    public string Description => "test";
    public object InputSchema => new { type = "object", properties = new { title = new { type = "string" } } };

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        ExecutedOn = Environment.CurrentManagedThreadId;
        return Task.FromResult("Opened a tab.");
    }
}

/// <summary>
/// Streams two rounds the way the real API does: a short text block, then a tool_use block whose
/// input arrives slowly in many small deltas — the shape that froze the window — then, on the
/// request that carries the tool result, a closing text round.
/// </summary>
sealed class FakeAnthropic : HttpMessageHandler
{
    /// <summary>
    /// Tool-use rounds before the closing text round. Three, not one: the cache-marker rule
    /// below is about a marker being REMOVED from an earlier round, which two requests cannot
    /// show.
    /// </summary>
    public const int ToolRounds = 3;

    private int _requests;
    public int Requests => _requests;

    /// <summary>
    /// Per request: cache_control markers in the body, whether a tool_result carried one, and
    /// whether every marker BEFORE the tool result is the long-lived kind while the tool result's
    /// own is not.
    /// </summary>
    public readonly List<(int Markers, bool OnToolResult, bool TtlOrderOk)> Caching = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        // ⚠️ Genuinely asynchronous, like a network call. A handler that returns a completed task
        // lets the provider's first await complete synchronously, so its loop runs inline on the
        // CALLING thread until the pipe is empty — and whether the first chunks land before that
        // is a race. That is a property of the fake, not of the code under test, and it made this
        // check flicker. A real response never arrives before the request has left.
        await Task.Delay(20, ct).ConfigureAwait(false);

        var round = Interlocked.Increment(ref _requests);
        var body  = await req.Content!.ReadAsStringAsync(ct);
        // ⚠️ The API requires longer-lived cache markers to precede shorter-lived ones. The two
        // stable entries (system, history) are one-hour; the moving tool-result marker is the
        // five-minute default. A change that put a 1h marker after a 5m one, or dropped the 1h
        // from a stable entry, would be a rejected request or a silently cold cache.
        var markers = System.Text.RegularExpressions.Regex.Matches(body, @"""cache_control"":\{[^}]*\}")
                          .Select(m => m.Value).ToList();
        var toolResultMarker = System.Text.RegularExpressions.Regex.Match(body, @"""type"":""tool_result""[^}]*(""cache_control"":\{[^}]*\})");
        var stable = toolResultMarker.Success ? markers.Where(m => m != toolResultMarker.Groups[1].Value).ToList() : markers;
        var ttlOk  = stable.All(m => m.Contains("\"ttl\":\"1h\""))
                  && (!toolResultMarker.Success || !toolResultMarker.Groups[1].Value.Contains("\"ttl\""));
        lock (Caching)
            Caching.Add((
                Markers:      markers.Count,
                OnToolResult: toolResultMarker.Success,
                TtlOrderOk:   ttlOk));
        var pipe  = new Pipe();

        _ = Task.Run(async () =>
        {
            async Task Ev(string type, string json)
            {
                await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes($"event: {type}\ndata: {json}\n\n"), ct);
                await pipe.Writer.FlushAsync(ct);
            }

            await Ev("message_start",       """{"type":"message_start","message":{"usage":{"input_tokens":10}}}""");
            await Ev("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text"}}""");

            var sentence = round <= ToolRounds ? "Let me assemble the table." : "Done, it is in the tab.";
            foreach (var word in sentence.Split(' '))
            {
                await Ev("content_block_delta",
                    "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(word + " ") + "}}");
                await Task.Delay(10, ct);
            }
            await Ev("content_block_stop", """{"type":"content_block_stop","index":0}""");

            if (round <= ToolRounds)
            {
                await Ev("content_block_start",
                    """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"t1","name":"show_table"}}""");

                // Sixty small deltas with a pause between them: a tool input the model takes a
                // while to write, during which no text is yielded to the consumer at all.
                var input = "{\"title\":\"" + new string('x', 300) + "\"}";
                foreach (var piece in input.Chunk(6))
                {
                    await Ev("content_block_delta",
                        "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(new string(piece)) + "}}");
                    await Task.Delay(8, ct);
                }
                await Ev("content_block_stop", """{"type":"content_block_stop","index":1}""");
                await Ev("message_delta",      """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":50}}""");
            }
            else
            {
                await Ev("message_delta",      """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}""");
            }

            await Ev("message_stop", """{"type":"message_stop"}""");
            await pipe.Writer.CompleteAsync();
        }, ct);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(pipe.Reader.AsStream()),
        };
        response.Content.Headers.ContentType = new("text/event-stream");
        return response;
    }
}
