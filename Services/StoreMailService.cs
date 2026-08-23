using System.Text;
using System.Text.RegularExpressions;
using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.ViewModels;
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
    MailBudget                      budget,
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

    /// <summary>
    /// What answering one mail costs against the rate limit: fetch its body, send the reply, mark
    /// it read. All three are char-social, the same 600-per-15-minutes as everything else the
    /// character's mail does.
    /// </summary>
    private const int CallsPerMail = 3;

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

        // ⚠️ !IsDeleted as well as Enabled. A deleted store is closed on the way out, but relying
        // on that alone would mean one row edited by hand — or a future path that forgets — could
        // leave a shop nobody can see quietly answering mail.
        var stores = await db.Stores
            .Where(s => !s.IsDeleted && s.Enabled && s.CharacterId != 0 && s.PostingId != 0)
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
    /// <summary>
    /// What an order line's state is, as one string, for comparison against what was last sent.
    ///
    /// <para>⚠️ The contract id is part of it. A contract being cut is the most interesting thing
    /// that happens to an order between ordering and receiving it — "go and accept it" is
    /// actionable in a way that "still on its way" is not — and without this the row would look
    /// unchanged and the buyer would never be told.</para>
    /// </summary>
    private static string StateOf(TrackedOrder o) =>
        $"{o.Status}|{o.FulfilmentSource}|{o.EstimatedDate}|{o.LinkedContractId}";

    /// <summary>Still open, with nothing behind it — no stock, no job, no contract.</summary>
    private static bool Uninformative(TrackedOrder o) =>
        o.Status == "pending" && o.FulfilmentSource.Length == 0 && o.LinkedContractId is null;

    /// <summary>Whether the last thing the buyer was told named a source or a contract.</summary>
    private static bool KnewSomething(string notified)
    {
        var parts = notified.Split('|');
        return parts.Length >= 4 && (parts[1].Length > 0 || parts[3].Length > 0);
    }

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

        var changed = orders
            .Where(o => StateOf(o) != o.NotifiedState)
            // ⚠️ Never mail bad news that is only the ABSENCE of news. An order that had a
            // source and now has none has not necessarily gone backwards — far more often the
            // pass that decided it landed while the assets table was mid-refresh and saw an
            // empty hangar. Telling a buyer their in-stock order is "waiting on materials", and
            // then telling them it is ready again a minute later, is two mails about nothing.
            //
            // The marker is deliberately NOT advanced for these, so the buyer is still owed
            // whatever the last real change was. If the order genuinely is stuck with nothing
            // behind it, the next mail they get is the one that says something useful.
            .Where(o => !(Uninformative(o) && KnewSomething(o.NotifiedState)))
            .ToList();
        if (changed.Count == 0) return 0;

        var names = await TypeNamesAsync(db, changed.Select(o => o.TypeId).Distinct().ToList(), ct);

        var sent = 0;
        foreach (var group in changed.GroupBy(o => (o.OrderRef, o.BuyerId, o.Buyer))
                                     .Take(MaxRepliesPerPass))
        {
            // Updates yield to the same ceiling. They are worth less than an answer to a direct
            // question — nobody is waiting on one — so they stop first and resume when the
            // allowance recovers, with the marker unadvanced so nothing is lost.
            if (!budget.StoreMayUse(store.CharacterId, 1)) break;

            var lines = group.ToList();

            var sb = new StringBuilder();
            sb.Append("Order <b>").Append(group.Key.OrderRef).Append("</b> — update<br><br>");
            foreach (var o in lines)
                sb.Append(o.Units.ToString("N0")).Append(" × ")
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
                Wrap(store, sb.ToString()),
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

        // ⚠️ Every mail costs three calls against char-social — read the body, send the reply,
        // mark it read — and reading the inbox costs more on top. A mailbox anyone can write to
        // is a mailbox anyone can flood, and without this a hundred junk mails would spend the
        // character's whole allowance in minutes. What breaks then is not the shop but the
        // character's mail itself, and anything else on that group.
        if (todo.Count > 0 && !budget.StoreMayUse(store.CharacterId, todo.Count * CallsPerMail))
        {
            StatusText = $"Holding off — {budget.Describe(store.CharacterId)}.";
            return 0;
        }

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
            // ⚠️ RAW. The order parser reads links out of the markup, and the stripped version
            // has already thrown every one of them away.
            try { body = await mail.GetRawBodyAsync(store.CharacterId, header.MailId, ct); }
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
        Head($"{store.Name} — how to order") +

        "Put one of these words in the mail <b>subject</b>." + Gap +

        Cmd("PRICES") + "the current price list." + Br + Gap +

        Cmd("ORDER") + "place an order, one item per line in the body." + Br +
        // ⚠️ Dragging is offered first because it is the thing that cannot go wrong: the link
        // carries the item's id, so there is nothing to spell and nothing to match.
        "The easiest way is to <b>drag the item in</b> — from this price list, from the market, " +
        "from your hangar — then put the quantity beside it if you want more than one." + Br +
        // ⚠️ Real links, not "[Archon]". The bracketed form was meant to picture a dragged item
        // and instead looked like syntax — someone would reasonably have typed the brackets.
        // These render exactly as a dragged one does, because they are the same thing.
        Ind + Item(23757, "Archon") + " x2" + Br +
        Ind + Item(24483, "Nidhoggur") + Br +
        Dim("Typing the name works too. The quantity always goes AFTER the item:") + Br +
        Eg("Archon x2     Archon 2     Archon") + Gap +

        Cmd("STATUS") + "where your orders have got to." + Br +
        Dim("No reference needed — you will get all of your open ones.") + Br +
        Dim("To ask about one order, put its reference in the subject or the body:") + Br +
        Eg("STATUS 3FVPA9") + Gap +

        Cmd("CANCEL") + "withdraw an order." + Br +
        // ⚠️ Says WHERE the reference goes. "It needs its reference" left the reader to guess
        // between the subject and the body, and a cancel that silently matches nothing is the
        // worst kind of guess to get wrong.
        Dim("This one always needs its reference, from the confirmation mail. " +
            "Subject or body, either works:") + Br +
        Eg("CANCEL 3FVPA9") + Gap +

        Cmd("HELP") + "this message." + Gap +

        Rule + Gap +

        "<b>Sending it to someone else?</b> Drag that character or corporation anywhere into the " +
        "body and the contract will be made out to them instead of you." + Gap +

        Dim($"Prices are those on the list at the moment your order is read. — {store.Name}");

    // ── Mail styling ──────────────────────────────────────────────────────────
    //
    // ⚠️ What EVE actually renders, established by testing rather than assumed: <b>, <i>, <u>,
    // <br>, <font size> and <font color>, plus showinfo links. NOT <ul>/<li>, NOT <table>, NOT
    // <strong>, and NOT HTML entities of any kind — those are drawn rather than decoded. EVE's
    // own composer offers bold, underline, colour, size and url, which is the same list from the
    // other direction.
    //
    // Whitespace is NOT collapsed, so plain spaces indent.

    private const string Br  = "<br>";
    private const string Gap = "<br><br>";

    /// <summary>Muted grey, for the asides — the sentence under a command rather than the
    /// command.</summary>
    private static string Dim(string s) => $"<font color=\"#ff8a8a99\">{s}</font>";

    /// <summary>Bad news in a message that is otherwise good news, so it cannot be skimmed past.</summary>
    private static string Warn(string s) => $"<font color=\"#ffc85a5a\"><b>{s}</b></font>";

    /// <summary>The shop's own line at the top, a size up so the mail opens with its name.</summary>
    private static string Head(string s) =>
        $"<font size=\"16\" color=\"#ffc8a84b\"><b>{s}</b></font>{Gap}";

    /// <summary>A command word: the thing a reader is scanning for, so it gets the weight.</summary>
    private static string Cmd(string s) =>
        $"<font color=\"#ffc8a84b\"><b>{s}</b></font> — ";

    /// <summary>An example line, indented and in a colour that says "this is literal".</summary>
    private static string Eg(string s) => $"    <font color=\"#ff4ac8a8\">{s}</font>";

    private static string Rule => Dim("————————————————————");

    /// <summary>An item link, exactly as EVE writes one when an item is dragged in.</summary>
    private static string Item(int typeId, string name) =>
        $"<a href=\"showinfo:{typeId}\">{name}</a>";
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
                    ? Warn("Not found: " + Esc(string.Join(", ", parsed.Unknown))) + Gap
                    : "") +
                Usage(store), ct);
            return;
        }

        var reference = await NewReferenceAsync(db, ct);
        var now       = DateTimeOffset.UtcNow;

        // Who the contract goes to. A dragged link carries the id, so there is no name to
        // resolve and nothing to guess between a character and a corporation.
        //
        // ⚠️ Defaults to whoever sent the mail, so every store order names a recipient. "Nobody
        // named means the buyer" was a rule held in this code and nowhere else — the Order
        // Tracker showed a blank, and whoever came to fill the order had to know the convention
        // to read it. Writing the sender in says the same thing where it can be seen.
        var toId   = parsed.ContractTo?.EntityId  ?? log.PartyId;
        var toName = parsed.ContractTo?.Text      ?? log.PartyName;
        var toKind = parsed.ContractTo?.EntityKind ?? "character";

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
                //
                // Rounded to the precision it is quoted at, so the record says the same number
                // the buyer was shown. Storing 3,705,101,922.94 against a quote of "3.71B" is
                // two figures for one agreement, and only one of them was ever visible to them.
                //
                // ⚠️ The UNIT is rounded and then multiplied, not the other way round. Rounding
                // the total independently gave a line that failed its own arithmetic: two
                // Phoenix at a quoted 3,720,000,000 each came to 7,450,000,000, because the
                // total was rounded from the raw figure rather than from the price shown. The
                // buyer can multiply.
                PurchasePrice = MarketFmt.RoundToDisplay(item.SalePrice ?? 0) * units,
                Status        = "pending",
                StoreId       = store.Id,
                OrderRef      = reference,
                NotifiedState = "pending||",
                ContractToId   = toId,
                ContractToName = toName,
                ContractToType = toKind,
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

        var settled = await db.TrackedOrders
            .Where(o => o.OrderRef == reference)
            .ToListAsync(ct);

        // ⚠️ Marked as told with what the confirmation is ABOUT to say, not with what was true a
        // moment ago. NotifiedState was being stamped at creation, before the fulfilment pass
        // ran — so an order filled from stock had its confirmation say "in stock and reserved",
        // and then the update sweep found the row disagreeing with the marker and sent a second
        // mail a minute later saying "ready now". Two mails, one event, and the second one added
        // nothing.
        // ⚠️ An expected date for anything coming off the shelf, before the state is stamped.
        // Nothing else would ever give one: only a job-sourced order gets a date, from its job,
        // so a stock order would sit blank until a contract appeared. Blank reads as "no idea"
        // when the truth is "as soon as somebody writes the contract".
        if (store.AutoEstimateInStock)
        {
            var due = DateTimeOffset.UtcNow.AddDays(Math.Max(0, store.AutoEstimateDays))
                                    .UtcDateTime.ToString("yyyy-MM-dd");

            foreach (var o in settled)
                // Only stock, and only where nothing has set one. A job's date is a real
                // forecast and must not be replaced by a guess.
                if (o.FulfilmentSource == "stock" && string.IsNullOrEmpty(o.EstimatedDate))
                    o.EstimatedDate = due;
        }

        foreach (var o in settled) o.NotifiedState = StateOf(o);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var byType = settled.ToDictionary(o => o.TypeId);

        // Summed from the ROUNDED line prices, so the total is what the lines add up to. Summing
        // the raw figures and rounding once at the end gives a total that does not match its own
        // arithmetic, which is the sort of thing a buyer checks.
        var total = created.Sum(o => o.PurchasePrice);

        var sb = new StringBuilder();
        sb.Append(Head($"Order {reference} received"));

        foreach (var (item, units) in parsed.Lines)
        {
            var line = byType.GetValueOrDefault(item.TypeId);

            // ⚠️ Total first, unit price after in brackets. The totals are what a reader scans
            // down and adds up, so they belong in one column; putting the unit price between the
            // name and the total pushed every total to a different place on the line.
            sb.Append("<b>").Append(units.ToString("N0")).Append(" × ").Append("</b>")
              .Append(Link(item))
              .Append(" — <b>").Append(Isk(line?.PurchasePrice ?? 0)).Append("</b>");

            // Only when there is more than one — on a single item it would print the same
            // number twice with an "each" between them.
            if (units > 1)
                sb.Append(Dim($" ({Isk(MarketFmt.RoundToDisplay(item.SalePrice ?? 0))} each)"));

            sb.Append(Br).Append(Ind).Append(Dim(Expect(line))).Append(Br);
        }

        sb.Append(Br).Append("<font size=\"14\"><b>Total ").Append(Isk(total)).Append("</b></font>").Append(Br);

        // Always stated, even when it is simply the sender. It is the one detail a buyer cannot
        // check afterwards, and someone who meant to name a third party and whose link did not
        // parse finds out here rather than when the contract lands on the wrong name.
        if (toName.Length > 0)
            sb.Append("Contract will be made out to <b>").Append(Esc(toName)).Append("</b>.").Append(Br);

        sb.Append("<br>");

        // ⚠️ In red, because it is the one line in a confirmation that is bad news. Everything
        // around it says what WAS ordered, and a rejection set in the same grey as the rest is
        // read as part of the receipt — the buyer discovers what is missing when it does not
        // arrive.
        if (parsed.Unknown.Count > 0)
            sb.Append(Warn("Not on the list, so NOT ordered: "
                         + Esc(string.Join(", ", parsed.Unknown))))
              .Append(Gap);

        sb.Append(Dim("Reply with STATUS for progress, or CANCEL and this reference to withdraw it."));

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

        var titled  = orders.Count > 0 && log.OrderRef.Length > 0
            ? $"Order {log.OrderRef}"
            : "Your open orders";

        var several = orders.Select(o => o.OrderRef).Distinct().Count() > 1;

        // One chunk per order, built whole so a split never lands inside one. Grouped by order
        // because that is the unit the buyer placed and the reference they quote back to cancel.
        var blocks = new List<string>();

        foreach (var group in orders.GroupBy(o => o.OrderRef).OrderBy(g => g.Key))
        {
            var lines = group.ToList();
            var block = new StringBuilder();

            if (several) block.Append("<b>").Append(group.Key).Append("</b>").Append(Br);

            foreach (var o in lines)
            {
                var unit = o.Units > 0 ? MarketFmt.RoundToDisplay(o.PurchasePrice / o.Units) : 0;

                // Same shape as the confirmation: total first, unit price after in brackets, so
                // the two mails read alike and the totals line up down the page.
                block.Append("<b>").Append(o.Units.ToString("N0")).Append(" × </b>")
                     .Append("<a href=\"showinfo:").Append(o.TypeId).Append("\">")
                     .Append(Esc(names.GetValueOrDefault(o.TypeId, $"Type {o.TypeId}"))).Append("</a>")
                     .Append(" — <b>").Append(Isk(o.PurchasePrice)).Append("</b>");

                if (o.Units > 1) block.Append(Dim($" ({Isk(unit)} each)"));

                block.Append(Br).Append(Ind).Append(Dim(Describe(o))).Append(Br);
            }

            // Per order, not per line: every line of one order shares its buyer and its
            // destination, and repeating them under each item would be noise.
            var contractTo = lines.Select(o => o.ContractToName)
                                  .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            if (contractTo is not null)
                block.Append(Ind).Append(Dim($"Contract to: {Esc(contractTo)}")).Append(Br);

            if (lines.Count > 1)
                block.Append(Ind).Append(Dim($"Order total: {Isk(lines.Sum(o => o.PurchasePrice))}"))
                     .Append(Br);

            block.Append(Br);
            blocks.Add(block.ToString());
        }

        await ReplyInPartsAsync(store, log, $"{store.Name} — order status", titled, blocks, ct);
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
    private static string Expect(TrackedOrder? o)
    {
        if (o?.LinkedContractId is not null)
            return "A contract is already waiting for you to accept.";

        // ⚠️ The date, wherever there is one. A stock order gets one from the store's own
        // setting and a job order from its job, and a confirmation that says "reserved for you"
        // without saying when answers half the question the buyer asked.
        var due = o?.EstimatedDate is { Length: > 0 } d ? d : null;

        return o?.FulfilmentSource switch
        {
            "stock" => due is null
                        ? "In stock and reserved for you — you will be notified when the "
                        + "contract has been created."
                        : $"In stock and reserved for you — contract expected {due}.",

            "job"   => due is null
                        ? "Already in build."
                        : $"Already in build, expected {due}.",

            _       => due is null
                        ? "Out of stock — your reservation is placed, and you will be told the "
                        + "completion date once the build starts."
                        : $"Out of stock — your reservation is placed, expected {due}.",
        };
    }

    /// <summary>What one line of an order is doing, in words a buyer can act on.</summary>
    private static string Describe(TrackedOrder o) => o.Status switch
    {
        "completed" => "delivered",

        // ⚠️ Says which kind of cancelled. An order withdrawn by the buyer and one ended because
        // they declined the contract look identical in the row, and only one of them is news.
        "canceled"  => o.LinkedContractId is not null
                        ? "the contract was declined, so this order is closed"
                        : "cancelled",

        // A contract on the table outranks everything else that could be said. It is the only
        // state where the next move is theirs.
        _ when o.LinkedContractId is not null
                    => "contract created — waiting for you to accept it",

        _ => o.FulfilmentSource switch
        {
            "stock" => o.EstimatedDate is { Length: > 0 } sd
                        ? $"ready now — contract expected {sd}"
                        : "ready now — you will be notified when the contract has been created",
            "job"   => o.EstimatedDate is { Length: > 0 } d
                        ? $"in production, expected {d}"
                        : "in production",
            _       => o.EstimatedDate is { Length: > 0 } e
                        ? $"expected {e}"
                        : "waiting on materials",
        },
    };

    // ── CANCEL ────────────────────────────────────────────────────────────────

    private async Task CancelAsync(AppDbContext db, Store store, StoreMail log, CancellationToken ct)
    {
        var orders = await FindOrderAsync(db, store, log, ct);
        if (orders.Count == 0)
        {
            // ⚠️ Says which reference it looked for. "No order of that reference" without naming
            // it leaves the reader unable to tell a typo from a missing order from a bug — and
            // when the bug was ours, it left them nothing to report either.
            var tried = What(log.Subject, log.Body);

            log.Outcome = "rejected";
            log.Detail  = tried.Length > 0
                ? $"No order matching {tried} for this sender."
                : "No order reference found in the mail.";

            await ReplyAsync(store, log, $"{store.Name} — order not found",
                (tried.Length > 0
                    ? $"No open order of yours matches <b>{Esc(tried)}</b>."
                    : "I could not find an order reference in that mail.") +
                Br + Dim("References look like K7P2QX and are in the confirmation mail. " +
                         "Reply STATUS on its own to list your open orders.") + Gap +
                Usage(store), ct);
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

            // ⚠️ Marked as told, because this reply IS the telling. Without it the update sweep
            // finds the status changed a minute later and sends a second mail about the same
            // cancellation — one saying it happened, one saying what was in it, and the buyer
            // left to work out that they are the same event.
            o.NotifiedState = StateOf(o);
        }

        if (open.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        var names = await TypeNamesAsync(db, orders.Select(o => o.TypeId).Distinct().ToList(), ct);

        var sb = new StringBuilder();
        sb.Append(Head($"Order {orders[0].OrderRef} cancelled"));

        if (open.Count > 0)
        {
            // Says WHAT was cancelled, not just how many lines. A count is a receipt for the
            // app's benefit; the buyer wants to see the thing they will not be getting.
            foreach (var o in open)
                sb.Append("<b>").Append(o.Units.ToString("N0")).Append(" × </b>")
                  .Append("<a href=\"showinfo:").Append(o.TypeId).Append("\">")
                  .Append(Esc(names.GetValueOrDefault(o.TypeId, $"Type {o.TypeId}"))).Append("</a>")
                  .Append(Dim($" — {Isk(o.PurchasePrice)}")).Append(Br);

            sb.Append(Br).Append(Dim("Nothing further is owed on these."));
        }
        else
        {
            sb.Append("Nothing on this order was still open to cancel.");
        }

        if (kept > 0)
            sb.Append(Br).Append(Dim(
                $"{kept} line(s) were already delivered or cancelled and have been left as they are."));

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
        var candidates = References(log.Subject).Concat(References(log.Body))
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0) return [];

        // ⚠️ EVERY candidate is offered to the database, and the one that exists wins. Taking
        // only the first match found in the text is what broke CANCEL: "CANCEL" is six letters
        // and every one of them — C, A, N, E, L — is in the reference alphabet, so the command
        // word itself looked like a reference and the real one was never reached.
        //
        // Filtering out the known commands would fix that one case and leave the next: any
        // six-letter word drawn from this alphabet reads as a reference. Asking which of them is
        // real settles all of them at once, and costs one query either way.
        var hits = await db.TrackedOrders
            .Where(o => o.StoreId == store.Id
                     && o.BuyerId == log.PartyId
                     && candidates.Contains(o.OrderRef))
            .ToListAsync(ct);

        // If the text somehow named two of the buyer's own orders, answer about the first.
        return hits.GroupBy(o => o.OrderRef).OrderBy(g => g.Key).FirstOrDefault()?.ToList() ?? [];
    }

    /// <summary>What the buyer wrote that could have been a reference, in the order it appeared.</summary>
    internal static string What(string? subject, string? body)
    {
        var all = References(subject).Concat(References(body))
                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return all.Count == 0 ? "" : string.Join(", ", all);
    }

    /// <summary>
    /// EVE's ceiling on a mail body, from the ESI schema. Left some room under it: the heading,
    /// the part marker and the closing line are added after the blocks are measured.
    /// </summary>
    private const int MaxBodyChars = 9_000;

    /// <summary>How many parts one answer may run to.</summary>
    private const int MaxParts = 5;

    /// <summary>
    /// Sends a reply as however many mails it takes.
    ///
    /// <para><b>⚠️ Split, not truncated.</b> A buyer with enough open orders would push a status
    /// reply past EVE's 10,000-character body limit, and the whole mail would be refused — so the
    /// answer to "where are my orders" would be silence. A sale posting can be designed to fit
    /// because its author controls its length; this is a list of whatever somebody has ordered,
    /// and nobody controls that.</para>
    ///
    /// <para>Blocks are whole orders, so a split never lands inside one. If a single order is
    /// itself over the limit it goes in a part of its own and is allowed to be too long — better
    /// a mail EVE may refuse than one that silently loses half an order.</para>
    ///
    /// <para>Bounded, because every part is a send against the same rate limit as everything
    /// else. What does not fit says so rather than vanishing.</para>
    /// </summary>
    private async Task ReplyInPartsAsync(
        Store store, StoreMail log, string subject, string heading,
        IReadOnlyList<string> blocks, CancellationToken ct)
    {
        var parts = new List<string>();
        var sb    = new StringBuilder();

        // ⚠️ Room reserved for what gets added AFTER the chunking: the store's own header and
        // footer, which ReplyAsync wraps around every message, plus this part's heading. Measure
        // the blocks alone and a mail that just fits becomes one that just does not, and EVE
        // refuses the whole thing.
        var room = MaxBodyChars - Wrap(store, "").Length - Head(heading).Length - 200;
        if (room < 1_000) room = 1_000;

        foreach (var block in blocks)
        {
            if (sb.Length > 0 && sb.Length + block.Length > room)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            sb.Append(block);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        if (parts.Count == 0) parts.Add("");

        var dropped = 0;
        if (parts.Count > MaxParts)
        {
            dropped = parts.Count - MaxParts;
            parts   = parts.Take(MaxParts).ToList();
        }

        for (var i = 0; i < parts.Count; i++)
        {
            var last  = i == parts.Count - 1;
            var title = parts.Count > 1 ? $"{heading} ({i + 1} of {parts.Count})" : heading;
            var tail  = last && dropped > 0
                ? Dim($"{dropped} further page(s) of orders were not included — reply STATUS "
                    + "with an order reference to ask about one of them.")
                : "";

            await ReplyAsync(store, log,
                parts.Count > 1 ? $"{subject} ({i + 1}/{parts.Count})" : subject,
                Head(title) + parts[i] + tail, ct);
        }
    }

    private async Task ReplyAsync(Store store, StoreMail log, string subject, string body, CancellationToken ct)
    {
        var (ok, error) = await mail.SendMailAsync(
            store.CharacterId, Trim(subject), Wrap(store, body),
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

    /// <summary>
    /// Everything in the text shaped like an order reference.
    ///
    /// <para>⚠️ All of them, not the first. The alphabet excludes the characters that misread
    /// when typed back — no O against 0, no I or 1, no S against 5 — but plenty of ordinary
    /// words survive it, "CANCEL" among them. Which of these is a real order is a question for
    /// the database, not for a regex.</para>
    /// </summary>
    internal static IEnumerable<string> References(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : RefPattern.Matches(Strip(text)).Select(m => m.Groups[1].Value);

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

        /// <summary>
        /// Which kind of entity, from the type it is an instance of — or "" for a kind that
        /// cannot be contracted to.
        ///
        /// <para>⚠️ A whitelist, not a fallback. This used to read every unrecognised instance
        /// link as a character, which is fine until somebody drags a STRUCTURE into an order —
        /// and people do, to say where they want it delivered. That would have made the contract
        /// out to a "character" whose id is a Keepstar.</para>
        ///
        /// <para>Characters carry their bloodline's type rather than one shared id, which is why
        /// this is a range and not a single number.</para>
        /// </summary>
        public string EntityKind => TypeId switch
        {
            2                              => "corporation",
            16159                          => "alliance",
            >= 1373 and <= 1386 or 34574   => "character",
            _                              => "",
        };

        /// <summary>Something a contract could actually be made out to.</summary>
        public bool IsContractable => !IsItem && EntityKind.Length > 0;
    }

    private static readonly Regex ShowInfo = new(
        """<a\s+href\s*=\s*"?showinfo:(\d+)(?://(\d+))?"?[^>]*>(.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Quantity forms — the count always after the item, never before it.</summary>
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
    /// <para>Quantities follow the item — <c>Archon x2</c>, <c>Archon 2</c>, <c>x2</c> beside a
    /// dragged link, or nothing at all meaning one. Never before it: see ByName. The forms are
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

            // A structure, a station or a ship dragged in is not a mistake — people paste them to
            // say where they want things — it simply is not somebody a contract can be made out
            // to, so it is passed over rather than misread.
            contractTo ??= links.FirstOrDefault(l => l.IsContractable);

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

        // ⚠️ The count always FOLLOWS the item. "2 x Archon" used to be accepted too, and the
        // trouble is what happens when somebody puts two items on one line: in "Archon 2 Chimera"
        // the 2 could belong to either, and no rule can tell which was meant. Accepting only one
        // side removes the question — the number attaches to the thing before it, always — and a
        // line written the other way round is reported as unrecognised rather than guessed at.
        foreach (var (pattern, nameGroup, qtyGroup) in new[]
                 {
                     (QtyTrailing, 1, 2),   // Archon x2, Archon x 2
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
    /// <summary>
    /// A name made safe to drop into a mail body.
    ///
    /// <para>⚠️ Angle brackets removed rather than entity-encoded, and nothing else touched.
    /// EVE's mail renderer does not decode HTML entities — it draws them — so HtmlEncode turned
    /// an ampersand in a corporation name into a literal "&amp;amp;" in front of the buyer, and
    /// the same mistake made the usage text's indentation appear as "&amp;nbsp;". The only
    /// characters that must not survive are the two that would be read as markup, and EVE type
    /// names contain neither.</para>
    /// </summary>
    private static string Esc(string s) => s.Replace("<", "").Replace(">", "");

    /// <summary>
    /// An indent.
    ///
    /// <para>⚠️ Literal spaces, and no entities. EVE's mail renderer does not decode
    /// entities — it draws them — so <c>&amp;nbsp;</c> and <c>&amp;#160;</c> both appeared
    /// verbatim in front of buyers.</para>
    ///
    /// <para>Plain spaces are enough: EVE does NOT collapse whitespace the way a browser
    /// does, measured by putting indented lines in a posting and reading the result.</para>
    /// </summary>
    private const string Ind = "   ";

    private static string Link(PostingItemView item) =>
        $"<a href=\"showinfo:{item.TypeId}\">{Esc(item.NameOverride is { Length: > 0 } n ? n : item.TypeName)}</a>";

    /// <summary>⚠️ Whole ISK, because every figure reaching a mail has been through
    /// <see cref="MarketFmt.RoundToDisplay"/> — at billions the decimals are always ".00", and
    /// printing them suggests a precision the price does not have.</summary>
    private static string Isk(double v) => v.ToString("N0") + " ISK";

    /// <summary>
    /// The store's own header and footer around a message body.
    ///
    /// <para>⚠️ Applied at the point of sending, so EVERY mail carries it — the answers, the
    /// rejections and the unprompted order updates alike. Adding it where each message is built
    /// would mean remembering it in six places, and the one forgotten would be the one somebody
    /// noticed.</para>
    ///
    /// <para>The text goes in exactly as written. It is the shop owner's, not a buyer's, so
    /// their markup is theirs to use.</para>
    /// </summary>
    private static string Wrap(Store store, string body)
    {
        // ⚠️ Through the EVE Mail output format, the same one a posting's static blocks use.
        // That is what turns [color=#rrggbb]…[/color] into a font tag and a typed newline into
        // <br>. Without it the markup that works in a posting arrived here as literal text, and
        // a multi-line footer came out as one long line — the same box, the same syntax, two
        // different behaviours depending on which screen it was typed into.
        var fmt  = OutputFormat.ByName("EVE Mail");
        var head = Mark(fmt, store.MessageHeader);
        var foot = Mark(fmt, store.MessageFooter);

        var sb = new StringBuilder();
        if (head.Length > 0) sb.Append(Tint(store.MessageHeaderColor, head)).Append(Gap);
        sb.Append(body);
        if (foot.Length > 0) sb.Append(Gap).Append(Tint(store.MessageFooterColor, foot));
        return sb.ToString();
    }

    /// <summary>
    /// Text in a colour, given the six-digit hex the pickers produce.
    ///
    /// <para>⚠️ EVE's colours are ARGB and the alpha is not optional — a six-digit value is read
    /// with a zero alpha, which is invisible text. The same widening the posting renderer does.</para>
    /// </summary>
    /// <summary>Free text as EVE mail: colour markup resolved, newlines turned into breaks.</summary>
    private static string Mark(OutputFormat fmt, string? text)
    {
        var s = (text ?? "").Trim();
        return s.Length == 0 ? "" : fmt.Finalize(s);
    }

    private static string Tint(string? hex, string text)
    {
        var rgb = (hex ?? "").Trim().TrimStart('#');
        if (rgb.Length == 6) rgb = "ff" + rgb;
        return rgb.Length == 8 ? $"<font color=\"#{rgb}\">{text}</font>" : text;
    }

    /// <summary>EVE rejects an over-long subject outright, which would lose the whole reply.</summary>
    private static string Trim(string subject) =>
        subject.Length <= 100 ? subject : subject[..100];
}
