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
    /// <summary>
    /// The turn belonging to the current async flow.
    ///
    /// <para>⚠️ AsyncLocal, not a field, because turns overlap. Summarization is fired WITHOUT
    /// await from inside a send, so two turns are open at once — and with a single field they
    /// fought over it: the summariser's Begin landed before the main turn's Complete, which then
    /// wrote the summariser's row using the main turn's numbers, and the summariser's own Complete
    /// found nothing left to write. Observed as an interaction with 0 round trips, 0 duration and
    /// no usage rows at all, with the summarisation's whole token spend unrecorded.</para>
    ///
    /// <para>An AsyncLocal flows into every continuation started after it is set, so the provider's
    /// rounds and the tool calls nested inside them all find the turn they actually belong to,
    /// without anyone having to thread a token through seventeen tools.</para>
    ///
    /// <para>⚠️ The part worth not second-guessing: the summariser calls Begin BEFORE its first
    /// await, so it looks as though it must run in the caller's context and clobber the caller's
    /// turn. It does not — an async method gets its own AsyncLocal scope from the moment it is
    /// invoked, even for a mutation ahead of its first suspension. Verified rather than assumed:
    /// with the caller set to one value and an un-awaited async callee setting another, the caller
    /// still reads its own both immediately after the call and after its next await. No
    /// Task.Yield or other boundary is needed here, and adding one would be cargo cult.</para>
    /// </summary>
    private readonly AsyncLocal<Turn?> _current = new();

    /// <summary>One turn in flight. Accumulated in memory, written once at the end.</summary>
    private sealed class Turn
    {
        private readonly object _gate = new();

        public string ConversationId = "";
        public string Provider       = "";
        public string Model          = "";
        public DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
        public long Started = Environment.TickCount64;
        public int  UserChars;

        private readonly List<AgentToolCall> _calls  = [];
        private readonly List<UsageReport>   _usages = [];

        // Locked per turn rather than globally: two turns running at once must not serialise on
        // each other, and a tool result arriving on a pool thread must not race the round that
        // requested it.
        public void Add(AgentToolCall call)
        {
            lock (_gate) { call.Sequence = _calls.Count + 1; _calls.Add(call); }
        }

        public void Add(UsageReport usage)
        {
            lock (_gate) { _usages.Add(usage); }
        }

        public (List<AgentToolCall> Calls, List<UsageReport> Usages) Snapshot()
        {
            lock (_gate) { return ([.. _calls], [.. _usages]); }
        }
    }

    /// <summary>
    /// Opens a turn on this async flow. Overlapping turns each get their own and do not interfere.
    /// </summary>
    public void Begin(string conversationId, string provider, string model, int userChars)
        => _current.Value = new Turn
        {
            ConversationId = conversationId,
            Provider       = provider,
            Model          = model,
            UserChars      = userChars,
        };

    /// <summary>From <see cref="TelemetryToolDecorator"/>, on whichever thread ran the tool.</summary>
    public void ToolCalled(string toolName, string inputJson, int durationMs, int resultChars, int rowCount, string error)
        => _current.Value?.Add(new AgentToolCall
        {
            OccurredAt  = DateTimeOffset.UtcNow,
            ToolName    = toolName,
            DurationMs  = durationMs,
            InputJson   = inputJson,
            ResultChars = resultChars,
            RowCount    = rowCount,
            Error       = error,
        });

    /// <summary>From the provider, once per round trip.</summary>
    public void Usage(UsageReport report) => _current.Value?.Add(report);

    /// <summary>
    /// One call to a non-LLM billable service — speech out, speech in.
    ///
    /// <para>⚠️ Counted here rather than read back from a provider, which is the opposite of how
    /// the LLM path works and worth knowing when reading the table. Nothing useful comes back from
    /// a TTS or transcription endpoint; the units are known because we know what was SENT —
    /// characters submitted to a voice, seconds of audio submitted to a transcriber. Exact, but
    /// measured at this end.</para>
    ///
    /// <para>⚠️ Written unlinked, with no InteractionId. Speech happens DURING a turn, and that
    /// turn's row does not exist yet — its key is assigned when it is written at the end. Rather
    /// than buffer these into the turn and complicate a path that must never fail, they carry
    /// their own timestamp and are correlated by time. Speech also fires outside any turn, from
    /// an alarm, where there would be nothing to link to.</para>
    /// </summary>
    public void ServiceCall(
        string kind, string provider, string model, bool isLocal,
        string unitKind, long units, int durationMs, string error = "")
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                db.ServiceUsage.Add(new ServiceUsage
                {
                    OccurredAt        = DateTimeOffset.UtcNow,
                    InteractionId     = null,
                    Kind              = kind,
                    Provider          = provider,
                    Model             = model,
                    IsLocal           = isLocal,
                    UnitKind          = unitKind,
                    InputUnits        = units,
                    UnitsAreEstimated = false,   // counted from what was sent, not inferred
                    DurationMs        = durationMs,
                    Error             = error,
                });

                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                errors.Log("AgentTelemetry", $"ServiceCall({kind})", ex);
            }
        });
    }

    /// <summary>
    /// Closes the turn and writes it. Fire-and-forget by design: the capsuleer's answer is already
    /// on screen and must not wait on a database.
    /// </summary>
    public void Complete(int responseChars, string error = "")
    {
        var turn = _current.Value;
        _current.Value = null;
        if (turn is null) return;

        _ = Task.Run(() => WriteAsync(turn, responseChars, error));
    }

    private async Task WriteAsync(Turn turn, int responseChars, string error)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var (calls, usages) = turn.Snapshot();

            var counts = calls
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
                Provider       = usages.Count > 0 ? usages[^1].Provider : turn.Provider,
                Model          = usages.Count > 0 ? usages[^1].Model    : turn.Model,
                RoundTrips     = usages.Count,
                ToolCallCount  = calls.Count,
                // Both tools that run SQL the model wrote. show_query's rows never reach the
                // model, but it is a database call all the same, and the number worth watching.
                QueryCount     = counts.GetValueOrDefault("query_database")
                               + counts.GetValueOrDefault("show_query"),
                ToolsUsed      = counts.Count > 0 ? JsonSerializer.Serialize(counts) : "",
                // The last round is the one that actually ended the turn; the earlier ones all
                // stopped for tool_use and would report that instead.
                StopReason     = usages.Count > 0 ? usages[^1].StopReason : "",
                Error          = error,
                UserChars      = turn.UserChars,
                ResponseChars  = responseChars,
            };

            db.AgentInteractions.Add(interaction);
            await db.SaveChangesAsync();      // assigns the key the children need

            foreach (var call in calls) call.InteractionId = interaction.Id;
            db.AgentToolCalls.AddRange(calls);

            foreach (var u in usages)
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
