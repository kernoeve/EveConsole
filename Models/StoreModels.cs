namespace EveConsole.Models;

/// <summary>
/// A shop buyers reach by EVE mail, by a web site, or both.
///
/// <para>One price list, one rule about who may buy, one order book — and two channels, each its
/// own switch. Mail needs a character: buyers mail it and every reply is sent from it, so the
/// conversation reads as one correspondence with a shop rather than with whichever alt happened
/// to be logged in. The web needs a site address and a secret, and the character only if the
/// owner wants web buyers mailed as their orders move. A store may have either or both.</para>
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

    /// <summary>The mail channel: read and answer mail sent to <see cref="CharacterId"/>. Off by
    /// default for the same reason: creating a shop should not put it on the air.</summary>
    public bool   Enabled       { get; set; }

    /// <summary>
    /// ⚠️ Mail older than this is never answered, and the mark moves forward every time the shop
    /// is switched on.
    ///
    /// <para>A character's inbox holds months of unrelated mail. Without this, opening a shop
    /// would reply to all of it at once — hundreds of messages, to real people, that cannot be
    /// recalled. Moving it on each enable also means a shop closed for a week does not answer the
    /// week it missed when it reopens.</para>
    /// </summary>
    public DateTimeOffset ListenFrom { get; set; }

    /// <summary>
    /// Hidden rather than removed.
    ///
    /// <para>⚠️ The row stays so everything that points at it still resolves. Orders keep their
    /// StoreId for life — they outlive the shop deliberately — and deleting the row left that id
    /// pointing at nothing, so an order could no longer say which shop took it. Its messages
    /// stay too, for the same reason: they are the record of what was agreed.</para>
    ///
    /// <para>A deleted store is closed as well as hidden, so nothing can leave it quietly
    /// answering mail from behind the list.</para>
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Give an in-stock order an expected date of its own, this many days out.
    ///
    /// <para>An order filled from the shelf has no job behind it, so nothing else would ever set
    /// a date — it would sit blank until a contract appeared. Blank reads as "no idea", when the
    /// truth is "as soon as somebody gets to it", and a date is what lets the order be ranked
    /// against everything else waiting.</para>
    ///
    /// <para>⚠️ Only for stock. A job-sourced order takes its date from the job, which is a real
    /// forecast rather than a promise, and overwriting that with a guess would be worse than
    /// having no guess at all.</para>
    /// </summary>
    /// <summary>
    /// Text put above and below every mail this store sends, with a colour each. Empty for none.
    ///
    /// <para>What a shop says on all of its correspondence and nowhere else — a greeting, a
    /// standing note about delivery, a line about who to talk to. It belongs on the store rather
    /// than in each message because it is the same regardless of what was asked.</para>
    ///
    /// <para>⚠️ Written into the mail exactly as typed, so EVE's own markup works here: &lt;b&gt;,
    /// &lt;i&gt;, &lt;u&gt;, &lt;br&gt;, font tags and showinfo links. Nothing is escaped, which
    /// is deliberate — this is the shop owner's text, not a buyer's.</para>
    /// </summary>
    /// <summary>
    /// Labels put on every order this store takes, comma-separated. Empty for none.
    ///
    /// <para>A flat string rather than rows, because this is a setting somebody types once — the
    /// orders themselves carry proper label rows, which is where filtering and counting happen.</para>
    /// </summary>
    public string OrderLabels        { get; set; } = "";

    /// <summary>
    /// Send this store's own usage message instead of the stock one.
    ///
    /// <para>For a shop whose rules are not the general ones — a first-capital programme where a
    /// buyer may take one hull and quantities are beside the point — the stock explanation is
    /// instructions that do not apply, which is worse than no instructions at all.</para>
    /// </summary>
    public bool UseCustomUsage { get; set; }

    /// <summary>The custom usage message, EVE mail markup and all. Ignored while the flag is off.</summary>
    public string CustomUsage { get; set; } = "";

    /// <summary>
    /// What INFO sends: whatever this store wants buyers to know about it.
    ///
    /// <para>⚠️ No flag beside it, unlike the usage message. Text is the switch — a store with
    /// something to say has written it, and one with nothing to say has an empty box. A checkbox
    /// would add a second way to mean the same thing, and a way for them to disagree.</para>
    ///
    /// <para>Whitespace does not count as something to say: a box holding a single blank line
    /// leaves INFO off the command list, because the command would answer with nothing.</para>
    /// </summary>
    public string Info { get; set; } = "";

    public string MessageHeader      { get; set; } = "";
    public string MessageHeaderColor { get; set; } = "";
    public string MessageFooter      { get; set; } = "";
    public string MessageFooterColor { get; set; } = "";

    public bool AutoEstimateInStock { get; set; } = true;

    /// <summary>Days from the order date. One by default — a shelf item is a contract to write,
    /// not a thing to build.</summary>
    public int  AutoEstimateDays    { get; set; } = 1;

    // ── Purchase limit ────────────────────────────────────────────────────────
    //
    // How much one buyer may take, counted over their orders in this store that were not
    // cancelled: so many units of each item type, of each item group, or of anything at all,
    // within a rolling period or ever. Off by default; a programme that hands out the first
    // hull of each kind is what it is for. The site greys out what a buyer may no longer order
    // and refuses more than the remainder; the app checks again when it books.

    public bool   LimitEnabled     { get; set; }

    /// <summary>Units per <see cref="LimitScope"/> within the period.</summary>
    public int    LimitUnits       { get; set; } = 1;

    /// <summary>"type", "group" or "store".</summary>
    public string LimitScope       { get; set; } = "type";

    /// <summary>"days", "months", "years" or "all".</summary>
    public string LimitPeriod      { get; set; } = "all";

    /// <summary>How many of <see cref="LimitPeriod"/>; unused for "all".</summary>
    public int    LimitPeriodCount { get; set; } = 1;


    // ── The web channel ───────────────────────────────────────────────────────
    //
    // A site the owner hosts, which the app pushes the posting and the order book to and pulls
    // orders from — see Services.WebStore.WebStoreProtocol. The app is the only side that ever
    // calls; the site holds what it was told and what buyers did.

    /// <summary>The web channel: push to and pull from <see cref="WebUrl"/>. Off by default.</summary>
    public bool   WebEnabled       { get; set; }

    /// <summary>The site's address, scheme and host, e.g. https://shop.example.workers.dev.</summary>
    public string WebUrl           { get; set; } = "";

    /// <summary>
    /// The secret the app and the site share, used as an HMAC key and never sent. Generated by
    /// the app and pasted into the site's secrets, or the other way round.
    ///
    /// <para>⚠️ Kept with the store, in the database, like the ESI refresh tokens: the sync runs
    /// on whichever client holds the worker lease, which may be another machine. A compromise of
    /// the database already exposes tokens that read wallets and mail; this adds nothing new.</para>
    /// </summary>
    public string WebSecret        { get; set; } = "";

    /// <summary>
    /// The app theme the site wears, by key — see Services.WebStore.WebThemes.
    ///
    /// <para>⚠️ A setting on the store, resolved by key. Nothing to do with the theme the owner's
    /// desktop is showing: a desktop on Light pushes a Dark store if that is what is set here.</para>
    /// </summary>
    public string WebTheme         { get; set; } = "dark";

    /// <summary>Whether a buyer may flip the site between the theme's dark and light pair.</summary>
    public bool   WebBuyerMaySwitch { get; set; } = true;

    /// <summary>The themes a buyer may pick from on the site, as keys separated by commas, the
    /// store's own among them. Empty means the old rule: the store's theme and, with
    /// <see cref="WebBuyerMaySwitch"/>, its dark or light partner.</summary>
    public string WebThemes        { get; set; } = "";

    /// <summary>Also mail web buyers as their orders move, when the store has a character.</summary>
    public bool   WebMailUpdates   { get; set; } = true;

    /// <summary>The owner's own words on the site — terms, pickup, whatever a buyer should read.
    /// Plain text; blank lines separate paragraphs.</summary>
    public string WebBlurb         { get; set; } = "";

    /// <summary>The last site event the app has applied. The site prunes at and below it.</summary>
    public long   WebCursor        { get; set; }

    /// <summary>The site database generation last seen. A different one means the site's data
    /// was recreated, and the app resends everything it holds.</summary>
    public string WebGeneration    { get; set; } = "";

    /// <summary>What the site said it was running, last time it answered.</summary>
    public string WebSiteVersion   { get; set; } = "";

    public DateTimeOffset? WebLastSyncAt { get; set; }

    /// <summary>Why the last exchange failed, in words, or empty. Shown on the Stores screen.</summary>
    public string WebLastError     { get; set; } = "";

    // ── Hosting on Cloudflare, from this app ──────────────────────────────────
    //
    // Filled in by the Deploy button so that Update knows where the site is. A site set up by
    // hand leaves them empty and is updated by hand; the address above is all syncing needs.

    /// <summary>The Cloudflare account the site was deployed to.</summary>
    public string WebCloudflareAccountId { get; set; } = "";

    /// <summary>The Worker's name on that account, which is the D1 database's name too.</summary>
    public string WebWorkerName        { get; set; } = "";

    /// <summary>A domain of the owner's own for the site, on their Cloudflare account — e.g.
    /// store.example.com — or empty for the free workers.dev address. The Deploy button attaches
    /// the Worker to it and switches workers.dev off, so buyers and the EVE application's
    /// callback see one name.</summary>
    public string WebCustomHostname    { get; set; } = "";

    /// <summary>The EVE developer application the site signs buyers in with: its client id…</summary>
    public string WebEveClientId       { get; set; } = "";

    /// <summary>…and its secret key, kept with the store like <see cref="WebSecret"/> and for the
    /// same reason. The Deploy button places both on the site every time; a site set up by hand
    /// takes the same values as its own secrets.</summary>
    public string WebEveClientSecret   { get; set; } = "";



    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A picture the site shows for a store — today only the banner across the top of the price
/// list. One row per store and kind, bytes and all, so every client of a shared database pushes
/// the same site; the sync names it by hash and sends the bytes only when the site lacks them.
/// </summary>
public class StoreWebAsset
{
    public const string Banner = "banner";

    public int    StoreId     { get; set; }
    public string Kind        { get; set; } = Banner;
    public string ContentType { get; set; } = "";
    public string Sha256      { get; set; } = "";
    /// <summary>The file it was chosen from, for the owner's eyes only.</summary>
    public string FileName    { get; set; } = "";
    public int    Width       { get; set; }
    public int    Height      { get; set; }
    public byte[] Bytes       { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
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
