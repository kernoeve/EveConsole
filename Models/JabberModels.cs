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

    /// <summary>The status chosen, as an XmppShow, and its words. Kept so a restart comes back the way it was left.</summary>
    public int    SavedShow   { get; set; } = 1;
    public string SavedStatus { get; set; } = "";
}

/// <summary>
/// One piece of how a connection stands, written by whichever process holds the worker lease for the other clients to
/// show: the account's own state under a blank <see cref="Part"/>, and each room under its address. Nothing here outlives
/// the worker that wrote it.
/// </summary>
public class JabberLiveState
{
    public int    AccountId { get; set; }
    public string Part      { get; set; } = "";

    /// <summary>Rises with every write, so a client reads only what it has not seen.</summary>
    public long   Version   { get; set; }

    public string Json      { get; set; } = "";
}

/// <summary>Something a client asks the worker to do, as only the worker has the connection. ⚠️ Never holds a password.</summary>
public class JabberCommand
{
    public long   Id          { get; set; }
    public int    AccountId   { get; set; }
    public string Kind        { get; set; } = "";
    public string Args        { get; set; } = "";
    public long   CreatedUnix { get; set; }

    /// <summary>Waiting, running or done.</summary>
    public int    State       { get; set; }

    public string Result      { get; set; } = "";
    public string Error       { get; set; } = "";
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

    /// <summary>Left by the person: stays out of the list, and is not rejoined from the server's bookmarks.</summary>
    public bool   Left      { get; set; }

    /// <summary>See <see cref="JabberRoomAlert"/>.</summary>
    public int    AlertMode { get; set; } = JabberRoomAlert.Mentions;

    /// <summary>See <see cref="JabberRoomType"/>.</summary>
    public int    RoomType  { get; set; } = JabberRoomType.General;

    /// <summary>
    /// Where an unfinished catch-up from the room's archive starts again, as sorted UTC text. Blank when there is
    /// none. ⚠️ Kept apart from the newest saved message, because a live message saved during a catch-up that then
    /// gets cut off is newer than everything still missing, and asking from it would lose those for good.
    /// </summary>
    public string ArchiveResumeFrom { get; set; } = "";
}

/// <summary>
/// What a room is for. The choices shown are listed in GroupChatViewModel.RoomTypeChoices: a new type goes in both, and
/// is stored as its number, so never renumber these.
/// </summary>
public static class JabberRoomType
{
    public const int General = 0;

    /// <summary>Also read for intel reports, like an intel channel in the chat logs.</summary>
    public const int Intel   = 1;
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

    /// <summary>The id it was sent with, which is what a correction names.</summary>
    public string MessageId    { get; set; } = "";

    /// <summary>A room message with the person's name in it, whatever the room is set to alert on.</summary>
    public bool   IsMention    { get; set; }

    /// <summary>The room's own id for a room message, which is what a moderator's removal names. Blank otherwise.</summary>
    public string StanzaId     { get; set; } = "";

    /// <summary>See <see cref="JabberDeleted"/>. A deleted message keeps its place, with its words gone.</summary>
    public int    Deleted      { get; set; }

    /// <summary>Why a moderator removed it, if they said.</summary>
    public string DeletedReason { get; set; } = "";

    /// <summary>What it answers: the room's id for it in a room, its own id with a person. Blank for none.</summary>
    public string ReplyId      { get; set; } = "";

    /// <summary>What its sender quoted of what it answers, for when that is not kept here.</summary>
    public string ReplyQuote   { get; set; } = "";

    /// <summary>Who said it in a room, as the room knows them whatever their nickname (XEP-0421). Blank when the room gives none.</summary>
    public string OccupantId   { get; set; } = "";

    /// <summary>Its sender asked to be told when it has been seen.</summary>
    public bool   Markable     { get; set; }

    /// <summary>This account's message, and the person it went to has seen it (XEP-0333).</summary>
    public bool   SeenByThem   { get; set; }
}

/// <summary>
/// One person's reactions (XEP-0444) to one message: their whole set, as each new one replaces the last.
/// </summary>
public class JabberReaction
{
    public int    AccountId    { get; set; }
    public string Conversation { get; set; } = "";

    /// <summary>The message reacted to: the room's id for it in a room, its own id with a person.</summary>
    public string Target       { get; set; } = "";

    /// <summary>Who: as the room knows them where it gives a lasting id, else a nickname in a room, an address with a person.</summary>
    public string Sender       { get; set; } = "";

    /// <summary>The name to show them by, which in a room may be other than what they are known by.</summary>
    public string SenderName   { get; set; } = "";

    /// <summary>Their emojis, in the order they chose them, separated by spaces.</summary>
    public string Emojis       { get; set; } = "";

    /// <summary>UTC, as yyyy-MM-ddTHH:mm:ssZ: emojis are shown in the order first chosen.</summary>
    public string UpdatedAt    { get; set; } = "";
}

/// <summary>Whether a message was deleted, and by whom. Stored as the number, so never renumber these.</summary>
public static class JabberDeleted
{
    public const int No          = 0;
    public const int BySender    = 1;
    public const int ByModerator = 2;
}
