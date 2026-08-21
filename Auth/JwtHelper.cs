using System.Text;
using System.Text.Json;

namespace EveConsole.Auth;

/// <summary>
/// Minimal JWT decoder — only used to extract the character ID from the ESI access token.
/// Does NOT validate the signature; ESI's own endpoints will reject invalid tokens.
/// </summary>
public static class JwtHelper
{
    /// <summary>
    /// Returns the Eve character ID from an ESI JWT access token.
    /// ESI encodes it in the "sub" claim as "CHARACTER:EVE:{id}".
    /// </summary>
    public static long GetCharacterId(string accessToken)
    {
        var payload = DecodePayload(accessToken);

        if (!payload.TryGetProperty("sub", out var sub))
            throw new InvalidOperationException("JWT has no 'sub' claim.");

        var subValue = sub.GetString() ?? "";

        // Format: "CHARACTER:EVE:12345678"
        var parts = subValue.Split(':');
        if (parts.Length != 3 || !long.TryParse(parts[2], out var id))
            throw new InvalidOperationException($"Unexpected 'sub' format: {subValue}");

        return id;
    }

    /// <summary>
    /// The scopes the token actually carries, from the "scp" claim.
    ///
    /// <para>This is the only honest answer to "what may this token do". What the application
    /// asked for at login is a different question, and the two can differ — a login that reuses an
    /// existing SSO authorisation can return a token scoped to the earlier grant. Storing the
    /// request in place of the grant is what left a character showing forty-eight scopes in the UI
    /// while ESI answered 401 for a third of them.</para>
    ///
    /// <para>Returns an empty array rather than throwing when the claim is absent or the token is
    /// unreadable: an unknown scope list must never be mistaken for an empty one, and callers are
    /// expected to leave what they already hold alone in that case.</para>
    /// </summary>
    public static string[] GetScopes(string accessToken)
    {
        try
        {
            var payload = DecodePayload(accessToken);
            if (!payload.TryGetProperty("scp", out var scp)) return [];

            // EVE sends a JSON array for several scopes and a bare string for exactly one.
            return scp.ValueKind switch
            {
                JsonValueKind.Array  => scp.EnumerateArray()
                                           .Select(e => e.GetString() ?? "")
                                           .Where(s => s.Length > 0)
                                           .ToArray(),
                JsonValueKind.String => (scp.GetString() ?? "")
                                           .Split(' ', StringSplitOptions.RemoveEmptyEntries),
                _                    => [],
            };
        }
        catch
        {
            return [];
        }
    }

    private static JsonElement DecodePayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            throw new ArgumentException("Not a valid JWT.");

        var payload = parts[1];

        // Base64URL → Base64 padding
        payload = payload.Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "=";  break;
        }

        var bytes = Convert.FromBase64String(payload);
        var json  = Encoding.UTF8.GetString(bytes);
        return JsonDocument.Parse(json).RootElement;
    }
}
