using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveConsole.Services.WebStore;

/// <summary>
/// The exchange between the app and a store's web site — one signed call per cycle, both
/// directions in it.
///
/// <para>The app is the source of truth and the site is a shop window with an inbox. Nothing
/// on the site reaches the database: the app pushes what the site may show and pulls what
/// buyers did there, and every state a buyer sees is one the app has confirmed. The site
/// repository holds the same contract as a document, and <see cref="Version"/> is what the two
/// agree they are speaking; a site behind or ahead answers with its own number and the app
/// says so rather than guessing at the difference.</para>
///
/// <para>⚠️ Everything that comes BACK is untrusted input, exactly as a mail body is. A buyer's
/// order is booked only after the app has checked the item is in the posting, the quantity is
/// within the bounds the mail path enforces, the buyer passes the store's sender policy and the
/// price is the one the app pushed. Anything else lands in review with the reason, never booked
/// and never silently dropped.</para>
/// </summary>
public static class WebStoreProtocol
{
    public const int Version = 1;

    /// <summary>The path on the site the app calls. Everything else the site serves is for buyers.</summary>
    public const string SyncPath = "/api/sync";
    /// <summary>Where the banner's bytes go, on their own signed call.</summary>
    public const string BannerPath = "/api/sync/banner";

    public const string TimestampHeader = "X-EveConsole-Timestamp";
    public const string SignatureHeader = "X-EveConsole-Signature";

    /// <summary>
    /// Wire shape: camel-cased, nulls left out, unknown members ignored — so a site one version
    /// ahead can add a field without breaking an app one version behind.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling              = JsonNumberHandling.AllowReadingFromString,
    };
}

// ── Request: what the app pushes ─────────────────────────────────────────────

public sealed class SyncRequest
{
    public int    Protocol   { get; set; } = WebStoreProtocol.Version;
    public string AppVersion { get; set; } = "";

    /// <summary>The last event sequence the app has applied. The site treats everything at or
    /// below it as done and may prune it.</summary>
    public long   Cursor     { get; set; }

    /// <summary>The site database generation the app last saw, or empty. A different one in the
    /// reply means the site's data was recreated and the app resends everything it holds.</summary>
    public string Generation { get; set; } = "";

    public StoreInfoDto    Store     { get; set; } = new();
    public CatalogueDto    Catalogue { get; set; } = new();

    /// <summary>Order rows changed since the last acknowledged push, plus what to remove.</summary>
    public List<OrderDto>  Orders    { get; set; } = [];
    public List<int>       Removed   { get; set; } = [];

    /// <summary>What became of web orders that never made it to the order book: under review,
    /// or rejected with the reason, so the buyer is told rather than left waiting.</summary>
    public List<WebOrderStateDto> WebOrders { get; set; } = [];

    /// <summary>More order rows are waiting behind this page; the app will call again at once.</summary>
    public bool More { get; set; }
}

public sealed class StoreInfoDto
{
    public string Name          { get; set; } = "";
    /// <summary>The owner's own words for the web, plain text with blank lines as paragraphs.</summary>
    public string Blurb         { get; set; } = "";
    /// <summary>The store's mailbox character, when it has one. Informational; the site shows nothing from it.</summary>
    public string CharacterName { get; set; } = "";
    /// <summary>Unused since site 0.1.4 — what the owner wants said about pickup goes in the blurb. Kept so older sites still read the field.</summary>
    public string Pickup        { get; set; } = "";

    /// <summary>"anyone" or "list". With "list", only <see cref="Allowed"/> may sign in.</summary>
    public string SenderPolicy  { get; set; } = "list";
    public List<AllowedDto> Allowed { get; set; } = [];

    /// <summary>Whether the owner also mails web buyers as their orders move. Informational.</summary>
    public bool MailUpdates     { get; set; }
    /// <summary>The store's per-buyer purchase limit, when it has one; null for none.</summary>
    public LimitDto? Limit      { get; set; }
    /// <summary>The banner across the top of the price list, by hash and type; the bytes go on
    /// their own call. ⚠️ Null is written out, not left off like other nulls: null tells the site
    /// the store has no banner now, while a push without the field (an older app) leaves whatever
    /// the site holds alone.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public BannerDto? Banner    { get; set; }

    public ThemeDto Theme       { get; set; } = new();
}

public sealed class BannerDto
{
    public string Sha256      { get; set; } = "";
    public string ContentType { get; set; } = "";
}

