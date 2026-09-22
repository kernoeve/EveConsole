using System.Diagnostics;
using System.Text.Json;

namespace EveConsole.Agent.Tools;

/// <summary>
/// Wraps a tool to record what it was asked and how it went, without the tool knowing.
///
/// <para>A decorator rather than a change to <see cref="IAgentTool"/>: there are seventeen tools
/// and none of them should have to care about telemetry, and a new one is covered the moment it is
/// added to the list rather than when somebody remembers to instrument it.</para>
///
/// <para>⚠️ Forwards <see cref="ExecuteWithResultAsync"/> to the inner tool's own override, not to
/// its <see cref="ExecuteAsync"/>. That method has a default implementation on the interface, so a
/// decorator that leaves it alone quietly takes the default — which discards the image a tool like
/// CaptureTabTool returns, and turns a screenshot into a bare caption.</para>
///
/// <para>Recording never fails the call. A tool that worked must not be reported as broken because
/// the sink could not write, so the sink's own errors are its problem, not the agent's.</para>
/// </summary>
/// <param name="onStart">
/// Called with the tool's name as it begins, so the UI can say what is happening. ⚠️ Separate from
/// the sink, which is only told once a call has FINISHED — too late to answer "is it still
/// working, or has it stopped?", which is the question a silent panel provokes.
/// </param>
public sealed class TelemetryToolDecorator(
    IAgentTool inner, IAgentToolSink sink, Action<string>? onStart = null) : IAgentTool
{
    public string Name        => inner.Name;
    public string Description => inner.Description;
    public object InputSchema => inner.InputSchema;

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
        => Record(input, async () => (AgentToolResult)await inner.ExecuteAsync(input, ct))
            .ContinueWith(t => t.Result.Text, ct, TaskContinuationOptions.None, TaskScheduler.Default);

    public Task<AgentToolResult> ExecuteWithResultAsync(JsonElement input, CancellationToken ct = default)
        => Record(input, () => inner.ExecuteWithResultAsync(input, ct));

    private async Task<AgentToolResult> Record(JsonElement input, Func<Task<AgentToolResult>> run)
    {
        var sw    = Stopwatch.StartNew();
        var error = "";
        AgentToolResult result = "";

        try { onStart?.Invoke(Name); } catch { /* a status label must never fail a tool call */ }

        try
        {
            result = await run();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            throw;
        }
        finally
        {
            try
            {
                sink.ToolCalled(
                    Name,
                    RawInput(input),
                    (int)sw.ElapsedMilliseconds,
                    result.Text.Length,
                    RowCount(result.Text),
                    error);
            }
            catch { /* telemetry must never break a call that otherwise worked */ }
        }

        return result;
    }

    /// <summary>
    /// The arguments as the model wrote them. For query_database this is the SQL, which is the
    /// single most useful thing in the whole telemetry set — a list of what the agent actually
    /// asked for is what says which part of its context is missing.
    /// </summary>
    private static string RawInput(JsonElement input)
    {
        try
        {
            var text = input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText();
            // ⚠️ Capped. A pathological query should not be able to grow the database, and the
            // first few thousand characters are always enough to see what was intended.
            return text.Length > 4000 ? text[..4000] + "…[truncated]" : text;
        }
        catch { return ""; }
    }

    /// <summary>
    /// Rows returned, for the tools that answer with a JSON array. -1 where the idea does not
    /// apply — an action tool has no row count, and reporting 0 would read as "found nothing".
    /// </summary>
    private static int RowCount(string resultText)
    {
        var t = resultText.AsSpan().TrimStart();
        if (t.Length == 0 || t[0] != '[') return -1;

        try
        {
            using var doc = JsonDocument.Parse(resultText);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : -1;
        }
        catch { return -1; }
    }
}

/// <summary>Where a wrapped tool reports what it did. Implemented by the telemetry service.</summary>
public interface IAgentToolSink
{
    void ToolCalled(string toolName, string inputJson, int durationMs, int resultChars, int rowCount, string error);
}
