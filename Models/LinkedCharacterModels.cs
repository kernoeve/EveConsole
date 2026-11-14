namespace EveConsole.Models;

// ─────────────────────────────────────────────────────────────────────────────
//  Linked characters: which characters belong to the same player, and which of them is the
//  main. EVE has no such link, so it can only come from somewhere players register their
//  characters, asked by a connector the user runs — see ConnectorService for asking, and
//  LinkedCharacterStore for what is kept.
//
//  ⚠️ Each connector the user set up is its own source, kept apart from every other one.
//
//  ⚠️ Only the CURRENT links. Each import replaces everything one source said before, so a
//  character moved to another account is simply under that account now, and a character the
//  source no longer lists is gone. The history of moves belongs to the source, not here.
//
//  ⚠️ Empty on nearly every install, and nothing may depend on these rows existing. A character
//  missing from them is its own player, which is what every tool assumed before they existed.
//
//  Kept apart from Character, which holds only the characters signed in on this install. Most
//  linked characters are other people's, with no token here, and never will have one.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One source of links, and the answer it last came from.</summary>
public class LinkedSource
{
    /// <summary>
    /// Whose answer this is: <c>connector:{id}</c> for a connector entry (LinkedCharacterStore.SourceKey).
    /// ⚠️ Never the label a connector sends: two connectors may send the same one.
    /// </summary>
    public string         Source      { get; set; } = "";

    /// <summary>What the answer calls its source. Only a label, for showing: EVE Console knows nothing of it.</summary>
    public string         Label       { get; set; } = "";

    /// <summary>When the tool wrote what was imported. An older file is never imported over it.</summary>
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>When this install imported it.</summary>
    public DateTimeOffset ImportedAt  { get; set; }

    public int            Accounts    { get; set; }
    public int            Characters  { get; set; }
}

/// <summary>One player's account at a source.</summary>
public class LinkedAccount
{
    public string Source          { get; set; } = "";

    /// <summary>The source's own id for the account, which outlasts its characters.</summary>
    public long   AccountId       { get; set; }

    /// <summary>As the source names it, usually after its main.</summary>
    public string Name            { get; set; } = "";

    /// <summary>Null where the account has no main: a source may allow that.</summary>
    public long?  MainCharacterId { get; set; }
}

/// <summary>One character, and the account it belongs to at a source.</summary>
public class LinkedCharacter
{
    public string Source        { get; set; } = "";
    public long   CharacterId   { get; set; }
    public long   AccountId     { get; set; }
    public string Name          { get; set; } = "";
    public bool   IsMain        { get; set; }

    /// <summary>Where the source last saw it: it may be behind ESI by the source's refresh.</summary>
    public int    CorporationId { get; set; }
    public int?   AllianceId    { get; set; }

    /// <summary>Whether the source still holds a working login for it; null where it did not say.</summary>
    public bool?  TokenValid    { get; set; }
}