/// <summary>PUT /api/sync/banner: the bytes, base64 in JSON so the type travels under the same
/// signature. <see cref="Data"/> is a byte array here; the serializer writes it as base64.</summary>
public sealed class BannerUpload
{
    public string Sha256      { get; set; } = "";
    public string ContentType { get; set; } = "";
    public byte[] Data        { get; set; } = [];
}

public sealed class AllowedDto
{
    public long   Id   { get; set; }
    /// <summary>"character", "corporation" or "alliance".</summary>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// The store's colours, resolved by the app from the theme the owner picked for the store —
/// which is a setting on the store and has nothing to do with what the owner's desktop wears.
/// </summary>
public sealed class ThemeDto
{
    /// <summary>The app's theme key, e.g. "blue-dark".</summary>
    public string Key            { get; set; } = "dark";
    /// <summary>Whether a buyer may flip to the paired light or dark variant.</summary>
    public bool   BuyerMaySwitch { get; set; }
    /// <summary>Which of the two variants is the store's own.</summary>
    public string Default        { get; set; } = "dark";
    /// <summary>"dark" and "light": token name → hex colour, the site's CSS custom properties.</summary>
    public Dictionary<string, Dictionary<string, string>> Variants { get; set; } = new();
    /// <summary>Every theme the buyer may pick from, the store's own first, each with its whole
    /// palette. A site that knows this list shows a dropdown of them; an older site uses the
    /// pair above and never sees it.</summary>
    public List<ThemeOptionDto> Themes { get; set; } = [];
}

public sealed class ThemeOptionDto
{
    public string Key  { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"dark" or "light": which the theme is built on, for the browser's colour scheme.</summary>
    public string Base { get; set; } = "dark";
    public Dictionary<string, string> Tokens { get; set; } = new();
}

public sealed class CatalogueDto
{
    /// <summary>SHA-256 of the catalogue's content, so the site can skip an unchanged write.</summary>
    public string Hash { get; set; } = "";
    public DateTimeOffset AsOf { get; set; }

    public bool ShowInStock        { get; set; } = true;
    public bool ShowInBuild        { get; set; } = true;
    public bool ShowReserved       { get; set; } = true;
    public bool ShowCompletionDate { get; set; }

    public bool   ColourByState  { get; set; }
    public string ColourInStock  { get; set; } = "";
    public string ColourInBuild  { get; set; } = "";
    public string ColourNone     { get; set; } = "";

    public List<CatalogueSectionDto> Sections { get; set; } = [];
}

public sealed class CatalogueSectionDto
{
    public string  Name         { get; set; } = "";
    public string  Prefix       { get; set; } = "";
    public string? HeaderColour { get; set; }
    public string? RowColour    { get; set; }
    public List<CatalogueItemDto> Items { get; set; } = [];
}

/// <summary>
/// A per-buyer purchase limit: so many units of each item type, each item group, or anything in
/// the store, within a rolling period or ever. The site counts the buyer's orders against it and
/// greys out what they may no longer order; the app checks again when it books.
/// </summary>
public sealed class LimitDto
{
    public int    Units  { get; set; } = 1;
    /// <summary>"type", "group" or "store".</summary>
    public string Scope  { get; set; } = "type";
    /// <summary>"days", "months", "years" or "all".</summary>
    public string Period { get; set; } = "all";
    /// <summary>How many of the period; unused for "all".</summary>
    public int    Count  { get; set; } = 1;
}

public sealed class CatalogueItemDto
{
    public int     TypeId    { get; set; }
    /// <summary>What the buyer sees: the posting's override or prefix applied.</summary>
    public string  Name      { get; set; } = "";
    /// <summary>The item's own name, for the icon and for search.</summary>
    public string  TypeName  { get; set; } = "";
    public string  GroupName { get; set; } = "";
    /// <summary>The SDE group, for a limit counted per group.</summary>
    public int     GroupId   { get; set; }

