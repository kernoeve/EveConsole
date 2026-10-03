namespace EveConsole.Api;

/// <summary>
/// Sits under the ESI HttpClients: every response, from every call site, is written into
/// <see cref="EsiBudget.Shared"/> — the error window, the route's bucket, the failures — and every
/// background request waits its turn with the governor first. A request the user is waiting on
/// is never held (<see cref="EsiClient.Background"/> decides which is which).
/// </summary>
/// <param name="governed">False for the status check, which must run whatever the budget says:
/// it is how downtime is noticed.</param>
public sealed class EsiBudgetHandler(bool governed = true) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.IsAbsoluteUri == true
            ? request.RequestUri.PathAndQuery
            : request.RequestUri?.OriginalString ?? "";

        // Rate-limit buckets are per character for a call with a login — see EsiGroupState.
        var owner = EsiBudget.BucketOwner(request.Headers.Authorization);

        var turn = governed && EsiClient.IsBackgroundLane ? await EsiBudget.Shared.WaitTurnAsync(path, ct, owner) : null;
        try
        {
            var response = await base.SendAsync(request, ct);
            EsiBudget.Shared.Record(path, (int)response.StatusCode, response.Headers, owner);
            return response;
        }
        finally { turn?.Dispose(); }
    }
}
