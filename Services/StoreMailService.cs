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
    OrderFulfilmentService          fulfilment,
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

    /// <summary>
    /// How many times a failed read-only command is tried again before it is left alone.
    ///
    /// <para>⚠️ Bounded, because the failure may be permanent — a body ESI will always refuse is
    /// not going to be accepted on the fifth attempt, and retrying it every minute forever would
    /// fill the log with the same rejection. Three attempts covers a transient outage and stops
    /// well short of that.</para>
    /// </summary>
    private const int MaxAttempts = 3;

    /// <summary>How long after sending someone the usage before it would be sent again.</summary>
    private static readonly TimeSpan HelpSilence = TimeSpan.FromHours(24);

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

        var prior = (await db.StoreMails
                .Where(m => m.StoreId == store.Id && m.Direction == "in")
                .Select(m => new { m.MailId, m.Outcome, m.Command })
                .ToListAsync(ct))
            .GroupBy(m => m.MailId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var todo = fresh.Where(h => !Handled(h.MailId)).Take(MaxRepliesPerPass).ToList();

        // Whether this mail is finished with, or may be looked at again.
        bool Handled(int mailId)
        {
            if (!prior.TryGetValue(mailId, out var attempts)) return false;

            // Anything that did not end in an error is done: answered, rejected, or left for a
            // person. Only a failure is worth revisiting.
            if (attempts.Any(a => a.Outcome != "error")) return true;

            // ⚠️ Retried only where a second attempt cannot do damage. PRICES and STATUS read and
            // report; running either again costs one more mail. ORDER and CANCEL change orders,
            // and a retry after a reply that failed halfway would place the order twice or cancel
            // something already cancelled — so those stay one attempt, with the failure on the
            // record for a person to answer by hand.
            if (attempts.Any(a => a.Command is not ("PRICES" or "STATUS"))) return true;

            return attempts.Count >= MaxAttempts;
        }
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

            // ⚠️ Marked read only once the row is safely written. The unread flag is the shop
            // owner's own view of its inbox, and marking first would leave a mail that looks
            // dealt with after a crash that lost the record of dealing with it.
            //
            // Failure here is not worth reporting: the mail was answered, and an unread flag that
            // did not clear is a cosmetic difference in a mailbox nobody reads by hand.
            try { await mail.MarkReadAsync(store.CharacterId, header.MailId, ct); }
            catch (OperationCanceledException) { throw; }
            catch { }

            handled++;
        }

        return handled;
    }

    /// <summary>Works out what one mail asked for and does it, filling in the log row.</summary>
    private async Task HandleAsync(AppDbContext db, Store store, StoreMail log, CancellationToken ct)
    {
        var command = ParseCommand(log.Subject);
        log.Command = command;

        // ⚠️ The allow list is checked before anything is said back, including help. A shop that
        // explained itself to anyone who wrote in would be answering strangers, and the whole
        // point of the list is deciding who gets served.
        if (!await IsAllowedAsync(db, store, log.PartyId, ct))
        {
            log.Outcome = "rejected";
            log.Detail  = "Sender is not on this store's list.";
            return;
        }

        if (command.Length == 0)
        {
            // Someone entitled to order wrote in and did not use a keyword. Most of that will be
            // ordinary conversation, so this answers with the usage once and then stays quiet —
            // a shop that replied to every message would be arguing with its own customers.
            if (await ToldRecentlyAsync(db, store, log.PartyId, ct))
            {
                log.Outcome = "unknown";
                log.Detail  = "No keyword in the subject — already sent the usage recently, left for a person.";
                return;
            }

            log.Command = "HELP";
            log.Detail  = "No keyword in the subject — sent the usage.";
            await ReplyAsync(store, log, $"{store.Name} — how to order",
                "I did not recognise that as an order.<br><br>" + Usage(store), ct);
            return;
        }

        switch (command)
        {
            case "PRICES": await PricesAsync(store, log, ct); break;
            case "ORDER":  await OrderAsync(db, store, log, ct); break;
            case "STATUS": await StatusAsync(db, store, log, ct); break;
            case "CANCEL": await CancelAsync(db, store, log, ct); break;
            case "HELP":
                await ReplyAsync(store, log, $"{store.Name} — how to order", Usage(store), ct);
                break;
        }
    }

    /// <summary>
    /// How to use the shop, in the shop's own words.
    ///
    /// <para>One place, so the answer to HELP and the answer to a mail that could not be read are
    /// the same words — a buyer told two different things about the same commands has been given
    /// a reason to doubt both.</para>
    /// </summary>
    private static string Usage(Store store) =>
        "Put one of these in the mail <b>subject</b>:<br><br>" +

        "<b>PRICES</b> — the current price list.<br><br>" +

        "<b>ORDER</b> — place an order. One item per line in the body.<br>" +
        // ⚠️ Dragging is offered first because it is the thing that cannot go wrong. The link
        // carries the item's id, so there is no spelling to get right and nothing to match on;
        // typing is the fallback, not the instruction.
        "The easiest way is to <b>drag the item</b> into the mail — from this price list, from " +
        "the market, from your hangar. Then put the quantity beside it if you want more than " +
        "one:<br>" +
        "&#160;&#160;&#160;[Archon] x2<br>" +
        "&#160;&#160;&#160;[Nidhoggur]<br>" +
        "Typing the name works too, in any of these forms:<br>" +
        "&#160;&#160;&#160;Archon x2&#160;&#160;·&#160;&#160;Archon 2&#160;&#160;·&#160;&#160;" +
        "2 x Archon&#160;&#160;·&#160;&#160;Archon<br><br>" +

        "<b>Contracting to someone else?</b> Drag that character or corporation into the body " +
        "anywhere and the contract will be made out to them instead of you.<br><br>" +

        "<b>STATUS</b> — where your orders have got to. No reference needed; you will get all of " +
        "your open ones. Add a reference to ask about just that one.<br><br>" +

        "<b>CANCEL</b> — withdraw an order. This one does need its reference, from the " +
        "confirmation mail.<br><br>" +

        "<b>HELP</b> — this message.<br><br>" +

        $"Prices are those on the list at the moment the order is read. — {store.Name}";
    /// <summary>
    /// Whether this sender has already been sent the usage lately.
    ///
    /// <para>⚠️ What stops a conversation becoming a loop. Without it, someone replying "thanks"
    /// to a help mail gets another help mail, and answers that one too.</para>
    /// </summary>
    private static async Task<bool> ToldRecentlyAsync(
        AppDbContext db, Store store, long partyId, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - HelpSilence;

        // ⚠️ In memory: EF cannot translate a DateTimeOffset comparison on SQLite, and putting it
        // in the query throws at runtime rather than failing to compile.
        var sent = await db.StoreMails
            .Where(m => m.StoreId == store.Id && m.Direction == "out"
                     && m.PartyId == partyId && m.Command == "HELP")
            .Select(m => m.At)
            .ToListAsync(ct);

        return sent.Any(at => at >= since);
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

        // The catalogue is the posting: what it quotes is what can be bought. Indexed both ways —
        // by type id for a dragged link, which is exact, and by name for anyone who types. A name
        // override is included because that is what the buyer was shown, so that is what they
        // will write back.
        var byName   = new Dictionary<string, PostingItemView>(StringComparer.OrdinalIgnoreCase);
        var byTypeId = new Dictionary<int, PostingItemView>();
        foreach (var item in view.Sections.SelectMany(s => s.Items))
        {
            byTypeId[item.TypeId] = item;
            byName[item.TypeName] = item;
            if (!string.IsNullOrWhiteSpace(item.NameOverride)) byName[item.NameOverride!] = item;
        }

        var parsed = ParseOrder(log.Body, byName, byTypeId);

        if (parsed.Lines.Count == 0)
        {
            log.Outcome = "rejected";
            log.Detail  = parsed.Unknown.Count > 0
                ? $"Nothing recognised. Unmatched: {string.Join(", ", parsed.Unknown)}"
                : "No order lines found in the body.";
            await ReplyAsync(store, log, $"{store.Name} — order not understood",
                "Nothing on this order matched the price list.<br><br>" +
                (parsed.Unknown.Count > 0
                    ? "Not found: " + Esc(string.Join(", ", parsed.Unknown)) + "<br><br>"
                    : "") +
                Usage(store), ct);
            return;
        }

        var reference = await NewReferenceAsync(db, ct);
        var now       = DateTimeOffset.UtcNow;

        // Who the contract goes to, if they dragged somebody in. The link carries the id, so
        // there is no name to resolve and nothing to guess between a character and a corporation.
        var to = parsed.ContractTo;

        var created = new List<TrackedOrder>();
        foreach (var (item, units) in parsed.Lines)
        {
            var order = new TrackedOrder
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
                NotifiedState = "pending||",
                ContractToId   = to?.EntityId ?? 0,
                ContractToName = to?.Text ?? "",
                ContractToType = to is null ? "" : to.EntityKind,
                CreatedAt     = now,
            };
            db.TrackedOrders.Add(order);
            created.Add(order);
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        log.OrderRef = reference;

        // ⚠️ Worked out before the confirmation is written, not after. The buyer's first question
        // is "when", and an answer of "we will let you know" when the item is on the shelf is a
        // worse answer than the truth. This is the same pass that runs every five minutes; asking
        // for it now just means the reply can say what it found.
        try { await fulfilment.RunOnceAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errorLogger.Log(nameof(StoreMailService), "fulfilment after order", ex); }

        var settled = await db.TrackedOrders.AsNoTracking()
            .Where(o => o.OrderRef == reference)
            .ToListAsync(ct);

        var byType = settled.ToDictionary(o => o.TypeId);

        var total = parsed.Lines.Sum(l => (l.Item.SalePrice ?? 0) * l.Units);

        var sb = new StringBuilder();
        sb.Append("Order <b>").Append(reference).Append("</b> received.<br><br>");

        foreach (var (item, units) in parsed.Lines)
        {
            sb.Append(units.ToString("N0")).Append(" &#215; ")
              .Append(Link(item)).Append(" — ")
              .Append(Isk((item.SalePrice ?? 0) * units)).Append("<br>")
              .Append("&#160;&#160;&#160;")
              .Append(Expect(byType.GetValueOrDefault(item.TypeId)))
              .Append("<br>");
        }

        sb.Append("<br><b>Total ").Append(Isk(total)).Append("</b><br>");

        if (to is not null)
            sb.Append("Contract will be made out to <b>").Append(Esc(to.Text)).Append("</b>.<br>");

        sb.Append("<br>");

        if (parsed.Unknown.Count > 0)
            sb.Append("Not on the list, so not ordered: ").Append(Esc(string.Join(", ", parsed.Unknown)))
              .Append("<br><br>");

        sb.Append("Reply with <b>STATUS</b> for progress, or <b>CANCEL</b> and this reference to ")
          .Append("withdraw it.");

        await ReplyAsync(store, log, $"{store.Name} — order {reference}", sb.ToString(), ct);
    }

    // ── STATUS ────────────────────────────────────────────────────────────────

    private async Task StatusAsync(AppDbContext db, Store store, StoreMail log, CancellationToken ct)
    {
        // ⚠️ A reference is accepted but not required. A buyer knows what they ordered, not what
        // this app called it, and asking them to dig a six-character code out of an old mail to
        // ask "where is my stuff" is the app's filing system leaking into the conversation.
        // Without one, they get everything of theirs that is still open.
        var orders = await FindOrderAsync(db, store, log, ct);
        var scope  = orders.Count > 0 ? $"Order <b>{orders[0].OrderRef}</b>" : "Your open orders";

        if (orders.Count > 0) log.OrderRef = orders[0].OrderRef;
        else
        {
            // Everything still open for this sender. Matched on the buyer, so nobody can read
            // anyone else's orders by asking.
            orders = await db.TrackedOrders
                .Where(o => o.StoreId == store.Id && o.BuyerId == log.PartyId
                         && o.OrderRef != "" && o.Status == "pending")
                .ToListAsync(ct);
        }

        if (orders.Count == 0)
        {
            log.Outcome = "ok";
            log.Detail  = "Nothing open for this sender.";
            await ReplyAsync(store, log, $"{store.Name} — no open orders",
                "You have no open orders with us.<br><br>" + Usage(store), ct);
            return;
        }

        var names = await TypeNamesAsync(db, orders.Select(o => o.TypeId).Distinct().ToList(), ct);

        var sb = new StringBuilder();
        sb.Append(scope).Append("<br><br>");

        // Grouped by order, because that is the unit the buyer placed and the reference they
        // would quote back to cancel one of several.
        foreach (var group in orders.GroupBy(o => o.OrderRef).OrderBy(g => g.Key))
        {
            if (orders.Select(o => o.OrderRef).Distinct().Count() > 1)
                sb.Append("<b>").Append(group.Key).Append("</b><br>");

            foreach (var o in group)
                sb.Append(o.Units.ToString("N0")).Append(" &#215; ")
                  .Append("<a href=\"showinfo:").Append(o.TypeId).Append("\">")
                  .Append(Esc(names.GetValueOrDefault(o.TypeId, $"Type {o.TypeId}"))).Append("</a>")
                  .Append(" — ").Append(Describe(o)).Append("<br>");

            sb.Append("<br>");
        }

        await ReplyAsync(store, log, $"{store.Name} — order status", sb.ToString(), ct);
    }

    /// <summary>
    /// What happens next for one line of a new order, in the buyer's terms.
    ///
    /// <para>Three answers, and the difference between them is the whole value of replying at
    /// all: it is on the shelf and reserved for you; it is already being built and here is the
    /// date; nothing exists yet and you are in the queue. "We will get back to you" is what this
    /// exists to avoid.</para>
    ///
    /// <para>⚠️ Reads what OrderFulfilmentService decided rather than deciding again. That pass
    /// owns which order claims which stock — running the same reasoning here would be a second
    /// opinion, and the two would disagree the moment either changed.</para>
    /// </summary>
    private static string Expect(TrackedOrder? o) => o?.FulfilmentSource switch
    {
        "stock"    => "In stock and reserved for you — it will be contracted shortly.",
        "contract" => "Already contracted to you.",
        "job"      => o.EstimatedDate is { Length: > 0 } d
                        ? $"Already in build, expected {d}."
                        : "Already in build.",
        _          => "Out of stock — your reservation is placed, and you will be told the "
                    + "completion date once the build starts.",
    };

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
                "No order of that reference was found against your name.<br><br>" + Usage(store), ct);
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
        }
        else if (log.Outcome.Length == 0) log.Outcome = "ok";

        // ⚠️ Recorded either way. A reply that failed is the one worth keeping: it holds the body
        // that was rejected and the length of it, which is most of what says why. Dropping it left
        // the inbound row saying "error" with nothing to inspect.
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
            Outcome   = ok ? "ok" : "error",
            Detail    = ok ? "" : $"Not sent: {error} ({body.Length:N0} characters)",
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
             : word is "HELP" or "COMMANDS" or "INFO" ? "HELP"
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
    /// <summary>
    /// A link the buyer dragged into the mail.
    ///
    /// <para>EVE writes these as <c>showinfo:&lt;typeId&gt;</c> for a thing, and
    /// <c>showinfo:&lt;typeId&gt;//&lt;entityId&gt;</c> for a particular one of something — a
    /// character, a corporation, an alliance. The presence of the second number is what tells
    /// the two apart.</para>
    /// </summary>
    internal sealed record Dragged(int TypeId, long EntityId, string Text)
    {
        public bool IsItem => EntityId == 0;

        /// <summary>Which kind of entity, from the type it is an instance of. Corporations are
        /// type 2 and alliances 16159; every other instance link is a character, whose type is
        /// whichever bloodline they were born to.</summary>
        public string EntityKind => TypeId switch
        {
            2     => "corporation",
            16159 => "alliance",
            _     => "character",
        };
    }

    private static readonly Regex ShowInfo = new(
        """<a\s+href\s*=\s*"?showinfo:(\d+)(?://(\d+))?"?[^>]*>(.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Quantity forms, most specific first: "x2", "2x", a bare trailing number.</summary>
    private static readonly Regex QtyLeading  = new(@"^\s*(\d[\d,]*)\s*[x*×]?\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex QtyTrailing = new(@"^\s*(.+?)\s*[x*×]\s*(\d[\d,]*)\s*$",   RegexOptions.Compiled);
    private static readonly Regex QtyBare     = new(@"^\s*(.+?)\s+(\d[\d,]*)\s*$",           RegexOptions.Compiled);

    /// <summary>Just a count, with or without an x — what is left beside a dragged item link.</summary>
    private static readonly Regex CountOnly   = new(@"^\s*[x*×]?\s*(\d[\d,]*)\s*[x*×]?\s*$", RegexOptions.Compiled);

    /// <summary>What one mail asked to buy.</summary>
    internal sealed record ParsedOrder(
        List<(PostingItemView Item, long Units)> Lines,
        List<string> Unknown,
        Dragged? ContractTo);

    /// <summary>
    /// Reads the body into order lines.
    ///
    /// <para><b>Links first, names second.</b> Dragging an item out of the price list, the market
    /// or a hangar is one gesture and carries the type id, so there is nothing to spell and
    /// nothing to match — the buyer cannot get it wrong and neither can this. Typed names still
    /// work, because someone will always type.</para>
    ///
    /// <para>⚠️ Parsed from the HTML rather than from stripped text. The anchor IS the item; a
    /// strip that turned it into its own label would throw away the one unambiguous thing in the
    /// message and leave a name to guess at.</para>
    ///
    /// <para>Quantities are read in whichever form arrives — <c>Archon x2</c>, <c>Archon 2</c>,
    /// <c>2 x Archon</c>, <c>x2</c> beside a link, or nothing at all meaning one. The forms are
    /// tried against the catalogue rather than assumed, because an item name can legitimately end
    /// in a number and "Archon 2" would otherwise become an item called "Archon" only by luck.</para>
    ///
    /// <para>An entity link anywhere in the body — a character, a corporation — is read as who
    /// the contract should be made out to. Anywhere, because it is unambiguous wherever it sits:
    /// the link says what it is.</para>
    /// </summary>
    internal static ParsedOrder ParseOrder(
        string? body, IReadOnlyDictionary<string, PostingItemView> byName,
        IReadOnlyDictionary<int, PostingItemView> byTypeId)
    {
        var lines   = new List<(PostingItemView, long)>();
        var unknown = new List<string>();
        Dragged? contractTo = null;

        if (string.IsNullOrWhiteSpace(body)) return new ParsedOrder(lines, unknown, null);

        foreach (var raw in Lines(body))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // A quoted reply carries our own mail back, links and all. Skipping these stops the
            // confirmation we just sent from being read as a second order for the same things.
            if (line.StartsWith('>')) continue;

            var links = ShowInfo.Matches(line)
                .Select(m => new Dragged(
                    int.TryParse(m.Groups[1].Value, out var t) ? t : 0,
                    m.Groups[2].Success && long.TryParse(m.Groups[2].Value, out var e) ? e : 0,
                    Tags.Replace(m.Groups[3].Value, "").Trim()))
                .ToList();

            contractTo ??= links.FirstOrDefault(l => !l.IsItem);

            // The line with its links taken out, which is where any count is.
            var rest = Decode(Tags.Replace(ShowInfo.Replace(line, " "), "")).Trim();

            var items = links.Where(l => l.IsItem).ToList();
            if (items.Count > 0)
            {
                // A count beside the link applies to it. Several links on one line each mean one,
                // since a single number could not say which of them it belonged to.
                var units = items.Count == 1 && CountOnly.Match(rest) is { Success: true } c
                    ? Qty(c.Groups[1].Value)
                    : 1;

                foreach (var link in items)
                {
                    if (byTypeId.TryGetValue(link.TypeId, out var known))
                        lines.Add((known, Math.Max(1, units)));
                    else
                        unknown.Add(link.Text.Length > 0 ? link.Text : $"type {link.TypeId}");
                }
                continue;
            }

            if (rest.Length == 0) continue;

            // No link: read it as text, trying each shape against the catalogue rather than
            // picking one and hoping.
            var parsed = ByName(rest, byName);
            if (parsed is { } hit) lines.Add(hit);
            else if (!IsChatter(rest)) unknown.Add(rest);
        }

        // The same item twice on one order is one line for that many.
        return new ParsedOrder(
            lines.GroupBy(l => l.Item1.TypeId)
                 .Select(g => (g.First().Item1, g.Sum(x => x.Item2)))
                 .ToList(),
            unknown.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            contractTo);
    }

    /// <summary>
    /// A typed line as an item and a count, or null when nothing in the catalogue fits.
    ///
    /// <para>⚠️ Every shape is tested against the catalogue and the first that MATCHES wins —
    /// not the first that parses. "Archon 2" splits happily into "Archon" and 2, but so would an
    /// item genuinely called something ending in a number, and only the catalogue knows which is
    /// which.</para>
    /// </summary>
    private static (PostingItemView, long)? ByName(
        string text, IReadOnlyDictionary<string, PostingItemView> byName)
    {
        // Whole line first: an item whose name ends in a number is still just its name.
        if (byName.TryGetValue(Clean(text), out var whole)) return (whole, 1);

        foreach (var (pattern, nameGroup, qtyGroup) in new[]
                 {
                     (QtyTrailing, 1, 2),   // Archon x2, Archon x 2
                     (QtyLeading,  2, 1),   // 2 x Archon, 2 Archon
                     (QtyBare,     1, 2),   // Archon 2
                 })
        {
            if (pattern.Match(text) is not { Success: true } m) continue;

            var name  = Clean(m.Groups[nameGroup].Value);
            var units = Qty(m.Groups[qtyGroup].Value);
            if (units > 0 && byName.TryGetValue(name, out var item)) return (item, units);
        }

        return null;
    }

    private static string Clean(string s) => s.Trim().Trim('-', ':', '.', ',', ';').Trim();

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
        return l.StartsWith("thank",   StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("hi",      StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("hello",   StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("please",  StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("cheers",  StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("o7",      StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("regards", StringComparison.OrdinalIgnoreCase);
    }

    // ── Text ──────────────────────────────────────────────────────────────────

    private static readonly Regex Tags   = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Breaks = new(@"<br\s*/?>|</p\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The body split into lines, with the markup left in place.</summary>
    private static IEnumerable<string> Lines(string html) =>
        Breaks.Replace(html, "\n").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string Decode(string s) => System.Net.WebUtility.HtmlDecode(s);

    /// <summary>
    /// A mail body as plain text, for the record rather than for parsing. EVE bodies are HTML —
    /// line breaks are &lt;br&gt; and anything dragged in arrives as an anchor — so a naive split
    /// on newlines finds one enormous line.
    /// </summary>
    internal static string Strip(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = Breaks.Replace(html, "\n");
        // The anchor's TEXT is the name a person would read.
        s = Regex.Replace(s, @"<a\b[^>]*>(.*?)</a>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Tags.Replace(s, "");
        return Decode(s).Replace("\r\n", "\n").Replace('\r', '\n');
    }
    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);

    private static string Link(PostingItemView item) =>
        $"<a href=\"showinfo:{item.TypeId}\">{Esc(item.NameOverride is { Length: > 0 } n ? n : item.TypeName)}</a>";

    private static string Isk(double v) => v.ToString("N2") + " ISK";

    /// <summary>EVE rejects an over-long subject outright, which would lose the whole reply.</summary>
    private static string Trim(string subject) =>
        subject.Length <= 100 ? subject : subject[..100];
}
