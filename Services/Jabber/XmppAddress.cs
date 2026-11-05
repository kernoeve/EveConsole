namespace EveConsole.Services.Jabber;

/// <summary>
/// A Jabber address: <c>local@domain/resource</c>, any part of it optional but the domain.
///
/// <para>Compared on the bare address as lower case for local and domain, as servers do. The resource
/// is case sensitive, and in a room it is the person's nickname, so it keeps the case it came with.</para>
/// </summary>
public readonly record struct XmppAddress(string Local, string Domain, string Resource)
{
    public static XmppAddress Parse(string? text)
    {
        var s = (text ?? "").Trim();

        var slash    = s.IndexOf('/');
        var resource = slash >= 0 ? s[(slash + 1)..] : "";
        if (slash >= 0) s = s[..slash];

        var at = s.IndexOf('@');
        return at >= 0
            ? new XmppAddress(s[..at].ToLowerInvariant(), s[(at + 1)..].ToLowerInvariant(), resource)
            : new XmppAddress("", s.ToLowerInvariant(), resource);
    }

    /// <summary>Without the resource: the person, or the room.</summary>
    public string Bare => Local.Length > 0 ? $"{Local}@{Domain}" : Domain;

    public string Full => Resource.Length > 0 ? $"{Bare}/{Resource}" : Bare;

    public override string ToString() => Full;
}
