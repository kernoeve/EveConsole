namespace EveConsole.Models;

/// <summary>
/// A set of emotes loaded from a Pidgin smiley theme: a zip, or a folder with a <c>theme</c> file in it.
///
/// <para>Stored once, in the database every machine of the owner's reaches, and given to Jabber servers by
/// <see cref="JabberEmotePack"/>. A pack no server uses any more is deleted with its emotes.</para>
/// </summary>
public class EmotePack
{
    public int    Id          { get; set; }
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author      { get; set; } = "";

    /// <summary>The theme's own icon, as one of its codes. Blank when it names none that loaded.</summary>
    public string IconCode    { get; set; } = "";

    /// <summary>UTC, as yyyy-MM-ddTHH:mm:ssZ.</summary>
    public string AddedAt     { get; set; } = "";
}

/// <summary>
/// One picture, kept once however many packs and codes use it, under the SHA-256 of its bytes. The bytes are what the
/// theme had: a GIF stays a GIF, so it still moves.
/// </summary>
public class EmoteImage
{
    /// <summary>SHA-256 of <see cref="Bytes"/>, as lower-case hex.</summary>
    public string Hash     { get; set; } = "";
    public byte[] Bytes    { get; set; } = [];
    public int    Width    { get; set; }
    public int    Height   { get; set; }
    public bool   Animated { get; set; }
}

/// <summary>A word in a pack that stands for a picture, such as <c>:wave:</c>. Several can share one picture.</summary>
public class EmoteCode
{
    public int    PackId    { get; set; }
    public string Code      { get; set; } = "";
    public string ImageHash { get; set; } = "";

    /// <summary>Where the theme listed it, so the picker shows a pack in its own order.</summary>
    public int    Position  { get; set; }
}

/// <summary>
/// A pack a Jabber server uses. Each server has its own, so the same word can be a picture on one and plain text on
/// another. Where two of a server's packs have the same code, the one with the lower <see cref="Position"/> wins.
/// </summary>
public class JabberEmotePack
{
    public int AccountId { get; set; }
    public int PackId    { get; set; }
    public int Position  { get; set; }
}
