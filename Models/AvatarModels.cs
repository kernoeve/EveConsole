namespace EveConsole.Models;

/// <summary>
/// A person's picture, from Jabber (their vCard) or Mumble (the server's copy of theirs), kept once under its hash
/// however many people and keys point at it.
/// </summary>
public class ChatAvatar
{
    /// <summary>The hash the service named it by, as lower-case hex: SHA-1 on both.</summary>
    public string Hash   { get; set; } = "";
    public byte[] Bytes  { get; set; } = [];
    public int    Width  { get; set; }
    public int    Height { get; set; }
}

/// <summary>Whose picture is which. One person can have several keys: in a room, and at their own address.</summary>
public class ChatAvatarOwner
{
    /// <summary>"jabber" or "mumble", as the Messages tool names them.</summary>
    public string Service   { get; set; } = "";

    /// <summary>The Jabber account or Mumble server it was seen on.</summary>
    public int    ServerId  { get; set; }

    /// <summary>
    /// Who: a Jabber address, room/nickname for someone in a room, a Mumble user's key or "name:" and their name, or
    /// "me" for this account itself.
    /// </summary>
    public string Person    { get; set; } = "";
    public string Hash      { get; set; } = "";

    /// <summary>UTC, as yyyy-MM-ddTHH:mm:ssZ: other clients look for changes by it.</summary>
    public string UpdatedAt { get; set; } = "";
}
