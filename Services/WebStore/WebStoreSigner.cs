using System.Security.Cryptography;
using System.Text;

namespace EveConsole.Services.WebStore;

/// <summary>
/// How the app proves a sync call is its own.
///
/// <para>A per-store secret shared with the site, used as an HMAC key and never sent. Each call
/// carries a timestamp and the HMAC-SHA256 of <c>timestamp + "\n" + body</c>; the site verifies
/// with the same secret, refuses anything older than a few minutes, and compares in constant
/// time. A bearer token would put the secret in every request; the signature costs one line on
/// each side and gives tamper evidence and replay resistance for free.</para>
///
/// <para>The verifying half lives here too, so a harness can stand in for the site and the two
/// halves are provably each other's inverse.</para>
/// </summary>
public static class WebStoreSigner
{
    /// <summary>How far a request's timestamp may sit from the receiver's clock.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>A fresh secret: 32 random bytes, base64url, safe to paste anywhere.</summary>
    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The signature header value for a body at a moment.</summary>
    public static string Sign(string secret, long unixSeconds, ReadOnlySpan<byte> body)
    {
        var key    = Encoding.UTF8.GetBytes(secret);
        var prefix = Encoding.UTF8.GetBytes(unixSeconds.ToString() + "\n");
        var data   = new byte[prefix.Length + body.Length];
        prefix.CopyTo(data, 0);
        body.CopyTo(data.AsSpan(prefix.Length));
        return "v1=" + Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant();
    }

    /// <summary>
    /// Whether a signature is the one this secret would have produced, and the timestamp is
    /// within the window of <paramref name="now"/>.
    /// </summary>
    public static bool Verify(string secret, string timestampHeader, string signatureHeader,
                              ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (!long.TryParse(timestampHeader, out var ts)) return false;
        var skew = Math.Abs(now.ToUnixTimeSeconds() - ts);
        if (skew > Window.TotalSeconds) return false;

        var expected = Encoding.UTF8.GetBytes(Sign(secret, ts, body));
        var given    = Encoding.UTF8.GetBytes(signatureHeader ?? "");
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}
