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
    IAsyncEnumerable<string> StreamAsync(
        string                      systemPrompt,
        IReadOnlyList<AgentMessage> history,
        IReadOnlyList<IAgentTool>?  tools   = null,
        Action<UsageReport>?        onUsage = null,
        CancellationToken           ct      = default);
}
