namespace EveConsole.Auth;

/// <summary>
/// The EVE sign-in that connects EVE Console to a connector: PKCE, with no scopes, against an EVE
/// application of its own.
///
/// <para>⚠️ Not the online services' application, though it works the same way. A connector may be run by
/// somebody else — a corp hosting one for its members — and a token issued to the online services'
/// application would let that host sign in to eveconsole.com as each member who connected. A token
/// issued to this one is refused there.</para>
///
/// <para>⚠️ Never the ESI application either: its tokens can read wallets and mail. The connector is
/// given this token once, checks who it names, and answers with a key of its own.</para>
/// </summary>
public sealed class ConnectorSignInService(IHttpClientFactory httpFactory)
    : OnlineSignInService(httpFactory, ClientIdValue, Port)
{
    /// <summary>
    /// The connector application's client id, public by design. ⚠️ Empty until the application is
    /// registered with EVE (no scopes, callback http://localhost:5053/callback); until then Connect says
    /// it is not available in this build.
    /// </summary>
    public const string ClientIdValue = "";

    public const int Port = 5053;
}
