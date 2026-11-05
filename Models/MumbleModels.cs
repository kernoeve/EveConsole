namespace EveConsole.Models;

/// <summary>
/// One Mumble server, with its own keys and its own idea of how to connect. Any number of them, connected
/// at once if wanted, each talking only on its own keys.
///
/// <para>⚠️ The password and the client certificate are not here: they are held per machine in the config
/// file, protected the way the database password is.</para>
/// </summary>
public class MumbleServer
{
    public int    Id       { get; set; }
    public string Name     { get; set; } = "";
    public string Host     { get; set; } = "";
    public int    Port     { get; set; } = 64738;
    public string Username { get; set; } = "";

    /// <summary>Access tokens some channels ask for, separated by commas.</summary>
    public string Tokens   { get; set; } = "";

    /// <summary>Fingerprint of a certificate the person accepted, as most Mumble servers' are self-signed.</summary>
    public string TrustedCertificate { get; set; } = "";

    /// <summary>Connects when the app starts. Otherwise only when asked. Either way, a connection someone
    /// wanted is kept up through a server going away, until they say disconnect.</summary>
    public bool   AutoConnect { get; set; }

    /// <summary>The key that talks only to the current channel, and nothing linked to it. Zero for none.</summary>
    public int    WhisperKey { get; set; }

    /// <summary>The key that shouts, the standard client's Push to Talk: the current channel and those linked to it. Zero for none.</summary>
    public int    ShoutKey   { get; set; }

    public bool   SelfMuted    { get; set; }
    public bool   SelfDeafened { get; set; }

    /// <summary>The channel to return to on connecting, by its names from the root, as ids can change.</summary>
    public string LastChannel  { get; set; } = "";
}

/// <summary>
/// One private message on a server, kept as long as Jabber's history is. Channel talk is not kept past the session:
/// a server holds no history of it, so anything saved would be full of gaps from whenever this client was away.
/// </summary>
public class MumbleDirectMessage
{
    public long   Id        { get; set; }
    public int    ServerId  { get; set; }
    public string Sender    { get; set; } = "";
    public string Body      { get; set; } = "";

    /// <summary>UTC, as yyyy-MM-ddTHH:mm:ssZ: sorted text, because a DateTimeOffset cannot be filtered on SQLite.</summary>
    public string SentAt    { get; set; } = "";

    /// <summary>Who the conversation is with: their certificate's hash where they have one, which outlasts a change of name.</summary>
    public string PeerKey   { get; set; } = "";

    /// <summary>What they were called at the time.</summary>
    public string PeerName  { get; set; } = "";

    public bool   IsOutgoing { get; set; }
    public bool   IsRead     { get; set; }

    /// <summary>Calls for attention until read, as every private message does.</summary>
    public bool   IsAlert    { get; set; }
}

/// <summary>How loud one person is on one server, for this listener.</summary>
public class MumbleUserVolume
{
    public int    ServerId { get; set; }

    /// <summary>Their certificate's hash where they have one, which outlasts a change of name.</summary>
    public string UserKey  { get; set; } = "";

    /// <summary>1 as sent, 0 silent, up to 3.</summary>
    public double Volume   { get; set; } = 1.0;
}
