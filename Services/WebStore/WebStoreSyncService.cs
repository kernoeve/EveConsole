using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.WebStore;

/// <summary>
/// The app's half of a store's web site: pushes what the site may show, pulls what buyers did
/// there, and books what passes the checks.
///
/// <para><b>One call per store per cycle, both directions in it.</b> The request carries the
/// store, its catalogue, its allow-list and the order rows that changed since the last
/// acknowledged push; the reply carries the events buyers raised since the cursor. The site is
/// never reached any other way and never reaches the app at all — see
/// <see cref="WebStoreProtocol"/>.</para>
///
/// <para><b>Runs on the client holding the worker lease</b>, like the mail poll, so SQLite and
/// PostgreSQL installs behave the same and two clients cannot book one order twice. Polls every
/// few minutes, and every half minute while the site reports somebody signed in; nudged at once
/// by anything that changes what the site should show — the fulfilment pass, a store edit.</para>
///
/// <para><b>⚠️ What comes back is untrusted input</b>, exactly as a mail body is. An order is
/// booked only when its item is in the posting, its quantities are within the bounds the mail
/// path enforces, its buyer passes the store's sender policy, and its price is the one the app
/// pushed. Anything else is recorded in review with the reason — never booked, never silently
/// dropped — and the owner decides on the Stores screen.</para>
/// </summary>
public class WebStoreSyncService(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory              httpFactory,
    StoreCatalogueBuilder           catalogues,
    SalePostingService              postings,
    OrderFulfilmentService          fulfilment,
    OrderLabelService               labels,
    EsiClient                       esi,
    AppErrorLogger                  errorLogger)
{
    /// <summary>Minutes between calls to a site unless the store says otherwise, and the bounds
    /// an owner may set on the Stores screen. What a buyer waits, at most, for a confirmation;
    /// each call is also one of the site's free daily requests.</summary>
    public const int DefaultPollMinutes = 5, MinPollMinutes = 1, MaxPollMinutes = 1440;

    /// <summary>Between looks for a store to open while none is.</summary>
    private static readonly TimeSpan NoneOpen = TimeSpan.FromMinutes(DefaultPollMinutes);

    /// <summary>Order rows per call. Sized for a slow link; the rest follow at once.</summary>
    private const int OrdersPerPage = 300;

    /// <summary>
    /// How far a site's quoted unit price may sit from the posting's current price before the
    /// order is held for a person to look at rather than booked.
    ///
    /// <para>The price the buyer saw is honoured — that is the agreement — and the site prices
    /// from the catalogue the app pushed, never from the browser. A large difference therefore
    /// means either the posting moved a long way between push and order, or the site is not
    /// quoting what it was given; both are worth a person's eyes before a hull changes hands.</para>
    /// </summary>
    private const double PriceTolerance = 0.20;

    // ── What the background-process view shows ────────────────────────────────

    public DateTimeOffset? LastRunAt  { get; private set; }
    public DateTimeOffset? NextRunAt  { get; private set; }
    public string          StatusText { get; private set; } = DataText.NotRunYet;

    private Task? _loop;
    private CancellationTokenSource? _cts;

    /// <summary>One pass at a time, whether the loop or a Sync Now button started it.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Released by <see cref="Nudge"/> to cut the wait short.</summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>When each open store is next due, by id; one not listed is due now.</summary>
    private readonly Dictionary<int, DateTimeOffset> _due = new();

    /// <summary>Set by <see cref="Nudge"/>: the next cycle takes every store, due or not, so a
    /// changed setting reaches its site at once.</summary>
    private volatile bool _every;

    public void Start(CancellationToken outerCt = default)
    {
        if (_loop is not null) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var ct = _cts.Token;

        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                // Until the earliest store is due; a nudge cuts the wait short. A cycle that
                // failed outright waits the default rather than spinning on a stale due time.
                TimeSpan wait;
                try { await RunOnceAsync(ct); wait = UntilNextDue(); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    errorLogger.Log(nameof(WebStoreSyncService), "cycle", ex);
                    wait = NoneOpen;
                    NextRunAt = DateTimeOffset.UtcNow + wait;
                }
                try { await _wake.WaitAsync(wait, ct); }
                catch (OperationCanceledException) { return; }
            }
        }, ct);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop; }
        catch (OperationCanceledException) { }
        _loop = null;
        _cts  = null;
        NextRunAt = null;
    }

    /// <summary>Run a cycle now, every store in it, rather than when each is due.</summary>
    public void Nudge()
    {
        _every = true;
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* already awake */ }
    }

    /// <summary>Every open web store that is due, one after the other (all of them after a
    /// nudge), and when the earliest is next due.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            List<(int Id, int Minutes)> stores;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                stores = (await db.Stores.AsNoTracking()
                    .Where(s => !s.IsDeleted && s.WebEnabled && s.WebUrl != "" && s.WebSecret != "" && s.PostingId != 0)
                    .Select(s => new { s.Id, s.WebPollMinutes })
                    .ToListAsync(ct))
                    .Select(s => (s.Id, s.WebPollMinutes)).ToList();

            var every = _every;
            _every = false;

            if (stores.Count == 0)
            {
                StatusText = SalesText.WebSyncNoneOpen;
                NextRunAt  = DateTimeOffset.UtcNow + NoneOpen;
                _due.Clear();
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var synced = 0; var sessions = 0; var booked = 0; var problems = 0;
            foreach (var (id, minutes) in stores)
            {
                ct.ThrowIfCancellationRequested();
                if (!every && _due.TryGetValue(id, out var due) && due > now) continue;

                var result = await SyncOneAsync(id, ct);
                _due[id]  = DateTimeOffset.UtcNow + Interval(minutes);
                synced++;
                sessions += result.Sessions;
                booked   += result.Booked;
                if (result.Error is not null) problems++;
            }

            // A store no longer open drops off the schedule; the earliest of the rest is next.
            foreach (var gone in _due.Keys.Where(k => stores.All(s => s.Id != k)).ToList()) _due.Remove(gone);
            NextRunAt = stores.Min(s => _due.TryGetValue(s.Id, out var d) ? d : now);

            if (synced == 0) return;
            LastRunAt  = DateTimeOffset.UtcNow;
            StatusText = problems > 0
                ? string.Format(SalesText.WebSyncUnreachable, synced, problems)
                : booked > 0
                    ? string.Format(SalesText.WebSyncBooked, synced, booked)
                    : string.Format(SalesText.WebSyncSessions, synced, sessions);
        }
        finally { _gate.Release(); }
    }

    /// <summary>A store's interval, within the bounds whatever its row says.</summary>
    public static TimeSpan Interval(int minutes) =>
        TimeSpan.FromMinutes(Math.Clamp(minutes, MinPollMinutes, MaxPollMinutes));

    /// <summary>How long until the earliest store is due, a second at least, or the look-back
    /// while nothing is open.</summary>
    private TimeSpan UntilNextDue()
    {
        if (NextRunAt is not { } next) return NoneOpen;
        var wait = next - DateTimeOffset.UtcNow;
        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
    }

    /// <summary>One store, now, for the Sync Now button. Returns a line for the screen.</summary>
    public async Task<string> SyncStoreNowAsync(int storeId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var r = await SyncOneAsync(storeId, ct);
            return r.Error ?? (r.Booked > 0
                ? string.Format(SalesText.WebSyncNowBooked, r.Booked, r.Events)
                : string.Format(SalesText.WebSyncNowDone, r.Events, r.Pushed, r.Sessions));
        }
        finally { _gate.Release(); }
    }

    private sealed record Outcome(int Sessions, int Booked, int Events, int Pushed, string? Error);

    // ── One exchange ──────────────────────────────────────────────────────────

    private async Task<Outcome> SyncOneAsync(int storeId, CancellationToken ct)
    {
        // Follow-up calls happen at once when more rows wait or events were applied — an order
        // booked on this pass has a confirmation to carry back — but never without limit.
        var sessions = 0; var booked = 0; var events = 0; var pushed = 0;
        for (var pass = 0; pass < 4; pass++)
        {
            var r = await ExchangeAsync(storeId, ct);
            if (r.Error is not null) return new Outcome(sessions, booked + r.Booked, events + r.Events, pushed + r.Pushed, r.Error);

            sessions  = r.Sessions;
            booked   += r.Booked;
            events   += r.Events;
            pushed   += r.Pushed;

            if (!r.Again) break;
        }
        return new Outcome(sessions, booked, events, pushed, null);
    }

    private sealed record Exchange(int Sessions, int Booked, int Events, int Pushed, bool Again, string? Error);

    private async Task<Exchange> ExchangeAsync(int storeId, CancellationToken ct)
    {
        // The affiliation lookups a sender policy needs go to ESI; on the background lane, so a
        // buyer's order does not queue a person's click.
        using var _ = EsiClient.Background();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null) return new Exchange(0, 0, 0, 0, false, SalesText.WebSyncStoreGone);
        if (!store.WebEnabled || store.WebUrl.Length == 0 || store.WebSecret.Length == 0)
            return new Exchange(0, 0, 0, 0, false, SalesText.WebSyncNotSetUp);

        SyncRequest request;
        Dictionary<int, string> sentHashes;
        try
        {
            (request, sentHashes) = await BuildRequestAsync(db, store, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return await FailAsync(db, store,
                string.Format(SalesText.WebSyncBuildFailed, AppErrorLogger.Line("", ex).TrimStart(':', ' ')), ex, ct);
        }

        if (request.Catalogue.Sections.Count == 0)
            return await FailAsync(db, store, SalesText.WebSyncPostingEmpty, null, ct);

        var (response, error) = await PostAsync(store, request, ct);
        if (response is null)
            return await FailAsync(db, store, error ?? SalesText.WebSyncNoReply, null, ct);

        // ── A new site database: everything the ledger says was pushed, was not ──
        //
        // The ledger is cleared and the resend follows at once rather than at the next
        // interval: a site that was just recreated is a site with nothing to show.
        var resend = false;
        if (response.Generation.Length > 0 && response.Generation != store.WebGeneration)
        {
            await db.StoreWebPushes.Where(p => p.StoreId == store.Id).ExecuteDeleteAsync(ct);
            // Not on first contact: the rows this very call carried went into the generation
            // being recorded, and there is nothing older to resend.
            resend = store.WebGeneration.Length > 0;
            store.WebGeneration = response.Generation;
            store.WebCursor     = 0;
        }
        else if (response.NeedsFullOrders)
        {
            // ⚠️ Only when there is something to resend. A store with no orders at all was told
            // this on every call, resent nothing, and called straight back — four calls a cycle,
            // each rewriting rows on the site, which is how a day's D1 allowance went on nothing.
            var hadLedger = await db.StoreWebPushes.AnyAsync(p => p.StoreId == store.Id, ct);
            if (hadLedger)
            {
                await db.StoreWebPushes.Where(p => p.StoreId == store.Id).ExecuteDeleteAsync(ct);
                resend = true;
            }
        }

        // ── What the site took: acknowledge in the ledger ──
        if (response.OrdersApplied.Count > 0 || response.RemovedApplied.Count > 0)
        {
            var applied = response.OrdersApplied.Where(sentHashes.ContainsKey).ToList();
            var existing = await db.StoreWebPushes
                .Where(p => p.StoreId == store.Id && applied.Contains(p.OrderId))
                .ToDictionaryAsync(p => p.OrderId, ct);
            foreach (var id in applied)
            {
                if (existing.TryGetValue(id, out var row)) row.Hash = sentHashes[id];
                else db.StoreWebPushes.Add(new StoreWebPush { StoreId = store.Id, OrderId = id, Hash = sentHashes[id] });
            }
            var removed = response.RemovedApplied;
            if (removed.Count > 0)
                await db.StoreWebPushes.Where(p => p.StoreId == store.Id && removed.Contains(p.OrderId)).ExecuteDeleteAsync(ct);
        }

        store.WebSiteVersion = response.SiteVersion;
        store.WebLastSyncAt  = DateTimeOffset.UtcNow;
        store.WebLastError   = "";
        await db.SaveChangesAsync(ct);

        // ── The banner: its bytes go on their own call, only when the site does not hold this
        //    one. The reply says what the site has; a site too old to say gets none.
        if (response.BannerSha256 is { } siteBanner && request.Store.Banner is { } banner && siteBanner != banner.Sha256)
        {
            var problem = await PutBannerAsync(db, store, banner.Sha256, ct);
            if (problem is not null) { store.WebLastError = problem; await db.SaveChangesAsync(ct); }
        }

        // ── What buyers did ──
        var booked = 0;
        var events = 0;
        foreach (var ev in response.Events.OrderBy(e => e.Seq))
        {
            ct.ThrowIfCancellationRequested();
            if (ev.Seq <= store.WebCursor) continue;

            // ⚠️ Once. A reply repeated after a failed save must not book the order twice.
            if (await db.StoreWebEvents.AnyAsync(e => e.StoreId == store.Id && e.Seq == ev.Seq, ct))
            {
                store.WebCursor = Math.Max(store.WebCursor, ev.Seq);
                continue;
            }

            var record = new StoreWebEvent
            {
                StoreId    = store.Id,
                Seq        = ev.Seq,
                Kind       = ev.Kind,
                WebOrderId = ev.WebOrderId,
                BuyerId    = ev.Buyer.Id,
                BuyerName  = ev.Buyer.Name,
                Payload    = JsonSerializer.Serialize(ev, WebStoreProtocol.Json),
                ReceivedAt = DateTimeOffset.UtcNow,
            };

            try
            {
                var (outcome, detail, orderRef) = ev.Kind switch
                {
                    "order"  => await BookAsync(store, ev, force: false, ct),
                    "cancel" => await CancelAsync(store, ev, ct),
                    "visit"  => ("noted", VisitDetail(ev), ""),
                    _        => ("rejected", string.Format(SalesText.WebSyncUnknownEvent, ev.Kind), ""),
                };
                record.Outcome  = outcome;
                record.Detail   = detail;
                record.OrderRef = orderRef;
                if (outcome == "booked") booked++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Kept, with the payload, so the owner can book it by hand once whatever broke is
                // fixed. The cursor still moves: an event that fails the same way every time would
                // otherwise stop every event behind it.
                errorLogger.Log(nameof(WebStoreSyncService), $"store {store.Id} event {ev.Seq}", ex);
                record.Outcome = "error";
                record.Detail  = AppErrorLogger.Line(SalesText.WebSyncCouldNotApply, ex);
            }

            db.StoreWebEvents.Add(record);
            store.WebCursor = Math.Max(store.WebCursor, ev.Seq);
            await db.SaveChangesAsync(ct);
            events++;
        }

        return new Exchange(response.ActiveSessions, booked, events, request.Orders.Count,
                            Again: request.More || events > 0 || resend, Error: null);
    }

    private async Task<Exchange> FailAsync(AppDbContext db, Store store, string message, Exception? ex, CancellationToken ct)
    {
        if (ex is not null) errorLogger.Log(nameof(WebStoreSyncService), $"store {store.Id}", ex);
        store.WebLastError = message.Length > 500 ? message[..500] : message;
        await db.SaveChangesAsync(ct);
        return new Exchange(0, 0, 0, 0, false, message);
    }

    // ── The request ───────────────────────────────────────────────────────────

    /// <summary>The themes the site's picker offers, named in the store's language: its buyers
    /// read them.</summary>
    private static List<ThemeOptionDto> ThemeOptions(Store store, IEnumerable<string> offered)
    {
        using var language = LanguageScope.Use(store.Language);
        return offered.Select(WebThemes.ChoiceOf)
            .Select(t => new ThemeOptionDto { Key = t.Key, Name = t.Name, Base = t.Parent, Tokens = WebThemes.Resolve(t.Key) })
            .ToList();
    }

    private async Task<(SyncRequest Request, Dictionary<int, string> Hashes)> BuildRequestAsync(
        AppDbContext db, Store store, CancellationToken ct)
    {
        var catalogue = await catalogues.BuildAsync(store, ct) ?? new CatalogueDto { AsOf = DateTimeOffset.UtcNow };

        var posting = await db.SalePostings.AsNoTracking().FirstOrDefaultAsync(p => p.Id == store.PostingId, ct);

        var allowed = store.SenderPolicy == "Anyone"
            ? []
            : await db.StoreSenders.AsNoTracking()
                .Where(s => s.StoreId == store.Id)
                .Select(s => new AllowedDto { Id = s.EntityId, Kind = s.EntityType, Name = s.Name })
                .ToListAsync(ct);

        var choice  = WebThemes.ChoiceOf(store.WebTheme);
        var offered = WebThemes.Offered(store);
        var theme = new ThemeDto
        {
            Key            = choice.Key,
            // The pair, for a site older than the list: it may switch when the partner is offered.
            BuyerMaySwitch = offered.Contains(choice.Pair),
            Default        = choice.Parent,
            Variants =
            {
                [choice.Parent] = WebThemes.Resolve(choice.Key),
                [choice.Parent == "dark" ? "light" : "dark"] = WebThemes.Resolve(choice.Pair),
            },
            Themes = ThemeOptions(store, offered),
        };

        // ── Order rows: this store's, with a buyer id, whatever doorway placed them. Orders
        //    of other stores and orders entered by hand with no store are not the site's to show.
        var orders = await db.TrackedOrders.AsNoTracking()
            .Where(o => o.BuyerId != 0 && o.StoreId == store.Id)
            .ToListAsync(ct);

        // The names in the store's language, as its price list has them. ⚠️ A change of language
        // changes every order's hash, so the orders are all sent again once.
        var typeIds = orders.Select(o => o.TypeId).Distinct().ToList();
        var names = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);
        foreach (var (id, name) in await SdeNames.InLanguageAsync(db, store.Language, SdeNameKind.Type, typeIds.Select(t => (long)t), ct))
            names[(int)id] = name;
        // The group too, so the site can count a per-group limit over orders whose item has
        // since left the price list.
        var groups = await (
                from t in db.SdeTypes.AsNoTracking()
                join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                where typeIds.Contains(t.TypeId)
                select new { t.TypeId, t.GroupId, GroupName = g.Name })
            .ToDictionaryAsync(x => x.TypeId, x => (x.GroupId, x.GroupName), ct);
        var groupNames = await SdeNames.InLanguageAsync(db, store.Language, SdeNameKind.Group,
            groups.Values.Select(g => (long)g.GroupId), ct);
        foreach (var typeId in groups.Keys.ToList())
            if (groupNames.TryGetValue(groups[typeId].GroupId, out var groupName))
                groups[typeId] = (groups[typeId].GroupId, groupName);

        var current = new Dictionary<int, (OrderDto Dto, string Hash)>();
        foreach (var o in orders)
        {
            var dto = ToDto(o, names.GetValueOrDefault(o.TypeId, $"Type {o.TypeId}"), groups.GetValueOrDefault(o.TypeId));

            current[o.Id] = (dto, HashOf(dto));
        }

        var ledger = await db.StoreWebPushes.AsNoTracking()
            .Where(p => p.StoreId == store.Id)
            .ToDictionaryAsync(p => p.OrderId, p => p.Hash, ct);

        var changed = current
            .Where(kv => !ledger.TryGetValue(kv.Key, out var h) || h != kv.Value.Hash)
            .OrderBy(kv => kv.Key)
            .ToList();
        var page = changed.Take(OrdersPerPage).ToList();

        var removed = ledger.Keys.Where(id => !current.ContainsKey(id)).OrderBy(id => id).Take(OrdersPerPage).ToList();

        // Web orders that never became orders: the buyer is told why they are waiting.
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var held = (await db.StoreWebEvents.AsNoTracking()
                .Where(e => e.StoreId == store.Id && e.Kind == "order" && e.WebOrderId != ""
                         && (e.Outcome == "review" || e.Outcome == "rejected" || e.Outcome == "error"))
                .ToListAsync(ct))
            .Where(e => e.ReceivedAt >= since)
            .Select(e => new WebOrderStateDto
            {
                WebOrderId = e.WebOrderId,
                State      = e.Outcome == "rejected" ? "rejected" : "review",
                Reason     = e.Outcome == "rejected" ? e.Detail : "",
            })
            .ToList();

        // The banner by hash only; the bytes follow on their own call when the site lacks them.
        var banner = await db.StoreWebAssets.AsNoTracking()
            .Where(a => a.StoreId == store.Id && a.Kind == StoreWebAsset.Banner)
            .Select(a => new BannerDto { Sha256 = a.Sha256, ContentType = a.ContentType })
            .FirstOrDefaultAsync(ct);

        var request = new SyncRequest
        {
            AppVersion = AppVersion.Number,
            Cursor     = store.WebCursor,
            Generation = store.WebGeneration,
            Store = new StoreInfoDto
            {
                Name          = store.Name,
                // The language it speaks to buyers, as the code the site will word itself in.
                Language      = StoreLanguageCode(store),
                Blurb         = store.WebBlurb,
                CharacterName = store.CharacterName,
                Pickup        = "",   // nothing is guessed from the posting: the owner's blurb says where and how

                SenderPolicy  = store.SenderPolicy == "Anyone" ? "anyone" : "list",
                Allowed       = allowed,
                MailUpdates   = store.CharacterId != 0 && store.WebMailUpdates,
                Limit         = store.LimitEnabled
                    ? new LimitDto
                    {
                        Units  = Math.Max(1, store.LimitUnits),
                        Scope  = store.LimitScope,
                        Period = store.LimitPeriod,
                        Count  = Math.Max(1, store.LimitPeriodCount),
                    }
                    : null,

                Theme         = theme,
                Banner        = banner,
            },
            Catalogue = catalogue,
            Orders    = page.Select(kv => kv.Value.Dto).ToList(),
            Removed   = removed,
            WebOrders = held,
            More      = changed.Count > page.Count || removed.Count == OrdersPerPage,
            PushedOrders = ledger.Count,
        };

        return (request, page.ToDictionary(kv => kv.Key, kv => kv.Value.Hash));
    }

    private static OrderDto ToDto(TrackedOrder o, string typeName, (int GroupId, string GroupName) group) => new()

    {
        Id             = o.Id,
        Ref            = o.OrderRef,
        WebOrderId     = o.WebOrderId,
        BuyerId        = o.BuyerId,
        BuyerType      = o.BuyerType,
        BuyerName      = o.Buyer,
        ContractToId   = o.ContractToId,
        ContractToName = o.ContractToName,
        TypeId         = o.TypeId,
        TypeName       = typeName,
        GroupId        = group.GroupId,
        GroupName      = group.GroupName ?? "",
        Units          = o.Units,

        TotalPrice     = o.PurchasePrice,
        Status         = o.Status,
        Fulfilment     = o.FulfilmentSource,
        EstimatedDate  = string.IsNullOrEmpty(o.EstimatedDate) ? null : o.EstimatedDate,
        ContractId     = o.LinkedContractId,
        CreatedAt      = o.CreatedAt,
        CompletedOn    = string.IsNullOrEmpty(o.CompletedOn) ? null : o.CompletedOn,
        StoreId        = o.StoreId,
        Channel        = o.StoreId == 0 ? "manual" : o.WebOrderId.Length > 0 ? "web" : "mail",
    };

    private static string HashOf(OrderDto dto) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(dto, WebStoreProtocol.Json)))
               .ToLowerInvariant();

    /// <summary>The store's language as a code: its own, or the interface language of this client,
    /// which is the one serving it, for a store that speaks the app's.</summary>
    private static string StoreLanguageCode(Store store) =>
        string.IsNullOrEmpty(store.Language) ? Languages.Active.Code : store.Language;

    // ── The call ──────────────────────────────────────────────────────────────

    private async Task<(SyncResponse? Response, string? Error)> PostAsync(Store store, SyncRequest request, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(request, WebStoreProtocol.Json);
        var now  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (!Uri.TryCreate(store.WebUrl.TrimEnd('/') + WebStoreProtocol.SyncPath, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return (null, string.Format(SalesText.WebSyncBadAddress, store.WebUrl));

        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
            message.Headers.Add(WebStoreProtocol.TimestampHeader, now.ToString());
            message.Headers.Add(WebStoreProtocol.SignatureHeader, WebStoreSigner.Sign(store.WebSecret, now, body));

            using var response = await httpFactory.CreateClient("webstore").SendAsync(message, ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (null, SalesText.WebSyncBadSignature);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, SalesText.WebSyncNoEndpoint);
            if ((int)response.StatusCode == 409 || (int)response.StatusCode == 426)
            {
                var theirs = TryProtocol(bytes);
                return (null, theirs is { } p
                    ? ProtocolMismatch(p)
                    : SalesText.WebSyncProtocolDisagree);
            }
            if (!response.IsSuccessStatusCode)
                return (null, Why(bytes) is { } why
                    ? string.Format(SalesText.WebSyncAnsweredWhy, (int)response.StatusCode, response.ReasonPhrase, why)
                    : string.Format(SalesText.WebSyncAnswered, (int)response.StatusCode, response.ReasonPhrase));

            var parsed = JsonSerializer.Deserialize<SyncResponse>(bytes, WebStoreProtocol.Json);
            if (parsed is null) return (null, SalesText.WebSyncEmptyReply);
            if (parsed.Protocol != WebStoreProtocol.Version)
                return (null, ProtocolMismatch(parsed.Protocol));
            return (parsed, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, SalesText.WebSyncTimeout);
        }
        catch (HttpRequestException ex)
        {
            return (null, string.Format(SalesText.WebSyncCouldNotReach, ex.Message));
        }
        catch (JsonException ex)
        {
            return (null, string.Format(SalesText.WebSyncUnreadable, ex.Message));
        }
    }

    /// <summary>The site and this app speak different protocols: which of the two to update.</summary>
    private static string ProtocolMismatch(int site) =>
        string.Format(site > WebStoreProtocol.Version ? SalesText.WebSyncProtocolUpdateApp : SalesText.WebSyncProtocolUpdateSite,
                      site, WebStoreProtocol.Version);

    /// <summary>Sends the store's banner to the site. Null when it went; else why not, in words.</summary>
    private async Task<string?> PutBannerAsync(AppDbContext db, Store store, string sha256, CancellationToken ct)
    {
        var asset = await db.StoreWebAssets.AsNoTracking()
            .FirstOrDefaultAsync(a => a.StoreId == store.Id && a.Kind == StoreWebAsset.Banner, ct);
        if (asset is null || asset.Sha256 != sha256) return null;   // changed since the push was built; the next call names the new one

        var body = JsonSerializer.SerializeToUtf8Bytes(
            new BannerUpload { Sha256 = asset.Sha256, ContentType = asset.ContentType, Data = asset.Bytes }, WebStoreProtocol.Json);
        var (status, reply, failure, message) = await SendSignedAsync(store, HttpMethod.Put, WebStoreProtocol.BannerPath, body, ct);
        switch (failure)
        {
            case CallFailure.BadAddress:  return string.Format(SalesText.WebSyncBannerBadAddress, store.WebUrl);
            case CallFailure.Timeout:     return SalesText.WebSyncBannerTimeout;
            case CallFailure.Unreachable: return string.Format(SalesText.WebSyncBannerUnreachable, message);
        }
        if (status == HttpStatusCode.NotFound) return SalesText.WebSyncBannerSiteTooOld;
        if ((int)status >= 300)
            return Why(reply) is { } why
                ? string.Format(SalesText.WebSyncBannerRefusedWhy, (int)status, why)
                : string.Format(SalesText.WebSyncBannerRefused, (int)status);
        return null;
    }

    /// <summary>Why a signed call got no answer, for its caller to say in a sentence of its own.</summary>
    private enum CallFailure { None, BadAddress, Timeout, Unreachable }

    /// <summary>One signed call to the site: the sync call's own timestamp and signature headers.</summary>
    /// <returns>The answer, or why there was none — with the error's own message for <see cref="CallFailure.Unreachable"/>.</returns>
    private async Task<(HttpStatusCode Status, byte[] Body, CallFailure Failure, string Message)> SendSignedAsync(
        Store store, HttpMethod method, string path, byte[] body, CancellationToken ct)
    {
        if (!Uri.TryCreate(store.WebUrl.TrimEnd('/') + path, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return (0, [], CallFailure.BadAddress, "");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var message = new HttpRequestMessage(method, uri) { Content = content };
            message.Headers.Add(WebStoreProtocol.TimestampHeader, now.ToString());
            message.Headers.Add(WebStoreProtocol.SignatureHeader, WebStoreSigner.Sign(store.WebSecret, now, body));
            using var response = await httpFactory.CreateClient("webstore").SendAsync(message, ct);
            return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct), CallFailure.None, "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (0, [], CallFailure.Timeout, ""); }
        catch (HttpRequestException ex) { return (0, [], CallFailure.Unreachable, ex.Message); }
    }

    /// <summary>A visit in words: signed in, or back after so long away.</summary>
    private static string VisitDetail(SiteEventDto ev)
    {
        if (ev.AwayMinutes is not { } m) return SalesText.WebSyncSignedIn;
        return m >= 1440 ? Plurals.Format(SalesText.ResourceManager, nameof(SalesText.WebSyncBackAfterDaysOther), m / 1440)
             : m >= 60   ? Plurals.Format(SalesText.ResourceManager, nameof(SalesText.WebSyncBackAfterHoursOther), m / 60)
             :             Plurals.Format(SalesText.ResourceManager, nameof(SalesText.WebSyncBackAfterMinutesOther), m);
    }

    /// <summary>What the site said with an error, for the status line: its JSON "error", else the
    /// start of its text; null when it said nothing worth showing.</summary>
    private static string? Why(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                && e.GetString() is { Length: > 0 } why)
                return why;
        }
        catch (JsonException) { }
        var text = Encoding.UTF8.GetString(bytes).Trim();
        return text.Length == 0 || text.StartsWith('<') ? null : text.Length > 160 ? text[..160] + "…" : text;
    }

    private static int? TryProtocol(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.TryGetProperty("protocol", out var p) && p.TryGetInt32(out var n) ? n : null;
        }
        catch { return null; }
    }

    // ── Booking a web order ───────────────────────────────────────────────────

    /// <summary>
    /// Turns a web order into tracked orders — or says why it cannot.
    /// </summary>
    /// <param name="force">The owner has looked and said yes: the price and sender checks are
    /// skipped. The item and quantity bounds never are — those guard the arithmetic.</param>
    /// <returns>The outcome ("booked", "review" or "rejected"), why, and the order reference.</returns>
    private async Task<(string Outcome, string Detail, string OrderRef)> BookAsync(
        Store store, SiteEventDto ev, bool force, CancellationToken ct)
    {
        // In the store's language: a rejection's reason goes back to the site for the buyer to
        // read (BuildRequestAsync). What is held for review, and what was booked, is for the owner
        // on the Stores screen, and goes back to the app's language through Owner.
        using var language = LanguageScope.Use(store.Language);

        if (ev.Buyer.Id <= 0)   return Rejected(StoreText.WebNoBuyer);
        if (ev.Lines.Count == 0) return Rejected(StoreText.WebNoLines);
        if (ev.Lines.Count > StoreMailService.MaxLinesPerOrder)
            return Rejected(Plurals.Format(StoreText.ResourceManager, nameof(StoreText.WebTooManyLinesOther),
                                           ev.Lines.Count, StoreMailService.MaxLinesPerOrder));

        var view = await postings.BuildViewAsync(store.PostingId, ct);
        if (view is null) return Review(() => SalesText.WebSyncNoPosting);

        var byTypeId = view.Sections.SelectMany(s => s.Items)
            .GroupBy(i => i.TypeId).ToDictionary(g => g.Key, g => g.First());

        // The same lines, merged by item: two lines for one hull are one line for two.
        var lines = ev.Lines.GroupBy(l => l.TypeId)
            .Select(g => (TypeId: g.Key, Units: g.Sum(l => l.Units), UnitPrice: g.First().UnitPrice))
            .ToList();

        foreach (var (typeId, units, unitPrice) in lines)
        {
            if (!byTypeId.ContainsKey(typeId)) return Rejected(string.Format(StoreText.WebNotOnPriceList, typeId));
            if (units <= 0)
                return Rejected(Plurals.Format(StoreText.ResourceManager, nameof(StoreText.WebUnitsAskedOther), units));
            if (units > StoreMailService.MaxUnitsPerLine)
                return Rejected(Plurals.Format(StoreText.ResourceManager, nameof(StoreText.WebTooManyUnitsOther),
                                               units, StoreMailService.MaxUnitsPerLine));
            if (unitPrice <= 0 && !force)
                return Review(() => string.Format(SalesText.WebSyncNoLinePrice, byTypeId[typeId].TypeName));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!force && !await StoreSenderPolicy.IsAllowedAsync(db, esi, store, ev.Buyer.Id, ct))
            return Review(() => string.Format(SalesText.WebSyncNotOnList, ev.Buyer.Name));

        // ⚠️ The price the buyer saw is honoured, and checked. The site prices from the catalogue
        // the app pushed, so a quote far from the posting's current price means the posting moved
        // a long way in between, or the site is not quoting what it was given. Either way a
        // person looks before a hull changes hands.
        if (!force)
            foreach (var (typeId, _, unitPrice) in lines)
            {
                var item = byTypeId[typeId];
                if (item.SalePrice is not { } sale)
                    return Review(() => string.Format(SalesText.WebSyncNoPostingPrice, item.TypeName));
                var shown = MarketFmt.RoundToDisplay(sale);
                if (shown > 0 && Math.Abs(unitPrice - shown) / shown > PriceTolerance)
                    return Review(() => string.Format(SalesText.WebSyncPriceMoved,
                        unitPrice, item.TypeName, shown, (unitPrice - shown) / shown * 100));
            }

        // ⚠️ The store's purchase limit, checked here as well as on the site: the site greys out
        // what a buyer may no longer order, but the app holds the whole history and does the
        // booking. Over the limit is a matter for the owner, not a refusal.
        if (!force && store.LimitEnabled
            && await PurchaseLimit.OverLimitAsync(db, store, ev.Buyer.Id, lines.Select(l => (l.TypeId, (long)l.Units)).ToList(), ct)
                is { } over)
            return Review(() => over.Words(byTypeId[over.TypeId].TypeName));

        var reference = await OrderReference.NewAsync(db, ct);
        var now       = DateTimeOffset.UtcNow;

        var toId   = ev.ContractTo?.Id   is > 0 and var id ? id : ev.Buyer.Id;
        var toName = ev.ContractTo?.Name is { Length: > 0 } n ? n : ev.Buyer.Name;
        var toKind = ev.ContractTo?.Kind is { Length: > 0 } k ? k : "character";

        var created = new List<TrackedOrder>();
        foreach (var (typeId, units, unitPrice) in lines)
        {
            var priced = unitPrice > 0
                ? unitPrice
                : byTypeId[typeId].SalePrice ?? 0;

            created.Add(new TrackedOrder
            {
                TypeId         = typeId,
                Units          = (int)units,
                Buyer          = ev.Buyer.Name,
                BuyerId        = ev.Buyer.Id,
                BuyerType      = "character",
                // Unit rounded, then multiplied — the same arithmetic the mail path uses, so the
                // total is what the lines add up to.
                PurchasePrice  = MarketFmt.RoundToDisplay(priced) * units,
                Status         = "pending",
                StoreId        = store.Id,
                OrderRef       = reference,
                WebOrderId     = ev.WebOrderId,
                MailUpdates    = ev.MailUpdates ?? true,
                NotifiedState  = "pending||",

                ContractToId   = toId,
                ContractToName = toName,
                ContractToType = toKind,
                CreatedAt      = now,
            });
        }
        db.TrackedOrders.AddRange(created);
        await db.SaveChangesAsync(ct);

        await labels.ApplyStoreLabelsAsync(db, store.OrderLabels, created.Select(o => o.Id), ct);
        db.ChangeTracker.Clear();

        // ⚠️ Worked out now rather than at the next five-minute pass, so the confirmation the
        // next cycle carries back says stock, build or waiting rather than nothing yet. In the
        // app's language, not the store's: the pass is the app's, and its status and whatever it
        // sets off are read by the owner.
        try { using (LanguageScope.App()) await fulfilment.RunOnceAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errorLogger.Log(nameof(WebStoreSyncService), "fulfilment after order", ex); }

        var settled = await db.TrackedOrders.Where(o => o.OrderRef == reference).ToListAsync(ct);
        StoreMailService.AutoEstimate(store, settled);
        foreach (var o in settled) o.NotifiedState = StoreMailService.StateOf(o);
        await db.SaveChangesAsync(ct);

        return ("booked", Owner(() => Plurals.Format(SalesText.ResourceManager, nameof(SalesText.WebSyncBookedLinesOther),
                                                     created.Count, created.Sum(o => o.PurchasePrice))), reference);
    }

    /// <summary>A web order turned down: its reason goes to the site, for the buyer, in the store's
    /// language (the scope BookAsync writes in).</summary>
    private static (string Outcome, string Detail, string OrderRef) Rejected(string reason) => ("rejected", reason, "");

    /// <summary>A web order held for the owner, with why, in the app's language.</summary>
    private static (string Outcome, string Detail, string OrderRef) Review(Func<string> why) => ("review", Owner(why), "");

    /// <summary>Text for the store's owner — a held order's reason, a booked order's summary — in
    /// the app's own language, while the store is writing to its buyers in theirs.</summary>
    private static string Owner(Func<string> text)
    {
        using var _ = LanguageScope.App();
        return text();
    }

    // ── Cancelling from the site ──────────────────────────────────────────────

    private async Task<(string Outcome, string Detail, string OrderRef)> CancelAsync(
        Store store, SiteEventDto ev, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // By the app's id where the site knows it, otherwise by the site's own id — an order
        // cancelled before the app ever confirmed it.
        var orders = ev.OrderId is { } appId
            ? await db.TrackedOrders.Where(o => o.Id == appId).ToListAsync(ct)
            : ev.WebOrderId.Length > 0
                ? await db.TrackedOrders.Where(o => o.WebOrderId == ev.WebOrderId).ToListAsync(ct)
                : [];

        if (orders.Count == 0)
        {
            // An order still in review, or one that was never booked: nothing to cancel, and the
            // review row is closed so the buyer is not left waiting on it.
            var pending = await db.StoreWebEvents
                .Where(e => e.StoreId == store.Id && e.Kind == "order" && e.WebOrderId == ev.WebOrderId && e.WebOrderId != ""
                         && (e.Outcome == "review" || e.Outcome == "error"))
                .ToListAsync(ct);
            if (pending.Count > 0)
            {
                // The reason the site shows the buyer, in the store's language. Everything else
                // here is the owner's, and stays in the app's.
                using (LanguageScope.Use(store.Language))
                    foreach (var p in pending) { p.Outcome = "rejected"; p.Detail = StoreText.WebWithdrawn; }
                await db.SaveChangesAsync(ct);
                return ("applied", SalesText.WebSyncWithdrawn, "");
            }
            return ("rejected", SalesText.WebSyncNoOrderMatches, "");
        }

        // ⚠️ The buyer's own, or their corporation's. A cancellation is the one thing a buyer can
        // do to an order, and the check is the same one the mail path makes on a CANCEL.
        var mine = orders.Where(o =>
                o.BuyerId == ev.Buyer.Id
             || (o.BuyerType == "corporation" && o.BuyerId == ev.Buyer.CorporationId))
            .ToList();
        if (mine.Count == 0) return ("rejected", SalesText.WebSyncNotTheirs, "");

        var open = mine.Where(o => o.Status == "pending").ToList();
        if (open.Count == 0) return ("rejected", SalesText.WebSyncNothingOpen, mine[0].OrderRef);

        // Only a contract still waiting on the buyer has to be withdrawn; one already accepted
        // for part of the order is a delivery made.
        var contracted = open.Where(OrderContractLinks.AwaitsAcceptance).ToList();
        foreach (var o in open)
        {
            o.Status        = "canceled";
            o.CompletedOn   = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            o.NotifiedState = StoreMailService.StateOf(o);
        }
        await db.SaveChangesAsync(ct);

        // Frees whatever the order had reserved, so the next push shows it available again.
        try { await fulfilment.RunOnceAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errorLogger.Log(nameof(WebStoreSyncService), "fulfilment after cancel", ex); }

        var detail = contracted.Count > 0
            ? Plurals.Format(SalesText.ResourceManager, nameof(SalesText.WebSyncCancelledContractOther),
                             open.Count, string.Join(", ", contracted.Select(o => o.LinkedContractId)))
            : ev.Reason.Length > 0
                ? Plurals.Format(SalesText.ResourceManager, nameof(SalesText.WebSyncCancelledReasonOther), open.Count, ev.Reason)
                : Plurals.Format(SalesText.ResourceManager, nameof(SalesText.WebSyncCancelledOther), open.Count);

        return ("applied", detail, mine[0].OrderRef);
    }

    // ── The owner's decision on a held order ──────────────────────────────────

    /// <summary>Books a held web order as it was placed, checks the owner has now made in person.</summary>
    public async Task<string> ApproveAsync(int eventId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.StoreWebEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (record is null) return SalesText.WebSyncEventGone;
        if (record.Kind != "order") return SalesText.WebSyncOnlyOrders;
        if (record.Outcome is "booked") return SalesText.WebSyncAlreadyBooked;

        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == record.StoreId, ct);
        if (store is null) return SalesText.WebSyncStoreGone;

        var ev = JsonSerializer.Deserialize<SiteEventDto>(record.Payload, WebStoreProtocol.Json);
        if (ev is null) return SalesText.WebSyncEventUnreadable;

        // A reason it is still turned down for is the buyer's, in the store's language: BookAsync
        // writes it in that. The rest is the owner's.
        var (outcome, detail, orderRef) = await BookAsync(store, ev, force: true, ct);
        record.Outcome  = outcome;
        record.Detail   = outcome == "booked" ? string.Format(SalesText.WebSyncApproved, detail) : detail;
        record.OrderRef = orderRef;
        await db.SaveChangesAsync(ct);

        // The order the owner just approved is matched against stock, jobs and contracts now.
        // The site hears of the decision at once; what the pass works out goes out with the
        // store's next call, within its interval.
        if (outcome == "booked") fulfilment.Nudge();
        Nudge();
        return outcome == "booked" ? string.Format(SalesText.WebSyncBookedAs, orderRef) : detail;
    }

    /// <summary>Turns a held web order down; the buyer sees the reason on the site.</summary>
    /// <param name="reason">The owner's own words, sent as they are; none for the stock reason,
    /// which is worded here in the store's language.</param>
    public async Task<string> RejectAsync(int eventId, string? reason = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.StoreWebEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (record is null) return SalesText.WebSyncEventGone;
        if (record.Outcome is "booked") return SalesText.WebSyncRejectBooked;

        record.Outcome = "rejected";
        var own = reason?.Trim() ?? "";
        if (own.Length > 0) record.Detail = own;
        else
        {
            // The buyer reads it on the site: in the store's language, not the owner's.
            var language = await db.Stores.AsNoTracking().Where(s => s.Id == record.StoreId)
                                   .Select(s => s.Language).FirstOrDefaultAsync(ct);
            using (LanguageScope.Use(language)) record.Detail = StoreText.WebDeclined;
        }
        await db.SaveChangesAsync(ct);

        Nudge();
        return SalesText.WebSyncDeclined;
    }
}
