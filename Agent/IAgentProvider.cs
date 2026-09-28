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

    /// <summary>
    /// Whether the model can be reached, at no cost: the service's free model list, asked with
    /// the key; for a server of the capsuleer's own, whether it lists this model at all. What a
    /// role watches while it is on its fallback, before going back to a model that stopped
    /// answering. It cannot promise the next answer will work — that is found out by asking.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

/// <summary>What a failed request says about the model behind it.</summary>
public static class ModelFailure
{
    /// <summary>
    /// Whether an exception means the model is not there to answer — the server is off, the
    /// service is down or overloaded, the key is refused, the model is missing — rather than that
    /// this particular request was wrong. Only the first kind is a reason to fall over to another
    /// model: a request one model rejects as malformed, the next will reject too.
    /// </summary>
    public static bool IsUnavailable(Exception ex, CancellationToken turn)
    {
        switch (ex)
        {
            // The caller's own cancellation is not a failure of anything.
            case OperationCanceledException when turn.IsCancellationRequested:
                return false;

            // HttpClient's own timeout, or a connection dropped partway.
            case OperationCanceledException:
            case TimeoutException:
            case IOException:
                return true;

            case HttpRequestException http:
                return http.StatusCode is null            // refused, reset, no such host
                    or System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.NotFound          // a model the server does not have
                    or System.Net.HttpStatusCode.RequestTimeout
                    or System.Net.HttpStatusCode.TooManyRequests
                    || (int)http.StatusCode >= 500;                // including Anthropic's 529, overloaded

            default:
                return ex.InnerException is { } inner && IsUnavailable(inner, turn);
        }
    }
}