    /// <summary>Per unit, rounded exactly as the price list shows it. Null when the posting
    /// cannot price the item; the site lists it without a price and takes no order for it.</summary>
    public double? UnitPrice { get; set; }
    public long    InStock   { get; set; }
    public long    InBuild   { get; set; }
    public long    Reserved  { get; set; }
    public DateTimeOffset? EarliestJobEnd { get; set; }
    public string? Colour    { get; set; }
}

/// <summary>
/// One order line as the site should show it to its buyer: every order that carries a buyer id,
/// whatever channel placed it. Keyed by the app's own order id; the site upserts by it.
/// </summary>
public sealed class OrderDto
{
    public int     Id            { get; set; }
    public string  Ref           { get; set; } = "";
    /// <summary>The site's own id for an order placed there, or empty.</summary>
    public string  WebOrderId    { get; set; } = "";
    public long    BuyerId       { get; set; }
    /// <summary>"character" or "corporation".</summary>
    public string  BuyerType     { get; set; } = "";
    public string  BuyerName     { get; set; } = "";
    public long    ContractToId   { get; set; }
    public string  ContractToName { get; set; } = "";
    public int     TypeId        { get; set; }
    public string  TypeName      { get; set; } = "";
    /// <summary>The item's SDE group, so the site can count a per-group limit over old orders too.</summary>
    public int     GroupId       { get; set; }
    public string  GroupName     { get; set; } = "";

    public int     Units         { get; set; }
    public double  TotalPrice    { get; set; }
    /// <summary>"pending", "completed" or "canceled".</summary>
    public string  Status        { get; set; } = "";
    /// <summary>"", "stock", "job" or "contract" — where the units are coming from.</summary>
    public string  Fulfilment    { get; set; } = "";
    public string? EstimatedDate { get; set; }
    public int?    ContractId    { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CompletedOn   { get; set; }
    /// <summary>Which store took it, or zero for one entered by hand.</summary>
    public int     StoreId       { get; set; }
    /// <summary>"web", "mail" or "manual".</summary>
    public string  Channel       { get; set; } = "";
}

public sealed class WebOrderStateDto
{
    public string WebOrderId { get; set; } = "";
    /// <summary>"review" or "rejected".</summary>
    public string State      { get; set; } = "";
    public string Reason     { get; set; } = "";
}

// ── Response: what the site hands back ───────────────────────────────────────

public sealed class SyncResponse
{
    public int    Protocol      { get; set; }
    public string SiteVersion   { get; set; } = "";
    public int    SchemaVersion { get; set; }
    public string Generation    { get; set; } = "";

    /// <summary>The catalogue hash the site now holds.</summary>
    public string CatalogueHash { get; set; } = "";

    public List<int> OrdersApplied  { get; set; } = [];
    public List<int> RemovedApplied { get; set; } = [];

    /// <summary>What buyers did since the cursor, in sequence order.</summary>
    public List<SiteEventDto> Events { get; set; } = [];

    /// <summary>Signed-in sessions active in the last few minutes — the app polls faster while
    /// somebody is on the site.</summary>
    public int ActiveSessions { get; set; }

    /// <summary>The site holds no order rows, so the app resends them all: a fresh database, or
    /// one restored from before the app's ledger.</summary>
    public bool NeedsFullOrders { get; set; }

    /// <summary>The hash of the banner the site holds, "" for none; null from a site too old to
    /// know about banners, which then gets none.</summary>
    public string? BannerSha256 { get; set; }

    public DateTimeOffset ServerTime { get; set; }
}

public sealed class SiteEventDto
{
    public long   Seq  { get; set; }
    /// <summary>"order" or "cancel".</summary>
    public string Kind { get; set; } = "";
    public DateTimeOffset At { get; set; }

    public string        WebOrderId { get; set; } = "";
    public SiteBuyerDto  Buyer      { get; set; } = new();

    // An order:
    public List<SiteOrderLineDto> Lines { get; set; } = [];
    public SiteContractToDto? ContractTo { get; set; }
    public string Note { get; set; } = "";
    /// <summary>The catalogue the buyer was looking at when they ordered.</summary>
    public string CatalogueHash { get; set; } = "";
    /// <summary>The buyer's answer to the site's "keep me posted by EVE mail"; absent means yes.</summary>
    public bool?  MailUpdates { get; set; }

    // A cancellation, of an order the site knows by the app's id:
    public int?   OrderId { get; set; }
    public string Reason  { get; set; } = "";
}

public sealed class SiteBuyerDto
{
    public long   Id            { get; set; }
    public string Name          { get; set; } = "";
    public long   CorporationId { get; set; }
    public long?  AllianceId    { get; set; }
}

public sealed class SiteContractToDto
{
    public long   Id   { get; set; }
    public string Name { get; set; } = "";
    /// <summary>"character" or "corporation".</summary>
    public string Kind { get; set; } = "character";
}

public sealed class SiteOrderLineDto
{
    public int    TypeId    { get; set; }
    public long   Units     { get; set; }
    /// <summary>The unit price the site quoted, from its own copy of the catalogue.</summary>
    public double UnitPrice { get; set; }
}
