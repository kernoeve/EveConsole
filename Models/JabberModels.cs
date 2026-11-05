namespace EveConsole.Models;

/// <summary>
/// One Jabber login on one server. As many as someone has groups.
///
/// <para>⚠️ The password is not here. It is held per machine in the config file, protected the way the
/// database password is: this table may be on a server several of the owner's machines reach, and a
/// DPAPI blob could not be opened on any but the one that wrote it.</para>
/// </summary>
public class JabberAccount
{
    public int    Id       { get; set; }

    /// <summary>What the person calls this server. Falls back to the address.</summary>
    public string Name     { get; set; } = "";

    /// <summary>The login, as name@domain.</summary>
    public string Address  { get; set; } = "";

    /// <summary>Where to connect when DNS should not decide. Blank means ask DNS.</summary>
    public string Host     { get; set; } = "";
    public int    Port     { get; set; } = 5222;

    /// <summary>Encrypted from the first byte, as on port 5223, rather than STARTTLS.</summary>
    public bool   DirectTls { get; set; }

    /// <summary>Blank for one this machine picks, so two of the owner's machines cannot replace each other.</summary>
    public string Resource { get; set; } = "";

    /// <summary>The nickname used in rooms. Blank for the login's name.</summary>
    public string Nickname { get; set; } = "";

    /// <summary>Fingerprint of a certificate the person accepted, for a server whose own is not trusted.</summary>
    public string TrustedCertificate { get; set; } = "";

    /// <summary>Connects when the app starts. Otherwise only when asked. Either way, a connection someone
    /// wanted is kept up through a server going away, until they say disconnect.</summary>
    public bool   AutoConnect { get; set; }

    /// <summary>Words that make a room message call for attention, separated by commas. The nickname always does.</summary>
    public string AlertWords { get; set; } = "";
}

/// <summary>A room an account keeps. One saved on the server as a bookmark is added here when it is seen.</summary>
public class JabberRoom
{
    public int    Id        { get; set; }
    public int    AccountId { get; set; }
    public string RoomJid   { get; set; } = "";
    public string Name      { get; set; } = "";

    /// <summary>The nickname in this room. Blank for the account's.</summary>
    public string Nickname  { get; set; } = "";

    public bool   AutoJoin  { get; set; } = true;

    /// <summary>See <see cref="JabberRoomAlert"/>.</summary>
    public int    AlertMode { get; set; } = JabberRoomAlert.Mentions;
}

public static class JabberRoomAlert
{
    public const int None     = 0;
    public const int Mentions = 1;
    public const int All      = 2;
}

/// <summary>
/// One message in a conversation, with a person or in a room.
///
/// <para>⚠️ <see cref="SentAt"/> is sorted ISO text rather than a date, because a DateTimeOffset cannot
/// be filtered or ordered on SQLite. Listing goes by <see cref="Id"/>, which is the order they arrived.</para>
/// </summary>
public class JabberStoredMessage
{
    public long   Id           { get; set; }
    public int    AccountId    { get; set; }

    /// <summary>The other person's bare address, or the room's.</summary>
    public string Conversation { get; set; } = "";
    public bool   IsRoom       { get; set; }

    /// <summary>Who said it: a bare address, or in a room the nickname.</summary>
    public string Sender       { get; set; } = "";
    public string Body         { get; set; } = "";

    /// <summary>UTC, as yyyy-MM-ddTHH:mm:ssZ.</summary>
    public string SentAt       { get; set; } = "";

    public bool   IsOutgoing   { get; set; }
    public bool   IsRead       { get; set; }

    /// <summary>Calls for the person's attention: every unread direct message, and a room's that names them.</summary>
    public bool   IsAlert      { get; set; }

    /// <summary>Tells a repeat from a new one: a reconnect's history, or a second client of the owner's.</summary>
    public string DedupeKey    { get; set; } = "";
}
