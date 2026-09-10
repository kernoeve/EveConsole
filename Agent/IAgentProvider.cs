using System.Runtime.CompilerServices;
using EveConsole.Agent.Tools;

namespace EveConsole.Agent;

public interface IAgentProvider
{
    string ProviderName { get; }
    bool   IsConfigured { get; }

    /// <param name="onUsage">
    /// Called once per provider round trip with what it consumed, so a turn that used tools can be
    /// costed properly rather than counted as one call.
    ///
    /// <para>Optional because not every provider can fill it in — a local model reports token
    /// counts, OpenAI reports them only when the request asks, and a future one may report
    /// nothing. A provider that cannot measure should either report an estimate with
    /// <see cref="UsageReport.IsEstimated"/> set or not call this at all; what it must not do is
    /// pass a guess off as a measurement.</para>
    /// </param>
    /// <param name="systemPrompt">
    /// The STABLE half of the system prompt — the same text on every call. Anything that varies
    /// belongs in <paramref name="volatileContext"/>.
    /// </param>
    /// <param name="volatileContext">
    /// Prompt text that changes between turns, such as what the capsuleer is currently looking at.
    ///
    /// <para>⚠️ Separate from <paramref name="systemPrompt"/> so prompt caching can work. A cache
    /// hit needs a byte-identical prefix, so appending live state to the stable prompt invalidates
    /// the whole thing on every turn — the cache would appear to be enabled and never once be
    /// read. Kept apart, the stable prefix is cached and the changing tail is simply sent.</para>
    /// </param>
    IAsyncEnumerable<string> StreamAsync(
        string                      systemPrompt,
        IReadOnlyList<AgentMessage> history,
        IReadOnlyList<IAgentTool>?  tools           = null,
        Action<UsageReport>?        onUsage         = null,
        string?                     volatileContext = null,
        CancellationToken           ct              = default);
}
