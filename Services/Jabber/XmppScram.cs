using System.Security.Cryptography;
using System.Text;

namespace EveConsole.Services.Jabber;

/// <summary>
/// The SCRAM login (RFC 5802): both sides prove they know the password without sending it.
///
/// <para>⚠️ The server's final signature is checked, not skipped. It is the half that proves the server
/// also knows the password, so ignoring it would let anyone holding a passable certificate pose as the
/// server and collect what the user sends next.</para>
/// </summary>
public sealed class XmppScram
{
    /// <summary>A server that asks for more than this is treated as hostile: each round is CPU here.</summary>
    private const int MaxIterations = 2_000_000;

    private readonly HashAlgorithmName _hash;
    private readonly string _password;
    private readonly string _nonce;
    private readonly string _clientFirstBare;
    private byte[] _expectedServerSignature = [];

    public XmppScram(string mechanism, string user, string password, string? nonce = null)
    {
        _hash = mechanism switch
        {
            "SCRAM-SHA-512" => HashAlgorithmName.SHA512,
            "SCRAM-SHA-256" => HashAlgorithmName.SHA256,
            "SCRAM-SHA-1"   => HashAlgorithmName.SHA1,
            _ => throw new ArgumentException($"Not a SCRAM mechanism: {mechanism}", nameof(mechanism)),
        };

        _password = password.Normalize(NormalizationForm.FormKC);
        _nonce    = nonce ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

        var name = user.Normalize(NormalizationForm.FormKC).Replace("=", "=3D").Replace(",", "=2C");
        _clientFirstBare = $"n={name},r={_nonce}";
    }

    /// <summary>What goes in the auth element. "n,," says this client does not use channel binding.</summary>
    public string ClientFirst() => "n,," + _clientFirstBare;

    /// <summary>Answers the server's challenge.</summary>
    /// <exception cref="XmppException">The challenge is malformed or does not extend our nonce.</exception>
    public string ClientFinal(string serverFirst)
    {
        var parts = ParseFields(serverFirst);

        if (!parts.TryGetValue('r', out var combined) || !combined.StartsWith(_nonce, StringComparison.Ordinal))
            throw new XmppException(XmppFailure.Login, "The server's login challenge does not match.");
        if (!parts.TryGetValue('s', out var saltText) || !parts.TryGetValue('i', out var iterationText)
            || !int.TryParse(iterationText, out var iterations) || iterations < 1 || iterations > MaxIterations)
            throw new XmppException(XmppFailure.Login, "The server's login challenge is malformed.");

        byte[] salt;
        try { salt = Convert.FromBase64String(saltText); }
        catch (FormatException) { throw new XmppException(XmppFailure.Login, "The server's login challenge is malformed."); }

        var length = _hash == HashAlgorithmName.SHA512 ? 64 : _hash == HashAlgorithmName.SHA256 ? 32 : 20;
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(_password), salt, iterations, _hash, length);

        var clientKey = Hmac(salted, "Client Key");
        var storedKey = Hash(clientKey);

        var withoutProof = $"c=biws,r={combined}";
        var authMessage  = $"{_clientFirstBare},{serverFirst},{withoutProof}";

        var signature = Hmac(storedKey, authMessage);
        var proof     = new byte[clientKey.Length];
        for (var i = 0; i < proof.Length; i++) proof[i] = (byte)(clientKey[i] ^ signature[i]);

        _expectedServerSignature = Hmac(Hmac(salted, "Server Key"), authMessage);

        return $"{withoutProof},p={Convert.ToBase64String(proof)}";
    }

    /// <summary>Whether the server's last message proves it knows the password too.</summary>
    public bool ServerIsGenuine(string serverFinal)
    {
        if (_expectedServerSignature.Length == 0) return false;
        if (!ParseFields(serverFinal).TryGetValue('v', out var text)) return false;

        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(text), _expectedServerSignature);
        }
        catch (FormatException) { return false; }
    }

    private static Dictionary<char, string> ParseFields(string message)
    {
        var fields = new Dictionary<char, string>();
        foreach (var part in message.Split(','))
            if (part.Length >= 2 && part[1] == '=') fields[part[0]] = part[2..];
        return fields;
    }

    private byte[] Hmac(byte[] key, string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        return _hash == HashAlgorithmName.SHA512 ? HMACSHA512.HashData(key, data)
             : _hash == HashAlgorithmName.SHA256 ? HMACSHA256.HashData(key, data)
             :                                     HMACSHA1.HashData(key, data);
    }

    private byte[] Hash(byte[] data) =>
        _hash == HashAlgorithmName.SHA512 ? SHA512.HashData(data)
      : _hash == HashAlgorithmName.SHA256 ? SHA256.HashData(data)
      :                                     SHA1.HashData(data);
}
