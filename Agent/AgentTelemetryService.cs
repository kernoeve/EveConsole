using System.Text.Json;
using EveConsole.Agent.Tools;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Agent;

/// <summary>
/// Records what each exchange with the agent cost and what it did.
///
/// <para>Built for two questions that cannot be answered by guesswork. What is this costing —
/// which needs the input side of the bill, invisible without reading it off the provider, and
/// multiplied by round trips a tool-using turn makes without telling anyone. And why did it answer
/// badly — which needs the SQL it actually wrote, because a list of the queries it got wrong is
/// what says which part of its context is missing.</para>
///
/// <para>⚠️ Nothing here may break a turn. Every write is wrapped, and a failure is reported to the
/// error log rather than thrown — an agent that stopped answering because its diary was full would
/// be a worse bug than the one this exists to find. Equally it is not silent: a swallowed
/// exception with no trace is how a feature quietly stops working for months.</para>
/// </summary>
public sealed class AgentTelemetryService(IServiceScopeFactory scopes, AppErrorLogger errors) : IAgentToolSink
{
    private readonly object _gate = new();
    private Turn? _turn;

    /// <summary>One turn in flight. Accumulated in memory, written once at the end.</summary>
    private sealed class Turn
    {
        public string ConversationId = "";
        public string Provider       = "";
        public string Model          = "";
        public DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
        public long Started = Environment.TickCount64;
        public int  UserChars;

        public readonly List<AgentToolCall> Calls  = [];
        public readonly List<UsageReport>   Usages = [];
    }

    /// <summary>
    /// Opens a turn. Any turn still open is abandoned rather than merged — the panel cancels the
    /// previous stream before starting a new one, so a leftover belongs to work the capsuleer
    /// has already walked away from.
    /// </summary>
    public void Begin(string conversationId, string provider, string model, int userChars)
    {
        lock (_gate)
        {
            _turn = new Turn
            {
                ConversationId = conversationId,
                Provider       = provider,
                Model          = model,
                UserChars      = userChars,
            };
        }
    }

    /// <summary>From <see cref="TelemetryToolDecorator"/>, on whichever thread ran the tool.</summary>
    public void ToolCalled(string toolName, string inputJson, int durationMs, int resultChars, int rowCount, string error)
    {
        lock (_gate)
        {
            if (_turn is null) return;   // a tool outside a turn is not something to invent a row for
            _turn.Calls.Add(new AgentToolCall
            {
                Sequence    = _turn.Calls.Count + 1,
                OccurredAt  = DateTimeOffset.UtcNow,
                ToolName    = toolName,
                DurationMs  = durationMs,
                InputJson   = inputJson,
                ResultChars = resultChars,
                RowCount    = rowCount,
                Error       = error,
            });
        }
    }

    /// <summary>From the provider, once per round trip.</summary>
    public void Usage(UsageReport report)
    {
        lock (_gate)
        {
            if (_turn is null) return;
            _turn.Usages.Add(report);
        }
    }

    /// <summary>
    /// Closes the turn and writes it. Fire-and-forget by design: the capsuleer's answer is already
    /// on screen and must not wait on a database.
    /// </summary>
    public void Complete(int responseChars, string error = "")
    {
        Turn? turn;
        lock (_gate) { turn = _turn; _turn = null; }
        if (turn is null) return;

        _ = Task.Run(() => WriteAsync(turn, responseChars, error));
    }

    private async Task WriteAsync(Turn turn, int responseChars, string error)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var counts = turn.Calls
                .GroupBy(c => c.ToolName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var interaction = new AgentInteraction
            {
                ConversationId = turn.ConversationId,
                StartedAt      = turn.StartedAt,
                DurationMs     = (int)(Environment.TickCount64 - turn.Started),
                // ⚠️ Taken from the provider's own report where there is one. The caller can only
                // say which provider it MEANT to use; the report says what actually answered,
                // which is the thing a spend figure has to be attributed to.
                Provider       = turn.Usages.Count > 0 ? turn.Usages[^1].Provider : turn.Provider,
                Model          = turn.Usages.Count > 0 ? turn.Usages[^1].Model    : turn.Model,
                RoundTrips     = turn.Usages.Count,
                ToolCallCount  = turn.Calls.Count,
                QueryCount     = counts.GetValueOrDefault("query_database"),
                ToolsUsed      = counts.Count > 0 ? JsonSerializer.Serialize(counts) : "",
                // The last round is the one that actually ended the turn; the earlier ones all
                // stopped for tool_use and would report that instead.
                StopReason     = turn.Usages.Count > 0 ? turn.Usages[^1].StopReason : "",
                Error          = error,
                UserChars      = turn.UserChars,
                ResponseChars  = responseChars,
            };

            db.AgentInteractions.Add(interaction);
            await db.SaveChangesAsync();      // assigns the key the children need

            foreach (var call in turn.Calls) call.InteractionId = interaction.Id;
            db.AgentToolCalls.AddRange(turn.Calls);

            foreach (var u in turn.Usages)
            {
                db.ServiceUsage.Add(new ServiceUsage
                {
                    OccurredAt        = DateTimeOffset.UtcNow,
                    InteractionId     = interaction.Id,
                    Kind              = "llm",
                    Provider          = u.Provider,
                    Model             = u.Model,
                    IsLocal           = u.IsLocal,
                    UnitKind          = "tokens",
                    InputUnits        = u.InputTokens,
                    OutputUnits       = u.OutputTokens,
                    CacheReadUnits    = u.CacheReadTokens,
                    CacheWriteUnits   = u.CacheWriteTokens,
                    UnitsAreEstimated = u.IsEstimated,
                    DurationMs        = u.DurationMs,
                });
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // ⚠️ Reported, not swallowed. Telemetry that stops working without saying so leaves
            // a spend figure that looks complete and is not — worse than having none.
            errors.Log("AgentTelemetry", "Write", ex);
        }
    }
}
