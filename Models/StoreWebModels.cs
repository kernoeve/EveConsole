namespace EveConsole.Models;

/// <summary>
/// What the app has told a store's web site about one order: the content hash of the row as it
/// was last acknowledged.
///
/// <para>⚠️ The reason order history is a merge rather than a resend. Each cycle the sync pushes
/// only rows whose hash differs from the one here, or that are missing here, and the site
/// upserts by the app's order id. Driven by hashes rather than timestamps so it does not depend
/// on every write path stamping an updated-at, and so an edit made outside the tracker — a
/// re-picked buyer id — still travels. A row here that no longer has an order behind it becomes
/// a tombstone the site is told to hide.</para>
///
/// <para>Written only after the site acknowledges the id, so a failed call is simply retried.
/// Cleared whole when the site reports a new database generation.</para>
/// </summary>
public class StoreWebPush
{
    public int    StoreId { get; set; }
    public int    OrderId { get; set; }
    public string Hash    { get; set; } = "";
}

/// <summary>
/// Everything a store's web site has sent the app, and what came of it — the web counterpart of
/// <see cref="StoreMail"/>.
///
/// <para>Kept in full for the same reason the mail log is: it is the record of what a buyer asked
/// for, as they asked it, and the only place a rejected or unbookable order survives at all. A
/// row in review is one the owner decides about from the Stores screen; a booked row names the
/// order it became.</para>
/// </summary>
public class StoreWebEvent
{
    public int    Id         { get; set; }
    public int    StoreId    { get; set; }

    /// <summary>The site's sequence number for the event, unique per site.</summary>
    public long   Seq        { get; set; }

    /// <summary>"order", "cancel" or "visit".</summary>
    public string Kind       { get; set; } = "";

    /// <summary>The site's own id for the order this concerns, where it concerns one.</summary>
    public string WebOrderId { get; set; } = "";

    public long   BuyerId    { get; set; }
    public string BuyerName  { get; set; } = "";

    /// <summary>The event exactly as the site sent it, as JSON.</summary>
    public string Payload    { get; set; } = "";

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>"booked" | "applied" | "review" | "rejected" | "error" — what the app did; a
    /// visit is only ever "noted".</summary>
    public string Outcome    { get; set; } = "";

    /// <summary>Why, in words: what failed the check, or what was cancelled.</summary>
    public string Detail     { get; set; } = "";

    /// <summary>The order this became or concerned, where it did.</summary>
    public string OrderRef   { get; set; } = "";
}
