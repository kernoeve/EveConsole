using System.Text;
using System.Text.RegularExpressions;
using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// The shop's correspondence: reads mail sent to a store's character, does what it says, and
/// replies.
///
/// <para><b>Why this exists.</b> A sale posting only tells buyers what the prices were when the
/// seller last posted it. Everything after that — is it still in stock, what does it cost now,
/// can I order two — was a question asked in chat and answered by hand, several times a day. The
/// answers are all already in this database. This lets a buyer ask for them directly.</para>
///
/// <para><b>⚠️ Nothing is answered until the shop is switched on, and never retrospectively.</b>
/// A character's inbox holds months of unrelated mail, and a shop that replied to all of it on
/// first run would send hundreds of messages that cannot be recalled. Only mail that arrived
/// after <see cref="Store.ListenFrom"/> is considered, and that mark is moved forward every time
/// a store is enabled — so switching a shop off for a week and back on does not make it answer
/// the week it missed.</para>
///
/// <para><b>Every mail is recorded, answered or not.</b> Rejections and things it could not
/// parse create no order and would otherwise vanish, taking with them the only evidence of why a
/// buyer got no reply.</para>
/// </summary>
public class StoreMailService(
    IDbContextFactory<AppDbContext> dbFactory,
    EveMailService                  mail,
    EsiClient                       esi,
    SalePostingService              postings,
    AppErrorLogger                  errorLogger)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// ⚠️ A ceiling on replies per pass, per store. Not a performance guard — a runaway guard.
    /// Every reply is a real mail to a real person, and a parser bug or a duplicated inbox could
    /// otherwise send hundreds before anyone noticed. What is skipped is not lost: it stays
    /// unprocessed and is picked up next pass, a minute later.
    /// </summary>
    private const int MaxRepliesPerPass = 10;

    private Task? _loop;

    // ── What the background-process view shows ────────────────────────────────
    public DateTimeOffset? LastRunAt  { get; private set; }
    public DateTimeOffset? NextRunAt  { get; private set; }
    public string          StatusText { get; private set; } = "Not run yet";

    public void Start(CancellationToken ct = default)
    {
        if (_loop is not null) return;

        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await RunOnceAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    StatusText = $"Last pass failed: {ex.Message}";
                    errorLogger.Log(nameof(StoreMailService), "poll", ex);
                }

                LastRunAt = DateTimeOffset.UtcNow;
                NextRunAt = LastRunAt + Interval;

                try { await Task.Delay(Interval, ct); }
                catch (OperationCanceledException) { return; }
            }
        }, ct);
    }

    /// <summary>One pass over every open shop. Public so the Stores tool can force one.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var stores = await db.Stores
            .Where(s => s.Enabled && s.CharacterId != 0 && s.PostingId != 0)
            .ToListAsync(ct);

        if (stores.Count == 0) { StatusText = "No open stores."; return; }

        var handled = 0;
        var told    = 0;
        foreach (var store in stores)
        {
            ct.ThrowIfCancellationRequested();
            handled += await ServeAsync(db, store, ct);
            told    += await NotifyAsync(db, store, ct);
        }

        StatusText = handled == 0 && told == 0
            ? $"{stores.Count} store(s) open, nothing new."
            : $"{handled} message(s) handled, {told} update(s) sent.";
    }

    // ── Telling the buyer when something changed ──────────────────────────────

    /// <summary>
    /// What an order line's state is, as one string, for comparison against what was last sent.
    /// </summary>
    private static string StateOf(TrackedOrder o) =>
        $"{o.Status}|{o.FulfilmentSource}|{o.EstimatedDate}";

    /// <summary>
    /// Mails the buyer about lines that have moved since they were last told.
    ///
    /// <para>This is the half of the feature the buyer did not ask for and gets anyway: an order
    /// placed for something out of stock sits there until a job is started for it, at which point
    /// OrderFulfilmentService attaches the job and works out a date. Nobody has to remember to
    /// pass that on.</para>
    ///
    /// <para><b>⚠️ One mail per order, not per line.</b> An order of six things whose job all
    /// started together would otherwise be six mails in one minute.</para>
    /// </summary>
    private async Task<int> NotifyAsync(AppDbContext db, Store store, CancellationToken ct)
    {
        var orders = await db.TrackedOrders
            .Where(o => o.StoreId == store.Id && o.OrderRef != "" && o.BuyerId != 0)
            .ToListAsync(ct);
        if (orders.Count == 0) return 0;

        var changed = orders.Where(o => StateOf(o) != o.NotifiedState).ToList();
        if (changed.Count == 0) return 0;

        var names = await TypeNamesAsync(db, changed.Select(o => o.TypeId).Distinct().ToList(), ct);

        var sent = 0;
        foreach (var group in changed.GroupBy(o => (o.OrderRef, o.BuyerId, o.Buyer))
                                     .Take(MaxRepliesPerPass))
        {
            var lines = group.ToList();

            var sb = new StringBuilder();
            sb.Append("Order <b>").Append(group.Key.OrderRef).Append("</b> — update<br><br>");
            foreach (var o in lines)
                sb.Append(o.Units.ToString("N0")).Append(" &#215; ")
                  .Append("<a href=\"showinfo:").Append(o.TypeId).Append("\">")
                  .Append(Esc(names.GetValueOrDefault(o.TypeId, $"Type {o.TypeId}"))).Append("</a>")
                  .Append(" — ").Append(Describe(o)).Append("<br>");

            var log = new StoreMail
            {
                StoreId   = store.Id,
                Direction = "out",
                PartyId   = group.Key.BuyerId,
                PartyName = group.Key.Buyer,
                Command   = "UPDATE",
                OrderRef  = group.Key.OrderRef,
            };

            var (ok, error) = await mail.SendMailAsync(
                store.CharacterId,
                Trim($"{store.Name} — order {group.Key.OrderRef}"),
                sb.ToString(),
                [new EsiMailRecipientItem(group.Key.BuyerId, "character")], ct);

            log.Subject = $"{store.Name} — order {group.Key.OrderRef}";
            log.Body    = sb.ToString();
            log.Outcome = ok ? "ok" : "error";
            log.Detail  = ok ? "" : $"Update failed: {error}";
            log.At      = DateTimeOffset.UtcNow;
            db.StoreMails.Add(log);

            // ⚠️ Only marked as told when the mail actually went. A failed send that still moved
            // the marker would lose the update permanently — nothing would ever notice again,
            // because the comparison is against this field.
            if (ok)
            {
                foreach (var o in lines) o.NotifiedState = StateOf(o);
                sent++;
            }

            await db.SaveChangesAsync(ct);
        }

        db.ChangeTracker.Clear();
        return sent;
    }

    // ── One store ─────────────────────────────────────────────────────────────

    private async Task<int> ServeAsync(AppDbContext db, Store store, CancellationToken ct)
    {
        // The inbox as the ordinary mail poll left it. Deliberately not a fetch of its own: the
        // shop reads the same headers every other part of the app does, so a mail cannot be seen
        // here and be missing from the Eve Mail tool.
        var incoming = await db.EsiMailHeaders
            .AsNoTracking()
            .Where(h => h.CharacterId == store.CharacterId && h.FromId != store.CharacterId)
            .Select(h => new { h.MailId, h.FromId, h.FromName, h.Subject, h.Timestamp })
            .ToListAsync(ct);

        // ⚠️ In memory: EF cannot translate a DateTimeOffset comparison on SQLite, and doing it
        // in the query throws at runtime rather than failing to compile.
        var fresh = incoming
            .Where(h => h.Timestamp >= store.ListenFrom)
            .OrderBy(h => h.Timestamp)
            .ToList();
        if (fresh.Count == 0) return 0;

        var seen = await db.StoreMails
            .Where(m => m.StoreId == store.Id && m.Direction == "in")
            .Select(m => m.MailId)
            .ToListAsync(ct);
        var seenSet = seen.ToHashSet();

        var todo = fresh.Where(h => !seenSet.Contains(h.MailId)).Take(MaxRepliesPerPass).ToList();
        if (todo.Count == 0) return 0;

        var handled = 0;
        foreach (var header in todo)
        {
            ct.ThrowIfCancellationRequested();

            var body = "";
            try { body = await mail.GetBodyAsync(store.CharacterId, header.MailId, ct); }
            catch { /* an unreadable body is still a mail we must record and not retry forever */ }

            var log = new StoreMail
            {
                StoreId   = store.Id,
                Direction = "in",
                MailId    = header.MailId,
                PartyId   = header.FromId,
                PartyName = header.FromName,
                Subject   = header.Subject ?? "",
                Body      = body ?? "",
                At        = header.Timestamp,
            };

            try
            {
                await HandleAsync(db, store, log, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.Outcome = "error";
                log.Detail  = ex.Message;
                errorLogger.Log(nameof(StoreMailService), $"store {store.Id} mail {header.MailId}", ex);
            }

            // ⚠️ Recorded whatever happened, including a failure. Without the row the next pass
            // reads the same mail again, and a mail that fails halfway — reply sent, order not
            // written — would be replied to over and over.
            db.StoreMails.Add(log);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            handled++;
        }

        return handled;
    }

    /// <summary>Works out what one mail asked for and does it, filling in the log row.</summary>
    private async Task HandleAsync(AppDbContext db, Store store, StoreMail log, CancellationToken ct)
    {
        var command = ParseCommand(log.Subject);
        log.Command = command;

        if (command.Length == 0)
        {
            // ⚠️ Silent. Anyone can mail this character, and most of what arrives will be
            // ordinary correspondence — replying "I did not understand" to every one of those
            // would turn the shop into a bot that argues with people. It is recorded so the
            // owner can see what was ignored and answer it themselves.
            log.Outcome = "unknown";
            log.Detail  = "No command keyword in the subject — left for a person.";
            return;
        }

        if (!await IsAllowedAsync(db, store, log.PartyId, ct))
        {
            log.Outcome = "rejected";
            log.Detail  = "Sender is not on this store's list.";
            return;
        }

        switch (command)
        {
            case "PRICES": await PricesAsync(store, log, ct); break;
            case "ORDER":  await OrderAsync(db, store, log, ct); break;
            case "STATUS": await StatusAsync(db, store, log, ct); break;
            case "CANCEL": await CancelAsync(db, store, log, ct); break;
        }
    }

    // ── Who may be served ─────────────────────────────────────────────────────

    /// <summary>
    /// Whether this sender may be served.
    ///
    /// <para>⚠️ Affiliation is fetched fresh rather than read from
    /// <c>CharacterAffiliations</c>. That cache never expires by design — for reading old intel
    /// the corp somebody used to be in is as good as the one they are in now — but this is an
    /// authorisation decision, and a stale row would serve someone who left the corporation
    /// months ago, or refuse someone who just joined.</para>
    /// </summary>
    private async Task<bool> IsAllowedAsync(AppDbContext db, Store store, long senderId, CancellationToken ct)
    {
        if (store.SenderPolicy == "Anyone") return true;

        var allowed = await db.StoreSenders
            .Where(s => s.StoreId == store.Id)
            .Select(s => new { s.EntityId, s.EntityType })
            .ToListAsync(ct);
        if (allowed.Count == 0) return false;

        if (allowed.Any(a => a.EntityType == "character" && a.EntityId == senderId)) return true;

        var needsOrg = allowed.Any(a => a.EntityType is "corporation" or "alliance");
        if (!needsOrg) return false;

        var affiliation = await esi.GetAffiliationsAsync([senderId], ct);
        if (affiliation.Count == 0) return false;   // unknown is not permission

        var (_, corpId, allianceId) = affiliation[0];

        return allowed.Any(a =>
            (a.EntityType == "corporation" && a.EntityId == corpId) ||
            (a.EntityType == "alliance"    && allianceId is { } al && a.EntityId == al));
    }

    // ── PRICES ────────────────────────────────────────────────────────────────

    private async Task PricesAsync(Store store, StoreMail log, CancellationToken ct)
    {
        var blocks = await postings.RenderAsync(store.PostingId, "EVE Mail", ct);
        if (blocks.Count == 0)
        {
            log.Outcome = "error";
            log.Detail  = "The store's posting produced nothing — check it has post blocks.";
            return;
        }

        // Every block, in order, in one mail. They exist as separate posts because Slack wants a
        // parent message with detail in a thread; EVE mail has no threading, so three mails would
        // arrive at once with nothing to relate them.
        var body = string.Join("<br><br>", blocks.Select(b => b.Text).Where(t => t.Length > 0));

        await ReplyAsync(store, log, $"{store.Name} — price list", body, ct);
    }

    // ── ORDER ─────────────────────────────────────────────────────────────────

    private async Task OrderAsync(AppDbContext db, Store store, StoreMail log, CancellationToken ct)
    {
        var view = await postings.BuildViewAsync(store.PostingId, ct);
        if (view is null)
        {
            log.Outcome = "error";
            log.Detail  = "The store has no posting to price against.";
            return;
        }

        // The catalogue is the posting: what it quotes is what can be bought. A name override is
        // matched as well as the real type name, because the override is what the buyer was
        // shown and so what they will write back.
        var catalogue = new Dictionary<string, PostingItemView>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in view.Sections.SelectMany(s => s.Items))
        {
            catalogue[item.TypeName] = item;
            if (!string.IsNullOrWhiteSpace(item.NameOverride)) catalogue[item.NameOverride!] = item;
        }

        var (lines, unknown) = ParseOrderLines(log.Body, catalogue);

        if (lines.Count == 0)
        {
            log.Outcome = "rejected";
            log.Detail  = unknown.Count > 0
                ? $"Nothing recognised. Unmatched: {string.Join(", ", unknown)}"
                : "No order lines found in the body.";
            await ReplyAsync(store, log, $"{store.Name} — order not understood",
                "Nothing on this order matched the price list.<br><br>" +
                "One item per line, as <b>quantity x item name</b> — for example:<br>" +
                "2 x Hulk<br>1 x Orca<br><br>" +
                (unknown.Count > 0
                    ? "Not found: " + Esc(string.Join(", ", unknown)) + "<br><br>"
                    : "") +
                "Reply with <b>PRICES</b> in the subject for the current list.", ct);
            return;
        }

        var reference = await NewReferenceAsync(db, ct);
        var now       = DateTimeOffset.UtcNow;

        foreach (var (item, units) in lines)
            db.TrackedOrders.Add(new TrackedOrder
            {
                TypeId    = item.TypeId,
                Units     = (int)units,
                Buyer     = log.PartyName,
                BuyerId   = log.PartyId,
                BuyerType = "character",
                // ⚠️ The price quoted, times the units — not a price looked up when the order is
                // filled. The buyer agreed to what the list said at the moment they wrote, and
                // the list moves with the market.
                PurchasePrice = (item.SalePrice ?? 0) * units,
                Status        = "pending",
                StoreId       = store.Id,
                OrderRef      = reference,
                // The confirmation below IS the first notification, so the order starts already
                // told. Without this every new order would trigger an update mail a minute later
                // saying exactly what the confirmation just said.
                NotifiedState = "pending||",
                CreatedAt     = now,
            });

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        log.OrderRef = reference;

        var total = lines.Sum(l => (l.Item.SalePrice ?? 0) * l.Units);
        var sb = new StringBuilder();
        sb.Append("Order <b>").Append(reference).Append("</b> received.<br><br>");
        foreach (var (item, units) in lines)
            sb.Append(units.ToString("N0")).Append(" &#215; ")
              .Append(Link(item)).Append(" — ")
              .Append(Isk((item.SalePrice ?? 0) * units)).Append("<br>");
        sb.Append("<br><b>Total ").Append(Isk(total)).Append("</b><br><br>");

        if (unknown.Count > 0)
            sb.Append("Not on the list, so not ordered: ").Append(Esc(string.Join(", ", unknown)))
              .Append("<br><br>");

        sb.Append("Reply with <b>STATUS</b> and this reference for progress, ")
          .Append("or <b>CANCEL</b> to withdraw it.");

        await ReplyAsync(store, log, $"{store.Name} — order {reference}", sb.ToString(), ct);
    }

    // ── STATUS ────────────────────────────────────────────────────────────────

    private async Task StatusAsync(AppDbContext db, Store store, StoreMail log, CancellationToken ct)
    {
        var orders = await FindOrderAsync(db, store, log, ct);
        if (orders.Count == 0)
        {
            log.Outcome = "rejected";
            log.Detail  = "No order of that reference for this sender.";
            await ReplyAsync(store, log, $"{store.Name} — order not found",
                "No order of that reference was found against your name.<br><br>" +
                "Put the reference in the subject or the body — for example: " +
                "<b>STATUS K7P2QX</b>.", ct);
            return;
        }

        log.OrderRef = orders[0].OrderRef;

        var names = await TypeNamesAsync(db, orders.Select(o => o.TypeId).Distinct().ToList(), ct);

        var sb = new StringBuilder();
        sb.Append("Order <b>").Append(orders[0].OrderRef).Append("</b><br><br>");
        foreach (var o in orders)
        {
            sb.Append(o.Units.ToString("N0")).Append(" &#215; ")
              .Append("<a href=\"showinfo:").Append(o.TypeId).Append("\">")
              .Append(Esc(names.GetValueOrDefault(o.TypeId, $"Type {o.TypeId}"))).Append("</a>")
              .Append(" — ").Append(Describe(o)).Append("<br>");
        }

        await ReplyAsync(store, log, $"{store.Name} — order {orders[0].OrderRef}", sb.ToString(), ct);
    }

    /// <summary>What one line of an order is doing, in words a buyer can act on.</summary>
    private static string Describe(TrackedOrder o) => o.Status switch
    {
        "completed" => "delivered",
        "canceled"  => "cancelled",
        _ => o.EstimatedDate is { Length: > 0 } d
                ? o.FulfilmentSource switch
                {
                    "stock" => $"ready now (expected {d})",
                    "job"   => $"in production, expected {d}",
                    _       => $"expected {d}",
                }
                : o.FulfilmentSource == "stock" ? "ready now" : "waiting on materials",
    };

    // ── CANCEL ────────────────────────────────────────────────────────────────

    private async Task CancelAsync(AppDbContext db, Store store, StoreMail log, CancellationToken ct)
    {
        var orders = await FindOrderAsync(db, store, log, ct);
        if (orders.Count == 0)
        {
            log.Outcome = "rejected";
            log.Detail  = "No order of that reference for this sender.";
            await ReplyAsync(store, log, $"{store.Name} — order not found",
                "No order of that reference was found against your name.", ct);
            return;
        }

        log.OrderRef = orders[0].OrderRef;

        // ⚠️ Only what is still pending. A line already delivered is not cancellable by mail —
        // the goods have moved — and quietly marking it cancelled would erase the sale.
        var open = orders.Where(o => o.Status == "pending").ToList();
        var kept = orders.Count - open.Count;

        foreach (var o in open)
        {
            o.Status      = "canceled";
            o.CompletedOn = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        }

        if (open.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        var sb = new StringBuilder();
        sb.Append("Order <b>").Append(orders[0].OrderRef).Append("</b><br><br>");
        sb.Append(open.Count > 0
            ? $"{open.Count} line(s) cancelled.<br>"
            : "Nothing left to cancel.<br>");
        if (kept > 0)
            sb.Append(kept).Append(" line(s) were already delivered or cancelled and were left as they are.<br>");

        await ReplyAsync(store, log, $"{store.Name} — order {orders[0].OrderRef} cancelled", sb.ToString(), ct);
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The order a mail is about: its reference, and only if it belongs to this sender.
    ///
    /// <para>⚠️ Matched on the buyer as well as the reference. References are short enough to
    /// guess, and without this a stranger could read or cancel somebody else's order.</para>
    /// </summary>
    private static async Task<List<TrackedOrder>> FindOrderAsync(
        AppDbContext db, Store store, StoreMail log, CancellationToken ct)
    {
        var reference = FindReference(log.Subject) ?? FindReference(log.Body);
        if (reference is null) return [];

        return await db.TrackedOrders
            .Where(o => o.StoreId == store.Id
                     && o.OrderRef == reference
                     && o.BuyerId == log.PartyId)
            .ToListAsync(ct);
    }

    private async Task ReplyAsync(Store store, StoreMail log, string subject, string body, CancellationToken ct)
    {
        var (ok, error) = await mail.SendMailAsync(
            store.CharacterId, Trim(subject), body,
            [new EsiMailRecipientItem(log.PartyId, "character")], ct);

        if (!ok)
        {
            log.Outcome = "error";
            log.Detail  = $"Reply failed: {error}";
            return;
        }

        if (log.Outcome.Length == 0) log.Outcome = "ok";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.StoreMails.Add(new StoreMail
        {
            StoreId   = store.Id,
            Direction = "out",
            PartyId   = log.PartyId,
            PartyName = log.PartyName,
            Subject   = subject,
            Body      = body,
            Command   = log.Command,
            Outcome   = "ok",
            OrderRef  = log.OrderRef,
            At        = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Dictionary<int, string>> TypeNamesAsync(
        AppDbContext db, List<int> typeIds, CancellationToken ct) =>
        await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

    // ── Parsing ───────────────────────────────────────────────────────────────

    /// <summary>Leading Re:/Fwd: on a reply, in the forms EVE and its players produce.</summary>
    private static readonly Regex ReplyPrefix =
        new(@"^\s*(re|fw|fwd)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The command keyword, from the subject.
    ///
    /// <para>The subject rather than the body because a body is quoted, greeted and signed, and
    /// any of that can contain the word "order". A subject is short, deliberate, and survives a
    /// reply intact — which is what lets a buyer answer our own mail and be understood.</para>
    /// </summary>
    internal static string ParseCommand(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "";

        var s = subject;
        // Repeatedly, because "Re: Fwd: Re: ORDER" is a real subject line.
        for (var i = 0; i < 5; i++)
        {
            var stripped = ReplyPrefix.Replace(s, "");
            if (stripped == s) break;
            s = stripped;
        }

        var word = s.Trim().Split([' ', '\t', ':', '-', ','], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.ToUpperInvariant() ?? "";

        return word is "PRICES" or "PRICE" or "LIST" ? "PRICES"
             : word is "ORDER" or "BUY"              ? "ORDER"
             : word is "STATUS"                      ? "STATUS"
             : word is "CANCEL"                      ? "CANCEL"
             : "";
    }

    /// <summary>Reference characters, chosen so nothing in them can be misread when typed back:
    /// no O against 0, no I or 1, no S against 5.</summary>
    private const string RefAlphabet = "ABCDEFGHJKLMNPQRTUVWXYZ2346789";

    private static readonly Regex RefPattern =
        new(@"\b([ABCDEFGHJKLMNPQRTUVWXYZ2346789]{6})\b", RegexOptions.Compiled);

    internal static string? FindReference(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null
        : RefPattern.Match(Strip(text)) is { Success: true } m ? m.Groups[1].Value : null;

    private async Task<string> NewReferenceAsync(AppDbContext db, CancellationToken ct)
    {
        var used = await db.TrackedOrders.Where(o => o.OrderRef != "")
            .Select(o => o.OrderRef).Distinct().ToListAsync(ct);
        var taken = used.ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = string.Concat(Enumerable.Range(0, 6)
                .Select(_ => RefAlphabet[Random.Shared.Next(RefAlphabet.Length)]));
            if (taken.Add(candidate)) return candidate;
        }

        // Every attempt collided, which at thirty characters to the sixth power means something
        // is wrong rather than unlucky. A timestamp is ugly but unique and keeps the order.
        return "T" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()[^5..];
    }

    /// <summary>An order line: "2 x Hulk", "2 Hulk", "Hulk x 2", or a bare item name meaning one.</summary>
    private static readonly Regex LeadingQty = new(@"^\s*(\d[\d,\s]*)\s*(?:x|\*|×)?\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex TrailingQty = new(@"^\s*(.+?)\s*(?:x|\*|×)\s*(\d[\d,\s]*)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Reads the body into order lines, matched against the catalogue.
    ///
    /// <para>Anything that does not match is returned rather than dropped: a buyer who asked for
    /// four things and gets three needs to be told which one was not understood, and the seller
    /// needs to see it to decide whether the catalogue is missing something.</para>
    /// </summary>
    internal static (List<(PostingItemView Item, long Units)> Lines, List<string> Unknown)
        ParseOrderLines(string? body, IReadOnlyDictionary<string, PostingItemView> catalogue)
    {
        var lines   = new List<(PostingItemView, long)>();
        var unknown = new List<string>();
        if (string.IsNullOrWhiteSpace(body)) return (lines, unknown);

        foreach (var raw in Strip(body).Split('\n'))
        {
            var line = raw.Trim().TrimEnd('.', ';', ',');
            if (line.Length == 0) continue;

            // A quoted reply carries our own mail back. Skipping these stops the confirmation we
            // just sent from being read as a second order for the same things.
            if (line.StartsWith('>')) continue;

            string name;
            long   units = 1;

            if (LeadingQty.Match(line) is { Success: true } a)
            {
                units = Qty(a.Groups[1].Value);
                name  = a.Groups[2].Value;
            }
            else if (TrailingQty.Match(line) is { Success: true } b)
            {
                name  = b.Groups[1].Value;
                units = Qty(b.Groups[2].Value);
            }
            else name = line;

            name = name.Trim().Trim('-', ':').Trim();
            if (name.Length == 0 || units <= 0) continue;

            if (catalogue.TryGetValue(name, out var item)) lines.Add((item, units));
            else if (!IsChatter(name)) unknown.Add(name);
        }

        // The same item twice on one order is one line for that many.
        return (lines.GroupBy(l => l.Item1.TypeId)
                     .Select(g => (g.First().Item1, g.Sum(x => x.Item2)))
                     .ToList(),
                unknown.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static long Qty(string s) =>
        long.TryParse(s.Replace(",", "").Replace(" ", ""), out var n) ? n : 0;

    /// <summary>
    /// Greetings, sign-offs and thanks — the human parts of a mail, which are not failed order
    /// lines and should not be reported back as things we could not find.
    /// </summary>
    private static bool IsChatter(string line)
    {
        var l = line.Trim().TrimEnd('!', '.', '?');
        if (l.Length <= 2) return true;
        return l.StartsWith("thank",  StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("hi",     StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("hello",  StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("please", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("cheers", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("o7",     StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("regards", StringComparison.OrdinalIgnoreCase);
    }

    // ── Text ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A mail body as plain text. EVE bodies are HTML — line breaks are &lt;br&gt; and an item
    /// the buyer dragged in arrives as a showinfo anchor, so a naive split on newlines finds one
    /// enormous line and no items at all.
    /// </summary>
    internal static string Strip(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</p\s*>", "\n", RegexOptions.IgnoreCase);
        // The anchor's TEXT is the item name, which is exactly what we want to match on.
        s = Regex.Replace(s, @"<a\b[^>]*>(.*?)</a>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        return s.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);

    private static string Link(PostingItemView item) =>
        $"<a href=\"showinfo:{item.TypeId}\">{Esc(item.NameOverride is { Length: > 0 } n ? n : item.TypeName)}</a>";

    private static string Isk(double v) => v.ToString("N2") + " ISK";

    /// <summary>EVE rejects an over-long subject outright, which would lose the whole reply.</summary>
    private static string Trim(string subject) =>
        subject.Length <= 100 ? subject : subject[..100];
}
