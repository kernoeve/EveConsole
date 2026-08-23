namespace EveConsole.Models;

/// <summary>
/// A shop the buyer talks to by EVE mail.
///
/// <para>One character, one price list, and a rule about who may write to it. The character is
/// the address: buyers mail it, and every reply is sent from it, so the conversation reads as one
/// correspondence with a shop rather than with whichever alt happened to be logged in.</para>
///
/// <para><b>⚠️ The posting is the shop.</b> There is no separate list of what may be ordered —
/// the sections and items of the attached <see cref="SalePosting"/> define both what gets quoted
/// and what can be bought. Two lists would eventually disagree, and the disagreement would show
/// up as a buyer ordering something the shop had just told them the price of.</para>
/// </summary>
public class Store
{
    public int    Id            { get; set; }
    public string Name          { get; set; } = "";

    /// <summary>The character whose mail is read and replied from. Must be one we hold a token
    /// for, with the mail scopes granted.</summary>
    public long   CharacterId   { get; set; }
    public string CharacterName { get; set; } = "";

    /// <summary>The <see cref="SalePosting"/> sent in answer to a price request, and the
    /// catalogue orders are checked against.</summary>
    public int    PostingId     { get; set; }

    /// <summary>
    /// Who may be served: "Anyone", or "List" to mean the entries in <see cref="StoreSender"/>.
    ///
    /// <para>⚠️ Defaults to List, which with no entries serves nobody. A shop that answered
    /// everyone by default would start replying to strangers the moment it was created, before
    /// its owner had decided that was wanted — and mail sent cannot be recalled.</para>
    /// </summary>
    public string SenderPolicy  { get; set; } = "List";

    /// <summary>Off by default for the same reason: creating a shop should not put it on the
    /// air.</summary>
    public bool   Enabled       { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One entry on a store's allow list: a character, a corporation, or an alliance.
///
/// <para>All three in one table rather than three settings, because they answer one question and
/// a sender is matched against whichever of them applies — their own id, their corporation's, or
/// their alliance's. Adding a fourth kind later is a row, not a column.</para>
/// </summary>
public class StoreSender
{
    public int    Id         { get; set; }
    public int    StoreId    { get; set; }
    public long   EntityId   { get; set; }
    public string EntityType { get; set; } = "";   // "character" | "corporation" | "alliance"
    public string Name       { get; set; } = "";
}

/// <summary>
/// Every mail the shop received or sent, and what came of it.
///
/// <para>Kept in full — subject and body, both directions — because this is the shop's record of
/// what was agreed. An order dispute is settled by what the buyer actually wrote, not by what the
/// parser made of it, and a reply nobody can produce is a reply that may as well not have been
/// sent. It is also the only place a rejected or unparsed message survives at all: those create
/// no order and would otherwise vanish.</para>
/// </summary>
public class StoreMail
{
    public int    Id        { get; set; }
    public int    StoreId   { get; set; }

    /// <summary>"in" or "out".</summary>
    public string Direction { get; set; } = "in";

    /// <summary>ESI's mail id for a received mail. Zero on anything we sent — ESI's send endpoint
    /// returns an id, but the sent copy is not in the shop character's inbox, so there is nothing
    /// to reconcile it against.</summary>
    public int    MailId    { get; set; }

    /// <summary>The other party: who wrote in, or who was written to.</summary>
    public long   PartyId   { get; set; }
    public string PartyName { get; set; } = "";

    public string Subject   { get; set; } = "";
    public string Body      { get; set; } = "";

    /// <summary>The keyword this was read as, or "" when nothing was recognised.</summary>
    public string Command   { get; set; } = "";

    /// <summary>"ok" | "rejected" | "unknown" | "error" — what the shop did about it.</summary>
    public string Outcome   { get; set; } = "";

    /// <summary>Why, in words: the sender was not on the list, the item was not in the catalogue,
    /// ESI refused the reply. Shown in the Stores UI beside the message.</summary>
    public string Detail    { get; set; } = "";

    /// <summary>The order this concerns, where it concerns one.</summary>
    public string OrderRef  { get; set; } = "";

    public DateTimeOffset At { get; set; }
}
