namespace EveConsole.Models;

/// <summary>
/// One IRC network login. As many as someone has groups.
///
/// <para>⚠️ The password is not here: it is kept with the other service logins (SharedLogins), by
/// <c>IrcService.PasswordReference</c>.</para>
///
/// <para>⚠️ Whether it connects when the app starts is per machine, not here: IRC has one nickname per connection, so
/// two of the owner's machines both connecting would fight over it (one ends up as name_).</para>
/// </summary>
public class IrcServer
{
    public int    Id       { get; set; }

    /// <summary>What the person calls this network. Falls back to the host.</summary>
    public string Name     { get; set; } = "";

    public string Host     { get; set; } = "";
    public int    Port     { get; set; } = 6697;

    /// <summary>Encrypted (TLS). On by default; without it no password is ever sent, except to this machine.</summary>
    public bool   Tls      { get; set; } = true;

    public string Nickname { get; set; } = "";

    /// <summary>The user name part of nick!user@host. Blank for the nickname.</summary>
    public string Username { get; set; } = "";

    public string RealName { get; set; } = "";

    /// <summary>See <see cref="EveConsole.Services.Irc.IrcLoginKind"/>.</summary>
    public int    LoginKind { get; set; }

    /// <summary>The account name to log in as, where it is not the nickname.</summary>
    public string Account  { get; set; } = "";

    /// <summary>Fingerprint of a certificate the person accepted, for a server whose own is not trusted.</summary>
    public string TrustedCertificate { get; set; } = "";

    /// <summary>Words that make a channel message call for attention, separated by commas. The nickname always does.</summary>
    public string AlertWords { get; set; } = "";

    /// <summary>The away message chosen, blank when here. Kept so a restart comes back the way it was left.</summary>
    public string AwayMessage { get; set; } = "";

    /// <summary>Commands sent once connected, one per line, as a network asks of its users (a /mode, a /msg to a bot).</summary>
    public string OnConnect { get; set; } = "";
}

/// <summary>A channel a server login keeps.</summary>
public class IrcChannel
{
    public int    Id        { get; set; }
    public int    ServerId  { get; set; }

    /// <summary>As the person typed it, #channel. Matched to what the server says by the server's own case rules.</summary>
    public string Name      { get; set; } = "";

    public bool   AutoJoin  { get; set; } = true;

    /// <summary>Left by the person: stays out of the list.</summary>
    public bool   Left      { get; set; }

    /// <summary>See <see cref="JabberRoomAlert"/>: the same choices.</summary>
    public int    AlertMode { get; set; } = JabberRoomAlert.Mentions;

    /// <summary>See <see cref="JabberRoomType"/>: the same list of what a room is for.</summary>
    public int    RoomType  { get; set; } = JabberRoomType.General;
}

/// <summary>
/// One message, in a channel or with a person.
///
/// <para>⚠️ <see cref="SentAt"/> is sorted ISO text rather than a date, because a DateTimeOffset cannot be filtered or
/// ordered on SQLite. Listing goes by <see cref="Id"/>, which is the order they arrived.</para>
/// </summary>
public class IrcStoredMessage
{
    public long   Id           { get; set; }
    public int    ServerId     { get; set; }

    /// <summary>The channel, or the other person's nickname, folded the server's way so case cannot split a conversation.</summary>
    public string Conversation { get; set; } = "";
    public bool   IsChannel    { get; set; }

    /// <summary>The nickname that said it, as written.</summary>
    public string Sender       { get; set; } = "";
    public string Body         { get; set; } = "";

    /// <summary>See <see cref="IrcMessageKind"/>.</summary>
    public int    Kind         { get; set; }

    /// <summary>UTC, as yyyy-MM-ddTHH:mm:ss.fffZ.</summary>
    public string SentAt       { get; set; } = "";

    public bool   IsOutgoing   { get; set; }
    public bool   IsRead       { get; set; }

    /// <summary>Calls for the person's attention: every unread direct message, and a channel's that names them.</summary>
    public bool   IsAlert      { get; set; }

    /// <summary>A channel message with the person's name in it, whatever the channel is set to alert on.</summary>
    public bool   IsMention    { get; set; }

    /// <summary>Tells a repeat from a new one: history after a reconnect, or a second of the owner's machines.</summary>
    public string DedupeKey    { get; set; } = "";

    /// <summary>The server's id for it (msgid), which replies and reactions name. Blank where the server gives none.</summary>
    public string MessageId    { get; set; } = "";

    /// <summary>What it answers, by <see cref="MessageId"/>. Blank for none.</summary>
    public string ReplyId      { get; set; } = "";

    /// <summary>The nickname a direct message conversation is shown under, as last written.</summary>
    public string PeerName     { get; set; } = "";
}

/// <summary>What sort of line: said, said as an action (/me), or a notice. Stored as the number: never renumber.</summary>
public static class IrcMessageKind
{
    public const int Said   = 0;
    public const int Action = 1;
    public const int Notice = 2;
}

/// <summary>One person's reactions (+draft/react) to one message: each emoji once.</summary>
public class IrcReaction
{
    public int    ServerId     { get; set; }
    public string Conversation { get; set; } = "";

    /// <summary>The message reacted to, by its id.</summary>
    public string Target       { get; set; } = "";
    public string Sender       { get; set; } = "";

    /// <summary>Their emojis, in the order they chose them, separated by spaces.</summary>
    public string Emojis       { get; set; } = "";

    /// <summary>UTC, as yyyy-MM-ddTHH:mm:ssZ.</summary>
    public string UpdatedAt    { get; set; } = "";
}
