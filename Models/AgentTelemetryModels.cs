namespace EveConsole.Models;

/// <summary>
/// One exchange with the agent: the capsuleer's turn, and everything the agent did to answer it.
///
/// <para>A single turn can make several provider round-trips — the model answers, calls a tool,
/// reads the result, and answers again — so <see cref="RoundTrips"/> is not always 1 and is what
/// makes token spend per turn hard to guess without measuring.</para>
///
/// <para>⚠️ Diagnostic exhaust, not the capsuleer's data. The message text is deliberately NOT
/// stored — only its length. What is worth keeping is what the agent DID, which lives in
/// <see cref="AgentToolCall"/>.</para>
/// </summary>
public class AgentInteraction
{
    public long Id { get; set; }

    /// <summary>Groups the turns of one conversation, so a thread can be read back in order.</summary>
    public string ConversationId { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Wall-clock for the whole turn, tool calls included.</summary>
    public int DurationMs { get; set; }

    public string Provider { get; set; } = "";
    public string Model    { get; set; } = "";

    /// <summary>Provider calls this turn. Tool use multiplies it; a plain answer is 1.</summary>
    public int RoundTrips    { get; set; }

    public int ToolCallCount { get; set; }

    /// <summary>How many of those were query_database, the number worth watching.</summary>
    public int QueryCount    { get; set; }

    /// <summary>JSON object of tool name to call count, e.g. {"query_database":3,"open_window":1}.</summary>
    public string ToolsUsed  { get; set; } = "";

    public string StopReason { get; set; } = "";

    /// <summary>Empty when the turn completed. Set from the exception when it did not.</summary>
    public string Error      { get; set; } = "";

    public int UserChars     { get; set; }
    public int ResponseChars { get; set; }
}

/// <summary>
/// One tool invocation inside a turn.
///
/// <para>⚠️ <see cref="InputJson"/> is the point of this table. For query_database it holds the SQL
/// the agent actually wrote, which turns "the agent is bad at data questions" into a list of the
/// exact queries it got wrong — the only evidence that says which part of its context is missing.
/// Truncated, because a pathological query should not be able to bloat the database.</para>
/// </summary>
public class AgentToolCall
{
    public long Id { get; set; }

    public long InteractionId { get; set; }

    /// <summary>Order within the turn, from 1.</summary>
    public int  Sequence      { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string ToolName    { get; set; } = "";
    public int    DurationMs  { get; set; }

    /// <summary>The arguments the model supplied. For query_database, the SQL.</summary>
    public string InputJson   { get; set; } = "";

    public int    ResultChars { get; set; }

    /// <summary>Rows returned, for the query tools. -1 where the notion does not apply.</summary>
    public int    RowCount    { get; set; } = -1;

    public string Error       { get; set; } = "";
}

/// <summary>
/// The billing ledger: one row per call to any paid-or-local service, so "what did today cost"
/// is a single query rather than three.
///
/// <para>⚠️ Units are the truth; cost is derived. Rates change, and there are six billable
/// services across three vendors — a price baked into a release six months ago is a number that
/// looks authoritative and is wrong. Cost is computed at read time from rates the capsuleer can
/// see and correct.</para>
///
/// <para>⚠️ <see cref="UnitsAreEstimated"/> exists because a value whose provenance is ambiguous
/// causes worse decisions than a missing one. Some providers report usage exactly; some report
/// nothing and leave only a character count to divide. Both are useful; conflating them is not —
/// this codebase has already paid for that lesson once, when FromMarketData=1 did not mean the
/// price was market-backed.</para>
///
/// <para>Local providers are logged too, with <see cref="IsLocal"/> set. They cost nothing, but
/// the consumption and latency still answer "how would this look on a paid provider".</para>
/// </summary>
public class ServiceUsage
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The turn this served, when it was part of one. Null for anything outside a turn.</summary>
    public long? InteractionId { get; set; }

    /// <summary>"llm", "tts" or "stt".</summary>
    public string Kind     { get; set; } = "";

    public string Provider { get; set; } = "";
    public string Model    { get; set; } = "";

    /// <summary>True for a provider that runs on this machine, whose cost is therefore zero.</summary>
    public bool   IsLocal  { get; set; }

    /// <summary>
    /// What the units below are counted in — and why the three kinds cannot share a schema:
    /// an LLM bills tokens, a TTS voice bills characters, and transcription bills audio seconds.
    /// </summary>
    public string UnitKind { get; set; } = "";

    public long InputUnits      { get; set; }
    public long OutputUnits     { get; set; }

    /// <summary>Prompt-cache reads and writes, where the provider distinguishes them. Zero elsewhere.</summary>
    public long CacheReadUnits  { get; set; }
    public long CacheWriteUnits { get; set; }

    /// <summary>True when the counts were inferred rather than reported by the provider.</summary>
    public bool UnitsAreEstimated { get; set; }

    public int    DurationMs { get; set; }
    public string Error      { get; set; } = "";
}

/// <summary>
/// What a service charges, so cost can be derived rather than stored.
///
/// <para>⚠️ Data, not code. There are six billable services across three vendors and their prices
/// change; a rate compiled into a release is a number that looks authoritative and quietly goes
/// wrong. Rows are seeded with sensible defaults and the capsuleer can correct them — a figure
/// they can see and edit is honest in a way a hardcoded one is not.</para>
///
/// <para>⚠️ USD per single unit, not per million, and <c>decimal</c> rather than a float. The unit
/// differs by service — a token, a character, a second — so one scale keeps the arithmetic
/// uniform, and the display multiplies for readability. REAL is float4 on PostgreSQL and has
/// silently truncated money in this codebase before, which is why this is decimal.</para>
/// </summary>
public class ServiceRate
{
    public long Id { get; set; }

    /// <summary>"llm", "tts" or "stt".</summary>
    public string Kind     { get; set; } = "";

    public string Provider { get; set; } = "";

    /// <summary>The specific model or voice. Empty matches any model from that provider.</summary>
    public string Model    { get; set; } = "";

    public decimal InputPerUnit      { get; set; }
    public decimal OutputPerUnit     { get; set; }

    /// <summary>Prompt-cache reads, typically a tenth of the input rate. Zero where not offered.</summary>
    public decimal CacheReadPerUnit  { get; set; }

    /// <summary>Prompt-cache writes, typically a quarter above the input rate.</summary>
    public decimal CacheWritePerUnit { get; set; }

    /// <summary>Where the figure came from, so a stale one can be judged.</summary>
    public string  Notes     { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}
