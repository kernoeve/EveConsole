using System.Text;
using System.Text.Json;

namespace EveCortex.Auth;

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
