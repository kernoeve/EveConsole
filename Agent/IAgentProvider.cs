using System.Runtime.CompilerServices;
using EveConsole.Agent.Tools;

namespace EveConsole.Agent;

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
