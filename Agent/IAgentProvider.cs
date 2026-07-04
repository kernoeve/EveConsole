using System.Runtime.CompilerServices;
using EveCortex.Agent.Tools;

namespace EveCortex.Agent;

public interface IAgentProvider
{
    string ProviderName { get; }
    bool   IsConfigured { get; }

    IAsyncEnumerable<string> StreamAsync(
        string                      systemPrompt,
        IReadOnlyList<AgentMessage> history,
        IReadOnlyList<IAgentTool>?  tools = null,
        CancellationToken           ct    = default);
}
