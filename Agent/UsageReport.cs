namespace EveConsole.Agent;

/// <summary>
/// What one provider round trip consumed.
///
/// <para>One of these per HTTP call, not per turn: a turn that uses tools makes several, and the
/// difference between "a question cost 2k tokens" and "a question cost 2k tokens five times" is
/// invisible without counting them separately.</para>
///
/// <para>⚠️ <see cref="IsEstimated"/> is not decoration. Providers differ in what they will tell
/// you — Anthropic reports usage exactly, OpenAI only when asked, and some report nothing at all,
/// leaving a character count to divide. An inferred number is still useful; an inferred number
/// filed as a measured one is how a spend figure becomes confidently wrong.</para>
/// </summary>
public sealed record UsageReport
{
    public string Provider { get; init; } = "";
    public string Model    { get; init; } = "";

    /// <summary>True for a provider running on this machine, whose cost is therefore zero.</summary>
    public bool   IsLocal  { get; init; }

    public long InputTokens      { get; init; }
    public long OutputTokens     { get; init; }

    /// <summary>Prompt-cache reads and writes where the provider distinguishes them.</summary>
    public long CacheReadTokens  { get; init; }
    public long CacheWriteTokens { get; init; }

    /// <summary>True when these counts were inferred rather than reported by the provider.</summary>
    public bool IsEstimated { get; init; }

    public string StopReason { get; init; } = "";
    public int    DurationMs { get; init; }

    /// <summary>
    /// The context window the model was loaded with, in tokens, when the server can say —
    /// Ollama reports it; a cloud service does not need to. Null means unknown, not unlimited.
    ///
    /// <para>Here rather than on the provider because the caller that has to act on it is the
    /// one already receiving these: the panel, which must summarise the history before the prompt
    /// reaches the window. Measured on a 2k window: Ollama does not refuse a prompt that outgrows
    /// it, it drops the OLDEST messages — which for this layout is the whole system prompt, while
    /// the chat stays — and the model went on answering with no instructions at all.</para>
    /// </summary>
    public int? ContextLength { get; init; }
}
