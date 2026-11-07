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

    /// <summary>
    /// ⚠️ Only the old shared answer to "connect when the app starts", waiting to be taken once by the first PC on this
    /// build: see <see cref="Services.Mumble.MumbleService.AutoConnectsHere"/>, which is the answer now. A server takes one
    /// connection per identity, and the login and certificate are shared, so a shared answer had every PC that started
    /// connect as the same person and throw the last one off.
    /// </summary>
    public bool   AutoConnect { get; set; }

    /// <summary>The key that talks only to the current channel, and nothing linked to it. Zero for none.</summary>
    public int    WhisperKey { get; set; }

    /// <summary>The key that shouts, the standard client's Push to Talk: the current channel and those linked to it. Zero for none.</summary>
    public int    ShoutKey   { get; set; }

    /// <summary>Whether the person chose to be muted. ⚠️ Not changed by deafening, so that undeafening puts back what they had: see <see cref="Silent"/>.</summary>
    public bool   SelfMuted    { get; set; }
    public bool   SelfDeafened { get; set; }

    /// <summary>Whether nothing is to go out: being deafened mutes as well, for as long as it lasts.</summary>
    public bool   Silent => SelfMuted || SelfDeafened;

    /// <summary>The channel to return to on connecting, by its names from the root, as ids can change.</summary>
    public string LastChannel  { get; set; } = "";

    /// <summary>Talks whenever the microphone hears a voice, rather than while a key is held. The microphone stays open while connected.</summary>
    public bool   VoiceActivated { get; set; }
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

    /// <summary>Not heard here, whatever anyone else hears. The volume is kept for when that ends.</summary>
    public bool   LocalMute { get; set; }

    /// <summary>Their words are dropped here as they arrive.</summary>
    public bool   IgnoreText { get; set; }
}
