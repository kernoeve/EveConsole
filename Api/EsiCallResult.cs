namespace EveConsole.Api;

public class EsiCallResult<T>
{
    public T?     Data                { get; init; }
    public int    StatusCode          { get; init; }
    public int    TotalPages          { get; init; } = 1;
    public string? RateLimitGroup     { get; init; }
    public int?   RateLimitRemaining  { get; init; }
    public int?   RateLimitLimit      { get; init; }
    public int?   ErrorLimitRemain    { get; init; }
    public int?   ErrorLimitReset     { get; init; }
    public int?   RetryAfterSeconds   { get; init; }

    /// <summary>When the server's copy goes stale, from the Expires header. Null when it sent
    /// none, in which case the caller falls back to its own interval.</summary>
    public DateTimeOffset? Expires  { get; init; }
    public string? Error              { get; init; }
    // For paged fetches: false when one or more pages after the first failed, so the returned
    // Data is incomplete. Callers that reconcile "rows no longer returned" must not act on a
    // partial set. Always true for single-page / non-paged calls.
    public bool   Complete            { get; init; } = true;

    public bool IsSuccess     => StatusCode is >= 200 and < 300;
    public bool IsRateLimited => StatusCode == 429;
    public bool IsNotModified => StatusCode == 304;
}
