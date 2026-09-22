namespace EveConsole.Auth;

/// <summary>
/// The SSO refused a refresh token outright — <c>invalid_grant</c> — which it does for a token
/// that has expired, been revoked on the account's third-party page, or belonged to a character
/// since re-authorised elsewhere. Unlike a 5xx or a dropped connection this is final: the same
/// token will be refused every time, so the caller stands the owner down and tells the user,
/// instead of asking again on every poll and filling the log with the same 400.
/// </summary>
public sealed class EsiTokenRevokedException(string error, string description)
    : Exception($"{error}: {description}")
{
    /// <summary>The SSO's error code, <c>invalid_grant</c>.</summary>
    public string Error { get; } = error;
    /// <summary>The SSO's own words, e.g. "Invalid refresh token. Token missing/expired."</summary>
    public string Description { get; } = description;
}
