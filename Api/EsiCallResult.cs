namespace EveCortex.Api;

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
    public string? Error              { get; init; }

    public bool IsSuccess     => StatusCode is >= 200 and < 300;
    public bool IsRateLimited => StatusCode == 429;
    public bool IsNotModified => StatusCode == 304;
}
